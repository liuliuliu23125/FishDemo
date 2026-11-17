using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class TreeRenderProbe
{
    private const string Tag = "[TreeProbe]";
    private const string WorldScenePath = "Assets/Scenes/World.unity";
    private const string ResultPath = "Logs/tree-render-probe.txt";

    public static void Run()
    {
        var sb = new StringBuilder();
        void W(string s) { sb.AppendLine(s); }
        void C(string s) { Debug.Log($"{Tag} {s}"); sb.AppendLine(s); }

        var scene = EditorSceneManager.OpenScene(WorldScenePath, OpenSceneMode.Single);
        var chunks = Object.FindObjectsOfType<SECTR_Chunk>().OrderBy(c => c.ScenePath).ToList();
        W($"主场景 chunk 数 = {chunks.Count}");

        var protoNames = new Dictionary<string, GameObject>();
        long totalTreeInstances = 0;
        int chunkInspected = 0;

        var instCount = new Dictionary<string, long>();

        foreach (var c in chunks)
        {
            if (string.IsNullOrEmpty(c.NodeName) || !File.Exists(c.NodeName)) continue;

            var cs = EditorSceneManager.OpenScene(c.NodeName, OpenSceneMode.Additive);
            var terrains = Object.FindObjectsOfType<Terrain>()
                .Where(t => t.gameObject.scene == cs).ToArray();

            foreach (var t in terrains)
            {
                var td = t.terrainData;
                if (td == null) continue;

                var protos = td.treePrototypes;
                if (chunkInspected == 0)
                {
                    W($"首个 chunk 的 treePrototypes 数 = {protos.Length}");
                }

                for (int i = 0; i < protos.Length; i++)
                {
                    var go = protos[i].prefab;
                    if (go == null) continue;
                    if (!protoNames.ContainsKey(go.name)) protoNames[go.name] = go;
                }

                var insts = td.treeInstances;
                totalTreeInstances += insts.Length;
                foreach (var ti in insts)
                {
                    if (ti.prototypeIndex < 0 || ti.prototypeIndex >= protos.Length) continue;
                    var go = protos[ti.prototypeIndex].prefab;
                    if (go == null) continue;
                    instCount.TryGetValue(go.name, out long n);
                    instCount[go.name] = n + 1;
                }
            }

            EditorSceneManager.CloseScene(cs, true);
            chunkInspected++;
        }

        W($"");
        W($"===== 树原型汇总 =====");
        C($"不同树原型数 = {protoNames.Count}");
        C($"全部 chunk 树实例总数 = {totalTreeInstances:N0}");
        W($"");
        W("按实例数排序：");
        foreach (var kv in instCount.OrderByDescending(x => x.Value))
            W($"  {kv.Key,-40} {kv.Value,10:N0}");

        W($"");
        W($"===== 各原型的 LOD、材质与 shader keyword =====");

        var variantGroups = new Dictionary<string, List<string>>();
        var shaderSummary = new Dictionary<string, int>();

        foreach (var kv in protoNames.OrderBy(x => x.Key))
        {
            var go = kv.Value;
            W($"");
            W($"-- {kv.Key} --");

            var lods = go.GetComponentsInChildren<LODGroup>(true);
            if (lods.Length == 0)
            {
                W($"   无 LODGroup");
                var renderers = go.GetComponentsInChildren<MeshRenderer>(true);
                DumpRenderers(renderers, "   ", W, kv.Key, variantGroups, shaderSummary);
            }
            else
            {
                foreach (var lg in lods)
                {
                    var l = lg.GetLODs();
                    W($"   LODGroup: {lg.name}，{l.Length} 级，fadeMode={lg.fadeMode}");
                    for (int i = 0; i < l.Length; i++)
                    {
                        W($"     LOD{i} screenRelativeTransitionHeight={l[i].screenRelativeTransitionHeight:F3}，" +
                          $"{l[i].renderers.Length} 个 renderer");
                        DumpRenderers(l[i].renderers, "       ", W, $"{kv.Key}/LOD{i}", variantGroups, shaderSummary);
                    }
                }
            }
        }

        W($"");
        W($"===== 渲染批次类型汇总（shader + keyword 组合）=====");
        C($"不同 shader = {shaderSummary.Count} 个");
        foreach (var kv in shaderSummary.OrderByDescending(x => x.Value))
            W($"   {kv.Key}: 出现 {kv.Value} 次");

        W($"");
        C($"不同 shader+keyword 组合 = {variantGroups.Count} 个（每组至少 1 个独立 draw call）");
        foreach (var kv in variantGroups.OrderBy(x => x.Key))
        {
            W($"   [{kv.Value.Count} 个原型] {kv.Key}");
            foreach (var p in kv.Value.Distinct().Take(6))
                W($"        {p}");
        }

        W($"");
        W("===== 结论提示 =====");
        W("批次数的决定因素：");
        W("  1. 不同 shader → 必须分开");
        W("  2. 同一 shader 但 keyword 组合不同 → 必须分开");
        W("  3. 同一 shader+keyword 但材质不同 → SRP Batcher 可合（不拆批次）");
        W("  4. LOD 层级各自独立提交（LOD0/1/2 是不同 mesh + 可能不同材质）");
        W("  5. 每个 Terrain 的树由 TerrainRenderer 提交，chunk 之间天然独立");

        Flush(sb);
        EditorApplication.Exit(0);
    }

    private static void DumpRenderers(
        Renderer[] renderers, string indent, System.Action<string> W,
        string protoKey,
        Dictionary<string, List<string>> variantGroups,
        Dictionary<string, int> shaderSummary)
    {
        foreach (var r in renderers)
        {
            if (r == null) continue;
            var m = r.sharedMaterial;
            if (m == null)
            {
                W($"{indent}[{r.name}] 材质为 null");
                continue;
            }

            var sh = m.shader;
            string shName = sh != null ? sh.name : "(shader null)";

            var mKeywords = m.shaderKeywords ?? new string[0];
            var shKeywords = sh != null ? sh.keywordSpace.keywordNames : new string[0];

            var active = mKeywords.Where(k => shKeywords.Contains(k)).OrderBy(k => k).ToArray();

            string variantKey = $"{shName} | {(active.Length == 0 ? "(无 keyword)" : string.Join(",", active))}";

            if (!variantGroups.ContainsKey(variantKey)) variantGroups[variantKey] = new List<string>();
            variantGroups[variantKey].Add(protoKey);

            shaderSummary.TryGetValue(shName, out int n);
            shaderSummary[shName] = n + 1;

            W($"{indent}[{r.name}] shader={shName}");
            W($"{indent}    material={m.name}  renderQueue={m.renderQueue} " +
               $"enableInstancing={m.enableInstancing}");
            W($"{indent}    材质启用 keyword({mKeywords.Length}) = " +
               $"{(mKeywords.Length == 0 ? "(无)" : string.Join(", ", mKeywords))}");
            W($"{indent}    shader 声明 keyword 数 = {shKeywords.Length}");
            W($"{indent}    → 实际变体 = {variantKey}");
        }
    }

    private static void Flush(StringBuilder sb)
    {
        Directory.CreateDirectory("Logs");
        File.WriteAllText(ResultPath, sb.ToString());
    }
}
