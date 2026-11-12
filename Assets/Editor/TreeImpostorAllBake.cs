using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;
using AmplifyImpostors;

/// <summary>
/// 把地形上实际在用的 4 个云杉原型批量烘 impostor，
/// 并把 LOD 结构改成「LOD0 保留原 mesh，其余合并成一级 impostor」。
///
/// ── 为什么用 ReplaceAllExceptFirst ──
/// 用户要求 LOD0 保留原 mesh（近景必须真几何），中远景换 impostor。
/// 这个模式在 AmplifyImpostor.cs:1457 的实现是：
///   1. 把 LOD1..N 的 renderer 全部 disabled；
///   2. Array.Resize(ref lods, 2) —— 压成 2 级；
///   3. 第 2 级（索引 1）放 impostor renderer，切换高度沿用原最远那一级的值。
/// 结果：4 级 → 2 级，提交次数减少，且 LOD0 不受影响。
///
/// ── 为什么要先烘再改结构 ──
/// 烘焙需要原始 LOD0 的完整几何作为取样源。
/// 若先改结构，LOD1~3 已被禁用，烘焙会缺少中远景信息。
///
/// ── 幂等性 ──
/// 已烘过的（有 ImpostorAsset 且组件已存在）会跳过烘焙，只校正 LOD 结构，
/// 所以可以重复执行。
///
/// 用法：-executeMethod TreeImpostorAllBake.Run
/// </summary>
public static class TreeImpostorAllBake
{
    private const string Tag = "[ImpostorAll]";
    private const string ResultPath = "Logs/tree-impostor-all.txt";
    private const string PrefabDir =
        "Assets/Procedural Worlds/Packages - Install/Gaia Pro Assets and Biomes/Prefabs/Trees";

    /// <summary>地形上实际在用的 4 个云杉原型（合计 464,810 / 464,843 实例）。</summary>
    private static readonly string[] Targets =
    {
        "PW_Tree_Spruce_02",      // 201,031
        "PW_Tree_Spruce_06",      // 131,574
        "PW_Tree_Spruce_04",      //  86,458
        "PW_Tree_Spruce_07_Hero", //  45,747
    };

    public static void Run()
    {
        var sb = new StringBuilder();
        void W(string s) { Debug.Log($"{Tag} {s}"); sb.AppendLine(s); }

        W($"目标原型 = {Targets.Length}");
        int baked = 0, skippedBake = 0, restructured = 0, failed = 0;

        foreach (var name in Targets)
        {
            string path = $"{PrefabDir}/{name}.prefab";
            if (!File.Exists(path)) { W($"跳过：找不到 {path}"); failed++; continue; }

            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (prefab == null) { W($"跳过：加载失败 {path}"); failed++; continue; }

            var lg = prefab.GetComponentInChildren<LODGroup>(true);
            if (lg == null) { W($"跳过：{name} 没有 LODGroup"); failed++; continue; }

            W("");
            W($"===== {name} =====");
            W($"  烘焙前：{lg.GetLODs().Length} 级");

            // ── 第 1 步：确保有 AmplifyImpostor 组件并绑定 LODGroup ──
            var root = PrefabUtility.LoadPrefabContents(path);
            bool needSave = false;
            try
            {
                var lgIn = root.GetComponentInChildren<LODGroup>(true);
                var comp = root.GetComponent<AmplifyImpostor>();
                if (comp == null)
                {
                    comp = root.AddComponent<AmplifyImpostor>();
                    needSave = true;
                    W("  已添加 AmplifyImpostor 组件");
                }
                if (comp.LodGroup == null) { comp.LodGroup = lgIn; needSave = true; }

                // 关键：替换模式改成"保留 LOD0，其余合并成一级 impostor"
                if (comp.m_lodReplacement != LODReplacement.ReplaceAllExceptFirst)
                {
                    comp.m_lodReplacement = LODReplacement.ReplaceAllExceptFirst;
                    needSave = true;
                    W($"  m_lodReplacement = {comp.m_lodReplacement}");
                }

                if (needSave) PrefabUtility.SaveAsPrefabAsset(root, path);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
            AssetDatabase.Refresh();

            // ── 第 2 步：烘焙（已烘过的跳过）──
            prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            var ai = prefab.GetComponent<AmplifyImpostor>();
            bool alreadyBaked = ai != null && ai.Data != null;
            if (alreadyBaked)
            {
                W($"  已有 ImpostorAsset = {ai.Data.name}，跳过烘焙");
                skippedBake++;
            }
            else
            {
                int n = 0;
                try
                {
                    n = AmplifyImpostorBakeAPI.BakeImpostorsAtPaths(
                        new[] { path }, autoCreate: true, autoCreateFolder: "Assets/ImpostorBaked");
                    W($"  烘焙提交数 = {n}");
                }
                catch (System.Exception e)
                {
                    W($"  烘焙异常：{e.GetType().Name}: {e.Message}");
                    failed++;
                    continue;
                }
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
                if (n > 0) baked++; else { failed++; continue; }
            }

            // ── 第 3 步：确认/修正 LOD 结构 ──
            // 已烘过的 prefab（Spruce_06）不会被重新烘焙，
            // 而 m_lodReplacement 只在烘焙时被读取应用，所以它的结构还是原来的 4 级。
            // 这里不调任何内部 API，直接按 ReplaceAllExceptFirst 的语义手改 LODGroup：
            //   1. 关掉 LOD1..N 的所有 renderer；
            //   2. 数组压成 2 级；
            //   3. 第 2 级放已存在的 impostor renderer，切换高度沿用原最远那级。
            // 手改比猜测包内 API 更可控，且逻辑与 AmplifyImpostor.cs:1457 的实现一致。
            prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            var lgAfter = prefab.GetComponentInChildren<LODGroup>(true);
            if (lgAfter == null) { W("  改后找不到 LODGroup"); failed++; continue; }

            if (lgAfter.GetLODs().Length != 2)
            {
                W($"  当前仍是 {lgAfter.GetLODs().Length} 级，手动压成 2 级");
                var root2 = PrefabUtility.LoadPrefabContents(path);
                try
                {
                    var lgIn = root2.GetComponentInChildren<LODGroup>(true);
                    var lodsIn = lgIn.GetLODs();

                    // 找已烘好的 impostor renderer：父物体名通常是 "Impostor"
                    Renderer impostorRenderer = null;
                    foreach (var r in root2.GetComponentsInChildren<Renderer>(true))
                    {
                        if (r == null || r.sharedMaterial == null) continue;
                        if (r.sharedMaterial.shader != null &&
                            r.sharedMaterial.shader.name.Contains("Amplify Impostors"))
                        {
                            impostorRenderer = r;
                            break;
                        }
                    }
                    if (impostorRenderer == null)
                    {
                        W("  找不到 impostor renderer，跳过结构修改");
                        PrefabUtility.UnloadPrefabContents(root2);
                        failed++;
                        continue;
                    }

                    for (int i = lodsIn.Length - 1; i > 0; i--)
                        foreach (var r in lodsIn[i].renderers)
                            if (r != null) r.enabled = false;

                    float lastTransition = lodsIn[lodsIn.Length - 1].screenRelativeTransitionHeight;
                    System.Array.Resize(ref lodsIn, 2);
                    lodsIn[1].screenRelativeTransitionHeight = lastTransition;
                    lodsIn[1].renderers = new Renderer[] { impostorRenderer };
                    lgIn.SetLODs(lodsIn);

                    PrefabUtility.SaveAsPrefabAsset(root2, path);
                }
                finally
                {
                    PrefabUtility.UnloadPrefabContents(root2);
                }
                AssetDatabase.Refresh();
                prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                lgAfter = prefab.GetComponentInChildren<LODGroup>(true);
            }

            if (lgAfter == null) { W("  仍找不到 LODGroup"); failed++; continue; }

            var lods = lgAfter.GetLODs();
            W($"  烘焙后：{lods.Length} 级");
            for (int i = 0; i < lods.Length; i++)
            {
                var r = lods[i].renderers.FirstOrDefault();
                string mn = (r != null && r.sharedMaterial != null) ? r.sharedMaterial.name : "(null)";
                W($"    LOD{i} h={lods[i].screenRelativeTransitionHeight:F4} " +
                  $"renderer={r?.name ?? "null"} mat={mn}");
            }

            if (lods.Length == 2) restructured++;
            else W($"  注意：级数不是 2（是 {lods.Length}）");
        }

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        W("");
        W($"=== 汇总 ===");
        W($"新烘焙 = {baked}，已有跳过 = {skippedBake}，已压成 2 级 = {restructured}，失败 = {failed}");
        bool ok = failed == 0 && restructured == Targets.Length;
        W($"==== 结论：{(ok ? "PASS" : "FAIL")} ====");

        Directory.CreateDirectory("Logs");
        File.WriteAllText(ResultPath, sb.ToString());
        if (Application.isBatchMode) EditorApplication.Exit(ok ? 0 : 2);
    }
}
