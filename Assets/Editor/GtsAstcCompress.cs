using System;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using ProceduralWorlds.GTS;

public static class GtsAstcCompress
{
    private const string Tag = "[GTS-ASTC]";
    private const string ResultPath = "Logs/Gaia/gts-astc.txt";
    private const string ProfilePath = "Assets/Gaia User Data/GTS World Profile.asset";
    private const string ChunkDir = "Assets/Scenes/World/Chunks";
    private const string GtsDataDir = "Assets/Gaia User Data/GTS World Profile_GTSData";

    public static void Run()
    {
        var sb = new StringBuilder();
        void W(string s) { Debug.Log($"{Tag} {s}"); sb.AppendLine(s); }

        try
        {
            var profile = AssetDatabase.LoadAssetAtPath<GTSProfile>(ProfilePath);
            if (profile == null) { W("找不到 Profile"); Flush(sb, 1); return; }

            W($"当前设置：maxTextureSize={profile.textureArraySettings.maxTextureSize}, " +
              $"compressed={profile.textureArraySettings.compressed}, " +
              $"format={profile.textureArraySettings.compressionFormat}");
            W($"旧数组体积：albedo={SizeOf("Albedo Texture Array.asset"):F1}MB, " +
              $"normal={SizeOf("Normal Texture Array.asset"):F1}MB");

            if (profile.gtsLayers.Count == 0)
            {
                W("gtsLayers 为空，先从一个 chunk 场景加载地形层…");
                if (!PopulateLayers(profile, W)) { W("填充 gtsLayers 失败"); Flush(sb, 2); return; }
            }
            W($"gtsLayers 数 = {profile.gtsLayers.Count}");

            profile.textureArraySettings.compressed = true;
            profile.textureArraySettings.compressionFormat = GTSCompressionFormat.ASTC_8x8;
            profile.textureArraySettings.maxTextureSize = GTSTextureSize.Is2048MetersSq;
            EditorUtility.SetDirty(profile);
            AssetDatabase.SaveAssets();

            W($"新设置：compressed=true, format=ASTC_8x8(51), maxTextureSize=2048");
            W("重建纹理数组（较慢，需逐层压缩 8 层 × 2 个数组）…");

            profile.CreateTextureArrays();
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            W($"新数组体积：albedo={SizeOf("Albedo Texture Array.asset"):F1}MB, " +
              $"normal={SizeOf("Normal Texture Array.asset"):F1}MB");

            var albedo = profile.textureArraySettings.albedoArray;
            var normal = profile.textureArraySettings.normalArray;
            W($"albedo 数组：{(albedo != null ? albedo.format.ToString() : "null")}, " +
              $"尺寸={(albedo != null ? albedo.width + "x" + albedo.height + "x" + albedo.depth : "?")}");
            W($"normal 数组：{(normal != null ? normal.format.ToString() : "null")}, " +
              $"尺寸={(normal != null ? normal.width + "x" + normal.height + "x" + normal.depth : "?")}");

            bool ok = albedo != null && normal != null
                      && albedo.format.ToString().Contains("ASTC")
                      && normal.format.ToString().Contains("ASTC");
            W("");
            W($"==== 结论：{(ok ? "PASS" : "FAIL")} ====");
            Flush(sb, ok ? 0 : 2);
        }
        catch (Exception ex)
        {
            W($"异常：{ex}");
            W("==== 结论：FAIL ====");
            Flush(sb, 3);
        }
    }

    private static bool PopulateLayers(GTSProfile profile, Action<string> W)
    {
        string[] files = Directory.GetFiles(ChunkDir, "*162007*.unity");
        if (files.Length == 0) return false;
        Array.Sort(files);

        var scene = UnityEditor.SceneManagement.EditorSceneManager.OpenScene(
            files[0].Replace('\\', '/'), UnityEditor.SceneManagement.OpenSceneMode.Single);
        var terrain = UnityEngine.Object.FindObjectsOfType<Terrain>();
        if (terrain.Length == 0) return false;

        var t = terrain[0];
        var gts = t.GetComponent<GTSTerrain>();
        if (gts == null) gts = t.gameObject.AddComponent<GTSTerrain>();
        gts.profile = profile;
        gts.ApplyProfile();

        W($"  从 {Path.GetFileName(files[0])} 加载，层数={profile.gtsLayers.Count}");
        return profile.gtsLayers.Count > 0;
    }

    private static float SizeOf(string fileName)
    {
        string p = Path.Combine(GtsDataDir, fileName);
        return File.Exists(p) ? new FileInfo(p).Length / 1024f / 1024f : 0f;
    }

    private static void Flush(StringBuilder sb, int code)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ResultPath));
        File.WriteAllText(ResultPath, sb.ToString());
        EditorApplication.Exit(code);
    }
}
