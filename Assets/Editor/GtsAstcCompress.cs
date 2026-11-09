using System;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using ProceduralWorlds.GTS;

/// <summary>
/// 把 GTS 纹理数组改为 ASTC 压缩并重建（移动端体积优化）。
///
/// ── 为什么必须用带 alpha 的 ASTC ──
/// albedo 数组的 **alpha 通道被当作高度混合权重**使用：
///   GTS_Functions.hlsl:591  float masks[] = { albedoA.a, albedoB.a, albedoC.a, albedoD.a };
///   GTS_Functions.hlsl:592  float4 splatHeight = float4(...) * splatControl.rgba;
/// 若用 ASTC_RGB_*（无 alpha），高度混合会失效，材质过渡变成硬边。
///
/// ── 格式陷阱（实测）──
/// GTSCompressionFormat 的枚举值与 Unity TextureFormat 的映射并不都带 alpha：
///   GTS ASTC_4x4 (48) → TextureFormat.ASTC_4x4      ✓ 带 alpha
///   GTS ASTC_6x6 (50) → TextureFormat.ASTC_RGB_6x6  ✗ 无 alpha（实测确认）
///   GTS ASTC_8x8 (51) → TextureFormat.ASTC_8x8      ✓ 带 alpha
/// 因此本脚本显式使用 ASTC_8x8，避开 6x6。
///
/// ── 体积对照（2048×2048×8 层，未含 mip）──
///   RGBA32   128 MB  ← 当前
///   ASTC_4x4  32 MB
///   ASTC_8x8   8 MB  ← 采用
///
/// ── 前提 ──
/// 需要 gtsLayers 有数据（由 GTSTerrain.ApplyProfile 填充）。
/// 本脚本先打开一个 chunk 场景、挂 GTSTerrain 跑 ApplyProfile，
/// 再改设置并重建数组。之后所有 chunk 复用同一套数组（数组是全局共享的）。
///
/// 用法：-executeMethod GtsAstcCompress.Run
/// 结果写 Logs/Gaia/gts-astc.txt
/// </summary>
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

            // gtsLayers 需要被填充，否则 CreateTextureArrays 直接返回
            if (profile.gtsLayers.Count == 0)
            {
                W("gtsLayers 为空，先从一个 chunk 场景加载地形层…");
                if (!PopulateLayers(profile, W)) { W("填充 gtsLayers 失败"); Flush(sb, 2); return; }
            }
            W($"gtsLayers 数 = {profile.gtsLayers.Count}");

            // ── 关键设置 ──
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

            // 校验实际格式
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

    /// <summary>
    /// 打开一个 chunk 场景、给地形挂 GTSTerrain 并 ApplyProfile，
    /// 让 profile.gtsLayers 被 RefreshTerrainLayers 填充。
    /// 只用于拿层数据，不改场景（不保存）。
    /// </summary>
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
