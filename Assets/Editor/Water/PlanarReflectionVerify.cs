using System;
using System.IO;
using System.Linq;
using System.Text;
using FishDemo.Gameplay;
using FishDemo.Rendering;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering.Universal;
using WaveHarmonic.Crest;

[InitializeOnLoad]
public static class PlanarReflectionVerify
{
    private const string Tag = "[PlanarReflectionVerify]";
    private const string WorldScenePath = "Assets/Scenes/World.unity";
    private const string OutDir = "Logs/Lake";
    private const string ResultPath = OutDir + "/planar-reflection-runtime.txt";
    private const string PendingKey = "FishDemo.PlanarReflectionVerify.Pending";

    private static Camera s_ReflectionCamera;

    static PlanarReflectionVerify()
    {
        WaterReflections.OnCameraAdded += camera => s_ReflectionCamera = camera;
        EditorApplication.update += Poll;
    }

    public static void Run()
    {
        var sb = new StringBuilder();
        void W(string s) { Debug.Log($"{Tag} {s}"); sb.AppendLine(s); }

        Directory.CreateDirectory(OutDir);
        EditorSceneManager.OpenScene(WorldScenePath, OpenSceneMode.Single);
        ChunkLoadScope.LoadRelevant(ChunkLoadScope.Area.FromLake(), W);
        EditorSceneManager.SetActiveScene(EditorSceneManager.GetSceneByPath(WorldScenePath));

        var water = UnityEngine.Object.FindObjectOfType<WaterRenderer>();
        if (water == null)
        {
            W("FAIL：场景里没有 WaterRenderer");
            Flush(sb, 2);
            return;
        }

        SessionState.SetString(PendingKey + ".Pre", sb.ToString());
        SessionState.SetBool(PendingKey, true);
        EditorApplication.isPlaying = true;
    }

    private static void Poll()
    {
        if (!SessionState.GetBool(PendingKey, false) || !EditorApplication.isPlaying)
            return;

        SessionState.SetBool(PendingKey, false);
        EditorApplication.update -= Poll;

        var sb = new StringBuilder(SessionState.GetString(PendingKey + ".Pre", string.Empty));
        var water = UnityEngine.Object.FindObjectOfType<WaterRenderer>();
        if (water == null)
        {
            sb.AppendLine("FAIL：Play Mode 下找不到 WaterRenderer");
            Flush(sb, 1);
            EditorApplication.Exit(1);
            return;
        }

        new GameObject("~PlanarReflectionVerifyDriver").AddComponent<Driver>().Begin(sb, water);
    }

    private sealed class Driver : MonoBehaviour
    {
        private StringBuilder _sb;
        private WaterRenderer _water;
        private PlanarReflectionController _controller;
        private Camera _camera;
        private int _frame;
        private float _hiddenTime;
        private bool _compareDone;
        private bool _offPhase;
        private bool _done;
        private RenderTexture _targetTexture;
        private RenderTexture _previousTarget;

        public void Begin(StringBuilder sb, WaterRenderer water)
        {
            _sb = sb;
            _water = water;
            _controller = UnityEngine.Object.FindObjectOfType<PlanarReflectionController>();
            _camera = water.Viewer != null ? water.Viewer : Camera.main;
            gameObject.hideFlags = HideFlags.DontSave;

            foreach (var tour in UnityEngine.Object.FindObjectsOfType<PegasusTourController>())
                tour.enabled = false;
            foreach (var grass in UnityEngine.Object.FindObjectsOfType<GrassBRGManager>())
                grass.gameObject.SetActive(false);

            _sb.AppendLine($"WaterRenderer: {water.name} pos={water.transform.position}, viewer={_camera.name}");
            foreach (var c in Camera.allCameras)
                _sb.AppendLine($"Camera: {c.name} type={c.cameraType} enabled={c.enabled} tag={c.tag}");
        }

        private void Update()
        {
            if (_done) return;
            _frame++;

            if (_frame < 30) return;

            if (_controller == null || _camera == null)
            {
                Fail($"缺少组件：controller={_controller != null}, camera={_camera != null}");
                return;
            }

            if (_targetTexture == null && !_offPhase)
            {
                _previousTarget = _camera.targetTexture;
                _targetTexture = new RenderTexture(640, 360, 24, RenderTextureFormat.ARGB32);
                _camera.targetTexture = _targetTexture;
            }

            if (!_offPhase)
                _camera.Render();

            var reflectionCamera = s_ReflectionCamera != null
                ? s_ReflectionCamera
                : Resources.FindObjectsOfTypeAll<Camera>().FirstOrDefault(c => c.cameraType == CameraType.Reflection);

            if (reflectionCamera == null || reflectionCamera.targetTexture == null)
            {
                if (_frame % 60 == 0)
                {
                    _sb.AppendLine($"[frame {_frame}] reflections={_water.Reflections.Enabled}, " +
                                   $"visible={_controller.WaterVisible}, seaLevel={_water.SeaLevel:F1}, " +
                                   $"viewerHeight={_water.ViewerHeightAboveWater:F1}, reflCam={reflectionCamera != null}");
                }

                if (_frame > 300)
                    Fail($"反射相机或 targetTexture 未创建（reflCam={reflectionCamera != null}）");
                return;
            }

            if (!_compareDone)
            {
                if (reflectionCamera.targetTexture.width != 512 || reflectionCamera.targetTexture.height != 256)
                {
                    Fail($"反射贴图 = {reflectionCamera.targetTexture.width}x{reflectionCamera.targetTexture.height}，期望 512x256");
                    return;
                }

                if (!_controller.WaterVisible)
                {
                    if (_frame > 300)
                        Fail("水体在视锥内但 WaterVisible=false");
                    return;
                }

                if (!_water.Reflections.Enabled)
                {
                    if (_frame > 300)
                        Fail("朝向水面后反射未启用");
                    return;
                }

                var additional = reflectionCamera.GetUniversalAdditionalCameraData();
                var activeAsset = UniversalRenderPipeline.asset;
                var reflectionRenderer = activeAsset != null ? activeAsset.GetRenderer(1) : null;
                _sb.AppendLine($"反射相机 OK：target={reflectionCamera.targetTexture.width}x{reflectionCamera.targetTexture.height}, " +
                               $"cullingMask={reflectionCamera.cullingMask}, enabled={reflectionCamera.enabled}, " +
                               $"requiresDepth={additional.requiresDepthTexture}, renderShadows={additional.renderShadows}, " +
                               $"requiresColor={additional.requiresColorTexture}, post={additional.renderPostProcessing}, " +
                               $"rendererIndex1={(reflectionRenderer != null && additional.scriptableRenderer == reflectionRenderer)}");
                if (additional.requiresDepthTexture)
                {
                    Fail("反射相机仍在请求 CopyDepth（CaptureDepth 应为 false）");
                    return;
                }
                if (additional.renderShadows)
                {
                    Fail("反射相机仍开启阴影（renderShadows 应为 false）");
                    return;
                }
                if (additional.requiresColorTexture || additional.renderPostProcessing)
                {
                    Fail("反射相机仍请求颜色拷贝或后处理");
                    return;
                }
                if (reflectionRenderer == null || additional.scriptableRenderer != reflectionRenderer)
                {
                    Fail("反射相机没有使用 ReflectionRenderer（renderer index 1）");
                    return;
                }
                _sb.AppendLine($"水体可见：WaterVisible=true, Reflections.Enabled=true, EnableCount={_controller.EnableCount}");

                var on = ReadFrame();
                _controller.enabled = false;
                _water.Reflections.Enabled = false;
                _camera.Render();
                var off = ReadFrame();
                _controller.enabled = true;
                _water.Reflections.Enabled = true;
                _camera.Render();

                int changed = 0;
                long sum = 0;
                for (int i = 0; i < on.Length; i++)
                {
                    int dr = on[i].r - off[i].r;
                    int dg = on[i].g - off[i].g;
                    int db = on[i].b - off[i].b;
                    int d = dr * dr + dg * dg + db * db;
                    if (d > 36) changed++;
                    sum += d;
                }

                float ratio = (float)changed / on.Length;
                double mean = (double)sum / on.Length;
                _sb.AppendLine($"反射开/关像素差：{changed}/{on.Length} = {ratio:P2}，均方差={mean:F2}（阈值 0.1%）");
                if (ratio < 0.001f)
                {
                    Fail("反射开/关画面无差异，水面没有吃到反射贴图");
                    return;
                }

                _compareDone = true;
                _camera.enabled = false;
                _offPhase = true;
                _hiddenTime = 0f;
                return;
            }

            _hiddenTime += Time.unscaledDeltaTime;
            if (_hiddenTime < 1.0f)
                return;

            bool ok = true;
            if (_controller.WaterVisible) { _sb.AppendLine("FAIL：相机停用后 WaterVisible 仍为 true"); ok = false; }
            if (_water.Reflections.Enabled) { _sb.AppendLine("FAIL：相机停用 1s 后反射仍启用"); ok = false; }
            if (_controller.DisableCount == 0) { _sb.AppendLine("FAIL：控制器没有记录关闭"); ok = false; }

            _camera.enabled = true;
            if (ok)
            {
                _sb.AppendLine($"相机停用：WaterVisible=false, Reflections.Enabled=false, DisableCount={_controller.DisableCount}");
                _sb.AppendLine("==== 结论：PASS ====");
            }
            else
            {
                _sb.AppendLine("==== 结论：FAIL ====");
            }

            Done(ok ? 0 : 2);
        }

        private Color32[] ReadFrame()
        {
            RenderTexture.active = _targetTexture;
            var tex = new Texture2D(_targetTexture.width, _targetTexture.height, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, _targetTexture.width, _targetTexture.height), 0, 0);
            tex.Apply();
            var pixels = tex.GetPixels32();
            RenderTexture.active = null;
            UnityEngine.Object.Destroy(tex);
            return pixels;
        }

        private void Fail(string reason)
        {
            _sb.AppendLine($"FAIL：{reason}");
            _sb.AppendLine("==== 结论：FAIL ====");
            Done(2);
        }

        private void Done(int exitCode)
        {
            if (_done) return;
            _done = true;

            if (_camera != null)
            {
                _camera.enabled = true;
                _camera.targetTexture = _previousTarget;
            }

            if (_targetTexture != null)
            {
                _targetTexture.Release();
                UnityEngine.Object.DestroyImmediate(_targetTexture);
                _targetTexture = null;
            }

            Flush(_sb, exitCode);
            EditorApplication.Exit(exitCode);
        }
    }

    private static void Flush(StringBuilder sb, int exitCode)
    {
        File.WriteAllText(ResultPath, sb.ToString());
        if (Application.isBatchMode)
            EditorApplication.Exit(exitCode);
    }
}
