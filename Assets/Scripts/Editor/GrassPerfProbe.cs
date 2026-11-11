using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using FishDemo.Rendering;
using Debug = UnityEngine.Debug;

namespace FishDemo.EditorTools
{
    /// <summary>
    /// 多 chunk 性能探针：实测剔除耗时、提交三角形/顶点、内存占用。
    ///
    /// 为什么必须测：M2.1 修完密度后单 chunk 只有 15.7 万顶点，
    /// 但全量 36 chunk（1265 万实例）的剔除是 3800 万次视锥测试/帧，
    /// 移动端能否成立完全取决于这个数字。不能靠估算。
    ///
    /// 用法：-executeMethod FishDemo.EditorTools.GrassPerfProbe.Run
    /// </summary>
    public static class GrassPerfProbe
    {
        private const string Tag = "[GrassPerf]";
        private const string ResultPath = "Logs/Grass/perf-probe.txt";
        private const string ShaderPath = "Assets/Grasses/GrassBRG.shader";
        private const string GrassDataDir = "Assets/Scenes/World/GrassData";

        [MenuItem("FishDemo/Rendering/⑭ 多 chunk 性能探针", false, 116)]
        public static void Run()
        {
            var sb = new StringBuilder();
            void W(string s) { Debug.Log($"{Tag} {s}"); sb.AppendLine(s); }

            string[] files = Directory.GetFiles(GrassDataDir, "*.bytes");
            if (files.Length == 0) { W("无烘焙数据"); Flush(sb); return; }

            var chunks = new List<GrassChunkData>(files.Length);
            foreach (var f in files)
                chunks.Add(GrassChunkData.Read(File.ReadAllBytes(f), Path.GetFileNameWithoutExtension(f)));

            var shader = AssetDatabase.LoadAssetAtPath<Shader>(ShaderPath);
            if (shader == null) { W("找不到 shader"); Flush(sb); return; }

            foreach (var m in Object.FindObjectsOfType<GrassBRGManager>())
                m.enabled = false;

            var root = new GameObject("GrassPerf_Probe");
            Camera cam = null;
            RenderTexture rt = null;
            var prevActive = RenderTexture.active;

            try
            {
                long memBefore = System.GC.GetTotalMemory(false);

                var mat = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
                var mgr = root.AddComponent<GrassBRGManager>();
                mgr.SetMaterialForValidation(mat);
                mgr.SetChunkDataList(chunks);
                if (!mgr.IsReady) { W("初始化失败"); Flush(sb); return; }

                var tris = mgr.GetLodTriangleCounts();
                W($"chunk 数 = {chunks.Count}，总实例 = {mgr.InstanceCount:N0}");
                W($"LOD 三角形 = [{tris[0]}, {tris[1]}, {tris[2]}]");
                W($"进程托管内存增量 ≈ {(System.GC.GetTotalMemory(false) - memBefore) / 1024f / 1024f:F0} MB");
                W($"Native 内存估算：实例 {mgr.InstanceCount * 32 / 1024f / 1024f:F0} MB" +
                  $" + 位置 {mgr.InstanceCount * 12 / 1024f / 1024f:F0} MB" +
                  $" + GPU {mgr.InstanceCount * 32 / 1024f / 1024f:F0} MB");
                W("");

                const int size = 512;
                rt = new RenderTexture(size, size, 24, RenderTextureFormat.ARGB32);
                rt.Create();

                var camGo = new GameObject("GrassPerf_Cam");
                cam = camGo.AddComponent<Camera>();
                cam.fieldOfView = 60f;
                cam.nearClipPlane = 0.3f;
                cam.farClipPlane = 3000f;
                cam.cullingMask = ~0;

                // 站到世界中心附近的地表
                Vector3 center = new Vector3(0f, 0f, 0f);
                Vector3 ground = center;
                float best = float.MaxValue;
                foreach (var c in chunks)
                {
                    foreach (var inst in c.Instances)
                    {
                        float dx = inst.Position.x - center.x, dz = inst.Position.z - center.z;
                        float d2 = dx * dx + dz * dz;
                        if (d2 < best)
                        {
                            best = d2;
                            ground = new Vector3(inst.Position.x, inst.Position.y, inst.Position.z);
                        }
                    }
                }
                W($"探针位置（世界中心最近地表）= {ground}");
                W("");

                cam.transform.position = new Vector3(ground.x, ground.y + 1.7f, ground.z);
                cam.transform.rotation = Quaternion.Euler(12f, 0f, 0f);

                var request = new RenderPipeline.StandardRequest { destination = rt };

                // 预热（首次剔除 Job 可能未完成 + shader 变体首次编译）
                RenderPipeline.SubmitRenderRequest(cam, request);
                RenderPipeline.SubmitRenderRequest(cam, request);

                // 连续 10 帧计时
                const int frames = 10;
                var sw = Stopwatch.StartNew();
                for (int i = 0; i < frames; i++)
                    RenderPipeline.SubmitRenderRequest(cam, request);
                sw.Stop();

                double msPerFrame = sw.Elapsed.TotalMilliseconds / frames;

                int l0 = mgr.GetLastLodCount(0);
                int l1 = mgr.GetLastLodCount(1);
                int l2 = mgr.GetLastLodCount(2);
                long t = mgr.LastSubmittedTriangles;
                long verts = (long)l0 * tris[0] * 2 + (long)l1 * tris[1] * 2 + (long)l2 * tris[2] * 2;

                W("── 单帧实测（512px 离屏渲染，含剔除 Job 等待）──");
                W($"  可见实例 = {mgr.LastVisibleCount:N0}（LOD0={l0:N0}, LOD1={l1:N0}, LOD2={l2:N0}）");
                W($"  提交三角形 = {t:N0}");
                W($"  提交顶点 = {verts:N0}");
                W($"  单帧耗时（含渲染+剔除同步）= {msPerFrame:F2} ms");
                W("");
                W("注：这是编辑器 + 512px 离屏渲染的数字，不是真机 1080p。");
                W("剔除 Job 是单线程 Burst，1265 万实例 × 3 遍扫描是主要成本。");
                W("M3 必须：统计并入主扫描（去掉第 4 遍）+ IJobParallelFor 并行化。");

                Flush(sb);
                if (Application.isBatchMode) EditorApplication.Exit(0);
            }
            finally
            {
                RenderTexture.active = prevActive;
                if (rt != null) { rt.Release(); Object.DestroyImmediate(rt); }
                if (cam != null) Object.DestroyImmediate(cam.gameObject);
                Object.DestroyImmediate(root);
                foreach (var m in Object.FindObjectsOfType<GrassBRGManager>()) m.enabled = true;
            }
        }

        private static void Flush(StringBuilder sb)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ResultPath));
            File.WriteAllText(ResultPath, sb.ToString());
        }
    }
}
