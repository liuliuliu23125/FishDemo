using System;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using WaveHarmonic.Crest;
using FishDemo.Gameplay;

[InitializeOnLoad]
public static class LakeDuckweedPreview
{
    private const string Tag = "[DuckweedPreview]";
    private const string WorldScenePath = "Assets/Scenes/World.unity";
    private const string OutDir = "Logs/Water";
    private const string ResultPath = OutDir + "/duckweed-preview.txt";
    private const string PendingKey = "FishDemo.DuckweedPreview.Pending";

    static LakeDuckweedPreview()
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
        new GameObject("~DuckweedPreviewDriver").AddComponent<Driver>();
    }

    private sealed class Driver : MonoBehaviour
    {
        private Camera _camera;
        private WaterRenderer _water;
        private GameObject _root;
        private Vector3 _focus;
        private int _frame;
        private int _stage;
        private int _warmup;
        private int _readyFrames;
        private bool _ready;
        private bool _primed;
        private Texture2D _on;
        private Texture2D _off;
        private Texture2D _animA;
        private Texture2D _animB;

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

            _root = GameObject.Find("Lake Duckweed");
            if (_root == null) { Fail("没有 Lake Duckweed"); return; }

            var renderers = _root.GetComponentsInChildren<MeshRenderer>(true);
            Debug.Log($"{Tag} 根节点存在，渲染器={renderers.Length}");

            var center = new Vector3(192f, 264f, 96f);
            var filters = _root.GetComponentsInChildren<MeshFilter>(true);
            float best = -1f;
            foreach (var filter in filters)
            {
                var p = filter.transform.position;
                if (p.x >= center.x || p.z >= center.z) continue;
                float d = Vector2.Distance(new Vector2(p.x, p.z), new Vector2(center.x, center.z));
                if (d <= best) continue;
                best = d;
                _focus = p;
            }
            Debug.Log($"{Tag} 对焦={_focus}");

            _camera.transform.position = new Vector3(192f, 318f, -240f);
            _camera.transform.rotation = Quaternion.LookRotation(
                (new Vector3(192f, 264f, 96f) - _camera.transform.position).normalized, Vector3.up);
        }

        private void Update()
        {
            if (_camera == null || _root == null) return;
            _frame++;

            if (!_ready)
            {
                _readyFrames++;
                if (!_primed)
                {
                    _primed = true;
                    _camera.Render();
                    Debug.Log($"{Tag} primed, sea={(_water != null ? _water.SeaLevel : -1f):F2}");
                    return;
                }

                bool waterReady = _water != null && _water.SeaLevel > 1f;
                bool sunReady = RenderSettings.sun != null && RenderSettings.sun.intensity > 0.02f;
                if ((waterReady && sunReady) || _readyFrames > 900)
                {
                    _ready = true;
                    _warmup = 0;
                    Debug.Log($"{Tag} ready waterReady={waterReady} sunReady={sunReady}");
                }
                return;
            }

            if (_stage == 0)
            {
                _warmup++;
                if (_warmup < 60) return;
                Capture("duckweed-preview-wide.png");
                var inward = new Vector3(192f, 0f, 96f) - new Vector3(_focus.x, 0f, _focus.z);
                inward.Normalize();
                _camera.transform.position = _focus + inward * 10f + Vector3.up * 2.4f;
                _camera.transform.rotation = Quaternion.LookRotation(
                    (_focus + Vector3.up * 0.3f - _camera.transform.position).normalized, Vector3.up);
                _stage = 1;
                _warmup = 0;
                return;
            }

            if (_stage == 1)
            {
                _warmup++;
                if (_warmup < 15) return;
                Capture("duckweed-preview-on.png");
                var inward = new Vector3(192f, 0f, 96f) - new Vector3(_focus.x, 0f, _focus.z);
                inward.Normalize();
                _camera.transform.position = _focus + inward * 45f + Vector3.up * 12f;
                _camera.transform.rotation = Quaternion.LookRotation(
                    (_focus + Vector3.up * 0.3f - _camera.transform.position).normalized, Vector3.up);
                _stage = 2;
                _warmup = 0;
                return;
            }

            if (_stage == 2)
            {
                _warmup++;
                if (_warmup < 12) return;
                _on = Capture("duckweed-preview-shore.png");
                _animA = Capture("duckweed-preview-anim-a.png");
                _stage = 3;
                _warmup = 0;
                return;
            }

            if (_stage == 3)
            {
                _warmup++;
                if (_warmup < 30) return;
                _animB = Capture("duckweed-preview-anim-b.png");
                _root.SetActive(false);
                _stage = 4;
                _warmup = 0;
                return;
            }

            if (_stage == 4)
            {
                _warmup++;
                if (_warmup < 6) return;
                _off = Capture("duckweed-preview-off.png");
                _root.SetActive(true);
                Finish();
            }
        }

        private Texture2D Capture(string name)
        {
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
            Debug.Log($"{Tag} 截图 {path} sea={(_water != null ? _water.SeaLevel : -1f):F2}");
            return tex;
        }

        private void Finish()
        {
            var sb = new StringBuilder();
            var material = AssetDatabase.LoadAssetAtPath<Material>("Assets/WaterPlants/Materials/M_SwampLily.mat");
            var renderers = _root.GetComponentsInChildren<MeshRenderer>(true);
            var filters = _root.GetComponentsInChildren<MeshFilter>(true);
            long tris = filters.Where(f => f.sharedMesh != null).Sum(f => f.sharedMesh.triangles.Length / 3L);
            sb.AppendLine($"renderers={renderers.Length} tris={tris} " +
                          $"shader={(material != null && material.shader != null ? material.shader.name : "null")} " +
                          $"instancing={(material != null && material.enableInstancing)}");

            bool ok = _on != null && _off != null && renderers.Length > 0;
            if (_on != null && _off != null)
            {
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
                sb.AppendLine($"duckweed on/off: changed={changedPct:F1}% avgPerPixel={avg:F2}");
                if (changedPct < 0.2f) ok = false;

                if (_animA != null && _animB != null)
                {
                    var aa = _animA.GetPixels32();
                    var bb = _animB.GetPixels32();
                    long animDiff = 0;
                    int mask = 0;
                    for (int i = 0; i < n; i++)
                    {
                        int maskDiff = Mathf.Abs(a[i].r - b[i].r) + Mathf.Abs(a[i].g - b[i].g) + Mathf.Abs(a[i].b - b[i].b);
                        if (maskDiff <= 12) continue;
                        mask++;
                        animDiff += Mathf.Abs(aa[i].r - bb[i].r) + Mathf.Abs(aa[i].g - bb[i].g) + Mathf.Abs(aa[i].b - bb[i].b);
                    }
                    float animAvg = mask > 0 ? animDiff / (float)mask : 0f;
                    sb.AppendLine($"duckweed animation: maskPixels={mask} avgPerPixel={animAvg:F2}");
                    if (animAvg < 1f) ok = false;
                }
                else
                {
                    sb.AppendLine("duckweed animation: 缺少 A/B 帧");
                    ok = false;
                }
            }

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
