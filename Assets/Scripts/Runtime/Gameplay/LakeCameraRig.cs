using Pegasus;
using UnityEngine;
using WaveHarmonic.Crest;
using Enviro;

namespace FishDemo.Gameplay
{
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

        private bool _pegasusPausedByFly;

        private PegasusFlyingCamera _fly;

        public bool IsOrbitActive { get; private set; }

        public bool IsFlying => _fly != null;

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

            _water = FindObjectOfType<WaterRenderer>();
            _pegasus = FindObjectOfType<PegasusManager>();

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

        public void EnterFlyMode()
        {
            if (_fly != null) return;

            var cam = ActiveCamera;
            if (cam == null) return;

            if (_orbit != null) _orbit.Paused = true;

            _pegasusPausedByFly = false;
            if (!IsOrbitActive && _pegasus != null)
            {
                _pegasus.PauseFlythrough();
                _pegasusPausedByFly = true;
            }

            _fly = cam.gameObject.AddComponent<PegasusFlyingCamera>();
            _fly.m_moveSpeed = _flyMoveSpeed;
            _fly.m_requireRmbToMove = false;
            _fly.m_maxMoveSpeed = Mathf.Max(_flyMaxMoveSpeed, _flyMoveSpeed);
            _fly.m_scrollSpeedStep = _flyScrollSpeedStep;

            Debug.Log($"[LakeCameraRig] {cam.name} 进入 WASD 自由飞行" +
                      $"（WASD 移动 / QE 升降 / 右键转视角 / 滚轮调速 / F 退出；" +
                      $"速度 {_flyMoveSpeed}–{_flyMaxMoveSpeed} m/s，水平范围 {_flyRangeRadius}m）");
        }

        public void ExitFlyMode()
        {
            if (_fly == null) return;

            var cam = ActiveCamera;
            Destroy(_fly);
            _fly = null;

            if (IsOrbitActive && _orbit != null)
            {
                _orbit.Paused = false;
                _orbit.ResumeFromCurrentPosition();
            }
            else
            {
                if (_orbit != null) _orbit.Paused = false;

                if (_pegasusPausedByFly && _pegasus != null)
                {
                    SnapPegasusToNearestPathPoint();
                    _pegasus.ResumeFlythrough();
                    _pegasusPausedByFly = false;
                }
            }

            Debug.Log($"[LakeCameraRig] {(cam != null ? cam.name : "相机")} 退出自由飞行");
        }

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

        public void Apply(bool orbitActive)
        {
            IsOrbitActive = orbitActive;

            SetCameraState(_tourCamera, _tourListener, _tourLoader, !orbitActive);
            SetCameraState(_orbitCamera, _orbitListener, _orbitLoader, orbitActive);

            if (orbitActive && _orbit != null)
            {
                _orbit.Paused = false;
                _orbit.ResetPose();
            }

            if (_water != null)
            {
                _water.Viewer = orbitActive ? _orbitCamera : _tourCamera;
            }

            var enviro = EnviroManager.instance;
            if (enviro != null)
            {
                enviro.ChangeCamera(orbitActive ? _orbitCamera : _tourCamera);
            }

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
            if (loader != null) loader.enabled = active;
        }
    }
}
