using System.Collections;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using FishDemo.Rendering;

[InitializeOnLoad]
public static class GrassCloseupVerify
{
    private const string Tag = "[GrassCloseup]";
    private const string WorldScenePath = "Assets/Scenes/World.unity";
    private const string ResultPath = "Logs/grass-closeup.txt";
    private const string PngPath = "Logs/grass-closeup.png";
    private const string PendingKey = "FishDemo.GrassCloseup.Pending";

    static GrassCloseupVerify()
    {
        EditorApplication.update += Poll;
    }

    public static void Run()
    {
        var sb = new StringBuilder();
        void W(string s) { Debug.Log($"{Tag} {s}"); sb.AppendLine(s); }

        EditorSceneManager.OpenScene(WorldScenePath, OpenSceneMode.Single);

        var mgr = Object.FindObjectOfType<GrassBRGManager>();
        var cam = Object.FindObjectsOfType<Camera>()
            .FirstOrDefault(c => c.CompareTag("MainCamera")) ?? Object.FindObjectOfType<Camera>();

        if (mgr == null) { W("FAIL：场景里没有 GrassBRGManager"); Flush(sb); return; }
        if (cam == null) { W("FAIL：场景里没有相机"); Flush(sb); return; }

        SessionState.SetString(PendingKey + ".Pre", sb.ToString());
        SessionState.SetBool(PendingKey, true);
        GrassBRGManager.RequestFullLoad();
        EditorApplication.isPlaying = true;
    }

    private static void Poll()
    {
        if (!SessionState.GetBool(PendingKey, false) || !EditorApplication.isPlaying)
            return;

        SessionState.SetBool(PendingKey, false);
        EditorApplication.update -= Poll;

        var sb = new StringBuilder(SessionState.GetString(PendingKey + ".Pre", string.Empty));
        var mgr = Object.FindObjectOfType<GrassBRGManager>();
        var cam = Object.FindObjectsOfType<Camera>()
            .FirstOrDefault(c => c.CompareTag("MainCamera")) ?? Object.FindObjectOfType<Camera>();

        if (mgr == null || cam == null)
        {
            sb.AppendLine("FAIL：Play Mode 下找不到管理器或相机");
            Flush(sb);
            EditorApplication.Exit(1);
            return;
        }

        new GameObject("~GrassCloseupDriver").AddComponent<Driver>().Begin(sb, mgr, cam);
    }

    private sealed class Driver : MonoBehaviour
    {
        private StringBuilder _sb;
        private GrassBRGManager _mgr;
        private Camera _cam;
        private int _frame;
        private bool _done;

        public void Begin(StringBuilder sb, GrassBRGManager mgr, Camera cam)
        {
            _sb = sb; _mgr = mgr; _cam = cam;
            gameObject.hideFlags = HideFlags.DontSave;

            _mgr.SetDebugCullShots(3);
        }

        private void Update()
        {
            if (_done) return;
            _frame++;

            if (_frame == 5)
            {
                const float RouteRadius = 1100f;
                const float ThetaDeg = 195f;
                float th = ThetaDeg * Mathf.Deg2Rad;
                Vector3 pos = new Vector3(Mathf.Cos(th) * RouteRadius, 0f, Mathf.Sin(th) * RouteRadius);

                float groundY = 333f;
                var terrains = Object.FindObjectsOfType<Terrain>();
                foreach (var tr in terrains)
                {
                    if (tr.terrainData == null) continue;
                    Vector3 o = tr.transform.position;
                    Vector3 s = tr.terrainData.size;
                    if (pos.x >= o.x && pos.x <= o.x + s.x && pos.z >= o.z && pos.z <= o.z + s.z)
                    {
                        groundY = tr.SampleHeight(new Vector3(pos.x, 0f, pos.z)) + o.y;
                        break;
                    }
                }

                _cam.transform.position = new Vector3(pos.x, groundY + 4f, pos.z);

                Vector3 toCenter = new Vector3(-pos.x, 0f, -pos.z).normalized;
                _cam.transform.rotation = Quaternion.LookRotation(toCenter) *
                                          Quaternion.Euler(20f, 0f, 0f);

                _sb.AppendLine($"相机摆位：{_cam.transform.position}（地面 {groundY:F1} + 4m），" +
                               $"朝向圆心，俯角 20°");
            }

            if (_frame == 30)
            {
                Report();
                _done = true;
            }
        }

        private void Report()
        {
            bool shotOk = false;
            try
            {
                Directory.CreateDirectory("Logs");
                const int W = 1920, H = 1080;
                var rt = new RenderTexture(W, H, 24, RenderTextureFormat.ARGB32);
                var prev = _cam.targetTexture;
                _cam.targetTexture = rt;

                _cam.Render();
                _cam.Render();
                RenderTexture.active = rt;
                var tex = new Texture2D(W, H, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, W, H), 0, 0);
                tex.Apply();
                _cam.targetTexture = prev;
                RenderTexture.active = null;

                var px = tex.GetPixels32();
                var distinct = new System.Collections.Generic.HashSet<int>();
                long sum = 0;
                for (int i = 0; i < px.Length; i += 7)
                {
                    distinct.Add((px[i].r << 16) | (px[i].g << 8) | px[i].b);
                    sum += px[i].g;
                }
                File.WriteAllBytes(PngPath, tex.EncodeToPNG());
                shotOk = distinct.Count > 200;
                _sb.AppendLine($"截图：{PngPath}，颜色种类 {distinct.Count}（>200 视为画面有内容）");
                Object.DestroyImmediate(tex);
                rt.Release();
                Object.DestroyImmediate(rt);
            }
            catch (System.Exception e)
            {
                _sb.AppendLine($"截图失败：{e.Message}");
            }

            int total = _mgr.InstanceCount;
            int visible = _mgr.LastVisibleCount;
            int lod0 = _mgr.GetLastLodCount(0);
            int lod1 = _mgr.GetLastLodCount(1);
            int lod2 = _mgr.GetLastLodCount(2);

            _sb.AppendLine("");
            _sb.AppendLine("===== 近景草地可见性 =====");
            _sb.AppendLine($"草总实例        = {total:N0}");
            _sb.AppendLine($"当前可见实例    = {visible:N0}");
            _sb.AppendLine($"LOD0 / LOD1 / LOD2 = {lod0:N0} / {lod1:N0} / {lod2:N0}");

            bool loaded = total > 0;
            bool visibleOk = visible > 0;
            bool lodWorks = lod0 > 0 && (lod1 > 0 || lod2 > 0);

            _sb.AppendLine("");
            _sb.AppendLine($"[1] 草数据已加载        : {(loaded ? "PASS" : "FAIL")} ({total:N0})");
            _sb.AppendLine($"[2] 贴地视角可见        : {(visibleOk ? "PASS" : "FAIL")} ({visible:N0})");
            _sb.AppendLine($"[3] LOD 分层生效        : {(lodWorks ? "PASS" : "FAIL")} " +
                           $"(LOD0={lod0:N0}, LOD1={lod1:N0}, LOD2={lod2:N0})");
            _sb.AppendLine($"[4] 截图有内容          : {(shotOk ? "PASS" : "FAIL")}");

            bool all = loaded && visibleOk && lodWorks;
            _sb.AppendLine("");
            _sb.AppendLine(all ? "==== 结论：PASS ====" : "==== 结论：FAIL ====");

            Flush(_sb);
            EditorApplication.isPlaying = false;
            gameObject.AddComponent<QuitAfter>().Init();
        }
    }

    private sealed class QuitAfter : MonoBehaviour
    {
        private int _f;
        public void Init() { hideFlags = HideFlags.DontSave; }
        private void Update()
        {
            if (++_f < 30) return;
            EditorApplication.Exit(0);
        }
    }

    private static void Flush(StringBuilder sb)
    {
        Directory.CreateDirectory("Logs");
        File.WriteAllText(ResultPath, sb.ToString());
    }
}
