using FishDemo.Rendering;
using UnityEngine;
using WaveHarmonic.Crest;

namespace FishDemo.Gameplay
{
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(-50)]
    public sealed class PlanarReflectionController : MonoBehaviour
    {
        [SerializeField] private WaterRenderer _water;
        [SerializeField] private float _visibilityPadding = 10f;
        [SerializeField] private float _disableDelay = 0.5f;
        [SerializeField] private bool _disableUnderwater = true;

        private readonly Plane[] _planes = new Plane[6];
        private Bounds _waterBounds;
        private float _hiddenTime;

        public bool WaterVisible { get; private set; }
        public int EnableCount { get; private set; }
        public int DisableCount { get; private set; }
        public Bounds WaterBounds => _waterBounds;

        private void OnEnable()
        {
            WaterReflections.OnCameraAdded -= OnReflectionCameraAdded;
            WaterReflections.OnCameraAdded += OnReflectionCameraAdded;

            if (_water == null)
                _water = FindObjectOfType<WaterRenderer>();

            RebuildBounds();
            _hiddenTime = 0f;
        }

        private void OnDisable()
        {
            WaterReflections.OnCameraAdded -= OnReflectionCameraAdded;
        }

        private static void OnReflectionCameraAdded(Camera camera)
        {
            GrassBRGManager.ReflectionCamera = camera;
        }

        private void Update()
        {
            if (_water == null)
            {
                _water = FindObjectOfType<WaterRenderer>();
                if (_water == null)
                    return;
            }

            if (_waterBounds.size == Vector3.zero)
                RebuildBounds();

            Camera camera = _water.Viewer != null ? _water.Viewer : Camera.main;
            if (camera == null)
                return;

            WaterVisible = IsVisible(camera);
            if (WaterVisible)
            {
                _hiddenTime = 0f;
                SetReflections(true);
                return;
            }

            _hiddenTime += Time.unscaledDeltaTime;
            if (_hiddenTime >= _disableDelay)
                SetReflections(false);
        }

        public void Bind(WaterRenderer water)
        {
            _water = water;
            RebuildBounds();
        }

        public void RebuildBounds()
        {
            var bodies = FindObjectsOfType<WaterBody>();
            bool hasBounds = false;
            Bounds bounds = default;

            foreach (var body in bodies)
            {
                if (body == null || !body.isActiveAndEnabled)
                    continue;

                Vector3 scale = body.transform.lossyScale;
                var bodyBounds = new Bounds(
                    body.transform.position,
                    new Vector3(Mathf.Abs(scale.x), Mathf.Max(Mathf.Abs(scale.y), 2f), Mathf.Abs(scale.z)));

                if (!hasBounds)
                {
                    bounds = bodyBounds;
                    hasBounds = true;
                }
                else
                {
                    bounds.Encapsulate(bodyBounds);
                }
            }

            if (hasBounds)
                _waterBounds = bounds;
            else if (_water != null)
                _waterBounds = new Bounds(_water.transform.position, new Vector3(2f, 2f, 2f));
            else
                _waterBounds = default;
        }

        public bool IsVisible(Camera camera)
        {
            if (camera == null || !camera.isActiveAndEnabled)
                return false;

            if (camera.cameraType == CameraType.Reflection)
                return false;

            if (_waterBounds.size == Vector3.zero)
                RebuildBounds();

            if (_waterBounds.size == Vector3.zero)
                return false;

            if (_disableUnderwater && camera.transform.position.y < _waterBounds.center.y - 0.5f)
                return false;

            var bounds = _waterBounds;
            bounds.Expand(new Vector3(_visibilityPadding * 2f, 0f, _visibilityPadding * 2f));

            GeometryUtility.CalculateFrustumPlanes(camera, _planes);
            return GeometryUtility.TestPlanesAABB(_planes, bounds);
        }

        private void SetReflections(bool enabled)
        {
            if (_water == null)
                return;

            var reflections = _water.Reflections;
            if (reflections.Enabled == enabled)
                return;

            reflections.Enabled = enabled;
            if (enabled)
                EnableCount++;
            else
                DisableCount++;
        }
    }
}
