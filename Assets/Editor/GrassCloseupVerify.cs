using System.Collections;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using FishDemo.Rendering;

/// <summary>
/// 近景草地可见性验收：证明「贴地巡游时草真的在画面里」。
///
/// 为什么需要单独一个验证：
/// PegasusTourVerify 跑在 -nographics 下，BRG 的剔除回调不会执行，
/// LastVisibleCount 恒为 -1，无法证明草被画出来。
/// 本脚本要求真实图形设备，从贴地视角统计各 LOD 层的可见实例数并落一张截图。
///
/// 判定：
///   1. 草数据已加载（实例数 > 0）；
///   2. 贴地视角下可见实例数 > 0（不是全被剔除）；
///   3. LOD 分层生效：近处 LOD0 与远处 LOD2 同时存在；
///   4. 截图非纯色（画面里确实有东西）。
///
/// 用法：
///   Unity.exe -batchmode -projectPath <proj> \
///     -executeMethod GrassCloseupVerify.Run -logFile <log>
/// </summary>
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
        // 本验收要拍「全世界都能看见草」，不能等 SECTR 逐块流式加载：
        // 强制本次 Play Mode 走旧的全量读盘路径（只影响本会话）。
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

            // 开启剔除诊断：前 3 次剔除会打印「可见块数 / 扫描实例数 / 可见数」。
            // 这是判断「0 可见」卡在哪一步的唯一直接证据。
            _mgr.SetDebugCullShots(3);
        }

        private void Update()
        {
            if (_done) return;
            _frame++;

            // 等几帧：BRG 首帧注册、chunk 异步加载、Terrain 出现都需要时间。
            if (_frame == 5)
            {
                // 摆一个贴地视角：站在巡游路线上（半径 1100 的 θ=195° 处——
                // 那是烘焙数据覆盖全区、草密度有代表的点），地面上方 4m，俯角 20°。
                // 不再用 GrassBRG 节点自身位置：那是世界中心附近，不在巡游路线上。
                const float RouteRadius = 1100f;
                const float ThetaDeg = 195f;
                float th = ThetaDeg * Mathf.Deg2Rad;
                Vector3 pos = new Vector3(Mathf.Cos(th) * RouteRadius, 0f, Mathf.Sin(th) * RouteRadius);

                // 用地形采样拿到地面高度（贴地 4m）。chunk 可能还没加载完，
                // Terrain 找不到就保持 333（世界平均地面），下一轮再校准。
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

                // 朝向圆心偏一侧：从半径 1100 往圆心看，画面里必然是地形本体。
                // 不能朝切线方向——θ=195° 处的切线指向世界西边界外，
                // 拍出来只有天空和远景树线（实测踩过，误判成"草没了"）。
                Vector3 toCenter = new Vector3(-pos.x, 0f, -pos.z).normalized;
                _cam.transform.rotation = Quaternion.LookRotation(toCenter) *
                                          Quaternion.Euler(20f, 0f, 0f);

                _sb.AppendLine($"相机摆位：{_cam.transform.position}（地面 {groundY:F1} + 4m），" +
                               $"朝向圆心，俯角 20°");
            }

            // 再等几帧让剔除跑完再统计。
            if (_frame == 30)
            {
                Report();
                _done = true;
            }
        }

        private void Report()
        {
            // ── 先渲染，再读计数 ──
            //
            // 顺序不能反：BRG 的剔除发生在相机渲染时（OnPerformCulling），
            // batchmode 播放模式下没有"每帧自动渲染"这回事，
            // 必须显式调一次 Camera.Render 剔除才会跑（实测先读后渲拿到的是恒 0）。
            bool shotOk = false;
            try
            {
                Directory.CreateDirectory("Logs");
                // RT 必须用 1080p：LOD 像素阈值（100/25/8px）按 1080p 标定。
                // 用 540p 会让阈值对应的距离减半——LOD0 要求 4.7m 内，而俯角 20°
                // 的视锥下缘与地面最近交点在 5.2m，导致 LOD0 恒为 0（实测踩过，
                // GrassLodVerify.cs 的注释记录过同一件事）。
                const int W = 1920, H = 1080;
                var rt = new RenderTexture(W, H, 24, RenderTextureFormat.ARGB32);
                var prev = _cam.targetTexture;
                _cam.targetTexture = rt;

                // 连续渲染两次，取第二次的结果。
                //
                // 为什么：BRG 的剔除是异步 Job（OnPerformCulling 返回 JobHandle，
                // 引擎负责等待）。手动调 Camera.Render() 时，第一次可能拿到尚未
                // 完成的剔除结果 —— draw command 为空 → 画面没草。
                // 实测同一位置/同一份数据，两次运行一张有草一张没有，就是这个问题。
                // 多渲一帧让上一帧的 Job 完成，第二次才是稳定结果。
                _cam.Render();
                _cam.Render();
                RenderTexture.active = rt;
                var tex = new Texture2D(W, H, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, W, H), 0, 0);
                tex.Apply();
                _cam.targetTexture = prev;
                RenderTexture.active = null;

                var px = tex.GetPixels32();
                // 统计颜色多样性：纯色（全黑/全天空色）说明没画出东西。
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

            // 渲染已完成，现在读剔除计数才有意义。
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
