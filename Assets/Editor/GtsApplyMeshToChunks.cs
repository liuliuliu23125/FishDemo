using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// 在现有 chunk 场景里叠加 GTS mesh，并关闭 Terrain 的高度图绘制。
///
/// ── 为什么改现有场景而不是新建 ──
/// SECTR 的 chunk 场景里有一套必须保持完整的接线：
///   SECTR_Chunk（ScenePath/NodeName）+ SECTR_Sector（TotalBounds）+ SECTR_Portal + SECTR_Member。
/// 重建场景意味着要手工复原这整套结构，一旦 bounds 或 portal 对不上，
/// 流式加载会静默失效（RegionLoader 靠 TotalBounds 求交，错了不报错）。
/// 所以在原场景上做增量修改，风险最低、可回滚。
///
/// ── 具体改动 ──
/// 1. 在 Terrain 下新增 GTS_Mesh 子对象，挂 4 个 subTile（MeshFilter + MeshRenderer + GTS 材质）
/// 2. Terrain.drawHeightmap = false（视觉改由 mesh 承担）
/// 3. Terrain.drawTreesAndFoliage 保持 true（树/草仍由 Terrain 渲染，本次不动）
/// 4. TerrainCollider 保留（物理不变）
///
/// 这样：地形表面 = mesh 绘制，树草 = Terrain 绘制，碰撞 = TerrainCollider。
/// 三者解耦，任一部分可单独回退。
///
/// 用法：
///   Unity.exe -batchmode -projectPath <proj> -executeMethod GtsApplyMeshToChunks.RunOne \
///       -gtsChunk "World_SECTR ... Sector"
///   Unity.exe ... -executeMethod GtsApplyMeshToChunks.RunAll
///   结果写 Logs/Gaia/gts-apply/<chunk>.txt 或 Logs/Gaia/gts-apply-all.txt
/// </summary>
public static class GtsApplyMeshToChunks
{
    private const string Tag = "[GTS-Apply]";
    private const string ChunkDir = "Assets/Scenes/World/Chunks";
    private const string WorldScenePath = "Assets/Scenes/World.unity";
    private const string GtsUserDataRoot = "Assets/GTS User Data";
    private const string LogDir = "Logs/Gaia/gts-apply";

    /// <summary>mesh 容器节点名。重复执行时先删同名节点，保证幂等。</summary>
    private const string MeshNodeName = "GTS_Mesh";

    public static void RunOne()
    {
        string chunk = GetArg("-gtsChunk");
        ApplyToChunk(chunk, out string report);
        Directory.CreateDirectory(LogDir);
        File.WriteAllText(Path.Combine(LogDir, Sanitize(chunk) + ".txt"), report);
        bool ok = report.Contains("结论：PASS");
        EditorApplication.Exit(ok ? 0 : 2);
    }

    public static void RunAll()
    {
        var sb = new StringBuilder();
        var active = LoadActiveChunkPaths();
        sb.AppendLine($"活跃 chunk = {active.Count}");

        int ok = 0, fail = 0;
        foreach (var chunk in active.OrderBy(x => x))
        {
            ApplyToChunk(chunk, out string report);
            sb.AppendLine(report);
            if (report.Contains("结论：PASS")) ok++; else fail++;
        }
        sb.AppendLine();
        sb.AppendLine($"===== 汇总：成功 {ok} / {ok + fail} =====");
        bool pass = fail == 0 && ok > 0;
        sb.AppendLine($"==== 结论：{(pass ? "PASS" : "FAIL")} ====");

        Directory.CreateDirectory("Logs/Gaia");
        File.WriteAllText("Logs/Gaia/gts-apply-all.txt", sb.ToString());
        EditorApplication.Exit(pass ? 0 : 2);
    }

    private static void ApplyToChunk(string chunk, out string report)
    {
        var sb = new StringBuilder();
        void W(string s) { Debug.Log($"{Tag} {s}"); sb.AppendLine(s); }
        W($"chunk = {chunk}");

        if (string.IsNullOrEmpty(chunk)) { W("缺少 -gtsChunk"); W("==== 结论：FAIL ===="); report = sb.ToString(); return; }

        try
        {
            string scenePath = $"{ChunkDir}/{chunk}.unity";
            if (!File.Exists(scenePath)) { W($"场景不存在：{scenePath}"); W("==== 结论：FAIL ===="); report = sb.ToString(); return; }

            string meshDir = $"{GtsUserDataRoot}/Scenes/{chunk}";
            if (!Directory.Exists(meshDir)) { W("无 GTS 输出目录"); W("==== 结论：FAIL ===="); report = sb.ToString(); return; }

            var meshPaths = Directory.GetFiles(meshDir, "*.mesh", SearchOption.AllDirectories)
                .Select(p => p.Replace('\\', '/')).OrderBy(p => p).ToList();
            var matPaths = Directory.GetFiles(meshDir, "*.mat", SearchOption.AllDirectories)
                .Select(p => p.Replace('\\', '/')).ToList();
            if (meshPaths.Count == 0 || matPaths.Count == 0)
            {
                W($"mesh={meshPaths.Count} mat={matPaths.Count}");
                W("==== 结论：FAIL ===="); report = sb.ToString(); return;
            }

            var material = AssetDatabase.LoadAssetAtPath<Material>(matPaths[0]);
            W($"材质={material?.name} shader={material?.shader?.name}");

            var scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);
            var terrains = UnityEngine.Object.FindObjectsOfType<Terrain>();
            if (terrains.Length == 0) { W("无 Terrain"); W("==== 结论：FAIL ===="); report = sb.ToString(); return; }
            var terrain = terrains[0];
            W($"Terrain={terrain.name} pos={terrain.transform.position}");

            // 幂等：删掉旧的 mesh 节点
            var old = terrain.transform.Find(MeshNodeName);
            if (old != null) UnityEngine.Object.DestroyImmediate(old.gameObject);

            var meshRoot = new GameObject(MeshNodeName);
            meshRoot.transform.SetParent(terrain.transform, false);
            // Terrain 的世界坐标是地块左上角，mesh 顶点是相对地形的局部坐标（GTS BuildUnityMesh 起点 0），
            // 所以 mesh 节点放在 Terrain 本地原点即可对齐。
            meshRoot.transform.localPosition = Vector3.zero;

            int created = 0;
            foreach (var mp in meshPaths)
            {
                var mesh = AssetDatabase.LoadAssetAtPath<Mesh>(mp);
                if (mesh == null) { W($"  mesh 加载失败：{Path.GetFileName(mp)}"); continue; }

                var go = new GameObject(ShortName(Path.GetFileNameWithoutExtension(mp)));
                go.transform.SetParent(meshRoot.transform, false);
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                var mr = go.AddComponent<MeshRenderer>();
                mr.sharedMaterial = material;
                mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
                mr.receiveShadows = true;
                // 不要标 static：静态合批会在构建期把 subTile 合成大 VBO，
                // 破坏 SRP Batcher 的按材质归并粒度。静态合批本身由 BatchingSetup 关闭，
                // 这里双重保证，避免重跑流水线后又把标记加回来。
                go.isStatic = false;
                created++;
            }

            // 关掉 Terrain 自身的高度图绘制，视觉交给 mesh
            terrain.drawHeightmap = false;
            W($"  已创建 mesh 子物体={created}, drawHeightmap=false, drawTreesAndFoliage={terrain.drawTreesAndFoliage}");

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            AssetDatabase.SaveAssets();

            // 校验：场景里应有 N 个 MeshRenderer 指向 GTS 材质，且 Terrain 不再画高度图
            var renderers = UnityEngine.Object.FindObjectsOfType<MeshRenderer>()
                .Count(r => r.sharedMaterial == material);
            W($"  校验：GTS 材质 MeshRenderer={renderers}, drawHeightmap={terrain.drawHeightmap}");
            bool ok = created > 0 && renderers >= created && !terrain.drawHeightmap;
            W($"==== 结论：{(ok ? "PASS" : "FAIL")} ====");
        }
        catch (Exception ex)
        {
            W($"异常：{ex.GetType().Name}: {ex.Message}");
            W("==== 结论：FAIL ====");
        }
        report = sb.ToString();
    }

    private static string ShortName(string full)
    {
        int i = full.LastIndexOf("SubTile", StringComparison.Ordinal);
        return i >= 0 ? full.Substring(i) : full;
    }

    private static HashSet<string> LoadActiveChunkPaths()
    {
        var result = new HashSet<string>();
        EditorSceneManager.OpenScene(WorldScenePath, OpenSceneMode.Single);
        foreach (var c in UnityEngine.Object.FindObjectsOfType<SECTR_Chunk>())
            if (!string.IsNullOrEmpty(c.NodeName))
                result.Add(Path.GetFileNameWithoutExtension(c.NodeName));
        return result;
    }

    private static string GetArg(string name)
    {
        var args = Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == name) return args[i + 1];
        return null;
    }

    private static string Sanitize(string s)
    {
        if (string.IsNullOrEmpty(s)) return "unknown";
        foreach (char c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        return s;
    }
}
