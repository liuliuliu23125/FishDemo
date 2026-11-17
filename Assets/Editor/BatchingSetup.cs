using System;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;

public static class BatchingSetup
{
    private const string OutPath = "Logs/Gaia/batching.txt";

    private static readonly MethodInfo s_GetBatching =
        typeof(PlayerSettings).GetMethod("GetBatchingForPlatform", BindingFlags.Static | BindingFlags.NonPublic);
    private static readonly MethodInfo s_SetBatching =
        typeof(PlayerSettings).GetMethod("SetBatchingForPlatform", BindingFlags.Static | BindingFlags.NonPublic);

    public static void Report() => Run("report");
    public static void FixAndroid() => Run("android");
    public static void FixAll() => Run("all");

    private static void Run(string mode)
    {
        var sb = new StringBuilder();
        void W(string s) { Debug.Log($"[Batching] {s}"); sb.AppendLine(s); }

        try
        {
            if (mode is "android" or "all")
            {
                SetBatching(BuildTarget.Android, 0, 0);
                W("Android: StaticBatching=0, DynamicBatching=0");
            }

            if (mode is "all")
            {
                SetBatching(BuildTarget.StandaloneWindows64, 0, 0);
                W("StandaloneWindows64: StaticBatching=0, DynamicBatching=0");
            }

            if (mode != "report")
            {
                AssetDatabase.SaveAssets();
            }

            ReportPlatform(W, BuildTarget.Android);
            ReportPlatform(W, BuildTarget.StandaloneWindows64);

            Directory.CreateDirectory(Path.GetDirectoryName(OutPath));
            File.WriteAllText(OutPath, sb.ToString());
            W("==== 结论：PASS ====");
            EditorApplication.Exit(0);
        }
        catch (Exception ex)
        {
            W($"异常：{ex}");
            W("==== 结论：FAIL ====");
            Directory.CreateDirectory(Path.GetDirectoryName(OutPath));
            File.WriteAllText(OutPath, sb.ToString());
            EditorApplication.Exit(1);
        }
    }

    private static void SetBatching(BuildTarget target, int staticBatching, int dynamicBatching)
    {
        if (s_SetBatching == null)
            throw new MissingMethodException("PlayerSettings.SetBatchingForPlatform(BindingFlags.NonPublic)");
        s_SetBatching.Invoke(null, new object[] { target, staticBatching, dynamicBatching });
    }

    private static void ReportPlatform(Action<string> W, BuildTarget target)
    {
        if (s_GetBatching == null)
            throw new MissingMethodException("PlayerSettings.GetBatchingForPlatform(BindingFlags.NonPublic)");

        var args = new object[] { target, 0, 0 };
        s_GetBatching.Invoke(null, args);
        W($"{target}: StaticBatching={args[1]}, DynamicBatching={args[2]}");
    }
}
