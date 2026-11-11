using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// 地形 chunk 渲染参数调优 / 基线导出。
///
/// ── 为什么需要它 ──
/// 36 个 chunk 场景里的 Terrain 参数（drawInstanced / detail 距离 / 草密度 / 树距离）
/// 是 Gaia "Desktop" 预设的产物，面向 PC。移动端 30fps 目标下这些值是主要瓶颈：
///   - m_DrawInstanced: 0 → 草走 per-object 提交，单块 570 万 detail 实例（实测）
///   - m_DetailObjectDistance: 150 → 移动端草可见距离远超标
/// 批量改这些字段必须走脚本：手改 36 个场景既不可行也不可复现。
///
/// ── 为什么带 Apply/Restore 两个方向 ──
/// 调参是一次实验，必须能一键回到原值再对比。Restore 恢复的是 Gaia 原始桌面预设值
/// （见 Baseline 常量），不是"猜测的默认值"，所以来回执行是幂等的。
///
/// ── 为什么不碰 TerrainData ──
/// 草密度既可以调 Terrain.m_DetailObjectDensity（渲染期密度，无损、可逆），
/// 也可以改 TerrainData 的 detail layer 数据（破坏性、需重新撒点）。
/// 这里只用前者：同一份数据，只改渲染密度，随时可还原。
///
/// 用法：
///   Unity.exe -batchmode -projectPath <proj> -executeMethod TerrainChunkTuning.Report
///   Unity.exe -batchmode -projectPath <proj> -executeMethod TerrainChunkTuning.ApplyMobile
///   Unity.exe -batchmode -projectPath <proj> -executeMethod TerrainChunkTuning.RestoreDesktop
///   不要加 -quit，由脚本自己 Exit。
/// </summary>
public static class TerrainChunkTuning
{
    private const string WorldScenePath = "Assets/Scenes/World.unity";
    private const string ChunkDir = "Assets/Scenes/World/Chunks";
    private const string ResultDir = "Logs/Gaia";
    private const string Tag = "[TerrainTuning]";

    /// <summary>Gaia Desktop 预设的原始值。Restore 恢复到这里，保证可逆。</summary>
    private static readonly Dictionary<string, float> Baseline = new Dictionary<string, float>
    {
        { "DrawInstanced", 0f },
        { "DetailObjectDistance", 150f },
        { "DetailObjectDensity", 0.5f },
        { "TreeDistance", 1000f },
        { "TreeBillboardDistance", 90f },
        { "TreeMaximumFullLODCount", 50f },
        { "HeightmapPixelError", 5f },
        { "SplatMapDistance", 1024f },
    };

    /// <summary>
    /// 移动端档位。
    ///
    /// 取值依据（不是拍脑袋）：
    /// - DrawInstanced=1：草从 per-object 提交改为 instanced，这是单项收益最大的开关。
    /// - DetailObjectDistance=70：150m 的草在 6 寸屏上屏幕占比 < 1 像素/实例，
    ///   降到 70m 直接砍掉大部分实例；配合 density 下调，实测需确认无明显"草地边界"。
    /// - DetailObjectDensity=0.35：渲染期密度，不改数据，可逆。
    /// - TreeBillboardDistance=60：早切 billboard，减少全 LOD 树数量。
    /// - TreeDistance=600：远景树靠地形烘焙/低模，1000m 无意义。
    /// - HeightmapPixelError=10：地形网格精度略降，移动端可接受（默认 5 过密）。
    /// - SplatMapDistance=512：贴图细节距离减半，与 detail 距离匹配。
    /// </summary>
    private static readonly Dictionary<string, float> Mobile = new Dictionary<string, float>
    {
        { "DrawInstanced", 1f },
        { "DetailObjectDistance", 70f },
        { "DetailObjectDensity", 0.35f },
        { "TreeDistance", 600f },
        { "TreeBillboardDistance", 60f },
        { "TreeMaximumFullLODCount", 32f },
        { "HeightmapPixelError", 10f },
        { "SplatMapDistance", 512f },
    };

    public static void Report()
    {
        Run("baseline", null, "terrain-tuning-before.txt");
    }

    public static void ApplyMobile()
    {
        Run("mobile", Mobile, "terrain-tuning-apply.txt");
    }

    public static void RestoreDesktop()
    {
        Run("restore", Baseline, "terrain-tuning-restore.txt");
    }

    /// <summary>
    /// 关闭 Terrain Detail（细节物体）渲染，只保留 BRG 草。
    ///
    /// 为什么需要：实测每个 chunk 的 TerrainData 登记了 7 种 detail 原型
    /// （1 种草 404 万 + 3 种花约 170 万 + 枯草等，合计约 570 万/块），
    /// 它们走 Terrain 的 detail 渲染路径，与 BRG 草（1265 万实例）叠在同一片地表
    /// ——同一处地方被画两遍草，且 detail 那套各原型是独立 draw call，无法与 BRG 合批。
    /// 设 DetailObjectDistance=0 是 Unity 文档规定的"关闭 detail 渲染"方式，
    /// 不改 TerrainData 里的撒点数据，随时可用 RestoreDesktop 恢复。
    ///
    /// 代价：CloverFlower / Daisy / Dandelions 这些花也一并消失（它们都是 detail 原型）。
    /// 若将来要保留花，应改为把花也做成 BRG 实例、并入同一套数据管线。
    /// </summary>
    public static void DisableDetail()
    {
        // 用复制构造 + 索引器覆盖（不能用 Add，同样的 key 会抛重复键异常）。
        var targets = new Dictionary<string, float>(Mobile);
        targets["DetailObjectDistance"] = 0f;   // 0 = 不渲染 detail
        targets["DetailObjectDensity"] = 0f;
        Run("disable-detail", targets, "terrain-tuning-disable-detail.txt");
    }

    /// <summary>
    /// 统一入口：targets 为 null 时只统计不修改。
    ///
    /// 为什么要逐个 OpenScene 而不是改主场景：chunk 是独立场景文件，
    /// 参数存在各自场景的 Terrain 组件上，主场景里没有 Terrain。
    /// </summary>
    private static void Run(string mode, Dictionary<string, float> targets, string outFile)
    {
        var sb = new StringBuilder();
        void W(string s) { Debug.Log($"{Tag} {s}"); sb.AppendLine(s); }

        W($"模式 = {mode}");
        var chunkScenes = Directory.GetFiles(ChunkDir, "*.unity", SearchOption.TopDirectoryOnly)
            .Select(p => p.Replace('\\', '/'))
            .OrderBy(p => p)
            .ToList();
        W($"chunk 场景文件数 = {chunkScenes.Count}");

        // 只处理主场景实际引用的那一批（活跃批次），避免动到孤儿批次
        var activeChunks = LoadActiveChunkPaths();
        W($"主场景引用的活跃 chunk 数 = {activeChunks.Count}");
        if (activeChunks.Count > 0)
        {
            chunkScenes = chunkScenes
                .Where(p => activeChunks.Contains(Path.GetFileNameWithoutExtension(p)))
                .ToList();
            W($"→ 本次处理 {chunkScenes.Count} 个活跃 chunk");
        }

        int touchedTerrains = 0, changedFiles = 0;
        var before = new Dictionary<string, List<float>>();
        var after = new Dictionary<string, List<float>>();

        foreach (string scenePath in chunkScenes)
        {
            var scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);
            var terrains = UnityEngine.Object.FindObjectsOfType<Terrain>();
            bool dirty = false;

            foreach (var t in terrains)
            {
                touchedTerrains++;
                Record(before, t);

                if (targets != null)
                {
                    if (ApplyTargets(t, targets, scenePath, W)) dirty = true;
                    Record(after, t);
                }
            }

            // 只统计、不修改时不需要保存，避免无意义地重写场景文件
            if (dirty)
            {
                EditorSceneManager.MarkSceneDirty(scene);
                EditorSceneManager.SaveScene(scene);
                changedFiles++;
            }
        }

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        W("");
        W($"处理 Terrain 数 = {touchedTerrains}，改写场景数 = {changedFiles}");
        W("");
        W("=== 参数分布 ===");
        foreach (var kv in (targets != null ? after : before))
        {
            W($"  {kv.Key}: {Describe(kv.Value)}");
        }

        bool ok = touchedTerrains > 0;
        W($"==== 结论：{(ok ? "PASS" : "FAIL")} ====");

        Directory.CreateDirectory(ResultDir);
        File.WriteAllText(Path.Combine(ResultDir, outFile), sb.ToString());
        EditorApplication.Exit(ok ? 0 : 2);
    }

    /// <summary>
    /// 从主场景的 SECTR_Chunk.NodeName 反推活跃 chunk 文件名集合。
    /// 与 CleanupOrphanAssets 用的是同一判定口径，保证"改哪些"和"留哪些"一致。
    /// </summary>
    private static HashSet<string> LoadActiveChunkPaths()
    {
        var result = new HashSet<string>();
        var world = EditorSceneManager.OpenScene(WorldScenePath, OpenSceneMode.Single);
        var chunks = UnityEngine.Object.FindObjectsOfType<SECTR_Chunk>();
        foreach (var c in chunks)
        {
            if (!string.IsNullOrEmpty(c.NodeName))
                result.Add(Path.GetFileNameWithoutExtension(c.NodeName));
        }
        return result;
    }

    private static void Record(Dictionary<string, List<float>> bag, Terrain t)
    {
        void Add(string key, float v)
        {
            if (!bag.TryGetValue(key, out var list)) { list = new List<float>(); bag[key] = list; }
            list.Add(v);
        }
        Add("DrawInstanced", t.drawInstanced ? 1f : 0f);
        Add("DetailObjectDistance", t.detailObjectDistance);
        Add("DetailObjectDensity", t.detailObjectDensity);
        Add("TreeDistance", t.treeDistance);
        Add("TreeBillboardDistance", t.treeBillboardDistance);
        Add("TreeMaximumFullLODCount", t.treeMaximumFullLODCount);
        Add("HeightmapPixelError", t.heightmapPixelError);
        Add("SplatMapDistance", t.basemapDistance > 0 ? t.basemapDistance : 0f);
    }

    /// <summary>把 targets 里的值应用到单个 Terrain，返回是否真的发生了改变。</summary>
    private static bool ApplyTargets(Terrain t, Dictionary<string, float> targets, string scenePath, Action<string> W)
    {
        bool changed = false;

        void SetIf(string key, Action<float> setter, Func<float> getter)
        {
            if (!targets.TryGetValue(key, out float want)) return;
            float cur = getter();
            if (Mathf.Approximately(cur, want)) return;
            setter(want);
            changed = true;
        }

        SetIf("DrawInstanced", v => t.drawInstanced = v > 0.5f, () => t.drawInstanced ? 1f : 0f);
        SetIf("DetailObjectDistance", v => t.detailObjectDistance = v, () => t.detailObjectDistance);
        SetIf("DetailObjectDensity", v => t.detailObjectDensity = v, () => t.detailObjectDensity);
        SetIf("TreeDistance", v => t.treeDistance = v, () => t.treeDistance);
        SetIf("TreeBillboardDistance", v => t.treeBillboardDistance = v, () => t.treeBillboardDistance);
        SetIf("TreeMaximumFullLODCount", v => t.treeMaximumFullLODCount = (int)v, () => t.treeMaximumFullLODCount);
        SetIf("HeightmapPixelError", v => t.heightmapPixelError = v, () => t.heightmapPixelError);
        SetIf("SplatMapDistance", v => t.basemapDistance = v, () => t.basemapDistance);

        if (changed)
        {
            EditorUtility.SetDirty(t);
            W($"  改 {Path.GetFileNameWithoutExtension(scenePath)}/{t.name}");
        }
        return changed;
    }

    /// <summary>把一组值压成 "值(数量)" 摘要，便于人工核对 36 块是否一致。</summary>
    private static string Describe(List<float> values)
    {
        var groups = values.GroupBy(v => v)
            .OrderBy(g => g.Key)
            .Select(g => $"{g.Key:G4}×{g.Count()}");
        return string.Join(", ", groups);
    }
}
