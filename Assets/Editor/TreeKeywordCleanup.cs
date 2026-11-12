using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

/// <summary>
/// 去掉树材质上无效的 _PW_SF_COVER_ON keyword。
///
/// ── 为什么这是"无效"，不是"可选" ──
/// 逐材质实测（92 个树材质）：
///   69 个启用了 _PW_SF_COVER_ON，
///   但全部是 _PW_Cover=0 且 _PW_CoverLayer0Progress=0，
///   其中绝大多数连 _PW_CoverLayer0 贴图都没设。
/// 看 PW_GeneralFuncs.cginc 的 CoverSurface：
///   amount = progress * min(worldY² + wrap, 1) * alpha
///   o_albedo = lerp(i_albedo, albedo, amount)
/// progress=0 → amount=0 → lerp 结果等于原 albedo，**完全不改变画面**。
/// 也就是说这 69 个材质白扛了一个 shader 变体。
///
/// ── 去掉能带来什么 ──
/// 实测在用的 shader+keyword 组合从 5 种降到 3 种
/// （树干 / 树叶 / billboard 各一种）。
/// 变体越少，SRP Batcher 越容易把不同树合进同一批，
/// 也少一份 shader 编译与包体。
///
/// ── 为什么保留 SSS / WIND / WORLDMAP / ALPHATEST / BILLBOARD ──
/// 这五个都在真实生效：
///   ALPHATEST 叶片镂空、SSS 背光透光、WIND 摆动、
///   WORLDMAP 世界色变（48 个材质都设了贴图）、BILLBOARD 面片朝向。
/// 只有 COVER 是空转。删有效的会让树失去镂空或透光，不能动。
///
/// 用法：
///   -executeMethod TreeKeywordCleanup.Run         去掉 COVER
///   -executeMethod TreeKeywordCleanup.RestoreAll  还原
/// </summary>
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

            // 安全检查：只有确实没在用的才删。
            // 判据是 **progress**，不是贴图：CoverSurface 里
            //   amount = progress * min(worldY²+wrap,1) * alpha
            // progress 为 0 时 amount 恒为 0，无论贴图设没设都不会画出来。
            // （PW_Tree_Log_01 就属于"设了贴图但 progress=0"，同样是空转。）
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

            // 备份原始 .mat（只备份一次，避免覆盖真正的原件）
            string bak = Path.Combine(BackupDir, mat.name + ".mat");
            if (!File.Exists(bak)) File.Copy(path, bak);

            mat.DisableKeyword(TargetKeyword);
            EditorUtility.SetDirty(mat);
            changed++;
        }

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        // 复核：重新读取，确认 keyword 已消失。
        // 统计口径要分开：
        //   全量 keyword 组合里混着 _DISABLE_DECALS / _DISABLE_SSR_TRANSPARENT / _DOUBLESIDED_ON
        //   这些 URP 内置项，它们不随材质角色变化，把它们算进"变体数"会虚高。
        //   真正决定树能否合进同一批的是 _PW_SF_* + _ALPHATEST 这组。
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
            // 备份文件名就是材质名，按名字回到原路径
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
