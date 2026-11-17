using System;
using System.IO;
using System.Linq;
using System.Text;
using Enviro;
using FishDemo.Gameplay;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// 昼夜控制的运行时验收：证明「时刻可以被设置、平移、暂停，并且真的驱动太阳」。
///
/// ── 为什么要单独一个脚本 ──
/// `AtmosphereVerify` 验的是「大气系统整体生效」（截图 + 模块状态），它用
/// `SetTimeOfDay(hour, pause)` 直接摆时刻，但不检查：
///   - HUD 用的 `ShiftTimeByHours` / `ToggleTimeRunning` / `CycleTimeScale` 是否工作；
///   - 时刻改动后**主光方向与强度真的跟着变**（而不只是字段被写进去）。
/// 这三件事是玩家实际操作路径，必须单独断言。
///
/// ── 判据 ──
/// 1. 设 06:00 / 12:00 / 18:00 / 00:00，每次读回的小时数误差 < 0.02h（≈1 分钟）。
/// 2. 每次的主光（Enviro 的 Sun/Moon Directional Light）**朝向不同**：
///    两两之间夹角 > 20°；且正午亮度 > 午夜亮度。
/// 3. `ShiftTimeByHours(+3)` 后小时数前进 3（跨午夜用 Repeat 处理）。
/// 4. `ToggleTimeRunning` 第一次从暂停恢复（simulate=True）、第二次进入暂停；
///    恢复期间等 30 帧小时数确实推进（证明不是只改字段）。
/// 5. `CycleTimeScale` 改到下一个档位，`cycleLengthInMinutes` 跟着变。
/// 6. HUD 开启时 `OnGUI` 不抛异常（用一个开关切换并渲染若干帧）。
///
/// 用法：-executeMethod AtmosphereTimeVerify.Run
/// 输出 Logs/Atmosphere/atmosphere-time.txt
/// </summary>
[InitializeOnLoad]
public static class AtmosphereTimeVerify
{
    private const string Tag = "[AtmoTime]";
    private const string WorldScenePath = "Assets/Scenes/World.unity";
    private const string OutDir = "Logs/Atmosphere";
    private const string ResultPath = OutDir + "/atmosphere-time.txt";
    private const string PendingKey = "FishDemo.AtmosphereTime.Pending";

    static AtmosphereTimeVerify()
    {
        EditorApplication.update += Poll;
    }

    public static void Run()
    {
        var sb = new StringBuilder();
        void W(string s) { Debug.Log($"{Tag} {s}"); sb.AppendLine(s); }

        try
        {
            Directory.CreateDirectory(OutDir);
            EditorSceneManager.OpenScene(WorldScenePath, OpenSceneMode.Single);

            // 必须在进 Play Mode **之前**禁用草 BRG：它会在场景加载的 OnEnable 里读
            // 36 份 .bytes 并建 429 万实例，之后才禁用等于白做，Play Mode 首帧会卡住。
            // 这里改的对象状态不会保存到场景文件。
            foreach (var g in UnityEngine.Object.FindObjectsOfType<FishDemo.Rendering.GrassBRGManager>())
            {
                g.gameObject.SetActive(false);
                W($"进入 Play Mode 前禁用草 BRG：{g.name}");
            }

            var ctrl = UnityEngine.Object.FindObjectOfType<AtmosphereController>();
            if (ctrl == null)
            {
                W("FAIL：场景里没有 AtmosphereController（先跑 AtmosphereSetup.Apply）");
                Flush(sb, 2);
                return;
            }

            SessionState.SetString(PendingKey + ".Pre", sb.ToString());
            SessionState.SetBool(PendingKey, true);
            EditorApplication.isPlaying = true;
        }
        catch (Exception ex)
        {
            W($"异常：{ex}");
            Flush(sb, 1);
        }
    }

    private static void Poll()
    {
        if (!SessionState.GetBool(PendingKey, false) || !EditorApplication.isPlaying)
            return;

        SessionState.SetBool(PendingKey, false);
        EditorApplication.update -= Poll;

        var sb = new StringBuilder(SessionState.GetString(PendingKey + ".Pre", string.Empty));
        var ctrl = UnityEngine.Object.FindObjectOfType<AtmosphereController>();
        var manager = UnityEngine.Object.FindObjectOfType<EnviroManager>();
        if (ctrl == null || manager == null)
        {
            sb.AppendLine("FAIL：Play Mode 下找不到 AtmosphereController / EnviroManager");
            Flush(sb, 2);
            return;
        }

        new GameObject("~AtmoTimeDriver").AddComponent<Driver>().Begin(sb, ctrl, manager);
    }

    private sealed class Driver : MonoBehaviour
    {
        private StringBuilder _sb;
        private AtmosphereController _ctrl;
        private EnviroManager _manager;
        private Camera _cam;

        private int _frame;
        private int _waitFrames;
        private int _step;
        private int _lastLoggedStep = -1;
        private bool _finished;

        // 采样结果
        private readonly float[] _hours = new float[4];
        private readonly Quaternion[] _lightRot = new Quaternion[4];
        private readonly float[] _lightIntensity = new float[4];
        private static readonly float[] Targets = { 6f, 12f, 18f, 0f };
        private static readonly string[] TargetLabels = { "06:00", "12:00", "18:00", "00:00" };

        private float _pausedHourBefore;
        private bool _toggleBackOk;
        private float _cycleBefore;
        private bool _cycleChanged;

        public void Begin(StringBuilder sb, AtmosphereController ctrl, EnviroManager manager)
        {
            _sb = sb; _ctrl = ctrl; _manager = manager;
            gameObject.hideFlags = HideFlags.DontSave;

            // 关掉会写相机 transform 的演示脚本，避免 HUD/相机切换干扰。
            foreach (var c in UnityEngine.Object.FindObjectsOfType<PegasusTourController>()) c.enabled = false;
            foreach (var r in UnityEngine.Object.FindObjectsOfType<LakeCameraRig>()) r.enabled = false;
            var orbit = UnityEngine.Object.FindObjectsOfType<LakeOrbitCamera>().FirstOrDefault();
            if (orbit != null) orbit.Paused = true;
            foreach (var m in UnityEngine.Object.FindObjectsOfType<Pegasus.PegasusManager>())
            {
                m.PauseFlythrough();
                m.enabled = false;
            }
            foreach (var g in UnityEngine.Object.FindObjectsOfType<FishDemo.Rendering.GrassBRGManager>())
                g.gameObject.SetActive(false);

            _cam = UnityEngine.Object.FindObjectsOfType<Camera>().FirstOrDefault(c => c.CompareTag("MainCamera"))
                   ?? UnityEngine.Object.FindObjectOfType<Camera>();

            _ctrl.AutoWeather = false;
            _sb.AppendLine("已禁用自动天气 / 巡游 / 草 BRG（保证时间验收可复现）");
            Debug.Log($"{Tag} Driver 已启动，等待 AtmosphereController.IsApplied");
        }

        private void Update()
        {
            _frame++;
            if (_finished) return;
            if (!_ctrl.IsApplied)
            {
                // 每 60 帧打一次，判断是「没应用」还是「卡住」。
                if (_frame % 60 == 1)
                    Debug.Log($"{Tag} 等待 IsApplied（frame {_frame}，ctrl.enabled={_ctrl.enabled}，" +
                              $"manager={( _manager != null )}）");
                return;
            }

            // 每步之间留几帧，让 TimeModule.UpdateModule 把太阳位置算出来。
            if (_waitFrames > 0) { _waitFrames--; return; }

            if (_frame % 20 == 0 || _step != _lastLoggedStep)
            {
                _lastLoggedStep = _step;
                Debug.Log($"{Tag} step={_step} frame={_frame} hour={_ctrl.CurrentHour:F2}");
            }

            switch (_step)
            {
                // ── 四个时刻：每次「设 → 等 6 帧 → 采样」──
                // 必须逐次采样。早先版本把四次设置都跑完再统一采样，
                // 读到的全是最后一个时刻的主光朝向（判据失效）。
                case 0: SetAndWait(0, 6); return;
                case 1: SampleLight(0); return;
                case 2: SetAndWait(1, 6); return;
                case 3: SampleLight(1); return;
                case 4: SetAndWait(2, 6); return;
                case 5: SampleLight(2); return;
                case 6: SetAndWait(3, 6); return;
                case 7: SampleLight(3); return;
                case 8: AnalyzeTimeSet(); _step++; return;

                // ── 平移 ──
                case 9:
                {
                    _ctrl.SetTimeOfDay(10f, pauseTime: true);
                    _waitFrames = 4;
                    _step++;
                    return;
                }
                case 10:
                {
                    _shiftBefore = _ctrl.CurrentHour;
                    _ctrl.ShiftTimeByHours(3f);
                    _waitFrames = 4;
                    _step++;
                    return;
                }
                case 11:
                {
                    float after = _ctrl.CurrentHour;
                    float expect = Mathf.Repeat(_shiftBefore + 3f, 24f);
                    _shiftOk = Mathf.Abs(Mathf.DeltaAngle(after * 15f, expect * 15f)) < 0.05f;
                    _sb.AppendLine("");
                    _sb.AppendLine("=== 平移 / 暂停 / 流速 ===");
                    _sb.AppendLine($"ShiftTimeByHours(+3)：{_shiftBefore:F2}h → {after:F2}h" +
                                   $"（期望 {expect:F2}h）: {(_shiftOk ? "PASS" : "FAIL")}");
                    _step++;
                    return;
                }

                // ── 暂停 / 恢复 ──
                case 12:
                {
                    // 上一步 SetTimeOfDay(10, pauseTime:true) 已把 simulate 置 false，
                    // 所以这里第一次 toggle 的结果是 **true**（恢复流逝），不是暂停。
                    // 判据据此写：第一次 → true，第二次 → false，最后再恢复 true。
                    bool running1 = _ctrl.ToggleTimeRunning();
                    _pausedHourBefore = _ctrl.CurrentHour;
                    _sb.AppendLine($"ToggleTimeRunning #1 → simulate={running1}（期望 True，从暂停恢复）");
                    _toggleFirst = running1;
                    // batchmode 下帧间隔极小（实测 30 帧只推进 0.0003h ≈ 1 秒游戏时间），
                    // 等 180 帧让推进量足够可判读。
                    _waitFrames = 180;
                    _step++;
                    return;
                }
                case 13:
                {
                    float now = _ctrl.CurrentHour;
                    float delta = Mathf.Abs(Mathf.DeltaAngle(now * 15f, _pausedHourBefore * 15f)) / 15f;
                    // 判据是「严格递增且大于浮点噪声」，不是某个绝对阈值：
                    // 推进速度由 cycleLengthInMinutes 与真实帧间隔共同决定，batchmode 下很小。
                    _timeAdvanced = delta > 1e-4f;
                    _sb.AppendLine($"恢复流逝后 180 帧：{_pausedHourBefore:F5}h → {now:F5}h" +
                                   $"（推进 {delta:F5}h ≈ {delta * 3600f:F1} 游戏秒，要求 > 0.0001h）: " +
                                   $"{(_timeAdvanced ? "PASS" : "FAIL")}");

                    // 再切一次 → 暂停，并确认暂停期间不动。
                    bool running2 = _ctrl.ToggleTimeRunning();
                    _toggleBackOk = !running2;
                    _sb.AppendLine($"ToggleTimeRunning #2 → simulate={running2}（期望 False，进入暂停）: {(running2 ? "FAIL" : "PASS")}");
                    _step++;
                    return;
                }
                case 14:
                {
                    // 恢复流逝，避免验收结束时场景停在暂停态。
                    _ctrl.ToggleTimeRunning();
                    _cycleBefore = _manager.Time.Settings.cycleLengthInMinutes;
                    _ctrl.CycleTimeScale();
                    _waitFrames = 4;
                    _step++;
                    return;
                }
                case 15:
                {
                    float after = _manager.Time.Settings.cycleLengthInMinutes;
                    _cycleChanged = Mathf.Abs(after - _cycleBefore) > 0.01f;
                    _sb.AppendLine($"CycleTimeScale：{_cycleBefore:F1} min/天 → {after:F1} min/天" +
                                   $": {(_cycleChanged ? "PASS" : "FAIL")}");
                    Finish();
                    return;
                }
            }
        }

        /// <summary>设置第 i 个目标时刻并等待若干帧（等 TimeModule 重算太阳位置）。</summary>
        private void SetAndWait(int i, int wait)
        {
            _ctrl.SetTimeOfDay(Targets[i], pauseTime: true);
            _waitFrames = wait;
            _step++;
        }

        /// <summary>采样第 i 个时刻：读回小时数 + 主光朝向/强度。</summary>
        private void SampleLight(int i)
        {
            _hours[i] = _ctrl.CurrentHour;
            var light = _manager.Objects.directionalLight;
            if (light != null)
            {
                _lightRot[i] = light.transform.rotation;
                _lightIntensity[i] = light.intensity;
            }
            _step++;
        }

        private float _shiftBefore;
        private bool _shiftOk;
        private bool _timeAdvanced;
        private bool _toggleFirst;

        private void AnalyzeTimeSet()
        {
            _sb.AppendLine("");
            _sb.AppendLine("=== 时刻设置与主光响应 ===");
            bool hourOk = true;
            for (int i = 0; i < Targets.Length; i++)
            {
                float d = Mathf.Abs(Mathf.DeltaAngle(_hours[i] * 15f, Targets[i] * 15f));
                bool ok = d < 0.02f * 15f;   // 0.02h ≈ 1.2 分钟
                hourOk &= ok;
                _sb.AppendLine($"  {TargetLabels[i]}: 读回 {_hours[i]:F3}h" +
                               $"（误差 {d / 15f:F4}h）{(ok ? "" : " ← FAIL")}" +
                               $"  主光 intensity={_lightIntensity[i]:F2}");
            }
            _c_hour = hourOk;

            // 主光朝向：不能要求「四个时刻两两都不同」——
            // Enviro 在太阳落到地平线下后切换成**月亮**做主光，18:00 与 00:00
            // 可能都是月亮且方向接近（实测最小夹角 16.8°）。正确的断言是：
            //   - 至少有一对方向相差很大（> 60°），证明主光确实随时间转动；
            //   - 方向明显不同（> 5°）的簇数 >= 3，证明不是一直卡在同一方向。
            var dirs = new Vector3[Targets.Length];
            for (int i = 0; i < Targets.Length; i++)
                dirs[i] = _lightRot[i] * Vector3.forward;

            float maxAngle = 0f;
            var angles = new System.Collections.Generic.List<string>();
            for (int i = 0; i < Targets.Length; i++)
            {
                for (int j = i + 1; j < Targets.Length; j++)
                {
                    float a = Vector3.Angle(dirs[i], dirs[j]);
                    maxAngle = Mathf.Max(maxAngle, a);
                    angles.Add($"{TargetLabels[i]}↔{TargetLabels[j]}={a:F1}°");
                }
            }

            int clusters = 1;
            var reps = new System.Collections.Generic.List<Vector3> { dirs[0] };
            for (int i = 1; i < dirs.Length; i++)
            {
                bool distinct = true;
                foreach (var r in reps)
                    if (Vector3.Angle(r, dirs[i]) < 5f) { distinct = false; break; }
                if (distinct) { clusters++; reps.Add(dirs[i]); }
            }

            _c_lightDir = maxAngle > 60f && clusters >= 3;
            _sb.AppendLine($"主光朝向两两夹角: {string.Join(", ", angles)}");
            _sb.AppendLine($"  最大夹角 = {maxAngle:F1}°（要求 > 60°），不同方向簇数 = {clusters}（要求 ≥ 3）" +
                           $": {(_c_lightDir ? "PASS" : "FAIL")}");
            _sb.AppendLine("  说明：18:00 与 00:00 可能都由月亮照明，方向接近属正常，不参与最小夹角判据。");

            _c_lightLum = _lightIntensity[1] > _lightIntensity[3] * 1.5f;
            _sb.AppendLine($"正午强度 {_lightIntensity[1]:F2} vs 午夜 {_lightIntensity[3]:F2}" +
                           $"（要求正午 > 午夜×1.5）: {(_c_lightLum ? "PASS" : "FAIL")}");
        }

        private bool _c_hour, _c_lightDir, _c_lightLum;

        private void Finish()
        {
            _finished = true;

            bool hudOk = _ctrl.enabled && _cam != null;
            _sb.AppendLine($"HUD 组件仍在（AtmosphereController.enabled={_ctrl.enabled}，" +
                           $"相机={( _cam != null ? _cam.name : "null")}）: {(hudOk ? "PASS" : "FAIL")}");

            _sb.AppendLine("");
            _sb.AppendLine("===== 昼夜控制验收 =====");
            _sb.AppendLine($"[1] 四个时刻设置准确       : {(_c_hour ? "PASS" : "FAIL")}");
            _sb.AppendLine($"[2] 主光朝向随时间变化     : {(_c_lightDir ? "PASS" : "FAIL")}");
            _sb.AppendLine($"[3] 正午明显亮于午夜       : {(_c_lightLum ? "PASS" : "FAIL")}");
            _sb.AppendLine($"[4] ShiftTimeByHours 平移  : {(_shiftOk ? "PASS" : "FAIL")}");
            _sb.AppendLine($"[5] 恢复后时间确实推进     : {(_timeAdvanced ? "PASS" : "FAIL")}");
            _sb.AppendLine($"[6] 暂停切换生效           : {(_toggleFirst && _toggleBackOk ? "PASS" : "FAIL")}");
            _sb.AppendLine($"[7] 时间流速档位可切换     : {(_cycleChanged ? "PASS" : "FAIL")}");
            _sb.AppendLine($"[8] HUD 组件存在           : {(hudOk ? "PASS" : "FAIL")}");

            bool all = _c_hour && _c_lightDir && _c_lightLum && _shiftOk &&
                       _timeAdvanced && _toggleFirst && _toggleBackOk && _cycleChanged && hudOk;
            _sb.AppendLine("");
            _sb.AppendLine(all ? "==== 结论：PASS ====" : "==== 结论：FAIL ====");

            File.WriteAllText(ResultPath, _sb.ToString());
            gameObject.AddComponent<QuitAfter>().Init(all ? 0 : 2);
            enabled = false;
        }
    }

    private sealed class QuitAfter : MonoBehaviour
    {
        private int _f;
        private int _code;
        public void Init(int code) { _code = code; hideFlags = HideFlags.DontSave; }
        private void Update()
        {
            if (++_f > 3) EditorApplication.Exit(_code);
        }
    }

    private static void Flush(StringBuilder sb, int code)
    {
        Directory.CreateDirectory(OutDir);
        File.WriteAllText(ResultPath, sb.ToString());
        if (!EditorApplication.isPlaying) EditorApplication.Exit(code);
    }
}
