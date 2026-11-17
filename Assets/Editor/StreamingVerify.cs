using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class StreamingVerify
{
    private const string WorldScenePath = "Assets/Scenes/World.unity";
    private const string ResultPath = "Logs/Gaia/streaming-verify-result.txt";
    private const string Tag = "[StreamingVerify]";

    public static void Run()
    {
        var scene = EditorSceneManager.OpenScene(WorldScenePath, OpenSceneMode.Single);
        var sectors = Object.FindObjectsOfType<SECTR_Sector>();
        var loader = Object.FindObjectOfType<SECTR_RegionLoader>();

        var sb = new StringBuilder();
        void W(string s) { Debug.Log($"{Tag} {s}"); sb.AppendLine(s); }

        W($"场景内 Sector 数 = {sectors.Length}");
        W($"RegionLoader LoadSize = {(loader != null ? loader.LoadSize.ToString() : "缺失")}");
        if (loader == null || sectors.Length == 0) { W("前置条件不满足，退出"); Flush(sb); EditorApplication.Exit(1); return; }

        Vector3 loadSize = loader.LoadSize;

        var samplePositions = new List<Vector3>();
        for (int i = 0; i < 7; i++)
        {
            for (int j = 0; j < 7; j++)
            {
                float x = Mathf.Lerp(-1536f, 1536f, i / 6f);
                float z = Mathf.Lerp(-1536f, 1536f, j / 6f);
                samplePositions.Add(new Vector3(x, 350f, z));
            }
        }

        var everSelected = new HashSet<SECTR_Sector>();
        var countsAtPositions = new List<int>();
        int maxAtOnePoint = 0;

        foreach (var pos in samplePositions)
        {
            var loadBounds = new Bounds(pos, loadSize);
            var selected = new List<SECTR_Sector>();
            SECTR_Sector.GetContaining(ref selected, loadBounds);
            selected = selected.Where(s => s.Frozen).ToList();
            countsAtPositions.Add(selected.Count);
            maxAtOnePoint = Mathf.Max(maxAtOnePoint, selected.Count);
            foreach (var s in selected) everSelected.Add(s);
        }

        W($"采样点数量 = {samplePositions.Count}");
        W($"单点最多同时选中 chunk 数 = {maxAtOnePoint}（应远小于 36，说明有剔除）");
        W($"被至少一个采样点选中过的 chunk 数 = {everSelected.Count} / {sectors.Length}（应等于 36，说明全覆盖）");

        int centerCount = CountAt(Vector3.zero, loadSize);
        int cornerCount = CountAt(new Vector3(-1536f, 350f, -1536f), loadSize);
        W($"中心点选中 = {centerCount}，角落点选中 = {cornerCount}（流式应随位置变化且都 > 0）");

        var buildSet = new HashSet<string>(EditorBuildSettings.scenes.Select(x => Path.GetFileNameWithoutExtension(x.path)));
        int pathMissing = 0, openFail = 0, openOk = 0;
        var chunks = Object.FindObjectsOfType<SECTR_Chunk>();
        foreach (var c in chunks)
        {
            if (!buildSet.Contains(c.ScenePath)) { pathMissing++; W($"ScenePath 未登记：{c.ScenePath}"); continue; }
            try
            {
                var cs = EditorSceneManager.OpenScene(c.NodeName, OpenSceneMode.Additive);
                EditorSceneManager.CloseScene(cs, true);
                openOk++;
            }
            catch (System.Exception e)
            {
                openFail++;
                W($"打开失败 {c.ScenePath}: {e.Message}");
            }
        }
        W($"chunk 场景验证：可打开 {openOk}，打开失败 {openFail}，路径缺失 {pathMissing}");

        bool pass =
            everSelected.Count == sectors.Length &&
            maxAtOnePoint < sectors.Length &&
            centerCount > 0 && cornerCount > 0 &&
            pathMissing == 0 && openFail == 0 && openOk == chunks.Length;

        W($"==== 结论：{(pass ? "PASS" : "FAIL")} ====");
        Flush(sb);
        EditorApplication.Exit(pass ? 0 : 2);
    }

    private static int CountAt(Vector3 pos, Vector3 loadSize)
    {
        var b = new Bounds(pos, loadSize);
        var list = new List<SECTR_Sector>();
        SECTR_Sector.GetContaining(ref list, b);
        return list.Count(s => s.Frozen);
    }

    private static void Flush(StringBuilder sb)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ResultPath));
        File.WriteAllText(ResultPath, sb.ToString());
    }
}
