using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// 量化「树的同屏可见量」：给同一个相机机位，按各 Terrain 当前的
/// treeDistance / treeBillboardDistance / treeMaximumFullLODCount，
/// 算出真正会被提交的树有多少棵、其中多少走完整 LOD、多少走 billboard。
///
/// ── 为什么必须自己算，不能读现成统计 ──
/// ChunkRenderStats 只报 treeInstanceCount（世界里有多少棵），
/// 但"同屏开销"取决于距离剔除后的可见量。
/// Terrain 的树裁剪在 native 层做，batchmode 下拿不到逐实例的可见列表，
/// 所以这里按 Unity 的规则复算：
///   1. 超出 treeDistance 的不提交；
///   2. 最近的 treeMaximumFullLODCount 棵（全局、按距离排序）走完整 LOD；
///   3. 其余在 treeBillboardDistance 内走完整 LOD，之外走末级 billboard。
///
/// 注意这是"模型推算"，不是引擎实测。它用来对比参数改动前后的相对变化，
/// 绝对值仍需在真机 Frame Debugger 里核对。
///
/// 用法：-executeMethod TreeVisibleCount.Run
/// 输出 Logs/tree-visible-count.txt
/// </summary>
public static class TreeVisibleCount
{
    private const string Tag = "[TreeVis]";
    private const string WorldScenePath = "Assets/Scenes/World.unity";
    private const string ResultPath = "Logs/tree-visible-count.txt";

    public static void Run()
    {
        var sb = new StringBuilder();
        void W(string s) { Debug.Log($"{Tag} {s}"); sb.AppendLine(s); }

        EditorSceneManager.OpenScene(WorldScenePath, OpenSceneMode.Single);
        int opened = 0;
        foreach (var s in EditorBuildSettings.scenes)
        {
            if (!s.enabled || s.path.EndsWith("World.unity") || !File.Exists(s.path)) continue;
            EditorSceneManager.OpenScene(s.path, OpenSceneMode.Additive);
            opened++;
        }
        W($"附加加载 chunk = {opened}");

        var terrains = Object.FindObjectsOfType<Terrain>().Where(t => t != null && t.terrainData != null).ToArray();
        W($"Terrain = {terrains.Length}");

        W("");
        W("=== 当前树参数分布 ===");
        foreach (var g in terrains.GroupBy(t => new {
                     t.treeDistance, t.treeBillboardDistance, t.treeMaximumFullLODCount, t.treeCrossFadeLength }))
        {
            W($"  dist={g.Key.treeDistance} billboard={g.Key.treeBillboardDistance} " +
              $"maxFull={g.Key.treeMaximumFullLODCount} crossfade={g.Key.treeCrossFadeLength} → {g.Count()} 块");
        }

        // 取几个有代表性的机位：世界中心、巡游路线上、某个树密集块中心
        var spots = new List<(string label, Vector3 pos)>();
        spots.Add(("世界中心 (0,地面,0)", Vector3.zero));

        // 巡游路线 195°/1100m（草验收脚本用的同一位置）
        float th = 195f * Mathf.Deg2Rad;
        spots.Add(("巡游 195°/1100m", new Vector3(Mathf.Cos(th) * 1100f, 0f, Mathf.Sin(th) * 1100f)));

        // 树最多的那一块中心
        var densest = terrains.OrderByDescending(t => t.terrainData.treeInstanceCount).First();
        spots.Add(($"树最密块 {densest.name}", densest.transform.position + densest.terrainData.size * 0.5f));

        W("");
        foreach (var (label, raw) in spots)
        {
            // 贴地：用地形采样校正高度
            Vector3 pos = raw;
            var probe = terrains.FirstOrDefault(t =>
            {
                var p = t.transform.position;
                var sz = t.terrainData.size;
                return pos.x >= p.x && pos.x <= p.x + sz.x && pos.z >= p.z && pos.z <= p.z + sz.z;
            });
            if (probe != null)
                pos.y = probe.SampleHeight(new Vector3(pos.x, 0, pos.z)) + probe.transform.position.y;

            W($"===== 机位：{label}  位置={pos} =====");
            Report(pos, terrains, W);
            W("");
        }

        Directory.CreateDirectory("Logs");
        File.WriteAllText(ResultPath, sb.ToString());
        if (Application.isBatchMode) EditorApplication.Exit(0);
    }

    private static void Report(Vector3 camPos, Terrain[] terrains, System.Action<string> W)
    {
        // 收集所有在 treeDistance 内的树实例及其距离
        var inRange = new List<float>();          // 每棵树到相机的距离
        int totalAll = 0;

        foreach (var t in terrains)
        {
            var td = t.terrainData;
            var insts = td.treeInstances;
            if (insts.Length == 0) continue;
            totalAll += insts.Length;

            float dist = t.treeDistance;
            Vector3 origin = t.transform.position;
            Vector3 size = td.size;
            float d2 = dist * dist;

            foreach (var ti in insts)
            {
                Vector3 wp = origin + Vector3.Scale(ti.position, size);
                float dd = (wp - camPos).sqrMagnitude;
                if (dd <= d2) inRange.Add(Mathf.Sqrt(dd));
            }
        }

        int visible = inRange.Count;
        float pct = totalAll > 0 ? visible * 100f / totalAll : 0f;

        // maxFullLODCount 是全局的（Terrain 统一设置），取第一块的
        int maxFull = terrains.Length > 0 ? terrains[0].treeMaximumFullLODCount : 0;
        float billboardDist = terrains.Length > 0 ? terrains[0].treeBillboardDistance : 0f;
        float treeDist = terrains.Length > 0 ? terrains[0].treeDistance : 0f;

        inRange.Sort();
        int fullByCount = Mathf.Min(maxFull, visible);           // 最近的 N 棵走完整 LOD
        int fullTotal = Mathf.Min(fullByCount, visible);
        int billboard = visible - fullTotal;

        W($"  世界树总数        = {totalAll:N0}");
        W($"  距离内可见（treeDistance={treeDist}）= {visible:N0}（{pct:F1}%）");
        W($"  其中走完整 LOD    ≈ {fullTotal:N0}（受 maxFullLODCount={maxFull} 限制）");
        W($"  走末级 billboard  ≈ {billboard:N0}");
        if (inRange.Count > 0)
        {
            W($"  可见树距离：最近 {inRange[0]:F1}m，中位 {inRange[inRange.Count / 2]:F1}m，" +
              $"最远 {inRange[inRange.Count - 1]:F1}m");
        }
    }
}
