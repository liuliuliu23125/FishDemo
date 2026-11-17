using System;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using FishDemo.Gameplay;
using FishDemo.Rendering;

/// <summary>
/// World 场景的流式集成验收（不需要截图）：
///   1. Play Mode 下 `GrassBRGManager` 进入流式模式，而不是启动时读全部 36 块；
///   2. `SectrGrassStreamer` 能从 SECTR 的加载状态驱动草驻留；
///   3. 驻留量受 SECTR 范围与 24 块上限约束，明显小于全量 429 万。
///
/// 依赖 SECTR 在 Play Mode 里自动加载相机附近的 chunk（与 LakeOrbitVerify 同一前提）。
///
/// 用法：Unity.exe -batchmode -projectPath E:\FishDemo \
///   -executeMethod GrassWorldStreamingVerify.Run -logFile Logs/Grass/world-streaming.log
/// </summary>
[InitializeOnLoad]
public static class GrassWorldStreamingVerify
{
    private const string Tag = "[GrassWorldStreaming]";
    private const string ResultPath = "Logs/Grass/world-streaming.txt";
    private const string WorldScenePath = "Assets/Scenes/World.unity";
    private const string PendingKey = "FishDemo.GrassWorldStreaming.Pending";

    static GrassWorldStreamingVerify() => EditorApplication.update += Poll;

    public static void Run()
    {
        EditorSceneManager.OpenScene(WorldScenePath, OpenSceneMode.Single);
        SessionState.SetBool(PendingKey, true);
        EditorApplication.isPlaying = true;
    }

    private static void Poll()
    {
        if (!SessionState.GetBool(PendingKey, false) || !EditorApplication.isPlaying)
            return;
        SessionState.SetBool(PendingKey, false);
        EditorApplication.update -= Poll;

        new GameObject("~GrassWorldStreamingDriver").AddComponent<Driver>();
    }

    private sealed class Driver : MonoBehaviour
    {
        private readonly StringBuilder _sb = new StringBuilder();
        private GrassBRGManager _mgr;
        private float _start;
        private bool _started;
        private bool _done;

        private void W(string s)
        {
            Debug.Log($"{Tag} {s}");
            _sb.AppendLine(s);
        }

        private void Update()
        {
            if (_done) return;
            if (!_started)
            {
                _started = true;
                _start = Time.unscaledTime;
                _mgr = FindObjectOfType<GrassBRGManager>();
                // 巡游 manager 会抢相机，影响 SECTR loader 的落点。
                foreach (var m in FindObjectsOfType<Pegasus.PegasusManager>())
                    m.enabled = false;
                W($"管理器={(_mgr != null ? _mgr.name : "null")}");
            }

            if (_mgr == null)
            {
                Finish(false, "World 里找不到 GrassBRGManager");
                return;
            }

            bool ready = _mgr.IsStreaming && _mgr.StreamingChunkCount > 0 && _mgr.InstanceCount > 0;
            float waited = Time.unscaledTime - _start;
            if (!ready && waited < 40f)
                return;

            bool streamMode = _mgr.IsStreaming;
            bool hasData = _mgr.StreamingChunkCount > 0 && _mgr.InstanceCount > 0;
            bool bounded = _mgr.StreamingChunkCount <= SectrGrassStreamer.MaxResidentChunks;
            // 全量是 429 万；这里要求明显更低，证明没有在启动时一次读全。
            bool notFull = _mgr.InstanceCount < 4_000_000;

            W($"流式模式={streamMode}，驻留 chunk={_mgr.StreamingChunkCount}，" +
              $"实例={_mgr.InstanceCount:N0}，等待 {waited:F1}s（上限 {SectrGrassStreamer.MaxResidentChunks}）");
            W($"断言：流式 {(streamMode ? "PASS" : "FAIL")}；" +
              $"有数据 {(hasData ? "PASS" : "FAIL")}；" +
              $"不超上限 {(bounded ? "PASS" : "FAIL")}；" +
              $"非全量 {(notFull ? "PASS" : "FAIL")}");

            bool ok = streamMode && hasData && bounded && notFull;
            Finish(ok, ok ? null : "流式集成断言未通过");
        }

        private void Finish(bool ok, string reason)
        {
            if (_done) return;
            _done = true;
            if (reason != null) W(reason);
            _sb.AppendLine("");
            _sb.AppendLine(ok ? "==== 结论：PASS ====" : "==== 结论：FAIL ====");
            Directory.CreateDirectory(Path.GetDirectoryName(ResultPath));
            File.WriteAllText(ResultPath, _sb.ToString());
            gameObject.AddComponent<QuitAfter>().Init(ok ? 0 : 2);
            enabled = false;
        }
    }

    private sealed class QuitAfter : MonoBehaviour
    {
        private int _code;
        private int _f;

        public void Init(int code) => _code = code;

        private void Update()
        {
            if (++_f < 3) return;
            EditorApplication.Exit(_code);
        }
    }
}
