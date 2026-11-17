using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;
using AmplifyImpostors;

public static class TreeImpostorAllBake
{
    private const string Tag = "[ImpostorAll]";
    private const string ResultPath = "Logs/tree-impostor-all.txt";
    private const string PrefabDir =
        "Assets/Procedural Worlds/Packages - Install/Gaia Pro Assets and Biomes/Prefabs/Trees";

    private static readonly string[] Targets =
    {
        "PW_Tree_Spruce_02",
        "PW_Tree_Spruce_06",
        "PW_Tree_Spruce_04",
        "PW_Tree_Spruce_07_Hero",
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
