using System;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using WaveHarmonic.Crest;

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

            foreach (var tour in UnityEngine.Object.FindObjectsOfType<FishDemo.Gameplay.PegasusTourController>())
            {
                tour.enabled = false;
                _sb.AppendLine($"已禁用 Pegasus 巡游：{tour.name}");
            }

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

            if (!_teleported)
            {
                _teleported = true;
                var c = new Vector3(LakeCarve.Center.x, 0f, LakeCarve.Center.y);
                _cam.transform.position = new Vector3(c.x, LakeCarve.WaterLevel + 30f, c.z - 150f);
                return;
            }

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
            Vector3 stand = new Vector3(center.x, LakeCarve.WaterLevel + 30f,
                                        center.z - 150f);
            _cam.transform.position = stand;
            _cam.transform.rotation = Quaternion.LookRotation(
                new Vector3(center.x - stand.x, (LakeCarve.WaterLevel - stand.y), center.z - stand.z).normalized);
            _cam.fieldOfView = 60f;
            _cam.nearClipPlane = 0.1f;
            _cam.farClipPlane = 3000f;

            if (_frame < _readyFrame + 60)
            {
                if (_readyFrame == 0) _readyFrame = _frame;
                return;
            }
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

            var abs = Shader.GetGlobalVector("_Crest_Absorption");
            var sca = Shader.GetGlobalVector("_Crest_Scattering");
            var center = Shader.GetGlobalVector("g_Crest_WaterCenter");
            _sb.AppendLine($"全局 _Crest_Absorption = ({abs.x:F3},{abs.y:F3},{abs.z:F3},{abs.w:F3})");
            _sb.AppendLine($"全局 _Crest_Scattering = ({sca.x:F3},{sca.y:F3},{sca.z:F3},{sca.w:F3})");
            _sb.AppendLine($"全局 g_Crest_WaterCenter = ({center.x:F1},{center.y:F1},{center.z:F1})");

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
            gameObject.AddComponent<QuitAfter>().Init();
        }

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
