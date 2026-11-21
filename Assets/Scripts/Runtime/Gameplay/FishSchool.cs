using System;
using UnityEngine;

namespace FishDemo.Gameplay
{
    [DisallowMultipleComponent]
    public sealed class FishSchool : MonoBehaviour
    {
        [Header("实例来源")]
        [SerializeField] private GameObject _prefab;
        [SerializeField] private Mesh _mesh;
        [SerializeField] private Mesh _meshFar;
        [SerializeField] private Material _material;
        [SerializeField] private Material _materialFar;
        [SerializeField] private float _lodDistance = 28f;

        [Header("规模")]
        [SerializeField] private int _count = 100;
        [SerializeField] private float _fishLength = 0.3f;
        [SerializeField] private int _seed = 1;

        [Header("运动")]
        [SerializeField] private Vector3 _center = new Vector3(192f, 255f, 246f);
        [SerializeField] private Vector3 _radius = new Vector3(10f, 2f, 10f);
        [SerializeField] private float _orbitSpeed = 0.18f;
        [SerializeField] private float _verticalSpeed = 0.55f;
        [SerializeField] private float _schoolDriftRadius = 6f;
        [SerializeField] private float _schoolDriftSpeed = 0.06f;
        [SerializeField] private float _modelYaw;
        [SerializeField] private bool _upright;
        [SerializeField] private float _bankAngle = 10f;

        [Header("水深约束")]
        [SerializeField] private float _minAboveBed = 1.2f;
        [SerializeField] private float _minBelowSurface = 1f;

        private Transform[] _fish;
        private MeshFilter[] _meshFilters;
        private MeshRenderer[] _meshRenderers;
        private float _lodTimer;
        private bool _nearLod = true;
        private float[] _phase;
        private float[] _radiusScale;
        private float[] _verticalScale;
        private float[] _speedScale;
        private Vector3[] _lastPosition;
        private bool[] _hasVelocity;
        private UnderwaterEcologyController _ecology;
        private float _waterLevel = 264f;
        private float _startTime;
        private bool _initialized;

        public int Count => _fish != null ? _fish.Length : 0;
        public Vector3 Center => _center;

        private void Awake()
        {
            _ecology = GetComponentInParent<UnderwaterEcologyController>();
            if (_ecology == null) _ecology = FindObjectOfType<UnderwaterEcologyController>();
            if (_ecology != null) _waterLevel = _ecology.WaterLevel;
            _startTime = Time.time;
            Build();
            _initialized = true;
        }

        private void Build()
        {
            if (_count <= 0) return;

            var random = new System.Random(_seed);
            _fish = new Transform[_count];
            _phase = new float[_count];
            _radiusScale = new float[_count];
            _verticalScale = new float[_count];
            _speedScale = new float[_count];
            _lastPosition = new Vector3[_count];
            _hasVelocity = new bool[_count];
            _meshFilters = new MeshFilter[_count];
            _meshRenderers = new MeshRenderer[_count];

            for (int i = 0; i < _count; i++)
            {
                var wrapper = CreateInstance();
                if (wrapper == null) continue;

                wrapper.transform.SetParent(transform, false);
                wrapper.transform.position = _center;
                _fish[i] = wrapper.transform;
                _phase[i] = (float)random.NextDouble() * Mathf.PI * 2f;
                _radiusScale[i] = 0.55f + (float)random.NextDouble() * 0.45f;
                _verticalScale[i] = 0.35f + (float)random.NextDouble() * 0.65f;
                _speedScale[i] = 0.8f + (float)random.NextDouble() * 0.4f;
                _lastPosition[i] = _center;
                _meshFilters[i] = wrapper.GetComponentInChildren<MeshFilter>(true);
                _meshRenderers[i] = wrapper.GetComponentInChildren<MeshRenderer>(true);
            }
        }

        private GameObject CreateInstance()
        {
            GameObject model;
            if (_prefab != null)
            {
                model = Instantiate(_prefab);
            }
            else if (_mesh != null && _material != null)
            {
                model = new GameObject("Model");
                model.AddComponent<MeshFilter>().sharedMesh = _mesh;
                model.AddComponent<MeshRenderer>().sharedMaterial = _material;
            }
            else
            {
                return null;
            }

            var wrapper = new GameObject("Fish");
            model.transform.SetParent(wrapper.transform, false);

            foreach (var renderer in model.GetComponentsInChildren<Renderer>(true))
            {
                renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                renderer.receiveShadows = false;
            }

            foreach (var animator in model.GetComponentsInChildren<Animator>(true))
            {
                animator.cullingMode = AnimatorCullingMode.CullUpdateTransforms;
            }

            float length = MeasureLength(model);
            if (length > 0.001f)
            {
                float scale = _fishLength / length;
                wrapper.transform.localScale = Vector3.one * scale;
            }

            return wrapper;
        }

        private static float MeasureLength(GameObject go)
        {
            bool hasBounds = false;
            var bounds = new Bounds(Vector3.zero, Vector3.zero);
            foreach (var renderer in go.GetComponentsInChildren<Renderer>(true))
            {
                var mesh = renderer is SkinnedMeshRenderer skinned ? skinned.sharedMesh : null;
                if (mesh == null)
                {
                    var filter = renderer.GetComponent<MeshFilter>();
                    mesh = filter != null ? filter.sharedMesh : null;
                }

                Bounds b = mesh != null
                    ? mesh.bounds
                    : renderer.bounds;

                var matrix = renderer.transform.localToWorldMatrix;
                b.center = matrix.MultiplyPoint3x4(b.center);
                b.size = matrix.MultiplyVector(b.size);

                if (!hasBounds)
                {
                    bounds = b;
                    hasBounds = true;
                }
                else
                {
                    bounds.Encapsulate(b);
                }
            }

            if (!hasBounds) return 0f;
            return Mathf.Max(bounds.size.z, bounds.size.x);
        }

        private void Update()
        {
            if (!_initialized || _fish == null || _fish.Length == 0) return;
            if (_ecology != null && !_ecology.EcologyEnabled) return;

            if (_meshFar != null && _mesh != null)
            {
                _lodTimer -= Time.deltaTime;
                if (_lodTimer <= 0f)
                {
                    _lodTimer = 0.5f;
                    var cam = Camera.main;
                    bool near = cam == null || (cam.transform.position - _center).sqrMagnitude < _lodDistance * _lodDistance;
                    if (near != _nearLod)
                    {
                        _nearLod = near;
                        for (int i = 0; i < _meshFilters.Length; i++)
                        {
                            if (_meshFilters[i] != null) _meshFilters[i].sharedMesh = near ? _mesh : _meshFar;
                            if (_meshRenderers[i] != null && _materialFar != null)
                                _meshRenderers[i].sharedMaterial = near ? _material : _materialFar;
                        }
                    }
                }
            }

            float t = Time.time - _startTime;
            float drift = _schoolDriftRadius;
            var center = _center + new Vector3(
                Mathf.Sin(t * _schoolDriftSpeed + _seed * 0.37f) * drift,
                Mathf.Sin(t * _schoolDriftSpeed * 0.7f + _seed * 0.91f) * drift * 0.3f,
                Mathf.Cos(t * _schoolDriftSpeed * 0.83f + _seed * 0.53f) * drift);

            for (int i = 0; i < _fish.Length; i++)
            {
                var fish = _fish[i];
                if (fish == null) continue;

                float angle = _phase[i] + t * _orbitSpeed * _speedScale[i];
                float r = _radiusScale[i];
                float y = center.y + Mathf.Sin(t * _verticalSpeed * _speedScale[i] + _phase[i]) * _radius.y * _verticalScale[i];
                var target = new Vector3(
                    center.x + Mathf.Cos(angle) * _radius.x * r,
                    y,
                    center.z + Mathf.Sin(angle) * _radius.z * r);

                float bed = _ecology != null ? _ecology.LakeBedHeight(target.x, target.z) : _waterLevel - 30f;
                float minY = bed + _minAboveBed;
                float maxY = _waterLevel - _minBelowSurface;
                if (minY > maxY) minY = maxY;
                target.y = Mathf.Clamp(target.y, minY, maxY);

                var last = _lastPosition[i];
                var velocity = target - last;
                if (_upright)
                {
                    float roll = Mathf.Sin(t * 0.3f + _phase[i]) * 8f;
                    fish.rotation = Quaternion.Euler(roll, angle * Mathf.Rad2Deg * 0.15f, Mathf.Cos(t * 0.23f + _phase[i]) * 8f);
                }
                else if (_hasVelocity[i] && velocity.sqrMagnitude > 0.000001f)
                {
                    var forward = velocity.normalized;
                    var look = Quaternion.LookRotation(forward, Vector3.up);
                    float bank = Mathf.Clamp(-velocity.x * _bankAngle * 0.2f, -_bankAngle, _bankAngle);
                    fish.rotation = look * Quaternion.Euler(0f, _modelYaw, bank);
                }
                else
                {
                    fish.rotation = Quaternion.LookRotation(Vector3.forward, Vector3.up) * Quaternion.Euler(0f, _modelYaw, 0f);
                }

                fish.position = target;
                _lastPosition[i] = target;
                _hasVelocity[i] = true;
            }
        }

#if UNITY_EDITOR
        private void OnDrawGizmosSelected()
        {
            Gizmos.color = new Color(0.2f, 0.9f, 1f, 0.7f);
            Gizmos.DrawWireSphere(_center, Mathf.Max(_radius.x, _radius.z));
            Gizmos.color = new Color(1f, 0.8f, 0.2f, 0.7f);
            Gizmos.DrawWireCube(_center, new Vector3(_radius.x * 2f, _radius.y * 2f, _radius.z * 2f));
        }
#endif
    }
}
