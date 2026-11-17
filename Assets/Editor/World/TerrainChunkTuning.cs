using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class TerrainChunkTuning
{
    private const string WorldScenePath = "Assets/Scenes/World.unity";
    private const string ChunkDir = "Assets/Scenes/World/Chunks";
    private const string ResultDir = "Logs/Gaia";
    private const string Tag = "[TerrainTuning]";

    private static readonly Dictionary<string, float> Baseline = new Dictionary<string, float>
    {
        { "DrawInstanced", 0f },
        { "DetailObjectDistance", 150f },
        { "DetailObjectDensity", 0.5f },
        { "TreeDistance", 1000f },
        { "TreeBillboardDistance", 90f },
        { "TreeMaximumFullLODCount", 50f },
        { "TreeCrossFadeLength", 50f },
        { "HeightmapPixelError", 5f },
        { "SplatMapDistance", 1024f },
    };

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

    public static void DisableDetail()
    {
        var targets = new Dictionary<string, float>(Mobile);
        targets["DetailObjectDistance"] = 0f;
        targets["DetailObjectDensity"] = 0f;
        Run("disable-detail", targets, "terrain-tuning-disable-detail.txt");
    }

    public static void ApplyTreeLight()
    {
        var targets = new Dictionary<string, float>(Mobile)
        {
            ["DetailObjectDistance"] = 0f,
            ["DetailObjectDensity"] = 0f,
            ["TreeDistance"] = 450f,
            ["TreeBillboardDistance"] = 35f,
            ["TreeMaximumFullLODCount"] = 16f,
            ["TreeCrossFadeLength"] = 20f,
        };
        Run("tree-light", targets, "terrain-tuning-tree-light.txt");
    }

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
        Add("TreeCrossFadeLength", t.treeCrossFadeLength);
        Add("HeightmapPixelError", t.heightmapPixelError);
        Add("SplatMapDistance", t.basemapDistance > 0 ? t.basemapDistance : 0f);
    }

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
        SetIf("TreeCrossFadeLength", v => t.treeCrossFadeLength = v, () => t.treeCrossFadeLength);
        SetIf("HeightmapPixelError", v => t.heightmapPixelError = v, () => t.heightmapPixelError);
        SetIf("SplatMapDistance", v => t.basemapDistance = v, () => t.basemapDistance);

        if (changed)
        {
            EditorUtility.SetDirty(t);
            W($"  改 {Path.GetFileNameWithoutExtension(scenePath)}/{t.name}");
        }
        return changed;
    }

    private static string Describe(List<float> values)
    {
        var groups = values.GroupBy(v => v)
            .OrderBy(g => g.Key)
            .Select(g => $"{g.Key:G4}×{g.Count()}");
        return string.Join(", ", groups);
    }
}
