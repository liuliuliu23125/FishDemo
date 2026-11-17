using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

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

        var spots = new List<(string label, Vector3 pos)>();
        spots.Add(("世界中心 (0,地面,0)", Vector3.zero));

        float th = 195f * Mathf.Deg2Rad;
        spots.Add(("巡游 195°/1100m", new Vector3(Mathf.Cos(th) * 1100f, 0f, Mathf.Sin(th) * 1100f)));

        var densest = terrains.OrderByDescending(t => t.terrainData.treeInstanceCount).First();
        spots.Add(($"树最密块 {densest.name}", densest.transform.position + densest.terrainData.size * 0.5f));

        W("");
        foreach (var (label, raw) in spots)
        {
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
        var inRange = new List<float>();
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

        int maxFull = terrains.Length > 0 ? terrains[0].treeMaximumFullLODCount : 0;
        float billboardDist = terrains.Length > 0 ? terrains[0].treeBillboardDistance : 0f;
        float treeDist = terrains.Length > 0 ? terrains[0].treeDistance : 0f;

        inRange.Sort();
        int fullByCount = Mathf.Min(maxFull, visible);
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
