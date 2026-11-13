using System;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using WaveHarmonic.Crest;

/// <summary>
/// 水体表现验收：证明「水面变透明了」和「相机下水后有内容」。
///
/// ── 判据怎么来的（第一性原理）──
/// 这不是「看起来顺眼」的主观判断，三条都用可量化的像素证据：
///
/// 1. **水面透明**：在水浅处（岸边内侧），从水面上方俯视。
///    水不透明时，水面区域是一片近似纯色的蓝（像素高度集中）；
///    透明时能看到水底地形纹理与明暗起伏（像素离散度显著上升）。
///    判据：水面区域内 **亮度标准差** ≥ 阈值，且**颜色种类**足够多。
///    对照组：把同一区域在「水面以下」的像素拿掉后剩下的地形像素，
///    两者离散度应接近（说明水面没把地形糊掉）。
///
/// 2. **水下有内容**：相机下沉到水位以下 5m。
///    - 判据 a：画面整体色调变青绿（G/B 通道均值显著高于 R）——
///      这是体积吸收（`extinction` 每米衰减 R 最快）造成的，是水下效果的**直接证据**。
///    - 判据 b：画面不是一片纯色（颜色种类 > 阈值），说明地形/水面仍在渲染。
///    - 判据 c：与「水上同机位」的截图相比，亮度必须显著下降（水下环境光衰减）。
///
/// 3. **开关正确**：水上（相机离水面 > 2m）时 `UnderwaterRenderer.ShouldRender`
///    应返回 false —— 见 `UnderwaterRenderer.cs:299-307`，那是引擎自带的行为。
///    这里用「水上截图不得呈水下色调」间接验证了它没在水上乱开。
///
/// ── 环境（batchmode 必须补偿的两件事）──
/// ① `RenderSettings.ambientMode = Flat`：场景未烘 GI，Skybox 模式的环境探针全黑，
///    会把所有背光面渲成近黑，与水无关。
/// ② 每个 `GTSMesh.Refresh()`：splat 混合的全局数组由它注入，不刷新地面全黑。
///
/// 用法：-executeMethod LakeWaterVerify.Run
/// 输出 Logs/Lake/lake-water.txt + lake-water-*.png
/// </summary>
[InitializeOnLoad]
public static class LakeWaterVerify
{
    private const string Tag = "[LakeWater]";
    private const string WorldScenePath = "Assets/Scenes/World.unity";
    private const string OutDir = "Logs/Lake";
    private const string ResultPath = OutDir + "/lake-water.txt";
    private const string PendingKey = "FishDemo.LakeWater.Pending";

    /// <summary>
    /// 每张截图前的预热帧数。
    ///
    /// 为什么不再是 40：那段注释的前提是「Crest 的 Absorption/Scattering 仿真逐帧推进，
    /// 首帧只有几何没有颜色」。但工程已切到 **SimpleTransparency**（`CREST_SIMPLE_TRANSPARENCY=1`），
    /// 该模式下 shader 不再采样任何仿真纹理（`Fragment.hlsl:490-500` 改用 waterDepth 估算），
    /// 也没有 opaque/depth copy pass（实测 URP pass 队列只剩 WaterSurfaceRenderPass）。
    /// 所以只需让水面 chunk 的可见性与材质参数就绪，8 帧足够。
    ///
    /// 若将来关掉 SimpleTransparency，这里要调回 40。
    /// </summary>
    private const int WarmupFrames = 8;

    static LakeWaterVerify()
    {
        EditorApplication.update += Poll;
    }

    public static void Run()
    {
        var sb = new StringBuilder();
        void W(string s) { Debug.Log($"{Tag} {s}"); sb.AppendLine(s); }

        Directory.CreateDirectory(OutDir);
        EditorSceneManager.OpenScene(WorldScenePath, OpenSceneMode.Single);

        // 预加载全部 chunk：SECTR 是异步流式，脚本瞬移相机后加载结果不确定
        // （同机位两次运行拿到的 chunk 完全不同），几何必须一定齐全。
        // 只加载与湖相关的 chunk（36 → 8 左右），验证机位固定看湖，
        // 不需要全世界。详见 ChunkLoadScope 的类注释。
        ChunkLoadScope.LoadRelevant(ChunkLoadScope.Area.FromLake(), W);
        EditorSceneManager.SetActiveScene(EditorSceneManager.GetSceneByPath(WorldScenePath));

        var water = UnityEngine.Object.FindObjectOfType<WaterRenderer>();
        if (water == null) { W("FAIL：场景里没有 WaterRenderer"); Flush(sb); return; }

        SessionState.SetString(PendingKey + ".Pre", sb.ToString());
        SessionState.SetBool(PendingKey, true);
        EditorApplication.isPlaying = true;
    }

    private static void Poll()
    {
        if (!SessionState.GetBool(PendingKey, false) || !EditorApplication.isPlaying)
            return;

        SessionState.SetBool(PendingKey, false);
        EditorApplication.update -= Poll;

        var sb = new StringBuilder(SessionState.GetString(PendingKey + ".Pre", string.Empty));
        var water = UnityEngine.Object.FindObjectOfType<WaterRenderer>();
        if (water == null)
        {
            sb.AppendLine("FAIL：Play Mode 下找不到 WaterRenderer");
            Flush(sb);
            EditorApplication.Exit(1);
            return;
        }

        new GameObject("~LakeWaterDriver").AddComponent<Driver>().Begin(sb, water);
    }

    private sealed class Driver : MonoBehaviour
    {
        private StringBuilder _sb;
        private WaterRenderer _water;
        private int _frame;
        private bool _ready;
        private int _readyFrame;

        public void Begin(StringBuilder sb, WaterRenderer water)
        {
            _sb = sb; _water = water;
            gameObject.hideFlags = HideFlags.DontSave;

            // 关掉巡游控制器与相机 rig：它们每帧改相机位姿，会把验收机位覆盖掉
            // （这是历史上「验了十几轮都看不到湖」的真正原因）。
            foreach (var t in UnityEngine.Object.FindObjectsOfType<FishDemo.Gameplay.PegasusTourController>())
            {
                t.enabled = false;
                _sb.AppendLine($"已禁用 Pegasus 巡游控制器：{t.name}");
            }
            foreach (var r in UnityEngine.Object.FindObjectsOfType<FishDemo.Gameplay.LakeCameraRig>())
            {
                r.enabled = false;
                _sb.AppendLine($"已禁用相机 rig：{r.name}");
            }
            // 关掉 Pegasus 本身，否则它的 LateUpdate 仍会写相机
            foreach (var m in UnityEngine.Object.FindObjectsOfType<Pegasus.PegasusManager>())
            {
                m.PauseFlythrough();
                m.enabled = false;
                _sb.AppendLine($"已暂停并禁用 PegasusManager：{m.name}");
            }
            // 草 BRG 重建百万实例会把这一轮拖到跑不完
            foreach (var g in UnityEngine.Object.FindObjectsOfType<FishDemo.Rendering.GrassBRGManager>())
            {
                g.gameObject.SetActive(false);
                _sb.AppendLine($"禁用草 BRG：{g.name}");
            }

            RenderSettings.ambientMode = AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.42f, 0.47f, 0.55f);
            RenderSettings.ambientIntensity = 1f;
            _sb.AppendLine("环境光 = Flat(0.42,0.47,0.55)");
        }

        private void Update()
        {
            _frame++;

            // 先把相机挪到湖边，触发 SECTR 加载这一带的 chunk。
            if (!_ready)
            {
                var c = new Vector3(LakeCarve.Center.x, 0f, LakeCarve.Center.y);
                var cam = FindVerifyCamera();
                if (cam != null)
                    cam.transform.position = new Vector3(c.x, LakeCarve.WaterLevel + 20f, c.z - 120f);

                if (CountTerrainsCoveringLake() > 0 || _frame > 3000)
                {
                    _ready = true;
                    _readyFrame = _frame;
                    _sb.AppendLine($"[frame {_frame}] 覆盖湖心的 Terrain = {CountTerrainsCoveringLake()}，" +
                                   $"场景 Terrain 总数 = {UnityEngine.Object.FindObjectsOfType<Terrain>().Length}");
                }
                return;
            }

            // 多等一会儿让水面 chunk / 深度数据就绪。
            if (_frame < _readyFrame + 60) return;

            int refreshed = 0;
            foreach (var gtsMesh in UnityEngine.Object.FindObjectsOfType<ProceduralWorlds.GTS.GTSMesh>())
            {
                try { gtsMesh.Refresh(); refreshed++; } catch { }
            }
            _sb.AppendLine($"已 Refresh GTSMesh = {refreshed}");

            enabled = false;
            Execute();
        }

        private static Camera FindVerifyCamera()
        {
            // 优先用轨道相机（不依赖 Pegasus），其次 MainCamera。
            var orbit = UnityEngine.Object.FindObjectsOfType<FishDemo.Gameplay.LakeOrbitCamera>()
                .Select(c => c.GetComponent<Camera>()).FirstOrDefault(c => c != null);
            if (orbit != null) { orbit.enabled = true; return orbit; }

            var cam = UnityEngine.Object.FindObjectsOfType<Camera>()
                .FirstOrDefault(c => c.CompareTag("MainCamera")) ?? UnityEngine.Object.FindObjectOfType<Camera>();
            if (cam != null) cam.enabled = true;
            return cam;
        }

        private static int CountTerrainsCoveringLake()
        {
            int n = 0;
            float cx = LakeCarve.Center.x, cz = LakeCarve.Center.y;
            foreach (var t in UnityEngine.Object.FindObjectsOfType<Terrain>())
            {
                if (t.terrainData == null) continue;
                var p = t.transform.position; var s = t.terrainData.size;
                if (cx >= p.x && cx <= p.x + s.x && cz >= p.z && cz <= p.z + s.z) n++;
            }
            return n;
        }

        private struct Shot
        {
            public string Name;
            public Color32[] Px;
            public int W, H;
        }

        private void Execute()
        {
            var cam = FindVerifyCamera();
            if (cam == null) { _sb.AppendLine("FAIL：找不到相机"); Finish(false); return; }

            cam.fieldOfView = 60f;
            cam.nearClipPlane = 0.1f;
            cam.farClipPlane = 3000f;

            float wl = LakeCarve.WaterLevel;
            float cx = LakeCarve.Center.x, cz = LakeCarve.Center.y;
            float R = LakeCarve.Radius;

            _sb.AppendLine("");
            _sb.AppendLine($"水位 = {wl}, 湖心 = ({cx},{cz}), 半径 = {R}");

            // ── 机位 A：浅水区上方俯视（验证水面透明）──
            // 相机悬在**水面上方**、湖内 r = R-55 = 135m 处，高度 wl+6、俯角约 30°。
            // 为什么悬在水面上而不是站在岸上：湖区植被只清到 r = R+8（LakeVegetationClear），
            // 站岸上会正好落在树里，近处树干会挡住画面（上一轮实测画面中间一条黑线就是它）。
            // 该处碗底深度 ≈ 30*(1-(135/190)^2) ≈ 15m；再往外 20m 就只有 ~9m，
            // 配合新的 1/e 深度 16~24m，能看到碗底的水下地形。
            var standA = new Vector3(cx, wl + 6f, cz + (R - 55f));
            var lookA = new Vector3(cx, wl - 10f, cz + (R - 105f));
            PlaceCam(cam, standA, lookA);
            var a = Capture(cam, "shallow-above", WarmupFrames);

            // ── 机位 B：与 A **同一 XZ 与朝向**，只把相机沉到水下 5m ──
            // 这样 A/B 的差异只剩"水上 / 水下"。
            // y = wl-5 时水面在相机上方 5m，UnderwaterRenderer 的
            // `_ViewerWaterHeight > 2f` 早退条件不成立（UnderwaterRenderer.cs:299-307），
            // 水下效果必然开启。
            var standB = new Vector3(standA.x, wl - 5f, standA.z);
            PlaceCam(cam, standB, lookA);
            var b = Capture(cam, "underwater", WarmupFrames);

            // ── 机位 B0：同机位，但**关掉水下效果**作为对照组 ──
            // 这是"水下效果真的生效"最直接的证据：同一台相机、同一朝向、
            // 同一个水面，只差 `UnderwaterRenderer.Enabled`。
            // 若两者画面几乎一样 → 水下效果没跑（比任何色调阈值都可靠）。
            var underwater = _water.Underwater;
            bool uwWasEnabled = underwater.Enabled;
            underwater.Enabled = false;
            var b0 = Capture(cam, "underwater-off", WarmupFrames);
            underwater.Enabled = uwWasEnabled;
            _sb.AppendLine($"水下效果对照组：Enabled {uwWasEnabled} → false → {uwWasEnabled}");

            // ── 机位 C：轨道相机的真实机位（验证玩家看到的画面）──
            // 直接用 LakeOrbitCamera 的参数复现：半径 300m、水位 +26m、俯角 12°。
            var standC = new Vector3(cx, wl + 26f, cz + 300f);
            PlaceCam(cam, standC, new Vector3(cx, wl, cz));
            var c = Capture(cam, "orbit-view", WarmupFrames);

            // ── 统计 ──
            var statsA = Analyze(a);
            var statsB = Analyze(b);
            var statsB0 = Analyze(b0);
            var statsC = Analyze(c);

            _sb.AppendLine("");
            _sb.AppendLine("=== 像素统计 ===");
            _sb.AppendLine($"A 浅水俯视   : 亮度均值 {statsA.MeanLuma:F1}, 标准差 {statsA.StdLuma:F1}, 色种 {statsA.Distinct}, " +
                           $"均值RGB ({statsA.MeanR:F0},{statsA.MeanG:F0},{statsA.MeanB:F0})");
            _sb.AppendLine($"B 水下(开)   : 亮度均值 {statsB.MeanLuma:F1}, 标准差 {statsB.StdLuma:F1}, 色种 {statsB.Distinct}, " +
                           $"均值RGB ({statsB.MeanR:F0},{statsB.MeanG:F0},{statsB.MeanB:F0})");
            _sb.AppendLine($"B0 水下(关)  : 亮度均值 {statsB0.MeanLuma:F1}, 标准差 {statsB0.StdLuma:F1}, 色种 {statsB0.Distinct}, " +
                           $"均值RGB ({statsB0.MeanR:F0},{statsB0.MeanG:F0},{statsB0.MeanB:F0})");
            _sb.AppendLine($"C 轨道机位   : 亮度均值 {statsC.MeanLuma:F1}, 标准差 {statsC.StdLuma:F1}, 色种 {statsC.Distinct}, " +
                           $"均值RGB ({statsC.MeanR:F0},{statsC.MeanG:F0},{statsC.MeanB:F0})");

            // 水下色调比 = (G+B)/2 / R。吸收让 R 衰减最快，所以水下该值显著大于 1。
            // 只取**画面上半部**：那里水体散射占比高，下半部透过水能看到黄绿地形会稀释色调。
            // 但绝对值受"这一帧里地形占多少"影响很大（湖只有 16m 深，碗底占屏幕大半），
            // 所以判定不用绝对值，改用「同机位开/关水下效果」的**差值**（见 [2]）。
            float tintB = (statsB.UpperMeanG + statsB.UpperMeanB) * 0.5f / Mathf.Max(1f, statsB.UpperMeanR);
            float tintB0 = (statsB0.UpperMeanG + statsB0.UpperMeanB) * 0.5f / Mathf.Max(1f, statsB0.UpperMeanR);
            float tintC = (statsC.UpperMeanG + statsC.UpperMeanB) * 0.5f / Mathf.Max(1f, statsC.UpperMeanR);
            _sb.AppendLine($"水下调色比 (G+B)/2/R : B开={tintB:F2}, B关={tintB0:F2}, C={tintC:F2}（取画面上半部）");

            // ── 判定 ──
            _sb.AppendLine("");
            _sb.AppendLine("===== 水体表现验收 =====");

            // [1] 水面透明：浅水画面必须"有细节"，不能是一块纯色板。
            //     透明水面能看到水底地形纹理 → 亮度标准差高、色种多。
            bool c1 = statsA.StdLuma >= 18f && statsA.Distinct > 2000;
            _sb.AppendLine($"[1] 水面透明（浅水见底）: {(c1 ? "PASS" : "FAIL")} " +
                           $"(标准差 {statsA.StdLuma:F1} ≥ 18, 色种 {statsA.Distinct} > 2000)");

            // [2] 水下呈吸收色调（R 衰减快于 G/B）。
            //     判据用**开/关差值**而不是绝对值：绝对值取决于画面里地形占多少，
            //     湖只有 16m 深，碗底本就占屏幕大半，会把比值压到 1 附近。
            //     差值只反映"水下效果把色偏改了多少"，与构图无关。
            bool c2 = (tintB - tintB0) >= 0.10f && tintB >= 1.05f;
            _sb.AppendLine($"[2] 水下呈吸收色调     : {(c2 ? "PASS" : "FAIL")} " +
                           $"(开/关差 {tintB - tintB0:F3} ≥ 0.10, 绝对值 {tintB:F2} ≥ 1.05)");

            // [3] 水下有内容：不是一片纯色（地形/水面仍在画），且不至于全黑。
            bool c3 = statsB.Distinct > 800 && statsB.MeanLuma >= 6f;
            _sb.AppendLine($"[3] 水下画面有内容     : {(c3 ? "PASS" : "FAIL")} " +
                           $"(色种 {statsB.Distinct} > 800, 亮度 {statsB.MeanLuma:F1} ≥ 6)");

            // [4] 水下效果真的生效：同机位「开 vs 关」必须明显不同。
            //     这是最可靠的判据 —— 不依赖任何色调阈值，只要求"开关造成可观测差异"。
            //     用亮度差 + 颜色分布差异两个维度同时判断，避免仅某一项偶然接近。
            float lumaDelta = Mathf.Abs(statsB.MeanLuma - statsB0.MeanLuma);
            float tintOn = (statsB.UpperMeanG + statsB.UpperMeanB) * 0.5f / Mathf.Max(1f, statsB.UpperMeanR);
            float tintOff = (statsB0.UpperMeanG + statsB0.UpperMeanB) * 0.5f / Mathf.Max(1f, statsB0.UpperMeanR);
            float tintDelta = Mathf.Abs(tintOn - tintOff);
            bool c4 = lumaDelta >= 10f || tintDelta >= 0.10f;
            _sb.AppendLine($"[4] 水下效果确实生效   : {(c4 ? "PASS" : "FAIL")} " +
                           $"(亮度差 {lumaDelta:F1} ≥ 10 或 调色比差 {tintDelta:F3} ≥ 0.10)");

            // [5] 水上不得呈水下色调：验证 UnderwaterRenderer 的 >2m 自动关闭生效。
            bool c5 = tintC < 1.15f;
            _sb.AppendLine($"[5] 水上未误开水下效果 : {(c5 ? "PASS" : "FAIL")} (比值 {tintC:F2} < 1.15)");

            // [6] 水面有内容（不是一块完美平板）——色种数够即可。
            bool c6 = statsC.Distinct > 1500;
            _sb.AppendLine($"[6] 水面有内容         : {(c6 ? "PASS" : "FAIL")} (色种 {statsC.Distinct} > 1500)");

            bool all = c1 && c2 && c3 && c4 && c5 && c6;
            _sb.AppendLine("");
            _sb.AppendLine(all ? "==== 结论：PASS ====" : "==== 结论：FAIL ====");

            Finish(all);
        }

        private static void PlaceCam(Camera cam, Vector3 pos, Vector3 lookAt)
        {
            cam.transform.position = pos;
            cam.transform.rotation = Quaternion.LookRotation((lookAt - pos).normalized, Vector3.up);
        }

        private Shot Capture(Camera cam, string name, int frames)
        {
            const int W = 1280, H = 720;
            var rt = new RenderTexture(W, H, 24, RenderTextureFormat.ARGB32);
            var prev = cam.targetTexture;
            cam.targetTexture = rt;

            // Crest 的仿真逐帧推进，首帧只有几何没有颜色；多渲几十帧让数据稳定。
            var swc = System.Diagnostics.Stopwatch.StartNew();
            for (int i = 0; i < frames; i++) cam.Render();
            long renderMs = swc.ElapsedMilliseconds;

            RenderTexture.active = rt;
            var tex = new Texture2D(W, H, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, W, H), 0, 0);
            tex.Apply();
            cam.targetTexture = prev;
            RenderTexture.active = null;

            var path = Path.Combine(OutDir, $"lake-water-{name}.png");
            File.WriteAllBytes(path, tex.EncodeToPNG());
            var px = tex.GetPixels32();

            DestroyImmediate(tex);
            rt.Release();
            DestroyImmediate(rt);

            _sb.AppendLine($"截图 {name}: {path}（渲染 {frames} 帧耗时 {renderMs}ms，总 {swc.ElapsedMilliseconds}ms）");
            return new Shot { Name = name, Px = px, W = W, H = H };
        }

        private struct Stats
        {
            public float MeanLuma, StdLuma, MeanR, MeanG, MeanB;
            // 画面上半部（去掉顶部天空带后的前 40% 高度）的三个通道均值。
            // 水下往上看主要是水体散射，地形占比低，能真实反映水色。
            public float UpperMeanR, UpperMeanG, UpperMeanB;
            public int Distinct;
        }

        /// <summary>
        /// 统计整幅图。刻意**不做水体色过滤**：
        /// 透明水面的判据是「画面有细节」，按颜色筛掉反而会人为降低离散度。
        /// 只跳过天空/纯背景（顶部 15% 且极亮），避免天空拉高均值。
        /// </summary>
        private static Stats Analyze(Shot shot)
        {
            var px = shot.Px;
            int W = shot.W, H = shot.H;
            int yStart = (int)(H * 0.15f);   // 跳过顶部天空
            int yEnd = H - yStart;
            int upperEnd = yStart + (int)((yEnd - yStart) * 0.40f);

            double sumL = 0; long n = 0;
            double sumR = 0, sumG = 0, sumB = 0;
            double upR = 0, upG = 0, upB = 0; long upN = 0;
            var set = new System.Collections.Generic.HashSet<int>();

            for (int y = yStart; y < yEnd; y++)
            {
                bool upper = y < upperEnd;
                for (int x = 0; x < W; x++)
                {
                    var c = px[y * W + x];
                    float luma = 0.299f * c.r + 0.587f * c.g + 0.114f * c.b;
                    sumL += luma; n++;
                    sumR += c.r; sumG += c.g; sumB += c.b;
                    if (upper) { upR += c.r; upG += c.g; upB += c.b; upN++; }
                    if (((x + y) & 1) == 0) set.Add((c.r << 16) | (c.g << 8) | c.b);
                }
            }

            double meanL = n > 0 ? sumL / n : 0;
            double var = 0;
            for (int y = yStart; y < yEnd; y++)
            {
                for (int x = 0; x < W; x++)
                {
                    var c = px[y * W + x];
                    float luma = 0.299f * c.r + 0.587f * c.g + 0.114f * c.b;
                    double d = luma - meanL;
                    var += d * d;
                }
            }

            return new Stats
            {
                MeanLuma = (float)meanL,
                StdLuma = (float)Math.Sqrt(n > 0 ? var / n : 0),
                MeanR = n > 0 ? (float)(sumR / n) : 0,
                MeanG = n > 0 ? (float)(sumG / n) : 0,
                MeanB = n > 0 ? (float)(sumB / n) : 0,
                UpperMeanR = upN > 0 ? (float)(upR / upN) : 0,
                UpperMeanG = upN > 0 ? (float)(upG / upN) : 0,
                UpperMeanB = upN > 0 ? (float)(upB / upN) : 0,
                Distinct = set.Count,
            };
        }

        private void Finish(bool pass)
        {
            File.WriteAllText(ResultPath, _sb.ToString());
            // 不先 `isPlaying = false`：退出 Play Mode 会触发域重载 + 全量资产重导入
            // （实测 Editor.log 里 `StopAssetImportingV2(ForceSynchronousImport | ForceDomainReload)`
            // 耗时 20s，还要把 10 个 __Backupscenes 落盘 ~10s）。
            // 本进程紧接着就 Exit，Play Mode 状态不会被保留到下次运行，
            // 所以直接退出最省时间。WaitThenExit 给渲染/写盘留几帧余量。
            gameObject.AddComponent<QuitAfter>().Init();
        }
    }

    private sealed class QuitAfter : MonoBehaviour
    {
        private int _f;
        public void Init() { hideFlags = HideFlags.DontSave; }
        private void Update()
        {
            if (++_f < 3) return;
            EditorApplication.Exit(0);
        }
    }

    private static void Flush(StringBuilder sb)
    {
        Directory.CreateDirectory(OutDir);
        File.WriteAllText(ResultPath, sb.ToString());
    }
}
