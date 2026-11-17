using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using FishDemo.Rendering;

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
        GrassBRGManager.RequestFullLoad();
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

    internal static (int white, int pink, int yellow) CountFlowerPixels(Color32[] px, int width, int height)
    {
        int white = 0, pink = 0, yellow = 0;
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

                if (mx > 0.62f && sat < 0.12f) { white++; continue; }
                if (r > 0.35f && r > g * 1.25f && b > g * 0.75f && b > 0.15f) { pink++; continue; }
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

            _mgr.ConfigureLodForValidation(false, 0f, 0f, 0f);
            _mgr.Initialize();
            var flowerB = RenderAndCountNoLod(visibleA);
            _sb.AppendLine("");
            _sb.AppendLine("===== B. 关 LOD（全 LOD0 对照）=====");
            _sb.AppendLine($"花色像素：白={flowerB.white:N0} 粉={flowerB.pink:N0} 黄={flowerB.yellow:N0}");

            int totalA = flowerA.white + flowerA.pink + flowerA.yellow;
            int totalB = flowerB.white + flowerB.pink + flowerB.yellow;

            bool midFarPresent = lod1 > 0 && lod2 > 0;
            bool flowerVisible = totalA > 800;
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
