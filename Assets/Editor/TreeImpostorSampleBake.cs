using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;
using AmplifyImpostors;

/// <summary>
/// 烘一个云杉 impostor 样本，验证 Amplify Impostors 在本工程（2022.3 + URP 14.0.12）能跑通。
///
/// ── 为什么先烘样本，不直接铺到 46 万棵树上 ──
/// 树的渲染由 TerrainRenderer 在 native 层提交，场景里没有带 LODGroup 的树对象，
/// AmplifyImpostor 挂不上去（它是 MonoBehaviour，靠替换 LODGroup 的 renderer 工作）。
/// 所以在设计接入方案前，必须先确认最基础的一环成立：
/// 这个包的 URP14 烘焙链路在本工程能出图、shader 能编译、生成的 impostor 看得过去。
/// 这一步不成立，后面的方案设计全是空谈。
///
/// 用法：-executeMethod TreeImpostorSampleBake.Run
/// 输出 Logs/tree-impostor-sample.txt，及 Logs/impostor-sample.png（烘焙结果预览）
/// </summary>
public static class TreeImpostorSampleBake
{
    private const string Tag = "[ImpostorSample]";
    private const string ResultPath = "Logs/tree-impostor-sample.txt";
    private const string PngPath = "Logs/impostor-sample.png";
    private const string PrefabPath =
        "Assets/Procedural Worlds/Packages - Install/Gaia Pro Assets and Biomes/Prefabs/Trees/PW_Tree_Spruce_06.prefab";

    public static void Run()
    {
        var sb = new StringBuilder();
        void W(string s) { Debug.Log($"{Tag} {s}"); sb.AppendLine(s); }

        W($"包版本 = {VersionInfo.StaticToString()}");
        W($"目标 prefab = {PrefabPath}");

        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
        if (prefab == null) { W("FAIL：prefab 载入失败"); Flush(sb, 2); return; }

        var lgs = prefab.GetComponentsInChildren<LODGroup>(true);
        W($"prefab 里 LODGroup 数 = {lgs.Length}");
        foreach (var lg in lgs)
        {
            var lods = lg.GetLODs();
            W($"  {lg.name}: {lods.Length} 级, size={lg.size:F2}, 各级 renderer 数 = " +
              string.Join(",", lods.Select(l => l.renderers.Length)));
        }

        // 记录烘焙前资产快照，便于判断包生成了什么
        var before = new System.Collections.Generic.HashSet<string>(
            AssetDatabase.GetAllAssetPaths().Where(p => p.EndsWith(".asset") || p.EndsWith(".png")));

        W("");
        W("=== 准备：给 prefab 加 AmplifyImpostor 组件 ===");
        // BakeImpostorsAtPaths 的 autoCreate 只影响 Data 是否允许为空，
        // 但 IsReady 要求 ResolveImpostor(go) != null，即 prefab 上必须先有组件。
        // 所以这里先补组件，再调烘焙。用 PrefabUtility 保持 prefab 结构正确。
        var root = PrefabUtility.LoadPrefabContents(PrefabPath);
        try
        {
            var lg0 = root.GetComponentInChildren<LODGroup>(true);
            var comp = root.GetComponent<AmplifyImpostor>();
            if (comp == null) comp = root.AddComponent<AmplifyImpostor>();
            if (comp.LodGroup == null) comp.LodGroup = lg0;
            W($"  组件 = {comp.GetType().Name}，绑定的 LODGroup = {(comp.LodGroup != null ? comp.LodGroup.name : "null")}");
            PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(root);
        }
        AssetDatabase.Refresh();

        W("");
        W("=== 开始烘焙（BakeImpostorsAtPaths, autoCreate=true）===");
        int n = 0;
        try
        {
            n = AmplifyImpostorBakeAPI.BakeImpostorsAtPaths(
                new[] { PrefabPath }, autoCreate: true, autoCreateFolder: "Assets/ImpostorBaked");
            W($"BakeImpostorsAtPaths 提交数 = {n}");
        }
        catch (System.Exception e)
        {
            W($"烘焙抛异常：{e.GetType().Name}: {e.Message}");
            W(e.StackTrace ?? "");
        }

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        // 烘焙产出了什么
        var after = AssetDatabase.GetAllAssetPaths().Where(p => p.EndsWith(".asset") || p.EndsWith(".png"));
        W("");
        W("=== 新增资产 ===");
        int added = 0;
        foreach (var p in after)
            if (!before.Contains(p)) { W($"  {p}"); added++; }
        W($"新增 {added} 个");

        // 校验 prefab 是否被加上了组件
        var imp = prefab.GetComponent<AmplifyImpostor>();
        W("");
        W($"prefab 上 AmplifyImpostor 组件 = {(imp != null ? "有" : "无")}");
        if (imp != null)
        {
            W($"  ImpostorAsset = {(imp.Data != null ? imp.Data.name : "null")}");
            W($"  LodGroup = {(imp.LodGroup != null ? imp.LodGroup.name : "null")}");
            W($"  m_lodReplacement = {imp.m_lodReplacement}");
            var lg2 = prefab.GetComponentsInChildren<LODGroup>(true).FirstOrDefault();
            if (lg2 != null)
                W($"  烘焙后 LOD 级数 = {lg2.GetLODs().Length}");
        }

        bool ok = n > 0 && added > 0;
        W("");
        W($"==== 结论：{(ok ? "PASS" : "FAIL")} ====");
        Flush(sb, ok ? 0 : 2);
    }

    private static void Flush(StringBuilder sb, int code)
    {
        Directory.CreateDirectory("Logs");
        File.WriteAllText(ResultPath, sb.ToString());
        if (Application.isBatchMode) EditorApplication.Exit(code);
    }
}
