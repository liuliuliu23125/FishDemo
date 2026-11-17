using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

public static class GtsApplyMeshToChunks
{
    private const string Tag = "[GTS-Apply]";
    private const string ChunkDir = "Assets/Scenes/World/Chunks";
    private const string WorldScenePath = "Assets/Scenes/World.unity";
    private const string GtsUserDataRoot = "Assets/GTS User Data";
    private const string LogDir = "Logs/Gaia/gts-apply";

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

            var old = terrain.transform.Find(MeshNodeName);
            if (old != null) UnityEngine.Object.DestroyImmediate(old.gameObject);

            var meshRoot = new GameObject(MeshNodeName);
            meshRoot.transform.SetParent(terrain.transform, false);
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
                go.isStatic = false;
                created++;
            }

            terrain.drawHeightmap = false;
            W($"  已创建 mesh 子物体={created}, drawHeightmap=false, drawTreesAndFoliage={terrain.drawTreesAndFoliage}");

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            AssetDatabase.SaveAssets();

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
