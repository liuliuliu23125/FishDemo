using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Enviro;
using FishDemo.Gameplay;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

[InitializeOnLoad]
public static class LightingVerify
{
    private const string Tag = "[LightingVerify]";
    private const string WorldScenePath = "Assets/Scenes/World.unity";
    private const string OutDir = "Logs/Lighting";
    private const string ResultPath = OutDir + "/lighting-verify.txt";
    private const string PendingKey = "FishDemo.LightingVerify.Pending";

    static LightingVerify()
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
            ChunkLoadScope.LoadRelevant(ChunkLoadScope.Area.FromLake(), W);
            EditorSceneManager.SetActiveScene(EditorSceneManager.GetSceneByPath(WorldScenePath));

            foreach (var g in UnityEngine.Object.FindObjectsOfType<FishDemo.Rendering.GrassBRGManager>())
            {
                g.gameObject.SetActive(false);
                W($"禁用草 BRG：{g.name}");
            }

            if (UnityEngine.Object.FindObjectOfType<TorchRingController>() == null)
            {
                W("FAIL：场景里没有 TorchRingController。先跑 LightingSetup.Apply");
                Flush(sb, 2);
                return;
            }
            if (UnityEngine.Object.FindObjectOfType<AtmosphereController>() == null)
            {
                W("FAIL：场景里没有 AtmosphereController。先跑 AtmosphereSetup.Apply");
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
        var ring = UnityEngine.Object.FindObjectOfType<TorchRingController>();
        var ctrl = UnityEngine.Object.FindObjectOfType<AtmosphereController>();
        var manager = UnityEngine.Object.FindObjectOfType<EnviroManager>();
        var cam = UnityEngine.Object.FindObjectsOfType<Camera>()
                      .FirstOrDefault(c => c.CompareTag("MainCamera"))
                  ?? UnityEngine.Object.FindObjectOfType<Camera>();

        if (ring == null || ctrl == null || manager == null || cam == null)
        {
            sb.AppendLine($"FAIL：Play Mode 下缺对象 ring={ring != null} ctrl={ctrl != null} " +
                          $"enviro={manager != null} cam={cam != null}");
            Flush(sb, 2);
            return;
        }

        new GameObject("~LightingVerify").AddComponent<Driver>().Begin(sb, ring, ctrl, manager, cam);
    }

    private sealed class Driver : MonoBehaviour
    {
        private const int W = 1280;
        private const int H = 720;

        private StringBuilder _sb;
        private TorchRingController _ring;
        private AtmosphereController _ctrl;
        private EnviroManager _manager;
        private Camera _cam;
        private int _frame;
        private bool _ready;
        private int _readyFrame;
        private bool _started;
        private int _step;
        private float _waitUntil;
        private int _torchOnWarm;
        private int _torchOffWarm;
        private float _torchOnLum;
        private float _torchOffLum;
        private string _night0Path;
        private string _night4Path;
        private string _torchOnPath;
        private string _torchOffPath;
        private string _dayPath;
        private string _dayOrbitPath;
        private float _nightExposure;
        private float _dayExposure;

        public void Begin(StringBuilder sb, TorchRingController ring, AtmosphereController ctrl,
                          EnviroManager manager, Camera cam)
        {
            _sb = sb;
            _ring = ring;
            _ctrl = ctrl;
            _manager = manager;
            _cam = cam;
            gameObject.hideFlags = HideFlags.DontSave;

            foreach (var c in UnityEngine.Object.FindObjectsOfType<PegasusTourController>())
            {
                c.enabled = false;
                _sb.AppendLine($"禁用 Pegasus 巡游：{c.name}");
            }
            foreach (var c in UnityEngine.Object.FindObjectsOfType<LakeOrbitCamera>())
            {
                c.Paused = true;
                _sb.AppendLine($"暂停环湖轨道：{c.name}");
            }
            foreach (var c in UnityEngine.Object.FindObjectsOfType<LakeCameraRig>())
            {
                c.enabled = false;
                _sb.AppendLine($"禁用相机 rig：{c.name}");
            }
            foreach (var g in UnityEngine.Object.FindObjectsOfType<FishDemo.Rendering.GrassBRGManager>())
            {
                g.gameObject.SetActive(false);
                _sb.AppendLine($"禁用草 BRG：{g.name}");
            }

            _ctrl.AutoWeather = false;
            _ctrl.ShowHud = false;
            _sb.AppendLine("关闭自动天气与 HUD（截图可复现）");
        }

        private void Update()
        {
            _frame++;
            if (!_ready)
            {
                if (!_ctrl.IsApplied)
                {
                    if (_frame > 1200)
                    {
                        _sb.AppendLine("FAIL：AtmosphereController 未应用");
                        Finish(false);
                    }
                    return;
                }
                if (_frame < 45) return;
                _ready = true;
                _readyFrame = _frame;
                RefreshGtsMeshes();
                _sb.AppendLine($"就绪：相机 = {_cam.name}，火把 = {_ring.TorchCount}，上限 = {TorchRingController.HardLightLimit}");
                return;
            }

            if (!_started)
            {
                _started = true;
                _step = 0;
                _waitUntil = 0f;
                return;
            }

            if (Time.realtimeSinceStartup < _waitUntil) return;

            switch (_step)
            {
                case 0:
                    SetupNight();
                    _ring.SetMaxActiveLights(0);
                    _ring.ApplyImmediate();
                    _sb.AppendLine("");
                    _sb.AppendLine("── 夜晚 / 0 盏 ──");
                    Next(2.5f);
                    break;
                case 1:
                    _night0Path = Shoot("lighting-night-0");
                    _nightExposure = ReadPostExposure();
                    _sb.AppendLine($"  夜景曝光 postExposure = {_nightExposure:F2} EV");
                    PoseOrbit();
                    _ring.SetMaxActiveLights(4);
                    _ring.ApplyImmediate();
                    _sb.AppendLine($"── 夜晚 / 4 盏：ActiveTarget={_ring.ActiveTargetCount} ──");
                    Next(2.5f);
                    break;
                case 2:
                    _night4Path = Shoot("lighting-night-4");
                    PoseTorch();
                    _ring.SetMaxActiveLights(1);
                    _ring.ApplyImmediate();
                    _sb.AppendLine($"── 火把近景 / 开：ActiveTarget={_ring.ActiveTargetCount} ──");
                    Next(3f);
                    break;
                case 3:
                    _torchOnPath = Shoot("lighting-torch-on");
                    _torchOnWarm = CountWarm(_lastPixels);
                    _torchOnLum = CenterLum(_lastPixels);
                    _sb.AppendLine($"  近景暖色像素 = {_torchOnWarm}，中心亮度 = {_torchOnLum:F4}");
                    _ring.SetMaxActiveLights(0);
                    _ring.ApplyImmediate();
                    _sb.AppendLine("── 火把近景 / 关 ──");
                    Next(3.2f);
                    break;
                case 4:
                    _torchOffPath = Shoot("lighting-torch-off");
                    _torchOffWarm = CountWarm(_lastPixels);
                    _torchOffLum = CenterLum(_lastPixels);
                    _sb.AppendLine($"  近景暖色像素 = {_torchOffWarm}，中心亮度 = {_torchOffLum:F4}");
                    SetupDay();
                    PoseTorch();
                    _ring.SetMaxActiveLights(1);
                    _ring.ApplyImmediate();
                    _sb.AppendLine("── 白天 / 火把近景 ──");
                    Next(3f);
                    break;
                case 5:
                    _dayPath = Shoot("lighting-day-torch");
                    SetupDay();
                    PoseOrbit();
                    _ring.SetMaxActiveLights(4);
                    _ring.ApplyImmediate();
                    _sb.AppendLine("── 白天 / 环湖 4 盏 ──");
                    Next(3f);
                    break;
                case 6:
                    _dayExposure = ReadPostExposure();
                    _sb.AppendLine($"  白天曝光 postExposure = {_dayExposure:F2} EV");
                    _dayOrbitPath = Shoot("lighting-day-orbit");
                    Finish(true);
                    break;
            }
        }

        private void Next(float seconds)
        {
            _waitUntil = Time.realtimeSinceStartup + seconds;
            _step++;
        }

        private void SetupNight()
        {
            _ctrl.SetTimeOfDay(0f, true);
            _ctrl.SetWeatherImmediate("Clear Sky");
            PoseOrbit();
        }

        private void SetupDay()
        {
            _ctrl.SetTimeOfDay(12f, true);
            _ctrl.SetWeatherImmediate("Clear Sky");
        }

        private void PoseOrbit()
        {
            var center = new Vector3(LakeCarve.Center.x, LakeCarve.WaterLevel, LakeCarve.Center.y);
            _cam.transform.position = new Vector3(center.x, center.y + 26f, center.z - 300f);
            _cam.transform.rotation = Quaternion.LookRotation((center - _cam.transform.position).normalized);
            _cam.fieldOfView = 60f;
            _cam.nearClipPlane = 0.1f;
            _cam.farClipPlane = 4000f;
        }

        private void PoseTorch()
        {
            var torch = _ring.Torches != null && _ring.Torches.Length > 0 ? _ring.Torches[0] : null;
            if (torch == null) return;
            var p = torch.transform.position;
            var inward = new Vector3(LakeCarve.Center.x - p.x, 0f, LakeCarve.Center.y - p.z).normalized;
            _cam.transform.position = p + inward * 18f + Vector3.up * 6f;
            var lookAt = p + Vector3.up * 6.5f;
            _cam.transform.rotation = Quaternion.LookRotation((lookAt - _cam.transform.position).normalized);
            _cam.fieldOfView = 60f;
            _cam.nearClipPlane = 0.1f;
            _cam.farClipPlane = 4000f;
        }

        private Color32[] _lastPixels;

        private string Shoot(string name)
        {
            var rt = new RenderTexture(W, H, 24, RenderTextureFormat.ARGB32);
            var prev = _cam.targetTexture;
            _cam.targetTexture = rt;

            for (int i = 0; i < 12; i++) _cam.Render();

            RenderTexture.active = rt;
            var tex = new Texture2D(W, H, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, W, H), 0, 0);
            tex.Apply();
            _cam.targetTexture = prev;
            RenderTexture.active = null;

            _lastPixels = tex.GetPixels32();
            string png = $"{OutDir}/{name}.png";
            File.WriteAllBytes(png, tex.EncodeToPNG());

            int lights = CountEnabledTorchLights();
            _sb.AppendLine($"截图：{png}  启用火把点光 = {lights}（请求 {_ring.MaxActiveLights}）");

            DestroyImmediate(tex);
            rt.Release();
            DestroyImmediate(rt);
            return png;
        }

        private int CountWarm(Color32[] px)
        {
            int n = 0;
            for (int i = 0; i < px.Length; i++)
            {
                var c = px[i];
                float r = c.r / 255f, g = c.g / 255f, b = c.b / 255f;
                if (r > 0.5f && r > g * 1.12f && g > b * 1.15f && r + g + b > 0.7f)
                    n++;
            }
            return n;
        }

        private static bool ReadBloom(out BloomDownscaleMode downscale, out int iterations)
        {
            downscale = BloomDownscaleMode.Half;
            iterations = 0;
            var go = GameObject.Find("Post Processing");
            var volume = go != null ? go.GetComponent<Volume>() : null;
            if (volume == null) return false;
            var profile = volume.profile;
            if (profile == null || !profile.TryGet<Bloom>(out var bloom)) return false;
            downscale = bloom.downscale.value;
            iterations = bloom.maxIterations.value;
            return true;
        }

        private static float ReadPostExposure()
        {
            var go = GameObject.Find("Post Processing");
            var volume = go != null ? go.GetComponent<Volume>() : null;
            if (volume == null) return float.NaN;
            var profile = volume.profile;
            if (profile == null || !profile.TryGet<ColorAdjustments>(out var c)) return float.NaN;
            return c.postExposure.value;
        }

        private static float CenterLum(Color32[] px)
        {
            int x0 = Mathf.RoundToInt(W * 0.45f), x1 = Mathf.RoundToInt(W * 0.55f);
            int y0 = Mathf.RoundToInt(H * 0.45f), y1 = Mathf.RoundToInt(H * 0.65f);
            double sum = 0;
            int n = 0;
            for (int y = y0; y < y1; y++)
            {
                for (int x = x0; x < x1; x++)
                {
                    var c = px[y * W + x];
                    sum += (0.2126 * c.r + 0.7152 * c.g + 0.0722 * c.b) / 255.0;
                    n++;
                }
            }
            return n > 0 ? (float)(sum / n) : 0f;
        }

        private int CountEnabledTorchLights()
        {
            var root = _ring.gameObject;
            return root.GetComponentsInChildren<Light>(true).Count(l => l.type == LightType.Point && l.enabled);
        }

        private void RefreshGtsMeshes()
        {
            int n = 0;
            foreach (var gts in UnityEngine.Object.FindObjectsOfType<ProceduralWorlds.GTS.GTSMesh>())
            {
                try { gts.Refresh(); n++; } catch { }
            }
            _sb.AppendLine($"Refresh GTSMesh = {n}");
        }

        private void Finish(bool shotsOk)
        {
            enabled = false;

            _sb.AppendLine();
            _sb.AppendLine("===== 状态断言 =====");

            var volumeRoot = GameObject.Find("Post Processing");
            var volume = volumeRoot != null ? volumeRoot.GetComponent<Volume>() : null;
            var profile = volume != null ? volume.sharedProfile : null;
            bool volumeOk = volume != null && volume.isGlobal && profile != null;
            bool aces = profile != null && profile.TryGet<Tonemapping>(out var tm) &&
                        tm.mode.value == TonemappingMode.ACES;
            bool bloom = profile != null && profile.TryGet<Bloom>(out var bl) &&
                         bl.IsActive() && bl.intensity.value > 0f;
            bool noDof = profile == null || !profile.TryGet<DepthOfField>(out var df) || !df.IsActive();
            bool grading = profile != null && profile.TryGet<ColorAdjustments>(out var ca) &&
                           ca.IsActive();
            var urp = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
            bool hdrGrading = urp != null && urp.colorGradingMode == ColorGradingMode.HighDynamicRange;

            var cams = UnityEngine.Object.FindObjectsOfType<Camera>(true);
            int postCameras = 0;
            foreach (var c in cams)
            {
                var d = c.GetComponent<UniversalAdditionalCameraData>();
                if (d != null && d.renderPostProcessing) postCameras++;
            }

            _ring.SetMaxActiveLights(99);
            bool clamp = _ring.MaxActiveLights == TorchRingController.HardLightLimit;
            _ring.SetMaxActiveLights(4);

            bool torchCount = _ring.TorchCount == 12;
            bool lightOn = _torchOnLum > _torchOffLum * 1.4f + 0.005f && _torchOnLum > 0.02f;
            bool limitOk = TorchRingController.HardLightLimit <= 6;
            bool nightExposure = _nightExposure > _dayExposure + 1.5f &&
                                 _nightExposure > 2f && _dayExposure < 1f;

            _ctrl.SetBloomQuality(AtmosphereController.BloomQuality.Half);
            bool bloomHalf = ReadBloom(out var halfDownscale, out var halfIterations) &&
                             halfDownscale == BloomDownscaleMode.Half && halfIterations == 6;
            _ctrl.SetBloomQuality(AtmosphereController.BloomQuality.HalfLow);
            bool bloomHalfLow = ReadBloom(out var halfLowDownscale, out var halfLowIterations) &&
                                halfLowDownscale == BloomDownscaleMode.Half && halfLowIterations == 3;
            _ctrl.SetBloomQuality(AtmosphereController.BloomQuality.Quarter);
            bool bloomQuarter = ReadBloom(out var quarterDownscale, out var quarterIterations) &&
                                quarterDownscale == BloomDownscaleMode.Quarter && quarterIterations == 3;
            _ctrl.SetBloomQuality(AtmosphereController.BloomQuality.Half);
            bool bloomSwitch = bloomHalf && bloomHalfLow && bloomQuarter;

            void Check(string name, bool ok, string detail)
            {
                _sb.AppendLine($"[{(ok ? "PASS" : "FAIL")}] {name}：{detail}");
            }

            Check("全局 Volume 存在", volumeOk, profile != null ? profile.name : "missing");
            Check("Tonemapping = ACES", aces, profile != null && profile.TryGet<Tonemapping>(out var t0) ? t0.mode.value.ToString() : "missing");
            Check("Bloom 生效", bloom, profile != null && profile.TryGet<Bloom>(out var b0) ? $"intensity={b0.intensity.value}" : "missing");
            Check("Depth of Field 已移除", noDof, profile != null && profile.TryGet<DepthOfField>(out var d0) ? $"active={d0.IsActive()}" : "absent");
            Check("Color Grading 生效", grading, profile != null && profile.TryGet<ColorAdjustments>(out var c0) ? $"contrast={c0.contrast.value} sat={c0.saturation.value}" : "missing");
            Check("Color Grading = HDR (LogC LUT)", hdrGrading, urp != null ? urp.colorGradingMode.ToString() : "no URP asset");
            Check("两台相机都启用后处理", postCameras >= 2, $"{postCameras}/{cams.Length}");
            Check("火把数 = 12", torchCount, $"{_ring.TorchCount}");
            Check("火把数量可控制且硬上限 6", clamp && limitOk, $"上限={TorchRingController.HardLightLimit}");
            Check("火把近景开关亮度差", lightOn,
                $"中心亮度 on={_torchOnLum:F4} off={_torchOffLum:F4}；暖色像素 {_torchOnWarm}/{_torchOffWarm}");
            Check("夜景曝光自适应", nightExposure, $"night={_nightExposure:F2}EV day={_dayExposure:F2}EV");
            Check("Bloom Half/6、Half/3、Quarter/3 可切换", bloomSwitch,
                $"Half/6={bloomHalf} Half/3={bloomHalfLow} Quarter/3={bloomQuarter}");

            bool pass = volumeOk && aces && bloom && noDof && grading && hdrGrading &&
                        postCameras >= 2 && torchCount && clamp && limitOk && lightOn &&
                        nightExposure && bloomSwitch && shotsOk;

            _sb.AppendLine();
            _sb.AppendLine("===== 截图清单 =====");
            _sb.AppendLine($"  {_night0Path}");
            _sb.AppendLine($"  {_night4Path}");
            _sb.AppendLine($"  {_torchOnPath}");
            _sb.AppendLine($"  {_torchOffPath}");
            _sb.AppendLine($"  {_dayPath}");
            _sb.AppendLine($"  {_dayOrbitPath}");
            _sb.AppendLine();
            _sb.AppendLine(pass ? "==== 结论：PASS ====" : "==== 结论：FAIL ====");

            Flush(_sb, pass ? 0 : 2);
            gameObject.AddComponent<QuitAfter>().Init(pass ? 0 : 2);
        }
    }

    private sealed class QuitAfter : MonoBehaviour
    {
        private int _f;
        private int _code;

        public void Init(int code)
        {
            _code = code;
            hideFlags = HideFlags.DontSave;
        }

        private void Update()
        {
            if (++_f > 3) EditorApplication.Exit(_code);
        }
    }

    private static void Flush(StringBuilder sb, int exitCode)
    {
        Directory.CreateDirectory(OutDir);
        File.WriteAllText(ResultPath, sb.ToString());
        if (!EditorApplication.isPlaying) EditorApplication.Exit(exitCode);
    }
}
