using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// 流式加载机制的确定性验证（不依赖 Play Mode）。
///
/// ── 为什么不直接进 Play Mode ──
/// Unity 在 -batchmode 下对 Play Mode 支持不稳定，容易挂死或跳过帧。
/// 而 SECTR 的流式判定本质是纯空间计算：
///   每帧用 SECTR_RegionLoader 的 LoadSize 立方体与每个 Sector 的 TotalBounds 求交
///   （SECTR_Sector.GetContaining），命中即 AddReference 触发场景加载。
/// 所以只要验证三件事，就能确定运行时会把对的 chunk 加载/卸载：
///   1. 空间选择：在不同相机位置，GetContaining 选出的 chunk 集合会随位置变化，
///      且远处 chunk 被剔除（不是 36 个全加载）；
///   2. 覆盖完整：每一个 chunk 都至少能在某个位置被选中（不会永远不加载）；
///   3. 场景可加载：每个 chunk 的 ScenePath 能在 Build Settings 里解析并成功 OpenScene。
/// 这三点成立，配合已验证的"36 chunk 已导出 + 已进 Build Settings"，流式链路即成立。
/// </summary>
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

        // ---- 1 & 2：在覆盖世界的采样点上模拟加载判定 ----
        // 世界范围 [-1536, +1536]，采样 7×7 网格点
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

        // 中心点与边缘点对比，确认选择随位置变化
        int centerCount = CountAt(Vector3.zero, loadSize);
        int cornerCount = CountAt(new Vector3(-1536f, 350f, -1536f), loadSize);
        W($"中心点选中 = {centerCount}，角落点选中 = {cornerCount}（流式应随位置变化且都 > 0）");

        // ---- 3：每个 chunk 的场景可加载性 ----
        var buildSet = new HashSet<string>(EditorBuildSettings.scenes.Select(x => Path.GetFileNameWithoutExtension(x.path)));
        int pathMissing = 0, openFail = 0, openOk = 0;
        var chunks = Object.FindObjectsOfType<SECTR_Chunk>();
        foreach (var c in chunks)
        {
            if (!buildSet.Contains(c.ScenePath)) { pathMissing++; W($"ScenePath 未登记：{c.ScenePath}"); continue; }
            // 尝试以 Additive 打开再关闭，验证场景文件本身可加载且不报错
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
