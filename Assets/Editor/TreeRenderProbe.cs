using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// 诊断树的渲染构成：为什么会被拆成很多批次。
///
/// 用户观察：树的 draw call 分成很多批次，且发现「很多树的 keyword 不一样」。
/// 需要逐层查清：
///   1. 地形上登记了哪些树原型（TreePrototype）、各多少实例；
///   2. 每个树原型 prefab 里有几个 LOD、各自用什么材质与 shader；
///   3. 每个材质的 shader keyword（shader_feature / multi_compile 的实际启用项）；
///   4. 哪些 keyword 差异会导致无法合批。
///
/// 术语澄清（写进结果，便于后续查阅）：
///   - 树的批次由 shader 变体（variant）决定，不是由 prefab 数量决定。
///   - 同一 shader + 同一 keyword 组合 + 同批实例 → 1 个 draw call。
///   - keyword 不同 = 不同 shader 变体 = 必须分开 draw call。
///
/// 用法：-executeMethod TreeRenderProbe.Run
/// 结果写 Logs/tree-render-probe.txt
/// </summary>
public static class TreeRenderProbe
{
    private const string Tag = "[TreeProbe]";
    private const string WorldScenePath = "Assets/Scenes/World.unity";
    private const string ResultPath = "Logs/tree-render-probe.txt";

    public static void Run()
    {
        var sb = new StringBuilder();
        // 只把汇总打到 Console。逐原型的细节只写文件——
        // 45 个原型 × 多级 LOD × 多材质会产生几千行日志，
        // batchmode 下逐条写 Console 会触发 Unity 自身的日志缓冲崩溃
        // （实测：EditorMonoConsole::LogToConsoleImplementation 处 RaiseException）。
        void W(string s) { sb.AppendLine(s); }
        void C(string s) { Debug.Log($"{Tag} {s}"); sb.AppendLine(s); }

        var scene = EditorSceneManager.OpenScene(WorldScenePath, OpenSceneMode.Single);
        var chunks = Object.FindObjectsOfType<SECTR_Chunk>().OrderBy(c => c.ScenePath).ToList();
        W($"主场景 chunk 数 = {chunks.Count}");

        // 收集所有 chunk 的树原型，合并去重（同一 Gaia 预设，各 chunk 原型应一致）
        var protoNames = new Dictionary<string, GameObject>();   // name -> prefab
        long totalTreeInstances = 0;
        int chunkInspected = 0;

        // per-prototype 实例数（按名字聚合）
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

                // 实例数：逐个取 TreeInstance
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

        // ── 逐个原型拆 LOD / 材质 / shader / keyword ──
        W($"");
        W($"===== 各原型的 LOD、材质与 shader keyword =====");

        // 按 shader 名 + keyword 集合 归类，看有多少种"渲染批次类型"
        var variantGroups = new Dictionary<string, List<string>>();  // variantKey -> 原型名
        var shaderSummary = new Dictionary<string, int>();

        foreach (var kv in protoNames.OrderBy(x => x.Key))
        {
            var go = kv.Value;
            W($"");
            W($"-- {kv.Key} --");

            var lods = go.GetComponentsInChildren<LODGroup>(true);
            if (lods.Length == 0)
            {
                // 无 LODGroup：整个 prefab 就是一个层级
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

        // ── 批次类型汇总（这是回答"为什么批次多"的核心）──
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

    /// <summary>
    /// 输出一组 renderer 的材质 / shader / keyword 细节，并登记到批次分类表。
    /// </summary>
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

            // 材质上显式启用的 keyword
            var mKeywords = m.shaderKeywords ?? new string[0];
            // shader 声明了哪些 keyword（用于判断"可切换项"）
            var shKeywords = sh != null ? sh.keywordSpace.keywordNames : new string[0];

            // 只保留真正影响变体的：材质 enabled + shader 声明
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
