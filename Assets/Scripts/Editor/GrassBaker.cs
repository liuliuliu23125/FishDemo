using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using FishDemo.Rendering;

namespace FishDemo.EditorTools
{
    public static class GrassBaker
    {
        private const string Tag = "[GrassBaker]";
        private const string WorldScenePath = "Assets/Scenes/World.unity";
        private const string OutputDir = "Assets/Scenes/World/GrassData";
        private const string ReportPath = "Logs/Grass/bake-report.txt";

        private const float SamplingRate = 0.0175f;

        private const float SparseKeepChance = 0.30f;

        private const float MaxSlopeTan = 1.0f;

        private const float MinHeight = 60f + 2f;

        private const float SlopeSampleStep = 1f / 1024f;

        [MenuItem("FishDemo/Rendering/⑧ 烘焙草数据（全部 chunk）", false, 110)]
        public static void RunAll()
        {
            var sb = new StringBuilder();
            void W(string s) { Debug.Log($"{Tag} {s}"); sb.AppendLine(s); }

            Directory.CreateDirectory(OutputDir);
            EditorSceneManager.OpenScene(WorldScenePath, OpenSceneMode.Single);

            var chunkPaths = new List<string>();
            foreach (var s in EditorBuildSettings.scenes)
            {
                if (!s.enabled) continue;
                if (s.path.EndsWith("World.unity")) continue;
                if (File.Exists(s.path)) chunkPaths.Add(s.path);
            }

            W($"待烘焙 chunk = {chunkPaths.Count}");
            W($"采样率 = {SamplingRate}，坡度上限 tan = {MaxSlopeTan}，最低高度 = {MinHeight}");
            W("");

            long totalInstances = 0;
            long totalBytes = 0;
            int ok = 0, skipped = 0;

            foreach (var path in chunkPaths)
            {
                var scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Additive);
                var terrains = new List<Terrain>();
                foreach (var root in scene.GetRootGameObjects())
                    terrains.AddRange(root.GetComponentsInChildren<Terrain>());

                if (terrains.Count == 0)
                {
                    W($"跳过（无 Terrain）：{Path.GetFileNameWithoutExtension(path)}");
                    skipped++;
                    EditorSceneManager.CloseScene(scene, true);
                    continue;
                }

                var terrain = terrains[0];
                var result = BakeTerrain(terrain, W);
                if (result == null)
                {
                    skipped++;
                    EditorSceneManager.CloseScene(scene, true);
                    continue;
                }

                string chunkName = Path.GetFileNameWithoutExtension(path);
                string outPath = Path.Combine(OutputDir, chunkName + ".bytes");
                byte[] bytes = Serialize(result, chunkName);
                File.WriteAllBytes(outPath, bytes);

                totalInstances += result.Count;
                totalBytes += bytes.Length;
                ok++;
                W($"  {chunkName}: {result.Count:N0} 实例, {bytes.Length / 1024f / 1024f:F2} MB");

                EditorSceneManager.CloseScene(scene, true);
            }

            AssetDatabase.Refresh();

            W("");
            W($"成功 = {ok}，跳过 = {skipped}");
            W($"实例总数 = {totalInstances:N0}");
            W($"磁盘总量 = {totalBytes / 1024f / 1024f:F1} MB");
            W($"单 chunk 均量 = {(ok > 0 ? totalInstances / ok : 0):N0}");

            Directory.CreateDirectory(Path.GetDirectoryName(ReportPath));
            File.WriteAllText(ReportPath, sb.ToString());

            if (Application.isBatchMode)
                EditorApplication.Exit(ok > 0 ? 0 : 2);
        }

        private class BakeResult
        {
            public GrassInstance[] Instances;
            public int Count => Instances.Length;
            public Vector3 Min, Max;
        }

        private static BakeResult BakeTerrain(Terrain terrain, Action<string> w)
        {
            var td = terrain.terrainData;
            if (td == null)
            {
                w("  terrainData 为空，跳过");
                return null;
            }

            var protos = td.detailPrototypes;
            if (protos == null || protos.Length == 0)
            {
                w("  无 detailPrototypes，跳过");
                return null;
            }

            Vector3 terrainPos = terrain.transform.position;
            Vector3 terrainSize = td.size;
            int dw = td.detailWidth;
            int dh = td.detailHeight;

            float cellW = terrainSize.x / dw;
            float cellH = terrainSize.z / dh;

            var instances = new List<GrassInstance>(200000);
            var rng = new System.Random(BakeSeed(terrain.name));

            var layerCounts = new long[protos.Length];
            long rejectedSteep = 0, rejectedLow = 0;
            long candidates = 0;

            for (int layer = 0; layer < protos.Length; layer++)
            {
                int[,] detail;
                try
                {
                    detail = td.GetDetailLayer(0, 0, dw, dh, layer);
                }
                catch (Exception e)
                {
                    w($"  层 {layer} 读取失败（{e.GetType().Name}），跳过");
                    continue;
                }

                for (int y = 0; y < dh; y++)
                {
                    for (int x = 0; x < dw; x++)
                    {
                        int n = detail[y, x];
                        if (n <= 0) continue;

                        int take = Mathf.RoundToInt(n * SamplingRate);
                        if (take == 0)
                        {
                            if (rng.NextDouble() >= SparseKeepChance)
                                continue;
                            take = 1;
                        }

                        for (int k = 0; k < take; k++)
                        {
                            candidates++;

                            float u = (x + (float)rng.NextDouble()) / dw;
                            float v = (y + (float)rng.NextDouble()) / dh;
                            float wx = terrainPos.x + u * terrainSize.x;
                            float wz = terrainPos.z + v * terrainSize.z;

                            float wy = terrainPos.y + td.GetInterpolatedHeight(u, v);

                            if (wy < MinHeight)
                            {
                                rejectedLow++;
                                continue;
                            }

                            float hL = td.GetInterpolatedHeight(Mathf.Clamp01(u - SlopeSampleStep), v);
                            float hR = td.GetInterpolatedHeight(Mathf.Clamp01(u + SlopeSampleStep), v);
                            float hD = td.GetInterpolatedHeight(u, Mathf.Clamp01(v - SlopeSampleStep));
                            float hU = td.GetInterpolatedHeight(u, Mathf.Clamp01(v + SlopeSampleStep));

                            float dxMeters = 2f * SlopeSampleStep * terrainSize.x;
                            float dzMeters = 2f * SlopeSampleStep * terrainSize.z;
                            float slopeX = Mathf.Abs(hR - hL) / Mathf.Max(1e-4f, dxMeters);
                            float slopeZ = Mathf.Abs(hU - hD) / Mathf.Max(1e-4f, dzMeters);
                            float slopeTan = Mathf.Sqrt(slopeX * slopeX + slopeZ * slopeZ);

                            if (slopeTan > MaxSlopeTan)
                            {
                                rejectedSteep++;
                                continue;
                            }

                            float nx = -(hR - hL) / Mathf.Max(1e-4f, dxMeters);
                            float nz = -(hU - hD) / Mathf.Max(1e-4f, dzMeters);
                            var normal = new Vector3(nx, 1f, nz).normalized;

                            var inst = new GrassInstance
                            {
                                Position = new Unity.Mathematics.float3(wx, wy, wz),
                                Normal = new Unity.Mathematics.float3(normal.x, normal.y, normal.z),
                                Color = 0xFFFFFFFFu,
                            };
                            inst.Type = layer;
                            inst.ScaleQ = (byte)rng.Next(0, 256);
                            inst.YawQ = (ushort)rng.Next(0, 65536);

                            instances.Add(inst);
                            layerCounts[layer]++;
                        }
                    }
                }
            }

            if (instances.Count == 0)
            {
                w("  采样结果为空，跳过");
                return null;
            }

            var min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            var max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
            foreach (var inst in instances)
            {
                var p = new Vector3(inst.Position.x, inst.Position.y, inst.Position.z);
                min = Vector3.Min(min, p);
                max = Vector3.Max(max, p);
            }
            min.y -= 1f;
            max.y += 2f;

            w($"  候选 {candidates:N0} → 保留 {instances.Count:N0} " +
              $"（坡度剔除 {rejectedSteep:N0}，高度剔除 {rejectedLow:N0}）");
            var perLayer = new StringBuilder();
            for (int i = 0; i < layerCounts.Length; i++)
            {
                if (layerCounts[i] == 0) continue;
                if (perLayer.Length > 0) perLayer.Append(", ");
                perLayer.Append($"L{i}={layerCounts[i]:N0}");
            }
            w($"  分层：{perLayer}");

            return new BakeResult { Instances = instances.ToArray(), Min = min, Max = max };
        }

        private static byte[] Serialize(BakeResult r, string chunkName)
        {
            var bytes = new byte[GrassInstance.FileSizeFor(r.Count)];
            int o = 0;

            void U32(uint v) { bytes[o++] = (byte)v; bytes[o++] = (byte)(v >> 8); bytes[o++] = (byte)(v >> 16); bytes[o++] = (byte)(v >> 24); }
            void F32(float v) { var b = BitConverter.GetBytes(v); bytes[o++] = b[0]; bytes[o++] = b[1]; bytes[o++] = b[2]; bytes[o++] = b[3]; }
            void V3(Vector3 v) { F32(v.x); F32(v.y); F32(v.z); }

            U32(GrassInstance.Magic);
            U32(GrassInstance.Version);
            U32((uint)r.Count);
            V3(r.Min);
            V3(r.Max);

            foreach (var inst in r.Instances)
            {
                F32(inst.Position.x); F32(inst.Position.y); F32(inst.Position.z);
                U32(inst.Packed);
                F32(inst.Normal.x); F32(inst.Normal.y); F32(inst.Normal.z);
                U32(inst.Color);
            }

            return bytes;
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
}
