using UnityEngine;

namespace FishDemo.Gameplay
{
    [DisallowMultipleComponent]
    public sealed class TorchRingController : MonoBehaviour
    {
        public const int HardLightLimit = 6;

        [SerializeField] private TorchLight[] _torches;
        [SerializeField] private int _maxActiveLights = 1;
        [SerializeField] private int[] _presets = { 0, 1, 2, 4, 6 };
        [SerializeField] private float _hysteresis = 0.12f;
        [SerializeField] private float _cameraRefreshSeconds = 0.5f;
        [SerializeField] private bool _enableHotkeys = true;
        [SerializeField] private KeyCode _cycleKey = KeyCode.L;

        private Vector3[] _positions;
        private bool[] _active;
        private bool[] _selected;
        private Camera _camera;
        private float _cameraRefreshTimer;
        private int _presetIndex;

        public int TorchCount => _torches != null ? _torches.Length : 0;
        public int MaxActiveLights => _maxActiveLights;
        public int ActiveTargetCount { get; private set; }
        public int LitLightCount { get; private set; }
        public TorchLight[] Torches => _torches;
        public string PresetLabel => _presets != null ? string.Join("/", _presets) : "";

        private void Awake()
        {
            if (_torches == null || _torches.Length == 0)
                _torches = GetComponentsInChildren<TorchLight>(true);

            _maxActiveLights = Mathf.Clamp(_maxActiveLights, 0, HardLightLimit);
            _presetIndex = FindPresetIndex(_maxActiveLights);

            int n = TorchCount;
            _positions = new Vector3[n];
            _active = new bool[n];
            _selected = new bool[n];
            for (int i = 0; i < n; i++)
            {
                _positions[i] = _torches[i] != null ? _torches[i].transform.position : Vector3.zero;
                _torches[i]?.SetTarget(false);
            }
        }

        private void Update()
        {
            if (_enableHotkeys && Input.GetKeyDown(_cycleKey))
                CyclePreset();

            Evaluate();
            UpdateLitCount();
        }

        public void ApplyImmediate()
        {
            Evaluate();
            int n = TorchCount;
            for (int i = 0; i < n; i++)
            {
                if (_torches[i] != null)
                    _torches[i].ApplyImmediate(_active[i]);
            }
            UpdateLitCount();
        }

        private void Evaluate()
        {
            int n = TorchCount;
            if (n == 0) return;

            _cameraRefreshTimer -= Time.unscaledDeltaTime;
            if (_camera == null || !_camera.isActiveAndEnabled || _cameraRefreshTimer <= 0f)
            {
                _camera = Camera.main;
                _cameraRefreshTimer = _cameraRefreshSeconds;
            }
            if (_camera == null) return;

            Vector3 origin = _camera.transform.position;
            for (int i = 0; i < n; i++)
                _selected[i] = false;

            int wanted = Mathf.Clamp(_maxActiveLights, 0, HardLightLimit);
            for (int pick = 0; pick < wanted; pick++)
            {
                int best = -1;
                float bestScore = float.MaxValue;
                for (int i = 0; i < n; i++)
                {
                    if (_selected[i] || _torches[i] == null) continue;
                    float distance = Vector3.Distance(origin, _positions[i]);
                    float score = _active[i] ? distance * (1f - _hysteresis) : distance;
                    if (score < bestScore)
                    {
                        bestScore = score;
                        best = i;
                    }
                }
                if (best < 0) break;
                _selected[best] = true;
            }

            ActiveTargetCount = 0;
            for (int i = 0; i < n; i++)
            {
                if (_torches[i] == null) continue;
                bool on = _selected[i];
                if (on != _active[i])
                {
                    _active[i] = on;
                    _torches[i].SetTarget(on);
                }
                if (on) ActiveTargetCount++;
            }
        }

        private void UpdateLitCount()
        {
            int lit = 0;
            int n = TorchCount;
            for (int i = 0; i < n; i++)
            {
                var light = _torches[i] != null ? _torches[i].PointLight : null;
                if (light != null && light.enabled) lit++;
            }
            LitLightCount = lit;
        }

        public void Bind(TorchLight[] torches, int maxActiveLights)
        {
            _torches = torches;
            _maxActiveLights = Mathf.Clamp(maxActiveLights, 0, HardLightLimit);
        }

        public void SetMaxActiveLights(int count)
        {
            _maxActiveLights = Mathf.Clamp(count, 0, HardLightLimit);
            _presetIndex = FindPresetIndex(_maxActiveLights);
        }

        public void CyclePreset()
        {
            if (_presets == null || _presets.Length == 0) return;
            _presetIndex = (_presetIndex + 1) % _presets.Length;
            SetMaxActiveLights(_presets[_presetIndex]);
        }

        private int FindPresetIndex(int value)
        {
            if (_presets == null) return 0;
            for (int i = 0; i < _presets.Length; i++)
                if (_presets[i] == value) return i;
            return 0;
        }

        private void OnValidate()
        {
            _maxActiveLights = Mathf.Clamp(_maxActiveLights, 0, HardLightLimit);
        }
    }
}
