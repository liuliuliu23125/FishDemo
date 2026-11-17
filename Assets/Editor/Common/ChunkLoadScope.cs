using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class ChunkLoadScope
{
    public struct Area
    {
        public float MinX, MaxX, MinZ, MaxZ;

        public static Area FromLake(float margin = 150f)
        {
            float half = LakeCarve.Radius + LakeCarve.Blend + margin;
            float cx = LakeCarve.Center.x, cz = LakeCarve.Center.y;
            return new Area
            {
                MinX = cx - half, MaxX = cx + half,
                MinZ = cz - half, MaxZ = cz + half,
            };
        }

        public bool Overlaps(float minX, float minZ, float sizeX, float sizeZ)
        {
            return minX + sizeX > MinX && minX < MaxX &&
                   minZ + sizeZ > MinZ && minZ < MaxZ;
        }

        public override string ToString() =>
            $"x[{MinX:F0},{MaxX:F0}] z[{MinZ:F0},{MaxZ:F0}]";
    }

    public static (int loaded, int skipped) LoadRelevant(Area? area, Action<string> log = null)
    {
        int loaded = 0, skipped = 0;
        var scenes = EditorBuildSettings.scenes
            .Where(s => s.enabled && !s.path.EndsWith("World.unity") && File.Exists(s.path))
            .ToArray();

        foreach (var s in scenes)
        {
            if (area.HasValue && !SceneTouchesArea(s.path, area.Value))
            {
                skipped++;
                continue;
            }

            EditorSceneManager.OpenScene(s.path, OpenSceneMode.Additive);
            loaded++;
        }

        log?.Invoke($"预加载 chunk：加载 {loaded} / 跳过 {skipped}" +
                    (area.HasValue ? $"（区域 {area.Value}）" : "（全量）"));
        return (loaded, skipped);
    }

    private static bool SceneTouchesArea(string scenePath, Area area)
    {
        if (!TryGetChunkOrigin(scenePath, out float ox, out float oz)) return true;
        float size = GetChunkSize(scenePath);
        if (size <= 0f) return true;
        return area.Overlaps(ox, oz, size, size);
    }

    private static bool TryGetChunkOrigin(string scenePath, out float ox, out float oz)
    {
        ox = oz = float.MaxValue;
        string text;
        try { text = File.ReadAllText(scenePath); }
        catch { return false; }

        bool any = false;
        foreach (Match m in Regex.Matches(text,
                     @"m_LocalPosition: \{x: (-?[\d.]+), y: -?[\d.]+, z: (-?[\d.]+)\}"))
        {
            if (!float.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out float x)) continue;
            if (!float.TryParse(m.Groups[2].Value, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out float z)) continue;
            if (x == 0f && z == 0f) continue;

            ox = Mathf.Min(ox, x);
            oz = Mathf.Min(oz, z);
            any = true;
        }

        if (!any) { ox = oz = 0f; return false; }
        return true;
    }

    private static float GetChunkSize(string scenePath)
    {
        foreach (var dep in AssetDatabase.GetDependencies(scenePath, recursive: false))
        {
            if (!dep.EndsWith(".asset")) continue;
            var td = AssetDatabase.LoadAssetAtPath<TerrainData>(dep);
            if (td != null && td.size.x > 0f) return td.size.x;
        }
        return 0f;
    }
}
