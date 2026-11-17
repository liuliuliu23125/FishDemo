using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

public static class TreeKeywordCleanup
{
    private const string Tag = "[KwCleanup]";
    private const string TreeRoot =
        "Assets/Procedural Worlds/Packages - Install/Gaia Pro Assets and Biomes/Content Resources/Trees";
    private const string BackupDir = "Tools/tree-keyword-backup";
    private const string ResultPath = "Logs/tree-keyword-cleanup.txt";
    private const string TargetKeyword = "_PW_SF_COVER_ON";

    public static void Run()
    {
        var sb = new StringBuilder();
        void W(string s) { Debug.Log($"{Tag} {s}"); sb.AppendLine(s); }

        Directory.CreateDirectory(BackupDir);

        var mats = AssetDatabase.FindAssets("t:Material", new[] { TreeRoot })
            .Select(AssetDatabase.GUIDToAssetPath)
            .Where(p => p.EndsWith(".mat"))
            .OrderBy(p => p)
            .ToList();
        W($"树材质数 = {mats.Count}");

        int changed = 0, skipped = 0;
        foreach (var path in mats)
        {
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null) continue;

            var kws = mat.shaderKeywords ?? new string[0];
            if (!kws.Contains(TargetKeyword)) { skipped++; continue; }

            float cover = mat.HasProperty("_PW_Cover") ? mat.GetFloat("_PW_Cover") : 0f;
            float prog0 = mat.HasProperty("_PW_CoverLayer0Progress") ? mat.GetFloat("_PW_CoverLayer0Progress") : 0f;
            float prog1 = mat.HasProperty("_PW_CoverLayer1Progress") ? mat.GetFloat("_PW_CoverLayer1Progress") : 0f;

            bool reallyUnused = cover <= 0.001f && prog0 <= 0.001f && prog1 <= 0.001f;
            if (!reallyUnused)
            {
                W($"保留 {mat.name}：cover={cover} prog0={prog0} prog1={prog1} —— 实际在用");
                skipped++;
                continue;
            }

            string bak = Path.Combine(BackupDir, mat.name + ".mat");
            if (!File.Exists(bak)) File.Copy(path, bak);

            mat.DisableKeyword(TargetKeyword);
            EditorUtility.SetDirty(mat);
            changed++;
        }

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        int stillHas = 0;
        var combosAll = new HashSet<string>();
        var combosTree = new HashSet<string>();
        foreach (var path in mats)
        {
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null) continue;
            var kws = (mat.shaderKeywords ?? new string[0]).OrderBy(k => k).ToArray();
            if (kws.Contains(TargetKeyword)) stillHas++;
            combosAll.Add(string.Join(",", kws));
            combosTree.Add(string.Join(",", kws.Where(k =>
                k.StartsWith("_PW_SF_") || k == "_ALPHATEST_ON" || k == "_ALPHATEST_OFF")));
        }

        W("");
        W($"去掉 {TargetKeyword} 的材质 = {changed}，跳过 = {skipped}");
        W($"复核：仍带该 keyword 的材质 = {stillHas}");
        W($"树的决定性问题组合（_PW_SF_* + _ALPHATEST）= {combosTree.Count} 种");
        foreach (var c in combosTree.OrderBy(x => x)) W($"    [{c}]");
        W($"全量 keyword 组合（含 URP 内置）= {combosAll.Count} 种");

        bool ok = stillHas == 0;
        W($"==== 结论：{(ok ? "PASS" : "FAIL")} ====");
        Directory.CreateDirectory("Logs");
        File.WriteAllText(ResultPath, sb.ToString());
        if (Application.isBatchMode) EditorApplication.Exit(ok ? 0 : 2);
    }

    public static void RestoreAll()
    {
        var sb = new StringBuilder();
        if (!Directory.Exists(BackupDir)) { Debug.LogWarning($"{Tag} 无备份"); EditorApplication.Exit(3); return; }
        int n = 0;
        foreach (var bak in Directory.GetFiles(BackupDir, "*.mat"))
        {
            var found = AssetDatabase.FindAssets(Path.GetFileNameWithoutExtension(bak) + " t:Material",
                new[] { TreeRoot }).Select(AssetDatabase.GUIDToAssetPath).FirstOrDefault();
            if (found == null) continue;
            File.Copy(bak, found, true);
            n++;
        }
        AssetDatabase.Refresh();
        sb.AppendLine($"还原 {n} 个材质");
        File.WriteAllText(ResultPath, sb.ToString());
        if (Application.isBatchMode) EditorApplication.Exit(0);
    }
}
