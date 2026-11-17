using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Gaia;
using static Gaia.GaiaConstants;

public static class TerrainStamper
{
    private const string StampRoot = "Assets/Procedural Worlds/Packages - Install/Stamps";
    private const string Tag = "[TerrainStamper]";

    private const int GridN = 3;
    private const float StampSize = 1400f;
    private const float WorldHalf = 1536f;

    private const float TargetReliefMeters = 700f;

    public static void TestOneTerrain()
    {
        EditorSceneManager.OpenScene("Assets/Scenes/World.unity", OpenSceneMode.Single);
        var terrains = Object.FindObjectsOfType<Terrain>();
        Debug.Log($"{Tag} 场景 Terrain 数（应为 0，因为已切块清理）：{terrains.Length}");
        EditorApplication.Exit(0);
    }

    public static bool ApplyBiomeStamps()
    {
        var terrains = Object.FindObjectsOfType<Terrain>();
        Debug.Log($"{Tag} 待塑形 Terrain 数 = {terrains.Length}");
        if (terrains.Length == 0) return false;

        var t0 = terrains[0].terrainData;
        float before = SampleRelief(t0);
        Debug.Log($"{Tag} 塑形前地形起伏 = {before:F4}");

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
        settings.m_y = 0;
        settings.m_x = worldX;
        settings.m_z = worldZ;
        settings.m_width = StampSize;

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
