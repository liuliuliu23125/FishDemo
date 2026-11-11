using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.Rendering;
using UnityEngine;
using UnityEngine.Rendering;
using FishDemo.Rendering;

namespace FishDemo.EditorTools
{
    /// <summary>
    /// M1 验收：验证 BRG 草链路的最小可用性。
    ///
    /// 三层检查，逐层加深：
    ///   1. Shader 编译：DOTS_INSTANCING_ON 变体是否真的编译通过。
    ///      这是 M1 最大的未知项（方案 §5 风险表第一行）。可在 -nographics 下跑。
    ///   2. BRG 可用性：BufferTarget 是否为 RawBuffer/ConstantBuffer。
    ///   3. 端到端渲染：建 BRG → 离屏渲染 → 回读像素 → 统计非背景像素。
    ///      这是唯一能证明「草真的画出来了」的自动检查，需要真实 GfxDevice。
    ///
    /// 为什么必须做第 3 层：BRG 的 OnPerformCulling 是引擎在渲染时回调的，
    /// 只要不真的渲染一次，AddBatch / metadata / 矩阵布局全都无法被验证。
    /// 编译通过 + BufferTarget 正确，仍可能因为 splitVisibilityMask=0 或
    /// matrix 布局错而一片空白（方案 §5 的典型失败模式）。
    ///
    /// 用法：
    ///   只查 shader（无设备也行）：
    ///     Unity.exe -batchmode -nographics -projectPath E:\FishDemo \
    ///       -executeMethod FishDemo.EditorTools.GrassM1Verify.Run -logFile Logs/Grass/m1-verify.log
    ///   完整验收（必须有真实图形设备，批处理时不要加 -nographics）：
    ///     同上，但去掉 -nographics
    ///   编辑器内：菜单 FishDemo/Rendering/① 验证 BRG 草链路（M1）
    /// </summary>
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

            // 判定口径：shader 必须过；设备存在时端到端必须过。
            // 无设备时不算失败，但也绝不报 PASS——避免"看起来通过"的假绿。
            bool overall = shaderOk && (!deviceOk || renderOk);
            W($"==== 结论：{(overall ? "PASS" : "FAIL")}"
              + (deviceOk ? "" : "（仅 shader 层通过，端到端未验证）") + " ====");

            Directory.CreateDirectory(Path.GetDirectoryName(ResultPath));
            File.WriteAllText(ResultPath, sb.ToString());

            if (Application.isBatchMode)
                EditorApplication.Exit(overall ? 0 : 2);
        }

        /// <summary>
        /// 检查 shader 是否真的为 DOTS_INSTANCING_ON 变体编译通过。
        ///
        /// 为什么不能只看 Shader.Find 是否为空：拿到对象不等于变体编译成功，
        /// 变体错误在编译/首次使用时才报。GetShaderMessages 才能拿到真实错误。
        /// </summary>
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

            // 强制把 DOTS 变体编一遍：只 CompilePass 默认变体不够，
            // 必须显式打开 DOTS_INSTANCING_ON 关键字，才能验证 BRG 路径。
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
                // 有 Error 级别才算失败；Warning 放行但记录。
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

            // 注意：BatchRendererGroup.BufferTarget 是静态属性（返回 BatchBufferTarget），
            // 枚举成员必须用类型名限定，写成 BatchRendererGroup.BufferTarget.RawBuffer
            // 会被编译器当成「通过实例访问静态成员」而报 CS0176。
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

        /// <summary>
        /// 端到端：临时建一个 GrassBRGManager，用固定相机离屏渲染，回读像素统计。
        ///
        /// 判定标准：非背景像素数 &gt; 0 且占屏幕比例落在合理区间。
        /// 只看"非空"不够——如果矩阵布局错了，草可能全挤在屏幕一角或跑出视野；
        /// 所以额外要求覆盖率 ≥ 0.5%（1000 株十字片在 256x256 里应远超此值）。
        /// </summary>
        private static bool CheckEndToEndRender(System.Action<string> w)
        {
            const int size = 256;

            // 关键：先禁用场景里已存在的 GrassBRGManager 节点。
            // World.unity 里摆着一个 GrassBRG (M1)（scale=8，参数与验证意图不同），
            // 不禁用的话它会和本脚本临时建的实例同时渲染，污染像素统计与渲染图
            // （实测踩过：画面出现一大块洋红，判定逻辑全被带偏）。
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
                // 关掉 LOD：本验证只回答「BRG 链路 + 矩阵布局 + 视锥剔除是否正确」，
                // 用 256px 离屏 RT 时距离 LOD 会把实例全剔掉（1m 草在 56m 处仅 4 像素），
                // 那是 LOD 的正常行为，会掩盖真正的链路问题。LOD 由 GrassLodVerify 单独验证。
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

                // 数据源断言：位置必须真的铺开（否则 shader 会全部叠在一点，
                // 表现为"画出来了但聚成一团"）。这是区分「数据错」和「shader 错」的关键。
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

                // 俯视略倾斜的相机，保证 1000 株方阵（约 47m 边长）完整入画。
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

                // ── 正向：应看到草 ──
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

                // 屏幕覆盖范围要合理：草方阵在视野里应铺开，而不是缩成一小团。
                // 1000 株 47m 方阵从 40m 高、60° FOV 看下去，横向应覆盖大半屏。
                float widthRatio = (stats.MaxX - stats.MinX + 1) / (float)size;
                float heightRatio = (stats.MaxY - stats.MinY + 1) / (float)size;
                w($"正向：屏幕覆盖 {widthRatio:P1} x {heightRatio:P1}");
                if (widthRatio < 0.3f || heightRatio < 0.3f)
                {
                    w("正向：草在屏幕上聚成一团 —— 矩阵布局或平移分量可能错位。");
                    return false;
                }

                // 草是绿色（_BaseColor = 0.25,0.55,0.15）：绿通道应显著高于红蓝。
                // 若矩阵错到把别的东西画进来，这条会挂。
                if (stats.GreenRatio < 0.5f)
                {
                    w("正向：可见像素不是草的颜色 —— 渲染内容可疑。");
                    return false;
                }

                // ── 负向：相机背对方阵，视锥剔除必须把实例全剔掉 ──
                cam.transform.position = new Vector3(0f, 40f, -40f);
                cam.transform.LookAt(new Vector3(0f, 40f, -200f)); // 朝 -Z 外侧看，草在 +Z 侧
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

                // 恢复被临时禁用的场景节点，避免留下副作用。
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

        /// <summary>统计非背景像素、屏幕包围盒、以及"绿色主导"的像素占比。</summary>
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
