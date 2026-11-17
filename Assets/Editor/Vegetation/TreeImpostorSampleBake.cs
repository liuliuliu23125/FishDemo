using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;
using AmplifyImpostors;

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

        var before = new System.Collections.Generic.HashSet<string>(
            AssetDatabase.GetAllAssetPaths().Where(p => p.EndsWith(".asset") || p.EndsWith(".png")));

        W("");
        W("=== 准备：给 prefab 加 AmplifyImpostor 组件 ===");
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

        var after = AssetDatabase.GetAllAssetPaths().Where(p => p.EndsWith(".asset") || p.EndsWith(".png"));
        W("");
        W("=== 新增资产 ===");
        int added = 0;
        foreach (var p in after)
            if (!before.Contains(p)) { W($"  {p}"); added++; }
        W($"新增 {added} 个");

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
