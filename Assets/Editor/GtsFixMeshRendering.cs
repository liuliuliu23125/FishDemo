using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using ProceduralWorlds.GTS;

/// <summary>
/// 修复 GTS mesh chunk 的渲染数据注入（视觉与 Terrain 不一致问题）。
///
/// ── 根因（实测确认）──
/// shader 的 splat 混合依赖一组**全局** Vector 数组：
///   _HeightData / _UVData / _ColorData / _LayerDataA / _TriPlanarData /
///   _MaskMapRemapData / _DisplacementData / _MaskMapRemapMin/Max
/// 这些数组由 GTSComponent.UpdateMaterial() 通过 Shader.SetGlobalVectorArray 注入。
///
/// 但 GtsApplyMeshToChunks 只创建了裸 MeshRenderer（MeshFilter + MeshRenderer + 材质），
/// **没有挂 GTSMesh 组件**，所以：
///   - 运行时无人调用 UpdateMaterial()
///   - 上述全局数组从未被设置 → shader 读到 0 或空
///   - 结果：splat 权重、UV 平铺、层颜色全部错乱 → 与 Terrain 视觉差异巨大
///
/// 同时 GTSMesh.worldSize 硬编码 (1024,0,1024)，而本工程 subTile 实际是 256m，
/// 会让 tiling（worldSize / tileSize）算错 4 倍。用 GTSMeshWorldSizePatch 覆盖。
///
/// ── 修法 ──
/// 给每个 subTile 挂 GTSMesh，设 profile / material / colormap，
/// 并在编辑器里调 Refresh() 让材质与全局数组就位；
/// 同时提供一个运行时注入组件，保证真机上 Start 时也设置一次全局数组。
///
/// 用法：
///   Unity.exe -batchmode -projectPath <proj> -executeMethod GtsFixMeshRendering.RunAll
///   结果写 Logs/Gaia/gts-fix-rendering.txt
/// </summary>
public static class GtsFixMeshRendering
{
    private const string Tag = "[GTS-Fix]";
    private const string ResultPath = "Logs/Gaia/gts-fix-rendering.txt";
    private const string ChunkDir = "Assets/Scenes/World/Chunks";
    private const string WorldScenePath = "Assets/Scenes/World.unity";
    private const string ProfilePath = "Assets/Gaia User Data/GTS World Profile.asset";

    public static void RunAll()
    {
        var sb = new StringBuilder();
        void W(string s) { Debug.Log($"{Tag} {s}"); sb.AppendLine(s); }

        var profile = AssetDatabase.LoadAssetAtPath<GTSProfile>(ProfilePath);
        if (profile == null) { W("找不到 Profile"); Flush(sb, 1); return; }
        W($"Profile = {profile.name}, gtsLayers = {profile.gtsLayers.Count}");

        // ── 先修 profile 的 worldSize 相关设置 ──
        // GTSMesh.worldSize 是硬编码的，不改源码就无法影响它；
        // 但 tiling 用的是 worldSize / gtsLayer.tileSize，
        // 所以可以通过调 gtsLayer.tileSize 抵消：tileSize' = tileSize * (实际worldSize / 1024)
        // 本工程 subTile = 256m，GTSMesh 假设 1024m → 比例 0.25
        W("");
        W("=== 检查 gtsLayers 的 tileSize ===");
        foreach (var l in profile.gtsLayers)
            W($"  {l.name}: tileSize=({l.tileSize.x},{l.tileSize.y})");

        var activeChunks = LoadActiveChunkPaths();
        W($"活跃 chunk = {activeChunks.Count}");

        int ok = 0, fail = 0, fixedTiles = 0;
        foreach (string chunk in activeChunks.OrderBy(x => x))
        {
            try
            {
                string scenePath = $"{ChunkDir}/{chunk}.unity";
                if (!File.Exists(scenePath)) { fail++; continue; }

                var scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);
                var terrain = UnityEngine.Object.FindObjectsOfType<Terrain>().FirstOrDefault();
                if (terrain == null) { fail++; continue; }

                var meshRoot = terrain.transform.Find("GTS_Mesh");
                if (meshRoot == null) { W($"  {chunk}: 无 GTS_Mesh 节点"); fail++; continue; }

                var rends = meshRoot.GetComponentsInChildren<MeshRenderer>(true);
                if (rends.Length == 0) { W($"  {chunk}: 无 MeshRenderer"); fail++; continue; }

                // 每块地形的世界尺寸（用于校正 tiling）
                Vector3 worldSize = terrain.terrainData.size;

                int n = 0;
                foreach (var mr in rends)
                {
                    var go = mr.gameObject;

                    // 幂等：清掉旧的 GTSMesh
                    var old = go.GetComponent<GTSMesh>();
                    if (old != null) UnityEngine.Object.DestroyImmediate(old);

                    var gtsMesh = go.AddComponent<GTSMesh>();
                    gtsMesh.profile = profile;
                    gtsMesh.material = mr.sharedMaterial;
                    // colormap 为 null 时 GTS 内部会跳过 colormap 采样
                    gtsMesh.colormap = null;

                    // ── 关键：写入该 subTile 实际覆盖的世界尺寸 ──
                    // GTSMesh.worldSize 决定 tiling（worldSize / layer.tileSize）。
                    // 原实现硬编码 1024，而本工程 subTile 实际是 chunk / 2 = 256m，
                    // 不校正会让贴图密度错 4 倍。
                    // 优先用 mesh 包围盒（转换后单位即米），它最准确地反映这块 mesh 的实际跨度。
                    Vector3 subSize = worldSize;
                    var mf = go.GetComponent<MeshFilter>();
                    if (mf != null && mf.sharedMesh != null)
                    {
                        var b = mf.sharedMesh.bounds.size;
                        if (b.x > 0.001f && b.z > 0.001f) subSize = b;
                    }
                    gtsMesh.SetWorldSizeOverride(subSize);

                    // 让材质与全局数组立刻就位（编辑器侧）
                    gtsMesh.Refresh();

                    // 运行时注入：GTSMesh 的 Update 只在 m_isDirty 时 Refresh，
                    // 且 [ExecuteAlways] 在真机上无效。加一个轻量组件保证 Start 时注入一次。
                    if (go.GetComponent<GtsRuntimeDataBootstrap>() == null)
                        go.AddComponent<GtsRuntimeDataBootstrap>().profile = profile;
                    n++;
                }

                EditorSceneManager.MarkSceneDirty(scene);
                EditorSceneManager.SaveScene(scene);
                W($"  {chunk}: GTSMesh x{n}，worldSize={worldSize}");
                fixedTiles += n;
                ok++;
            }
            catch (Exception ex)
            {
                W($"  {chunk}: 异常 {ex.GetType().Name}: {ex.Message}");
                fail++;
            }
        }

        W("");
        W($"===== 汇总：成功 chunk={ok}，失败={fail}，修复 subTile={fixedTiles} =====");
        bool pass = fail == 0 && ok > 0;
        W($"==== 结论：{(pass ? "PASS" : "FAIL")} ====");
        Flush(sb, pass ? 0 : 2);
    }

    private static HashSet<string> LoadActiveChunkPaths()
    {
        var r = new HashSet<string>();
        EditorSceneManager.OpenScene(WorldScenePath, OpenSceneMode.Single);
        foreach (var c in UnityEngine.Object.FindObjectsOfType<SECTR_Chunk>())
            if (!string.IsNullOrEmpty(c.NodeName))
                r.Add(Path.GetFileNameWithoutExtension(c.NodeName));
        return r;
    }

    private static void Flush(StringBuilder sb, int code)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ResultPath));
        File.WriteAllText(ResultPath, sb.ToString());
        EditorApplication.Exit(code);
    }
}
