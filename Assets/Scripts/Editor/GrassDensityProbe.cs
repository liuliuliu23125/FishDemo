using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// 只统计、不写盘：给定若干候选采样率，报告各自的实例总数，
/// 用于把烘焙量校准到目标值（而不是靠 0.035→0.0175 的线性直觉）。
///
/// 为什么需要：实际保留量 = Σ round(n×rate) + 稀疏格保底（30% 概率补 1 个）。
/// 保底项与 rate 无关，所以 rate 减半时总量降幅**超过**一半
/// （实测 0.035→12.65M，0.0175→4.35M，即降 65.6% 而非 50%）。
/// 要落到「减一半」必须按真实分布反算，不能等比缩放。
///
/// 用法：-executeMethod GrassDensityProbe.Run
/// 输出：Logs/Grass/density-probe.txt
/// </summary>
public static class GrassDensityProbe
{
    private const string Tag = "[GrassDensityProbe]";
    private const string ResultPath = "Logs/Grass/density-probe.txt";

    /// <summary>与 GrassBaker 保持一致。</summary>
    private const float SparseKeepChance = 0.30f;
    private const float MaxSlopeTan = 1.0f;
    private const float MinHeight = 60f + 2f;
    private const float SlopeSampleStep = 1f / 1024f;

    private static readonly float[] CandidateRates = { 0.035f, 0.030f, 0.0275f, 0.025f, 0.0225f, 0.020f, 0.0175f };

    public static void Run()
    {
        var sb = new StringBuilder();
        void W(string s) { Debug.Log($"{Tag} {s}"); sb.AppendLine(s); }

        var chunkPaths = new List<string>();
        foreach (var s in EditorBuildSettings.scenes)
        {
            if (!s.enabled || s.path.EndsWith("World.unity")) continue;
            if (File.Exists(s.path)) chunkPaths.Add(s.path);
        }
        W($"chunk 数 = {chunkPaths.Count}，候选采样率 = {string.Join(", ", CandidateRates)}");

        var totals = new long[CandidateRates.Length];
        var perType = new long[CandidateRates.Length][];
        for (int i = 0; i < totals.Length; i++) perType[i] = new long[7];

        long totalArea = 0;

        foreach (var path in chunkPaths)
        {
            var scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Additive);
            Terrain terrain = null;
            foreach (var root in scene.GetRootGameObjects())
            {
                var t = root.GetComponentInChildren<Terrain>();
                if (t != null) { terrain = t; break; }
            }
            if (terrain == null || terrain.terrainData == null)
            {
                EditorSceneManager.CloseScene(scene, true);
                continue;
            }

            var td = terrain.terrainData;
            var protos = td.detailPrototypes;
            Vector3 tPos = terrain.transform.position;
            Vector3 tSize = td.size;
            int dw = td.detailWidth, dh = td.detailHeight;
            totalArea += (long)(tSize.x * tSize.z);

            float cellW = tSize.x / dw, cellH = tSize.z / dh;
            // 确定性种子必须与 GrassBaker.BakeSeed 一致，否则保底抽样的命中格不同
            var rng = new System.Random(BakeSeed(terrain.name));

            int layerCount = Mathf.Min(protos.Length, 7);
            for (int layer = 0; layer < layerCount; layer++)
            {
                int[,] detail;
                try { detail = td.GetDetailLayer(0, 0, dw, dh, layer); }
                catch { continue; }

                for (int y = 0; y < dh; y++)
                {
                    for (int x = 0; x < dw; x++)
                    {
                        int n = detail[y, x];
                        if (n <= 0) continue;

                        // 坡度/高度过滤与烘焙一致：先算该格候选点是否会被剔除。
                        // 这里为了速度只在“该格至少会被任一 rate 保留”时才做过滤。
                        bool anyKeep = false;
                        for (int r = 0; r < CandidateRates.Length; r++)
                        {
                            if (Mathf.RoundToInt(n * CandidateRates[r]) > 0) { anyKeep = true; break; }
                        }
                        if (!anyKeep)
                        {
                            // 全靠保底：用同一个 rng 序列推进，保持与烘焙的随机流一致
                            bool keepFloor = rng.NextDouble() < SparseKeepChance;
                            for (int r = 0; r < CandidateRates.Length; r++)
                                if (keepFloor) { totals[r]++; perType[r][layer]++; }
                            continue;
                        }

                        // 有确定保留量的格：仍需消耗同样的随机数（烘焙里 take 个位置用 take 次）
                        // 为简化，这里不做位置抖动，只按几何位置判一次坡度/高度。
                        float u = (x + 0.5f) / dw;
                        float v = (y + 0.5f) / dh;
                        float wy = tPos.y + td.GetInterpolatedHeight(u, v);
                        bool pass = wy >= MinHeight;
                        if (pass)
                        {
                            float hL = td.GetInterpolatedHeight(Mathf.Clamp01(u - SlopeSampleStep), v);
                            float hR = td.GetInterpolatedHeight(Mathf.Clamp01(u + SlopeSampleStep), v);
                            float hD = td.GetInterpolatedHeight(u, Mathf.Clamp01(v - SlopeSampleStep));
                            float hU = td.GetInterpolatedHeight(u, Mathf.Clamp01(v + SlopeSampleStep));
                            float dxM = 2f * SlopeSampleStep * tSize.x;
                            float dzM = 2f * SlopeSampleStep * tSize.z;
                            float sx = Mathf.Abs(hR - hL) / Mathf.Max(1e-4f, dxM);
                            float sz = Mathf.Abs(hU - hD) / Mathf.Max(1e-4f, dzM);
                            pass = Mathf.Sqrt(sx * sx + sz * sz) <= MaxSlopeTan;
                        }

                        if (!pass) continue;

                        for (int r = 0; r < CandidateRates.Length; r++)
                        {
                            int take = Mathf.RoundToInt(n * CandidateRates[r]);
                            if (take <= 0) continue;   // 有确定保留量时不再走保底（与烘焙一致：take>0 就不进保底分支）
                            totals[r] += take;
                            perType[r][layer] += take;
                        }
                    }
                }
            }

            EditorSceneManager.CloseScene(scene, true);
        }

        W($"总面积 = {totalArea / 1e6f:F1} km²");
        W("");
        W($"{"rate",8} {"实例总数",14} {"密度(株/m²)",12} {"相对 0.035",12}");
        long baseline = totals[0];
        for (int r = 0; r < CandidateRates.Length; r++)
        {
            float density = totalArea > 0 ? (float)totals[r] / totalArea : 0f;
            W($"{CandidateRates[r],8:F4} {totals[r],14:N0} {density,12:F3} " +
              $"{(baseline > 0 ? 100f * totals[r] / baseline : 0),11:F1}%");
        }

        W("");
        W("各 rate 的分层明细：");
        for (int r = 0; r < CandidateRates.Length; r++)
        {
            var sb2 = new StringBuilder();
            for (int t = 0; t < 7; t++)
            {
                if (perType[r][t] == 0) continue;
                if (sb2.Length > 0) sb2.Append(' ');
                sb2.Append($"T{t}={perType[r][t]:N0}");
            }
            W($"  {CandidateRates[r]:F4}: {sb2}");
        }

        Directory.CreateDirectory("Logs/Grass");
        File.WriteAllText(ResultPath, sb.ToString());
        if (Application.isBatchMode) EditorApplication.Exit(0);
    }

    private static int BakeSeed(string name)
    {
        unchecked
        {
            int h = 17;
            foreach (char c in name) h = h * 31 + c;
            return h;
        }
    }
}
