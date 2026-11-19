using Enviro;
using FishDemo.Rendering;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using WaveHarmonic.Crest;

namespace FishDemo.Gameplay
{
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(1000)]
    public sealed class AtmosphereController : MonoBehaviour
    {
        public enum BloomQuality
        {
            Half = 0,
            Quarter = 1,
            HalfLow = 2
        }

        [Header("质量档（移动端自定义 EnviroQuality 资产）")]
        [SerializeField] private EnviroQuality _mobileQuality;

        [Header("时间")]
        [Tooltip("一个游戏日对应的现实分钟数。20 分钟兼顾演示可感知与不晃眼。")]
        [SerializeField] private float _cycleLengthMinutes = 20f;
        [Tooltip("启动时刻（小时，0-24）。")]
        [Range(0f, 24f)]
        [SerializeField] private float _startHour = 9f;
        [SerializeField] private float _latitude = 30f;
        [SerializeField] private float _longitude = 120f;
        [Range(-13, 13)]
        [SerializeField] private int _utcOffset = 8;

        [Header("天气")]
        [SerializeField] private bool _autoWeather = true;
        [Tooltip("自动换天气的间隔（秒）。演示用，正式玩法应改由任务/区域驱动。")]
        [SerializeField] private float _weatherChangeIntervalSeconds = 180f;
        [SerializeField] private string _startWeather = "Cloudy 2";
        [Tooltip("自动轮换的天气顺序。名字必须与 Enviro 天气预设资产同名。")]
        [SerializeField]
        private string[] _weatherCycle =
        {
            "Clear Sky", "Cloudy 2", "Rain", "Cloudy 3", "Foggy"
        };

        [Header("性能")]
        [SerializeField] private bool _disableGlobalReflections = true;
        [Tooltip("环境探针刷新间隔（秒）。Enviro 默认 0.1s，移动端过热。")]
        [SerializeField] private float _ambientUpdateInterval = 0.5f;
        [Tooltip("体积雾最大距离（米）。湖面尺度 600m 足够，超出部分由天空盒承担。")]
        [SerializeField] private float _volumetricMaxRange = 600f;

        [Header("水下")]
        [Tooltip("相机低于 Crest 水位时关闭 Enviro fog/volumetrics，交给 Crest 水下效果。")]
        [SerializeField] private bool _disableFogUnderwater = true;

        [Header("体积光")]
        [Tooltip("低太阳角时把体积散射提高到这个值。Clear 天气预设只有 0.045，光柱几乎不可见。")]
        [SerializeField] private float _lowSunScattering = 0.18f;
        [Tooltip("solarTime 低于此值视为低太阳角（0.5≈日出/日落，1≈正午）。")]
        [Range(0.1f, 0.9f)]
        [SerializeField] private float _lowSunSolarTimeMax = 0.6f;

        [Header("夜景曝光")]
        [Tooltip("全局后处理 Volume。运行时用它的 profile 克隆改 ColorAdjustments.postExposure。")]
        [SerializeField] private Volume _postProcessVolume;
        [Tooltip("夜间额外曝光（EV）。Enviro 的夜空是显示参考色，ACES 需要抬曝光才可见。")]
        [SerializeField] private float _nightExposureEv = 1.8f;
        [SerializeField] private float _nightContrast = 8f;
        [SerializeField] private float _nightSaturation = -18f;
        [SerializeField] private Color _nightColorFilter = new Color(0.78f, 0.88f, 1.08f, 1f);
        [Tooltip("昼夜曝光过渡时间（秒）。")]
        [SerializeField] private float _exposureFadeSeconds = 2.5f;

        [Header("Bloom 质量")]
        [Tooltip("Half/6 = half-res 6 mip；Half/3 = half-res 3 mip；Quarter/3 = quarter-res 3 mip。")]
        [SerializeField] private BloomQuality _bloomQuality = BloomQuality.Half;
        [SerializeField] private bool _enableBloomQualityHotkey = true;
        [SerializeField] private KeyCode _bloomQualityKey = KeyCode.B;

        [Header("演示热键")]
        [Tooltip("1-5 切换天气，T 暂停/恢复时间流逝，[ ] 快退/快进 1 小时，\\ 切时间流速。")]
        [SerializeField] private bool _enableHotkeys = true;

        [Header("HUD")]
        [Tooltip("显示左上角状态与操作提示（时刻 / 天气 / 时间流速 / 热键）。")]
        [SerializeField] private bool _showHud = true;
        [Tooltip("HUD 字号（像素，按 1080p 设计，运行时按屏幕高度缩放）。")]
        [SerializeField] private int _hudFontSize = 18;

        private EnviroManager _manager;
        private bool _applied;
        private bool _loggedMissingModules;
        private float _weatherTimer;
        private float _grassWindTimer;
        private GrassBRGManager _grass;
        private bool _grassSearchDone;
        private WaterRenderer _water;
        private bool _waterSearchDone;
        private TorchRingController _torchRing;
        private bool _torchSearchDone;
        private ColorAdjustments _colorAdjustments;
        private Bloom _bloom;
        private float _baseExposure;
        private float _baseContrast;
        private float _baseSaturation;
        private Color _baseColorFilter;
        private float _nightAmount;
        private bool _exposureReady;
        private int _underwaterState = -1;
        private int _weatherIndex;

        private static readonly float[] CyclePresets = { 20f, 5f, 2f, 1f };
        private static readonly string[] CycleLabels = { "20min/天", "5min/天", "2min/天", "1min/天" };
        private int _cycleIndex;

        private GUIStyle _hudStyle;
        private GUIStyle _hudHintStyle;
        private GUIStyle _hudShadowStyle;
        private GUIStyle _hudHintShadowStyle;
        private Texture2D _hudBg;
        private int _hudStyleScreenW = -1;
        private int _hudStyleScreenH = -1;
        private GUIContent _hudContent;
        private GUIContent _hudHintContent;
        private GUIContent _hudTitleContent;
        private GUIContent _hudBloomContent;
        private string _hudText;
        private string _hudHintText;
        private string _hudBloomText;
        private string _hudTitle;
        private int _hudHour = -1;
        private int _hudMinute = -1;
        private bool _hudRunning;
        private string _hudWeather;
        private int _hudCycle = -1;
        private float _hudWidth;
        private float _hudHeight;
        private float _hudTitleHeight;
        private float _hudMainHeight;
        private float _hudHintHeight;
        private float _hudBloomHeight;
        private int _hudTorchActive = -2;
        private int _hudTorchMax = -2;
        private int _hudBloomQuality = -2;

        public bool AutoWeather
        {
            get => _autoWeather;
            set => _autoWeather = value;
        }

        public bool IsApplied => _applied;

        public bool ShowHud
        {
            get => _showHud;
            set => _showHud = value;
        }

        private void Awake()
        {
            _manager = EnviroManager.instance;
        }

        private void OnEnable()
        {
            RenderPipelineManager.beginCameraRendering += OnBeginCameraRendering;
        }

        private void OnDisable()
        {
            RenderPipelineManager.beginCameraRendering -= OnBeginCameraRendering;

            if (_underwaterState == 1 && _mobileQuality != null)
            {
                _mobileQuality.fogOverride.fog = true;
                _mobileQuality.fogOverride.volumetrics = true;
            }
        }

        private void OnBeginCameraRendering(ScriptableRenderContext context, Camera cam)
        {
            if (!_applied || !_disableFogUnderwater || _mobileQuality == null || _manager == null)
                return;
            if (cam == null || cam.cameraType != CameraType.Game)
                return;
            if (!EnviroHelper.CanRenderOnCamera(cam))
                return;

            ApplyLowSunVolumetrics();
            EnsureWater();
            ApplyUnderwaterFog(cam);
        }

        private void Start()
        {
            _manager = EnviroManager.instance;
            if (_manager == null)
                Debug.LogError("[Atmosphere] 场景里找不到 EnviroManager，控制器不会生效");
            TryApply();
        }

        private void Update()
        {
            if (!_applied)
            {
                if (!TryApply()) return;
            }

            if (_autoWeather)
            {
                _weatherTimer += Time.deltaTime;
                if (_weatherTimer >= _weatherChangeIntervalSeconds)
                {
                    _weatherTimer = 0f;
                    _weatherIndex = (_weatherIndex + 1) % Mathf.Max(1, _weatherCycle.Length);
                    ChangeWeather(_weatherCycle[_weatherIndex]);
                }
            }

            if (_enableHotkeys) HandleHotkeys();

            EnsureWater();
            EnsureTorchRing();
            UpdateNightExposure();
            ApplyUnderwaterFog(_manager.Camera != null ? _manager.Camera : Camera.main);

            _grassWindTimer += Time.deltaTime;
            if (_grassWindTimer >= 0.25f)
            {
                _grassWindTimer = 0f;
                UpdateGrassWind();
            }
        }

        public bool SetTimeOfDay(float hour, bool pauseTime)
        {
            if (!_applied || _manager?.Time == null) return false;
            _manager.Time.Settings.simulate = !pauseTime;
            _manager.Time.SetTimeOfDay(Mathf.Repeat(hour, 24f));
            return true;
        }

        public bool SetWeatherImmediate(string weatherName)
        {
            if (!_applied || _manager?.Weather == null) return false;
            var type = FindWeather(weatherName);
            if (type == null) return false;
            _manager.Weather.ChangeWeatherInstant(type);
            return true;
        }

        public bool ChangeWeather(string weatherName)
        {
            if (!_applied || _manager?.Weather == null) return false;
            var type = FindWeather(weatherName);
            if (type == null) return false;
            _manager.Weather.ChangeWeather(type);
            return true;
        }

        public float CurrentHour =>
            _applied && _manager?.Time != null ? _manager.Time.GetTimeOfDay() : -1f;

        public bool IsTimeRunning =>
            _applied && _manager?.Time != null && _manager.Time.Settings.simulate;

        public bool ShiftTimeByHours(float hours)
        {
            if (!_applied || _manager?.Time == null) return false;
            float next = Mathf.Repeat(_manager.Time.GetTimeOfDay() + hours, 24f);
            _manager.Time.SetTimeOfDay(next);
            return true;
        }

        public bool ToggleTimeRunning()
        {
            if (!_applied || _manager?.Time == null) return false;
            bool next = !_manager.Time.Settings.simulate;
            _manager.Time.Settings.simulate = next;
            return next;
        }

        public string CurrentWeatherName =>
            _applied && _manager?.Weather?.targetWeatherType != null
                ? _manager.Weather.targetWeatherType.name
                : "(none)";

        private EnviroWeatherType FindWeather(string weatherName)
        {
            if (string.IsNullOrEmpty(weatherName)) return null;
            var list = _manager.Weather.Settings.weatherTypes;
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i] != null && list[i].name == weatherName) return list[i];
            }
            Debug.LogWarning($"[Atmosphere] 找不到天气预设「{weatherName}」，可用：{string.Join(", ", list.ConvertAll(t => t != null ? t.name : "null"))}");
            return null;
        }

        private bool TryApply()
        {
            if (_manager == null) _manager = EnviroManager.instance;
            if (_manager == null) return false;

            if (_manager.Time == null || _manager.Weather == null || _manager.Quality == null ||
                _manager.Lighting == null || _manager.Sky == null || _manager.Fog == null ||
                _manager.Reflections == null || _manager.Environment == null || _manager.Effects == null)
            {
                if (!_loggedMissingModules)
                {
                    _loggedMissingModules = true;
                    Debug.LogWarning("[Atmosphere] Enviro 模块尚未就绪：" +
                        $"Time={_manager.Time != null} Lighting={_manager.Lighting != null} Sky={_manager.Sky != null} " +
                        $"Fog={_manager.Fog != null} Weather={_manager.Weather != null} Quality={_manager.Quality != null} " +
                        $"Reflections={_manager.Reflections != null} Environment={_manager.Environment != null} " +
                        $"Effects={_manager.Effects != null}");
                }
                return false;
            }

            ApplySettings();
            _applied = true;
            Debug.Log($"[Atmosphere] 已应用：{CurrentHour:0.0}h / {CurrentWeatherName} / " +
                      $"体积雾={_manager.Fog.Settings.volumetrics}（{_manager.Fog.Settings.quality}，{_manager.Fog.Settings.steps} steps）" +
                      $" / 全局反射={_manager.Reflections.Settings.globalReflections}");
            return true;
        }

        private void ApplySettings()
        {
            if (_mobileQuality != null)
            {
                _manager.Quality.Settings.defaultQuality = _mobileQuality;
                if (!_manager.Quality.Settings.Qualities.Contains(_mobileQuality))
                    _manager.Quality.Settings.Qualities.Add(_mobileQuality);
            }

            var time = _manager.Time.Settings;
            time.simulate = true;
            time.cycleLengthInMinutes = _cycleLengthMinutes;
            time.latitude = _latitude;
            time.longitude = _longitude;
            time.utcOffset = _utcOffset;
            _manager.Time.SetDateTime(0, 0, Mathf.FloorToInt(_startHour), 15, 10, 2026);
            _cycleIndex = Mathf.Max(0, System.Array.IndexOf(CyclePresets, _cycleLengthMinutes));

            _manager.Sky.Settings.moonMode = EnviroSky.MoonMode.Simple;

            _manager.Lighting.Settings.ambientUpdateEveryFrame = false;
            _manager.Lighting.Settings.ambientUpdateIntervall = _ambientUpdateInterval;
            _manager.Lighting.Settings.ambientIntensityModifier = 1.4f;

            _manager.Reflections.Settings.globalReflections = !_disableGlobalReflections;
            _manager.Reflections.Settings.updateDefaultEnvironmentReflections = false;

            _manager.Fog.Settings.maxRange = _volumetricMaxRange;
            _manager.Fog.Settings.maxRangePointSpot = 100f;

            _manager.Weather.globalAutoWeatherChange = false;
            SetWeatherImmediate(_startWeather);
            _weatherIndex = Mathf.Max(0, System.Array.IndexOf(_weatherCycle, _startWeather));

            var cam = Camera.main;
            if (cam != null) _manager.ChangeCamera(cam);

            UpdateGrassWind();
        }

        private void ApplyLowSunVolumetrics()
        {
            if (_manager == null || _manager.Fog == null) return;

            bool lowSun = !_manager.isNight &&
                          _manager.solarTime > 0.05f &&
                          _manager.solarTime < _lowSunSolarTimeMax;
            if (!lowSun) return;

            if (_manager.Fog.Settings.scattering < _lowSunScattering)
                _manager.Fog.Settings.scattering = _lowSunScattering;
        }

        private void ApplyUnderwaterFog(Camera cam)
        {
            if (!_disableFogUnderwater || _mobileQuality == null || _water == null || cam == null)
                return;

            bool under = cam.transform.position.y < _water.SeaLevel - 0.05f;
            int state = under ? 1 : 0;
            if (state == _underwaterState) return;
            _underwaterState = state;

            _mobileQuality.fogOverride.fog = !under;
            _mobileQuality.fogOverride.volumetrics = !under;
            Debug.Log($"[Atmosphere] 相机{(under ? "入水" : "出水")}：" +
                      $"Enviro fog={!under} volumetrics={!under}（水下交给 Crest）");
        }

        private void EnsureWater()
        {
            if (_water != null || _waterSearchDone) return;
            _water = FindObjectOfType<WaterRenderer>();
            _waterSearchDone = true;
        }

        private void EnsureTorchRing()
        {
            if (_torchRing != null || _torchSearchDone) return;
            _torchRing = FindObjectOfType<TorchRingController>();
            _torchSearchDone = true;
        }

        private void UpdateNightExposure()
        {
            if (!_exposureReady)
            {
                if (_postProcessVolume == null) _postProcessVolume = FindObjectOfType<Volume>();
                if (_postProcessVolume == null) return;
                var profile = _postProcessVolume.profile;
                if (profile == null || !profile.TryGet<ColorAdjustments>(out _colorAdjustments))
                    return;
                _baseExposure = _colorAdjustments.postExposure.value;
                _baseContrast = _colorAdjustments.contrast.value;
                _baseSaturation = _colorAdjustments.saturation.value;
                _baseColorFilter = _colorAdjustments.colorFilter.value;
                profile.TryGet<Bloom>(out _bloom);
                ApplyBloomQuality();
                _exposureReady = true;
            }

            float target = _manager != null && _manager.isNight ? 1f : 0f;
            float step = _exposureFadeSeconds > 0.01f ? Time.deltaTime / _exposureFadeSeconds : 1f;
            _nightAmount = Mathf.MoveTowards(_nightAmount, target, step);
            ApplyNightGrading();
        }

        public void SnapNightExposure()
        {
            if (!_exposureReady) UpdateNightExposure();
            if (!_exposureReady) return;
            _nightAmount = _manager != null && _manager.isNight ? 1f : 0f;
            ApplyNightGrading();
        }

        private void ApplyNightGrading()
        {
            _colorAdjustments.postExposure.value = _baseExposure + _nightExposureEv * _nightAmount;
            _colorAdjustments.contrast.value = Mathf.Lerp(_baseContrast, _nightContrast, _nightAmount);
            _colorAdjustments.saturation.value = Mathf.Lerp(_baseSaturation, _nightSaturation, _nightAmount);
            _colorAdjustments.colorFilter.value = Color.Lerp(_baseColorFilter, _nightColorFilter, _nightAmount);
        }

        public BloomQuality CurrentBloomQuality => _bloomQuality;

        public void SetBloomQuality(BloomQuality quality)
        {
            _bloomQuality = quality;
            ApplyBloomQuality();
        }

        public void CycleBloomQuality()
        {
            switch (_bloomQuality)
            {
                case BloomQuality.Half:
                    SetBloomQuality(BloomQuality.HalfLow);
                    break;
                case BloomQuality.HalfLow:
                    SetBloomQuality(BloomQuality.Quarter);
                    break;
                default:
                    SetBloomQuality(BloomQuality.Half);
                    break;
            }
        }

        private void ApplyBloomQuality()
        {
            if (_bloom == null) return;

            BloomDownscaleMode downscale;
            int iterations;
            switch (_bloomQuality)
            {
                case BloomQuality.Half:
                    downscale = BloomDownscaleMode.Half;
                    iterations = 6;
                    break;
                case BloomQuality.HalfLow:
                    downscale = BloomDownscaleMode.Half;
                    iterations = 3;
                    break;
                default:
                    downscale = BloomDownscaleMode.Quarter;
                    iterations = 3;
                    break;
            }

            _bloom.downscale.overrideState = true;
            _bloom.downscale.value = downscale;
            _bloom.maxIterations.overrideState = true;
            _bloom.maxIterations.value = iterations;
            _bloom.highQualityFiltering.overrideState = true;
            _bloom.highQualityFiltering.value = false;
        }

        private static string BloomLabel(BloomQuality quality)
        {
            if (quality == BloomQuality.Half) return "Half/6";
            if (quality == BloomQuality.HalfLow) return "Half/3";
            return "Quarter/3";
        }

        private void UpdateGrassWind()
        {
            var env = _manager != null && _manager.Environment != null
                ? _manager.Environment.Settings
                : null;
            if (env == null) return;

            if (_grass == null && !_grassSearchDone)
            {
                _grass = FindObjectOfType<GrassBRGManager>();
                _grassSearchDone = true;
            }
            if (_grass != null) _grass.SetWeatherWind(env.windSpeed, env.windTurbulence);
        }

        private void HandleHotkeys()
        {
            if (Input.GetKeyDown(KeyCode.Alpha1)) ChangeWeather("Clear Sky");
            else if (Input.GetKeyDown(KeyCode.Alpha2)) ChangeWeather("Cloudy 2");
            else if (Input.GetKeyDown(KeyCode.Alpha3)) ChangeWeather("Rain");
            else if (Input.GetKeyDown(KeyCode.Alpha4)) ChangeWeather("Foggy");
            else if (Input.GetKeyDown(KeyCode.Alpha5)) ChangeWeather("Storm");
            else if (Input.GetKeyDown(KeyCode.T))
            {
                bool running = ToggleTimeRunning();
                Debug.Log($"[Atmosphere] 时间流逝 = {running}");
            }
            else if (Input.GetKeyDown(KeyCode.LeftBracket))
            {
                ShiftTimeByHours(-1f);
            }
            else if (Input.GetKeyDown(KeyCode.RightBracket))
            {
                ShiftTimeByHours(1f);
            }
            else if (Input.GetKeyDown(KeyCode.Backslash))
            {
                CycleTimeScale();
            }
            else if (_enableBloomQualityHotkey && Input.GetKeyDown(_bloomQualityKey))
            {
                CycleBloomQuality();
                Debug.Log($"[Atmosphere] Bloom 质量 = {BloomLabel(_bloomQuality)}");
            }
        }

        public void CycleTimeScale()
        {
            if (!_applied || _manager?.Time == null) return;
            _cycleIndex = (_cycleIndex + 1) % CyclePresets.Length;
            _manager.Time.Settings.cycleLengthInMinutes = CyclePresets[_cycleIndex];
            Debug.Log($"[Atmosphere] 时间流速 = {CycleLabels[_cycleIndex]}");
        }

        private const float HudInset = 8f;

        public static float ComputeHudWidth(float naturalWidth, int screenWidth)
        {
            const float margin = 12f;
            const float minWidth = 64f;
            float available = Mathf.Max(minWidth, screenWidth - margin * 2f);
            return Mathf.Min(naturalWidth + HudInset * 2f, available);
        }

        public static int HudDrawCount { get; private set; }
        public static int HudSkipNotAppliedCount { get; private set; }
        public static int HudSkipDisabledCount { get; private set; }
        public static Rect HudLastRect { get; private set; }
        public static Vector2Int HudLastScreen { get; private set; }
        public static Color HudTextColor { get; private set; }

        private static void MeasureBlock(GUIStyle style, string text, out float width, out float height)
        {
            width = 0f;
            height = 0f;
            if (string.IsNullOrEmpty(text))
                return;

            int lineStart = 0;
            for (int i = 0; i <= text.Length; i++)
            {
                if (i < text.Length && text[i] != '\n')
                    continue;

                var size = style.CalcSize(new GUIContent(text.Substring(lineStart, i - lineStart)));
                if (size.x > width) width = size.x;
                height += size.y;

                lineStart = i + 1;
                if (i >= text.Length) break;
            }
        }

        private void OnGUI()
        {
            if (!_showHud)
            {
                HudSkipDisabledCount++;
                return;
            }
            if (!_applied)
            {
                HudSkipNotAppliedCount++;
                return;
            }

            if (_hudStyle == null ||
                _hudStyleScreenW != Screen.width || _hudStyleScreenH != Screen.height)
            {
                if (_hudBg == null)
                {
                    _hudBg = new Texture2D(1, 1, TextureFormat.RGBA32, false);
                    _hudBg.SetPixel(0, 0, new Color(0f, 0f, 0f, 0.72f));
                    _hudBg.Apply();
                    _hudBg.hideFlags = HideFlags.DontSave;
                }

                int baseSize = Mathf.Max(14, Mathf.RoundToInt(_hudFontSize * Screen.height / 1080f));

                _hudStyle = new GUIStyle(GUI.skin.label)
                {
                    fontSize = baseSize,
                    richText = true,
                    alignment = TextAnchor.UpperLeft,
                    wordWrap = false,
                    clipping = TextClipping.Overflow,
                };
                _hudStyle.normal.textColor = Color.white;
                HudTextColor = _hudStyle.normal.textColor;

                _hudHintStyle = new GUIStyle(_hudStyle)
                {
                    fontSize = Mathf.Max(14, Mathf.RoundToInt(baseSize * 0.85f)),
                    richText = false,
                };
                _hudHintStyle.normal.textColor = new Color(0.93f, 0.93f, 0.93f, 1f);

                _hudShadowStyle = new GUIStyle(_hudStyle);
                _hudShadowStyle.normal.textColor = new Color(0f, 0f, 0f, 0.85f);
                _hudHintShadowStyle = new GUIStyle(_hudHintStyle);
                _hudHintShadowStyle.normal.textColor = new Color(0f, 0f, 0f, 0.85f);

                _hudStyleScreenW = Screen.width;
                _hudStyleScreenH = Screen.height;
                _hudText = null;
            }

            float hour = CurrentHour;
            int hh = Mathf.FloorToInt(hour);
            int mm = Mathf.FloorToInt((hour - hh) * 60f);

            string phase = hour < 5f ? "深夜"
                : hour < 7f ? "黎明"
                : hour < 11f ? "上午"
                : hour < 14f ? "正午"
                : hour < 17f ? "下午"
                : hour < 19f ? "黄昏"
                : "夜晚";

            int torchActive = _torchRing != null ? _torchRing.ActiveTargetCount : -1;
            int torchMax = _torchRing != null ? _torchRing.MaxActiveLights : -1;
            int bloomQuality = (int)_bloomQuality;

            if (_hudText == null || _hudHour != hh || _hudMinute != mm ||
                _hudRunning != IsTimeRunning || _hudWeather != CurrentWeatherName ||
                _hudCycle != _cycleIndex || _hudTorchActive != torchActive ||
                _hudTorchMax != torchMax || _hudBloomQuality != bloomQuality)
            {
                _hudHour = hh;
                _hudMinute = mm;
                _hudRunning = IsTimeRunning;
                _hudWeather = CurrentWeatherName;
                _hudCycle = _cycleIndex;
                _hudTorchActive = torchActive;
                _hudTorchMax = torchMax;
                _hudBloomQuality = bloomQuality;

                _hudTitle = "<b>键盘操作提示</b>";

                _hudText =
                    $"<b>昼夜 / 天气</b>  |  <b>时间 {hh:00}:{mm:00}</b>（{phase}）" +
                    $"{(IsTimeRunning ? "" : "  <color=#ffcc66>[已暂停]</color>")}\n" +
                    $"天气 <b>{CurrentWeatherName}</b>  |  流速 {CycleLabels[Mathf.Clamp(_cycleIndex, 0, CycleLabels.Length - 1)]}" +
                    (_torchRing != null
                        ? $"\n火把 <b>{torchActive}/{_torchRing.TorchCount}</b>  |  L 循环 {_torchRing.PresetLabel}"
                        : "");

                _hudHintText =
                    "1-5 天气  |  T 暂停/继续时间  |  [ ] 前后 1 小时  |  \\ 切时间流速\n" +
                    "C 切相机  |  F 自由飞行  |  L 火把数量  |  B Bloom";

                _hudBloomText =
                    $"Bloom <b>{BloomLabel(_bloomQuality)}</b>  |  B 循环 Half/6 · Half/3 · Quarter/3";

                _hudTitleContent = new GUIContent(_hudTitle);
                _hudContent = new GUIContent(_hudText);
                _hudHintContent = new GUIContent(_hudHintText);
                _hudBloomContent = new GUIContent(_hudBloomText);

                MeasureBlock(_hudStyle, _hudTitle, out float titleW, out float titleH);
                MeasureBlock(_hudStyle, _hudText, out float mainW, out float mainH);
                MeasureBlock(_hudHintStyle, _hudHintText, out float hintW, out float hintH);
                MeasureBlock(_hudStyle, _hudBloomText, out float bloomW, out float bloomH);

                _hudWidth = ComputeHudWidth(
                    Mathf.Max(Mathf.Max(titleW, mainW), Mathf.Max(hintW, bloomW)), Screen.width);
                _hudTitleHeight = titleH;
                _hudMainHeight = mainH;
                _hudHintHeight = hintH;
                _hudBloomHeight = bloomH;
                _hudHeight = titleH + mainH + hintH + bloomH + 6f + 4f;
            }

            var rect = new Rect(12f, 40f, _hudWidth, _hudHeight);
            GUI.DrawTexture(rect, _hudBg);

            var titleRect = new Rect(rect.x + HudInset, rect.y + 6f,
                                     rect.width - HudInset * 2f, _hudTitleHeight);
            var mainRect = new Rect(rect.x + HudInset, titleRect.yMax,
                                    rect.width - HudInset * 2f, _hudMainHeight);
            var hintRect = new Rect(rect.x + HudInset, mainRect.yMax,
                                    rect.width - HudInset * 2f, _hudHintHeight);
            var bloomRect = new Rect(rect.x + HudInset, hintRect.yMax + 4f,
                                     rect.width - HudInset * 2f, _hudBloomHeight);

            const float shadowDx = 1f;
            const float shadowDy = 1f;
            GUI.Label(new Rect(titleRect.x + shadowDx, titleRect.y + shadowDy,
                               titleRect.width, titleRect.height), _hudTitleContent, _hudShadowStyle);
            GUI.Label(new Rect(mainRect.x + shadowDx, mainRect.y + shadowDy,
                               mainRect.width, mainRect.height), _hudContent, _hudShadowStyle);
            GUI.Label(new Rect(hintRect.x + shadowDx, hintRect.y + shadowDy,
                               hintRect.width, hintRect.height), _hudHintContent, _hudHintShadowStyle);
            GUI.Label(new Rect(bloomRect.x + shadowDx, bloomRect.y + shadowDy,
                               bloomRect.width, bloomRect.height), _hudBloomContent, _hudShadowStyle);

            GUI.Label(titleRect, _hudTitleContent, _hudStyle);
            GUI.Label(mainRect, _hudContent, _hudStyle);
            GUI.Label(hintRect, _hudHintContent, _hudHintStyle);
            GUI.Label(bloomRect, _hudBloomContent, _hudStyle);

            HudDrawCount++;
            HudLastRect = rect;
            HudLastScreen = new Vector2Int(Screen.width, Screen.height);
        }

        private void OnDestroy()
        {
            if (_hudBg != null) Destroy(_hudBg);
        }
    }
}
