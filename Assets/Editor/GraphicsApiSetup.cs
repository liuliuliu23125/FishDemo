using System;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// 把 Android 图形 API 改为「仅 Vulkan」，并关闭 Automatic。
///
/// ── 为什么要改 ──
/// BRG（BatchRendererGroup）的 shader 需要 `#pragma target 4.5` 且
/// `#pragma exclude_renderers gles`（依据 URP 14 的 BRGPicking.shader:31-32）。
/// 若 Android 仍可能 fallback 到 OpenGLES3，草在真机上会直接消失。
///
/// 现状（改前）：m_APIs = [Vulkan(21), OpenGLES3(11)]，m_Automatic = 1
///               → Vulkan 优先但允许回落到 GLES3
/// 目标（改后）：m_APIs = [Vulkan(21)]，m_Automatic = 0
///
/// ── 为什么用 PlayerSettings API 而不是改 YAML ──
/// m_APIs 是序列化的 int[]，字节序与打包方式（4 字节 little-endian per element）
/// 不适合手改；PlayerSettings.SetGraphicsAPIs 是官方入口，会同时维护
/// Automatic 标志与平台元数据。
///
/// 用法：-executeMethod GraphicsApiSetup.SetAndroidVulkanOnly
/// 结果写 Logs/Gaia/graphics-api.txt
/// </summary>
public static class GraphicsApiSetup
{
    private const string Tag = "[GraphicsAPI]";
    private const string ResultPath = "Logs/Gaia/graphics-api.txt";

    public static void SetAndroidVulkanOnly()
    {
        var sb = new StringBuilder();
        void W(string s) { Debug.Log($"{Tag} {s}"); sb.AppendLine(s); }

        var target = BuildTarget.Android;
        var before = PlayerSettings.GetGraphicsAPIs(target);
        bool beforeAuto = PlayerSettings.GetUseDefaultGraphicsAPIs(target);
        W($"改前：APIs = [{string.Join(", ", before)}]，Automatic = {beforeAuto}");

        // 目标：只保留 Vulkan
        PlayerSettings.SetUseDefaultGraphicsAPIs(target, false);
        PlayerSettings.SetGraphicsAPIs(target, new[] { GraphicsDeviceType.Vulkan });

        // 强制重新导入以让设置落盘
        AssetDatabase.SaveAssets();

        var after = PlayerSettings.GetGraphicsAPIs(target);
        bool afterAuto = PlayerSettings.GetUseDefaultGraphicsAPIs(target);
        W($"改后：APIs = [{string.Join(", ", after)}]，Automatic = {afterAuto}");

        bool ok = after.Length == 1 && after[0] == GraphicsDeviceType.Vulkan && !afterAuto;
        W("");
        W($"校验：仅 Vulkan = {after.Length == 1 && after[0] == GraphicsDeviceType.Vulkan}，" +
          $"Automatic 已关 = {!afterAuto}");
        W($"==== 结论：{(ok ? "PASS" : "FAIL")} ====");

        Directory.CreateDirectory(Path.GetDirectoryName(ResultPath));
        File.WriteAllText(ResultPath, sb.ToString());
        EditorApplication.Exit(ok ? 0 : 2);
    }

    /// <summary>只读检查，不改配置。</summary>
    public static void Report()
    {
        var sb = new StringBuilder();
        void W(string s) { Debug.Log($"{Tag} {s}"); sb.AppendLine(s); }
        foreach (BuildTarget t in new[] { BuildTarget.Android, BuildTarget.StandaloneWindows64 })
        {
            var apis = PlayerSettings.GetGraphicsAPIs(t);
            bool auto = PlayerSettings.GetUseDefaultGraphicsAPIs(t);
            W($"{t}: [{string.Join(", ", apis)}]，Automatic = {auto}");
        }
        Directory.CreateDirectory(Path.GetDirectoryName(ResultPath));
        File.WriteAllText(ResultPath, sb.ToString());
        EditorApplication.Exit(0);
    }
}
