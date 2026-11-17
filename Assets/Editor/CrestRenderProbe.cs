using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using WaveHarmonic.Crest;

/// <summary>
/// Crest 渲染开销诊断：**只读**，不修改任何资产。
///
/// ── 这个工具能看到什么 ──
/// 1. **Crest / 自定义 RendererFeature 自己 enqueue 的 pass**：Crest 在 URP 14 下会
///    用反射复用 URP 私有的 `m_OpaqueColor` / `m_DepthTexture`，再 enqueue 自己 new 的
///    CopyColorPass/CopyDepthPass。这里统计的是这部分。
/// 2. 水面 chunk 数量、可见数、每帧提交次数（Chunks 模式是 `CommandBuffer.DrawRenderer`
///    逐个提交，见 `SurfaceRenderer.cs:921-967`，这是它不能进 SRP Batcher 的直接原因）。
/// 3. 当前路径（Chunks / Quad / Custom）与 SimpleTransparency 是否开启。
///
/// ── 重要限制：看不到 URP 内部的 copy ──
/// `beginCameraRendering` 触发在 URP `renderer.Setup()` **之前**；URP 自己的
/// `CopyDepthPass` 是在 `Setup()` 里入队的（`UniversalRenderer.cs:1151-1154`）。
/// 所以本工具输出 `CopyDepthPass = 0` **不代表管线没有 depth copy**。
/// 例如当前 Enviro 需要 `_CameraDepthTexture`，管线实际有 1 次 CopyDepth，
/// 但本工具仍然只能看到 Crest 入队的那 1 个 pass。要确认 URP 内部 copy，
/// 用 Frame Debugger（或按 URP 源码 + `m_RequireDepthTexture` 判断）。
///
/// ── 为什么用反射读 pass 队列 ──
/// `ScriptableRenderer.m_ActiveRenderPassQueue` 是 internal，URP 没有公开 API。
/// 在 `beginCameraRendering` 读它，能拿到 **Crest/自定义 feature 本帧已入队**的 pass；
/// 再晚就读不到了（URP 在 `Render()` 结束会清空）。
///
/// 用法：-executeMethod CrestRenderProbe.Run
/// 输出 Logs/Crest/crest-render-probe.txt
/// </summary>
[InitializeOnLoad]
public static class CrestRenderProbe
{
    private const string Tag = "[CrestProbe]";
    private const string WorldScenePath = "Assets/Scenes/World.unity";
    private const string OutDir = "Logs/Crest";
    private const string ResultPath = OutDir + "/crest-render-probe.txt";
    private const string PendingKey = "FishDemo.CrestProbe.Pending";

    static CrestRenderProbe()
    {
        EditorApplication.update += Poll;
    }

    public static void Run()
    {
        var sb = new StringBuilder();
        void W(string s) { Debug.Log($"{Tag} {s}"); sb.AppendLine(s); }

        Directory.CreateDirectory(OutDir);
        EditorSceneManager.OpenScene(WorldScenePath, OpenSceneMode.Single);

        // 预加载 chunk：否则相机附近没有地形，水面 chunk 的可见性统计没有意义。
        // 只加载与湖相关的 chunk（36 → 8 左右），验证机位固定看湖，
        // 不需要全世界。详见 ChunkLoadScope 的类注释。
        ChunkLoadScope.LoadRelevant(ChunkLoadScope.Area.FromLake(), W);
        EditorSceneManager.SetActiveScene(EditorSceneManager.GetSceneByPath(WorldScenePath));

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
        new GameObject("~CrestProbeDriver").AddComponent<Driver>().Begin(sb);
    }

    private sealed class Driver : MonoBehaviour
    {
        private StringBuilder _sb;
        private int _frame;
        private bool _dumped;

        /// <summary>beginCameraRendering 里抓的 pass 快照（见 Begin 中的说明）。</summary>
        private List<string> _passSnapshot;
        private readonly List<string> _frameLog = new List<string>();
        private int _copyColorCount, _copyDepthCount;
        private string _probeCamName;

        public void Begin(StringBuilder sb)
        {
            _sb = sb;
            gameObject.hideFlags = HideFlags.DontSave;

            // 关掉会抢相机/拖慢帧的东西。
            foreach (var t in UnityEngine.Object.FindObjectsOfType<FishDemo.Gameplay.PegasusTourController>())
                t.enabled = false;
            foreach (var m in UnityEngine.Object.FindObjectsOfType<Pegasus.PegasusManager>())
            {
                m.PauseFlythrough();
                m.enabled = false;
            }
            foreach (var g in UnityEngine.Object.FindObjectsOfType<FishDemo.Rendering.GrassBRGManager>())
                g.gameObject.SetActive(false);

            RenderSettings.ambientMode = AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.42f, 0.47f, 0.55f);

            // 必须在这里挂钩：URP 每次 Render() 结束会清空 m_ActiveRenderPassQueue，
            // Update 里读永远是空表（实测 pass 总数 = 0）。
            // beginCameraRendering 在 URP renderer.Setup()（它才入队内部 copy pass）之前触发，
            // 所以这里只能看到 Crest / 自定义 feature 已入队的 pass；URP 内部的 CopyDepthPass
            // 不在此列表里。需要确认 URP 拷贝请用 Frame Debugger。
            RenderPipelineManager.beginCameraRendering += OnBeginCamera;

            // 把相机摆到湖边（触发 SECTR 加载），并确保轨道相机在渲染。
            var rig = UnityEngine.Object.FindObjectOfType<FishDemo.Gameplay.LakeCameraRig>();
            if (rig != null) rig.enabled = false;
            var orbit = UnityEngine.Object.FindObjectOfType<FishDemo.Gameplay.LakeOrbitCamera>();
            var cam = orbit != null ? orbit.GetComponent<Camera>() : Camera.main;
            if (cam != null)
            {
                cam.enabled = true;
                cam.transform.position = new Vector3(LakeCarve.Center.x, LakeCarve.WaterLevel + 26f,
                                                     LakeCarve.Center.y + 300f);
                cam.transform.rotation = Quaternion.LookRotation(
                    new Vector3(LakeCarve.Center.x, LakeCarve.WaterLevel, LakeCarve.Center.y) - cam.transform.position);
            }
        }

        private void Update()
        {
            _frame++;
            if (_frame < 120 || _dumped) return;   // 等 SECTR 与水面就绪
            _dumped = true;
            Dump();
        }

        /// <summary>
        /// 在 URP 完成本帧入队后抓取 pass 队列。只抓游戏相机，跳过反射/阴影相机。
        /// </summary>
        private void OnBeginCamera(ScriptableRenderContext ctx, Camera camera)
        {
            if (camera == null || camera.cameraType != CameraType.Game) return;
            if (!camera.isActiveAndEnabled) return;
            // 等场景稳定后再抓，避免抓到 SECTR 加载途中的半成品帧。
            if (_frame < 120) return;

            var urpData = camera.GetComponent<UniversalAdditionalCameraData>();
            var renderer = urpData?.scriptableRenderer;
            if (renderer == null) return;

            var queueField = typeof(ScriptableRenderer).GetField("m_ActiveRenderPassQueue",
                BindingFlags.NonPublic | BindingFlags.Instance);
            var queue = queueField?.GetValue(renderer) as List<ScriptableRenderPass>;
            if (queue == null) return;

            _probeCamName = camera.name;
            _frameLog.Add($"frame {Time.frameCount}: pass={queue.Count}");
            _passSnapshot = new List<string>(queue.Count);
            _copyColorCount = 0;
            _copyDepthCount = 0;
            for (int i = 0; i < queue.Count; i++)
            {
                var p = queue[i];
                var name = p.GetType().Name;
                if (name == "CopyColorPass") _copyColorCount++;
                if (name == "CopyDepthPass") _copyDepthCount++;
                _passSnapshot.Add($"  [{i}] {name,-34} event={p.renderPassEvent}");
            }
        }

        private void OnDestroy()
        {
            RenderPipelineManager.beginCameraRendering -= OnBeginCamera;
        }

        private void Dump()
        {
            _sb.AppendLine("");
            _sb.AppendLine($"=== 采样帧 {Time.frameCount} ===");

            var water = UnityEngine.Object.FindObjectOfType<WaterRenderer>();
            if (water == null) { _sb.AppendLine("FAIL：找不到 WaterRenderer"); Finish(); return; }

            // ── 1. 静态配置 ──
            var surface = water.Surface;
            _sb.AppendLine("");
            _sb.AppendLine("=== 水面配置 ===");
            _sb.AppendLine($"MeshType = {GetPrivate(surface, "_MeshType")}  " +
                           $"（0=Chunks 1=Quad 2=Custom）");
            _sb.AppendLine($"InjectionPoint = {water.InjectionPoint}  " +
                           $"（0=Default 1=BeforeTransparent）");
            _sb.AppendLine($"WriteToColorTexture = {water.WriteToColorTexture}, " +
                           $"WriteToDepthTexture = {water.WriteToDepthTexture}");
            _sb.AppendLine($"LodLevels = {water.LodLevels}, LodResolution = {water.LodResolution}");
            _sb.AppendLine($"Viewpoint = {(water.Viewpoint != null ? water.Viewpoint.name : "相机（默认）")}" +
                           "（固定时水面数据不跟相机；Viewer 仍跟相机）");

            // DepthLod 现在只对与湖 AABB 相交的 Terrain dispatch（见 DepthLod.TerrainDepthInput）。
            // 这里用 WaterBody 的 transform 尺度复算一次，给 Frame Debugger 的 Depth dispatch 数做对照。
            var waterBody = UnityEngine.Object.FindObjectOfType<WaterBody>();
            if (waterBody != null)
            {
                var scale = waterBody.transform.lossyScale;
                var p = waterBody.transform.position;
                var lakeRect = new Rect(p.x - scale.x * 0.5f, p.z - scale.z * 0.5f, scale.x, scale.z);
                var terrains = Terrain.activeTerrains;
                var intersect = 0;
                foreach (var terrain in terrains)
                {
                    if (terrain.terrainData == null) continue;
                    var pos = terrain.GetPosition();
                    var size = terrain.terrainData.size;
                    if (new Rect(pos.x, pos.z, size.x, size.z).Overlaps(lakeRect)) intersect++;
                }
                _sb.AppendLine($"Terrain: active={terrains.Length}, 与湖 AABB 相交={intersect}" +
                               "（DepthLod 过滤后应只 dispatch 这些）");
            }

            // SimpleTransparency 是编译期宏（CREST_SIMPLE_TRANSPARENCY），运行时从材质 keyword 读不到，
            // 但可以从 shader 的编译结果间接判断：这里直接打印当前工程宏值供对照。
            var settingsPath = "Packages/com.waveharmonic.crest/Runtime/Shaders/Library/Settings/Settings.Crest.Default.hlsl";
            string simple = "未知";
            if (File.Exists(settingsPath))
            {
                var txt = File.ReadAllText(settingsPath);
                var m = System.Text.RegularExpressions.Regex.Match(txt, @"CREST_SIMPLE_TRANSPARENCY \((\d)\)");
                if (m.Success) simple = m.Groups[1].Value == "1" ? "开启(1)" : "关闭(0)";
            }
            _sb.AppendLine($"CREST_SIMPLE_TRANSPARENCY = {simple}（编译期宏，改它要重新生成 shader）");

            // ── 2. chunk 数量与可见性 ──
            // `WaterChunkRenderer` 是 internal，编辑器程序集访问不到（CS0122，见 Docs/water.md §11.6），
            // 所以整段走反射：拿 Chunks 列表，再逐项读 _LodIndex / _Culled / Rend。
            // Chunks 是 internal 属性（`internal List<WaterChunkRenderer> Chunks { get; }`），
            // 不是字段，必须用 GetProperty。
            var chunks = typeof(SurfaceRenderer)
                .GetProperty("Chunks", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public)
                ?.GetValue(surface) as IEnumerable;
            int total = 0, visible = 0, culled = 0, off = 0;
            var perLod = new Dictionary<int, int>();
            if (chunks != null)
            {
                foreach (var c in chunks)
                {
                    if (c == null) continue;
                    total++;
                    var t = c.GetType();
                    var rend = t.GetProperty("Rend", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance)
                                   ?.GetValue(c) as Renderer;
                    if (rend == null) continue;
                    if (rend.isVisible) visible++;
                    if ((bool)(t.GetField("_Culled", BindingFlags.NonPublic | BindingFlags.Instance)
                                   ?.GetValue(c) ?? false)) culled++;
                    if (rend.forceRenderingOff) off++;
                    var lod = (int)(t.GetField("_LodIndex", BindingFlags.NonPublic | BindingFlags.Instance)
                                       ?.GetValue(c) ?? -1);
                    perLod[lod] = perLod.TryGetValue(lod, out var n) ? n + 1 : 1;
                }
            }
            _sb.AppendLine("");
            _sb.AppendLine("=== 水面 chunk ===");
            _sb.AppendLine($"总数 = {total}，isVisible = {visible}，_Culled = {culled}，forceRenderingOff = {off}");
            foreach (var kv in perLod.OrderBy(k => k.Key))
                _sb.AppendLine($"  LOD{kv.Key}: {kv.Value} 个 chunk");

            // ── 3. 本帧 render pass 队列 ──
            // 用 beginCameraRendering 抓的快照（Update 里队列已被 URP 清空）。
            // batchmode 下没有主循环渲染，回调不会自然触发，这里主动 Render 一次来驱动。
            var probeCam = UnityEngine.Object.FindObjectsOfType<Camera>()
                .FirstOrDefault(c => c.enabled && c.cameraType == CameraType.Game) ?? Camera.main;
            if (probeCam != null)
            {
                var rt = new RenderTexture(320, 180, 24, RenderTextureFormat.ARGB32);
                var prev = probeCam.targetTexture;
                probeCam.targetTexture = rt;
                for (int i = 0; i < 3; i++) probeCam.Render();   // 多次以覆盖仿真首帧
                probeCam.targetTexture = prev;
                rt.Release();
                DestroyImmediate(rt);
            }

            _sb.AppendLine("");
            _sb.AppendLine("=== 本帧 render pass 队列 ===");
            _sb.AppendLine($"渲染相机 = {_probeCamName ?? "未抓到"}");

            if (_passSnapshot == null)
            {
                _sb.AppendLine("FAIL：没抓到 pass 快照（beginCameraRendering 未触发？）");
            }
            else
            {
                _sb.AppendLine($"feature 入队 pass 总数 = {_passSnapshot.Count}（不含 URP 内部 pass）");
                _sb.AppendLine($"Crest/自定义 CopyColorPass = {_copyColorCount}  ← 每多一个就是一次全屏 color copy");
                _sb.AppendLine($"Crest/自定义 CopyDepthPass = {_copyDepthCount}  ← 每多一个就是一次全屏 depth copy");
                _sb.AppendLine("注意：URP 在 renderer.Setup() 里入队的 CopyDepthPass 在这里看不见；");
                _sb.AppendLine("      当前 Enviro 的 1 次 CopyDepth 需要 Frame Debugger 确认。");
                _sb.AppendLine("");
                _sb.AppendLine("完整队列：");
                foreach (var line in _passSnapshot) _sb.AppendLine(line);
                _sb.AppendLine("");
                _sb.AppendLine("多帧采样（确认不是单帧偶然）：");
                foreach (var l in _frameLog) _sb.AppendLine("  " + l);
            }

            // ── 4. 结论式摘要 ──
            _sb.AppendLine("");
            _sb.AppendLine("=== 判读 ===");
            if (chunks != null && total > 0)
            {
                _sb.AppendLine($"• Chunks 模式：{total} 个 chunk 各自一次 DrawRenderer，" +
                               $"可见 {visible} 个 ≈ {visible} 次 draw call（SRP Batcher 合并不了，" +
                               $"因为是 CommandBuffer.DrawRenderer 逐个提交）");
            }
            _sb.AppendLine("• 本列表只含 Crest/自定义 feature 的 pass；URP 内部 CopyDepthPass 不可见，别把 0 当成“管线没有拷贝”");

            Finish();
        }

        private static object GetPrivate(object obj, string field)
        {
            var f = obj.GetType().GetField(field, BindingFlags.NonPublic | BindingFlags.Instance);
            return f?.GetValue(obj);
        }

        private void Finish()
        {
            File.WriteAllText(ResultPath, _sb.ToString());
            // 不先 isPlaying=false：退出 Play Mode 会触发域重载 + 全量资产重导入
            // （实测 20s 资产导入 + 10s 备份场景落盘）。本进程紧接着 Exit，
            // Play Mode 状态不会保留到下次运行，直接退出最省时间。
            gameObject.AddComponent<QuitAfter>().Init();
        }
    }

    private sealed class QuitAfter : MonoBehaviour
    {
        private int _f;
        public void Init() { hideFlags = HideFlags.DontSave; }
        private void Update()
        {
            if (++_f < 3) return;
            EditorApplication.Exit(0);
        }
    }
}
