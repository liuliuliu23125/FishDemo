using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// 只读探针：把全世界的真实地形高度采成网格，用于选湖址、定水位、算挖方量。
///
/// ── 为什么要先采网格再动手 ──
/// 主场景 World.unity 里没有 Terrain（地形全在 36 个 chunk 场景里，由 SECTR 运行时加载），
/// 所以必须把 chunk 场景 additive 打开才能拿到真实高度。
/// 而"选哪块洼地、水位定多少、要挖多深"是几何问题，先落一份网格数据，
/// 后续设计（Python 分析）与施工（CarveLake）用同一份数据，避免反复跑 Unity。
///
/// ── 为什么用 Terrain.SampleHeight 而不是读 heightmap ──
/// 各 chunk 的 TerrainData 是切块产物，原点/分辨率不一；SampleHeight 是世界坐标，
/// 与 GTS mesh 的顶点空间一致（mesh 顶点也是米），可直接对照。
///
/// 用法：
///   Unity.exe -batchmode -projectPath <proj> -executeMethod LakeSiteProbe.Run -logFile <log>
/// 输出：
///   Logs/Lake/site-probe.txt   元数据 + 统计
///   Logs/Lake/height-grid.csv  世界高度网格（x,z,height）
/// </summary>
public static class LakeSiteProbe
{
    private const string Tag = "[LakeProbe]";
    private const string WorldScenePath = "Assets/Scenes/World.unity";
    private const string OutDir = "Logs/Lake";
    private const string ResultPath = OutDir + "/site-probe.txt";
    private const string GridPath = OutDir + "/height-grid.csv";

    /// <summary>网格采样步长（米）。32m 对 3072m 世界是 97×97，够选湖址。</summary>
    private const float Step = 32f;

    public static void Run()
    {
        var sb = new StringBuilder();
        void W(string s) { Debug.Log($"{Tag} {s}"); sb.AppendLine(s); }

        try
        {
            Directory.CreateDirectory(OutDir);
            EditorSceneManager.OpenScene(WorldScenePath, OpenSceneMode.Single);

            // 与 WorldProbe 同理：用 Build Settings 枚举 chunk，绕开 SECTR 程序集依赖。
            var chunkPaths = new List<string>();
            foreach (var s in EditorBuildSettings.scenes)
            {
                if (!s.enabled) continue;
                if (s.path.EndsWith("World.unity")) continue;
                chunkPaths.Add(s.path);
            }
            foreach (var p in chunkPaths)
                if (File.Exists(p)) EditorSceneManager.OpenScene(p, OpenSceneMode.Additive);

            var terrains = UnityEngine.Object.FindObjectsOfType<Terrain>();
            W($"chunk 场景 {chunkPaths.Count}，Terrain {terrains.Length}");
            if (terrains.Length == 0) { W("==== 结论：FAIL（无 Terrain） ===="); Flush(sb, 2); return; }

            // ── 每块元数据 ──
            W("");
            W("=== 逐块元数据 ===");
            W("name | pos | size | hmRes | detailRes | trees | layers");
            foreach (var t in terrains.OrderBy(x => x.name))
            {
                var d = t.terrainData;
                if (d == null) { W($"{t.name} | TerrainData 为空"); continue; }
                W($"{t.name} | {t.transform.position} | {d.size} | hm={d.heightmapResolution} | " +
                  $"detail={d.detailResolution} | trees={d.treeInstanceCount} | layers={d.terrainLayers?.Length ?? 0}");
            }

            // ── 世界包围 ──
            float minX = float.MaxValue, maxX = float.MinValue, minZ = float.MaxValue, maxZ = float.MinValue;
            foreach (var t in terrains)
            {
                var p = t.transform.position; var s = t.terrainData.size;
                minX = Mathf.Min(minX, p.x); maxX = Mathf.Max(maxX, p.x + s.x);
                minZ = Mathf.Min(minZ, p.z); maxZ = Mathf.Max(maxZ, p.z + s.z);
            }
            W("");
            W($"世界 XZ 范围 = [{minX}, {maxX}] × [{minZ}, {maxZ}]");

            // ── 高度网格 ──
            var heights = new List<float>();
            var csv = new StringBuilder("x,z,height\n");
            int nx = Mathf.CeilToInt((maxX - minX) / Step) + 1;
            int nz = Mathf.CeilToInt((maxZ - minZ) / Step) + 1;
            for (int iz = 0; iz < nz; iz++)
            {
                float z = minZ + iz * Step;
                for (int ix = 0; ix < nx; ix++)
                {
                    float x = minX + ix * Step;
                    float h = SampleWorld(terrains, x, z);
                    if (float.IsNaN(h)) h = 0f;
                    heights.Add(h);
                    csv.Append(x.ToString("F0", CultureInfo.InvariantCulture)).Append(',')
                       .Append(z.ToString("F0", CultureInfo.InvariantCulture)).Append(',')
                       .Append(h.ToString("F2", CultureInfo.InvariantCulture)).Append('\n');
                }
            }
            File.WriteAllText(GridPath, csv.ToString());

            heights.Sort();
            float P(float q) => heights[Mathf.Clamp(Mathf.RoundToInt(q * (heights.Count - 1)), 0, heights.Count - 1)];
            W("");
            W("=== 高度分布（米） ===");
            W($"样本 {heights.Count}（步长 {Step}m）");
            W($"min={heights[0]:F1}  p1={P(0.01f):F1}  p5={P(0.05f):F1}  p25={P(0.25f):F1}  " +
              $"median={P(0.5f):F1}  p75={P(0.75f):F1}  p95={P(0.95f):F1}  max={heights[heights.Count - 1]:F1}");

            // ── 中央区域（|x|,|z| <= 800）最低点：湖址候选 ──
            W("");
            W("=== 中央区域（±800m）最低 10 个采样点 ===");
            var central = new List<(float x, float z, float h)>();
            for (int iz = 0; iz < nz; iz++)
            {
                float z = minZ + iz * Step;
                if (Mathf.Abs(z) > 800f) continue;
                for (int ix = 0; ix < nx; ix++)
                {
                    float x = minX + ix * Step;
                    if (Mathf.Abs(x) > 800f) continue;
                    central.Add((x, z, SampleWorld(terrains, x, z)));
                }
            }
            foreach (var c in central.OrderBy(c => c.h).Take(10))
                W($"  ({c.x:F0}, {c.z:F0}) h={c.h:F1}");

            // ── 以最低点为中心，半径 300m 内的高度统计（估湖盆大小） ──
            var best = central.OrderBy(c => c.h).First();
            W("");
            W($"=== 候选湖心 ({best.x:F0}, {best.z:F0}) 周边统计 ===");
            foreach (float r in new[] { 100f, 200f, 300f, 400f })
            {
                var ring = new List<float>();
                for (float z = best.z - r; z <= best.z + r; z += Step)
                    for (float x = best.x - r; x <= best.x + r; x += Step)
                        if ((new Vector2(x - best.x, z - best.z)).sqrMagnitude <= r * r)
                            ring.Add(SampleWorld(terrains, x, z));
                ring.Sort();
                W($"  r={r:F0}m: n={ring.Count} min={ring[0]:F1} max={ring[ring.Count - 1]:F1} " +
                  $"mean={ring.Average():F1} p90={ring[Mathf.RoundToInt(0.9f * (ring.Count - 1))]:F1}");
            }

            // ── 候选区内的树（TerrainData.treeInstances，位置是归一化的） ──
            W("");
            W("=== 候选湖心半径 400m 内的树（按 chunk） ===");
            int totalTrees = 0;
            foreach (var t in terrains)
            {
                var d = t.terrainData;
                if (d == null || d.treeInstanceCount == 0) continue;
                var p0 = t.transform.position; var sz = d.size;
                int n = 0;
                foreach (var ti in d.treeInstances)
                {
                    float wx = p0.x + ti.position.x * sz.x;
                    float wz = p0.z + ti.position.z * sz.z;
                    if ((new Vector2(wx - best.x, wz - best.z)).sqrMagnitude <= 400f * 400f) n++;
                }
                if (n > 0) { W($"  {t.name}: {n}"); totalTrees += n; }
            }
            W($"  合计 = {totalTrees}");

            W("");
            W("==== 结论：PASS ====");
            Flush(sb, 0);
        }
        catch (Exception ex)
        {
            W($"异常：{ex}");
            W("==== 结论：FAIL ====");
            Flush(sb, 1);
        }
    }

    /// <summary>世界坐标取高度：找覆盖该点的 Terrain，用 SampleHeight 并加上地形原点 y。</summary>
    private static float SampleWorld(Terrain[] terrains, float x, float z)
    {
        foreach (var t in terrains)
        {
            var p = t.transform.position;
            var s = t.terrainData.size;
            if (x < p.x || x > p.x + s.x || z < p.z || z > p.z + s.z) continue;
            return p.y + t.SampleHeight(new Vector3(x, 0f, z));
        }
        return float.NaN;
    }

    private static void Flush(StringBuilder sb, int code)
    {
        Directory.CreateDirectory(OutDir);
        File.WriteAllText(ResultPath, sb.ToString());
        EditorApplication.Exit(code);
    }
}
