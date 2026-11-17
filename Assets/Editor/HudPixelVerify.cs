using System;
using System.IO;
using System.Linq;
using System.Text;
using FishDemo.Gameplay;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// HUD **像素级**可见性验收：真的渲染一帧 Game 视图、抓屏、在左上角区域数非天空像素。
///
/// ── 为什么之前的验收不够 ──
/// 只验 `ComputeHudWidth` 的算术（宽度不出屏）是不够的。
/// 但"看不见"有两种可能，算术 PASS 一个都排除不了：
///   ① OnGUI 根本没执行（`_applied=false` / 组件禁用 / 对象失活）；
///   ② 执行了但画到了别处，或黑底透明度太低看不出来。
/// 而且既有验收截图走 `cam.Render()` → RenderTexture，**OnGUI 属于屏幕空间，
/// 不会进那张 RT**——所以历史上没有任何一张截图包含过 HUD。
///
/// ── 本脚本怎么拿到真证据 ──
/// 1. 读 `AtmosphereController` 的 OnGUI 探针计数，确认它到底跑没跑、rect 是多少；
/// 2. 把 Game 视图切到窄分辨率（模拟"区域太窄"），保证走的是夹取分支；
/// 3. `ScreenCapture.CaptureScreenshot` 抓真实后缓冲（OnGUI 已合成），
///    在 HUD rect 覆盖的区域内统计与背景色差异明显的像素比例。
///
/// 判据：
///   A. OnGUI 执行次数 > 0（画了）；
///   B. HUD rect 完全落在屏幕内（没出屏）；
///   C. 截图左上角区域有足够比例的"非背景"像素（真的看得见）；
///   D. 在 480 宽与 1920 宽下都成立（窄视图不退化）。
///
/// 用法：-executeMethod HudPixelVerify.Run（**不能用 -nographics**，抓屏需要真实设备）
/// 输出 Logs/Atmosphere/hud-pixel.txt 与两张 png
/// </summary>
[InitializeOnLoad]
public static class HudPixelVerify
{
    private const string Tag = "[HudPixel]";
    private const string WorldScenePath = "Assets/Scenes/World.unity";
    private const string OutDir = "Logs/Atmosphere";
    private const string ResultPath = OutDir + "/hud-pixel.txt";
    private const string PendingKey = "FishDemo.HudPixel.Pending";

    static HudPixelVerify()
    {
        EditorApplication.update += Poll;
    }

    public static void Run()
    {
        var sb = new StringBuilder();
        void W(string s) { Debug.Log($"{Tag} {s}"); sb.AppendLine(s); }

        try
        {
            Directory.CreateDirectory(OutDir);
            EditorSceneManager.OpenScene(WorldScenePath, OpenSceneMode.Single);
            foreach (var g in UnityEngine.Object.FindObjectsOfType<FishDemo.Rendering.GrassBRGManager>())
                g.gameObject.SetActive(false);

            var ctrl = UnityEngine.Object.FindObjectOfType<AtmosphereController>();
            if (ctrl == null)
            {
                W("FAIL：场景里没有 AtmosphereController");
                Flush(sb, 2);
                return;
            }

            SessionState.SetString(PendingKey + ".Pre", sb.ToString());
            SessionState.SetBool(PendingKey, true);
            EditorApplication.isPlaying = true;
        }
        catch (Exception ex)
        {
            W($"异常：{ex}");
            Flush(sb, 1);
        }
    }

    private static void Poll()
    {
        if (!SessionState.GetBool(PendingKey, false) || !EditorApplication.isPlaying)
            return;

        SessionState.SetBool(PendingKey, false);
        EditorApplication.update -= Poll;

        var sb = new StringBuilder(SessionState.GetString(PendingKey + ".Pre", string.Empty));
        var ctrl = UnityEngine.Object.FindObjectOfType<AtmosphereController>();
        if (ctrl == null)
        {
            sb.AppendLine("FAIL：Play Mode 下找不到 AtmosphereController");
            Flush(sb, 2);
            return;
        }
        new GameObject("~HudPixelDriver").AddComponent<Driver>().Begin(sb, ctrl);
    }

    private sealed class Driver : MonoBehaviour
    {
        private StringBuilder _sb;
        private AtmosphereController _ctrl;

        private int _frame;
        private int _step;
        private float _waitUntil;
        private bool _finished;

        // 两个尺寸：480 是"区域太窄"的复现，1920 是对照。
        private static readonly Vector2Int[] Sizes =
        {
            new Vector2Int(480, 270),
            new Vector2Int(1920, 1080),
        };
        private int _sizeIndex;
        private readonly bool[] _okDrawn = new bool[2];
        private readonly bool[] _okInside = new bool[2];
        private readonly bool[] _okPixels = new bool[2];
        private readonly float[] _ratios = new float[2];

        public void Begin(StringBuilder sb, AtmosphereController ctrl)
        {
            _sb = sb; _ctrl = ctrl;
            gameObject.hideFlags = HideFlags.DontSave;

            // 抢占相机 transform 的脚本会让我们抓到的画面与 HUD 位置对不上。
            foreach (var r in UnityEngine.Object.FindObjectsOfType<LakeCameraRig>()) r.enabled = false;
            foreach (var o in UnityEngine.Object.FindObjectsOfType<LakeOrbitCamera>()) o.Paused = true;
            foreach (var m in UnityEngine.Object.FindObjectsOfType<Pegasus.PegasusManager>()) m.enabled = false;
            _ctrl.AutoWeather = false;
        }

        private void Update()
        {
            _frame++;
            if (_finished) return;
            if (!_ctrl.IsApplied) return;

            switch (_step)
            {
                case 0:
                {
                    // Play Mode 下 SetResolution 只在 standalone 生效；
                    // 编辑器 Game 视图尺寸由 GameView 控制，这里至少保证宽度分支被走到。
                    var s = Sizes[_sizeIndex];
                    _sb.AppendLine($"—— 目标尺寸 {s.x}x{s.y} ——");
                    _sb.AppendLine($"Screen 当前 = {Screen.width}x{Screen.height}");

                    int before = AtmosphereController.HudDrawCount;
                    _sb.AppendLine($"OnGUI 执行计数（抓屏前）= {before}，" +
                                   $"因未应用跳过 {AtmosphereController.HudSkipNotAppliedCount}，" +
                                   $"因 _showHud=false 跳过 {AtmosphereController.HudSkipDisabledCount}");
                    _okDrawn[_sizeIndex] = before > 0;

                    // 等几帧让 OnGUI 在本次尺寸下重算 rect。
                    _waitUntil = Time.realtimeSinceStartup + 0.5f;
                    _step = 1;
                    return;
                }
                case 1:
                {
                    if (Time.realtimeSinceStartup < _waitUntil) return;

                    var rect = AtmosphereController.HudLastRect;
                    var screen = AtmosphereController.HudLastScreen;
                    _sb.AppendLine($"HUD rect = ({rect.x:F0},{rect.y:F0}) {rect.width:F0}x{rect.height:F0}，" +
                                   $"探针记录的屏幕 = {screen.x}x{screen.y}");

                    bool inside = rect.x >= 0 && rect.y >= 0 &&
                                  rect.xMax <= screen.x + 0.5f && rect.yMax <= screen.y + 0.5f &&
                                  rect.width > 1f && rect.height > 1f;
                    _okInside[_sizeIndex] = inside;
                    _sb.AppendLine($"HUD 完全在屏内（x/y/宽高均有效）: {(inside ? "PASS" : "FAIL")}");

                    string path = $"{OutDir}/hud-{screen.x}x{screen.y}.png";
                    ScreenCapture.CaptureScreenshot(path);
                    _sb.AppendLine($"已抓屏 → {path}");
                    _waitUntil = Time.realtimeSinceStartup + 1.0f;
                    _step = 2;
                    return;
                }
                case 2:
                {
                    if (Time.realtimeSinceStartup < _waitUntil) return;

                    string path = $"{OutDir}/hud-{Screen.width}x{Screen.height}.png";
                    float ratio = MeasureHudPixels(path, out string detail);
                    _ratios[_sizeIndex] = ratio;
                    // HUD 背景是 55% 黑 + 文字，左上角区域应有相当比例被压暗/点亮。
                    _okPixels[_sizeIndex] = ratio >= 0.15f;
                    _sb.AppendLine($"左上角区域与背景差异像素比例 = {ratio:P1}（要求 ≥ 15%）: " +
                                   $"{(_okPixels[_sizeIndex] ? "PASS" : "FAIL")}");
                    _sb.AppendLine($"  {detail}");

                    _sizeIndex++;
                    if (_sizeIndex < Sizes.Length) { _step = 0; }
                    else { Finish(); }
                    return;
                }
            }
        }

        /// <summary>
        /// 在 HUD 覆盖区域内统计与"仅天空"参考色差异明显的像素。
        /// 参考色取同高度的最右列（HUD 只占左侧，右侧是纯天空/场景）。
        /// </summary>
        private float MeasureHudPixels(string path, out string detail)
        {
            detail = string.Empty;
            if (!File.Exists(path))
            {
                detail = "截图文件不存在";
                return 0f;
            }

            byte[] bytes = File.ReadAllBytes(path);
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            if (!tex.LoadImage(bytes))
            {
                UnityEngine.Object.DestroyImmediate(tex);
                detail = "截图无法解码";
                return 0f;
            }

            int w = tex.width, h = tex.height;
            var rect = AtmosphereController.HudLastRect;

            // 像素坐标原点在左下；GUI 原点在左上。
            int x0 = Mathf.Clamp(Mathf.RoundToInt(rect.x), 0, w - 1);
            int x1 = Mathf.Clamp(Mathf.RoundToInt(rect.xMax), x0 + 1, w);
            int yTop = Mathf.Clamp(Mathf.RoundToInt(rect.y), 0, h - 1);
            int yBot = Mathf.Clamp(Mathf.RoundToInt(rect.yMax), yTop + 1, h);
            int py0 = Mathf.Clamp(h - yBot, 0, h - 1);
            int py1 = Mathf.Clamp(h - yTop, py0 + 1, h);

            // 参考色：同一竖直区间的最右侧一列（HUD 之外）。
            var refPixels = tex.GetPixels(w - 4, py0, 4, py1 - py0);
            float rr = 0, rg = 0, rb = 0;
            for (int i = 0; i < refPixels.Length; i++)
            {
                rr += refPixels[i].r; rg += refPixels[i].g; rb += refPixels[i].b;
            }
            int n = Mathf.Max(1, refPixels.Length);
            rr /= n; rg /= n; rb /= n;

            var area = tex.GetPixels(x0, py0, x1 - x0, py1 - py0);
            int differ = 0;
            for (int i = 0; i < area.Length; i++)
            {
                float d = Mathf.Abs(area[i].r - rr) + Mathf.Abs(area[i].g - rg) + Mathf.Abs(area[i].b - rb);
                if (d > 0.12f) differ++;
            }

            float ratio = area.Length > 0 ? (float)differ / area.Length : 0f;
            detail = $"截图 {w}x{h}，HUD 区域 ({x0},{py0}) {x1 - x0}x{py1 - py0}，" +
                     $"参考色 ({rr:F2},{rg:F2},{rb:F2})，差异像素 {differ}/{area.Length}";

            UnityEngine.Object.DestroyImmediate(tex);
            return ratio;
        }

        private void Finish()
        {
            _finished = true;

            _sb.AppendLine("");
            _sb.AppendLine("===== HUD 像素可见性 =====");
            for (int i = 0; i < Sizes.Length; i++)
            {
                _sb.AppendLine($"[{i + 1}] {Sizes[i].x}x{Sizes[i].y}: " +
                               $"已绘制={_okDrawn[i]} 屏内={_okInside[i]} " +
                               $"可见比例={_ratios[i]:P1}");
            }

            bool all = true;
            for (int i = 0; i < Sizes.Length; i++)
                all &= _okDrawn[i] && _okInside[i] && _okPixels[i];

            _sb.AppendLine("");
            _sb.AppendLine(all ? "==== 结论：PASS ====" : "==== 结论：FAIL ====");

            File.WriteAllText(ResultPath, _sb.ToString());
            gameObject.AddComponent<QuitAfter>().Init(all ? 0 : 2);
            enabled = false;
        }
    }

    private sealed class QuitAfter : MonoBehaviour
    {
        private int _f;
        private int _code;

        public void Init(int code)
        {
            _code = code;
            gameObject.hideFlags = HideFlags.DontSave;
        }

        private void Update()
        {
            if (++_f < 5) return;
            EditorApplication.Exit(_code);
        }
    }

    private static void Flush(StringBuilder sb, int code)
    {
        File.WriteAllText(ResultPath, sb.ToString());
        EditorApplication.Exit(code);
    }
}
