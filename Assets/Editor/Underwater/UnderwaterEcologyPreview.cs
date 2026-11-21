using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using WaveHarmonic.Crest;
using FishDemo.Gameplay;

[InitializeOnLoad]
public static class UnderwaterEcologyPreview
{
    private const string Tag = "[UnderwaterPreview]";
    private const string WorldScenePath = "Assets/Scenes/World.unity";
    private const string OutDir = "Logs/Underwater";
    private const string ResultPath = OutDir + "/underwater-preview.txt";
    private const string PendingKey = "FishDemo.UnderwaterPreview.Pending";

    static UnderwaterEcologyPreview()
    {
        EditorApplication.update += Poll;
    }

    public static void Run()
    {
        Directory.CreateDirectory(OutDir);
        EditorSceneManager.OpenScene(WorldScenePath, OpenSceneMode.Single);
        ChunkLoadScope.LoadRelevant(ChunkLoadScope.Area.FromLake(20f), s => Debug.Log($"{Tag} {s}"));
        EditorSceneManager.SetActiveScene(EditorSceneManager.GetSceneByPath(WorldScenePath));
        SessionState.SetBool(PendingKey, true);
        EditorApplication.isPlaying = true;
    }

    private static void Poll()
    {
        if (!SessionState.GetBool(PendingKey, false) || !EditorApplication.isPlaying) return;
        SessionState.SetBool(PendingKey, false);
        EditorApplication.update -= Poll;
        new GameObject("~UnderwaterPreviewDriver").AddComponent<Driver>();
    }

    private sealed class Driver : MonoBehaviour
    {
        private Camera _camera;
        private WaterRenderer _water;
        private UnderwaterEcologyController _ecology;
        private int _frame;
        private int _stage;
        private int _warmup;
        private int _readyFrames;
        private bool _ready;
        private bool _primed;
        private Texture2D _on;
        private Texture2D _off;
        private bool _onUnderwater;

        private void Start()
        {
            gameObject.hideFlags = HideFlags.DontSave;

            foreach (var rig in FindObjectsOfType<LakeCameraRig>()) rig.enabled = false;
            foreach (var orbit in FindObjectsOfType<LakeOrbitCamera>()) orbit.enabled = false;
            foreach (var tour in FindObjectsOfType<PegasusTourController>()) tour.enabled = false;

            _camera = FindObjectsOfType<Camera>()
                          .FirstOrDefault(c => c.GetComponent<LakeOrbitCamera>() != null)
                      ?? Camera.main
                      ?? FindObjectOfType<Camera>();
            if (_camera == null) { Fail("没有相机"); return; }
            _camera.enabled = true;
            _camera.tag = "MainCamera";
            var listener = _camera.GetComponent<AudioListener>();
            if (listener != null) listener.enabled = true;

            _water = FindObjectOfType<WaterRenderer>();
            if (_water != null) _water.Viewer = _camera;

            var enviro = Enviro.EnviroManager.instance;
            if (enviro != null)
            {
                enviro.ChangeCamera(_camera);
                enviro.Time.SetTimeOfDay(12f);
            }

            foreach (var grass in FindObjectsOfType<FishDemo.Rendering.GrassBRGManager>())
                grass.gameObject.SetActive(false);

            RenderSettings.ambientMode = AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.5f, 0.68f, 0.82f);
            RenderSettings.ambientIntensity = 1.2f;

            var sun = RenderSettings.sun;
            Debug.Log($"{Tag} sun={(sun != null ? sun.name : "null")} color={(sun != null ? sun.color.ToString() : "none")} " +
                      $"intensity={(sun != null ? sun.intensity : -1f):F2} dir={(sun != null ? sun.transform.forward.ToString() : "none")} " +
                      $"ambientMode={RenderSettings.ambientMode} ambient={RenderSettings.ambientLight.ToString()}");

            var root = GameObject.Find("Underwater Ecology");
            _ecology = root != null ? root.GetComponent<UnderwaterEcologyController>() : null;
            if (_ecology == null) { Fail("没有 UnderwaterEcologyController"); return; }

            var seabed = root.transform.Find("Seabed Sand");
            if (seabed != null)
            {
                var mr = seabed.GetComponent<MeshRenderer>();
                var mat = mr != null ? mr.sharedMaterial : null;
                Debug.Log($"{Tag} seabed bounds={(mr != null ? mr.bounds.ToString() : "none")} " +
                          $"mat={(mat != null ? mat.name : "null")} shader={(mat != null && mat.shader != null ? mat.shader.name : "null")} " +
                          $"base={(mat != null && mat.GetTexture("_BaseMap") != null ? mat.GetTexture("_BaseMap").name : "null")} " +
                          $"color={(mat != null ? mat.GetColor("_BaseColor").ToString() : "none")}");
            }

            _camera.transform.position = new Vector3(192f, 258f, 236f);
            _camera.transform.rotation = Quaternion.LookRotation(
                (new Vector3(192f, 254f, 246f) - _camera.transform.position).normalized, Vector3.up);
        }

        private void Update()
        {
            if (_camera == null) return;
            _frame++;

            if (!_ready)
            {
                _readyFrames++;
                if (!_primed)
                {
                    _primed = true;
                    _camera.Render();
                    Debug.Log($"{Tag} primed render, sea={(_water != null ? _water.SeaLevel : -1f):F2}");
                    return;
                }

                bool waterReady = _water != null && _water.SeaLevel > 1f;
                bool sunReady = RenderSettings.sun != null && RenderSettings.sun.intensity > 0.02f;
                if ((waterReady && sunReady) || _readyFrames > 900)
                {
                    _ready = true;
                    _warmup = 0;
                    Debug.Log($"{Tag} ready frame={_frame} waterReady={waterReady} sunReady={sunReady} " +
                              $"sea={(_water != null ? _water.SeaLevel : -1f):F2} " +
                              $"sunIntensity={(RenderSettings.sun != null ? RenderSettings.sun.intensity : -1f):F2}");
                }
                return;
            }

            if (_stage == 0)
            {
                _warmup++;
                if (_warmup < 90) return;
                _onUnderwater = _ecology.IsUnderwater;
                _on = Capture("underwater-preview-on.png");
                _ecology.SetEcologyEnabled(false);
                _stage = 1;
                _warmup = 0;
                return;
            }

            if (_stage == 1)
            {
                _warmup++;
                if (_warmup < 8) return;
                _off = Capture("underwater-preview-off.png");
                _ecology.SetEcologyEnabled(true);
                _camera.transform.position = new Vector3(192f, 258f, 246f);
                _camera.transform.rotation = Quaternion.LookRotation(Vector3.down, Vector3.forward);
                _stage = 2;
                _warmup = 0;
                return;
            }

            if (_stage == 2)
            {
                _warmup++;
                if (_warmup < 8) return;
                Capture("underwater-preview-bed.png");
                _camera.transform.position = new Vector3(192f, 300f, 320f);
                _camera.transform.rotation = Quaternion.LookRotation(
                    (new Vector3(192f, 260f, 96f) - _camera.transform.position).normalized, Vector3.up);
                _stage = 3;
                _warmup = 0;
                return;
            }

            if (_stage == 3)
            {
                _warmup++;
                if (_warmup < 30) return;
                Capture("underwater-preview-above.png");
                Finish();
            }
        }

        private Texture2D Capture(string name)
        {
            var main = Camera.main;
            Debug.Log($"{Tag} cam={_camera.name} enabled={_camera.enabled} y={_camera.transform.position.y:F2} " +
                      $"tag={_camera.tag} main={(main != null ? main.name : "null")} sameMain={(main == _camera)} " +
                      $"viewer={(_water != null && _water.Viewer != null ? _water.Viewer.name : "null")} " +
                      $"sea={(_water != null ? _water.SeaLevel : -1f):F2} under={_ecology.IsUnderwater} " +
                      $"mask={_camera.cullingMask} godActive={_ecology.GodRaysActive} bubblesActive={_ecology.BubblesActive} " +
                      $"sunIntensity={(RenderSettings.sun != null ? RenderSettings.sun.intensity : -1f):F2} " +
                      $"sunColor={(RenderSettings.sun != null ? RenderSettings.sun.color.ToString() : "none")}");

            var godRoot = GameObject.Find("Underwater Ecology/God Rays");
            if (godRoot != null)
            {
                var r = godRoot.GetComponentInChildren<Renderer>(true);
                Debug.Log($"{Tag} godRoot active={godRoot.activeSelf} child={godRoot.transform.childCount} " +
                          $"renderer={(r != null ? r.enabled.ToString() : "none")} " +
                          $"shader={(r != null && r.sharedMaterial != null && r.sharedMaterial.shader != null ? r.sharedMaterial.shader.name : "none")} " +
                          $"bounds={(r != null ? r.bounds.ToString() : "none")}");
            }

            if (_water != null)
            {
                var f = typeof(WaterRenderer).GetField("_Underwater", BindingFlags.Instance | BindingFlags.NonPublic);
                var uw = f != null ? f.GetValue(_water) : null;
                if (uw != null)
                {
                    var m = uw.GetType().GetMethod("ShouldRender", BindingFlags.Instance | BindingFlags.NonPublic);
                    if (m != null)
                    {
                        object result = null;
                        try { result = m.Invoke(uw, new object[] { _camera }); } catch (Exception e) { result = e.InnerException?.Message ?? e.Message; }
                        Debug.Log($"{Tag} underwaterShouldRender={result}");
                    }
                }
            }

            const int w = 1280, h = 720;
            var rt = RenderTexture.GetTemporary(w, h, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            var prev = _camera.targetTexture;
            _camera.targetTexture = rt;
            _camera.Render();
            RenderTexture.active = rt;
            var tex = new Texture2D(w, h, TextureFormat.RGB24, false, false);
            tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
            tex.Apply();
            RenderTexture.active = null;
            _camera.targetTexture = prev;
            RenderTexture.ReleaseTemporary(rt);

            string path = Path.Combine(OutDir, name);
            File.WriteAllBytes(path, tex.EncodeToPNG());
            Debug.Log($"{Tag} 截图 {path}");
            return tex;
        }

        private void Finish()
        {
            var sb = new StringBuilder();
            if (_on == null || _off == null)
            {
                sb.AppendLine("FAIL：缺少对比截图");
                Flush(sb, 2);
                return;
            }

            var a = _on.GetPixels32();
            var b = _off.GetPixels32();
            int n = Mathf.Min(a.Length, b.Length);
            long diff = 0;
            int changed = 0;
            for (int i = 0; i < n; i++)
            {
                int d = Mathf.Abs(a[i].r - b[i].r) + Mathf.Abs(a[i].g - b[i].g) + Mathf.Abs(a[i].b - b[i].b);
                diff += d;
                if (d > 12) changed++;
            }
            float changedPct = n > 0 ? changed * 100f / n : 0f;
            float avg = n > 0 ? diff / (float)n : 0f;
            sb.AppendLine($"ecology on/off: changed={changedPct:F1}% avgPerPixel={avg:F2}");
            sb.AppendLine($"onCaptureUnderwater={_onUnderwater}");
            bool ok = changedPct > 1f;
            sb.AppendLine($"==== 结论：{(ok ? "PASS" : "FAIL")} ====");
            Flush(sb, ok ? 0 : 2);
        }

        private void Fail(string message)
        {
            var sb = new StringBuilder();
            sb.AppendLine("FAIL：" + message);
            Flush(sb, 1);
        }

        private static void Flush(StringBuilder sb, int code)
        {
            Directory.CreateDirectory(OutDir);
            File.WriteAllText(ResultPath, sb.ToString());
            EditorApplication.Exit(code);
        }
    }
}
