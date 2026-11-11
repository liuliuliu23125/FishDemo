using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace FishDemo.EditorTools
{
    /// <summary>
    /// 只读诊断：核查地形高度分布，验证烘焙时的高度/坡度过滤是否该生效。
    ///
    /// 为什么要查：烘焙报告显示「高度剔除 0」——如果地形确实都在海平面以上，
    /// 那是正常的；如果过滤逻辑没生效，就是 bug。必须用数据判断，不能猜。
    ///
    /// 用法：-executeMethod FishDemo.EditorTools.GrassBakeDiagnose.Run
    /// </summary>
    public static class GrassBakeDiagnose
    {
        private const string Tag = "[BakeDiag]";
        private const string ResultPath = "Logs/Grass/bake-diagnose.txt";
        private const string WorldScenePath = "Assets/Scenes/World.unity";

        [MenuItem("FishDemo/Rendering/⑨ 诊断烘焙过滤条件", false, 111)]
        public static void Run()
        {
            var sb = new StringBuilder();
            void W(string s) { Debug.Log($"{Tag} {s}"); sb.AppendLine(s); }

            EditorSceneManager.OpenScene(WorldScenePath, OpenSceneMode.Single);

            string chunkPath = null;
            foreach (var s in EditorBuildSettings.scenes)
            {
                if (s.enabled && !s.path.EndsWith("World.unity") && File.Exists(s.path))
                { chunkPath = s.path; break; }
            }
            if (chunkPath == null) { W("找不到 chunk"); Flush(sb); return; }

            var scene = EditorSceneManager.OpenScene(chunkPath, OpenSceneMode.Additive);
            W($"样本 chunk = {Path.GetFileNameWithoutExtension(chunkPath)}");

            var terrain = Object.FindObjectOfType<Terrain>();
            var td = terrain.terrainData;
            Vector3 tp = terrain.transform.position;
            Vector3 ts = td.size;

            W($"Terrain pos = {tp}, size = {ts}");
            W($"detail 分辨率 = {td.detailWidth}x{td.detailHeight}");
            W($"heightmap 分辨率 = {td.heightmapResolution}");
            W("");

            // 采样高度分布（每 8 格取一次，共 64x64 点）
            W("高度分布采样（世界坐标 Y）：");
            float minH = float.MaxValue, maxH = float.MinValue;
            long below62 = 0, total = 0;
            float[] buckets = new float[8]; // 0-100,100-200,...700
            int res = td.heightmapResolution;
            int step = Mathf.Max(1, res / 64);
            for (int y = 0; y < res; y += step)
            {
                for (int x = 0; x < res; x += step)
                {
                    float u = x / (float)(res - 1);
                    float v = y / (float)(res - 1);
                    float h = tp.y + td.GetInterpolatedHeight(u, v);
                    minH = Mathf.Min(minH, h);
                    maxH = Mathf.Max(maxH, h);
                    total++;
                    if (h < 62f) below62++;
                    int b = Mathf.Clamp((int)(h / 100f), 0, 7);
                    buckets[b]++;
                }
            }
            W($"  采样点 = {total}, 高度范围 = [{minH:F1}, {maxH:F1}]");
            W($"  低于 62m 的点 = {below62} ({below62 * 100f / total:F2}%)");
            for (int i = 0; i < 8; i++)
                if (buckets[i] > 0)
                    W($"    {i * 100,3}-{(i + 1) * 100,3}m: {buckets[i],6} ({buckets[i] * 100f / total:F1}%)");

            W("");
            W("坡度分布采样（tan 值，用中心差分算）：");
            const float st = 1f / 1024f;
            float maxSlope = 0;
            long steep = 0, slopeTotal = 0;
            for (int y = 0; y < res; y += step)
            {
                for (int x = 0; x < res; x += step)
                {
                    float u = x / (float)(res - 1);
                    float v = y / (float)(res - 1);
                    float hL = td.GetInterpolatedHeight(Mathf.Clamp01(u - st), v);
                    float hR = td.GetInterpolatedHeight(Mathf.Clamp01(u + st), v);
                    float hD = td.GetInterpolatedHeight(u, Mathf.Clamp01(v - st));
                    float hU = td.GetInterpolatedHeight(u, Mathf.Clamp01(v + st));
                    float dxM = 2f * st * ts.x;
                    float dzM = 2f * st * ts.z;
                    float sx = Mathf.Abs(hR - hL) / Mathf.Max(1e-4f, dxM);
                    float sz = Mathf.Abs(hU - hD) / Mathf.Max(1e-4f, dzM);
                    float tan = Mathf.Sqrt(sx * sx + sz * sz);
                    maxSlope = Mathf.Max(maxSlope, tan);
                    if (tan > 1f) steep++;
                    slopeTotal++;
                }
            }
            W($"  最大 tan = {maxSlope:F3}（对应 {Mathf.Atan(maxSlope) * Mathf.Rad2Deg:F1}°）");
            W($"  tan > 1.0（45°）的点 = {steep} ({steep * 100f / slopeTotal:F2}%)");

            W("");
            W("detail 层实例数（用于核对烘焙数量）：");
            long sumAll = 0;
            for (int l = 0; l < td.detailPrototypes.Length; l++)
            {
                int[,] layer;
                try { layer = td.GetDetailLayer(0, 0, td.detailWidth, td.detailHeight, l); }
                catch { W($"  层 {l}: 读取失败"); continue; }
                long sum = 0;
                foreach (int v in layer) if (v > 0) sum += v;
                sumAll += sum;
                W($"  层 {l}: {sum:N0}");
            }
            W($"  合计 = {sumAll:N0}");
            W($"  × 0.35 = {sumAll * 0.35f:N0}");

            Flush(sb);
        }

        private static void Flush(StringBuilder sb)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ResultPath));
            File.WriteAllText(ResultPath, sb.ToString());
            if (Application.isBatchMode) EditorApplication.Exit(0);
        }
    }
}
