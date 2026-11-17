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
public static class LakeWaterVerify
{
    private const string Tag = "[LakeWater]";
    private const string WorldScenePath = "Assets/Scenes/World.unity";
    private const string OutDir = "Logs/Lake";
    private const string ResultPath = OutDir + "/lake-water.txt";
    private const string PendingKey = "FishDemo.LakeWater.Pending";

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
            foreach (var m in UnityEngine.Object.FindObjectsOfType<Pegasus.PegasusManager>())
            {
                m.PauseFlythrough();
                m.enabled = false;
                _sb.AppendLine($"已暂停并禁用 PegasusManager：{m.name}");
            }
            foreach (var g in UnityEngine.Object.FindObjectsOfType<FishDemo.Rendering.GrassBRGManager>())
            {
                g.gameObject.SetActive(false);
                _sb.AppendLine($"禁用草 BRG：{g.name}");
            }

            int enviroOff = 0;
            foreach (var em in UnityEngine.Object.FindObjectsOfType<Enviro.EnviroManager>())
            {
                em.gameObject.SetActive(false);
                enviroOff++;
            }
            foreach (var atmo in UnityEngine.Object.FindObjectsOfType<FishDemo.Gameplay.AtmosphereController>())
                atmo.enabled = false;
            var legacyLightGo = GameObject.Find("Directional Light");
            if (legacyLightGo != null)
            {
                var legacyLight = legacyLightGo.GetComponent<Light>();
                if (legacyLight != null)
                {
                    legacyLight.enabled = true;
                    legacyLight.color = Color.white;
                    legacyLight.intensity = 1.2f;
                }
            }
            var defaultSky = AssetDatabase.GetBuiltinExtraResource<Material>("Default-Skybox.mat");
            if (defaultSky != null) RenderSettings.skybox = defaultSky;
            _sb.AppendLine($"已禁用 Enviro {enviroOff} 个，恢复旧主光/默认天空盒（隔离 Crest 水下测试）");

            RenderSettings.ambientMode = AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.42f, 0.47f, 0.55f);
            RenderSettings.ambientIntensity = 1f;
            _sb.AppendLine("环境光 = Flat(0.42,0.47,0.55)");
        }

        private void Update()
        {
            _frame++;

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

            var standA = new Vector3(cx, wl + 6f, cz + (R - 55f));
            var lookA = new Vector3(cx, wl - 10f, cz + (R - 105f));
            PlaceCam(cam, standA, lookA);
            var a = Capture(cam, "shallow-above", WarmupFrames);

            var standB = new Vector3(standA.x, wl - 5f, standA.z);
            PlaceCam(cam, standB, lookA);
            var b = Capture(cam, "underwater", WarmupFrames);

            var underwater = _water.Underwater;
            bool uwWasEnabled = underwater.Enabled;
            underwater.Enabled = false;
            var b0 = Capture(cam, "underwater-off", WarmupFrames);
            underwater.Enabled = uwWasEnabled;
            _sb.AppendLine($"水下效果对照组：Enabled {uwWasEnabled} → false → {uwWasEnabled}");

            var standC = new Vector3(cx, wl + 26f, cz + 300f);
            PlaceCam(cam, standC, new Vector3(cx, wl, cz));
            var c = Capture(cam, "orbit-view", WarmupFrames);

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

            float tintB = (statsB.UpperMeanG + statsB.UpperMeanB) * 0.5f / Mathf.Max(1f, statsB.UpperMeanR);
            float tintB0 = (statsB0.UpperMeanG + statsB0.UpperMeanB) * 0.5f / Mathf.Max(1f, statsB0.UpperMeanR);
            float tintC = (statsC.UpperMeanG + statsC.UpperMeanB) * 0.5f / Mathf.Max(1f, statsC.UpperMeanR);
            _sb.AppendLine($"水下调色比 (G+B)/2/R : B开={tintB:F2}, B关={tintB0:F2}, C={tintC:F2}（取画面上半部）");

            _sb.AppendLine("");
            _sb.AppendLine("===== 水体表现验收 =====");

            bool c1 = statsA.StdLuma >= 18f && statsA.Distinct > 2000;
            _sb.AppendLine($"[1] 水面透明（浅水见底）: {(c1 ? "PASS" : "FAIL")} " +
                           $"(标准差 {statsA.StdLuma:F1} ≥ 18, 色种 {statsA.Distinct} > 2000)");

            bool c2 = (tintB - tintB0) >= 0.10f && tintB >= 1.05f;
            _sb.AppendLine($"[2] 水下呈吸收色调     : {(c2 ? "PASS" : "FAIL")} " +
                           $"(开/关差 {tintB - tintB0:F3} ≥ 0.10, 绝对值 {tintB:F2} ≥ 1.05)");

            bool c3 = statsB.Distinct > 800 && statsB.MeanLuma >= 6f;
            _sb.AppendLine($"[3] 水下画面有内容     : {(c3 ? "PASS" : "FAIL")} " +
                           $"(色种 {statsB.Distinct} > 800, 亮度 {statsB.MeanLuma:F1} ≥ 6)");

            float lumaDelta = Mathf.Abs(statsB.MeanLuma - statsB0.MeanLuma);
            float tintOn = (statsB.UpperMeanG + statsB.UpperMeanB) * 0.5f / Mathf.Max(1f, statsB.UpperMeanR);
            float tintOff = (statsB0.UpperMeanG + statsB0.UpperMeanB) * 0.5f / Mathf.Max(1f, statsB0.UpperMeanR);
            float tintDelta = Mathf.Abs(tintOn - tintOff);
            bool c4 = lumaDelta >= 10f || tintDelta >= 0.10f;
            _sb.AppendLine($"[4] 水下效果确实生效   : {(c4 ? "PASS" : "FAIL")} " +
                           $"(亮度差 {lumaDelta:F1} ≥ 10 或 调色比差 {tintDelta:F3} ≥ 0.10)");

            bool c5 = tintC < 1.15f;
            _sb.AppendLine($"[5] 水上未误开水下效果 : {(c5 ? "PASS" : "FAIL")} (比值 {tintC:F2} < 1.15)");

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
            public float UpperMeanR, UpperMeanG, UpperMeanB;
            public int Distinct;
        }

        private static Stats Analyze(Shot shot)
        {
            var px = shot.Px;
            int W = shot.W, H = shot.H;
            int yStart = (int)(H * 0.15f);
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
