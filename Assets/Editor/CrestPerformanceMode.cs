using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using WaveHarmonic.Crest;

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

    internal const float SimpleTransparencyMinAlpha = 0.12f;

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
                SetMeshType(0, W);
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
                SetMeshType(1, W);
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

    private static void SetSimpleTransparency(bool enabled, Action<string> W)
    {
        const string path = "ProjectSettings/Packages/com.waveharmonic.crest/Settings.asset";
        if (!File.Exists(path)) { W($"FAIL：找不到 {path}"); return; }

        var text = File.ReadAllText(path);
        var original = text;

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

        ReloadSingletonAndUpdateSymbols(W);

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

    private static Type FindCrestProjectSettingsType()
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            var t = asm.GetType("WaveHarmonic.Crest.Editor.Settings.ProjectSettings");
            if (t != null) return t;
        }
        return null;
    }

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

        GenerateShaderIncludesNow(W);
    }

    internal static void SetTransparencyMinAlpha(float value, Action<string> W)
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

    internal static void SetUrpTextures(bool requireDepth, bool requireOpaque, Action<string> W)
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

    internal static bool HasOtherDepthOrOpaqueConsumers(Action<string> W)
    {
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

        W($"  已检查的 shader：草 BRG / URP 资产 → {(any ? "存在依赖" : "无依赖")}");
        return any;
    }

    internal static int GetMeshType(WaterRenderer water)
    {
        if (water == null) return -1;

        var so = new SerializedObject(water);
        var surfaceProp = so.FindProperty("_Surface");
        var meshTypeProp = surfaceProp?.FindPropertyRelative("_MeshType");
        if (meshTypeProp != null) return meshTypeProp.enumValueIndex;

        var surfaceObj = surfaceProp?.managedReferenceValue as UnityEngine.Object;
        if (surfaceObj == null) return -1;
        return new SerializedObject(surfaceObj).FindProperty("_MeshType")?.enumValueIndex ?? -1;
    }

    internal static void SetMeshType(WaterRenderer water, int type, Action<string> W)
    {
        if (water == null) { W("FAIL：WaterRenderer 为空"); return; }

        var so = new SerializedObject(water);
        var surfaceProp = so.FindProperty("_Surface");
        var meshTypeProp = surfaceProp?.FindPropertyRelative("_MeshType");
        if (meshTypeProp == null)
        {
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

        W($"MeshType = {type}（0=Chunks 1=Quad 2=Custom）");
    }

    private static void SetMeshType(int type, Action<string> W)
    {
        var scene = EditorSceneManager.OpenScene(WorldScenePath, OpenSceneMode.Single);
        var root = GameObject.Find(NodeName);
        if (root == null) { W($"FAIL：找不到 {NodeName}"); return; }
        var water = root.GetComponent<WaterRenderer>();
        if (water == null) { W($"FAIL：{NodeName} 上没有 WaterRenderer"); return; }

        SetMeshType(water, type, W);

        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        AssetDatabase.SaveAssets();
    }

    internal static bool IsSimpleTransparencyEnabled()
    {
        var defines = PlayerSettings.GetScriptingDefineSymbolsForGroup(BuildTargetGroup.Android);
        var hasDefine = defines.Contains("d_Crest_SimpleTransparency");
        if (!hasDefine) return false;

        const string hlsl = "Packages/com.waveharmonic.crest/Runtime/Shaders/Library/Settings/Settings.Crest.Default.hlsl";
        if (!File.Exists(hlsl)) return false;
        var m = Regex.Match(File.ReadAllText(hlsl), @"CREST_SIMPLE_TRANSPARENCY \((\d)\)");
        return m.Success && m.Groups[1].Value == "1";
    }

    private static void Report(Action<string> W)
    {
        W("");
        W("=== 当前配置 ===");

        var defines = PlayerSettings.GetScriptingDefineSymbolsForGroup(BuildTargetGroup.Android);
        W($"scripting define d_Crest_SimpleTransparency = {defines.Contains("d_Crest_SimpleTransparency")}");
        var hlsl = "Packages/com.waveharmonic.crest/Runtime/Shaders/Library/Settings/Settings.Crest.Default.hlsl";
        if (File.Exists(hlsl))
        {
            var m = Regex.Match(File.ReadAllText(hlsl), @"CREST_SIMPLE_TRANSPARENCY \((\d)\)");
            W($"生成 hlsl 的 CREST_SIMPLE_TRANSPARENCY = {(m.Success ? m.Groups[1].Value : "?")}");
        }

        var urp = File.ReadAllText(UrpAssetPath);
        var d = Regex.Match(urp, @"m_RequireDepthTexture:\s*(\d)");
        var o = Regex.Match(urp, @"m_RequireOpaqueTexture:\s*(\d)");
        W($"URP m_RequireDepthTexture = {(d.Success ? d.Groups[1].Value : "?")}, " +
          $"m_RequireOpaqueTexture = {(o.Success ? o.Groups[1].Value : "?")}");

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
