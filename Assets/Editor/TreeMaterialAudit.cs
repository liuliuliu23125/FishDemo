using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class TreeMaterialAudit
{
    private const string Tag = "[TreeAudit]";
    private const string WorldScenePath = "Assets/Scenes/World.unity";
    private const string ChunkDir = "Assets/Scenes/World/Chunks";
    private const string ResultPath = "Logs/tree-material-audit.txt";

    public static void Run()
    {
        var sb = new StringBuilder();
        void W(string s) { sb.AppendLine(s); }
        void C(string s) { Debug.Log($"{Tag} {s}"); sb.AppendLine(s); }

        var scene = EditorSceneManager.OpenScene(WorldScenePath, OpenSceneMode.Single);
        var chunks = Object.FindObjectsOfType<SECTR_Chunk>().OrderBy(c => c.NodeName).ToList();
        W($"主场景 chunk 数 = {chunks.Count}");

        var instCount = new Dictionary<string, int>();
        var protoPrefab = new Dictionary<string, GameObject>();

        int opened = 0;
        foreach (var c in chunks)
        {
            if (string.IsNullOrEmpty(c.NodeName) || !File.Exists(c.NodeName)) continue;
            var cs = EditorSceneManager.OpenScene(c.NodeName, OpenSceneMode.Additive);
            foreach (var t in Object.FindObjectsOfType<Terrain>())
            {
                if (t.gameObject.scene != cs) continue;
                var td = t.terrainData;
                if (td == null) continue;
                var protos = td.treePrototypes;
                var insts = td.treeInstances;
                for (int i = 0; i < insts.Length; i++)
                {
                    int pi = insts[i].prototypeIndex;
                    if (pi < 0 || pi >= protos.Length) continue;
                    var go = protos[pi].prefab;
                    if (go == null) continue;
                    string key = go.name;
                    instCount.TryGetValue(key, out int n);
                    instCount[key] = n + 1;
                    if (!protoPrefab.ContainsKey(key)) protoPrefab[key] = go;
                }
            }
            EditorSceneManager.CloseScene(cs, true);
            opened++;
        }
        W($"附加加载 chunk = {opened}，不同原型 = {instCount.Count}，实例总数 = {instCount.Values.Sum()}");

        var problems = new List<string>();
        var shaderSet = new HashSet<string>();
        var variantSet = new HashSet<string>();
        int slotTotal = 0, nullTex = 0, nullMat = 0;

        foreach (var kv in instCount.OrderByDescending(x => x.Value))
        {
            var go = protoPrefab[kv.Key];
            W($"");
            W($"===== {kv.Key}  实例 {kv.Value:N0} =====");

            var lgs = go.GetComponentsInChildren<LODGroup>(true);
            if (lgs.Length == 0)
            {
                W("  无 LODGroup（单级或不走 LOD）");
                DumpRenderers(go.GetComponentsInChildren<MeshRenderer>(true), "  ", W, problems,
                    shaderSet, variantSet, ref slotTotal, ref nullTex, ref nullMat);
                continue;
            }

            foreach (var lg in lgs)
            {
                var lods = lg.GetLODs();
                W($"  LODGroup {lg.name}：{lods.Length} 级");
                for (int i = 0; i < lods.Length; i++)
                {
                    W($"    LOD{i} h={lods[i].screenRelativeTransitionHeight:F4} renderers={lods[i].renderers.Length}");
                    DumpRenderers(lods[i].renderers, "      ", W, problems,
                        shaderSet, variantSet, ref slotTotal, ref nullTex, ref nullMat);
                }
            }
        }

        W($"");
        W("===== 汇总 =====");
        C($"材质槽总数 = {slotTotal}，_MainTex 空 = {nullTex}，材质引用丢失 = {nullMat}");
        C($"不同 shader = {shaderSet.Count}");
        foreach (var s in shaderSet) W($"   {s}");
        C($"不同 shader+keyword 组合 = {variantSet.Count}");
        foreach (var v in variantSet.OrderBy(x => x)) W($"   {v}");

        W("");
        W($"===== 问题清单（{problems.Count} 项）=====");
        foreach (var p in problems) W($"  {p}");

        Directory.CreateDirectory("Logs");
        File.WriteAllText(ResultPath, sb.ToString());
        C($"写出 {ResultPath}");

        if (Application.isBatchMode) EditorApplication.Exit(0);
    }

    private static void DumpRenderers(
        Renderer[] renderers, string indent, System.Action<string> W, List<string> problems,
        HashSet<string> shaderSet, HashSet<string> variantSet,
        ref int slotTotal, ref int nullTex, ref int nullMat)
    {
        foreach (var r in renderers)
        {
            if (r == null) continue;
            var mats = r.sharedMaterials;
            if (mats.Length == 0)
            {
                problems.Add($"{r.name}: sharedMaterials 为空");
                continue;
            }

            for (int i = 0; i < mats.Length; i++)
            {
                var m = mats[i];
                slotTotal++;
                if (m == null)
                {
                    nullMat++;
                    problems.Add($"{r.name} 槽 {i}: 材质引用丢失（prefab 里指向不存在的 .mat）");
                    W($"{indent}[{r.name}] 槽{i} 材质 NULL");
                    continue;
                }

                var sh = m.shader;
                string shName = sh != null ? sh.name : "(null shader)";
                shaderSet.Add(shName);

                var tex = m.HasProperty("_MainTex") ? m.GetTexture("_MainTex") : null;
                bool texNull = tex == null;
                if (texNull) { nullTex++; problems.Add($"{m.name}: _MainTex 为空（会渲染成白色）"); }

                var kws = (m.shaderKeywords ?? new string[0]).OrderBy(k => k).ToArray();
                string variant = $"{shName} | {(kws.Length == 0 ? "(无)" : string.Join(",", kws))}";
                variantSet.Add(variant);

                var col = m.HasProperty("_Color") ? m.GetColor("_Color") : Color.white;
                string cutoff = m.HasProperty("_Cutoff") ? m.GetFloat("_Cutoff").ToString("F3") : "-";
                W($"{indent}[{r.name}] 槽{i} mat={m.name}");
                W($"{indent}    shader={shName}  _MainTex={(texNull ? "NULL" : tex.name)}");
                W($"{indent}    _Color={col}  cutoff={cutoff}");
                W($"{indent}    keywords({kws.Length})={string.Join(",", kws)}");
            }
        }
    }
}
