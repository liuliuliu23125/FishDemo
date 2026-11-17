using System;
using UnityEngine;

namespace FishDemo.Gameplay
{
    [DisallowMultipleComponent]
    public sealed class LakeOrbitCamera : MonoBehaviour
    {
        [Tooltip("轨道半径（米）。应大于湖盆半径 190m，才能在岸外看全湖面。")]
        [SerializeField] private float _radius = 300f;

        [Tooltip("离地高度（米）。相机高度 = max(地表高度 + 该值, 水位 + 最低水上高度)。")]
        [SerializeField] private float _groundClearance = 18f;

        [Tooltip("相对水面的最低高度（米）。防止相机在低洼处贴到水面。")]
        [SerializeField] private float _minHeightAboveWater = 26f;

        [Tooltip("高度平滑系数（1/秒）。越大跟随地形越快，越小越平稳。")]
        [SerializeField] private float _heightSmoothing = 2.5f;

        [Tooltip("绕一圈的秒数。120s ≈ 3.9m/s 的线速度，慢到能看清水面而不晕。")]
        [SerializeField] private float _secondsPerRevolution = 120f;

        [Tooltip("起始角度（度）。0 = 湖心正南方（-Z 方向）。")]
        [SerializeField] private float _startAngle = 0f;

        [Tooltip("俯仰角（度）。正数表示低头看湖。")]
        [SerializeField] private float _pitch = 12f;

        [Tooltip("俯仰角的缓慢摆动幅度（度）。0 = 固定俯仰。")]
        [SerializeField] private float _pitchSway = 3f;

        [Tooltip("俯仰摆动的周期（秒）。")]
        [SerializeField] private float _pitchSwayPeriod = 24f;

        [SerializeField] private Vector3 _lakeCenter = new Vector3(192f, 264f, 96f);

        private float _angle;

        private float _elapsed;

        private float _smoothedY = float.NaN;

        private Func<float> _deltaTimeProvider = () => Time.deltaTime;

        public Vector3 LakeCenter => _lakeCenter;

        public bool Paused { get; set; }

        public void OverrideDeltaTime(Func<float> provider)
        {
            _deltaTimeProvider = provider ?? (() => Time.deltaTime);
        }

        public void ResetPose()
        {
            _angle = _startAngle * Mathf.Deg2Rad;
            _elapsed = 0f;
            _smoothedY = float.NaN;
            ApplyPose(_elapsed);
        }

        public void ResumeFromCurrentPosition()
        {
            var p = transform.position;
            float dx = p.x - _lakeCenter.x;
            float dz = p.z - _lakeCenter.z;
            _angle = Mathf.Atan2(dx, dz);
            _elapsed = 0f;
            ApplyPose(_elapsed);
        }

        private void OnEnable()
        {
            ResetPose();
        }

        private void LateUpdate()
        {
            if (Paused) return;

            float dt = _deltaTimeProvider();
            _elapsed += dt;

            if (_secondsPerRevolution > 0.01f)
            {
                _angle += dt * Mathf.PI * 2f / _secondsPerRevolution;
            }
            else
            {
                _angle += dt;
            }

            ApplyPose(_elapsed);
        }

        private void ApplyPose(float t)
        {
            float cos = Mathf.Cos(_angle);
            float sin = Mathf.Sin(_angle);

            float x = _lakeCenter.x + _radius * sin;
            float z = _lakeCenter.z + _radius * cos;

            float y = ResolveHeight(x, z);

            var pos = new Vector3(x, y, z);
            transform.position = pos;

            float pitch = _pitch;
            if (_pitchSway > 0.001f && _pitchSwayPeriod > 0.01f)
            {
                pitch += _pitchSway * Mathf.Sin(t * Mathf.PI * 2f / _pitchSwayPeriod);
            }

            var toCenter = new Vector3(_lakeCenter.x - pos.x, 0f, _lakeCenter.z - pos.z);
            var look = Quaternion.LookRotation(toCenter.normalized, Vector3.up)
                       * Quaternion.Euler(pitch, 0f, 0f);
            transform.rotation = look;
        }

        private float ResolveHeight(float x, float z)
        {
            float waterFloor = _lakeCenter.y + _minHeightAboveWater;
            float target = waterFloor;

            float ground = SampleGroundHeight(x, z);
            if (!float.IsNaN(ground))
            {
                target = Mathf.Max(waterFloor, ground + _groundClearance);
            }
            else if (!float.IsNaN(_smoothedY))
            {
                target = Mathf.Max(waterFloor, _smoothedY);
            }

            if (float.IsNaN(_smoothedY))
            {
                _smoothedY = target;
            }
            else
            {
                float dt = _deltaTimeProvider();
                float k = _heightSmoothing > 0.01f
                    ? 1f - Mathf.Exp(-_heightSmoothing * dt)
                    : 1f;
                _smoothedY = Mathf.Lerp(_smoothedY, target, k);
            }

            return _smoothedY;
        }

        private static float SampleGroundHeight(float x, float z)
        {
            var terrains = Terrain.activeTerrains;
            for (int i = 0; i < terrains.Length; i++)
            {
                var t = terrains[i];
                if (t == null || t.terrainData == null) continue;

                var p = t.transform.position;
                var s = t.terrainData.size;
                if (x < p.x || x > p.x + s.x || z < p.z || z > p.z + s.z) continue;

                return p.y + t.SampleHeight(new Vector3(x, 0f, z));
            }
            return float.NaN;
        }

#if UNITY_EDITOR
        private void OnValidate()
        {
            if (!Application.isPlaying)
            {
                _angle = _startAngle * Mathf.Deg2Rad;
                ApplyPose(0f);
            }
        }

        private void OnDrawGizmosSelected()
        {
            Gizmos.color = new Color(0.2f, 0.9f, 1f, 0.9f);
            const int Segments = 96;
            var prev = Vector3.zero;
            for (int i = 0; i <= Segments; i++)
            {
                float a = i * Mathf.PI * 2f / Segments;
                float x = _lakeCenter.x + _radius * Mathf.Sin(a);
                float z = _lakeCenter.z + _radius * Mathf.Cos(a);
                var p = new Vector3(x, ResolveHeightForGizmo(x, z), z);
                if (i > 0) Gizmos.DrawLine(prev, p);
                prev = p;
            }

            Gizmos.color = new Color(1f, 0.85f, 0.2f, 1f);
            Gizmos.DrawLine(transform.position, _lakeCenter);
        }

        private float ResolveHeightForGizmo(float x, float z)
        {
            float target = _lakeCenter.y + _minHeightAboveWater;
            float ground = SampleGroundHeight(x, z);
            if (!float.IsNaN(ground))
                target = Mathf.Max(target, ground + _groundClearance);
            return target;
        }
#endif
    }
}
