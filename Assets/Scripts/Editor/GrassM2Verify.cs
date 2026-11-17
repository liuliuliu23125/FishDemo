using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using FishDemo.Rendering;

namespace FishDemo.EditorTools
{
    public static class GrassM2Verify
    {
        private const string Tag = "[GrassM2]";
        private const string ResultPath = "Logs/Grass/m2-verify.txt";
        private const string ShaderPath = "Assets/Grasses/GrassBRG.shader";
        private const string GrassDataDir = "Assets/Scenes/World/GrassData";
        private const long TriangleBudget = 1_500_000;

        [MenuItem("FishDemo/Rendering/⑩ 验证 M2（烘焙数据链路）", false, 112)]
        public static void Run()
        {
            var sb = new StringBuilder();
            void W(string s) { Debug.Log($"{Tag} {s}"); sb.AppendLine(s); }

            string[] files = Directory.GetFiles(GrassDataDir, "*.bytes");
            if (files.Length == 0)
            {
                W($"{GrassDataDir} 下没有 .bytes，请先跑 GrassBaker.RunAll");
                Flush(sb); return;
            }
            string file = files[0];
            string chunkName = Path.GetFileNameWithoutExtension(file);
            W($"测试文件：{chunkName}");

            GrassChunkData data;
            try
            {
                data = GrassChunkData.Read(File.ReadAllBytes(file), chunkName);
            }
            catch (System.Exception e)
            {
                W($"读取失败：{e.Message}");
                Flush(sb); return;
            }

            var bytesInfo = new FileInfo(file);
            W($"实例数 = {data.Instances.Length:N0}，文件 {bytesInfo.Length / 1024f / 1024f:F2} MB");
            W($"包围盒 = {data.Bounds.min} ~ {data.Bounds.max}");
            double bytesPerInstance = (bytesInfo.Length - GrassInstance.HeaderSize) / (double)data.Instances.Length;
            W($"每实例字节 = {bytesPerInstance:F1}");

            for (int i = 0; i < Mathf.Min(3, data.Instances.Length); i++)
            {
                var s = data.Instances[i];
                W($"  [{i}] pos=({s.Position.x:F1},{s.Position.y:F1},{s.Position.z:F1}) " +
                  $"normal=({s.Normal.x:F2},{s.Normal.y:F2},{s.Normal.z:F2}) " +
                  $"type={s.Type} scale={s.Scale:F2} yaw={s.YawRadians:F2}");
            }

            bool sizeOk = data.Instances.Length > 0
                && Mathf.Abs((float)bytesPerInstance - GrassInstance.Stride) < 0.01f;
            W($"判定1 文件大小与 32B/实例一致：{(sizeOk ? "PASS" : "FAIL")}");

            var dist = AnalyzeDistribution(data, W);
            bool distOk = dist.HasVariance;
            W($"判定2 分布非规则网格（最近邻距离 CV={dist.Cv:F3}，需 > 0.15）：{(distOk ? "PASS" : "FAIL")}");

            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
            {
                W("NullGfxDevice：跳过渲染检查（SKIP）");
                W($"==== 结论：{(sizeOk && distOk ? "PASS（仅数据层）" : "FAIL")} ====");
                Flush(sb); return;
            }

            bool renderOk = CheckRender(data, chunkName, W, out long tris);
            bool budgetOk = tris >= 0 && tris <= TriangleBudget;
            W($"判定4 提交三角形 {tris:N0} ≤ 预算 {TriangleBudget:N0}：{(budgetOk ? "PASS" : "FAIL")}");

            bool pass = sizeOk && distOk && renderOk && budgetOk;
            W("");
            W($"==== 结论：{(pass ? "PASS" : "FAIL")} ====");
            Flush(sb);

            if (Application.isBatchMode)
                EditorApplication.Exit(pass ? 0 : 2);
        }

        private static Vector3 FindNearestGroundY(GrassChunkData data, Vector3 target)
        {
            float best = float.MaxValue;
            Vector3 result = target;
            foreach (var inst in data.Instances)
            {
                float dx = inst.Position.x - target.x;
                float dz = inst.Position.z - target.z;
                float d2 = dx * dx + dz * dz;
                if (d2 < best)
                {
                    best = d2;
                    result = new Vector3(inst.Position.x, inst.Position.y, inst.Position.z);
                }
            }
            return result;
        }

        private readonly struct DistStats
        {
            public readonly bool HasVariance;
            public readonly float Cv;
            public readonly float CoverageRatio;
            public DistStats(bool hasVariance, float cv, float coverageRatio)
            { HasVariance = hasVariance; Cv = cv; CoverageRatio = coverageRatio; }
        }

        private static DistStats AnalyzeDistribution(GrassChunkData data, System.Action<string> w)
        {
            const int samples = 400;
            const float cellSize = 8f;
            int n = data.Instances.Length;
            if (n < 2) return new DistStats(false, 0, 1);

            var rng = new System.Random(12345);
            float sum = 0, sumSq = 0;

            for (int s = 0; s < samples; s++)
            {
                int i = rng.Next(n);
                var a = data.Instances[i].Position;
                float best = float.MaxValue;
                for (int k = 0; k < 200; k++)
                {
                    int j = rng.Next(n);
                    if (j == i) continue;
                    var b = data.Instances[j].Position;
                    float dx = a.x - b.x, dz = a.z - b.z;
                    float d2 = dx * dx + dz * dz;
                    if (d2 < best) best = d2;
                }
                if (best == float.MaxValue) continue;
                float d = Mathf.Sqrt(best);
                sum += d; sumSq += d * d;
            }

            float mean = sum / samples;
            float variance = Mathf.Max(0, sumSq / samples - mean * mean);
            float cv = mean > 1e-4f ? Mathf.Sqrt(variance) / mean : 0f;

            var cells = new System.Collections.Generic.HashSet<long>();
            int minX = int.MaxValue, maxX = int.MinValue, minZ = int.MaxValue, maxZ = int.MinValue;
            foreach (var inst in data.Instances)
            {
                int cx = Mathf.FloorToInt(inst.Position.x / cellSize);
                int cz = Mathf.FloorToInt(inst.Position.z / cellSize);
                cells.Add(((long)cx << 32) ^ (uint)cz);
                if (cx < minX) minX = cx; if (cx > maxX) maxX = cx;
                if (cz < minZ) minZ = cz; if (cz > maxZ) maxZ = cz;
            }
            long totalCells = (long)(maxX - minX + 1) * (maxZ - minZ + 1);
            float coverage = totalCells > 0 ? cells.Count / (float)totalCells : 1f;

            w($"  最近邻均值 {mean:F3}m，CV={cv:F3}；8m 网格占用 {cells.Count}/{totalCells} = {coverage:P1}");
            return new DistStats(cv > 0.15f, cv, coverage);
        }

        private static bool CheckRender(GrassChunkData data, string chunkName,
            System.Action<string> w, out long tris)
        {
            tris = -1;

            var disabled = new System.Collections.Generic.List<GrassBRGManager>();
            foreach (var m in Object.FindObjectsOfType<GrassBRGManager>())
                if (m.isActiveAndEnabled) { m.enabled = false; disabled.Add(m); }

            var root = new GameObject("GrassM2_Verify");
            Camera cam = null;
            RenderTexture rt = null;
            var prevActive = RenderTexture.active;

            try
            {
                var shader = AssetDatabase.LoadAssetAtPath<Shader>(ShaderPath);
                if (shader == null) { w("找不到 shader"); return false; }
                var mat = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };

                var mgr = root.AddComponent<GrassBRGManager>();
                mgr.SetMaterialForValidation(mat);
                mgr.ConfigureLodForValidation(true, 100f, 25f, 8f);
                mgr.SetChunkData(data);
                if (!mgr.IsReady) { w("初始化失败"); return false; }

                var colorProp = mat.HasProperty("_BaseColor");
                if (colorProp) mat.SetColor("_BaseColor", new Color(1f, 0f, 1f, 1f));

                Vector3 center = data.Bounds.center;

                Vector3 groundAtCenter = FindNearestGroundY(data, center);
                w($"  中心附近地表高度 = {groundAtCenter.y:F1}m（包围盒中心 y={center.y:F1}）");

                var camGo = new GameObject("GrassM2_VerifyCam");
                cam = camGo.AddComponent<Camera>();
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = Color.black;
                cam.fieldOfView = 60f;
                cam.nearClipPlane = 0.3f;
                cam.farClipPlane = 3000f;
                cam.cullingMask = ~0;

                cam.transform.position = new Vector3(groundAtCenter.x, groundAtCenter.y + 1.7f, groundAtCenter.z);
                cam.transform.rotation = Quaternion.Euler(12f, 0f, 0f);

                const int size = 512;
                rt = new RenderTexture(size, size, 24, RenderTextureFormat.ARGB32);
                rt.Create();

                var request = new RenderPipeline.StandardRequest { destination = rt };
                if (!RenderPipeline.SupportsRenderRequest(cam, request))
                { w("不支持 StandardRequest"); return false; }
                RenderPipeline.SubmitRenderRequest(cam, request);

                RenderPipeline.SubmitRenderRequest(cam, request);

                RenderTexture.active = rt;
                var tex = new Texture2D(size, size, TextureFormat.RGBA32, false, true);
                tex.ReadPixels(new Rect(0, 0, size, size), 0, 0);
                tex.Apply();
                var pixels = tex.GetPixels32();
                RenderTexture.active = prevActive;

                int magenta = 0, nonBlack = 0;
                for (int i = 0; i < pixels.Length; i++)
                {
                    var p = pixels[i];
                    if (p.r > 8 || p.g > 8 || p.b > 8) nonBlack++;
                    if (p.r > 100 && p.b > 100 && p.g < 60) magenta++;
                }
                w($"  非背景像素 {nonBlack}/{pixels.Length} = {nonBlack / (float)pixels.Length:P2}");

                var png = tex.EncodeToPNG();
                Object.DestroyImmediate(tex);
                Directory.CreateDirectory("Logs/Grass");
                File.WriteAllBytes("Logs/Grass/m2-render.png", png);

                tris = mgr.LastSubmittedTriangles;
                w($"  洋红像素 {magenta}，可见实例 {mgr.LastVisibleCount}，提交三角形 {tris:N0}");

                if (colorProp) mat.SetColor("_BaseColor", new Color(0.25f, 0.55f, 0.15f, 1f));

                bool ok = magenta > 0;
                w($"判定3 渲染出草（洋红像素 > 0）：{(ok ? "PASS" : "FAIL")}（可见 {mgr.LastVisibleCount}）");


                return ok;
            }
            finally
            {
                RenderTexture.active = prevActive;
                if (rt != null) { rt.Release(); Object.DestroyImmediate(rt); }
                if (cam != null) Object.DestroyImmediate(cam.gameObject);
                Object.DestroyImmediate(root);
                foreach (var m in disabled) if (m != null) m.enabled = true;
            }
        }

        private static void Flush(StringBuilder sb)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ResultPath));
            File.WriteAllText(ResultPath, sb.ToString());
            if (Application.isBatchMode && !sb.ToString().Contains("结论"))
                EditorApplication.Exit(1);
        }
    }
}
