using UnityEngine;

namespace FishDemo.Gameplay
{
    [DisallowMultipleComponent]
    public sealed class UnderwaterDemoControls : MonoBehaviour
    {
        [SerializeField] private Vector3 _anchor = new Vector3(192f, 258f, 232f);
        [SerializeField] private Vector3 _lookAt = new Vector3(192f, 254f, 246f);
        [SerializeField] private KeyCode _diveKey = KeyCode.U;

        private LakeOrbitCamera _orbit;
        private LakeCameraRig _rig;
        private Camera _diveCamera;
        private bool _diving;

        public bool IsDiving => _diving;

        private void Update()
        {
            if (_diving && Camera.main != _diveCamera)
            {
                Exit();
                return;
            }

            if (!Application.isFocused || !Input.GetKeyDown(_diveKey)) return;

            if (_diving) Exit();
            else Enter();
        }

        private void Enter()
        {
            _rig = FindObjectOfType<LakeCameraRig>();
            _orbit = FindObjectOfType<LakeOrbitCamera>();
            if (_rig != null && !_rig.IsOrbitActive) _rig.Apply(true);
            if (_rig != null && _rig.IsFlying) _rig.ExitFlyMode();

            var cam = Camera.main;
            if (cam == null) return;

            _diveCamera = cam;
            if (_orbit != null) _orbit.Paused = true;

            cam.transform.position = _anchor;
            var forward = _lookAt - _anchor;
            if (forward.sqrMagnitude > 0.0001f)
            {
                cam.transform.rotation = Quaternion.LookRotation(forward.normalized, Vector3.up);
            }

            _diving = true;
            Debug.Log("[UnderwaterDemo] 进入水下机位（U 返回环湖轨道，F 自由飞行）");
        }

        private void Exit()
        {
            _diving = false;
            if (_rig != null && _rig.IsFlying) _rig.ExitFlyMode();

            if (_orbit != null)
            {
                _orbit.Paused = false;
                _orbit.ResumeFromCurrentPosition();
            }

            Debug.Log("[UnderwaterDemo] 返回环湖轨道");
        }

        private void OnDisable()
        {
            if (!_diving) return;
            _diving = false;
            if (_orbit != null) _orbit.Paused = false;
        }

#if UNITY_EDITOR
        private void OnDrawGizmosSelected()
        {
            Gizmos.color = new Color(0.2f, 0.9f, 1f, 0.9f);
            Gizmos.DrawSphere(_anchor, 0.5f);
            Gizmos.DrawLine(_anchor, _lookAt);
        }
#endif
    }
}
