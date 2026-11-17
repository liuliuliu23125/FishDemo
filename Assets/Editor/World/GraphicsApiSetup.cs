using System;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

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

        PlayerSettings.SetUseDefaultGraphicsAPIs(target, false);
        PlayerSettings.SetGraphicsAPIs(target, new[] { GraphicsDeviceType.Vulkan });

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
