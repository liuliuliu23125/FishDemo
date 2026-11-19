using UnityEngine;

namespace FishDemo.Gameplay
{
    [DisallowMultipleComponent]
    public sealed class TorchLight : MonoBehaviour
    {
        [SerializeField] private Light _light;
        [SerializeField] private ParticleSystem _flame;
        [SerializeField] private ParticleSystem _embers;
        [SerializeField] private Renderer _core;
        [SerializeField] private Color _coreEmission = new Color(9f, 3.4f, 0.8f, 1f);
        [SerializeField] private float _baseIntensity = 120f;
        [SerializeField] private float _flicker = 0.16f;
        [SerializeField] private float _fadeSeconds = 0.7f;
        [SerializeField] private float _flameRate = 26f;
        [SerializeField] private float _emberRate = 5f;

        private static readonly int EmissionColorId = Shader.PropertyToID("_EmissionColor");

        private MaterialPropertyBlock _mpb;
        private float _on;
        private float _target;
        private float _seed;
        private bool _ratesReady;
        private bool _particlesOn;
        private float _lastAppliedOn = -1f;

        public bool IsOn => _target > 0.5f;
        public float Fade => _on;
        public Light PointLight => _light;

        public void Bind(Light pointLight, ParticleSystem flame, ParticleSystem embers, Renderer core,
                         float baseIntensity, Color coreEmission, float flameRate, float emberRate)
        {
            _light = pointLight;
            _flame = flame;
            _embers = embers;
            _core = core;
            _baseIntensity = baseIntensity;
            _coreEmission = coreEmission;
            _flameRate = flameRate;
            _emberRate = emberRate;
            _ratesReady = true;
            ApplyImmediate(false);
        }

        public void SetTarget(bool on)
        {
            _target = on ? 1f : 0f;
        }

        public void ApplyImmediate(bool on)
        {
            _target = on ? 1f : 0f;
            _on = _target;
            ApplyState();
        }

        private void Awake()
        {
            if (_light == null)
                _light = GetComponentInChildren<Light>(true);
            if (_flame == null)
            {
                var t = transform.Find("Flame FX");
                if (t != null) _flame = t.GetComponent<ParticleSystem>();
            }
            if (_embers == null)
            {
                var t = transform.Find("Embers");
                if (t != null) _embers = t.GetComponent<ParticleSystem>();
            }
            if (_core == null)
            {
                var t = transform.Find("Flame Core");
                if (t != null) _core = t.GetComponent<Renderer>();
            }

            var p = transform.position;
            _seed = Mathf.Abs(p.x * 0.37f + p.z * 0.61f) % 97f;
            CacheRates();
            ApplyState();
        }

        private void Update()
        {
            if (_light == null) return;

            float step = _fadeSeconds > 0.01f ? Time.deltaTime / _fadeSeconds : 1f;
            if (_on != _target)
                _on = Mathf.MoveTowards(_on, _target, step);

            float t = Time.time;
            float n1 = Mathf.PerlinNoise(t * 6.1f + _seed, _seed * 0.73f);
            float n2 = Mathf.PerlinNoise(t * 13.7f + _seed * 1.91f, _seed * 0.37f);
            float flicker = 1f + _flicker * ((n1 - 0.5f) * 1.4f + (n2 - 0.5f) * 0.6f);

            float intensity = _baseIntensity * _on * flicker;
            bool active = _on > 0.001f;
            if (_light.enabled != active) _light.enabled = active;
            if (active) _light.intensity = intensity;

            if (_core != null)
            {
                if (_core.enabled != active) _core.enabled = active;
                if (active)
                {
                    if (_mpb == null) _mpb = new MaterialPropertyBlock();
                    _core.GetPropertyBlock(_mpb);
                    _mpb.SetColor(EmissionColorId, _coreEmission * (_on * flicker));
                    _core.SetPropertyBlock(_mpb);
                }
            }

            if (active != _particlesOn)
            {
                _particlesOn = active;
                ToggleParticles(active);
            }

            if (Mathf.Abs(_on - _lastAppliedOn) > 0.004f)
                ApplyParticleRates();
        }

        private void CacheRates()
        {
            if (_ratesReady) return;
            if (_flame != null && _flameRate <= 0f)
                _flameRate = _flame.emission.rateOverTimeMultiplier;
            if (_embers != null && _emberRate <= 0f)
                _emberRate = _embers.emission.rateOverTimeMultiplier;
            _ratesReady = true;
        }

        private void ApplyState()
        {
            if (_light == null) return;

            bool active = _on > 0.001f;
            _light.enabled = active;
            _light.intensity = active ? _baseIntensity * _on : 0f;

            if (_core != null)
            {
                _core.enabled = active;
                if (active)
                {
                    if (_mpb == null) _mpb = new MaterialPropertyBlock();
                    _core.GetPropertyBlock(_mpb);
                    _mpb.SetColor(EmissionColorId, _coreEmission * _on);
                    _core.SetPropertyBlock(_mpb);
                }
            }

            _particlesOn = active;
            ToggleParticles(active);
            ApplyParticleRates();
            _lastAppliedOn = _on;
        }

        private void ToggleParticles(bool on)
        {
            if (on)
            {
                if (_flame != null && !_flame.isPlaying) _flame.Play();
                if (_embers != null && !_embers.isPlaying) _embers.Play();
            }
            else
            {
                if (_flame != null && _flame.isPlaying)
                    _flame.Stop(true, ParticleSystemStopBehavior.StopEmitting);
                if (_embers != null && _embers.isPlaying)
                    _embers.Stop(true, ParticleSystemStopBehavior.StopEmitting);
            }
        }

        private void ApplyParticleRates()
        {
            _lastAppliedOn = _on;
            if (_flame != null)
            {
                var emission = _flame.emission;
                emission.rateOverTimeMultiplier = _flameRate * _on;
            }
            if (_embers != null)
            {
                var emission = _embers.emission;
                emission.rateOverTimeMultiplier = _emberRate * _on;
            }
        }
    }
}
