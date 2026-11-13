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
/// 湖泊渲染验收：从玩家视角截图，用**像素统计**证明湖盆里有水。
///
/// ── 为什么用像素统计而不是"按世界点投影采样" ──
/// 上一版按世界点投影取样，结果恒为 0/0（点全部投不进屏幕）：
/// 世界点投影依赖 Camera.Render 之后的相机矩阵状态，batchmode 下不可靠。
/// 改为对整张图分区统计，鲁棒得多，也更接近"人眼看到什么"：
///   - 中带（画面高度 20%~60%）：平视时水面主要落在这里；
///   - 下半部：水面/地面；
///   - 上半部：天空（对照）。
///
/// ── 判据 ──
/// Crest 默认水材质呈青-蓝：B 显著高于 R。草地/树/土是 R>=B；天空 B>R 但极亮。
/// 判"中带水体色占比 >= 15%"。
///
/// ── 相机 ──
/// 玩家视角：湖岸 (192, -144)、水面上方 2m（岸边略高于水面 + 人眼），朝湖心平视，FOV 60°。
///
/// 用法：-executeMethod LakeRenderVerify.Run
/// 输出 Logs/Lake/lake-render.txt + lake-render.png
/// </summary>
[InitializeOnLoad]
public static class LakeRenderVerify
{
    private const string Tag = "[LakeRender]";
    private const string WorldScenePath = "Assets/Scenes/World.unity";
    private const string OutDir = "Logs/Lake";
    private const string ResultPath = OutDir + "/lake-render.txt";
    private const string PngPath = OutDir + "/lake-render.png";
    private const string PendingKey = "FishDemo.LakeRender.Pending";

    static LakeRenderVerify()
    {
        EditorApplication.update += Poll;
    }

    public static void Run()
    {
        var sb = new StringBuilder();
        void W(string s) { Debug.Log($"{Tag} {s}"); sb.AppendLine(s); }

        Directory.CreateDirectory(OutDir);
        EditorSceneManager.OpenScene(WorldScenePath, OpenSceneMode.Single);

        // ── 预先打开与湖相关的 chunk ──
        // 为什么不靠 SECTR 流式：实测 Play Mode 下 SECTR 按相机位置异步加载，
        // 脚本瞬移相机后每轮加载到的 chunk 不同（同机位两次运行 GTS subTile bounds 完全不同）。
        // 但也不需要全部 36 个 —— 本验证只在一个固定机位看湖，
        // 按湖 AABB 筛选即可（36 → 8 左右），详见 ChunkLoadScope 类注释。
        ChunkLoadScope.LoadRelevant(ChunkLoadScope.Area.FromLake(), W);
        // 回到主场景为 active（Additive 打开不会切换 active scene，但保险）
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
        var cam = UnityEngine.Object.FindObjectsOfType<Camera>()
            .FirstOrDefault(c => c.CompareTag("MainCamera")) ?? UnityEngine.Object.FindObjectOfType<Camera>();

        if (water == null || cam == null)
        {
            sb.AppendLine("FAIL：Play Mode 下找不到 WaterRenderer 或相机");
            Flush(sb);
            EditorApplication.Exit(1);
            return;
        }

        new GameObject("~LakeRenderDriver").AddComponent<Driver>().Begin(sb, water, cam);
    }

    private sealed class Driver : MonoBehaviour
    {
        private StringBuilder _sb;
        private WaterRenderer _water;
        private Camera _cam;
        private int _frame;
        private bool _teleported;
        private bool _ready;
        private int _readyFrame;

        public void Begin(StringBuilder sb, WaterRenderer water, Camera cam)
        {
            _sb = sb; _water = water; _cam = cam;
            gameObject.hideFlags = HideFlags.DontSave;

            // ── 关掉 Pegasus 巡游控制器 ──
            // 它挂在 Main Camera 上、在 Update() 里驱动相机位姿（PegasusTourController.cs:47），
            // 会把本脚本摆好的机位每帧覆盖掉。
            // 实测证据：脚本把相机指向 (192,264,96)，但射线打出的 dir=(0.80,0.20,0.57) —— 完全不是设定值。
            // 这是之前所有"看不到湖/画面全黑"验收反复失败的真正原因。
            foreach (var tour in UnityEngine.Object.FindObjectsOfType<FishDemo.Gameplay.PegasusTourController>())
            {
                tour.enabled = false;
                _sb.AppendLine($"已禁用 Pegasus 巡游：{tour.name}");
            }

            // 关草 BRG：重建 1265 万实例会把这一轮拖到跑不完（实测 40 分钟不推进）。
            foreach (var g in UnityEngine.Object.FindObjectsOfType<FishDemo.Rendering.GrassBRGManager>())
            {
                g.gameObject.SetActive(false);
                _sb.AppendLine($"禁用草 BRG：{g.name}");
            }

            // ── 环境光：必须显式给 ──
            // World.unity 的 m_AmbientMode=0（Skybox 模式），环境光来自天空盒的**烘焙环境探针**。
            // batchmode 从不烘 GI，探针全黑 → 所有背光面和水面渲染成 (0,3,17) 近黑。
            // 这不是水面问题；改成 Flat 给个中性灰蓝，才能看到真实观感。
            RenderSettings.ambientMode = AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.42f, 0.47f, 0.55f);
            RenderSettings.ambientIntensity = 1f;
            _sb.AppendLine("环境光 = Flat(0.42,0.47,0.55)");
        }

        private void Update()
        {
            _frame++;

            // ── 第一步：把相机瞬移到湖边，SECTR 才会去加载这一带的 chunk ──
            // 实测教训：相机留在世界中心时，场景里只有 4 个 chunk，
            // 覆盖湖心的 Terrain = 0 —— 截图下半部全黑不是水的问题，是**地形没加载**。
            if (!_teleported)
            {
                _teleported = true;
                var c = new Vector3(LakeCarve.Center.x, 0f, LakeCarve.Center.y);
                _cam.transform.position = new Vector3(c.x, LakeCarve.WaterLevel + 30f, c.z - 150f);
                return;
            }

            // ── 第二步：等 SECTR 把湖边 chunk 真正加载完 ──
            // SECTR_Chunk 用 LoadSceneAsync + activationQueue 节流，一帧只激活一个
            // （SECTR_Chunk.cs:450-454），要给它足够帧数。判据用"覆盖湖心的 Terrain 数"，
            // 比固定帧数可靠。
            if (!_ready)
            {
                int covering = CountTerrainsCoveringLake();
                if (covering > 0 || _frame > 3000)
                {
                    _ready = true;
                    _sb.AppendLine($"[frame {_frame}] 覆盖湖心的 Terrain = {covering}，" +
                                   $"场景 Terrain 总数 = {UnityEngine.Object.FindObjectsOfType<Terrain>().Length}");
                }
                return;
            }

            var center = new Vector3(LakeCarve.Center.x, 0f, LakeCarve.Center.y);
            // ── 机位：站到湖岸**外侧**的高处，俯视整片湖面 ──
            // 上一版站在 R+50m、水面上方 2m 平视，实测画面下半部全黑：
            // 那个位置紧贴水面，4° 俯角只能看到极近处，而近处正好是 Clip 边界。
            // 改到 R+120m（岸外，地形约 270m）、抬高到水面 + 25m、俯角 18°，
            // 这样整片 380m 直径的湖面都在视野内，且能看到碗壁与岸线。
            // 机位放在湖心正上方偏南 150m：让 Crest 的视点（水面 clipmap 中心）
            // 与 Clip 盒体中心基本重合，避免 Clip 数据在远距离精度不足。
            Vector3 stand = new Vector3(center.x, LakeCarve.WaterLevel + 30f,
                                        center.z - 150f);
            _cam.transform.position = stand;
            _cam.transform.rotation = Quaternion.LookRotation(
                new Vector3(center.x - stand.x, (LakeCarve.WaterLevel - stand.y), center.z - stand.z).normalized);
            _cam.fieldOfView = 60f;
            _cam.nearClipPlane = 0.1f;
            _cam.farClipPlane = 3000f;

            // 再等 60 帧让地形与水面都稳定
            if (_frame < _readyFrame + 60)
            {
                if (_readyFrame == 0) _readyFrame = _frame;
                return;
            }
            // ── 刷新 GTS 地表材质 ──
            // GTS 的 splat 混合依赖一批全局 Vector 数组，由 GTSComponent.UpdateMaterial()
            // 在运行时注入。编辑器/批处理下不显式 Refresh 时地表全黑
            // （实测同一画面里树正常、地面黑，正是这个原因）。
            int refreshed = 0;
            foreach (var gtsMesh in UnityEngine.Object.FindObjectsOfType<ProceduralWorlds.GTS.GTSMesh>())
            {
                try { gtsMesh.Refresh(); refreshed++; } catch { }
            }
            _sb.AppendLine($"已 Refresh GTSMesh = {refreshed}");

            enabled = false;
            Shoot();
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

        private void Shoot()
        {
            _sb.AppendLine($"相机 = {_cam.name} pos={_cam.transform.position} " +
                           $"rot={_cam.transform.eulerAngles} forward={_cam.transform.forward} " +
                           $"FOV={_cam.fieldOfView}");

            const int W = 1920, H = 1080;
            var rt = new RenderTexture(W, H, 24, RenderTextureFormat.ARGB32);
            var prev = _cam.targetTexture;
            _cam.targetTexture = rt;

            // 渲 40 帧：Crest 的仿真（Absorption/Scattering 等）是逐帧推进的，
            // 首帧只有几何没有颜色数据。实测渲 3 帧时水面是纯黑。
            // 多渲几十帧让仿真纹理稳定下来。
            for (int i = 0; i < 40; i++) _cam.Render();

            RenderTexture.active = rt;
            var tex = new Texture2D(W, H, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, W, H), 0, 0);
            tex.Apply();
            _cam.targetTexture = prev;
            RenderTexture.active = null;

            File.WriteAllBytes(PngPath, tex.EncodeToPNG());

            var px = tex.GetPixels32();
            int distinct;
            {
                var set = new System.Collections.Generic.HashSet<int>();
                for (int i = 0; i < px.Length; i += 11)
                    set.Add((px[i].r << 16) | (px[i].g << 8) | px[i].b);
                distinct = set.Count;
            }

            // 分区统计（Image y=0 在底部）。
            int lowerWater = 0, lowerTotal = 0, upperWater = 0, allWater = 0;
            int midWater = 0, midTotal = 0;
            for (int y = 0; y < H; y++)
            {
                for (int x = 0; x < W; x++)
                {
                    var c = px[y * W + x];
                    bool isW = IsWater(c);
                    if (isW) allWater++;
                    if (y < H / 2) { lowerTotal++; if (isW) lowerWater++; }
                    else upperWater += isW ? 1 : 0;
                    if (y >= H * 2 / 10 && y < H * 6 / 10) { midTotal++; if (isW) midWater++; }
                }
            }

            float lowerRatio = lowerTotal > 0 ? (float)lowerWater / lowerTotal : 0f;
            float midRatio = midTotal > 0 ? (float)midWater / midTotal : 0f;
            float waterPixelRatio = (float)allWater / (W * H);

            _sb.AppendLine($"截图：{PngPath}，颜色种类 {distinct}");
            _sb.AppendLine($"水体色像素：整图 {allWater:N0}（{waterPixelRatio:P1}）");
            _sb.AppendLine($"  中带 20~60%    {midWater:N0}/{midTotal:N0} = {midRatio:P1}");
            _sb.AppendLine($"  下半部         {lowerWater:N0}/{lowerTotal:N0} = {lowerRatio:P1}");
            _sb.AppendLine($"  上半部（天空） 水体色像素 {upperWater:N0}");

            // 打印 Crest 写入的关键全局参数，判断仿真是否真的产出了颜色数据
            var abs = Shader.GetGlobalVector("_Crest_Absorption");
            var sca = Shader.GetGlobalVector("_Crest_Scattering");
            var center = Shader.GetGlobalVector("g_Crest_WaterCenter");
            _sb.AppendLine($"全局 _Crest_Absorption = ({abs.x:F3},{abs.y:F3},{abs.z:F3},{abs.w:F3})");
            _sb.AppendLine($"全局 _Crest_Scattering = ({sca.x:F3},{sca.y:F3},{sca.z:F3},{sca.w:F3})");
            _sb.AppendLine($"全局 g_Crest_WaterCenter = ({center.x:F1},{center.y:F1},{center.z:F1})");

            // 相机正下方的 GTS 地形是否在渲染
            var gtsRends = UnityEngine.Object.FindObjectsOfType<MeshRenderer>()
                .Where(r => r.sharedMaterial != null && r.sharedMaterial.shader != null &&
                            r.sharedMaterial.shader.name.Contains("GTS")).ToArray();
            var lakePos = new Vector3(LakeCarve.Center.x, LakeCarve.WaterLevel, LakeCarve.Center.y);
            var near = gtsRends.Where(r => r.bounds.Contains(lakePos) ||
                        Vector3.Distance(r.bounds.ClosestPoint(lakePos), lakePos) < 300f).ToArray();
            _sb.AppendLine($"GTS renderer 总数 = {gtsRends.Length}，湖心 300m 内 = {near.Length}");
            foreach (var r in near.Take(6))
                _sb.AppendLine($"    {r.name}: bounds={r.bounds.center} size={r.bounds.size} " +
                               $"vis={r.isVisible} off={r.forceRenderingOff} act={r.gameObject.activeInHierarchy}");

            // ── 画面中央像素对应什么：用射线找它打到哪一层 ──
            var centerRay = _cam.ScreenPointToRay(new Vector3(1920 * 0.5f, 1080 * 0.45f, 0f));
            _sb.AppendLine($"画面中央射线 origin={centerRay.origin} dir={centerRay.direction}");
            var hitsAll = Physics.RaycastAll(centerRay, 3000f);
            if (hitsAll.Length == 0) _sb.AppendLine("  无碰撞命中");
            foreach (var h in hitsAll.OrderBy(h => h.distance))
                _sb.AppendLine($"  hit {h.collider.name} @ {h.point} dist={h.distance:F1}");

            var mat = _water.Surface.Material;
            var rends = _water.GetComponentsInChildren<MeshRenderer>(true)
                .Where(r => r.sharedMaterial == mat).ToArray();
            int vis = rends.Count(r => r.isVisible);
            _sb.AppendLine($"水面 chunk：{rends.Length} 个，渲染可见 {vis} 个");

            _sb.AppendLine("");
            _sb.AppendLine("===== 湖泊渲染验收 =====");
            bool c1 = Mathf.Abs(_water.transform.position.y - LakeCarve.WaterLevel) < 0.5f;
            bool c2 = rends.Length > 0;
            bool c3 = midRatio >= 0.12f;
            bool c4 = distinct > 500;

            _sb.AppendLine($"[1] 水位正确           : {(c1 ? "PASS" : "FAIL")} (y={_water.transform.position.y:F1})");
            _sb.AppendLine($"[2] 水面 chunk 存在    : {(c2 ? "PASS" : "FAIL")} ({rends.Length})");
            _sb.AppendLine($"[3] 中带出现水体色     : {(c3 ? "PASS" : "FAIL")} ({midRatio:P1}，阈值 12%)");
            _sb.AppendLine($"[4] 画面有内容         : {(c4 ? "PASS" : "FAIL")} ({distinct} 色)");

            bool all = c1 && c2 && c3 && c4;
            _sb.AppendLine("");
            _sb.AppendLine(all ? "==== 结论：PASS ====" : "==== 结论：FAIL ====");

            DestroyImmediate(tex);
            rt.Release();
            DestroyImmediate(rt);

            File.WriteAllText(ResultPath, _sb.ToString());
            // 不先 isPlaying=false：退出 Play Mode 会触发域重载 + 全量资产重导入
            // （实测 20s 资产导入 + 10s 备份场景落盘）。本进程紧接着 Exit，直接退出最省时间。
            gameObject.AddComponent<QuitAfter>().Init();
        }

        /// <summary>
        /// 水体色判据：
        /// B 显著高于 R，且亮度不过高（排除天空/云）、不过低（排除阴影）。
        /// 草地/树 R>=B，土石 R>B，都会被排除。
        /// </summary>
        private static bool IsWater(Color32 c)
        {
            int r = c.r, g = c.g, b = c.b;
            if (b < 45) return false;
            if (r > 200 && g > 200 && b > 200) return false;
            return b > r + 18 && b >= g - 5;
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
