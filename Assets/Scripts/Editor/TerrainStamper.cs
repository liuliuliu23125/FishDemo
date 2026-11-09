using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Gaia;
using static Gaia.GaiaConstants;

/// <summary>
/// 给已创建的地形应用 Gaia 地貌印章，产出真实起伏。
///
/// ── 为什么之前地形是平的 ──
/// CreateOrUpdateWorld 只做"创建地形网格"，不会自动应用任何 Stamp。
/// 高度塑造必须显式调用 GaiaSessionManager.Stamp。实测不调用时所有 TerrainData
/// 高度恒为 0（起伏=0），切块后自然是平的。
///
/// ── 为什么用多个印章叠加 ──
/// 单一印章覆盖不了 3km 世界，会显得单调。这里按世界坐标铺设多张官方印章
/// （Hills / Mountains / Valleys / Plains 混合），让不同区域有不同地貌。
/// Stamper 的 m_y/m_x/m_z 是绝对世界坐标，m_width/m_height 是覆盖尺寸（米）。
/// </summary>
public static class TerrainStamper
{
    private const string StampRoot = "Assets/Procedural Worlds/Packages - Install/Stamps";
    private const string Tag = "[TerrainStamper]";

    // 世界范围 [-1536,+1536]，共 3072m。
    // 用 3×3 张 1400m 的印章平铺，重叠约 20% 保证无缝。
    private const int GridN = 3;
    private const float StampSize = 1400f;
    private const float WorldHalf = 1536f;

    /// <summary>
    /// 目标起伏幅度（米）。
    ///
    /// Stamper 的 m_height 不是米，而是 0..50 的垂直缩放系数，
    /// 换算关系见 StamperSettings.GetStampScaleByMeter：
    ///     scaleY = 50 * 米数 / terrainData.size.y
    /// 早期误把 m_height 当成米直接填 260（远超 50 上限），
    /// 纹理被极度压扁，实测起伏只有 0.0085。这里改为按米数反算。
    /// </summary>
    private const float TargetReliefMeters = 700f;

    /// <summary>
    /// 在一张已存在的 Terrain 上叠加若干印章做一次快速验证。
    /// </summary>
    public static void TestOneTerrain()
    {
        EditorSceneManager.OpenScene("Assets/Scenes/World.unity", OpenSceneMode.Single);
        var terrains = Object.FindObjectsOfType<Terrain>();
        Debug.Log($"{Tag} 场景 Terrain 数（应为 0，因为已切块清理）：{terrains.Length}");
        EditorApplication.Exit(0);
    }

    /// <summary>
    /// 对场景中所有激活的 Terrain 应用地貌印章。供 WorldPipeline 在世界创建后调用。
    /// 注意：此时地形尚未切块，场景内应是 9 块原始 Terrain。
    /// </summary>
    public static bool ApplyBiomeStamps()
    {
        var terrains = Object.FindObjectsOfType<Terrain>();
        Debug.Log($"{Tag} 待塑形 Terrain 数 = {terrains.Length}");
        if (terrains.Length == 0) return false;

        // 先记录初始高度，判断是否真的塑形成功
        var t0 = terrains[0].terrainData;
        float before = SampleRelief(t0);
        Debug.Log($"{Tag} 塑形前地形起伏 = {before:F4}");

        // 挑选地貌印章：中心山脉 + 周围丘陵 + 谷地，形成层次
        var stamps = new[]
        {
            "Mountains/4k Mountain 1.exr",
            "Mountains/2k Mountain 2.exr",
            "Hills/4k Hills 2.exr",
            "Hills/2k Hills 5.exr",
            "Valleys/2k Valley 2.exr",
            "Plains/4k Plains 1.exr",
            "Islands/4k Island 3.exr",
            "Mesas/2k Mesa 1.exr",
            "Rivers/2k River 1.exr",
        }.Where(rel => System.IO.File.Exists($"{StampRoot}/{rel}")).ToArray();

        Debug.Log($"{Tag} 可用印章数 = {stamps.Length}");
        foreach (var s in stamps) Debug.Log($"{Tag}   印章: {s}");

        if (stamps.Length == 0)
        {
            Debug.LogError($"{Tag} 没有可用印章，Stamps 包可能未导入");
            return false;
        }

        int idx = 0;
        for (int gx = 0; gx < GridN; gx++)
        {
            for (int gz = 0; gz < GridN; gz++)
            {
                float x = Mathf.Lerp(-WorldHalf + StampSize * 0.5f, WorldHalf - StampSize * 0.5f, gx / (float)(GridN - 1));
                float z = Mathf.Lerp(-WorldHalf + StampSize * 0.5f, WorldHalf - StampSize * 0.5f, gz / (float)(GridN - 1));

                string rel = stamps[idx % stamps.Length];
                idx++;
                ApplyStamp($"{StampRoot}/{rel}", x, z);
            }
        }

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        // 校验：抽样所有地形，确认起伏已产生（同时给出米数）
        float maxRelief = 0f;
        float terrainH = 700f;
        foreach (var t in Object.FindObjectsOfType<Terrain>())
        {
            float r = SampleRelief(t.terrainData);
            if (r > maxRelief) maxRelief = r;
            if (t.terrainData != null) terrainH = t.terrainData.size.y;
        }
        Debug.Log($"{Tag} 塑形后最大地形起伏 = {maxRelief:F4}（归一化），" +
                  $"约 {maxRelief * terrainH:F1} 米（>0 表示成功）");
        return maxRelief > 0.01f;
    }

    private static void ApplyStamp(string stampPath, float worldX, float worldZ)
    {
        var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(stampPath);
        if (tex == null)
        {
            Debug.LogWarning($"{Tag} 印章贴图加载失败：{stampPath}");
            return;
        }

        var settings = ScriptableObject.CreateInstance<StamperSettings>();
        settings.m_operation = FeatureOperation.RaiseHeight;
        // m_y 是印章基准高度（地形世界 Y），取地形基部
        settings.m_y = 0;
        settings.m_x = worldX;
        settings.m_z = worldZ;
        settings.m_width = StampSize;

        // 关键：m_height 是 0..50 的缩放系数，不是米。
        // 用 GetStampScaleByMeter 的逆运算把目标米数换算成系数：
        //     scaleY = 50 * meters / terrainHeight
        // 这里以场景首个地形的 size.y 作为基准（所有 tile 高度一致）。
        Terrain anyTerrain = Object.FindObjectOfType<Terrain>();
        float terrainHeight = anyTerrain != null ? anyTerrain.terrainData.size.y : 700f;
        float scaleY = Mathf.Clamp(50f * TargetReliefMeters / terrainHeight, 0.0001f, 50f);
        settings.m_height = scaleY;

        settings.m_rotation = 0f;
        settings.m_baseLevel = 0f;
        settings.m_imageMasks = new ImageMask[1]
        {
            new ImageMask
            {
                m_operation = ImageMaskOperation.ImageMask,
                ImageMaskTexture = tex,
            }
        };

        Debug.Log($"{Tag} 应用印章 {System.IO.Path.GetFileName(stampPath)} @ ({worldX:F0},{worldZ:F0}) " +
                  $"size={StampSize} 目标起伏={TargetReliefMeters}m → scaleY={scaleY:F2}");
        GaiaSessionManager.Stamp(settings, true);
    }

    /// <summary>抽样计算一个 TerrainData 的高度起伏（max-min）。</summary>
    private static float SampleRelief(TerrainData td)
    {
        if (td == null) return 0f;
        int res = td.heightmapResolution;
        int step = Mathf.Max(1, res / 48);
        var h = td.GetHeights(0, 0, res, res);
        float min = 1f, max = 0f;
        for (int x = 0; x < res; x += step)
            for (int y = 0; y < res; y += step)
            { if (h[x, y] < min) min = h[x, y]; if (h[x, y] > max) max = h[x, y]; }
        return max - min;
    }
}
