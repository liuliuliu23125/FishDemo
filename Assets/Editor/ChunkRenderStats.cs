using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// chunk 渲染开销的静态统计（不进 Play Mode、不依赖 GPU）。
///
/// ── 为什么不用 Profiler 出数 ──
/// batchmode 下没有可用的 GPU 与 Play 循环，Profiler/Frame Debugger 拿不到有效帧。
/// 但"一帧要提交多少东西"这件事，绝大部分可以从场景数据静态算出来：
///   - 每块地形在 8 层 splat 下要画几个 pass；
///   - detail（草）实例总数 = 渲染密度 × 该层非零格数 × 距离内比例；
///   - 树实例总数 = TreeInstance 数；
///   - 普通 Renderer 数 = MeshRenderer 数量。
/// 这些正是 draw call 与顶点量的**上界来源**，可作为回归对比基线。
///
/// ── 能证明什么、不能证明什么 ──
/// 能：参数改动前后"提交量"的变化倍数（例如草实例从 570 万降到多少）。
/// 不能：真实 GPU 毫秒数。那必须上 iQOO 16 实测，见 performance_budget.md。
///
/// 用法：
///   Unity.exe -batchmode -projectPath <proj> -executeMethod ChunkRenderStats.Run
///   结果写入 Logs/Gaia/chunk-render-stats.txt
/// </summary>
public static class ChunkRenderStats
{
    private const string WorldScenePath = "Assets/Scenes/World.unity";
    private const string ResultPath = "Logs/Gaia/chunk-render-stats.txt";
    private const string Tag = "[ChunkStats]";

    /// <summary>URP TerrainLit 单 pass 只吃 4 层 splat，超过要走 TerrainLitAdd 追加 pass。</summary>
    private const int LayersPerPass = 4;

    public static void Run()
    {
        var sb = new StringBuilder();
        void W(string s) { Debug.Log($"{Tag} {s}"); sb.AppendLine(s); }

        var world = EditorSceneManager.OpenScene(WorldScenePath, OpenSceneMode.Single);
        var chunks = UnityEngine.Object.FindObjectsOfType<SECTR_Chunk>()
            .OrderBy(c => c.ScenePath)
            .ToList();

        W($"活跃 chunk = {chunks.Count}");

        long totalTrees = 0, totalDetailVisible = 0, totalDetailAll = 0;
        int totalMeshRenderers = 0, totalLayers = 0, maxLayers = 0;
        int terrainCount = 0;
        var perChunk = new List<(string name, int layers, int passes, int trees, long detailAll, long detailVisible, int mrs, float detailDist, float density)>();

        foreach (var c in chunks)
        {
            if (string.IsNullOrEmpty(c.NodeName) || !File.Exists(c.NodeName)) continue;

            var scene = EditorSceneManager.OpenScene(c.NodeName, OpenSceneMode.Additive);

            var terrains = UnityEngine.Object.FindObjectsOfType<Terrain>()
                .Where(t => t.gameObject.scene == scene).ToList();
            var mrs = UnityEngine.Object.FindObjectsOfType<MeshRenderer>()
                .Count(r => r.gameObject.scene == scene);

            foreach (var t in terrains)
            {
                terrainCount++;
                var td = t.terrainData;
                if (td == null) continue;

                int layers = td.terrainLayers?.Length ?? 0;
                totalLayers += layers;
                maxLayers = Mathf.Max(maxLayers, layers);

                int passes = layers <= 0 ? 1 : Mathf.CeilToInt(layers / (float)LayersPerPass);

                // detail 实例量：逐层把所有非零格的值累加。
                // 这是"全密度、全距离"的总量，实际提交量还要乘距离剔除与 density。
                long detailAll = 0;
                for (int l = 0; l < (td.detailPrototypes?.Length ?? 0); l++)
                {
                    var layer = td.GetDetailLayer(0, 0, td.detailWidth, td.detailHeight, l);
                    foreach (int v in layer) if (v > 0) detailAll += v;
                }

                // 渲染期实际密度 = Terrain.detailObjectDensity；距离剔除无法静态精确算，
                // 这里用 "detailObjectDistance / 1024m(块边长) 的平方占比" 作粗估上界。
                float areaRatio = Mathf.Clamp01((t.detailObjectDistance * t.detailObjectDistance) /
                                                (512f * 512f));
                long detailVisible = (long)(detailAll * t.detailObjectDensity * areaRatio);

                totalDetailAll += detailAll;
                totalDetailVisible += detailVisible;
                totalTrees += td.treeInstanceCount;

                perChunk.Add((Path.GetFileNameWithoutExtension(c.NodeName), layers, passes,
                    td.treeInstanceCount, detailAll, detailVisible, mrs,
                    t.detailObjectDistance, t.detailObjectDensity));

                W($"  {Short(c.NodeName)}: layers={layers} passes={passes} " +
                  $"trees={td.treeInstanceCount} detailAll={detailAll} " +
                  $"detailVisible~{detailVisible} mrs={mrs} " +
                  $"dist={t.detailObjectDistance} density={t.detailObjectDensity:F2} inst={(t.drawInstanced ? 1 : 0)}");
            }

            EditorSceneManager.CloseScene(scene, true);
        }

        W("");
        W("===== 汇总 =====");
        W($"Terrain 数 = {terrainCount}，chunk 数 = {chunks.Count}");
        W($"splat 层数：平均 {totalLayers / Mathf.Max(1, terrainCount):F1}，最大 {maxLayers}");
        int terrainPasses = terrainCount * Mathf.CeilToInt(Mathf.Max(1, maxLayers) / (float)LayersPerPass);
        W($"地形 pass 数（全部 chunk）= {terrainPasses}（{maxLayers} 层 → 每块 {Mathf.CeilToInt(Mathf.Max(1, maxLayers) / (float)LayersPerPass)} pass）");

        // 常驻按 RegionLoader 实测值：单点最多 4 块
        const int Resident = 4;
        W("");
        W($"常驻 chunk 假设 = {Resident}（RegionLoader 实测单点最多 4 块）");
        W($"常驻地形 pass ≈ {Resident * Mathf.CeilToInt(Mathf.Max(1, maxLayers) / (float)LayersPerPass)}");
        W($"树上界（全部）= {totalTrees}；常驻估算 ≈ {totalTrees * Resident / Mathf.Max(1, chunks.Count)}");
        W($"草实例（全密度全距离）= {totalDetailAll:N0}");
        W($"草实例（渲染密度+距离粗估）= {totalDetailVisible:N0}");
        W($"MeshRenderer 总数 = {totalMeshRenderers + perChunk.Sum(p => p.mrs)}");

        W("");
        W("===== 对照（若 splat 降到 4 层）=====");
        W($"地形 pass 将从 {terrainPasses} 降到 {terrainCount * 1}，即减半");

        Directory.CreateDirectory(Path.GetDirectoryName(ResultPath));
        File.WriteAllText(ResultPath, sb.ToString());
        W($"已写入 {ResultPath}");
        EditorApplication.Exit(0);
    }

    private static string Short(string path)
    {
        string n = Path.GetFileNameWithoutExtension(path);
        return n.Length <= 58 ? n : n.Substring(0, 58) + "…";
    }
}
