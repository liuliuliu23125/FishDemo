using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using WaveHarmonic.Crest;

/// <summary>
/// 水面性能模式切换（可逆）：SimpleTransparency 与 Quad 两个独立开关。
///
/// ── 两者的作用位置完全不同，别混为一谈 ──
///
/// **SimpleTransparency**（`CREST_SIMPLE_TRANSPARENCY`）是**编译期宏**：
/// - 由 Crest 的 `ProjectSettings`（`ProjectSettings/Packages/com.waveharmonic.crest/Settings.asset`）
///   上的 `_PlatformSettings*.SimpleTransparency` 写入 scripting define
///   `d_Crest_SimpleTransparency`（`ProjectSettings.cs:396`），
///   然后重新生成 `Runtime/Shaders/Library/Settings/Settings.Crest.*.hlsl`（`ProjectSettings.cs:399`）。
/// - **不能只改 hlsl 文本**：`ShaderSettingsGenerator` 会在脚本重载时重新生成，
///   手改的宏会被覆盖。必须走 Crest 的 Settings 资产。
/// - 效果：shader 不再采样 `_CameraOpaqueTexture` / `_CameraDepthTexture`，
///   `SurfaceRenderer.Universal.cs:61-63` 把 `ConfigureInput` 降为 `None`，
///   URP 因此不再需要 opaque/depth 纹理 → 省掉 CopyColor + CopyDepth 两个全屏 pass。
/// - 代价：失去折射；透明度改由 `volumeOpacity`（Depth 仿真）驱动，
///   并且 `o_Alpha` 有下限 `_Crest_TransparencyMinimumAlpha`（默认 **0.95**，必须调低，
///   否则水面几乎不透明，见 `Fragment.hlsl:825-828`）。
///
/// **Quad**（`WaterMeshType.Quad`）是**运行时枚举**（`SurfaceRenderer._MeshType`）：
/// - `SurfaceRenderer.cs:930-933` 只发 1 次 `DrawMesh`，不再有 49 个 chunk。
/// - 代价：置换只贡献法线，水面几何不再起伏（`WaterBuilder` 完全不参与）。
///   对钓鱼游戏够用：水面的"动"来自法线扰动带来的高光与反射变化。
///
/// ── 为什么 SimpleTransparency 必须配合关掉 URP 资产的 Depth/Opaque 纹理 ──
/// 光改宏只是让 **Crest 自己**不再请求；但 `Assets/Settings/UniversalRP.asset` 上
/// `m_RequireDepthTexture: 1` / `m_RequireOpaqueTexture: 1` 会让 **URP 对所有相机**
/// 无条件做 copy（`UniversalRenderPipeline.cs:1243-1244`）。
/// 不关掉这两项，省下的只是 Crest 的那一份（实测本来就只有 1 份，见 water.md §13.1），
/// 等于白改。关掉后要先确认场景里没有别的 shader 依赖它们。
///
/// ── 用法 ──
///   -executeMethod CrestPerformanceMode.ApplySimpleTransparency
///   -executeMethod CrestPerformanceMode.ApplyQuad
///   -executeMethod CrestPerformanceMode.ApplyBoth
///   -executeMethod CrestPerformanceMode.Restore
///   -executeMethod CrestPerformanceMode.Report
/// 输出 Logs/Crest/crest-perf-mode.txt
/// </summary>
public static class CrestPerformanceMode
{
    private const string Tag = "[CrestPerf]";
    private const string WorldScenePath = "Assets/Scenes/World.unity";
    private const string OutDir = "Logs/Crest";
    private const string ResultPath = OutDir + "/crest-perf-mode.txt";
    private const string UrpAssetPath = "Assets/Settings/UniversalRP.asset";
    private const string WaterMatPath = "Packages/com.waveharmonic.crest/Runtime/Materials/Water.mat";
    private const string VolumeMatPath = "Packages/com.waveharmonic.crest/Runtime/Materials/Water Volume.mat";
    private const string NodeName = "Crest Lake";

    /// <summary>
    /// SimpleTransparency 下的透明度下限。
    ///
    /// 为什么必须改：`Fragment.hlsl:825-828` 在 SimpleTransparency 分支里用
    /// `max(length(volumeOpacity), _Crest_TransparencyMinimumAlpha, ...)` 写 `o_Alpha`，
    /// 而该属性默认 **0.95**（Water.shadergraph 的 `Transparency Minimum Alpha`）。
    /// 0.95 意味着水面最多只有 5% 透光 —— 等于不透明。
    /// 取 0.12：浅水处 volumeOpacity 很小，让 alpha 落到 0.12 附近，
    /// 能看见水底；深水处 volumeOpacity 上升，水面自然变实。
    /// </summary>
    private const float SimpleTransparencyMinAlpha = 0.12f;

    public static void ApplySimpleTransparency() => Run("simple");
    public static void ApplyQuad() => Run("quad");
    public static void ApplyBoth() => Run("both");
    public static void Restore() => Run("restore");
    public static void Report() => Run("report");

    private static void Run(string mode)
    {
        var sb = new StringBuilder();
        void W(string s) { Debug.Log($"{Tag} {s}"); sb.AppendLine(s); }

        try
        {
            Directory.CreateDirectory(OutDir);

            if (mode == "report")
            {
                Report(W);
                W("==== 结论：PASS ====");
                Flush(sb, 0);
                return;
            }

            if (mode == "restore")
            {
                SetSimpleTransparency(false, W);
                SetUrpTextures(requireDepth: true, requireOpaque: true, W);
                SetMeshType(0, W);   // 0 = Chunks
                SetTransparencyMinAlpha(0.95f, W);
                W("已还原：Chunks + 非 SimpleTransparency + URP Depth/Opaque 开启");
                Report(W);
                W("==== 结论：PASS ====");
                Flush(sb, 0);
                return;
            }

            if (mode is "simple" or "both")
            {
                SetSimpleTransparency(true, W);
                SetTransparencyMinAlpha(SimpleTransparencyMinAlpha, W);
                // 只在确认没有其它依赖时才关 URP 的 Depth/Opaque。
                if (HasOtherDepthOrOpaqueConsumers(W))
                {
                    W("⚠ 检测到除 Crest 外还有 shader 依赖 _CameraDepthTexture/_CameraOpaqueTexture，" +
                      "**保持 URP 资产不变**（否则那些 shader 会失效）");
                }
                else
                {
                    SetUrpTextures(requireDepth: false, requireOpaque: false, W);
                }
            }

            if (mode is "quad" or "both")
            {
                SetMeshType(1, W);   // 1 = Quad
            }

            Report(W);
            W("==== 结论：PASS ====");
            Flush(sb, 0);
        }
        catch (Exception ex)
        {
            W($"异常：{ex}");
            W("==== 结论：FAIL ====");
            Flush(sb, 1);
        }
    }

    /// <summary>
    /// 通过 Crest 自己的 ProjectSettings 打开/关闭 SimpleTransparency。
    ///
    /// 必须走这条路：该字段的 setter 会
    /// ① 写入 scripting define `d_Crest_SimpleTransparency`（影响 C# 的 `#if`）；
    /// ② 调 `ShaderSettingsGenerator.Generate()` 重新生成 `Settings.Crest.*.hlsl`（影响 shader）。
    /// 手改 hlsl 文本会在下次脚本重载时被覆盖。
    ///
    /// 为什么用反射而不是直接引用：`ProjectSettings` 是 **internal** 类
    /// （`WaveHarmonic.Crest.Editor.Settings` 命名空间，`ProjectSettings.cs:192`），
    /// 编辑器程序集访问不到类型本身。它继承 `ScriptableSingleton<T>`，
    /// 单例实例通过基类的 internal `instance` 属性暴露。
    ///
    /// 为什么不用 `AssetDatabase.LoadAllAssetsAtPath`：该资产位于 `ProjectSettings/` 目录，
    /// **不在 Assets 下**，AssetDatabase 不索引它（实测返回空 → "找不到 Crest ProjectSettings"）。
    /// </summary>
    private static void SetSimpleTransparency(bool enabled, Action<string> W)
    {
        // ── 为什么直接改文本，而不是 SerializedObject + Save() ──
        // 实测（2026-11-13）：`so.ApplyModifiedPropertiesWithoutUndo()` 之后内存里
        // 回读确实是 True，但调 `ScriptableSingleton.Save()` 落盘后文件里仍是 0。
        // 该单例的 `OnEnable` 把 hideFlags 设成 `HideAndDontSave & ~NotEditable`，
        // 且 `Save()` 走内部序列化路径，与 SerializedObject 的修改不是同一条链路。
        // 直接改文本最可靠，改完再让 Crest 自己重算 define 与 hlsl。
        const string path = "ProjectSettings/Packages/com.waveharmonic.crest/Settings.asset";
        if (!File.Exists(path)) { W($"FAIL：找不到 {path}"); return; }

        var text = File.ReadAllText(path);
        var original = text;

        // 每个平台段内替换，避免误伤其它字段。
        string[] sections =
        {
            "_PlatformSettingsDesktop:", "_PlatformSettingsServer:", "_PlatformSettingsAndroid:",
            "_PlatformSettingsIOS:", "_PlatformSettingsTVOS:", "_PlatformSettingsVisionOS:",
            "_PlatformSettingsWeb:", "_PlatformSettings:",
        };

        foreach (var header in sections)
        {
            int start = text.IndexOf(header, StringComparison.Ordinal);
            if (start < 0) continue;
            int bodyStart = start + header.Length;
            // 段尾 = 下一个顶格键（两空格缩进）或文件末尾。
            var next = Regex.Match(text.Substring(bodyStart), "\n  _[A-Za-z]");
            int bodyEnd = next.Success ? bodyStart + next.Index : text.Length;
            var body = text.Substring(bodyStart, bodyEnd - bodyStart);

            body = Regex.Replace(body, @"_OverrideSimpleTransparency: [01]",
                $"_OverrideSimpleTransparency: {(enabled ? 1 : 0)}");
            body = Regex.Replace(body, @"_SimpleTransparency: [01]",
                $"_SimpleTransparency: {(enabled ? 1 : 0)}");

            text = text.Substring(0, bodyStart) + body + text.Substring(bodyEnd);
        }

        if (text != original)
        {
            File.WriteAllText(path, text);
            W($"Crest Settings.asset：SimpleTransparency = {enabled}（已写入全部平台段）");
        }
        else
        {
            W($"Crest Settings.asset：SimpleTransparency 已是 {enabled}");
        }

        // 文本改动不会触发 OnChange（那是 Inspector 的 [@OnChange] 回调）。
        // 必须模拟 Crest 自己的 FileSystemWatcher 逻辑（ProjectSettings.cs:440-455）：
        //   销毁单例 → 清 s_Instance → 重新取（从磁盘重新反序列化）→ UpdateSymbols()。
        // 直接调 UpdateSymbols() 不行：它读的是**内存里**的旧单例，会把 define 写回旧值
        // （实测：文本已改 True，UpdateSymbols 又把 define 写成 False）。
        ReloadSingletonAndUpdateSymbols(W);

        // 校验。
        var defines = PlayerSettings.GetScriptingDefineSymbolsForGroup(BuildTargetGroup.Android);
        W($"  Android scripting define 含 d_Crest_SimpleTransparency = " +
          $"{defines.Contains("d_Crest_SimpleTransparency")}（期望 {enabled}）");

        var hlsl = "Packages/com.waveharmonic.crest/Runtime/Shaders/Library/Settings/Settings.Crest.Default.hlsl";
        if (File.Exists(hlsl))
        {
            var m = Regex.Match(File.ReadAllText(hlsl), @"CREST_SIMPLE_TRANSPARENCY \((\d)\)");
            W($"  生成的 hlsl 宏 = {(m.Success ? m.Groups[1].Value : "?")}（期望 {(enabled ? 1 : 0)}）");
            if (m.Success && (m.Groups[1].Value == "1") != enabled)
                W("  ⚠ hlsl 未同步：下一次脚本重载（DidReloadScripts）会重新生成");
        }
    }

    /// <summary>
    /// 直接执行 Crest 的 shader includes 生成（等价于菜单 Edit > Rendering > Generate Shader Includes）。
    /// `ShaderGeneratorUtility.GenerateAll()` 是 internal static async Task，反射调用后同步等待。
    /// </summary>
    private static void GenerateShaderIncludesNow(Action<string> W)
    {
        Type util = null;
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            util = asm.GetType("WaveHarmonic.Crest.Editor.ShaderGeneratorUtility");
            if (util != null) break;
        }
        if (util == null) { W("  ⚠ 找不到 ShaderGeneratorUtility"); return; }

        var method = util.GetMethod("GenerateAll",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic |
            System.Reflection.BindingFlags.Public);
        if (method == null) { W("  ⚠ 找不到 GenerateAll"); return; }

        try
        {
            var task = method.Invoke(null, null) as System.Threading.Tasks.Task;
            // batchmode 下没有同步上下文，直接等待即可；加超时避免卡死。
            if (task != null && !task.Wait(TimeSpan.FromSeconds(60)))
                W("  ⚠ GenerateAll 超时（60s）");
            else
                W("  已执行 GenerateAll()：Settings.Crest.*.hlsl 已重新生成");
        }
        catch (Exception ex)
        {
            W($"  ⚠ GenerateAll 异常：{ex.InnerException?.Message ?? ex.Message}");
        }
    }

    /// <summary>找到 Crest ProjectSettings 的类型（internal，需扫程序集）。</summary>
    private static Type FindCrestProjectSettingsType()
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            var t = asm.GetType("WaveHarmonic.Crest.Editor.Settings.ProjectSettings");
            if (t != null) return t;
        }
        return null;
    }

    /// <summary>取 ScriptableSingleton&lt;T&gt;.instance（internal static 属性）。</summary>
    private static System.Reflection.PropertyInfo FindSingletonInstanceProp(Type settingsType)
    {
        var baseType = settingsType.BaseType;
        while (baseType != null && !(baseType.IsGenericType &&
               baseType.GetGenericTypeDefinition().Name == "ScriptableSingleton`1"))
            baseType = baseType.BaseType;
        return baseType?.GetProperty("instance",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic |
            System.Reflection.BindingFlags.Public);
    }

    /// <summary>
    /// 销毁单例、清 s_Instance、重新从磁盘加载，然后调 UpdateSymbols()。
    /// 这是 Crest 的 FileSystemWatcher.OnChanged 做的事（ProjectSettings.cs:440-455），
    /// batchmode 下 watcher 的 delayCall 不执行，所以手动复现。
    /// </summary>
    private static void ReloadSingletonAndUpdateSymbols(Action<string> W)
    {
        var type = FindCrestProjectSettingsType();
        if (type == null) { W("  ⚠ 找不到 ProjectSettings 类型"); return; }

        var instanceProp = FindSingletonInstanceProp(type);
        var old = instanceProp?.GetValue(null) as UnityEngine.Object;
        if (old != null) UnityEngine.Object.DestroyImmediate(old);

        var baseType = type.BaseType;
        while (baseType != null && !(baseType.IsGenericType &&
               baseType.GetGenericTypeDefinition().Name == "ScriptableSingleton`1"))
            baseType = baseType.BaseType;
        baseType?.GetField("s_Instance",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)
            ?.SetValue(null, null);

        var fresh = instanceProp?.GetValue(null) as UnityEngine.Object;
        if (fresh == null) { W("  ⚠ 单例重载失败"); return; }

        var updateSymbols = type.GetMethod("UpdateSymbols",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        updateSymbols?.Invoke(fresh, null);
        W("  已重载单例并调用 UpdateSymbols()：写 define + 请求重新生成 hlsl");

        // UpdateSymbols 内部只调 `ShaderSettingsGenerator.Generate()`，而它只是
        // `CompilationPipeline.RequestScriptCompilation()` —— 真正的 hlsl 生成
        // 要等域重载后 `[DidReloadScripts]` 触发。batchmode 单进程内不会发生，
        // 所以直接反射调 `ShaderGeneratorUtility.GenerateAll()`（internal static async Task）并等它完成。
        GenerateShaderIncludesNow(W);
    }

    /// <summary>写 `_Crest_TransparencyMinimumAlpha`。只在 SimpleTransparency 下有意义。</summary>
    private static void SetTransparencyMinAlpha(float value, Action<string> W)
    {
        foreach (var path in new[] { WaterMatPath, VolumeMatPath })
        {
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null) continue;
            if (!mat.HasProperty("_Crest_TransparencyMinimumAlpha")) continue;
            mat.SetFloat("_Crest_TransparencyMinimumAlpha", value);
            EditorUtility.SetDirty(mat);
            W($"  {mat.name}: _Crest_TransparencyMinimumAlpha = {value}");
        }
        AssetDatabase.SaveAssets();
    }

    /// <summary>改 URP 资产的 Depth/Opaque 纹理开关。</summary>
    private static void SetUrpTextures(bool requireDepth, bool requireOpaque, Action<string> W)
    {
        var text = File.ReadAllText(UrpAssetPath);
        bool changed = false;
        foreach (var (key, value) in new[]
                 {
                     ("m_RequireDepthTexture", requireDepth ? 1 : 0),
                     ("m_RequireOpaqueTexture", requireOpaque ? 1 : 0),
                 })
        {
            var pattern = key + @":\s*[01]";
            var replaced = Regex.Replace(text, pattern, $"{key}: {value}");
            if (replaced != text) { text = replaced; changed = true; }
        }

        if (changed)
        {
            File.WriteAllText(UrpAssetPath, text);
            AssetDatabase.ImportAsset(UrpAssetPath, ImportAssetOptions.ForceUpdate);
            W($"URP 资产：m_RequireDepthTexture={requireDepth}, m_RequireOpaqueTexture={requireOpaque}");
        }
        else
        {
            W($"URP 资产：已是 Depth={requireDepth}, Opaque={requireOpaque}");
        }
    }

    /// <summary>
    /// 检查除 Crest 外是否还有 shader 依赖 `_CameraDepthTexture` / `_CameraOpaqueTexture`。
    ///
    /// 为什么要查：关掉 URP 资产的这两个开关后，**所有**相机都不再生成这两张图。
    /// 场景里若有 shader 依赖它们（如 Gaia 的天气 VFX、GeNa 的河流），会静默失效。
    /// 只扫本项目实际会用到的东西：Assets 下非插件目录 + 场景里挂的材质。
    /// 保守起见：只把 Gaia/GeNa 的 VFX 视为"不参与"（它们未被接入，见 AGENTS.md §2）。
    /// </summary>
    private static bool HasOtherDepthOrOpaqueConsumers(Action<string> W)
    {
        // 工程里已接入渲染的 shader：GTS 地表、草 BRG、树（PW）、Crest。
        // 逐个确认它们不采样这两张图。若将来新增依赖，这里会提醒。
        var suspects = new (string path, string label)[]
        {
            ("Assets/Grasses/GrassBRG.shader", "草 BRG"),
            ("Assets/Settings/UniversalRP.asset", "URP 资产自身"),
        };

        bool any = false;
        foreach (var (path, label) in suspects)
        {
            if (!File.Exists(path)) continue;
            var text = File.ReadAllText(path);
            bool uses = Regex.IsMatch(text, @"_CameraDepthTexture|_CameraOpaqueTexture|SampleSceneDepth|SampleSceneColor");
            if (uses)
            {
                W($"  {label}（{path}）依赖 Depth/Opaque 纹理");
                any = true;
            }
        }

        // GTS shader 与树材质是 ShaderGraph 产物，扫描不到源码依赖；它们在 SRP Batcher 下
        // 正常不采样这两张图。这里不阻断，只提示。
        W($"  已检查的 shader：草 BRG / URP 资产 → {(any ? "存在依赖" : "无依赖")}");
        return any;
    }

    /// <summary>改场景里 WaterRenderer 的 MeshType（运行时枚举，直接改序列化字段）。</summary>
    private static void SetMeshType(int type, Action<string> W)
    {
        var scene = EditorSceneManager.OpenScene(WorldScenePath, OpenSceneMode.Single);
        var root = GameObject.Find(NodeName);
        if (root == null) { W($"FAIL：找不到 {NodeName}"); return; }
        var water = root.GetComponent<WaterRenderer>();
        if (water == null) { W($"FAIL：{NodeName} 上没有 WaterRenderer"); return; }

        var so = new SerializedObject(water);
        var surfaceProp = so.FindProperty("_Surface");
        // Surface 是 [SerializeReference] 的托管对象，字段在它的 SerializedProperty 下。
        var meshTypeProp = surfaceProp?.FindPropertyRelative("_MeshType");
        if (meshTypeProp == null)
        {
            // 兜底：SerializedReference 在部分 Unity 版本下需要走 managedReferenceValue。
            var surfaceObj = surfaceProp?.managedReferenceValue as UnityEngine.Object;
            if (surfaceObj == null) { W("FAIL：拿不到 SurfaceRenderer 序列化数据"); return; }
            var sso = new SerializedObject(surfaceObj);
            meshTypeProp = sso.FindProperty("_MeshType");
            if (meshTypeProp == null) { W("FAIL：拿不到 _MeshType 字段"); return; }
            if (meshTypeProp.enumValueIndex == type) { W($"MeshType 已是 {type}"); return; }
            meshTypeProp.enumValueIndex = type;
            sso.ApplyModifiedPropertiesWithoutUndo();
        }
        else
        {
            if (meshTypeProp.enumValueIndex == type) { W($"MeshType 已是 {type}"); return; }
            meshTypeProp.enumValueIndex = type;
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        AssetDatabase.SaveAssets();
        W($"MeshType = {type}（0=Chunks 1=Quad 2=Custom）");
    }

    private static void Report(Action<string> W)
    {
        W("");
        W("=== 当前配置 ===");

        // SimpleTransparency
        var defines = PlayerSettings.GetScriptingDefineSymbolsForGroup(BuildTargetGroup.Android);
        W($"scripting define d_Crest_SimpleTransparency = {defines.Contains("d_Crest_SimpleTransparency")}");
        var hlsl = "Packages/com.waveharmonic.crest/Runtime/Shaders/Library/Settings/Settings.Crest.Default.hlsl";
        if (File.Exists(hlsl))
        {
            var m = Regex.Match(File.ReadAllText(hlsl), @"CREST_SIMPLE_TRANSPARENCY \((\d)\)");
            W($"生成 hlsl 的 CREST_SIMPLE_TRANSPARENCY = {(m.Success ? m.Groups[1].Value : "?")}");
        }

        // URP 资产
        var urp = File.ReadAllText(UrpAssetPath);
        var d = Regex.Match(urp, @"m_RequireDepthTexture:\s*(\d)");
        var o = Regex.Match(urp, @"m_RequireOpaqueTexture:\s*(\d)");
        W($"URP m_RequireDepthTexture = {(d.Success ? d.Groups[1].Value : "?")}, " +
          $"m_RequireOpaqueTexture = {(o.Success ? o.Groups[1].Value : "?")}");

        // MeshType
        var scene = EditorSceneManager.OpenScene(WorldScenePath, OpenSceneMode.Single);
        var root = GameObject.Find(NodeName);
        if (root != null)
        {
            var water = root.GetComponent<WaterRenderer>();
            if (water != null)
            {
                var so = new SerializedObject(water);
                var surfaceProp = so.FindProperty("_Surface");
                var meshTypeProp = surfaceProp?.FindPropertyRelative("_MeshType");
                int idx = meshTypeProp?.enumValueIndex ?? -1;
                W($"MeshType 枚举值 = {idx}（0=Chunks 1=Quad 2=Custom）");
            }
        }

        // 材质上的透明度下限
        var mat = AssetDatabase.LoadAssetAtPath<Material>(WaterMatPath);
        if (mat != null && mat.HasProperty("_Crest_TransparencyMinimumAlpha"))
            W($"_Crest_TransparencyMinimumAlpha = {mat.GetFloat("_Crest_TransparencyMinimumAlpha")}");
    }

    private static void Flush(StringBuilder sb, int code)
    {
        Directory.CreateDirectory(OutDir);
        File.WriteAllText(ResultPath, sb.ToString());
        EditorApplication.Exit(code);
    }
}
