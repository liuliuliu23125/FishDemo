using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

/// <summary>
/// 修复 Cloud Spruce 系树材质的 albedo 丢失问题。
///
/// ── 症状与根因 ──
/// 地形上 99.99% 的树是 4 个 Spruce 原型（464,810 / 464,843 实例），
/// 它们的 Bark / Needles 材质 _MainTex 全为空引用（fileID: 0）。
/// ShaderGraph 里 albedo = SAMPLE(_MainTex)，空贴图取到白色，
/// 白 × _Color × 偏暖主光 = 画面上的「淡淡粉红」树。
///
/// 为什么贴图会空：这 10 个 .mat 的引用在入库时就是 fileID: 0
/// （git HEAD 版本同样如此），不是运行时被改坏的。
/// 贴图本身没丢，FBX 内记录的原始贴图名是：
///   PW_Tree_Spruce_D.png / PW_Tree_Spruce_Needles_02_D.png
/// 它们现在位于 Content Resources/Terrain Details/Textures/ 下，
/// 且导入设置（_N 为 linear、_D/_M 为 sRGB）与正常树材质完全一致。
///
/// ── 为什么只填引用、不复制文件 ──
/// 贴图已在工程内且被别的树共用；复制会产生 6 份 ~10MB 冗余，
/// 还要额外配 LFS。材质只需换 GUID 引用即可。
///
/// 为什么可回滚：脚本先备份被改的 .mat 到 Tools/tree-material-backup/，
/// 再改写。还原用 RestoreAll。
///
/// 用法：
///   Unity.exe -batchmode -projectPath <proj> -executeMethod TreeMaterialFix.Run
///   Unity.exe -batchmode -projectPath <proj> -executeMethod TreeMaterialFix.RestoreAll
/// 不要加 -quit，由脚本自己 Exit。
/// </summary>
public static class TreeMaterialFix
{
    private const string Tag = "[TreeMatFix]";
    private const string MatDir =
        "Assets/Procedural Worlds/Packages - Install/Gaia Pro Assets and Biomes/Content Resources/Trees/PW_Tree_Spruce";
    private const string TexDir =
        "Assets/Procedural Worlds/Packages - Install/Gaia Pro Assets and Biomes/Content Resources/Terrain Details/Textures";
    private const string BackupDir = "Tools/tree-material-backup";
    private const string ResultPath = "Logs/tree-material-fix.txt";

    /// <summary>材质名 -> (albedo 贴图名, 法线贴图名, 金属光滑贴图名)。null 表示该槽保持不填。</summary>
    private static readonly Dictionary<string, (string d, string n, string m)> Targets =
        new Dictionary<string, (string d, string n, string m)>
    {
        // Bark：树干，用 PW_Tree_Spruce_* 三张
        { "PW_Tree_Spruce_01_Bark",    ("PW_Tree_Spruce_D.png", "PW_Tree_Spruce_N.png", "PW_Tree_Spruce_M.png") },
        { "PW_Tree_Spruce_02_Bark",    ("PW_Tree_Spruce_D.png", "PW_Tree_Spruce_N.png", "PW_Tree_Spruce_M.png") },
        { "PW_Tree_Spruce_04_Bark",    ("PW_Tree_Spruce_D.png", "PW_Tree_Spruce_N.png", "PW_Tree_Spruce_M.png") },
        { "PW_Tree_Spruce_06_Bark",    ("PW_Tree_Spruce_D.png", "PW_Tree_Spruce_N.png", "PW_Tree_Spruce_M.png") },
        { "PW_Tree_Spruce_07_Bark",    ("PW_Tree_Spruce_D.png", "PW_Tree_Spruce_N.png", "PW_Tree_Spruce_M.png") },
        // Needles：针叶，用 Needles_02 三张
        { "PW_Tree_Spruce_01_Needles", ("PW_Tree_Spruce_Needles_02_D.png", "PW_Tree_Spruce_Needles_02_N.png", "PW_Tree_Spruce_Needles_02_M.png") },
        { "PW_Tree_Spruce_02_Needles", ("PW_Tree_Spruce_Needles_02_D.png", "PW_Tree_Spruce_Needles_02_N.png", "PW_Tree_Spruce_Needles_02_M.png") },
        { "PW_Tree_Spruce_04_Needles", ("PW_Tree_Spruce_Needles_02_D.png", "PW_Tree_Spruce_Needles_02_N.png", "PW_Tree_Spruce_Needles_02_M.png") },
        { "PW_Tree_Spruce_06_Needles", ("PW_Tree_Spruce_Needles_02_D.png", "PW_Tree_Spruce_Needles_02_N.png", "PW_Tree_Spruce_Needles_02_M.png") },
        { "PW_Tree_Spruce_07_Needles", ("PW_Tree_Spruce_Needles_02_D.png", "PW_Tree_Spruce_Needles_02_N.png", "PW_Tree_Spruce_Needles_02_M.png") },
    };

    public static void Run()
    {
        var sb = new StringBuilder();
        void W(string s) { Debug.Log($"{Tag} {s}"); sb.AppendLine(s); }

        Directory.CreateDirectory(BackupDir);
        int changed = 0, skipped = 0, failed = 0;

        foreach (var kv in Targets)
        {
            string matPath = $"{MatDir}/{kv.Key}.mat";
            if (!File.Exists(matPath)) { W($"跳过：找不到 {matPath}"); failed++; continue; }

            var mat = AssetDatabase.LoadAssetAtPath<Material>(matPath);
            if (mat == null) { W($"跳过：加载失败 {matPath}"); failed++; continue; }

            // 备份（只备份未备份过的，避免 Restore 时把"修好的"当成原件）
            string bak = Path.Combine(BackupDir, kv.Key + ".mat");
            if (!File.Exists(bak)) File.Copy(matPath, bak);
            else W($"已有备份，保留原备份：{bak}");

            bool any = false;

            var texD = LoadTex(kv.Value.d);
            if (texD != null && mat.GetTexture("_MainTex") != texD)
            {
                mat.SetTexture("_MainTex", texD);
                W($"{kv.Key}: _MainTex <- {kv.Value.d}");
                any = true;
            }

            var texN = LoadTex(kv.Value.n);
            if (texN != null && mat.GetTexture("_BumpMap") != texN)
            {
                mat.SetTexture("_BumpMap", texN);
                W($"{kv.Key}: _BumpMap <- {kv.Value.n}");
                any = true;
            }

            var texM = LoadTex(kv.Value.m);
            if (texM != null && mat.GetTexture("_MetallicGlossMap") != texM)
            {
                mat.SetTexture("_MetallicGlossMap", texM);
                W($"{kv.Key}: _MetallicGlossMap <- {kv.Value.m}");
                any = true;
            }

            if (any) { EditorUtility.SetDirty(mat); changed++; }
            else { W($"{kv.Key}: 已是目标状态，跳过"); skipped++; }
        }

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        W("");
        W($"改动 = {changed}，跳过 = {skipped}，失败 = {failed}");
        W($"==== 结论：{(failed == 0 ? "PASS" : "FAIL")} ====");
        Directory.CreateDirectory("Logs");
        File.WriteAllText(ResultPath, sb.ToString());
        if (Application.isBatchMode) EditorApplication.Exit(failed == 0 ? 0 : 2);
    }

    /// <summary>还原：把 Tools/tree-material-backup/ 下的 .mat 拷回去。</summary>
    public static void RestoreAll()
    {
        var sb = new StringBuilder();
        void W(string s) { Debug.Log($"{Tag} {s}"); sb.AppendLine(s); }

        if (!Directory.Exists(BackupDir)) { W($"没有备份目录 {BackupDir}"); EditorApplication.Exit(3); return; }

        int n = 0;
        foreach (var bak in Directory.GetFiles(BackupDir, "*.mat"))
        {
            string dst = $"{MatDir}/{Path.GetFileName(bak)}";
            File.Copy(bak, dst, true);
            W($"还原 {Path.GetFileName(bak)}");
            n++;
        }
        AssetDatabase.Refresh();
        W($"还原 {n} 个材质");
        File.WriteAllText(ResultPath, sb.ToString());
        if (Application.isBatchMode) EditorApplication.Exit(0);
    }

    private static Texture2D LoadTex(string file)
    {
        string p = $"{TexDir}/{file}";
        var t = AssetDatabase.LoadAssetAtPath<Texture2D>(p);
        if (t == null) Debug.LogWarning($"{Tag} 贴图加载失败：{p}");
        return t;
    }
}
