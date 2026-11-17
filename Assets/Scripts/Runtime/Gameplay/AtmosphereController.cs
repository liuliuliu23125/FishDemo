using Enviro;
using FishDemo.Rendering;
using UnityEngine;
using UnityEngine.Rendering;
using WaveHarmonic.Crest;

namespace FishDemo.Gameplay
{
    /// <summary>
    /// 大气系统运行时配置（昼夜 / 天气 / 体积雾 / 体积光）。
    ///
    /// ── 为什么配置写在运行时而不是预制体序列化 ──
    /// Enviro 的预制体是一份第三方资产，直接在场景实例上改几十个模块字段会变成
    /// 一大片不易 diff 的 prefab override。这里只保留一个控制器组件，
    /// 所有移动端取舍（关体积云、关全局反射探针、体积雾降档）都写成代码，
    /// 便于 review、回滚和按画质档切换。
    ///
    /// ── 程序集位置 ──
    /// 放在 `Runtime/Gameplay/`（无 asmdef，编进 Assembly-CSharp）：Enviro3.Runtime
    /// 是 autoReferenced 的 asmdef，默认程序集能直接引用；而 `FishDemo.Rendering`
    /// 不应为了大气系统引入 Enviro 依赖，草的 BRG 保持独立。
    ///
    /// ── 默认性能档（iQOO 16 / 30fps 目标）──
    /// - 体积云关闭，改用 flat clouds（2D），天气预设自带 flat cloud coverage；
    /// - 体积雾 Low 档（1/4 分辨率 raymarch）+ steps 32；
    /// - 全局反射探针关闭（水面反射本来就是关的，场景里也没有金属高光）；
    /// - 环境探针刷新间隔 0.5s，而不是每帧 DynamicGI.UpdateEnvironment。
    /// 这些都在 ApplySettings() 里，改档位只改这里。
    /// </summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(1000)] // 必须晚于 EnviroManager.Start()（它在那里 StartModules）
    public sealed class AtmosphereController : MonoBehaviour
    {
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
        private int _underwaterState = -1;
        private int _weatherIndex;

        /// <summary>时间流速档位（cycleLengthInMinutes）。1 = 20min/天，最慢 60min/天。</summary>
        private static readonly float[] CyclePresets = { 20f, 5f, 2f, 1f };
        private static readonly string[] CycleLabels = { "20min/天", "5min/天", "2min/天", "1min/天" };
        private int _cycleIndex;

        /// <summary>HUD 用的 GUIStyle，懒创建（OnGUI 里每次 new 会每帧分配）。</summary>
        private GUIStyle _hudStyle;
        private GUIStyle _hudHintStyle;
        private GUIStyle _hudShadowStyle;
        private GUIStyle _hudHintShadowStyle;
        private Texture2D _hudBg;
        /// <summary>创建 <see cref="_hudStyle"/> 时的屏幕尺寸。变了就重建，字号/换行宽度跟着走。</summary>
        private int _hudStyleScreenW = -1;
        private int _hudStyleScreenH = -1;
        // HUD 文本缓存：内容不变时不重新拼字符串（OnGUI 每帧调用多次）。
        private GUIContent _hudContent;
        private GUIContent _hudHintContent;
        private GUIContent _hudTitleContent;
        private string _hudText;
        private string _hudHintText;
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

        /// <summary>自动换天气开关。验收脚本会关掉它，保证截图可复现。</summary>
        public bool AutoWeather
        {
            get => _autoWeather;
            set => _autoWeather = value;
        }

        public bool IsApplied => _applied;

        /// <summary>HUD 开关的当前值。`_showHud` 若没被场景序列化就是字段初始值 true，
        /// 验收需要能读到真实值来判断"看不见"是不是被关掉了。</summary>
        public bool ShowHud
        {
            get => _showHud;
            set => _showHud = value;
        }

        private void Awake()
        {
            // EnviroManager.instance 会 FindAnyObjectByType，Awake 阶段即可拿到。
            _manager = EnviroManager.instance;
        }

        private void OnEnable()
        {
            // 为什么不用 Update 判断水下：验收/截图脚本会在同一帧内把相机沉到水下
            // 再连续 Render()，Update 根本没机会跑。beginCameraRendering 在 pass
            // Execute 之前触发，Enviro 的 Execute 会重新读 quality.fogOverride，
            // 所以这里改字段一定能被当帧看到。
            RenderPipelineManager.beginCameraRendering += OnBeginCameraRendering;
        }

        private void OnDisable()
        {
            RenderPipelineManager.beginCameraRendering -= OnBeginCameraRendering;

            // 只在“我们确实因入水关过雾”时恢复，避免覆盖用户有意配置的 fog=false。
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
            // 只处理游戏相机：SceneView / 反射探针也会触发 beginCameraRendering，
            // 它们的 y 与玩家相机不同，混进来会把质量档来回翻。
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
                // EnviroManager 的 Start 与我们的 Start 顺序由 DefaultExecutionOrder 保证，
                // 但模块加载失败时不要每帧刷日志，静默重试即可。
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
            ApplyUnderwaterFog(_manager.Camera != null ? _manager.Camera : Camera.main);

            // 草不读 WindZone，必须由这里把 Enviro 的风桥接到 BRG 材质。
            // 0.25s 一次足够：Environment 的 windSpeed 本身是插值渐变的。
            _grassWindTimer += Time.deltaTime;
            if (_grassWindTimer >= 0.25f)
            {
                _grassWindTimer = 0f;
                UpdateGrassWind();
            }
        }

        /// <summary>
        /// 设置时刻（0-24）。pauseTime=true 时停止时间流逝，供验收脚本冻结画面。
        /// 注意：SetTimeOfDay 只改 timeOfDay，太阳位置在下一帧 TimeModule.UpdateModule 里算。
        /// </summary>
        public bool SetTimeOfDay(float hour, bool pauseTime)
        {
            if (!_applied || _manager?.Time == null) return false;
            _manager.Time.Settings.simulate = !pauseTime;
            _manager.Time.SetTimeOfDay(Mathf.Repeat(hour, 24f));
            return true;
        }

        /// <summary>立即切换天气（跳过过渡）。空名字或找不到时返回 false。</summary>
        public bool SetWeatherImmediate(string weatherName)
        {
            if (!_applied || _manager?.Weather == null) return false;
            var type = FindWeather(weatherName);
            if (type == null) return false;
            _manager.Weather.ChangeWeatherInstant(type);
            return true;
        }

        /// <summary>带过渡切换天气。返回 false 表示名字不在预设列表里。</summary>
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

        /// <summary>当前是否在时间流逝（Enviro Time.simulate）。</summary>
        public bool IsTimeRunning =>
            _applied && _manager?.Time != null && _manager.Time.Settings.simulate;

        /// <summary>把时刻按给定小时数平移（负数回退）。用于 HUD 的 [ ] 热键与外部调用。</summary>
        public bool ShiftTimeByHours(float hours)
        {
            if (!_applied || _manager?.Time == null) return false;
            float next = Mathf.Repeat(_manager.Time.GetTimeOfDay() + hours, 24f);
            _manager.Time.SetTimeOfDay(next);
            return true;
        }

        /// <summary>暂停/恢复时间流逝。返回切换后的状态。</summary>
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

            // EnviroManager.StartModules() 在 Start 里执行；模块没齐就下一帧再来。
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
            // ── 质量档：QualityModule 每帧把 defaultQuality 的值抄到各模块 ──
            if (_mobileQuality != null)
            {
                _manager.Quality.Settings.defaultQuality = _mobileQuality;
                if (!_manager.Quality.Settings.Qualities.Contains(_mobileQuality))
                    _manager.Quality.Settings.Qualities.Add(_mobileQuality);
            }

            // ── 时间 / 地理位置 ──
            var time = _manager.Time.Settings;
            time.simulate = true;
            time.cycleLengthInMinutes = _cycleLengthMinutes;
            time.latitude = _latitude;
            time.longitude = _longitude;
            time.utcOffset = _utcOffset;
            _manager.Time.SetDateTime(0, 0, Mathf.FloorToInt(_startHour), 15, 10, 2026);
            // HUD 的流速档位从当前配置起步，避免显示与实际不一致。
            _cycleIndex = Mathf.Max(0, System.Array.IndexOf(CyclePresets, _cycleLengthMinutes));

            // ── 天空：月亮用简单模式（省掉真实月面/月晕的额外采样）──
            _manager.Sky.Settings.moonMode = EnviroSky.MoonMode.Simple;

            // ── 环境探针：默认 0.1s 一次 DynamicGI.UpdateEnvironment，移动端过重 ──
            _manager.Lighting.Settings.ambientUpdateEveryFrame = false;
            _manager.Lighting.Settings.ambientUpdateIntervall = _ambientUpdateInterval;

            // ── 反射：全局 cubemap 每 ~0.6s 渲一次，本场景无收益 ──
            _manager.Reflections.Settings.globalReflections = !_disableGlobalReflections;
            _manager.Reflections.Settings.updateDefaultEnvironmentReflections = false;

            // ── 体积雾范围：湖面尺度，别用 PC 的 1000m ──
            _manager.Fog.Settings.maxRange = _volumetricMaxRange;
            _manager.Fog.Settings.maxRangePointSpot = 100f;

            // ── 天气：自动轮换由本脚本驱动，关掉 Enviro 自己的自动逻辑避免双份 ──
            _manager.Weather.globalAutoWeatherChange = false;
            SetWeatherImmediate(_startWeather);
            _weatherIndex = Mathf.Max(0, System.Array.IndexOf(_weatherCycle, _startWeather));

            // ── 相机：跟随当前渲染相机（rig 切换时会再调用 ChangeCamera）──
            var cam = Camera.main;
            if (cam != null) _manager.ChangeCamera(cam);

            UpdateGrassWind();
        }

        /// <summary>
        /// 低太阳角时提高体积散射，让树影光柱可见。
        ///
        /// 为什么必须在 beginCameraRendering 而不是 Update：
        /// EnviroFogModule 会在 Update 里按天气预设重写 scattering；Enviro 的 render pass
        /// 在 Execute 里读 Settings.scattering 生成体积纹理。每帧 pass 之前改才有效，
        /// 且天气混合会把它拉回去，所以不能只设一次。
        ///
        /// solarTime 映射：0=午夜，0.5≈日出/日落，1=正午（EnviroManager.UpdateNonTime）。
        /// </summary>
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

        /// <summary>
        /// Enviro 没有水下检测，体积雾/高度雾在水下仍会应用，把 Crest 的蓝绿吸收
        /// 冲成棕色（实测 LakeWaterVerify 的水下调色比 1.10 → 0.86）。
        /// 这里用 Crest 的 SeaLevel 做最简单的判断：入水关 fog/volumetrics，
        /// 出水恢复。QualityModule 每帧从 quality 资产读值，所以改资产字段即可。
        ///
        /// 为什么不让 Crest 自己处理：Crest 的 UnderwaterRenderer 只负责水下后处理，
        /// 不会关 Enviro 的 render pass；两者的 pass 顺序也不是从属关系。
        /// </summary>
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

        /// <summary>
        /// 天气风 → 草 BRG 材质。草管理器在验收脚本里会被禁用，此时 FindObjectOfType
        /// 返回 null，不报错；正常游戏里它常驻。
        /// </summary>
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
            // 天气：1-5
            if (Input.GetKeyDown(KeyCode.Alpha1)) ChangeWeather("Clear Sky");
            else if (Input.GetKeyDown(KeyCode.Alpha2)) ChangeWeather("Cloudy 2");
            else if (Input.GetKeyDown(KeyCode.Alpha3)) ChangeWeather("Rain");
            else if (Input.GetKeyDown(KeyCode.Alpha4)) ChangeWeather("Foggy");
            else if (Input.GetKeyDown(KeyCode.Alpha5)) ChangeWeather("Storm");
            // 时间：T 暂停/恢复，[ ] 前后 1 小时，\ 切时间流速
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
        }

        /// <summary>轮换时间流速档位。改的是 Time.Settings.cycleLengthInMinutes。</summary>
        public void CycleTimeScale()
        {
            if (!_applied || _manager?.Time == null) return;
            _cycleIndex = (_cycleIndex + 1) % CyclePresets.Length;
            _manager.Time.Settings.cycleLengthInMinutes = CyclePresets[_cycleIndex];
            Debug.Log($"[Atmosphere] 时间流速 = {CycleLabels[_cycleIndex]}");
        }

        /// <summary>
        /// 左上角 HUD：**昼夜/天气操作说明置顶**，其下是时刻 / 天气 / 时间流速，末尾是相机操作。
        ///
        /// ── 为什么操作说明放最上面 ──
        /// 演示时需要先看到怎么操作，再看当前状态；原先提示在最底部，容易被当成脚注忽略。
        ///
        /// ── 为什么用 OnGUI 而不是 Canvas ──
        /// 工程没有 UI 系统（`manifest.json` 无 Input System / uGUI 场景），
        /// 加一个 Canvas 需要额外的 EventSystem 与 prefab 资产。
        /// 这只是开发期观察工具，与 `GrassBRGManager.OnGUI`（GrassBRGManager.cs:948）同一做法。
        /// 正式 UI 接入后应删掉本方法。
        /// </summary>
        /// <summary>HUD 内边距（左右各一份）。文本 rect 与宽度计算必须用同一个值，否则会裁字。</summary>
        private const float HudInset = 8f;

        /// <summary>
        /// HUD 背景块宽度：自然宽度 + 左右内边距，且不超过屏幕可用宽度。
        ///
        /// 抽成静态纯函数是为了可测：`OnGUI` 里的 `CalcSize` 依赖字体与 GUI 事件，
        /// 验证脚本拿不到；而"会不会出屏"只取决于这条夹取规则。
        /// 早先版本直接用 `CalcSize().x`，窄 Game 视图（< ~600px）下操作说明那两行
        /// 会超出右边界，表现为"看不到"。
        /// </summary>
        public static float ComputeHudWidth(float naturalWidth, int screenWidth)
        {
            const float margin = 12f;
            const float minWidth = 64f;
            float available = Mathf.Max(minWidth, screenWidth - margin * 2f);
            return Mathf.Min(naturalWidth + HudInset * 2f, available);
        }

        /// <summary>
        /// HUD 运行时探针：记录 OnGUI 是否真的执行、实际落笔的 rect 与屏幕尺寸。
        /// 为什么需要：验收脚本用 `cam.Render()` 到 RenderTexture 截图，而 OnGUI 在屏幕空间、
        /// **不会进那张 RT**，所以截图永远看不到 HUD，"看不见"既可能是没画也可能是画错位置。
        /// 这两个计数把两种情况分开。
        /// </summary>
        public static int HudDrawCount { get; private set; }
        public static int HudSkipNotAppliedCount { get; private set; }
        public static int HudSkipDisabledCount { get; private set; }
        public static Rect HudLastRect { get; private set; }
        public static Vector2Int HudLastScreen { get; private set; }
        /// <summary>实际使用的文字颜色（诊断"黑字画黑底"）。</summary>
        public static Color HudTextColor { get; private set; }

        /// <summary>
        /// 按行的块尺寸测量（宽取最长行，高取各行之和）。
        /// 为什么不用整块 `CalcSize`：含 `\n` 的富文本整块测量会低估行高，
        /// 实测最后一行被裁掉半截。逐行量再累加与实际绘制一致。
        /// </summary>
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

            // 屏幕尺寸变化必须重建样式：字号按高度缩放，换行宽度按当前宽度算。
            // 之前只建一次，窗口变大/变小后字号与折行宽度都停在旧值。
            if (_hudStyle == null ||
                _hudStyleScreenW != Screen.width || _hudStyleScreenH != Screen.height)
            {
                if (_hudBg == null)
                {
                    _hudBg = new Texture2D(1, 1, TextureFormat.RGBA32, false);
                    // 0.55 在明亮树叶背景上对比不足，小字会糊。0.72 仍能看出背景，但字清楚。
                    _hudBg.SetPixel(0, 0, new Color(0f, 0f, 0f, 0.72f));
                    _hudBg.Apply();
                    _hudBg.hideFlags = HideFlags.DontSave;
                }

                // 整数像素字号：非整数（如 13.5）会让字形落在半像素上，抗锯齿后整体发虚。
                int baseSize = Mathf.Max(14, Mathf.RoundToInt(_hudFontSize * Screen.height / 1080f));

                _hudStyle = new GUIStyle(GUI.skin.label)
                {
                    fontSize = baseSize,
                    richText = true,
                    alignment = TextAnchor.UpperLeft,
                    // 不开 wordWrap：文本每一行都已在代码里手工分好，
                    // 让 IMGUI 再折行反而会让 CalcSize（未折行的自然宽度）与
                    // 实际绘制宽度不一致，表现为右端被框裁掉。框宽按最长行算即可。
                    wordWrap = false,
                    clipping = TextClipping.Overflow,
                };
                // 文字必须是亮色：`GUI.skin.label` 的默认色与浅色编辑器主题相关，
                // 画在深色底上几乎等于黑字黑底。Crest 的调试 HUD 也是显式置白
                // （DebugGUI.cs:201-202）。
                _hudStyle.normal.textColor = Color.white;
                HudTextColor = _hudStyle.normal.textColor;

                // 操作说明单独用一个稍小的样式，而**不是**靠富文本 `<size=-2>`。
                // 原因：IMGUI 的富文本 `<size>` 只认绝对像素（`<size=12>`），
                // 相对写法 `<size=-2>` 是 TextMeshPro 的语法，在 IMGUI 下等于把字号设成负数，
                // 那一段整块不渲染——实测标题/时刻能看见、三条操作说明全丢
                // （Logs/Atmosphere/hud-1920x1080.png 逐行扫只有 4 段文字）。
                //
                // 字号不低于 14：中文在 13px 以下笔画会糊成一团，宁可多占两行。
                // 颜色接近白而不是 0.82 灰：灰色小字叠在半透明黑底上对比度不够。
                _hudHintStyle = new GUIStyle(_hudStyle)
                {
                    fontSize = Mathf.Max(14, Mathf.RoundToInt(baseSize * 0.85f)),
                    richText = false,     // 说明行不用富文本，彻底避开 <size> 语法
                };
                _hudHintStyle.normal.textColor = new Color(0.93f, 0.93f, 0.93f, 1f);

                // IMGUI 没有描边 API，标准做法是先用深色偏移 1px 画一遍当投影，
                // 再画正文。文字压在明暗交错的树叶/天空上时，这一步是清晰度的关键。
                _hudShadowStyle = new GUIStyle(_hudStyle);
                _hudShadowStyle.normal.textColor = new Color(0f, 0f, 0f, 0.85f);
                _hudHintShadowStyle = new GUIStyle(_hudHintStyle);
                _hudHintShadowStyle.normal.textColor = new Color(0f, 0f, 0f, 0.85f);

                _hudStyleScreenW = Screen.width;
                _hudStyleScreenH = Screen.height;
                // 样式变了，文本尺寸必须重算。
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

            // OnGUI 每帧会被调用多次（Layout / Repaint 各一次），字符串拼接与 GUIContent
            // 都会产生托管垃圾。本工程正在治 GC 尖峰，观察工具自己不该制造噪声：
            // 只有显示内容真的变了才重建文本，其余帧复用缓存。
            if (_hudText == null || _hudHour != hh || _hudMinute != mm ||
                _hudRunning != IsTimeRunning || _hudWeather != CurrentWeatherName ||
                _hudCycle != _cycleIndex)
            {
                _hudHour = hh;
                _hudMinute = mm;
                _hudRunning = IsTimeRunning;
                _hudWeather = CurrentWeatherName;
                _hudCycle = _cycleIndex;

                // 第一块（顶部）：键盘操作提示。演示时要先看到"能按什么"，
                // 所以放最前面，用和大字同级的粗体标题。
                _hudTitle = "<b>键盘操作提示</b>";

                // 主信息（大字、可加粗）。不用 <size>，字号由 _hudStyle 决定。
                _hudText =
                    $"<b>昼夜 / 天气</b>  |  <b>时间 {hh:00}:{mm:00}</b>（{phase}）" +
                    $"{(IsTimeRunning ? "" : "  <color=#ffcc66>[已暂停]</color>")}\n" +
                    $"天气 <b>{CurrentWeatherName}</b>  |  流速 {CycleLabels[Mathf.Clamp(_cycleIndex, 0, CycleLabels.Length - 1)]}";

                // 操作说明（小字、单色）。与主信息分开画，字号由 _hudHintStyle 决定。
                // 只放玩家真正会用的键：昼夜/天气/相机。托管堆状态属诊断信息，
                // 已从 HUD 移除——它每帧有波动，会一直触发文本重建，也让画面杂乱。
                _hudHintText =
                    "1-5 天气  |  T 暂停/继续时间  |  [ ] 前后 1 小时  |  \\ 切时间流速\n" +
                    "C 切相机  |  F 自由飞行（WASD/QE/右键/滚轮）";

                _hudTitleContent = new GUIContent(_hudTitle);
                _hudContent = new GUIContent(_hudText);
                _hudHintContent = new GUIContent(_hudHintText);

                // 逐行测量而不是对整块调 CalcSize：IMGUI 对含 `\n` 的富文本
                // 整块 CalcSize 会低估行高（实测最后一行被裁半截），
                // 分行量再加行间距才与实际绘制一致。
                MeasureBlock(_hudStyle, _hudTitle, out float titleW, out float titleH);
                MeasureBlock(_hudStyle, _hudText, out float mainW, out float mainH);
                MeasureBlock(_hudHintStyle, _hudHintText, out float hintW, out float hintH);

                _hudWidth = ComputeHudWidth(Mathf.Max(titleW, Mathf.Max(mainW, hintW)), Screen.width);
                _hudTitleHeight = titleH;
                _hudMainHeight = mainH;
                _hudHeight = titleH + mainH + hintH + 6f;
            }

            // y=40：草 BRG 的调试标签占着 (12,12) 那一条，避让开避免文字叠在一起。
            var rect = new Rect(12f, 40f, _hudWidth, _hudHeight);
            GUI.DrawTexture(rect, _hudBg);

            // 两块分别画，各用自己的字号。合在一起画就必须用 <size> 控制大小差异，
            // 而 IMGUI 不支持相对尺寸（见上面 _hudHintStyle 的注释）。
            // 左右各留 HudInset，与 ComputeHudWidth 加的边距一一对应——
            // 少减一次就会让每行比可用宽度宽 2×HudInset，右端被裁（实测 [GC] 行截断）。
            var titleRect = new Rect(rect.x + HudInset, rect.y + 6f,
                                     rect.width - HudInset * 2f, _hudTitleHeight);
            var mainRect = new Rect(rect.x + HudInset, titleRect.yMax,
                                    rect.width - HudInset * 2f, _hudMainHeight);
            var hintRect = new Rect(rect.x + HudInset, mainRect.yMax,
                                    rect.width - HudInset * 2f,
                                    rect.height - _hudTitleHeight - _hudMainHeight - 6f);

            // 先画深色投影（偏移 1px）再画正文：IMGUI 没有描边，
            // 文字压在明暗交错的树叶/天空上时，只靠半透明底色会发虚、边缘与背景混在一起。
            const float shadowDx = 1f;
            const float shadowDy = 1f;
            GUI.Label(new Rect(titleRect.x + shadowDx, titleRect.y + shadowDy,
                               titleRect.width, titleRect.height), _hudTitleContent, _hudShadowStyle);
            GUI.Label(new Rect(mainRect.x + shadowDx, mainRect.y + shadowDy,
                               mainRect.width, mainRect.height), _hudContent, _hudShadowStyle);
            GUI.Label(new Rect(hintRect.x + shadowDx, hintRect.y + shadowDy,
                               hintRect.width, hintRect.height), _hudHintContent, _hudHintShadowStyle);

            GUI.Label(titleRect, _hudTitleContent, _hudStyle);
            GUI.Label(mainRect, _hudContent, _hudStyle);
            GUI.Label(hintRect, _hudHintContent, _hudHintStyle);

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
