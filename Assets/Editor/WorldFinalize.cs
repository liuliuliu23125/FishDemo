using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// 收尾阶段：清理主场景中的原始地形残留，并挂载 SECTR 运行时加载器。
///
/// ── 为什么必须清理原始地形 ──
/// SectorizeTerrain 切分完成后只对原始地形做 SetActive(false)，并不清空其
/// TerrainData 引用。实测主场景里残留 9 个失活 Terrain，各带 1025×1025 高度图
/// （合计约 945 万顶点）。这些数据会在加载主场景时全部进入内存，
/// 与 36 个 chunk 的内容重复，SECTR 的流式就完全失去意义。
/// chunk 引用的 TerrainData 位于 World/TerrainSplits/，与原始地形独立，
/// 因此删除原始地形不会破坏已导出的 chunk。
///
/// ── 为什么挂 RegionLoader ──
/// SECTR 的加载判定是体积式的：SECTR_RegionLoader 每帧用 LoadSize 立方体
/// 与所有 Sector 的 TotalBounds 求交（SECTR_Sector.GetContaining），
/// 命中的 sector 通过 SECTR_Chunk.AddReference 触发异步加载，离开后 RemoveReference。
/// 因此主场景里必须有一个跟随玩家的 RegionLoader，否则 chunk 永远不会加载。
/// </summary>
public static class WorldFinalize
{
    private const string WorldScenePath = "Assets/Scenes/World.unity";
    private const string Tag = "[WorldFinalize]";

    /// <summary>单块 chunk 为 512m，加载半径取 1.5 块，约等于常驻 3×3 个 chunk。</summary>
    private const float LoadSizeXY = 768f;

    public static void FinalizeScene()
    {
        var scene = EditorSceneManager.OpenScene(WorldScenePath, OpenSceneMode.Single);
        Debug.Log($"{Tag} 已打开 {WorldScenePath}");

        // ---------- 1. 删除原始地形残留 ----------
        var allTerrains = Resources.FindObjectsOfTypeAll<Terrain>()
            .Where(t => t.gameObject.scene == scene)
            .ToList();

        int removed = 0;
        foreach (var t in allTerrains)
        {
            // 只删失活的：激活的 Terrain 如果是某个 sector 的内容，不能动。
            // 这里切块后所有原始地形都是失活状态，sector 内的地形由 chunk 管理。
            if (!t.gameObject.activeInHierarchy)
            {
                Debug.Log($"{Tag} 删除原始地形残留：{t.name}");
                Object.DestroyImmediate(t.gameObject);
                removed++;
            }
        }
        Debug.Log($"{Tag} 已删除原始地形 {removed} 个");

        // ---------- 2. 挂 RegionLoader ----------
        // 优先复用主相机作为加载中心；没有就新建一个跟随点
        Camera cam = Object.FindObjectOfType<Camera>();
        GameObject loaderHost;
        if (cam != null)
        {
            loaderHost = cam.gameObject;
            Debug.Log($"{Tag} 使用相机作为加载中心：{cam.name}");
        }
        else
        {
            loaderHost = new GameObject("Streaming Center");
            Debug.LogWarning($"{Tag} 场景里没有相机，已新建 Streaming Center 作为加载中心");
        }

        // 先把已有的 RegionLoader 清掉，保证重复执行幂等
        var existing = loaderHost.GetComponents<SECTR_RegionLoader>();
        foreach (var l in existing)
        {
            Object.DestroyImmediate(l);
        }

        var loader = loaderHost.AddComponent<SECTR_RegionLoader>();
        loader.LoadSize = new Vector3(LoadSizeXY, 1000f, LoadSizeXY);
        loader.UnloadBuffer = 0.25f;   // 离开 25% 距离才卸载，避免边界反复横跳
        loader.LayersToLoad = ~0;      // 加载所有层的 sector
        Debug.Log($"{Tag} 已在 {loaderHost.name} 挂载 RegionLoader，LoadSize={loader.LoadSize}，UnloadBuffer={loader.UnloadBuffer}");

        // ---------- 3. 校验：残留地形应为 0，chunk 仍为 36 ----------
        var remainTerrains = Resources.FindObjectsOfTypeAll<Terrain>()
            .Count(t => t.gameObject.scene == scene);
        var chunks = Object.FindObjectsOfType<SECTR_Chunk>();
        var sectors = Object.FindObjectsOfType<SECTR_Sector>();

        // 关键校验：sector 的 TotalBounds 必须有效且覆盖地形范围。
        // RegionLoader 靠 TotalBounds 与 LoadSize 求交来决定加载，
        // 若边界为 0，chunk 永远不会被加载，而且不会报任何错。
        int zeroBounds = 0;
        float minY = float.MaxValue, maxY = float.MinValue;
        foreach (var s in sectors)
        {
            var b = s.TotalBounds;
            if (b.size.x <= 0.01f || b.size.z <= 0.01f) zeroBounds++;
            minY = Mathf.Min(minY, b.min.y);
            maxY = Mathf.Max(maxY, b.max.y);
        }
        Debug.Log($"{Tag} 校验：残留 Terrain {remainTerrains}，SECTR_Chunk {chunks.Length}，Sector {sectors.Length}，" +
                  $"边界为空/零的 Sector {zeroBounds}，Sector 高度范围 Y[{minY:F1}, {maxY:F1}]");

        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        bool ok = remainTerrains == 0 && chunks.Length == 36 && sectors.Length == 36 && zeroBounds == 0;
        Debug.Log($"{Tag} 收尾{(ok ? "成功" : "存在问题")}");
        EditorApplication.Exit(ok ? 0 : 2);
    }
}
