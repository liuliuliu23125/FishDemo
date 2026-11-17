using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// Pegasus 巡游的运行时验收：证明「接入真的生效」，而不是只看场景里有没有节点。
///
/// 检查 5 件事：
///   1. Pegasus 是否自动启动，且相机被它驱动（位置随时间变化）；
///   2. 相机是否沿着环形路线走（半径接近设定值）；
///   3. SECTR 是否真的随相机移动加载/卸载 chunk（本次接入的核心目的）；
///   4. 相机高度是否落在烘焙的离地区间内（证明高度烘焙有效，没有掉进地里）；
///   5. F 键切手动 / 切回自动是否可用。
///
/// 为什么必须进 Play Mode：SECTR 的加载是 SceneManager.LoadSceneAsync，编辑期不跑；
/// Pegasus 的驱动在 LateUpdate。静态检查无法覆盖这两点。
///
/// ── 跨域重载的处理（关键，实测踩过两次）──
/// 设置 EditorApplication.isPlaying = true 会触发**域重载**（日志可见
/// "Reloading assemblies for play mode"），进程内的静态字段和
/// EditorApplication.update 订阅全部被清空。所以：
///   * 用 SessionState 存「待执行」标记，它能在域重载后存活；
///   * 用 [InitializeOnLoad] 在每次域重载后重新注册轮询，
///     发现标记且已进入播放态时，才创建采样驱动。
///
/// 用法：
///   Unity.exe -batchmode -projectPath <proj> -executeMethod PegasusTourVerify.Run -logFile <log>
/// 结果写 Logs/pegasus-tour-verify.txt
/// </summary>
[InitializeOnLoad]
public static class PegasusTourVerify
{
    private const string Tag = "[PegasusVerify]";
    private const string WorldScenePath = "Assets/Scenes/World.unity";
    private const string ResultPath = "Logs/pegasus-tour-verify.txt";

    /// <summary>SessionState 键：Run() 置位，域重载后由轮询消费。</summary>
    private const string PendingKey = "FishDemo.PegasusVerify.Pending";

    /// <summary>
    /// 观测时长（秒）。
    /// 阶段 1（0~60%）让 Pegasus 真实驱动；阶段 2（60% 之后）全圈扫描，
    /// 需要 96 步 × 12 帧 ≈ 19 秒（60fps），所以总时长给到 60 秒。
    /// </summary>
    private const float ObserveSeconds = 60f;

    /// <summary>静态构造在每次域重载后执行，包括进入 Play Mode 的那一次。</summary>
    static PegasusTourVerify()
    {
        EditorApplication.update += Poll;
    }

    public static void Run()
    {
        var sb = new StringBuilder();
        void W(string s) { Debug.Log($"{Tag} {s}"); sb.AppendLine(s); }

        var scene = EditorSceneManager.OpenScene(WorldScenePath, OpenSceneMode.Single);

        var manager = Object.FindObjectOfType<Pegasus.PegasusManager>();
        var camera = FindTourCamera(manager);

        if (manager == null) { W("FAIL：场景里没有 PegasusManager"); Flush(sb); return; }
        if (camera == null) { W("FAIL：场景里没有相机"); Flush(sb); return; }

        W($"前置：Manager={manager.name}，Target={(manager.m_target ? manager.m_target.name : "null")}，" +
          $"POI={manager.transform.childCount}，总里程={manager.m_totalDistance / 1000f:F2}km");
        W($"Manager 高度校验={manager.m_heightCheckType}（None 表示已用烘焙高度），" +
          $"自动启动={manager.m_autoStartAtRuntime}");

        // 记录前置信息供域重载后复用（域重载会清掉局部变量）。
        SessionState.SetString(PendingKey + ".Pre", sb.ToString());
        SessionState.SetBool(PendingKey, true);

        EditorApplication.isPlaying = true;
    }

    /// <summary>轮询：域重载后标记仍在，且已进入播放态时启动采样。</summary>
    private static void Poll()
    {
        if (!SessionState.GetBool(PendingKey, false))
            return;
        if (!EditorApplication.isPlaying)
            return;

        // 立刻清除标记，避免重复创建。
        SessionState.SetBool(PendingKey, false);
        EditorApplication.update -= Poll;

        var sb = new StringBuilder(SessionState.GetString(PendingKey + ".Pre", string.Empty));
        void W(string s) { Debug.Log($"{Tag} {s}"); sb.AppendLine(s); }

        // 重新取引用：进 Play Mode 重载了场景，之前的对象引用已失效。
        var manager = Object.FindObjectOfType<Pegasus.PegasusManager>();
        var camera = FindTourCamera(manager);

        if (manager == null || camera == null)
        {
            W("FAIL：进入 Play Mode 后找不到 Manager 或相机");
            Flush(sb);
            EditorApplication.isPlaying = false;
            EditorApplication.Exit(1);
            return;
        }

        PrepareTourCamera(camera);

        var driver = new GameObject("~PegasusVerifyDriver").AddComponent<VerifyDriver>();
        driver.Begin(W, sb, camera, manager);
    }

    /// <summary>
    /// 优先用 PegasusManager 的 target 作为巡游相机。
    /// 当前场景的 LakeCameraRig 默认切到环湖轨道相机，并把 MainCamera tag 搬走；
    /// 继续按 tag 找会拿到轨道相机，测到的就不是 Pegasus 巡游。
    /// </summary>
    private static Camera FindTourCamera(Pegasus.PegasusManager manager)
    {
        if (manager != null && manager.m_target != null)
        {
            var targetCamera = manager.m_target.GetComponent<Camera>();
            if (targetCamera != null)
                return targetCamera;
        }

        return Object.FindObjectsOfType<Camera>()
            .FirstOrDefault(c => c.CompareTag("MainCamera")) ?? Object.FindObjectOfType<Camera>();
    }

    /// <summary>
    /// 停掉 LakeCameraRig / 轨道相机，恢复巡游相机的 Camera 与 SECTR loader。
    /// 否则 rig 会在 Start 里禁用巡游相机和它的 loader，导致整个测试 0 chunk。
    /// </summary>
    private static void PrepareTourCamera(Camera camera)
    {
        if (camera == null)
            return;

        var rig = Object.FindObjectOfType<FishDemo.Gameplay.LakeCameraRig>();
        if (rig != null)
            rig.enabled = false;

        var orbit = Object.FindObjectOfType<FishDemo.Gameplay.LakeOrbitCamera>();
        if (orbit != null)
        {
            orbit.enabled = false;
            var orbitCamera = orbit.GetComponent<Camera>();
            if (orbitCamera != null)
            {
                orbitCamera.enabled = false;
                var orbitLoader = orbitCamera.GetComponent<SECTR_RegionLoader>();
                if (orbitLoader != null)
                    orbitLoader.enabled = false;
            }
        }

        var tourController = camera.GetComponent<FishDemo.Gameplay.PegasusTourController>();
        if (tourController != null)
            tourController.enabled = false;

        camera.enabled = true;
        var loader = camera.GetComponent<SECTR_RegionLoader>();
        if (loader != null)
            loader.enabled = true;
    }

    /// <summary>
    /// 轮询驱动。做成 MonoBehaviour 是为了拿 Update 回调；
    /// DontSave 保证不污染场景、不被保存进 World.unity。
    /// </summary>
    private sealed class VerifyDriver : MonoBehaviour
    {
        private System.Action<string> _w;
        private StringBuilder _sb;
        private Camera _camera;
        private Pegasus.PegasusManager _manager;

        private float _t0 = -1f;
        private Vector3 _lastPos;
        private float _movedDistance;
        private float _minRadius = float.MaxValue;
        private float _maxRadius;
        private float _minY = float.MaxValue, _maxY = float.MinValue;
        private int _maxLoadedChunks;
        private int _minLoadedChunks = int.MaxValue;
        private readonly HashSet<string> _loadedSeen = new HashSet<string>();
        private int _sampleCount;
        private bool _sweeping;
        private bool _sweepDone;
        private int _sweepStep;

        /// <summary>全圈扫描步数。整圈 6.92km，取 96 步 → 每步约 72m，远小于 512m 的 chunk 尺寸。</summary>
        private const int SweepSteps = 96;

        /// <summary>
        /// 扫描时每步停留的**秒数**。不能用帧数：batchmode 帧率可达数百 fps，
        /// 12 帧可能只有 30ms，而 SECTR 补丁后 9 个 chunk 的激活需要约 0.7s。
        /// 0.8s 覆盖“加载 + 逐块激活”后仍留一点余量。
        /// </summary>
        private const float SweepDwellSeconds = 0.8f;

        private float _sweepDwellUntil;
        private SECTR_Chunk[] _chunks;

        private bool _toggleTested;
        private bool _manualEntered;
        private bool _autoRestored;
        private bool _finished;

        public void Begin(System.Action<string> w, StringBuilder sb, Camera camera,
            Pegasus.PegasusManager manager)
        {
            _w = w; _sb = sb; _camera = camera; _manager = manager;
            // chunk 组件都在主场景里，数量固定；缓存起来避免每帧 FindObjectsOfType。
            _chunks = Object.FindObjectsOfType<SECTR_Chunk>();
            gameObject.hideFlags = HideFlags.DontSave;
        }

        private void Update()
        {
            if (_finished)
                return;

            // 第一帧只记录起点：此时 Pegasus 的 Start 刚跑完。
            if (_t0 < 0f)
            {
                _t0 = Time.time;
                _lastPos = _camera.transform.position;
                _w($"Play 首帧：相机 {_lastPos}，Pegasus 状态 {_manager.m_currentState}，" +
                   $"已加载 chunk {CountLoaded()}");
                return;
            }

            float elapsed = Time.time - _t0;
            Vector3 pos = _camera.transform.position;

            _movedDistance += Vector3.Distance(pos, _lastPos);
            _lastPos = pos;

            // 半径 / 高度只在阶段 1（Pegasus 真实驱动期）统计。
            // 阶段 2 由本驱动直接摆相机，统计进去会把两个口径混在一起。
            if (!_sweeping)
            {
                float radius = new Vector2(pos.x, pos.z).magnitude;
                _minRadius = Mathf.Min(_minRadius, radius);
                _maxRadius = Mathf.Max(_maxRadius, radius);
                _minY = Mathf.Min(_minY, pos.y);
                _maxY = Mathf.Max(_maxY, pos.y);
            }

            int loaded = CountLoaded();
            _maxLoadedChunks = Mathf.Max(_maxLoadedChunks, loaded);
            _minLoadedChunks = Mathf.Min(_minLoadedChunks, loaded);
            _sampleCount++;

            if (!_toggleTested && elapsed > ObserveSeconds * 0.5f)
            {
                _toggleTested = true;
                TestManualToggle();
            }

            // 阶段2 按 wall-clock 每步 0.8s，96 步约 77s；不能到 60s 就强行结束，
            // 否则只扫到前 30 步，触达数会被误判成很低。
            if (elapsed >= ObserveSeconds && (!_sweeping || _sweepDone))
            {
                _finished = true;
                Report();
                return;
            }

            // ── 阶段 2：全圈流式扫描 ──
            //
            // 为什么不在阶段 1 就扫：MoveTargetTo 会直接写 m_target.transform，
            // 而 Pegasus 的 LateUpdate 同时在用 Vector3.Slerp 把相机往它自己算的位置拉。
            // 两者互相打架，相机会飞出世界（实测 Y 冲到 1346m、半径掉到 247m）。
            // 所以先 StopFlythrough 让 Pegasus 彻底停手，再由本驱动独占相机。
            if (elapsed > ObserveSeconds * 0.6f && !_sweeping)
            {
                _sweeping = true;
                _manager.StopFlythrough();
                // StopFlythrough 仍可能在 LateUpdate 把相机拉回最后目标；
                // 全圈扫描要独占相机，直接禁用 Manager，避免每帧覆盖驱动器的位置。
                _manager.enabled = false;
                _w($"阶段2：已停用 Pegasus，开始全圈扫描（{SweepSteps} 步）");
            }

            if (_sweeping)
            {
                if (_sweepStep < SweepSteps)
                {
                    // 每步停足 wall-clock 时间再前进。
                    //
                    // 为什么不能按帧停：batchmode 帧率可达数百 fps，12 帧可能只有 30ms，
                    // 而补丁后 9 个 chunk 要按 50ms 间隔发起加载，激活再错开（约 0.5s+）。
                    // 按帧停会把“还没激活”误判成“没加载”。
                    if (Time.unscaledTime < _sweepDwellUntil)
                        return;

                    float pct = (float)_sweepStep / SweepSteps;
                    _manager.CalculateTargetAtDistance(
                        pct * _manager.m_totalDistance, out Vector3 p, out Quaternion r, out _);
                    _camera.transform.position = p;
                    _camera.transform.rotation = r;
                    _sweepStep++;
                    _sweepDwellUntil = Time.unscaledTime + SweepDwellSeconds;
                }
                else if (!_sweepDone)
                {
                    _sweepDone = true;
                    _w($"阶段2 完成：触达 {_loadedSeen.Count} 个 chunk，当前驻留 {CountLoaded()}");
                }
            }
        }

        /// <summary>统计当前已加载的 chunk，并累积「整个观测期触达过」的集合。</summary>
        private int CountLoaded()
        {
            int loaded = 0;
            if (_chunks == null)
                return 0;

            foreach (var c in _chunks)
            {
                if (c == null || !c.IsLoaded())
                    continue;
                loaded++;
                _loadedSeen.Add(c.ScenePath);
            }
            return loaded;
        }

        private void TestManualToggle()
        {
            var controller = _camera.GetComponent<FishDemo.Gameplay.PegasusTourController>();
            if (controller == null)
            {
                _w("WARN：相机上没有 PegasusTourController，跳过切换验证");
                return;
            }

            // 反射调私有方法，避免为了测试给运行时脚本加公共 API。
            const System.Reflection.BindingFlags Flags =
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
            var enterManual = typeof(FishDemo.Gameplay.PegasusTourController).GetMethod("EnterManualMode", Flags);
            var enterAuto = typeof(FishDemo.Gameplay.PegasusTourController).GetMethod("EnterAutoMode", Flags);
            if (enterManual == null || enterAuto == null)
            {
                _w("WARN：找不到 EnterManualMode/EnterAutoMode");
                return;
            }

            enterManual.Invoke(controller, null);
            _manualEntered = controller.IsManual
                             && _manager.m_currentState == Pegasus.PegasusConstants.FlythroughState.Paused;
            _w($"切手动：IsManual={controller.IsManual}，Pegasus={_manager.m_currentState}");

            enterAuto.Invoke(controller, null);
            _autoRestored = !controller.IsManual
                            && _manager.m_currentState == Pegasus.PegasusConstants.FlythroughState.Started;
            _w($"切回自动：IsManual={controller.IsManual}，Pegasus={_manager.m_currentState}");
        }

        private void Report()
        {
            _w("");
            _w("===== 观测结果 =====");
            _w($"采样帧数 {_sampleCount}，累计位移 {_movedDistance:F1}m");
            _w($"相机半径范围 [{_minRadius:F0}, {_maxRadius:F0}]m（设定 1100m）");
            _w($"相机 Y 范围 [{_minY:F1}, {_maxY:F1}]");
            _w($"已加载 chunk 数 [{_minLoadedChunks}, {_maxLoadedChunks}]，" +
               $"整个周期触达 {_loadedSeen.Count} 个不同 chunk");

            bool driven = _movedDistance > 100f;
            bool onRoute = _minRadius > 1000f && _maxRadius < 1200f;
            // 流式判定：整圈扫下来触达的 chunk 必须明显多于同时驻留数。
            // 驻留上限由 RegionLoader.LoadSize=768 决定；实测最多 9 块（512m chunk），
            // 采样点位置只命中 4 块不代表运行时上限。
            bool streaming = _loadedSeen.Count > _maxLoadedChunks + 4;
            bool heightOk = _minY > 200f && _maxY < 500f;   // 贴地 3~8m：地面 280~350 + 8
            bool toggleOk = _manualEntered && _autoRestored;

            _w("");
            _w($"[1] Pegasus 驱动相机（位移>100m）     : {(driven ? "PASS" : "FAIL")} ({_movedDistance:F1}m)");
            _w($"[2] 沿环形路线（半径 1000~1200m）     : {(onRoute ? "PASS" : "FAIL")} [{_minRadius:F0},{_maxRadius:F0}]");
            _w($"[3] SECTR 动态加载（触达 ≫ 同时驻留） : {(streaming ? "PASS" : "FAIL")} " +
               $"触达 {_loadedSeen.Count}/36 / 驻留上限 {_maxLoadedChunks}");
            _w($"[4] 高度贴地（200~500m）              : {(heightOk ? "PASS" : "FAIL")} [{_minY:F1},{_maxY:F1}]");
            _w($"[5] F 键切换自动/手动                 : {(toggleOk ? "PASS" : "FAIL")}");
            _w("    注：[1][2][4] 取自阶段1（Pegasus 真实驱动），[3] 取自阶段2（全圈扫描）");

            bool all = driven && onRoute && streaming && heightOk && toggleOk;
            _w("");
            _w(all ? "==== 结论：PASS ====" : "==== 结论：FAIL ====");
            _w("");
            _w("触达的 chunk：");
            foreach (var p in _loadedSeen.OrderBy(x => x))
                _w("  " + p);

            Flush(_sb);

            EditorApplication.isPlaying = false;
            gameObject.AddComponent<QuitAfterFrames>().Init();
        }
    }

    private sealed class QuitAfterFrames : MonoBehaviour
    {
        private int _frames;
        public void Init() { hideFlags = HideFlags.DontSave; }

        private void Update()
        {
            if (++_frames < 30) return;
            EditorApplication.Exit(0);
        }
    }

    private static void Flush(StringBuilder sb)
    {
        Directory.CreateDirectory("Logs");
        File.WriteAllText(ResultPath, sb.ToString());
    }
}
