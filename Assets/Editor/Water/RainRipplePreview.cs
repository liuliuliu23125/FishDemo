using System;
using System.IO;
using System.Linq;
using System.Text;
using FishDemo.Gameplay;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using WaveHarmonic.Crest;

[InitializeOnLoad]
public static class RainRipplePreview
{
    private const string Tag = "[RainRipplePreview]";
    private const string WorldScenePath = "Assets/Scenes/World.unity";
    private const string OutDir = "Logs/RainRipple";
    private const string ResultPath = OutDir + "/preview.txt";
    private const string PendingKey = "FishDemo.RainRipplePreview.Pending";

    private static readonly int s_Normal = Shader.PropertyToID("_Crest_RainRippleNormal");
    private static readonly int s_Intensity = Shader.PropertyToID("_Crest_RainRippleIntensity");
    private static readonly int s_Scale = Shader.PropertyToID("_Crest_RainRippleScale");
    private static readonly int s_Speed = Shader.PropertyToID("_Crest_RainRippleSpeed");

    static RainRipplePreview()
    {
        EditorApplication.update += Poll;
    }

    public static void Run()
    {
        var sb = new StringBuilder();
        void W(string s)
        {
            Debug.Log($"{Tag} {s}");
            sb.AppendLine(s);
        }

        Directory.CreateDirectory(OutDir);
        EditorSceneManager.OpenScene(WorldScenePath, OpenSceneMode.Single);
        ChunkLoadScope.LoadRelevant(ChunkLoadScope.Area.FromLake(), W);
        EditorSceneManager.SetActiveScene(EditorSceneManager.GetSceneByPath(WorldScenePath));

        var water = UnityEngine.Object.FindObjectOfType<WaterRenderer>();
        var controller = UnityEngine.Object.FindObjectOfType<RainRippleController>();
        if (water == null || controller == null || controller.NormalTexture == null)
        {
            W($"FAIL：water={water != null}, controller={controller != null}, texture={(controller != null && controller.NormalTexture != null)}");
            Flush(sb);
            return;
        }

        SessionState.SetString(PendingKey + ".Pre", sb.ToString());
        SessionState.SetBool(PendingKey, true);
        EditorApplication.isPlaying = true;
    }

    private static void Poll()
    {
        if (!SessionState.GetBool(PendingKey, false) || !EditorApplication.isPlaying)
        {
            return;
        }

        SessionState.SetBool(PendingKey, false);
        var sb = new StringBuilder(SessionState.GetString(PendingKey + ".Pre", string.Empty));
        var water = UnityEngine.Object.FindObjectOfType<WaterRenderer>();
        var controller = UnityEngine.Object.FindObjectOfType<RainRippleController>();
        var cam = UnityEngine.Object.FindObjectsOfType<Camera>()
            .FirstOrDefault(c => c.CompareTag("MainCamera")) ?? UnityEngine.Object.FindObjectOfType<Camera>();

        if (water == null || controller == null || controller.NormalTexture == null || cam == null)
        {
            sb.AppendLine("FAIL：Play Mode 下缺少 WaterRenderer / RainRippleController / texture / camera");
            Flush(sb);
            return;
        }

        new GameObject("~RainRipplePreviewDriver").AddComponent<Driver>().Begin(sb, controller, cam);
    }

    private sealed class Driver : MonoBehaviour
    {
        private StringBuilder _sb;
        private RainRippleController _controller;
        private Camera _cam;
        private int _frame;
        private int _readyFrame;
        private int _stage;
        private bool _ready;

        public void Begin(StringBuilder sb, RainRippleController controller, Camera cam)
        {
            _sb = sb;
            _controller = controller;
            _cam = cam;
            gameObject.hideFlags = HideFlags.DontSave;

            foreach (var tour in UnityEngine.Object.FindObjectsOfType<PegasusTourController>())
            {
                tour.enabled = false;
            }
            foreach (var rig in UnityEngine.Object.FindObjectsOfType<LakeCameraRig>())
            {
                rig.enabled = false;
            }
            foreach (var orbit in UnityEngine.Object.FindObjectsOfType<LakeOrbitCamera>())
            {
                orbit.enabled = false;
            }
            foreach (var grass in UnityEngine.Object.FindObjectsOfType<FishDemo.Rendering.GrassBRGManager>())
            {
                grass.gameObject.SetActive(false);
            }
            foreach (var enviro in UnityEngine.Object.FindObjectsOfType<Enviro.EnviroManager>())
            {
                enviro.gameObject.SetActive(false);
            }
            foreach (var atmo in UnityEngine.Object.FindObjectsOfType<AtmosphereController>())
            {
                atmo.enabled = false;
            }

            var legacy = GameObject.Find("Directional Light");
            if (legacy != null && legacy.TryGetComponent<Light>(out var light))
            {
                light.enabled = true;
                light.color = Color.white;
                light.intensity = 1.2f;
            }

            var sky = AssetDatabase.GetBuiltinExtraResource<Material>("Default-Skybox.mat");
            if (sky != null) RenderSettings.skybox = sky;
            RenderSettings.ambientMode = AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.42f, 0.47f, 0.55f);
            RenderSettings.ambientIntensity = 1f;

            _controller.enabled = false;
            Shader.SetGlobalTexture(s_Normal, _controller.NormalTexture);
            Shader.SetGlobalFloat(s_Scale, 2f);
            Shader.SetGlobalFloat(s_Speed, 0.05f);
            Shader.SetGlobalFloat(s_Intensity, 0f);
        }

        private void Update()
        {
            _frame++;

            if (!_ready)
            {
                if (CountTerrainsCoveringLake() > 0 || _frame > 3000)
                {
                    _ready = true;
                    _readyFrame = _frame;
                    var c = new Vector3(LakeCarve.Center.x, 0f, LakeCarve.Center.y);
                    var stand = new Vector3(c.x, LakeCarve.WaterLevel + 6f, c.z + (LakeCarve.Radius - 55f));
                    var look = new Vector3(c.x, LakeCarve.WaterLevel - 10f, c.z + (LakeCarve.Radius - 105f));
                    _cam.transform.position = stand;
                    _cam.transform.rotation = Quaternion.LookRotation((look - stand).normalized, Vector3.up);
                    _cam.fieldOfView = 60f;
                    _cam.nearClipPlane = 0.1f;
                    _cam.farClipPlane = 3000f;
                    _sb.AppendLine($"[frame {_frame}] 相机就位 pos={stand} look={look}");
                }
                return;
            }

            if (_frame < _readyFrame + 20)
            {
                return;
            }

            if (_stage == 0)
            {
                Shader.SetGlobalFloat(s_Intensity, 0.7f);
                var on = Capture("preview-rain-on");
                Shader.SetGlobalFloat(s_Intensity, 0f);
                var off = Capture("preview-rain-off");
                _stage = 1;
                _sb.AppendLine($"rain-on/off 截图完成，像素 {on.Length}/{off.Length}（同一帧，冻结波浪对比）");
                Finish(on, off);
            }
        }

        private Color32[] Capture(string name)
        {
            const int W = 1280, H = 720;
            var rt = new RenderTexture(W, H, 24, RenderTextureFormat.ARGB32);
            var prev = _cam.targetTexture;
            _cam.targetTexture = rt;
            for (var i = 0; i < 4; i++) _cam.Render();
            RenderTexture.active = rt;
            var tex = new Texture2D(W, H, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, W, H), 0, 0);
            tex.Apply();
            _cam.targetTexture = prev;
            RenderTexture.active = null;
            File.WriteAllBytes(Path.Combine(OutDir, $"rain-ripple-{name}.png"), tex.EncodeToPNG());
            var px = tex.GetPixels32();
            DestroyImmediate(tex);
            rt.Release();
            DestroyImmediate(rt);
            return px;
        }

        private void Finish(Color32[] on, Color32[] off)
        {
            if (on == null || off == null || on.Length != off.Length)
            {
                _sb.AppendLine("FAIL：缺少对照截图");
                Flush(_sb, 2);
                return;
            }

            double sum = 0;
            var changed = 0;
            for (var i = 0; i < on.Length; i++)
            {
                var d = Mathf.Abs(on[i].r - off[i].r) + Mathf.Abs(on[i].g - off[i].g) + Mathf.Abs(on[i].b - off[i].b);
                sum += d;
                if (d > 2) changed++;
            }

            var mean = sum / (on.Length * 3.0);
            var ratio = (double)changed / on.Length;
            _sb.AppendLine($"雨纹开/关平均通道差 = {mean:F3}/255，变化像素 = {ratio:P2}");
            var ok = mean > 0.3 && ratio > 0.001;
            _sb.AppendLine($"==== 结论：{(ok ? "PASS" : "FAIL")} ====");
            Flush(_sb, ok ? 0 : 2);
        }

        private static int CountTerrainsCoveringLake()
        {
            var n = 0;
            var cx = LakeCarve.Center.x;
            var cz = LakeCarve.Center.y;
            foreach (var t in UnityEngine.Object.FindObjectsOfType<Terrain>())
            {
                if (t.terrainData == null) continue;
                var p = t.transform.position;
                var s = t.terrainData.size;
                if (cx >= p.x && cx <= p.x + s.x && cz >= p.z && cz <= p.z + s.z) n++;
            }
            return n;
        }
    }

    private static void Flush(StringBuilder sb)
    {
        Flush(sb, 1);
    }

    private static void Flush(StringBuilder sb, int code)
    {
        Directory.CreateDirectory(OutDir);
        File.WriteAllText(ResultPath, sb.ToString());
        EditorApplication.Exit(code);
    }
}
