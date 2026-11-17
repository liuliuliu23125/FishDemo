using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.Rendering;
using UnityEngine;
using UnityEngine.Rendering;
using FishDemo.Rendering;

namespace FishDemo.EditorTools
{
    public static class GrassM1Verify
    {
        private const string Tag = "[GrassM1]";
        private const string ShaderPath = "Assets/Grasses/GrassBRG.shader";
        private const string ResultPath = "Logs/Grass/m1-verify.txt";

        [MenuItem("FishDemo/Rendering/① 验证 BRG 草链路（M1）", false, 100)]
        public static void Run()
        {
            var sb = new StringBuilder();
            void W(string s) { Debug.Log($"{Tag} {s}"); sb.AppendLine(s); }

            bool shaderOk = CheckShaderCompile(W);
            bool deviceOk = CheckDevice(W);
            bool renderOk = false;

            if (deviceOk)
                renderOk = CheckEndToEndRender(W);
            else
                W("无真实图形设备，端到端渲染检查跳过（SKIP，不代表失败）。");

            W("");
            W($"shader 编译 = {(shaderOk ? "PASS" : "FAIL")}，"
              + $"设备可用 = {(deviceOk ? "PASS" : "SKIP")}，"
              + $"端到端渲染 = {(renderOk ? "PASS" : (deviceOk ? "FAIL" : "SKIP"))}");

            bool overall = shaderOk && (!deviceOk || renderOk);
            W($"==== 结论：{(overall ? "PASS" : "FAIL")}"
              + (deviceOk ? "" : "（仅 shader 层通过，端到端未验证）") + " ====");

            Directory.CreateDirectory(Path.GetDirectoryName(ResultPath));
            File.WriteAllText(ResultPath, sb.ToString());

            if (Application.isBatchMode)
                EditorApplication.Exit(overall ? 0 : 2);
        }

        private static bool CheckShaderCompile(System.Action<string> w)
        {
            var shader = AssetDatabase.LoadAssetAtPath<Shader>(ShaderPath);
            if (shader == null)
            {
                w($"找不到 shader：{ShaderPath}");
                return false;
            }

            if (ShaderUtil.ShaderHasError(shader))
            {
                w("ShaderHasError = true");
                DumpMessages(shader, w);
                return false;
            }

            var mat = new Material(shader);
            try
            {
                mat.EnableKeyword("DOTS_INSTANCING_ON");
                ShaderUtil.CompilePass(mat, 0, true);
            }
            finally
            {
                Object.DestroyImmediate(mat);
            }

            var msgs = ShaderUtil.GetShaderMessages(shader);
            if (msgs != null && msgs.Length > 0)
            {
                w($"DOTS 变体编译产生 {msgs.Length} 条消息：");
                foreach (var m in msgs)
                {
                    w($"  [{m.severity}] {m.platform} {m.file}:{m.line} {m.message} {m.messageDetails}");
                }
                foreach (var m in msgs)
                    if (m.severity == ShaderCompilerMessageSeverity.Error)
                        return false;
            }

            w("Shader 编译通过（含 DOTS_INSTANCING_ON 变体）");
            return true;
        }

        private static void DumpMessages(Shader shader, System.Action<string> w)
        {
            foreach (var m in ShaderUtil.GetShaderMessages(shader))
                w($"  [{m.severity}] {m.platform} {m.file}:{m.line} {m.message} {m.messageDetails}");
        }

        private static bool CheckDevice(System.Action<string> w)
        {
            var device = SystemInfo.graphicsDeviceType;
            w($"GraphicsDevice = {device}，supportsComputeShaders = {SystemInfo.supportsComputeShaders}");

            if (device == GraphicsDeviceType.Null)
            {
                w("NullGfxDevice：BRG 需要真实设备。");
                return false;
            }

            BatchBufferTarget target = BatchRendererGroup.BufferTarget;
            w($"BatchRendererGroup.BufferTarget = {target}");
            if (target != BatchBufferTarget.RawBuffer &&
                target != BatchBufferTarget.ConstantBuffer)
            {
                w("当前图形 API 不提供 BRG 可用的 buffer target，BRG 无法工作。");
                return false;
            }

            return true;
        }

        private static bool CheckEndToEndRender(System.Action<string> w)
        {
            const int size = 256;

            var preexisting = Object.FindObjectsOfType<GrassBRGManager>();
            var disabled = new System.Collections.Generic.List<GrassBRGManager>();
            foreach (var m in preexisting)
            {
                if (m.isActiveAndEnabled)
                {
                    m.enabled = false;
                    disabled.Add(m);
                }
            }
            if (disabled.Count > 0)
                w($"已临时禁用场景内 {disabled.Count} 个已有 GrassBRGManager 节点");

            var root = new GameObject("GrassBRG_M1Verify");
            Camera cam = null;
            RenderTexture rt = null;
            RenderTexture prevActive = RenderTexture.active;

            try
            {
                var shader = AssetDatabase.LoadAssetAtPath<Shader>(ShaderPath);
                if (shader == null)
                {
                    w("端到端：找不到 shader。");
                    return false;
                }

                var mat = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };

                var mgr = root.AddComponent<GrassBRGManager>();
                mgr.ConfigureForValidation(1000, 1.5f, 1.0f);
                mgr.ConfigureLodForValidation(false, 0f, 0f, 0f);
                mgr.SetMaterialForValidation(mat);
                root.transform.position = Vector3.zero;
                mgr.Initialize();

                if (!mgr.IsReady)
                {
                    w("端到端：GrassBRGManager 初始化失败（IsReady=false）。");
                    return false;
                }

                var _lodTris = mgr.GetLodTriangleCounts();
                w($"LOD 三角形数 = [{_lodTris[0]}, {_lodTris[1]}, {_lodTris[2]}]");

                int n = mgr.InstanceCount;
                Vector3 p0 = n > 0 ? mgr.GetInstancePosition(0) : Vector3.zero;
                Vector3 pLast = n > 0 ? mgr.GetInstancePosition(n - 1) : Vector3.zero;
                float spread = Vector3.Distance(p0, pLast);
                w($"数据源：count={n}, p[0]={p0}, p[last]={pLast}, 跨度={spread:F1}m");
                if (spread < 10f)
                {
                    w("数据源：实例位置没有铺开 —— 问题在 CPU 侧布点，不在 shader。");
                    return false;
                }

                var camGo = new GameObject("GrassBRG_M1VerifyCam");
                cam = camGo.AddComponent<Camera>();
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = new Color(0f, 0f, 0f, 1f);
                cam.orthographic = false;
                cam.fieldOfView = 60f;
                cam.nearClipPlane = 0.3f;
                cam.farClipPlane = 500f;
                cam.transform.position = new Vector3(0f, 40f, -40f);
                cam.transform.LookAt(new Vector3(0f, 0f, 0f));
                cam.cullingMask = ~0;

                rt = new RenderTexture(size, size, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
                rt.Create();

                var pixels = RenderAndRead(cam, rt, size, out bool rendered);
                if (!rendered)
                {
                    w("端到端：当前 RenderPipeline 不支持 StandardRequest。");
                    return false;
                }

                var stats = Analyze(pixels);
                w($"正向：可见实例 {mgr.LastVisibleCount}，非背景 {stats.NonBlack}/{pixels.Length} = {stats.Ratio:P2}，"
                  + $"包围盒 {stats.MinX},{stats.MinY} - {stats.MaxX},{stats.MaxY}，绿色占比 {stats.GreenRatio:P2}");
                w($"  诊断：LOD 计数 = [{mgr.GetLastLodCount(0)}, {mgr.GetLastLodCount(1)}, {mgr.GetLastLodCount(2)}]，"
                  + $"LOD 三角形 = [{_lodTris[0]}, {_lodTris[1]}, {_lodTris[2]}]");
                SavePng(pixels, size, "Logs/Grass/m1-render-front.png");

                if (stats.NonBlack == 0)
                {
                    w("正向：画面全黑 —— BRG 没有产生任何可见像素。");
                    return false;
                }

                if (stats.Ratio < 0.005f)
                {
                    w("正向：非背景像素过少 —— 可能矩阵布局或剔除平面方向有误。");
                    return false;
                }

                float widthRatio = (stats.MaxX - stats.MinX + 1) / (float)size;
                float heightRatio = (stats.MaxY - stats.MinY + 1) / (float)size;
                w($"正向：屏幕覆盖 {widthRatio:P1} x {heightRatio:P1}");
                if (widthRatio < 0.3f || heightRatio < 0.3f)
                {
                    w("正向：草在屏幕上聚成一团 —— 矩阵布局或平移分量可能错位。");
                    return false;
                }

                if (stats.GreenRatio < 0.5f)
                {
                    w("正向：可见像素不是草的颜色 —— 渲染内容可疑。");
                    return false;
                }

                cam.transform.position = new Vector3(0f, 40f, -40f);
                cam.transform.LookAt(new Vector3(0f, 40f, -200f));
                var pixelsBack = RenderAndRead(cam, rt, size, out _);
                var statsBack = Analyze(pixelsBack);
                w($"负向（背对）：可见实例 {mgr.LastVisibleCount}，非背景 {statsBack.NonBlack}");
                SavePng(pixelsBack, size, "Logs/Grass/m1-render-back.png");

                if (statsBack.NonBlack != 0)
                {
                    w("负向：背对时仍有像素 —— 视锥剔除没生效。");
                    return false;
                }

                w("端到端：BRG 草渲染成功，且视锥剔除生效。");
                return true;
            }
            finally
            {
                RenderTexture.active = prevActive;
                if (rt != null)
                {
                    rt.Release();
                    Object.DestroyImmediate(rt);
                }
                if (cam != null)
                    Object.DestroyImmediate(cam.gameObject);
                Object.DestroyImmediate(root);

                foreach (var m in disabled)
                    if (m != null) m.enabled = true;
            }
        }

        private static Color32[] RenderAndRead(Camera cam, RenderTexture rt, int size, out bool supported)
        {
            var request = new RenderPipeline.StandardRequest { destination = rt };
            supported = RenderPipeline.SupportsRenderRequest(cam, request);
            if (!supported)
                return System.Array.Empty<Color32>();

            RenderPipeline.SubmitRenderRequest(cam, request);

            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false, true);
            tex.ReadPixels(new Rect(0, 0, size, size), 0, 0);
            tex.Apply();
            var pixels = tex.GetPixels32();
            Object.DestroyImmediate(tex);
            RenderTexture.active = prev;
            return pixels;
        }

        private readonly struct PixelStats
        {
            public readonly int NonBlack;
            public readonly float Ratio;
            public readonly int MinX, MinY, MaxX, MaxY;
            public readonly float GreenRatio;

            public PixelStats(int nonBlack, float ratio, int minX, int minY, int maxX, int maxY, float greenRatio)
            {
                NonBlack = nonBlack; Ratio = ratio;
                MinX = minX; MinY = minY; MaxX = maxX; MaxY = maxY;
                GreenRatio = greenRatio;
            }
        }

        private static PixelStats Analyze(Color32[] pixels)
        {
            const int size = 256;
            int nonBlack = 0, green = 0;
            int minX = size, minY = size, maxX = -1, maxY = -1;

            for (int i = 0; i < pixels.Length; i++)
            {
                var p = pixels[i];
                if (p.r <= 8 && p.g <= 8 && p.b <= 8)
                    continue;

                nonBlack++;
                if (p.g > p.r && p.g > p.b)
                    green++;

                int x = i % size;
                int y = i / size;
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
            }

            float ratio = nonBlack / (float)pixels.Length;
            float greenRatio = nonBlack > 0 ? green / (float)nonBlack : 0f;
            return new PixelStats(nonBlack, ratio, minX, minY, maxX, maxY, greenRatio);
        }

        private static void SavePng(Color32[] pixels, int size, string path)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false, true);
            tex.SetPixels32(pixels);
            tex.Apply();
            var png = tex.EncodeToPNG();
            Object.DestroyImmediate(tex);

            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllBytes(path, png);
        }
    }
}
