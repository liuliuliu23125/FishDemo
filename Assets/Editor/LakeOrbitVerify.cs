using System;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using WaveHarmonic.Crest;

/// <summary>
/// 环湖轨道相机的运行时验收：证明「真的在绕湖转」和「切换后各系统跟随」。
///
/// ── 判据（每条都能落到具体数值）──
/// 1. **启动即在轨道模式**：`LakeCameraRig.IsOrbitActive == true`，
///    轨道相机的 `enabled == true`，原相机 `enabled == false`。
/// 2. **真在绕圈**：采样 N 帧的相机位置，拟合到湖心。
///    - 每个采样点到湖心的**水平距离**必须恒等于设定半径（±2m）；
///    - 累积扫过的**圆心角**必须明显大于 0（证明在动，而不是停着）；
///    - **高度不穿模**：每个采样点必须高于该点地表（`Terrain.SampleHeight`）至少
///      `MinGroundClearance`，且不低于水位 + 最低水上高度。
///      2026-11-17 起高度是地形跟随的，不再恒定——旧的「高度跨度 < 0.01」判据会误报。
/// 3. **始终朝湖心**：每帧 `forward` 与「相机指向湖心」的水平方向夹角 < 15°。
/// 4. **Crest Viewer 跟随 + Viewpoint 固定**：`water.Viewer == 轨道相机`，且 `Viewpoint != null`。
/// 5. **SECTR loader 唯一**：同一时刻只有 1 个 `SECTR_RegionLoader.enabled == true`，
///    且它挂在当前渲染相机上 —— 两个同时开会造成加载范围叠加、chunk 常驻。
/// 6. **切换回原相机后不破坏原行为**：`rig.Apply(false)` 之后原相机 `enabled == true`
///    且 `water.Viewer` 变回原相机。
///
/// ── 为什么不验像素 ──
/// 轨道运动是纯数学，位置/朝向/角度都能精确断言，比截图可靠得多。
/// 画面质量由 `LakeWaterVerify` 负责。
///
/// 用法：-executeMethod LakeOrbitVerify.Run
/// 输出 Logs/Lake/lake-orbit.txt
/// </summary>
[InitializeOnLoad]
public static class LakeOrbitVerify
{
    private const string Tag = "[LakeOrbit]";
    private const string WorldScenePath = "Assets/Scenes/World.unity";
    private const string OutDir = "Logs/Lake";
    private const string ResultPath = OutDir + "/lake-orbit.txt";
    private const string PendingKey = "FishDemo.LakeOrbit.Pending";

    static LakeOrbitVerify()
    {
        EditorApplication.update += Poll;
    }

    public static void Run()
    {
        var sb = new StringBuilder();
        void W(string s) { Debug.Log($"{Tag} {s}"); sb.AppendLine(s); }

        Directory.CreateDirectory(OutDir);
        EditorSceneManager.OpenScene(WorldScenePath, OpenSceneMode.Single);

        // 高度跟随地形后，"不穿模"判据依赖 Terrain.SampleHeight。
        // 必须先把湖区 chunk 预加载进来，否则 Play Mode 时 Terrain.activeTerrains
        // 可能还是空的，离地净空会"空过"（minClearance 恒为 float.MaxValue）。
        ChunkLoadScope.LoadRelevant(ChunkLoadScope.Area.FromLake(), W);
        EditorSceneManager.SetActiveScene(EditorSceneManager.GetSceneByPath(WorldScenePath));

        // 草 BRG 的 429 万实例构建会在进 Play Mode 时拖慢首帧；验收不需要草。
        // 必须在进 Play Mode 之前禁用（之后禁用已经晚了）。
        foreach (var g in UnityEngine.Object.FindObjectsOfType<FishDemo.Rendering.GrassBRGManager>())
        {
            g.gameObject.SetActive(false);
            W($"进入 Play Mode 前禁用草 BRG：{g.name}");
        }

        var rig = UnityEngine.Object.FindObjectOfType<FishDemo.Gameplay.LakeCameraRig>();
        var orbit = UnityEngine.Object.FindObjectOfType<FishDemo.Gameplay.LakeOrbitCamera>();
        var water = UnityEngine.Object.FindObjectOfType<WaterRenderer>();
        if (rig == null) { W("FAIL：场景里没有 LakeCameraRig"); Flush(sb); return; }
        if (orbit == null) { W("FAIL：场景里没有 LakeOrbitCamera"); Flush(sb); return; }
        if (water == null) { W("FAIL：场景里没有 WaterRenderer"); Flush(sb); return; }

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
        new GameObject("~LakeOrbitDriver").AddComponent<Driver>().Begin(sb);
    }

    private sealed class Driver : MonoBehaviour
    {
        private StringBuilder _sb;
        private int _frame;
        private int _phase;

        private FishDemo.Gameplay.LakeCameraRig _rig;
        private FishDemo.Gameplay.LakeOrbitCamera _orbit;
        private Camera _orbitCam;
        private Camera _tourCam;
        private WaterRenderer _water;

        // 采样
        private const int SampleCount = 120;     // 采 120 帧
        /// <summary>不穿模判据：相机离地表的最小净空（米）。与 LakeOrbitCamera 的 18m 留出余量。</summary>
        private const float MinGroundClearance = 10f;
        /// <summary>相机离水面的最小高度（米）。</summary>
        private const float MinAboveWater = 20f;
        private Vector3[] _positions;
        private Vector3[] _forwards;
        private int _sampled;

        // 自由飞行探测阶段：0=轨道采样中，1=等飞行位移保持，2=等轨道接管
        private int _flyProbeStage;
        private int _flyProbeFrames;
        private Vector3 _flyProbePos;

        // 各判据结果提到字段：Analyze 与 CheckOrbitResumed 分两帧执行，局部变量传不过去。
        private bool _ok, _c1, _c1b, _c2, _c3, _c4, _c5, _c6, _c7;
        // 跨方法使用的中间量（打印报告时要用）
        private float _totalAngle, _maxAngleErr;
        private bool _held, _exited;

        public void Begin(StringBuilder sb)
        {
            _sb = sb;
            gameObject.hideFlags = HideFlags.DontSave;

            _rig = UnityEngine.Object.FindObjectOfType<FishDemo.Gameplay.LakeCameraRig>();
            _orbit = UnityEngine.Object.FindObjectOfType<FishDemo.Gameplay.LakeOrbitCamera>();
            _water = UnityEngine.Object.FindObjectOfType<WaterRenderer>();
            _orbitCam = _orbit.GetComponent<Camera>();
            var tour = GameObject.Find("Main Camera");
            _tourCam = tour != null ? tour.GetComponent<Camera>() : null;

            _positions = new Vector3[SampleCount];
            _forwards = new Vector3[SampleCount];

            // batchmode `-nographics` 下 Time.deltaTime 不推进（实测 120 帧只走 0.6°），
            // 用固定步长替换时间源。步长取 1.0s：120 帧 × 1s = 120s = **整整一圈**，
            // 这样采样覆盖 0°~360° 全周，能验到西北侧 270°~315° 的高地形（312~320m）。
            // 早先取 0.05s 只扫过 18°，那一段地形低，穿模 bug 会被漏掉。
            const float FixedStep = 1.0f;
            _orbit.OverrideDeltaTime(() => FixedStep);
            _sb.AppendLine($"时间源 = 固定步长 {FixedStep}s（120 帧 = 一整圈 360°）");

            _sb.AppendLine($"启动状态：rig.IsOrbitActive={_rig.IsOrbitActive}, " +
                           $"轨道相机 enabled={_orbitCam.enabled}, 原相机 enabled={_tourCam?.enabled}");
        }

        private void Update()
        {
            _frame++;

            // 阶段 1/2：自由飞行探测
            if (_flyProbeStage == 1) { CheckFlyProbe(); return; }
            if (_flyProbeStage == 2) { CheckOrbitResumed(); return; }

            // 阶段 0：等 10 帧让 Awake/第一次 LateUpdate 跑完，再采样 120 帧轨道
            if (_frame > 10 && _sampled < SampleCount)
            {
                _positions[_sampled] = _orbitCam.transform.position;
                _forwards[_sampled] = _orbitCam.transform.forward;
                _sampled++;
                if (_sampled == SampleCount) Analyze();
                return;
            }
        }

        private void Analyze()
        {
            _orbit.OverrideDeltaTime(null);   // 还原默认时间源

            _ok = true;
            var center = _orbit.transform.position;   // 轨道脚本每帧重写，这里只用来读参数不合适
            // 湖心从 LakeCarve 的常量来（与 LakeOrbitCamera 的默认一致）。
            var lakeCenter = new Vector3(192f, LakeCarve.WaterLevel, 96f);

            _sb.AppendLine("");
            _sb.AppendLine("=== 轨道几何 ===");

            // ── 半径恒定 + 高度不穿模 ──
            // 高度自 2026-11-17 起跟随地形（固定 290m 会在西北侧 312~320m 的地形里穿模），
            // 所以不再断言高度恒定，改为断言「每帧都高于地表」。
            float minR = float.MaxValue, maxR = float.MinValue;
            float minY = float.MaxValue, maxY = float.MinValue;
            float minClearance = float.MaxValue;       // 相对地表的净空
            float minAboveWater = float.MaxValue;      // 相对水位的净空
            int groundMisses = 0;
            foreach (var p in _positions)
            {
                float r = new Vector2(p.x - lakeCenter.x, p.z - lakeCenter.z).magnitude;
                minR = Mathf.Min(minR, r); maxR = Mathf.Max(maxR, r);
                minY = Mathf.Min(minY, p.y); maxY = Mathf.Max(maxY, p.y);

                float ground = SampleGroundHeight(p.x, p.z);
                if (float.IsNaN(ground))
                {
                    groundMisses++;
                }
                else
                {
                    minClearance = Mathf.Min(minClearance, p.y - ground);
                }
                minAboveWater = Mathf.Min(minAboveWater, p.y - lakeCenter.y);
            }

            float rSpread = maxR - minR;
            _c1 = rSpread < 2f;
            _sb.AppendLine($"半径: {minR:F2} ~ {maxR:F2} m（跨度 {rSpread:F3} < 2）");
            _sb.AppendLine($"高度: {minY:F2} ~ {maxY:F2} m（跨度 {maxY - minY:F3}，地形跟随，不再要求恒定）");
            if (!_c1) _ok = false;

            // 不穿模：采样到的每个点都要高于地表。grid 采不到的点（chunk 未加载）单独计数。
            bool noClipping = minClearance >= MinGroundClearance - 0.5f;
            bool aboveWater = minAboveWater >= MinAboveWater - 0.5f;
            _sb.AppendLine($"离地净空: 最小 {minClearance:F2} m（要求 ≥ {MinGroundClearance}，" +
                           $"采不到地形的采样点 {groundMisses}/{_sampled}）");
            _sb.AppendLine($"离水净空: 最小 {minAboveWater:F2} m（要求 ≥ {MinAboveWater}）");
            _c1b = noClipping && aboveWater;
            if (!_c1b) _ok = false;

            // ── 累积扫过的圆心角 ──
            _totalAngle = 0f;
            for (int i = 1; i < _sampled; i++)
            {
                var a0 = new Vector2(_positions[i - 1].x - lakeCenter.x, _positions[i - 1].z - lakeCenter.z);
                var a1 = new Vector2(_positions[i].x - lakeCenter.x, _positions[i].z - lakeCenter.z);
                _totalAngle += Vector2.Angle(a0, a1);
            }
            _c2 = _totalAngle > 15f;
            _sb.AppendLine($"累积扫过角度: {_totalAngle:F1}°（> 15°，证明在运动）");
            if (!_c2) _ok = false;

            // ── 朝向：forward 应指向湖心（水平投影）──
            _maxAngleErr = 0f;
            for (int i = 0; i < _sampled; i++)
            {
                var p = _positions[i];
                var toCenter = new Vector3(lakeCenter.x - p.x, 0f, lakeCenter.z - p.z).normalized;
                var fwdH = new Vector3(_forwards[i].x, 0f, _forwards[i].z);
                if (fwdH.sqrMagnitude < 0.01f) { _maxAngleErr = 999f; break; }
                float err = Vector3.Angle(fwdH.normalized, toCenter);
                _maxAngleErr = Mathf.Max(_maxAngleErr, err);
            }
            _c3 = _maxAngleErr < 15f;
            _sb.AppendLine($"朝向偏差: 最大 {_maxAngleErr:F1}°（< 15°，始终朝湖心）");
            if (!_c3) _ok = false;

            // ── Crest Viewer + 固定 Viewpoint ──
            _sb.AppendLine("");
            _sb.AppendLine("=== 系统跟随 ===");
            var viewerOk = _water.Viewer == _orbitCam;
            var viewpointOk = _water.Viewpoint != null;
            _c4 = viewerOk && viewpointOk;
            _sb.AppendLine($"Crest Viewer == 轨道相机 : {viewerOk}（实际 {(_water.Viewer != null ? _water.Viewer.name : "null")}）");
            _sb.AppendLine($"Crest Viewpoint = {(_water.Viewpoint != null ? _water.Viewpoint.name : "null")}" +
                           "（固定湖心，不跟相机）");
            if (!_c4) _ok = false;

            // ── SECTR loader 唯一且挂在当前相机 ──
            var enabledLoaders = UnityEngine.Object.FindObjectsOfType<SECTR_RegionLoader>()
                .Where(l => l.enabled).ToArray();
            _c5 = enabledLoaders.Length == 1 &&
                      enabledLoaders[0].gameObject == _orbitCam.gameObject;
            _sb.AppendLine($"启用的 SECTR_RegionLoader: {enabledLoaders.Length} 个" +
                           $"（期望 1，且挂在轨道相机上；实际 {string.Join(",", enabledLoaders.Select(l => l.gameObject.name))}）");
            if (!_c5) _ok = false;

            // ── 切回原相机 ──
            _rig.Apply(false);
            _c6 = _tourCam.enabled && !_orbitCam.enabled && _water.Viewer == _tourCam &&
                  _water.Viewpoint != null;
            _sb.AppendLine($"切换回原相机: 原相机 enabled={_tourCam.enabled}, 轨道 enabled={_orbitCam.enabled}, " +
                           $"Viewer==原相机 {_water.Viewer == _tourCam}, Viewpoint 仍固定 {_water.Viewpoint != null}");
            if (!_c6) _ok = false;

            var loadersAfter = UnityEngine.Object.FindObjectsOfType<SECTR_RegionLoader>().Where(l => l.enabled).ToArray();
            _c7 = loadersAfter.Length == 1 && loadersAfter[0].gameObject == _tourCam.gameObject;
            _sb.AppendLine($"切换后启用的 loader: {loadersAfter.Length} 个" +
                           $"（期望 1，挂在原相机上；实际 {string.Join(",", loadersAfter.Select(l => l.gameObject.name))}）");
            if (!_c7) _ok = false;

            // ── 自由飞行（F 键）──
            // 关键判据：进入飞行后，轨道脚本必须让出 transform（否则 LateUpdate 会把
            // 飞行位移覆盖掉，表现为"按 F 没反应"）。这里直接验证三件事：
            //   ① 飞行组件挂在当前渲染相机上；
            //   ② 轨道脚本 Paused == true；
            //   ③ 手动位移相机后，经过若干帧位置不被轨道脚本改回去。
            _rig.Apply(true);
            _rig.EnterFlyMode();
            var flyComp = _orbitCam.GetComponent<Pegasus.PegasusFlyingCamera>();
            bool flyAttached = flyComp != null;
            bool orbitPaused = _orbit.Paused;
            _sb.AppendLine("");
            _sb.AppendLine("=== 自由飞行（F）===");
            _sb.AppendLine($"飞行组件挂在轨道相机上 = {flyAttached}，轨道脚本 Paused = {orbitPaused}");
            if (!flyAttached || !orbitPaused) _ok = false;

            // 手动把相机推到一个明显偏离轨道的位置，等几帧看轨道有没有抢回去。
            var pushed = _orbitCam.transform.position + new Vector3(40f, 15f, 40f);
            _orbitCam.transform.position = pushed;
            _flyProbePos = pushed;
            _flyProbeFrames = 0;
            _flyProbeStage = 1;
            _sb.AppendLine($"已把相机推到偏离轨道的位置 {pushed}，等待 15 帧观察是否被轨道覆盖");

            File.WriteAllText(OutDir + "/lake-orbit-partial.txt", _sb.ToString());
            _flyProbeStage = 1;   // 转到 Update 里继续
        }

        /// <summary>
        /// 世界 XZ 处的地表高度；采不到（chunk 未加载）返回 NaN。
        /// 与 LakeOrbitCamera.SampleGroundHeight 同一换算：
        /// `Terrain.SampleHeight(worldXZ)` 返回的是相对地形本地原点的高度，必须加 `position.y`。
        /// </summary>
        private static float SampleGroundHeight(float x, float z)
        {
            foreach (var t in Terrain.activeTerrains)
            {
                if (t == null || t.terrainData == null) continue;
                var p = t.transform.position;
                var s = t.terrainData.size;
                if (x < p.x || x > p.x + s.x || z < p.z || z > p.z + s.z) continue;
                return p.y + t.SampleHeight(new Vector3(x, 0f, z));
            }
            return float.NaN;
        }

        /// <summary>自由飞行的位移保持探测：等若干帧后核对相机是否还在被推到的位置。</summary>
        private void CheckFlyProbe()
        {
            _flyProbeFrames++;
            if (_flyProbeFrames < 15) return;

            var cur = _orbitCam.transform.position;
            float drift = (cur - _flyProbePos).magnitude;
            _held = drift < 1f;
            _sb.AppendLine($"15 帧后相机位置漂移 = {drift:F3}m（< 1 说明轨道没有覆盖飞行位移）: {(_held ? "PASS" : "FAIL")}");

            // 退出飞行，验证轨道能接回来（半径回到 300m）。
            // 注意：`Destroy` 到帧末才真正销毁，同帧 GetComponent 仍能取到组件，
            // 所以"组件已移除"必须留到下一阶段（CheckOrbitResumed）再判。
            _rig.ExitFlyMode();
            _sb.AppendLine($"已调用 ExitFlyMode：轨道 Paused={_orbit.Paused}（组件销毁延迟到帧末，下一阶段核对）");

            // 再等几帧让轨道把机位拉回圆周，然后量半径。
            _flyProbeStage = 2;
            _flyProbeFrames = 0;
            _sb.AppendLine($"已退出飞行，等待 15 帧核对轨道是否接管");
        }

        private void CheckOrbitResumed()
        {
            _flyProbeFrames++;
            if (_flyProbeFrames < 15) return;

            // 到这里 Destroy 已生效，可以真正判定"组件已移除"。
            _exited = _orbitCam.GetComponent<Pegasus.PegasusFlyingCamera>() == null && !_orbit.Paused;
            _sb.AppendLine($"退出飞行后：飞行组件已移除={_orbitCam.GetComponent<Pegasus.PegasusFlyingCamera>() == null}, " +
                           $"轨道 Paused={_orbit.Paused}");

            var p = _orbitCam.transform.position;
            float r = new Vector2(p.x - 192f, p.z - 96f).magnitude;
            bool backOnCircle = Mathf.Abs(r - 300f) < 2f;
            _sb.AppendLine($"退出飞行后半径 = {r:F2}m（期望 ≈300）: {(backOnCircle ? "PASS" : "FAIL")}");

            _sb.AppendLine("");
            _sb.AppendLine("===== 环湖轨道相机验收 =====");
            _sb.AppendLine($"[1] 半径恒定         : {(_c1 ? "PASS" : "FAIL")}");
            _sb.AppendLine($"[1b] 不穿模（离地/离水净空）: {(_c1b ? "PASS" : "FAIL")}");
            _sb.AppendLine($"[2] 确实在运动       : {(_c2 ? "PASS" : "FAIL")} ({_totalAngle:F1}°)");
            _sb.AppendLine($"[3] 始终朝湖心       : {(_c3 ? "PASS" : "FAIL")} (最大偏差 {_maxAngleErr:F1}°)");
            _sb.AppendLine($"[4] Viewer+固定Viewpoint: {(_c4 ? "PASS" : "FAIL")}");
            _sb.AppendLine($"[5] loader 唯一      : {(_c5 ? "PASS" : "FAIL")}");
            _sb.AppendLine($"[6] 切回原相机正常   : {(_c6 ? "PASS" : "FAIL")}");
            _sb.AppendLine($"[7] 切换后 loader 转移: {(_c7 ? "PASS" : "FAIL")}");
            _sb.AppendLine($"[8] F 键飞行可用     : {(_held && _exited ? "PASS" : "FAIL")} " +
                           $"(飞行位移保持={_held}, 可退出={_exited})");
            _sb.AppendLine($"[9] 退出后轨道接管   : {(backOnCircle ? "PASS" : "FAIL")} (半径 {r:F2}m)");

            bool all = _ok && _held && _exited && backOnCircle;
            _sb.AppendLine("");
            _sb.AppendLine(all ? "==== 结论：PASS ====" : "==== 结论：FAIL ====");

            File.WriteAllText(ResultPath, _sb.ToString());
            // 不先 isPlaying=false：退出 Play Mode 会触发域重载 + 全量资产重导入
            // （实测 20s 资产导入 + 10s 备份场景落盘）。本进程紧接着 Exit，
            // Play Mode 状态不会保留到下次运行，直接退出最省时间。
            gameObject.AddComponent<QuitAfter>().Init();
            enabled = false;
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

    private static void Flush(StringBuilder sb)
    {
        Directory.CreateDirectory(OutDir);
        File.WriteAllText(ResultPath, sb.ToString());
        EditorApplication.Exit(1);
    }
}
