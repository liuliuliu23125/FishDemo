using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using FishDemo.Rendering;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// 验证草流式的运行时增删路径：BRG 的 AddBatch/RemoveBatch、剔除表重建、
/// buffer 池复用，以及「驻留集合 = 当前加入的 chunk」。
///
/// 不打开 World 场景：直接 new 一个 GrassBRGManager，用真实 .bytes 驱动，
/// 避免把 SECTR 加载时序混进本项验证（那由 LakeOrbitVerify / PegasusTourVerify 覆盖）。
///
/// 渲染部分需要真实图形设备；`SystemInfo.graphicsDeviceType == Null` 时跳过，
/// 但增删与表重建仍必须通过。
///
/// 用法：Unity.exe -batchmode -projectPath E:\FishDemo \
///   -executeMethod GrassStreamingVerify.Run -logFile Logs/Grass/streaming.log
/// </summary>
[InitializeOnLoad]
public static class GrassStreamingVerify
{
    private const string Tag = "[GrassStreaming]";
    private const string ResultPath = "Logs/Grass/streaming.txt";
    private const string GrassDataDir = "Assets/Scenes/World/GrassData";
    private const string PendingKey = "FishDemo.GrassStreaming.Pending";

    static GrassStreamingVerify() => EditorApplication.update += Poll;

    public static void Run()
    {
        // 用空场景隔离：否则会打开上次的 World，场景里的 SECTR 加载/第二个
        // GrassBRGManager 会干扰本项断言。
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        SessionState.SetBool(PendingKey, true);
        EditorApplication.isPlaying = true;
    }

    private static void Poll()
    {
        if (!SessionState.GetBool(PendingKey, false) || !EditorApplication.isPlaying)
            return;
        SessionState.SetBool(PendingKey, false);
        EditorApplication.update -= Poll;

        var go = new GameObject("~GrassStreamingVerify");
        go.hideFlags = HideFlags.DontSave;
        go.AddComponent<Driver>();
    }

    private sealed class Driver : MonoBehaviour
    {
        private readonly StringBuilder _sb = new StringBuilder();
        private bool _done;
        private int _frame;
        private GrassBRGManager _mgr;
        private string[] _names;
        private int _step;
        private Camera _renderCam;
        private RenderTexture _renderRt;

        private void W(string s)
        {
            Debug.Log($"{Tag} {s}");
            _sb.AppendLine(s);
        }

        private void Update()
        {
            if (_done) return;
            _frame++;
            if (_frame < 5) return;
            if (!Application.isPlaying) return;

            try
            {
                switch (_step++)
                {
                    case 0: Prepare(); break;
                    case 1: AssertEmpty(); break;
                    case 2: AddFirst(); break;
                    case 3: AddSecond(); break;
                    case 4: RemoveFirst(); break;
                    case 5: ReAddFirst(); break;
                    case 6: BeginRenderIfPossible(); break;
                    case 7: ReadRenderResult(); break;
                    case 8: ClearAll(); break;
                    case 9: Finish(0); break;
                }
            }
            catch (Exception e)
            {
                W($"异常：{e}");
                Finish(2);
            }
        }

        private void Prepare()
        {
            string[] files = Directory.GetFiles(GrassDataDir, "*.bytes");
            if (files.Length < 2)
                throw new InvalidOperationException("草数据不足两块");

            _names = new[]
            {
                Path.GetFileNameWithoutExtension(files[0]),
                Path.GetFileNameWithoutExtension(files[1]),
            };

            // 标志必须在 AddComponent 之前置位，否则 OnEnable 已走全量读盘。
            GrassBRGManager.RequestStreaming();
            var go = new GameObject("~GrassStreamingMgr");
            go.hideFlags = HideFlags.DontSave;
            _mgr = go.AddComponent<GrassBRGManager>();
            _mgr.SetGrassDataDir(GrassDataDir);

            W($"管理器流式={_mgr.IsStreaming}，初始 chunk={_mgr.StreamingChunkCount}，" +
              $"实例={_mgr.InstanceCount}");
        }

        private void AssertEmpty()
        {
            bool ok = _mgr.IsStreaming && _mgr.StreamingChunkCount == 0 && _mgr.InstanceCount == 0;
            W($"空态（流式、0 chunk、0 实例）: {(ok ? "PASS" : "FAIL")}");
            if (!ok) throw new InvalidOperationException("空态不符合预期");
        }

        private void AddFirst()
        {
            bool added = _mgr.AddGrassChunk(_names[0]);
            bool ok = added && _mgr.StreamingChunkCount == 1 && _mgr.InstanceCount > 0;
            W($"加入 {_names[0]} → chunk={_mgr.StreamingChunkCount} 实例={_mgr.InstanceCount:N0} : " +
              $"{(ok ? "PASS" : "FAIL")}");
            if (!ok) throw new InvalidOperationException("加入首块失败");
        }

        private void AddSecond()
        {
            int first = _mgr.InstanceCount;
            bool added = _mgr.AddGrassChunk(_names[1]);
            bool ok = added && _mgr.StreamingChunkCount == 2 && _mgr.InstanceCount > first;
            W($"加入 {_names[1]} → chunk={_mgr.StreamingChunkCount} 实例={_mgr.InstanceCount:N0} " +
              $"(增加 {_mgr.InstanceCount - first:N0}) : {(ok ? "PASS" : "FAIL")}");
            if (!ok) throw new InvalidOperationException("加入第二块失败");
        }

        private void RemoveFirst()
        {
            int before = _mgr.InstanceCount;
            bool removed = _mgr.RemoveGrassChunk(_names[0]);
            bool ok = removed && _mgr.StreamingChunkCount == 1 && _mgr.InstanceCount < before;
            W($"移除 {_names[0]} → chunk={_mgr.StreamingChunkCount} 实例={_mgr.InstanceCount:N0} : " +
              $"{(ok ? "PASS" : "FAIL")}");
            if (!ok) throw new InvalidOperationException("移除失败");
        }

        private void ReAddFirst()
        {
            bool added = _mgr.AddGrassChunk(_names[0]);
            bool ok = added && _mgr.StreamingChunkCount == 2;
            W($"重新加入 {_names[0]} → chunk={_mgr.StreamingChunkCount} : {(ok ? "PASS" : "FAIL")}");
            if (!ok) throw new InvalidOperationException("重加失败");
        }

        private void BeginRenderIfPossible()
        {
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
            {
                W("无图形设备：跳过 OnPerformCulling / LastVisibleCount 断言（不算失败）");
                _step = 8;
                return;
            }

            var p = _mgr.GetInstancePosition(0);
            var camGo = new GameObject("~GrassStreamingCam");
            camGo.hideFlags = HideFlags.DontSave;
            _renderCam = camGo.AddComponent<Camera>();
            _renderCam.transform.position = new Vector3(p.x, p.y + 6f, p.z - 12f);
            _renderCam.transform.LookAt(p);
            _renderCam.fieldOfView = 60f;
            _renderCam.farClipPlane = 300f;

            _renderRt = RenderTexture.GetTemporary(256, 256, 24);
            _renderCam.targetTexture = _renderRt;
            // 第一次渲染只是建立剔除输出，Job 可能还没完成；
            // 结果留到下一帧读，避免和 Job 抢同一块 NativeArray。
            _renderCam.Render();
        }

        private void ReadRenderResult()
        {
            if (_renderCam == null)
                return;

            _renderCam.Render();
            int visible = _mgr.LastVisibleCount;
            bool ok = visible > 0;
            W($"渲染 256² 后 LastVisibleCount={visible} : {(ok ? "PASS" : "FAIL")}");

            _renderCam.targetTexture = null;
            RenderTexture.ReleaseTemporary(_renderRt);
            _renderRt = null;
            Destroy(_renderCam.gameObject);
            _renderCam = null;

            if (!ok) throw new InvalidOperationException("剔除后没有可见实例");
        }

        private void ClearAll()
        {
            _mgr.ClearGrassChunks();
            bool ok = _mgr.StreamingChunkCount == 0 && _mgr.InstanceCount == 0;
            W($"清空全部草（低内存路径）→ chunk={_mgr.StreamingChunkCount} 实例={_mgr.InstanceCount} : " +
              $"{(ok ? "PASS" : "FAIL")}");
            if (!ok) throw new InvalidOperationException("ClearGrassChunks 未清空");
        }

        private void Finish(int code)
        {
            if (_done) return;
            _done = true;

            bool pass = code == 0;
            _sb.AppendLine("");
            _sb.AppendLine(pass ? "==== 结论：PASS ====" : "==== 结论：FAIL ====");

            Directory.CreateDirectory(Path.GetDirectoryName(ResultPath));
            File.WriteAllText(ResultPath, _sb.ToString());

            if (_mgr != null)
                Destroy(_mgr.gameObject);

            gameObject.AddComponent<QuitAfter>().Init(pass ? 0 : code);
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
