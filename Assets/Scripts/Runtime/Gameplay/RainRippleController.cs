using Enviro;
using UnityEngine;

namespace FishDemo.Gameplay
{
    [DisallowMultipleComponent]
    public sealed class RainRippleController : MonoBehaviour
    {
        [Header("雨纹")]
        [Tooltip("可平铺的雨滴法线贴图。为空时 intensity 恒为 0。")]
        [SerializeField] private Texture2D _normalTexture;
        [Tooltip("从 Enviro wetness 自动驱动；关闭后用下面的手动强度。")]
        [SerializeField] private bool _autoFromEnviro = true;
        [Tooltip("wetness(0-1) 到雨纹强度的映射。")]
        [SerializeField] private AnimationCurve _intensityFromWetness = AnimationCurve.EaseInOut(0f, 0f, 1f, 1f);
        [Tooltip("关闭自动驱动时使用的强度。")]
        [Range(0f, 1f)]
        [SerializeField] private float _manualIntensity;
        [Tooltip("雨纹最大强度。")]
        [Range(0f, 4f)]
        [SerializeField] private float _maxIntensity = 0.7f;
        [Tooltip("雨纹世界平铺尺寸（米）。")]
        [SerializeField] private float _scale = 4f;
        [Tooltip("雨纹滚动速度（UV/秒）。")]
        [SerializeField] private float _speed = 0.15f;
        [Tooltip("强度过渡时间（秒）。")]
        [SerializeField] private float _blendSeconds = 1.5f;

        private static readonly int s_NormalTexture = Shader.PropertyToID("_Crest_RainRippleNormal");
        private static readonly int s_Intensity = Shader.PropertyToID("_Crest_RainRippleIntensity");
        private static readonly int s_Scale = Shader.PropertyToID("_Crest_RainRippleScale");
        private static readonly int s_Speed = Shader.PropertyToID("_Crest_RainRippleSpeed");

        private float _intensity;
        private bool _loggedMissingEnviro;

        public Texture2D NormalTexture
        {
            get => _normalTexture;
            set
            {
                _normalTexture = value;
                ApplyStatics();
            }
        }

        public bool AutoFromEnviro
        {
            get => _autoFromEnviro;
            set => _autoFromEnviro = value;
        }

        public float ManualIntensity
        {
            get => _manualIntensity;
            set => _manualIntensity = Mathf.Clamp01(value);
        }

        public float BlendSeconds
        {
            get => _blendSeconds;
            set => _blendSeconds = Mathf.Max(value, 0.01f);
        }

        public float Scale
        {
            get => _scale;
            set => _scale = Mathf.Max(0.001f, value);
        }

        public float Speed
        {
            get => _speed;
            set => _speed = value;
        }

        public float MaxIntensity
        {
            get => _maxIntensity;
            set => _maxIntensity = Mathf.Max(0f, value);
        }

        private void OnEnable()
        {
            ApplyStatics();
            Shader.SetGlobalFloat(s_Intensity, 0f);
        }

        private void OnDisable()
        {
            Shader.SetGlobalFloat(s_Intensity, 0f);
        }

        private void Update()
        {
            ApplyStatics();

            var target = 0f;
            if (_normalTexture != null)
            {
                var source = _autoFromEnviro ? ReadEnviroWetness() : _manualIntensity;
                target = _intensityFromWetness.Evaluate(Mathf.Clamp01(source)) * _maxIntensity;
            }

            _intensity = Mathf.MoveTowards(_intensity, target, Time.deltaTime / Mathf.Max(_blendSeconds, 0.01f));
            Shader.SetGlobalFloat(s_Intensity, _intensity);
        }

        private void ApplyStatics()
        {
            Shader.SetGlobalTexture(s_NormalTexture, _normalTexture);
            Shader.SetGlobalFloat(s_Scale, Mathf.Max(_scale, 0.001f));
            Shader.SetGlobalFloat(s_Speed, _speed);
        }

        private float ReadEnviroWetness()
        {
            var manager = EnviroManager.instance;
            var settings = manager != null && manager.Environment != null ? manager.Environment.Settings : null;
            if (settings != null)
            {
                return settings.wetness;
            }

            if (!_loggedMissingEnviro)
            {
                Debug.LogWarning("[RainRipple] 找不到 EnviroManager/Environment，雨纹强度保持 0；可关闭自动驱动改用手动强度。");
                _loggedMissingEnviro = true;
            }
            return 0f;
        }
    }
}
