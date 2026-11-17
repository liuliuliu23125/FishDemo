using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class WorldFinalize
{
    private const string WorldScenePath = "Assets/Scenes/World.unity";
    private const string Tag = "[WorldFinalize]";

    private const float LoadSizeXY = 768f;

    public static void FinalizeScene()
    {
        var scene = EditorSceneManager.OpenScene(WorldScenePath, OpenSceneMode.Single);
        Debug.Log($"{Tag} 已打开 {WorldScenePath}");

        var allTerrains = Resources.FindObjectsOfTypeAll<Terrain>()
            .Where(t => t.gameObject.scene == scene)
            .ToList();

        int removed = 0;
        foreach (var t in allTerrains)
        {
            if (!t.gameObject.activeInHierarchy)
            {
                Debug.Log($"{Tag} 删除原始地形残留：{t.name}");
                Object.DestroyImmediate(t.gameObject);
                removed++;
            }
        }
        Debug.Log($"{Tag} 已删除原始地形 {removed} 个");

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

        var existing = loaderHost.GetComponents<SECTR_RegionLoader>();
        foreach (var l in existing)
        {
            Object.DestroyImmediate(l);
        }

        var loader = loaderHost.AddComponent<SECTR_RegionLoader>();
        loader.LoadSize = new Vector3(LoadSizeXY, 1000f, LoadSizeXY);
        loader.UnloadBuffer = 0.25f;
        loader.LayersToLoad = ~0;
        Debug.Log($"{Tag} 已在 {loaderHost.name} 挂载 RegionLoader，LoadSize={loader.LoadSize}，UnloadBuffer={loader.UnloadBuffer}");

        var remainTerrains = Resources.FindObjectsOfTypeAll<Terrain>()
            .Count(t => t.gameObject.scene == scene);
        var chunks = Object.FindObjectsOfType<SECTR_Chunk>();
        var sectors = Object.FindObjectsOfType<SECTR_Sector>();

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
