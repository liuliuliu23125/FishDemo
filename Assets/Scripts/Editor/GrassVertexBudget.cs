using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using FishDemo.Rendering;

namespace FishDemo.EditorTools
{
    /// <summary>
    /// 顶点预算实测：用真实规格相机（1080p、第一人称）统计一帧提交的顶点数。
    ///
    /// 为什么单独做：其他验证脚本用离屏 RT + 简化相机，口径与「编辑器 Game 视图里看到的」
    /// 不一致，容易得出与用户观感不符的数字。本脚本用 1080p、60° FOV、
    /// 站姿高度 1.7m、俯角 0~30° 的几组视角，给出可直接对标预算的顶点数。
    ///
    /// 顶点数 = Σ(各 LOD 层可见实例 × 该层顶点数)。引擎实际渲染的顶点还受
    /// 屏幕外裁剪等影响，这里算的是「提交量」，是上界但接近真实。
    ///
    /// 用法：-executeMethod FishDemo.EditorTools.GrassVertexBudget.Run
    /// </summary>
    public static class GrassVertexBudget
    {
        private const string Tag = "[GrassVtx]";
        private const string ResultPath = "Logs/Grass/vertex-budget.txt";
        private const string ShaderPath = "Assets/Grasses/GrassBRG.shader";
        private const string GrassDataDir = "Assets/Scenes/World/GrassData";

        [MenuItem("FishDemo/Rendering/⑫ 实测顶点预算", false, 114)]
        public static void Run()
        {
            var sb = new StringBuilder();
            void W(string s) { Debug.Log($"{Tag} {s}"); sb.AppendLine(s); }

            string[] files = Directory.GetFiles(GrassDataDir, "*.bytes");
            if (files.Length == 0) { W("无烘焙数据"); Flush(sb); return; }
            var data = GrassChunkData.Read(File.ReadAllBytes(files[0]),
                Path.GetFileNameWithoutExtension(files[0]));

            var shader = AssetDatabase.LoadAssetAtPath<Shader>(ShaderPath);
            if (shader == null) { W("找不到 shader"); Flush(sb); return; }

            // 禁用场景里已有的草节点，避免双重统计
            foreach (var m in Object.FindObjectsOfType<GrassBRGManager>())
                m.enabled = false;

            var root = new GameObject("GrassVtx_Probe");
            Camera cam = null;
            RenderTexture rt = null;
            var prevActive = RenderTexture.active;

            try
            {
                var mat = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
                var mgr = root.AddComponent<GrassBRGManager>();
                mgr.SetMaterialForValidation(mat);
                mgr.ConfigureLodForValidation(true, 100f, 25f, 8f);
                mgr.SetChunkData(data);
                if (!mgr.IsReady) { W("初始化失败"); Flush(sb); return; }

                var tris = mgr.GetLodTriangleCounts();
                W($"数据：{data.ChunkName}");
                W($"实例 {data.Instances.Length:N0}，密度 {data.Instances.Length / (511.5f * 511.5f):F2} 株/m²");
                W($"LOD 三角形数 = [{tris[0]}, {tris[1]}, {tris[2]}]，顶点 = [{tris[0] * 2}, {tris[1] * 2}, {tris[2] * 2}]");
                W("");

                // 真实规格相机：1080p（影响像素阈值）、60° FOV、站姿 1.7m
                const int w = 1920, h = 1080;
                rt = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32);
                rt.Create();

                var camGo = new GameObject("GrassVtx_Cam");
                cam = camGo.AddComponent<Camera>();
                cam.fieldOfView = 60f;
                cam.nearClipPlane = 0.1f;
                cam.farClipPlane = 2000f;
                cam.cullingMask = ~0;

                // 找地表点（用离包围盒中心最近的实例）
                Vector3 center = data.Bounds.center;
                Vector3 ground = center;
                float best = float.MaxValue;
                foreach (var inst in data.Instances)
                {
                    float dx = inst.Position.x - center.x, dz = inst.Position.z - center.z;
                    float d2 = dx * dx + dz * dz;
                    if (d2 < best) { best = d2; ground = new Vector3(inst.Position.x, inst.Position.y, inst.Position.z); }
                }

                W($"{"视角",-16} {"LOD0",7} {"LOD1",7} {"LOD2",7} {"可见",8} {"三角形",10} {"顶点",10}");
                long maxVerts = 0;

                foreach (var (label, pitch, yaw) in new[]
                {
                    ("平视 0°", 0f, 0f),
                    ("俯视 15°", 15f, 0f),
                    ("俯视 30°", 30f, 0f),
                    ("环视 90°", 5f, 90f),
                    ("环视 180°", 5f, 180f),
                    ("环视 270°", 5f, 270f),
                })
                {
                    cam.transform.position = new Vector3(ground.x, ground.y + 1.7f, ground.z);
                    cam.transform.rotation = Quaternion.Euler(pitch, yaw, 0f);

                    var request = new UnityEngine.Rendering.RenderPipeline.StandardRequest { destination = rt };
                    if (!UnityEngine.Rendering.RenderPipeline.SupportsRenderRequest(cam, request))
                    { W("不支持 StandardRequest"); break; }
                    // 渲两次：首次剔除 Job 可能未完成（M2 踩过的坑）
                    UnityEngine.Rendering.RenderPipeline.SubmitRenderRequest(cam, request);
                    UnityEngine.Rendering.RenderPipeline.SubmitRenderRequest(cam, request);

                    int l0 = mgr.GetLastLodCount(0);
                    int l1 = mgr.GetLastLodCount(1);
                    int l2 = mgr.GetLastLodCount(2);
                    int vis = mgr.LastVisibleCount;
                    long t = mgr.LastSubmittedTriangles;
                    long v = (long)l0 * tris[0] * 2 + (long)l1 * tris[1] * 2 + (long)l2 * tris[2] * 2;
                    if (v > maxVerts) maxVerts = v;

                    W($"{label,-16} {l0,7:N0} {l1,7:N0} {l2,7:N0} {vis,8:N0} {t,10:N0} {v,10:N0}");
                }

                W("");
                W($"最大顶点数（6 个方向中最高的一个）= {maxVerts:N0}");
                W($"按 4 个水平方向合计（360° 环绕）= 约 {maxVerts * 2:N0}");

                // 判定：单帧提交顶点应 << 1M（参考 performance_budget 的 1.5M 三角形）
                bool ok = maxVerts < 1_000_000;
                W($"判定 单帧顶点 < 100 万：{(ok ? "PASS" : "FAIL")}");
                W($"==== 结论：{(ok ? "PASS" : "FAIL")} ====");

                // 先落盘再退出：EditorApplication.Exit 会终止进程，
                // 放在 try 里会导致 finally 的 Flush 来不及执行（实测报告文件为空）。
                Flush(sb);
                if (Application.isBatchMode)
                    EditorApplication.Exit(ok ? 0 : 2);
            }
            finally
            {
                RenderTexture.active = prevActive;
                if (rt != null) { rt.Release(); Object.DestroyImmediate(rt); }
                if (cam != null) Object.DestroyImmediate(cam.gameObject);
                Object.DestroyImmediate(root);
                Flush(sb);
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
