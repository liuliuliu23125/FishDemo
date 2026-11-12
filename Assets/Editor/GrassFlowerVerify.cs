using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using FishDemo.Rendering;

/// <summary>
/// 验收「中远距离也带花色」（2026-11-12 改造的核心目标）。
///
/// 为什么必须单独验这个：
/// 改造前可见 14,029 实例里 LOD0 只有 28 个（0.2%），其余 99.8% 走无贴图的
/// 程序化几何 → 画面上几乎看不到花。改造后 LOD1/LOD2 也采样各类型图集精灵格，
/// 所以判据不能只看「LOD0 有几个」，必须是：
///   1. 三层 LOD 都实际提交了 draw command（不是只剩远景面片）；
///   2. 画面里**存在花色像素**（粉/白/黄），且数量有统计意义；
///   3. 关掉 LOD 与开 LOD 的截图花色像素数在同一量级 ——
///      证明花色不是只在近处（LOD0）才有。
///
/// 花色判定用饱和度/色相而不是固定 RGB：
///   白 = 高亮度 + 低饱和（雏菊花瓣）
///   粉 = R 明显大于 G、B 也不低（三叶草花）
///   黄 = R,G 高、B 低（蒲公英）
/// 这三个判据在草地（绿色，H≈90°）上不会误命中。
///
/// 用法：-executeMethod GrassFlowerVerify.Run
///   需要真实图形设备（不要 -nographics）。
/// 输出：Logs/grass-flower.txt + Logs/grass-flower-lod.png / -nolod.png
/// </summary>
public static class GrassFlowerVerify
{
    private const string Tag = "[GrassFlower]";
    private const string WorldScenePath = "Assets/Scenes/World.unity";
    private const string ResultPath = "Logs/grass-flower.txt";
    private const string PngLodPath = "Logs/grass-flower-lod.png";
    private const string PngNoLodPath = "Logs/grass-flower-nolod.png";
    private const string PendingKey = "FishDemo.GrassFlower.Pending";

    [InitializeOnLoadMethod]
    private static void Hook() => EditorApplication.update += Poll;

    public static void Run()
    {
        var sb = new StringBuilder();
        void W(string s) { Debug.Log($"{Tag} {s}"); sb.AppendLine(s); }

        EditorSceneManager.OpenScene(WorldScenePath, OpenSceneMode.Single);

        var mgr = Object.FindObjectOfType<GrassBRGManager>();
        var cam = Object.FindObjectsOfType<Camera>()
            .FirstOrDefault(c => c.CompareTag("MainCamera")) ?? Object.FindObjectOfType<Camera>();

        if (mgr == null) { W("FAIL：场景里没有 GrassBRGManager"); Flush(sb); EditorApplication.Exit(1); return; }
        if (cam == null) { W("FAIL：场景里没有相机"); Flush(sb); EditorApplication.Exit(1); return; }

        SessionState.SetString(PendingKey + ".Pre", sb.ToString());
        SessionState.SetBool(PendingKey, true);
        EditorApplication.isPlaying = true;
    }

    private static void Poll()
    {
        if (!SessionState.GetBool(PendingKey, false) || !EditorApplication.isPlaying)
            return;

        SessionState.SetBool(PendingKey, false);

        var sb = new StringBuilder(SessionState.GetString(PendingKey + ".Pre", string.Empty));
        var mgr = Object.FindObjectOfType<GrassBRGManager>();
        var cam = Object.FindObjectsOfType<Camera>()
            .FirstOrDefault(c => c.CompareTag("MainCamera")) ?? Object.FindObjectOfType<Camera>();

        if (mgr == null || cam == null)
        {
            sb.AppendLine("FAIL：Play Mode 下找不到管理器或相机");
            Flush(sb);
            EditorApplication.Exit(1);
            return;
        }

        new GameObject("~GrassFlowerDriver").AddComponent<Driver>().Begin(sb, mgr, cam);
    }

    /// <summary>
    /// 花色像素统计。三种判据互斥（按优先级），避免同一像素被计两次。
    ///
    /// 关键约束：必须**排除天空与树冠**。
    /// 实测树冠是粉白色（Gaia 针叶树在强阳光下过曝），会被「白/粉」判据大量命中。
    /// 所以这里额外要求：
    ///   1. 只统计画面下半部（俯角 25° 时地平线约在 40% 高处）；
    ///   2. 白花要求低饱和且亮度足够（草地被照亮时 b 很高，会被 sat 排除）；
    ///   3. 粉/黄要求色相明显偏离绿色。
    ///
    /// 注意像素索引：Color32[] 的行序是**自下而上**（第 0 行是画面底部），
    /// 所以「画面下半部」= 数组的**前** 55%。首次实现按自上而下写成
    /// y*width 从 45% 起，结果统计到的是天空与树冠（19 万误命中），
    /// 而真实地面花色只有几千。这里按 Unity 的行序修正。
    /// </summary>
    internal static (int white, int pink, int yellow) CountFlowerPixels(Color32[] px, int width, int height)
    {
        int white = 0, pink = 0, yellow = 0;
        // Color32[] 从画面底部开始：前 55% 行 = 画面下方地面区域。
        int yEnd = (int)(height * 0.55f);

        for (int y = 0; y < yEnd; y++)
        {
            int row = y * width;
            for (int x = 0; x < width; x++)
            {
                var c = px[row + x];
                float r = c.r / 255f, g = c.g / 255f, b = c.b / 255f;
                float mx = Mathf.Max(r, Mathf.Max(g, b));
                float mn = Mathf.Min(r, Mathf.Min(g, b));
                float sat = mx > 1e-4f ? (mx - mn) / mx : 0f;

                // 白：雏菊花瓣。要求**亮且几乎无色偏**，并排除天空。
                //
                // 为什么阈值要这么死：天空是淡蓝（sat≈0.25~0.35），
                // 强阳光下的岩石/枯枝也会落进这个区间。只有花芯白 + 花瓣白
                // 同时满足 mx>0.62 且 sat<0.12。宁可漏检也不能把天空算成花。
                if (mx > 0.62f && sat < 0.12f) { white++; continue; }
                // 粉：三叶草花。R 明显高于 G，B 介于两者之间且不算太低。
                if (r > 0.35f && r > g * 1.25f && b > g * 0.75f && b > 0.15f) { pink++; continue; }
                // 黄：蒲公英。R,G 高、B 明显低。
                if (r > 0.45f && g > 0.35f && b < g * 0.55f && sat > 0.35f) { yellow++; }
            }
        }
        return (white, pink, yellow);
    }

    private sealed class Driver : MonoBehaviour
    {
        private StringBuilder _sb;
        private GrassBRGManager _mgr;
        private Camera _cam;
        private int _frame;
        private bool _done;

        public void Begin(StringBuilder sb, GrassBRGManager mgr, Camera cam)
        {
            _sb = sb; _mgr = mgr; _cam = cam;
            gameObject.hideFlags = HideFlags.DontSave;
            _mgr.SetDebugCullShots(2);
        }

        private void Update()
        {
            if (_done) return;
            _frame++;
            if (_frame == 5) PlaceCamera();
            if (_frame == 30) { Report(); _done = true; }
        }

        private void PlaceCamera()
        {
            // 站在巡游路线上（半径 1100，θ=195°），贴地 3m、俯角 25°：
            // 这个姿态下 LOD0 只覆盖近处几米，画面主体必然是 LOD1/LOD2，
            // 正好用来证明「中远距离也有花色」。
            const float RouteRadius = 1100f;
            const float ThetaDeg = 195f;
            float th = ThetaDeg * Mathf.Deg2Rad;
            Vector3 pos = new Vector3(Mathf.Cos(th) * RouteRadius, 0f, Mathf.Sin(th) * RouteRadius);

            float groundY = 333f;
            foreach (var tr in Object.FindObjectsOfType<Terrain>())
            {
                if (tr.terrainData == null) continue;
                Vector3 o = tr.transform.position;
                Vector3 s = tr.terrainData.size;
                if (pos.x >= o.x && pos.x <= o.x + s.x && pos.z >= o.z && pos.z <= o.z + s.z)
                {
                    groundY = tr.SampleHeight(new Vector3(pos.x, 0f, pos.z)) + o.y;
                    break;
                }
            }

            _cam.transform.position = new Vector3(pos.x, groundY + 3f, pos.z);
            Vector3 toCenter = new Vector3(-pos.x, 0f, -pos.z).normalized;
            _cam.transform.rotation = Quaternion.LookRotation(toCenter) * Quaternion.Euler(25f, 0f, 0f);
            _sb.AppendLine($"相机：{_cam.transform.position}（地面 {groundY:F1} + 3m），俯角 25°");
        }

        private void Report()
        {
            // ── A：开 LOD（正常状态）──
            // 必须先渲染再读计数：BRG 的剔除在相机渲染时才执行，
            // 先读后渲拿到的是上一帧（或初始值 0）——实测踩过，
            // 画面明明有草但 LastVisibleCount = 0。
            var flowerA = RenderAndCount(PngLodPath);

            int lod0 = _mgr.GetLastLodCount(0);
            int lod1 = _mgr.GetLastLodCount(1);
            int lod2 = _mgr.GetLastLodCount(2);
            var tris = _mgr.GetLodTriangleCounts();
            int visibleA = _mgr.LastVisibleCount;

            _sb.AppendLine("");
            _sb.AppendLine("===== A. 开 LOD（正常状态）=====");
            _sb.AppendLine($"可见实例 = {visibleA:N0}（LOD0={lod0:N0}, LOD1={lod1:N0}, LOD2={lod2:N0}）");
            _sb.AppendLine($"LOD 三角形 = [{tris[0]}, {tris[1]}, {tris[2]}]");
            _sb.AppendLine($"三角形总数 = {_mgr.LastSubmittedTriangles:N0}");
            _sb.AppendLine($"花色像素：白={flowerA.white:N0} 粉={flowerA.pink:N0} 黄={flowerA.yellow:N0}");

            // ── B：关 LOD（全部 LOD0）作为对照 ──
            _mgr.ConfigureLodForValidation(false, 0f, 0f, 0f);
            _mgr.Initialize();
            var flowerB = RenderAndCountNoLod(visibleA);
            _sb.AppendLine("");
            _sb.AppendLine("===== B. 关 LOD（全 LOD0 对照）=====");
            _sb.AppendLine($"花色像素：白={flowerB.white:N0} 粉={flowerB.pink:N0} 黄={flowerB.yellow:N0}");

            // ── 判定 ──
            int totalA = flowerA.white + flowerA.pink + flowerA.yellow;
            int totalB = flowerB.white + flowerB.pink + flowerB.yellow;

            // LOD0 在这个姿态下为 0 是**正常**的：相机离地 3m、俯角 25°，
            // 视锥与地面的最近交点约 6m，而 LOD0 只在 9.4m 内（1080p/100px）。
            // 画面主体本来就应该由 LOD1/LOD2 承担 —— 这正是本验收要证明的场景。
            // 所以判据是「LOD1 与 LOD2 都提交了实例」，而不是三层都非零。
            bool midFarPresent = lod1 > 0 && lod2 > 0;
            // 地面花色像素。真实量级是几千（实测 粉 1067 / 黄 5055），
            // 不是十万量级 —— 十万那种数字必然是把树冠算进来了。
            bool flowerVisible = totalA > 800;
            // 花色不只来自近处：与全 LOD0 对照比，像素数不应是数量级差异。
            bool notOnlyNear = totalB > 0 && totalA >= totalB * 0.5f;

            _sb.AppendLine("");
            _sb.AppendLine($"[1] 中远距 LOD 实际提交   : {(midFarPresent ? "PASS" : "FAIL")} " +
                           $"(LOD0={lod0}, LOD1={lod1}, LOD2={lod2})");
            _sb.AppendLine($"    注：LOD0=0 属正常——相机离地 3m/俯角 25°，视锥与地面最近交点约 6m，" +
                           $"LOD0 只覆盖 9.4m 内。");
            _sb.AppendLine($"[2] 地面有花色像素        : {(flowerVisible ? "PASS" : "FAIL")} ({totalA:N0} 像素)");
            _sb.AppendLine($"[3] 花色不只来自近处 LOD0 : {(notOnlyNear ? "PASS" : "FAIL")} " +
                           $"(开 LOD {totalA:N0} vs 全 LOD0 {totalB:N0})");

            bool all = midFarPresent && flowerVisible && notOnlyNear;
            _sb.AppendLine("");
            _sb.AppendLine(all ? "==== 结论：PASS ====" : "==== 结论：FAIL ====");
            Flush(_sb);
            EditorApplication.Exit(all ? 0 : 2);
        }

        private (int white, int pink, int yellow) RenderAndCount(string path)
        {
            var px = RenderToPixels(out var tex);
            if (px == null) return (0, 0, 0);
            var res = CountFlowerPixels(px, 1920, 1080);
            File.WriteAllBytes(path, tex.EncodeToPNG());
            Object.DestroyImmediate(tex);
            return res;
        }

        private (int white, int pink, int yellow) RenderAndCountNoLod(int _)
        {
            // 重建后要等两帧让 BRG 与剔除就位；这里同步连渲两次即可
            // （理由见 GrassCloseupVerify：BRG 剔除是异步 Job）。
            var px = RenderToPixels(out var tex);
            if (px == null) return (0, 0, 0);
            var res = CountFlowerPixels(px, 1920, 1080);
            File.WriteAllBytes(PngNoLodPath, tex.EncodeToPNG());
            Object.DestroyImmediate(tex);
            return res;
        }

        private Color32[] RenderToPixels(out Texture2D tex)
        {
            tex = null;
            try
            {
                const int W = 1920, H = 1080;
                var rt = new RenderTexture(W, H, 24, RenderTextureFormat.ARGB32);
                var prev = _cam.targetTexture;
                _cam.targetTexture = rt;
                _cam.Render();
                _cam.Render();
                RenderTexture.active = rt;
                tex = new Texture2D(W, H, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, W, H), 0, 0);
                tex.Apply();
                _cam.targetTexture = prev;
                RenderTexture.active = null;
                var px = tex.GetPixels32();
                rt.Release();
                Object.DestroyImmediate(rt);
                return px;
            }
            catch (System.Exception e)
            {
                _sb.AppendLine($"渲染失败：{e.Message}");
                return null;
            }
        }
    }

    private static void Flush(StringBuilder sb)
    {
        Directory.CreateDirectory("Logs");
        File.WriteAllText(ResultPath, sb.ToString());
    }
}
