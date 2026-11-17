using Pegasus;
using UnityEngine;
using WaveHarmonic.Crest;
using Enviro;

namespace FishDemo.Gameplay
{
    /// <summary>
    /// 在两台相机之间切换：原 Pegasus 巡游相机 ⇄ 环湖轨道相机。
    ///
    /// ── 为什么要一个 rig，而不是"两台相机同时开着" ──
    /// 有三处状态必须**跟着当前渲染相机走**，只换相机是不够的：
    ///
    /// 1. **Crest 的 Viewer**：`WaterRenderer.Viewer` 仍必须指向当前渲染相机——
    ///    水下检测、反射，以及未设置固定 `Viewpoint` 时的 LOD 中心都取它。
    ///    本工程把 LOD 中心固定在湖心（`Lake Viewpoint`，`_Viewpoint` 非空），
    ///    所以水面数据不随相机漂移；但 `Viewer` 仍要随相机切换。
    ///
    /// 2. **SECTR 的 chunk 加载**：`SECTR_RegionLoader` 用**自身 transform** 判定
    ///    加载范围（SECTR_RegionLoader.cs:83-84），不看相机。所以每台相机各自挂一个
    ///    loader，由 rig 只启用当前那台，否则会两份范围叠加、chunk 常驻内存。
    ///
    /// 3. **`Camera.main`**：它返回"第一个 **enabled** 且 tag 为 MainCamera 的相机"。
    ///    若两台都带 tag，返回哪台不确定；若都不带，返回 null。
    ///    GrassBRG 的剔除兜底（GrassBRGManager.cs:1831）等代码依赖它，
    ///    所以 rig 把 tag 也一起搬过去。
    ///
    /// ── 为什么只禁用组件、不 SetActive(false) ──
    /// 每台相机自带 `AudioListener`。禁 GameObject 会把监听器一起关掉，
    /// Unity 会报 "There are no audio listeners in the scene"。
    /// 所以按组件逐个开关，监听器由 rig 显式接管。
    ///
    /// ── 原相机不改进 ──
    /// Pegasus 仍在驱动原相机的 transform（`PegasusManager.m_target` 持有其引用），
    /// 只是它的 Camera 组件被禁用、不参与渲染。切回去时巡游从当时的位置继续，
    /// 不需要重新定位，也不会打断原有行为。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class LakeCameraRig : MonoBehaviour
    {
        [Header("相机（两台都必须有 Camera 组件）")]
        [Tooltip("原 Pegasus 巡游相机。rig 不会修改它的运动逻辑。")]
        [SerializeField] private Camera _tourCamera;

        [Tooltip("环湖轨道相机。")]
        [SerializeField] private Camera _orbitCamera;

        [Header("切换")]
        [Tooltip("切换键。默认 C。")]
        [SerializeField] private KeyCode _toggleKey = KeyCode.C;

        [Tooltip("自由飞行切换键。默认 F，与原 PegasusTourController 一致。")]
        [SerializeField] private KeyCode _flyToggleKey = KeyCode.F;

        [Header("自由飞行")]
        [Tooltip("WASD 基础移动速度（米/秒）。原 120 太快，默认缩到 30。")]
        [SerializeField] private float _flyMoveSpeed = 30f;

        [Tooltip("滚轮调速上限（米/秒）。")]
        [SerializeField] private float _flyMaxMoveSpeed = 120f;

        [Tooltip("相对湖心的最大水平距离（米）。超过会被 LateUpdate 拉回。")]
        [SerializeField] private float _flyRangeRadius = 600f;

        [Tooltip("滚轮每格的调速步长。")]
        [SerializeField] private float _flyScrollSpeedStep = 0.5f;

        [Tooltip("启动时先启用哪一台。true = 环湖轨道（默认展示湖）。")]
        [SerializeField] private bool _startOnOrbit = true;

        private WaterRenderer _water;
        private AudioListener _tourListener;
        private AudioListener _orbitListener;
        private SECTR_RegionLoader _tourLoader;
        private SECTR_RegionLoader _orbitLoader;
        private LakeOrbitCamera _orbit;
        private PegasusManager _pegasus;

        /// <summary>本次飞行是否由 rig 暂停了 Pegasus。只恢复自己暂停过的。</summary>
        private bool _pegasusPausedByFly;

        /// <summary>当前挂在**渲染相机**上的自由飞行组件；未进入飞行模式时为 null。</summary>
        private PegasusFlyingCamera _fly;

        /// <summary>当前是否处于环湖轨道模式。</summary>
        public bool IsOrbitActive { get; private set; }

        /// <summary>当前是否处于 WASD 自由飞行模式。</summary>
        public bool IsFlying => _fly != null;

        /// <summary>当前正在渲染的相机。</summary>
        private Camera ActiveCamera => IsOrbitActive ? _orbitCamera : _tourCamera;

        private void Awake()
        {
            if (_tourCamera == null || _orbitCamera == null)
            {
                Debug.LogError("[LakeCameraRig] 两台相机未绑定，rig 失效", this);
                enabled = false;
                return;
            }

            _tourListener = _tourCamera.GetComponent<AudioListener>();
            _orbitListener = _orbitCamera.GetComponent<AudioListener>();
            _tourLoader = _tourCamera.GetComponent<SECTR_RegionLoader>();
            _orbitLoader = _orbitCamera.GetComponent<SECTR_RegionLoader>();
            _orbit = _orbitCamera.GetComponent<LakeOrbitCamera>();

            // 每帧找一次 WaterRenderer 太浪费；场景里只有一台，初始化时取一次。
            _water = FindObjectOfType<WaterRenderer>();
            _pegasus = FindObjectOfType<PegasusManager>();

            // F 键由 rig 统一接管。原 `PegasusTourController` 也监听 F，
            // 两者同帧响应会各挂一份 PegasusFlyingCamera（重复组件 + 抢 transform）。
            // 这里把它禁掉：rig 已经实现了它的全部职责（飞行/暂停/续接），
            // 且扩展到两台相机。禁用而不是删除，场景结构不变、可随时改回。
            var tourController = _tourCamera.GetComponent<PegasusTourController>();
            if (tourController != null)
            {
                tourController.enabled = false;
                Debug.Log("[LakeCameraRig] 已接管 F 键，禁用原 PegasusTourController（职责已合并到 rig）");
            }

            Apply(_startOnOrbit);
        }

        private void Update()
        {
            if (PegasusInputCompat.GetKeyDown(_toggleKey))
            {
                // 切相机时先退出飞行模式：飞行组件挂在旧相机上，继续留着会让旧相机
                // 在不渲染的情况下仍吃掉 WASD 输入，且轨道脚本恢复后会与飞行抢 transform。
                ExitFlyMode();

                Apply(!IsOrbitActive);
                return;
            }

            if (PegasusInputCompat.GetKeyDown(_flyToggleKey))
            {
                if (IsFlying) ExitFlyMode();
                else EnterFlyMode();
            }
        }

        /// <summary>
        /// 把自由飞行限制在湖心周围的水平圆内。
        /// 必须放 LateUpdate：`PegasusFlyingCamera` 在 Update 里写 transform，
        /// 这里晚一步执行才能覆盖它的位移；轨道脚本在飞行期间已 Paused，不会抢。
        /// 只限制 XZ，不限制 Y —— 允许下潜看水下效果。
        /// </summary>
        private void LateUpdate()
        {
            if (_fly == null || _orbit == null) return;

            var cam = ActiveCamera;
            if (cam == null || _flyRangeRadius <= 0f) return;

            var center = _orbit.LakeCenter;
            var p = cam.transform.position;
            var dx = p.x - center.x;
            var dz = p.z - center.z;
            var sqr = dx * dx + dz * dz;
            if (sqr <= _flyRangeRadius * _flyRangeRadius) return;

            var scale = _flyRangeRadius / Mathf.Sqrt(sqr);
            p.x = center.x + dx * scale;
            p.z = center.z + dz * scale;
            cam.transform.position = p;
        }

        /// <summary>
        /// 进入 WASD 自由飞行。飞行组件挂在**当前渲染相机**上，两台相机都支持。
        ///
        /// ── 为什么必须暂停轨道脚本 ──
        /// `LakeOrbitCamera` 在 `LateUpdate` 里每帧写 transform，比
        /// `PegasusFlyingCamera` 的 `Update` 更晚执行，不暂停就会把飞行位移整帧覆盖掉
        /// （表现为"按 F 没反应、镜头还在自己转"）。
        ///
        /// ── 为什么巡游模式下也要暂停 Pegasus ──
        /// 原 `PegasusTourController` 进入手动飞行时会 `PauseFlythrough()`，
        /// 否则 Pegasus 的 `LateUpdate` 会继续把相机拖回路径点。rig 接管 F 键后
        /// 必须自己做同样的事，否则在巡游相机上按 F 飞不动。
        ///
        /// ── 为什么每次都重建组件 ──
        /// `PegasusFlyingCamera` 把 yaw/pitch 缓存在私有字段里，只在 `Awake` 时从
        /// transform.rotation 取一次。复用同一实例的话，第二次进入飞行模式会沿用
        /// 上一次（可能已被轨道脚本改过）的朝向记录，一按右键视角就突跳。
        /// 每次重新 AddComponent 让 Awake 用当前朝向重新初始化 —— 代价是极少分配，
        /// 这是观察工具，正确性优先。同 `PegasusTourController` 的处理方式。
        /// </summary>
        public void EnterFlyMode()
        {
            if (_fly != null) return;

            var cam = ActiveCamera;
            if (cam == null) return;

            // 暂停轨道驱动，让出 transform 控制权。
            if (_orbit != null) _orbit.Paused = true;

            // 巡游相机被飞行接管时，Pegasus 必须暂停，否则它每帧把相机拖回路径。
            _pegasusPausedByFly = false;
            if (!IsOrbitActive && _pegasus != null)
            {
                _pegasus.PauseFlythrough();
                _pegasusPausedByFly = true;
            }

            _fly = cam.gameObject.AddComponent<PegasusFlyingCamera>();
            _fly.m_moveSpeed = _flyMoveSpeed;
            // 允许 WASD 直接移动，不必先按住右键；视角仍用右键拖动。
            _fly.m_requireRmbToMove = false;
            _fly.m_maxMoveSpeed = Mathf.Max(_flyMaxMoveSpeed, _flyMoveSpeed);
            _fly.m_scrollSpeedStep = _flyScrollSpeedStep;

            Debug.Log($"[LakeCameraRig] {cam.name} 进入 WASD 自由飞行" +
                      $"（WASD 移动 / QE 升降 / 右键转视角 / 滚轮调速 / F 退出；" +
                      $"速度 {_flyMoveSpeed}–{_flyMaxMoveSpeed} m/s，水平范围 {_flyRangeRadius}m）");
        }

        /// <summary>
        /// 退出自由飞行，把控制权交还当前模式（轨道或 Pegasus）。
        ///
        /// 轨道模式复位到圆周上：保留飞行终点会让"半径恒定"的观感崩掉，
        /// 而直接跳回原机位又太突兀 —— `ResetPose` 从起始角度重新开始，
        /// 与"每次切进轨道模式"的行为一致，是这里最可预期的选择。
        /// </summary>
        public void ExitFlyMode()
        {
            if (_fly == null) return;

            var cam = ActiveCamera;
            Destroy(_fly);
            _fly = null;

            if (IsOrbitActive && _orbit != null)
            {
                // 轨道脚本恢复驱动。按当前位置反解角度续接，
                // 而不是 ResetPose（那会瞬移回起始角度，观感是镜头一跳）。
                _orbit.Paused = false;
                _orbit.ResumeFromCurrentPosition();
            }
            else
            {
                if (_orbit != null) _orbit.Paused = false;

                // 恢复 Pegasus 巡游。只有确实是我们暂停的才恢复，
                // 避免覆盖用户手动暂停的状态。
                if (_pegasusPausedByFly && _pegasus != null)
                {
                    // 从路径上最近点续接，而不是阻尼拖回暂停点（同 PegasusTourController 的做法）。
                    SnapPegasusToNearestPathPoint();
                    _pegasus.ResumeFlythrough();
                    _pegasusPausedByFly = false;
                }
            }

            Debug.Log($"[LakeCameraRig] {(cam != null ? cam.name : "相机")} 退出自由飞行");
        }

        /// <summary>
        /// 在整条路径上采样，把巡游进度定位到离当前相机位置最近的点。
        /// 与 `PegasusTourController.SnapToNearestPathPoint` 同一做法：
        /// 直接 Resume 会让 Pegasus 用阻尼把相机一路拖回暂停点，看到的是"倒退飞行"。
        /// </summary>
        private void SnapPegasusToNearestPathPoint()
        {
            if (_pegasus == null || _tourCamera == null) return;

            float total = _pegasus.m_totalDistance;
            if (total <= 0f) return;

            const int Samples = 256;
            float bestDist = 0f;
            float bestSqr = float.MaxValue;
            Vector3 cameraPos = _tourCamera.transform.position;

            for (int i = 0; i < Samples; i++)
            {
                float d = total * i / Samples;
                _pegasus.CalculateTargetAtDistance(d, out Vector3 pos, out _, out _);
                float sqr = (pos - cameraPos).sqrMagnitude;
                if (sqr < bestSqr)
                {
                    bestSqr = sqr;
                    bestDist = d;
                }
            }

            _pegasus.m_totalDistanceTravelled = bestDist;
            _pegasus.MoveTargetTo(bestDist / total);
        }

        /// <summary>
        /// 切换到指定模式。幂等：重复调用同一模式不会产生副作用。
        /// </summary>
        public void Apply(bool orbitActive)
        {
            IsOrbitActive = orbitActive;

            SetCameraState(_tourCamera, _tourListener, _tourLoader, !orbitActive);
            SetCameraState(_orbitCamera, _orbitListener, _orbitLoader, orbitActive);

            // 切进轨道模式时从统一起点开始，避免每次进入的机位都不一样。
            // 切出时保留原相机的位姿，让 Pegasus 从当前位置继续。
            if (orbitActive && _orbit != null)
            {
                _orbit.Paused = false;
                _orbit.ResetPose();
            }

            // Crest 视点跟随当前渲染相机（见类注释 §1）。
            if (_water != null)
            {
                _water.Viewer = orbitActive ? _orbitCamera : _tourCamera;
            }

            // Enviro 的 RenderFeature 只对 EnviroManager.Camera（以及 Cameras 列表）生效。
            // 不同步的话，切过去的那台相机没有天空/体积雾 pass，画面只剩纯色背景。
            var enviro = EnviroManager.instance;
            if (enviro != null)
            {
                enviro.ChangeCamera(orbitActive ? _orbitCamera : _tourCamera);
            }

            // 把 MainCamera tag 搬到当前相机（见类注释 §3）。
            // 只在运行时搬；退出 Play Mode 后场景里的 tag 会恢复，不影响验收脚本。
            if (Application.isPlaying)
            {
                _tourCamera.tag = orbitActive ? "Untagged" : "MainCamera";
                _orbitCamera.tag = orbitActive ? "MainCamera" : "Untagged";
            }

            Debug.Log($"[LakeCameraRig] 当前相机 = {(orbitActive ? "环湖轨道" : "Pegasus 巡游")}");
        }

        private static void SetCameraState(Camera camera, AudioListener listener,
                                           SECTR_RegionLoader loader, bool active)
        {
            if (camera != null) camera.enabled = active;
            if (listener != null) listener.enabled = active;
            // loader 必须精确开关：两台同时启用会让两份加载范围的并集常驻内存。
            if (loader != null) loader.enabled = active;
        }
    }
}
