using UnityEngine;

namespace FishDemo.Gameplay
{
    [DisallowMultipleComponent]
    public sealed class UnderwaterEcologyController : MonoBehaviour
    {
        [SerializeField] private float _waterLevel = 264f;
        [SerializeField] private Vector3 _lakeCenter = new Vector3(192f, 264f, 96f);
        [SerializeField] private float _lakeRadius = 190f;
        [SerializeField] private float _lakeDepth = 30f;
        [SerializeField] private Vector3 _reefCenter = new Vector3(192f, 255f, 246f);
        [SerializeField] private Transform _benthicRoot;
        [SerializeField] private Transform _fishRoot;
        [SerializeField] private GameObject _godRayRoot;
        [SerializeField] private GameObject _bubbleRoot;
        [SerializeField] private AudioSource _ambience;
        [SerializeField] private float _ambienceVolume = 0.3f;
        [SerializeField] private float _benthicActivateDistance = 120f;
        [SerializeField] private KeyCode _toggleKey = KeyCode.J;
        [SerializeField] private KeyCode _godRayToggleKey = KeyCode.G;
        [SerializeField] private bool _godRaysEnabled;

        private bool _isUnderwater;
        private bool _ecologyEnabled = true;
        private bool _benthicActive = true;
        private bool _godRaysActive;
        private bool _bubblesActive;
        private Transform[] _benthicChildren = new Transform[0];

        public float WaterLevel => _waterLevel;
        public Vector3 LakeCenter => _lakeCenter;
        public float LakeRadius => _lakeRadius;
        public float LakeDepth => _lakeDepth;
        public bool IsUnderwater => _isUnderwater;
        public bool EcologyEnabled => _ecologyEnabled;

        public float LakeBedHeight(float x, float z)
        {
            float dx = x - _lakeCenter.x;
            float dz = z - _lakeCenter.z;
            float r = Mathf.Sqrt(dx * dx + dz * dz);
            float k = 1f - (r / _lakeRadius) * (r / _lakeRadius);
            if (k < 0f) k = 0f;
            return _waterLevel - _lakeDepth * k;
        }

        private void Awake()
        {
            CacheBenthicChildren();
        }

        private void OnEnable()
        {
            _ecologyEnabled = true;
            _benthicActive = true;
            ApplyBenthicState();
            ApplyFogState();
        }

        private void OnDisable()
        {
            if (_ambience != null) _ambience.volume = 0f;
        }

        private void Update()
        {
            if (Application.isFocused && Input.GetKeyDown(_toggleKey))
            {
                _ecologyEnabled = !_ecologyEnabled;
                ApplyBenthicState();
                ApplyFogState();
            }

            if (Application.isFocused && Input.GetKeyDown(_godRayToggleKey))
            {
                _godRaysEnabled = !_godRaysEnabled;
                ApplyFogState();
            }

            var cam = Camera.main;
            if (cam == null) return;

            var p = cam.transform.position;
            _isUnderwater = p.y < _waterLevel - 0.2f && InsideLake(p.x, p.z);

            float dx = p.x - _reefCenter.x;
            float dz = p.z - _reefCenter.z;
            bool nearReef = dx * dx + dz * dz < _benthicActivateDistance * _benthicActivateDistance;

            bool benthicTarget = _ecologyEnabled && (_isUnderwater || nearReef);
            if (benthicTarget != _benthicActive)
            {
                _benthicActive = benthicTarget;
                ApplyBenthicState();
            }

            ApplyFogState();
            ApplyAmbience();
        }

        private void CacheBenthicChildren()
        {
            if (_benthicRoot == null)
            {
                _benthicChildren = new Transform[0];
                return;
            }

            _benthicChildren = new Transform[_benthicRoot.childCount];
            for (int i = 0; i < _benthicChildren.Length; i++)
            {
                _benthicChildren[i] = _benthicRoot.GetChild(i);
            }
        }

        private void ApplyBenthicState()
        {
            if (_benthicChildren.Length == 0) CacheBenthicChildren();

            bool active = _ecologyEnabled && _benthicActive;
            for (int i = 0; i < _benthicChildren.Length; i++)
            {
                var child = _benthicChildren[i];
                if (child != null && child.gameObject.activeSelf != active)
                {
                    child.gameObject.SetActive(active);
                }
            }

            if (_fishRoot != null && _fishRoot.gameObject.activeSelf != _ecologyEnabled)
            {
                _fishRoot.gameObject.SetActive(_ecologyEnabled);
            }
        }

        private void ApplyFogState()
        {
            var sun = RenderSettings.sun;
            bool sunUp = sun == null || sun.transform.forward.y < -0.02f;

            bool godRayTarget = _ecologyEnabled && _isUnderwater && sunUp && _godRaysEnabled;
            if (_godRayRoot != null && _godRayRoot.activeSelf != godRayTarget)
            {
                _godRayRoot.SetActive(godRayTarget);
            }
            _godRaysActive = godRayTarget;

            bool bubbleTarget = _ecologyEnabled && _isUnderwater;
            if (_bubbleRoot != null && _bubbleRoot.activeSelf != bubbleTarget)
            {
                _bubbleRoot.SetActive(bubbleTarget);
            }
            _bubblesActive = bubbleTarget;
        }

        private void ApplyAmbience()
        {
            if (_ambience == null) return;

            float target = _ecologyEnabled && _isUnderwater ? _ambienceVolume : 0f;
            _ambience.volume = Mathf.MoveTowards(_ambience.volume, target, Time.deltaTime * 0.4f);
            if (target > 0f && !_ambience.isPlaying) _ambience.Play();
            if (target <= 0f && _ambience.isPlaying && _ambience.volume <= 0.001f) _ambience.Stop();
        }

        private bool InsideLake(float x, float z)
        {
            float dx = x - _lakeCenter.x;
            float dz = z - _lakeCenter.z;
            float r = _lakeRadius + 90f;
            return dx * dx + dz * dz <= r * r;
        }

        public bool GodRaysActive => _godRaysActive;
        public bool BubblesActive => _bubblesActive;

        public void SetEcologyEnabled(bool enabled)
        {
            _ecologyEnabled = enabled;
            ApplyBenthicState();
            ApplyFogState();
        }

        public void SetGodRaysEnabled(bool enabled)
        {
            _godRaysEnabled = enabled;
            ApplyFogState();
        }
    }
}
