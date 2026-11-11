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
    /// <summary>
    /// 把 Terrain 的 detail 数据烘焙成每 chunk 一个 .bytes。
    ///
    /// ── 为什么这样采样（而不是像 M1 那样铺方阵）──
    /// TerrainData.GetDetailLayer 返回的每一格是该 detail 层的**实例数**，
    /// 它已经包含了美术撒点的疏密信息（林下密、岩石上为 0）。
    /// 逐格读取并按格内随机抖动散布，草就自然长在「地形真的有草」的地方，
    /// 密度也跟随地形变化——这正是 M1 硬编码方阵做不到的。
    ///
    /// ── 采样规则（方案 §4.0）──
    ///   n' = round(n × SamplingRate)
    ///   若 n > 0 但 n' == 0，按 SparseKeepChance 概率保底放 1 个（避免空洞）
    ///   位置在格内均匀随机抖动（消除网格感）
    ///   过滤：坡度过大、低于海平面的位置丢弃
    ///
    /// ── 坡度判定为什么不用 TerrainData.GetSteepness ──
    /// 官方文档只说「gradient of the terrain at point (x,y)，always positive」，
    /// 没说单位（米/米 还是 度）。方案 §4.0 写的 0.707f = cos45° 也在暗示正弦值，
    /// 但语义未经证实。这里改用高度图相邻采样自己算高差，单位明确是「米/米」，
    /// 与阈值 tan(45°)=1.0 直接可比，不依赖未证实的语义。
    ///
    /// 用法：
    ///   Unity.exe -batchmode -projectPath <proj> \
    ///     -executeMethod FishDemo.EditorTools.GrassBaker.RunAll
    ///   -grassChunk "chunk 场景路径" 只烘一个（A/B 对比用）
    /// 输出：Assets/Scenes/World/GrassData/&lt;chunk 名&gt;.bytes
    /// </summary>
    public static class GrassBaker
    {
        private const string Tag = "[GrassBaker]";
        private const string WorldScenePath = "Assets/Scenes/World.unity";
        private const string OutputDir = "Assets/Scenes/World/GrassData";
        private const string ReportPath = "Logs/Grass/bake-report.txt";

        /// <summary>
        /// 采样率。
        ///
        /// 0.35 是 §4.1 按「与当前 Terrain 渲染档可见密度对齐」算出的值，
        /// 但实测下来视觉过密：0.35 → 8.21 株/m²，而 Terrain 当前档可见密度约
        /// 0.14 株/m²（577万 × 0.35 × 距离占比），高了近 58 倍。
        /// 用户实测相机内 1000 万顶点，确认过密。
        ///
        /// 0.035 → 0.82 株/m²，配合「每实例一簇草」（48 顶点，见 BuildClumpMesh）
        /// 视觉上仍饱满，但顶点数降一个数量级。
        /// </summary>
        private const float SamplingRate = 0.035f;

        /// <summary>稀疏处保底放 1 个的概率，避免整片空洞。</summary>
        private const float SparseKeepChance = 0.30f;

        /// <summary>坡度上限：tan(45°) = 1.0（高差/水平距离）。</summary>
        private const float MaxSlopeTan = 1.0f;

        /// <summary>最低高度 = 海平面 + 2m。海平面取 60（WorldPipeline.cs:42 / BiomeSpawner.cs:33）。</summary>
        private const float MinHeight = 60f + 2f;

        /// <summary>高度图局部梯度采样步长（归一化 UV）。1/1024 ≈ 0.5m，够用。</summary>
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

                // 一个 chunk 场景一个 Terrain。若未来一块多个，这里需要合并成同一个 batch 的多段，
                // 但 BRG 的 batch 边界由 buffer 决定，不影响正确性——记录数量变化即可。
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

        /// <summary>单个 Terrain 的烘焙结果（尚未序列化）。</summary>
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

            // detail 格在世界里的边长。detail 分辨率是按地形归一化铺满的。
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

                        // 按比例缩减；稀疏处按概率保底
                        int take = Mathf.RoundToInt(n * SamplingRate);
                        if (take == 0)
                        {
                            if (rng.NextDouble() >= SparseKeepChance)
                                continue;
                            take = 1;
                        }

                        // 同一格内取 take 个不重叠的随机位置
                        for (int k = 0; k < take; k++)
                        {
                            candidates++;

                            // 格内抖动：格坐标 → 世界坐标
                            float u = (x + (float)rng.NextDouble()) / dw;
                            float v = (y + (float)rng.NextDouble()) / dh;
                            float wx = terrainPos.x + u * terrainSize.x;
                            float wz = terrainPos.z + v * terrainSize.z;

                            float wy = terrainPos.y + td.GetInterpolatedHeight(u, v);

                            // 高度过滤：低于海平面 + 2m 的丢弃（水下/滩涂不长草）
                            if (wy < MinHeight)
                            {
                                rejectedLow++;
                                continue;
                            }

                            // 坡度过滤：中心差分算高差（米），与水平间距（米）之比即 tan
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

                            // 地表法线：由两个方向的高差得到
                            float nx = -(hR - hL) / Mathf.Max(1e-4f, dxMeters);
                            float nz = -(hU - hD) / Mathf.Max(1e-4f, dzMeters);
                            var normal = new Vector3(nx, 1f, nz).normalized;

                            var inst = new GrassInstance
                            {
                                Position = new Unity.Mathematics.float3(wx, wy, wz),
                                Normal = new Unity.Mathematics.float3(normal.x, normal.y, normal.z),
                                Color = 0xFFFFFFFFu, // 白 tint：不额外染色，颜色来自材质
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

            // 算包围盒（供 BRG.SetGlobalBounds 与运行时剔除用）
            var min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            var max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
            foreach (var inst in instances)
            {
                var p = new Vector3(inst.Position.x, inst.Position.y, inst.Position.z);
                min = Vector3.Min(min, p);
                max = Vector3.Max(max, p);
            }
            // 竖直方向留出草高，避免早期剔除
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

        /// <summary>
        /// 序列化成二进制。格式见 GrassInstance 的常量定义。
        /// 手写而不是 BinaryWriter：需要与运行时 GrassChunkData.Read 的字段顺序严格对应，
        /// 集中在一处更容易对照。
        /// </summary>
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

        /// <summary>
        /// 由 chunk 名生成确定性种子：同一 chunk 重烘结果一致，便于比对。
        /// 用字符串哈希而不是 Random.Range，避免烘焙顺序影响结果。
        /// </summary>
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
