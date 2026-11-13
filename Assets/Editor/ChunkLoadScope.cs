using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// 验证脚本共用的「只加载必要 chunk」工具。
///
/// ── 为什么要做这件事 ──
/// 三个验证脚本（`LakeWaterVerify` / `CrestRenderProbe` / `LakeRenderVerify`）
/// 原先都把 Build Settings 里 **全部 36 个 chunk** 用 `OpenScene(Additive)` 打开，
/// 单次运行 2 分钟以上。但验证只在固定机位看湖：
/// 湖盆半径 190m + 过渡带 90m = 280m 的 AABB，只需覆盖这个矩形附近的 chunk。
/// 实测按区域筛选后只需 **8 个**（含重复计数；唯一位置 4 个）。
///
/// ── 为什么必须预加载，而不是靠 SECTR 流式 ──
/// SECTR 是异步加载（`SECTR_Chunk` 用 `LoadSceneAsync` + activationQueue 节流，
/// 一帧只激活一个），脚本瞬移相机后**每次运行加载到的 chunk 都不一样**
/// （实测同机位两次运行 GTS subTile bounds 完全不同）。
/// 所以仍然要预加载，只是把「全部」换成「相关的」。
///
/// ── 怎么定位 chunk 的世界原点（实测校准，2026-11-13）──
/// **不能只看文件名网格坐标**：本工程 36 个 chunk 的文件名里
/// `Terrain_X_Y` 是 1024m 大区块、`a-b-0` 是 512m 子块，
/// 两者组合才唯一确定位置，直接取末尾数字会得到 4 种重复值。
///
/// 可靠做法：读场景 YAML 里所有非零 `m_LocalPosition`，**取 x/z 的最小值**
/// 作为 chunk 世界原点。原因：每个 chunk 场景里有两个对称位置的物体
/// （地形在负半轴、镜像物体在正半轴），最小值恒为地形原点。
/// 实测校验：
///   `Terrain_0_0 ... 0-0-0` → (-1536,-1536)
///   `Terrain_1_1 ... 0-0-0` → (-512,-512)
///   `Terrain_2_2 ... 1-1-0` → (-1024,-1024)
/// 均与场景内 Terrain 的实际摆放一致。
/// </summary>
public static class ChunkLoadScope
{
    /// <summary>目标矩形（世界 XZ）。</summary>
    public struct Area
    {
        public float MinX, MaxX, MinZ, MaxZ;

        /// <summary>
        /// 以湖盆为中心的矩形。
        /// `margin` 给相机视野留余量：轨道相机在半径 300m 处看湖心，
        /// 视野边缘会扫到岸外约 150m，所以默认留 150。
        /// </summary>
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

    /// <summary>
    /// 打开与 <paramref name="area"/> 相交的 chunk 场景（Additive）。
    /// </summary>
    /// <param name="area">目标区域；传 null 表示加载全部。</param>
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

    /// <summary>判断 chunk 场景是否与目标区域相交。</summary>
    private static bool SceneTouchesArea(string scenePath, Area area)
    {
        if (!TryGetChunkOrigin(scenePath, out float ox, out float oz)) return true;  // 解析失败 → 保守加载
        float size = GetChunkSize(scenePath);
        if (size <= 0f) return true;
        return area.Overlaps(ox, oz, size, size);
    }

    /// <summary>
    /// 从场景文本读出 chunk 世界原点（所有非零 localPosition 的最小 x/z）。
    /// 纯文本扫描，不触发场景打开与资产导入。
    /// </summary>
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
            if (x == 0f && z == 0f) continue;   // 跳过根节点与子物体

            ox = Mathf.Min(ox, x);
            oz = Mathf.Min(oz, z);
            any = true;
        }

        if (!any) { ox = oz = 0f; return false; }
        return true;
    }

    /// <summary>从场景依赖里读 TerrainData.size.x 作为 chunk 边长。</summary>
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
