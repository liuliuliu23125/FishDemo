using System;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using WaveHarmonic.Crest;

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

        ChunkLoadScope.LoadRelevant(ChunkLoadScope.Area.FromLake(), W);
        EditorSceneManager.SetActiveScene(EditorSceneManager.GetSceneByPath(WorldScenePath));

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

        private const int SampleCount = 120;
        private const float MinGroundClearance = 10f;
        private const float MinAboveWater = 20f;
        private Vector3[] _positions;
        private Vector3[] _forwards;
        private int _sampled;

        private int _flyProbeStage;
        private int _flyProbeFrames;
        private Vector3 _flyProbePos;

        private bool _ok, _c1, _c1b, _c2, _c3, _c4, _c5, _c6, _c7;
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

            const float FixedStep = 1.0f;
            _orbit.OverrideDeltaTime(() => FixedStep);
            _sb.AppendLine($"时间源 = 固定步长 {FixedStep}s（120 帧 = 一整圈 360°）");

            _sb.AppendLine($"启动状态：rig.IsOrbitActive={_rig.IsOrbitActive}, " +
                           $"轨道相机 enabled={_orbitCam.enabled}, 原相机 enabled={_tourCam?.enabled}");
        }

        private void Update()
        {
            _frame++;

            if (_flyProbeStage == 1) { CheckFlyProbe(); return; }
            if (_flyProbeStage == 2) { CheckOrbitResumed(); return; }

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
            _orbit.OverrideDeltaTime(null);

            _ok = true;
            var center = _orbit.transform.position;
            var lakeCenter = new Vector3(192f, LakeCarve.WaterLevel, 96f);

            _sb.AppendLine("");
            _sb.AppendLine("=== 轨道几何 ===");

            float minR = float.MaxValue, maxR = float.MinValue;
            float minY = float.MaxValue, maxY = float.MinValue;
            float minClearance = float.MaxValue;
            float minAboveWater = float.MaxValue;
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

            bool noClipping = minClearance >= MinGroundClearance - 0.5f;
            bool aboveWater = minAboveWater >= MinAboveWater - 0.5f;
            _sb.AppendLine($"离地净空: 最小 {minClearance:F2} m（要求 ≥ {MinGroundClearance}，" +
                           $"采不到地形的采样点 {groundMisses}/{_sampled}）");
            _sb.AppendLine($"离水净空: 最小 {minAboveWater:F2} m（要求 ≥ {MinAboveWater}）");
            _c1b = noClipping && aboveWater;
            if (!_c1b) _ok = false;

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

            _sb.AppendLine("");
            _sb.AppendLine("=== 系统跟随 ===");
            var viewerOk = _water.Viewer == _orbitCam;
            var viewpointOk = _water.Viewpoint != null;
            _c4 = viewerOk && viewpointOk;
            _sb.AppendLine($"Crest Viewer == 轨道相机 : {viewerOk}（实际 {(_water.Viewer != null ? _water.Viewer.name : "null")}）");
            _sb.AppendLine($"Crest Viewpoint = {(_water.Viewpoint != null ? _water.Viewpoint.name : "null")}" +
                           "（固定湖心，不跟相机）");
            if (!_c4) _ok = false;

            var enabledLoaders = UnityEngine.Object.FindObjectsOfType<SECTR_RegionLoader>()
                .Where(l => l.enabled).ToArray();
            _c5 = enabledLoaders.Length == 1 &&
                      enabledLoaders[0].gameObject == _orbitCam.gameObject;
            _sb.AppendLine($"启用的 SECTR_RegionLoader: {enabledLoaders.Length} 个" +
                           $"（期望 1，且挂在轨道相机上；实际 {string.Join(",", enabledLoaders.Select(l => l.gameObject.name))}）");
            if (!_c5) _ok = false;

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

            _rig.Apply(true);
            _rig.EnterFlyMode();
            var flyComp = _orbitCam.GetComponent<Pegasus.PegasusFlyingCamera>();
            bool flyAttached = flyComp != null;
            bool orbitPaused = _orbit.Paused;
            _sb.AppendLine("");
            _sb.AppendLine("=== 自由飞行（F）===");
            _sb.AppendLine($"飞行组件挂在轨道相机上 = {flyAttached}，轨道脚本 Paused = {orbitPaused}");
            if (!flyAttached || !orbitPaused) _ok = false;

            var pushed = _orbitCam.transform.position + new Vector3(40f, 15f, 40f);
            _orbitCam.transform.position = pushed;
            _flyProbePos = pushed;
            _flyProbeFrames = 0;
            _flyProbeStage = 1;
            _sb.AppendLine($"已把相机推到偏离轨道的位置 {pushed}，等待 15 帧观察是否被轨道覆盖");

            File.WriteAllText(OutDir + "/lake-orbit-partial.txt", _sb.ToString());
            _flyProbeStage = 1;
        }

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

        private void CheckFlyProbe()
        {
            _flyProbeFrames++;
            if (_flyProbeFrames < 15) return;

            var cur = _orbitCam.transform.position;
            float drift = (cur - _flyProbePos).magnitude;
            _held = drift < 1f;
            _sb.AppendLine($"15 帧后相机位置漂移 = {drift:F3}m（< 1 说明轨道没有覆盖飞行位移）: {(_held ? "PASS" : "FAIL")}");

            _rig.ExitFlyMode();
            _sb.AppendLine($"已调用 ExitFlyMode：轨道 Paused={_orbit.Paused}（组件销毁延迟到帧末，下一阶段核对）");

            _flyProbeStage = 2;
            _flyProbeFrames = 0;
            _sb.AppendLine($"已退出飞行，等待 15 帧核对轨道是否接管");
        }

        private void CheckOrbitResumed()
        {
            _flyProbeFrames++;
            if (_flyProbeFrames < 15) return;

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
