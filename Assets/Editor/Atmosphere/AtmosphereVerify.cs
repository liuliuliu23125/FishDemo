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
public static class AtmosphereVerify
{
    private const string Tag = "[AtmosphereVerify]";
    private const string WorldScenePath = "Assets/Scenes/World.unity";
    private const string OutDir = "Logs/Atmosphere";
    private const string ResultPath = OutDir + "/atmosphere-verify.txt";
    private const string RendererPath = "Assets/Settings/UniversalRenderer.asset";
    private const string PendingKey = "FishDemo.AtmosphereVerify.Pending";

    static AtmosphereVerify()
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
                W($"进入 Play Mode 前禁用草 BRG：{g.name}（避免 429 万实例首帧构建）");
            }

            var controller = UnityEngine.Object.FindObjectOfType<AtmosphereController>();
            if (controller == null)
            {
                W("FAIL：场景里没有 AtmosphereController。先跑 AtmosphereSetup.Apply");
                Flush(sb, 2);
                return;
            }
            if (UnityEngine.Object.FindObjectOfType<EnviroManager>() == null)
            {
                W("FAIL：场景里没有 EnviroManager。先跑 AtmosphereSetup.Apply");
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
        var controller = UnityEngine.Object.FindObjectOfType<AtmosphereController>();
        var manager = UnityEngine.Object.FindObjectOfType<EnviroManager>();
        var cam = UnityEngine.Object.FindObjectsOfType<Camera>().FirstOrDefault(c => c.CompareTag("MainCamera"))
                  ?? UnityEngine.Object.FindObjectOfType<Camera>();

        if (controller == null || manager == null || cam == null)
        {
            sb.AppendLine("FAIL：Play Mode 下找不到 AtmosphereController / EnviroManager / 主相机");
            Flush(sb, 2);
            return;
        }

        new GameObject("~AtmosphereVerify").AddComponent<Driver>().Begin(sb, controller, manager, cam);
    }

    private sealed class Driver : MonoBehaviour
    {
        private const int WarmupFrames = 18;
        private const int TerrainWaitLimit = 3000;

        private StringBuilder _sb;
        private AtmosphereController _ctrl;
        private EnviroManager _manager;
        private Camera _cam;
        private int _frame;
        private bool _ready;
        private int _readyFrame;
        private int _shotIndex;
        private bool _shotApplied;
        private int _shotWait;

        private readonly List<ShotResult> _results = new List<ShotResult>();

        private struct ShotResult
        {
            public string Name;
            public string Time;
            public string Weather;
            public float TopLum;
            public float TopR, TopG, TopB;
            public float BottomLum;
            public int Distinct;
            public bool FogTexture;
            public bool WeatherOk;
            public float LightIntensity;
            public Color LightColor;
            public float FogDensity;
            public float WetnessTarget;
            public string Path;
        }

        private static readonly (string name, string time, string weather, bool lookAtSun)[] Shots =
        {
            ("noon",       "12:00", "Clear Sky", false),
            ("cloudy",     "10:00", "Cloudy 2",  false),
            ("sunset",     "17:10", "Clear Sky", true),
            ("lowsun",     "17:10", "Clear Sky", true),
            ("moonrise",   "18:20", "Clear Sky", true),
            ("midnight",   "00:00", "Clear Sky", false),
            ("fog",        "07:00", "Foggy",     false),
            ("rain",       "12:00", "Rain",      false),
        };

        public void Begin(StringBuilder sb, AtmosphereController ctrl, EnviroManager manager, Camera cam)
        {
            _sb = sb; _ctrl = ctrl; _manager = manager; _cam = cam;
            gameObject.hideFlags = HideFlags.DontSave;

            foreach (var c in UnityEngine.Object.FindObjectsOfType<FishDemo.Gameplay.PegasusTourController>())
            {
                c.enabled = false;
                _sb.AppendLine($"已禁用 Pegasus 巡游：{c.name}");
            }
            foreach (var c in UnityEngine.Object.FindObjectsOfType<FishDemo.Gameplay.LakeOrbitCamera>())
            {
                c.Paused = true;
                _sb.AppendLine($"已暂停环湖轨道：{c.name}");
            }
            foreach (var c in UnityEngine.Object.FindObjectsOfType<FishDemo.Gameplay.LakeCameraRig>())
            {
                c.enabled = false;
                _sb.AppendLine($"已禁用相机 rig：{c.name}");
            }

            foreach (var g in UnityEngine.Object.FindObjectsOfType<FishDemo.Rendering.GrassBRGManager>())
            {
                g.gameObject.SetActive(false);
                _sb.AppendLine($"已禁用草 BRG：{g.name}");
            }

            _ctrl.AutoWeather = false;
            _sb.AppendLine("已关闭自动天气轮换（保证截图可复现）");
        }

        private void Update()
        {
            _frame++;

            if (!_ready)
            {
                if (!_ctrl.IsApplied)
                {
                    if (_frame > 900)
                    {
                        _sb.AppendLine($"FAIL：等了 {_frame} 帧 AtmosphereController 仍未应用");
                        _sb.AppendLine($"EnviroManager={( _manager != null )} Camera={(_manager != null && _manager.Camera != null ? _manager.Camera.name : "null")}");
                        Flush(_sb, 2);
                        gameObject.AddComponent<QuitAfter>().Init(2);
                        enabled = false;
                    }
                    return;
                }

                int covering = CountTerrainsCoveringLake();
                if (covering > 0 || _frame > TerrainWaitLimit)
                {
                    _ready = true;
                    _readyFrame = _frame;
                    _sb.AppendLine($"[frame {_frame}] 覆盖湖心的 Terrain = {covering}，" +
                                   $"场景 Terrain 总数 = {UnityEngine.Object.FindObjectsOfType<Terrain>().Length}");
                }
                return;
            }

            if (_frame < _readyFrame + 45) return;

            if (!_shotsStarted)
            {
                _shotsStarted = true;
                RefreshGtsMeshes();
                _sb.AppendLine($"EnviroManager.Camera = {(_manager.Camera != null ? _manager.Camera.name : "null")}，" +
                               $"验收相机 = {_cam.name}");
            }

            if (_shotIndex >= Shots.Length)
            {
                Finish();
                return;
            }

            var shot = Shots[_shotIndex];
            if (!_shotApplied)
            {
                bool timeOk = _ctrl.SetTimeOfDay(ParseHour(shot.time), pauseTime: true);
                bool weatherOk = _ctrl.SetWeatherImmediate(shot.weather);
                _shotApplied = true;
                _shotWait = 0;
                _sb.AppendLine($"");
                _sb.AppendLine($"── 镜头 {_shotIndex + 1}/{Shots.Length}：{shot.name} / {shot.time} / {shot.weather} ──");
                _sb.AppendLine($"SetTimeOfDay ok={timeOk}，SetWeatherImmediate ok={weatherOk}");
                return;
            }

            _shotWait++;

            if (shot.name == "rain")
            {
                var rain = FindEffectParticleSystem("Rain");
                bool enough = rain != null && rain.particleCount >= 300;
                if (!enough && _shotWait < 900) return;
            }
            else if (_shotWait < WarmupFrames) return;

            Shoot(shot);
            _shotIndex++;
            _shotApplied = false;
        }

        private bool _shotsStarted;

        private void Shoot((string name, string time, string weather, bool lookAtSun) shot)
        {
            _ctrl.SnapNightExposure();
            PoseCamera(shot.name, shot.lookAtSun);

            const int W = 1280, H = 720;
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

            string png = $"{OutDir}/atmosphere-{shot.name}.png";
            File.WriteAllBytes(png, tex.EncodeToPNG());

            var px = tex.GetPixels32();
            double topLum = 0, bottomLum = 0, topR = 0, topG = 0, topB = 0;
            int topN = 0, bottomN = 0;
            var distinctSet = new HashSet<int>();
            for (int y = 0; y < H; y++)
            {
                for (int x = 0; x < W; x++)
                {
                    var c = px[y * W + x];
                    if ((x % 7 == 0) && (y % 7 == 0))
                        distinctSet.Add((c.r << 16) | (c.g << 8) | c.b);
                    if (y >= H * 2 / 3)
                    {
                        topLum += (0.2126 * c.r + 0.7152 * c.g + 0.0722 * c.b) / 255.0;
                        topR += c.r; topG += c.g; topB += c.b;
                        topN++;
                    }
                    else if (y < H / 3)
                    {
                        bottomLum += (0.2126 * c.r + 0.7152 * c.g + 0.0722 * c.b) / 255.0;
                        bottomN++;
                    }
                }
            }

            var light = _manager.Objects.directionalLight;
            var fogTex = Shader.GetGlobalTexture("_EnviroVolumetricFogTex");
            var result = new ShotResult
            {
                Name = shot.name,
                Time = shot.time,
                Weather = shot.weather,
                TopLum = topN > 0 ? (float)(topLum / topN) : 0f,
                TopR = topN > 0 ? (float)(topR / topN / 255.0) : 0f,
                TopG = topN > 0 ? (float)(topG / topN / 255.0) : 0f,
                TopB = topN > 0 ? (float)(topB / topN / 255.0) : 0f,
                BottomLum = bottomN > 0 ? (float)(bottomLum / bottomN) : 0f,
                Distinct = distinctSet.Count,
                FogTexture = fogTex != null,
                WeatherOk = _manager.Weather.targetWeatherType != null &&
                            _manager.Weather.targetWeatherType.name == shot.weather,
                LightIntensity = light != null ? light.intensity : -1f,
                LightColor = light != null ? light.color : Color.black,
                FogDensity = _manager.Fog.Settings.fogDensity,
                WetnessTarget = _manager.Environment.Settings.wetnessTarget,
                Path = png,
            };
            _results.Add(result);

            _sb.AppendLine($"截图：{png}");
            _sb.AppendLine($"  顶部 1/3 天空：lum={result.TopLum:F3} rgb=({result.TopR:F2},{result.TopG:F2},{result.TopB:F2})");
            _sb.AppendLine($"  底部 1/3 地面/水面：lum={result.BottomLum:F3}，颜色种类={result.Distinct}");
            _sb.AppendLine($"  主光：intensity={result.LightIntensity:F3} color=({result.LightColor.r:F2},{result.LightColor.g:F2},{result.LightColor.b:F2})");
            _sb.AppendLine($"  天气目标：{(_manager.Weather.targetWeatherType != null ? _manager.Weather.targetWeatherType.name : "null")}（期望 {shot.weather}）");
            _sb.AppendLine($"  fogDensity={result.FogDensity:F3}，wetnessTarget={result.WetnessTarget:F2}，体积雾纹理={(result.FogTexture ? "有" : "无")}");

            if (_manager.Effects != null && _manager.Effects.Settings != null)
            {
                foreach (var e in _manager.Effects.Settings.effectTypes)
                {
                    if (e == null) continue;
                    var ps = e.mySystem;
                    string state = ps == null
                        ? "mySystem=null"
                        : $"playing={ps.isPlaying} count={ps.particleCount} " +
                          $"rendererVisible={(ps.GetComponent<ParticleSystemRenderer>() != null && ps.GetComponent<ParticleSystemRenderer>().isVisible)}";
                    _sb.AppendLine($"  粒子 {e.name}: emissionRate={e.emissionRate:F2} {state}");
                }
            }

            DestroyImmediate(tex);
            rt.Release();
            DestroyImmediate(rt);
        }

        private void PoseCamera(string shotName, bool lookAtSun)
        {
            var center = new Vector3(LakeCarve.Center.x, LakeCarve.WaterLevel, LakeCarve.Center.y);
            Vector3 stand = shotName == "lowsun"
                ? new Vector3(center.x, LakeCarve.WaterLevel + 2f, center.z + 150f)
                : new Vector3(center.x, LakeCarve.WaterLevel + 8f, center.z - 140f);
            _cam.transform.position = stand;
            _cam.fieldOfView = 60f;
            _cam.nearClipPlane = 0.1f;
            _cam.farClipPlane = 4000f;

            Vector3 dir = center - stand;
            if (lookAtSun && _manager.Objects.directionalLight != null)
            {
                Vector3 sunDir = -_manager.Objects.directionalLight.transform.forward;
                Vector3 flat = new Vector3(sunDir.x, 0f, sunDir.z);
                if (flat.sqrMagnitude > 0.001f)
                    dir = flat.normalized * 0.9f + Vector3.down * 0.05f + Vector3.up * 0.15f;
            }
            _cam.transform.rotation = Quaternion.LookRotation(dir.normalized);
        }

        private ParticleSystem FindEffectParticleSystem(string effectName)
        {
            if (_manager == null || _manager.Effects == null || _manager.Effects.Settings == null)
                return null;
            foreach (var e in _manager.Effects.Settings.effectTypes)
            {
                if (e != null && e.name == effectName)
                    return e.mySystem;
            }
            return null;
        }

        private static float ParseHour(string hhmm)
        {
            var parts = hhmm.Split(':');
            return int.Parse(parts[0]) + int.Parse(parts[1]) / 60f;
        }

        private void RefreshGtsMeshes()
        {
            int n = 0;
            foreach (var gts in UnityEngine.Object.FindObjectsOfType<ProceduralWorlds.GTS.GTSMesh>())
            {
                try { gts.Refresh(); n++; } catch { }
            }
            _sb.AppendLine($"已 Refresh GTSMesh = {n}（否则地表全黑）");
        }

        private static int CountTerrainsCoveringLake()
        {
            int n = 0;
            float cx = LakeCarve.Center.x, cz = LakeCarve.Center.y;
            foreach (var t in UnityEngine.Object.FindObjectsOfType<Terrain>())
            {
                if (t.terrainData == null) continue;
                var p = t.transform.position;
                var s = t.terrainData.size;
                if (cx >= p.x && cx <= p.x + s.x && cz >= p.z && cz <= p.z + s.z) n++;
            }
            return n;
        }

        private void Finish()
        {
            enabled = false;

            _sb.AppendLine();
            _sb.AppendLine("===== 状态断言 =====");

            var urp = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
            var rendererData = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(RendererPath);
            bool feature = rendererData != null &&
                           rendererData.rendererFeatures.Any(f => f != null && f.GetType().Name == "EnviroURPRenderFeature");
            bool depth = urp != null && urp.supportsCameraDepthTexture;

            int activeDirectional = 0;
            foreach (var l in UnityEngine.Object.FindObjectsOfType<Light>(true))
                if (l.type == LightType.Directional && l.enabled && l.gameObject.activeInHierarchy) activeDirectional++;

            var legacy = GameObject.Find("Directional Light");
            var legacyLight = legacy != null ? legacy.GetComponent<Light>() : null;
            bool legacyOff = legacyLight == null || !legacyLight.enabled;

            bool oneLight = activeDirectional == 1;
            bool vol = _manager.Fog.Settings.volumetrics;
            bool fogLow = _manager.Fog.Settings.quality == EnviroFogSettings.Quality.Low;
            bool steps32 = _manager.Fog.Settings.steps == 32;
            bool fogRange = _manager.Fog.Settings.maxRange <= 600.5f;
            bool cloudsOff = _manager.VolumetricClouds != null &&
                             !_manager.VolumetricClouds.settingsQuality.volumetricClouds;
            bool flatOn = _manager.FlatClouds != null && _manager.FlatClouds.settings.useFlatClouds;
            bool reflOff = !_manager.Reflections.Settings.globalReflections;
            bool ambientThrottle = !_manager.Lighting.Settings.ambientUpdateEveryFrame &&
                                   _manager.Lighting.Settings.ambientUpdateIntervall >= 0.4f;
            bool enviroCamera = EnviroHelper.CanRenderOnCamera(_cam);

            var noon = _results.FirstOrDefault(r => r.Name == "noon");
            var mid = _results.FirstOrDefault(r => r.Name == "midnight");
            var fog = _results.FirstOrDefault(r => r.Name == "fog");
            var rain = _results.FirstOrDefault(r => r.Name == "rain");
            bool skyBrightnessOrder = noon.TopLum > mid.TopLum * 1.1f;
            bool volumesRan = _results.All(r => r.FogTexture);
            bool weatherOk = _results.All(r => r.WeatherOk);
            bool imagesNotEmpty = _results.All(r => r.Distinct > 100 && r.TopLum > 0.001f);
            bool fogDensityOrder = fog.FogDensity > noon.FogDensity * 1.5f;
            bool rainWet = rain.WetnessTarget > 0.01f;

            void Check(string name, bool ok, string detail)
            {
                _sb.AppendLine($"[{(ok ? "PASS" : "FAIL")}] {name}：{detail}");
            }

            Check("EnviroManager 存在", _manager != null, "singleton");
            Check("AtmosphereController 已应用", _ctrl.IsApplied, $"hour={_ctrl.CurrentHour:F2} weather={_ctrl.CurrentWeatherName}");
            Check("只有一盏启用的方向光", oneLight, $"activeDirectional={activeDirectional}，期望 1");
            Check("旧 Directional Light 已禁用", legacyOff, legacyLight != null ? $"enabled={legacyLight.enabled}" : "场景无旧主光");
            Check("URP depth texture = 1", depth, "体积雾/云的硬依赖");
            Check("EnviroURPRenderFeature 已挂", feature, "UniversalRenderer.asset");
            Check("体积雾 volumetrics = true", vol, $"quality={_manager.Fog.Settings.quality} steps={_manager.Fog.Settings.steps}");
            Check("体积雾 Low 档", fogLow, _manager.Fog.Settings.quality.ToString());
            Check("体积雾 steps = 32", steps32, $"{_manager.Fog.Settings.steps}");
            Check("体积雾 maxRange <= 600m", fogRange, $"{_manager.Fog.Settings.maxRange:F0}m");
            Check("体积云已关闭", cloudsOff, "改用 flat clouds");
            Check("flat clouds 已开启", flatOn, $"useFlatClouds={_manager.FlatClouds?.settings.useFlatClouds}");
            Check("全局反射探针已关闭", reflOff, "省每 ~0.6s 一次 cubemap");
            Check("环境探针刷新已降频", ambientThrottle, $"every={_manager.Lighting.Settings.ambientUpdateEveryFrame} interval={_manager.Lighting.Settings.ambientUpdateIntervall:F2}s");
            Check("Enviro feature 对验收相机生效", enviroCamera, $"manager.Camera={(_manager.Camera != null ? _manager.Camera.name : "null")} / cam={_cam.name}");
            Check("体积雾 pass 实际执行", volumesRan, "_EnviroVolumetricFogTex " + (_results.Count > 0 && _results[0].FogTexture ? "非空" : "为空"));
            Check("全部截图的天气都切换成功", weatherOk, string.Join(", ", _results.Select(r => $"{r.Name}:{r.Weather}")));
            Check("正午天空比午夜亮", skyBrightnessOrder, $"noon={noon.TopLum:F3} midnight={mid.TopLum:F3}");
            Check("雾天 fogDensity 明显高于晴天", fogDensityOrder, $"fog={fog.FogDensity:F3} clear={noon.FogDensity:F3}");
            Check("雨天 wetnessTarget > 0", rainWet, $"{rain.WetnessTarget:F2}");
            Check("截图非黑屏/非纯色", imagesNotEmpty, string.Join(", ", _results.Select(r => $"{r.Name}:{r.Distinct}色")));

            bool pass = oneLight && legacyOff && depth && feature && vol && fogLow && steps32 &&
                        fogRange && cloudsOff && flatOn && reflOff && ambientThrottle &&
                        enviroCamera && volumesRan && weatherOk && skyBrightnessOrder &&
                        fogDensityOrder && rainWet && imagesNotEmpty && _manager != null &&
                        _ctrl.IsApplied;

            _sb.AppendLine();
            _sb.AppendLine("===== 截图清单 =====");
            foreach (var r in _results) _sb.AppendLine($"  {r.Name}: {r.Path}");
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
        public void Init(int code) { _code = code; hideFlags = HideFlags.DontSave; }
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
