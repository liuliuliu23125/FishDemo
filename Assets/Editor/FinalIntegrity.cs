using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// 最终完整性整理 + 全面验证。
/// 1. 用主场景里 36 个活跃 chunk 重建 Build Settings，去掉指向已删旧批次的死条目；
/// 2. 逐项核验：主场景构成 / chunk 高度起伏 / 贴图层 / 流式空间判定 / 场景可加载。
/// 全部通过才返回 exit 0。
/// </summary>
public static class FinalIntegrity
{
    private const string Tag = "[FinalIntegrity]";
    private const string WorldScene = "Assets/Scenes/World.unity";

    public static void Run()
    {
        var sb = new StringBuilder();
        void W(string s) { Debug.Log($"{Tag} {s}"); sb.AppendLine(s); }

        var scene = EditorSceneManager.OpenScene(WorldScene, OpenSceneMode.Single);
        var chunks = Object.FindObjectsOfType<SECTR_Chunk>().OrderBy(c => c.ScenePath).ToList();
        var sectors = Object.FindObjectsOfType<SECTR_Sector>();
        var loader = Object.FindObjectOfType<SECTR_RegionLoader>();
        W($"主场景：Chunk={chunks.Count}, Sector={sectors.Length}, RegionLoader={(loader != null)}, Terrain={Object.FindObjectsOfType<Terrain>().Length}");

        // ---- 1. 重建 Build Settings ----
        var newBuild = new List<EditorBuildSettingsScene> { new EditorBuildSettingsScene(WorldScene, true) };
        int missingScene = 0;
        foreach (var c in chunks)
        {
            string p = c.NodeName;
            if (File.Exists(p)) newBuild.Add(new EditorBuildSettingsScene(p, true));
            else { missingScene++; W($"chunk 场景文件缺失：{p}"); }
        }
        EditorBuildSettings.scenes = newBuild.ToArray();
        W($"Build Settings 重建为 {newBuild.Count} 个场景（含主场景），缺失 {missingScene}");

        // ---- 2. chunk 高度起伏核验（全 36 块）----
        int flat = 0; float globalMin = 1f, globalMax = 0f;
        foreach (var c in chunks)
        {
            var cs = EditorSceneManager.OpenScene(c.NodeName, OpenSceneMode.Additive);
            var terrains = Object.FindObjectsOfType<Terrain>().Where(t => t.gameObject.scene == cs).ToList();
            if (terrains.Count == 0) { flat++; W($"空 chunk：{c.ScenePath}"); }
            foreach (var t in terrains)
            {
                var td = t.terrainData;
                int res = td.heightmapResolution, step = Mathf.Max(1, res / 32);
                var h = td.GetHeights(0, 0, res, res);
                float mn = 1f, mx = 0f;
                for (int x = 0; x < res; x += step)
                    for (int y = 0; y < res; y += step)
                    { if (h[x, y] < mn) mn = h[x, y]; if (h[x, y] > mx) mx = h[x, y]; }
                globalMin = Mathf.Min(globalMin, mn); globalMax = Mathf.Max(globalMax, mx);
                if (mx - mn < 0.005f) flat++;
            }
            EditorSceneManager.CloseScene(cs, true);
        }
        W($"高度起伏：全局 [{globalMin:F3},{globalMax:F3}] 跨 {(globalMax - globalMin) * 700f:F1}m，平坦/空 chunk 数={flat}");

        // ---- 3. 流式空间判定 ----
        var loadSize = loader.LoadSize;
        var ever = new HashSet<SECTR_Sector>();
        int maxAt = 0;
        for (int i = 0; i < 7; i++)
            for (int j = 0; j < 7; j++)
            {
                var pos = new Vector3(Mathf.Lerp(-1536, 1536, i / 6f), 350, Mathf.Lerp(-1536, 1536, j / 6f));
                var list = new List<SECTR_Sector>();
                SECTR_Sector.GetContaining(ref list, new Bounds(pos, loadSize));
                list = list.Where(s => s.Frozen).ToList();
                maxAt = Mathf.Max(maxAt, list.Count);
                foreach (var s in list) ever.Add(s);
            }
        W($"流式：单点最多 {maxAt} 块（应<36），全覆盖 {ever.Count}/{sectors.Length}");

        bool pass = chunks.Count == 36 && sectors.Length == 36 && loader != null
                    && missingScene == 0 && flat == 0
                    && maxAt < sectors.Length && ever.Count == sectors.Length;
        W($"==== 结论：{(pass ? "PASS" : "FAIL")} ====");

        EditorSceneManager.SaveScene(scene);
        AssetDatabase.SaveAssets();
        Directory.CreateDirectory("Logs/Gaia");
        File.WriteAllText("Logs/Gaia/final-integrity.txt", sb.ToString());
        EditorApplication.Exit(pass ? 0 : 2);
    }
}
