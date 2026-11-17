using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

[InitializeOnLoad]
public static class PegasusTourVerify
{
    private const string Tag = "[PegasusVerify]";
    private const string WorldScenePath = "Assets/Scenes/World.unity";
    private const string ResultPath = "Logs/pegasus-tour-verify.txt";

    private const string PendingKey = "FishDemo.PegasusVerify.Pending";

    private const float ObserveSeconds = 60f;

    static PegasusTourVerify()
    {
        EditorApplication.update += Poll;
    }

    public static void Run()
    {
        var sb = new StringBuilder();
        void W(string s) { Debug.Log($"{Tag} {s}"); sb.AppendLine(s); }

        var scene = EditorSceneManager.OpenScene(WorldScenePath, OpenSceneMode.Single);

        var manager = Object.FindObjectOfType<Pegasus.PegasusManager>();
        var camera = FindTourCamera(manager);

        if (manager == null) { W("FAIL：场景里没有 PegasusManager"); Flush(sb); return; }
        if (camera == null) { W("FAIL：场景里没有相机"); Flush(sb); return; }

        W($"前置：Manager={manager.name}，Target={(manager.m_target ? manager.m_target.name : "null")}，" +
          $"POI={manager.transform.childCount}，总里程={manager.m_totalDistance / 1000f:F2}km");
        W($"Manager 高度校验={manager.m_heightCheckType}（None 表示已用烘焙高度），" +
          $"自动启动={manager.m_autoStartAtRuntime}");

        SessionState.SetString(PendingKey + ".Pre", sb.ToString());
        SessionState.SetBool(PendingKey, true);

        EditorApplication.isPlaying = true;
    }

    private static void Poll()
    {
        if (!SessionState.GetBool(PendingKey, false))
            return;
        if (!EditorApplication.isPlaying)
            return;

        SessionState.SetBool(PendingKey, false);
        EditorApplication.update -= Poll;

        var sb = new StringBuilder(SessionState.GetString(PendingKey + ".Pre", string.Empty));
        void W(string s) { Debug.Log($"{Tag} {s}"); sb.AppendLine(s); }

        var manager = Object.FindObjectOfType<Pegasus.PegasusManager>();
        var camera = FindTourCamera(manager);

        if (manager == null || camera == null)
        {
            W("FAIL：进入 Play Mode 后找不到 Manager 或相机");
            Flush(sb);
            EditorApplication.isPlaying = false;
            EditorApplication.Exit(1);
            return;
        }

        PrepareTourCamera(camera);

        var driver = new GameObject("~PegasusVerifyDriver").AddComponent<VerifyDriver>();
        driver.Begin(W, sb, camera, manager);
    }

    private static Camera FindTourCamera(Pegasus.PegasusManager manager)
    {
        if (manager != null && manager.m_target != null)
        {
            var targetCamera = manager.m_target.GetComponent<Camera>();
            if (targetCamera != null)
                return targetCamera;
        }

        return Object.FindObjectsOfType<Camera>()
            .FirstOrDefault(c => c.CompareTag("MainCamera")) ?? Object.FindObjectOfType<Camera>();
    }

    private static void PrepareTourCamera(Camera camera)
    {
        if (camera == null)
            return;

        var rig = Object.FindObjectOfType<FishDemo.Gameplay.LakeCameraRig>();
        if (rig != null)
            rig.enabled = false;

        var orbit = Object.FindObjectOfType<FishDemo.Gameplay.LakeOrbitCamera>();
        if (orbit != null)
        {
            orbit.enabled = false;
            var orbitCamera = orbit.GetComponent<Camera>();
            if (orbitCamera != null)
            {
                orbitCamera.enabled = false;
                var orbitLoader = orbitCamera.GetComponent<SECTR_RegionLoader>();
                if (orbitLoader != null)
                    orbitLoader.enabled = false;
            }
        }

        var tourController = camera.GetComponent<FishDemo.Gameplay.PegasusTourController>();
        if (tourController != null)
            tourController.enabled = false;

        camera.enabled = true;
        var loader = camera.GetComponent<SECTR_RegionLoader>();
        if (loader != null)
            loader.enabled = true;
    }

    private sealed class VerifyDriver : MonoBehaviour
    {
        private System.Action<string> _w;
        private StringBuilder _sb;
        private Camera _camera;
        private Pegasus.PegasusManager _manager;

        private float _t0 = -1f;
        private Vector3 _lastPos;
        private float _movedDistance;
        private float _minRadius = float.MaxValue;
        private float _maxRadius;
        private float _minY = float.MaxValue, _maxY = float.MinValue;
        private int _maxLoadedChunks;
        private int _minLoadedChunks = int.MaxValue;
        private readonly HashSet<string> _loadedSeen = new HashSet<string>();
        private int _sampleCount;
        private bool _sweeping;
        private bool _sweepDone;
        private int _sweepStep;

        private const int SweepSteps = 96;

        private const float SweepDwellSeconds = 0.8f;

        private float _sweepDwellUntil;
        private SECTR_Chunk[] _chunks;

        private bool _toggleTested;
        private bool _manualEntered;
        private bool _autoRestored;
        private bool _finished;

        public void Begin(System.Action<string> w, StringBuilder sb, Camera camera,
            Pegasus.PegasusManager manager)
        {
            _w = w; _sb = sb; _camera = camera; _manager = manager;
            _chunks = Object.FindObjectsOfType<SECTR_Chunk>();
            gameObject.hideFlags = HideFlags.DontSave;
        }

        private void Update()
        {
            if (_finished)
                return;

            if (_t0 < 0f)
            {
                _t0 = Time.time;
                _lastPos = _camera.transform.position;
                _w($"Play 首帧：相机 {_lastPos}，Pegasus 状态 {_manager.m_currentState}，" +
                   $"已加载 chunk {CountLoaded()}");
                return;
            }

            float elapsed = Time.time - _t0;
            Vector3 pos = _camera.transform.position;

            _movedDistance += Vector3.Distance(pos, _lastPos);
            _lastPos = pos;

            if (!_sweeping)
            {
                float radius = new Vector2(pos.x, pos.z).magnitude;
                _minRadius = Mathf.Min(_minRadius, radius);
                _maxRadius = Mathf.Max(_maxRadius, radius);
                _minY = Mathf.Min(_minY, pos.y);
                _maxY = Mathf.Max(_maxY, pos.y);
            }

            int loaded = CountLoaded();
            _maxLoadedChunks = Mathf.Max(_maxLoadedChunks, loaded);
            _minLoadedChunks = Mathf.Min(_minLoadedChunks, loaded);
            _sampleCount++;

            if (!_toggleTested && elapsed > ObserveSeconds * 0.5f)
            {
                _toggleTested = true;
                TestManualToggle();
            }

            if (elapsed >= ObserveSeconds && (!_sweeping || _sweepDone))
            {
                _finished = true;
                Report();
                return;
            }

            if (elapsed > ObserveSeconds * 0.6f && !_sweeping)
            {
                _sweeping = true;
                _manager.StopFlythrough();
                _manager.enabled = false;
                _w($"阶段2：已停用 Pegasus，开始全圈扫描（{SweepSteps} 步）");
            }

            if (_sweeping)
            {
                if (_sweepStep < SweepSteps)
                {
                    if (Time.unscaledTime < _sweepDwellUntil)
                        return;

                    float pct = (float)_sweepStep / SweepSteps;
                    _manager.CalculateTargetAtDistance(
                        pct * _manager.m_totalDistance, out Vector3 p, out Quaternion r, out _);
                    _camera.transform.position = p;
                    _camera.transform.rotation = r;
                    _sweepStep++;
                    _sweepDwellUntil = Time.unscaledTime + SweepDwellSeconds;
                }
                else if (!_sweepDone)
                {
                    _sweepDone = true;
                    _w($"阶段2 完成：触达 {_loadedSeen.Count} 个 chunk，当前驻留 {CountLoaded()}");
                }
            }
        }

        private int CountLoaded()
        {
            int loaded = 0;
            if (_chunks == null)
                return 0;

            foreach (var c in _chunks)
            {
                if (c == null || !c.IsLoaded())
                    continue;
                loaded++;
                _loadedSeen.Add(c.ScenePath);
            }
            return loaded;
        }

        private void TestManualToggle()
        {
            var controller = _camera.GetComponent<FishDemo.Gameplay.PegasusTourController>();
            if (controller == null)
            {
                _w("WARN：相机上没有 PegasusTourController，跳过切换验证");
                return;
            }

            const System.Reflection.BindingFlags Flags =
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
            var enterManual = typeof(FishDemo.Gameplay.PegasusTourController).GetMethod("EnterManualMode", Flags);
            var enterAuto = typeof(FishDemo.Gameplay.PegasusTourController).GetMethod("EnterAutoMode", Flags);
            if (enterManual == null || enterAuto == null)
            {
                _w("WARN：找不到 EnterManualMode/EnterAutoMode");
                return;
            }

            enterManual.Invoke(controller, null);
            _manualEntered = controller.IsManual
                             && _manager.m_currentState == Pegasus.PegasusConstants.FlythroughState.Paused;
            _w($"切手动：IsManual={controller.IsManual}，Pegasus={_manager.m_currentState}");

            enterAuto.Invoke(controller, null);
            _autoRestored = !controller.IsManual
                            && _manager.m_currentState == Pegasus.PegasusConstants.FlythroughState.Started;
            _w($"切回自动：IsManual={controller.IsManual}，Pegasus={_manager.m_currentState}");
        }

        private void Report()
        {
            _w("");
            _w("===== 观测结果 =====");
            _w($"采样帧数 {_sampleCount}，累计位移 {_movedDistance:F1}m");
            _w($"相机半径范围 [{_minRadius:F0}, {_maxRadius:F0}]m（设定 1100m）");
            _w($"相机 Y 范围 [{_minY:F1}, {_maxY:F1}]");
            _w($"已加载 chunk 数 [{_minLoadedChunks}, {_maxLoadedChunks}]，" +
               $"整个周期触达 {_loadedSeen.Count} 个不同 chunk");

            bool driven = _movedDistance > 100f;
            bool onRoute = _minRadius > 1000f && _maxRadius < 1200f;
            bool streaming = _loadedSeen.Count > _maxLoadedChunks + 4;
            bool heightOk = _minY > 200f && _maxY < 500f;
            bool toggleOk = _manualEntered && _autoRestored;

            _w("");
            _w($"[1] Pegasus 驱动相机（位移>100m）     : {(driven ? "PASS" : "FAIL")} ({_movedDistance:F1}m)");
            _w($"[2] 沿环形路线（半径 1000~1200m）     : {(onRoute ? "PASS" : "FAIL")} [{_minRadius:F0},{_maxRadius:F0}]");
            _w($"[3] SECTR 动态加载（触达 ≫ 同时驻留） : {(streaming ? "PASS" : "FAIL")} " +
               $"触达 {_loadedSeen.Count}/36 / 驻留上限 {_maxLoadedChunks}");
            _w($"[4] 高度贴地（200~500m）              : {(heightOk ? "PASS" : "FAIL")} [{_minY:F1},{_maxY:F1}]");
            _w($"[5] F 键切换自动/手动                 : {(toggleOk ? "PASS" : "FAIL")}");
            _w("    注：[1][2][4] 取自阶段1（Pegasus 真实驱动），[3] 取自阶段2（全圈扫描）");

            bool all = driven && onRoute && streaming && heightOk && toggleOk;
            _w("");
            _w(all ? "==== 结论：PASS ====" : "==== 结论：FAIL ====");
            _w("");
            _w("触达的 chunk：");
            foreach (var p in _loadedSeen.OrderBy(x => x))
                _w("  " + p);

            Flush(_sb);

            EditorApplication.isPlaying = false;
            gameObject.AddComponent<QuitAfterFrames>().Init();
        }
    }

    private sealed class QuitAfterFrames : MonoBehaviour
    {
        private int _frames;
        public void Init() { hideFlags = HideFlags.DontSave; }

        private void Update()
        {
            if (++_frames < 30) return;
            EditorApplication.Exit(0);
        }
    }

    private static void Flush(StringBuilder sb)
    {
        Directory.CreateDirectory("Logs");
        File.WriteAllText(ResultPath, sb.ToString());
    }
}
