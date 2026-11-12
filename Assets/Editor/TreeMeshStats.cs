using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

/// <summary>
/// 逐个 mesh 对比：原树各级 LOD 的顶点/面数 vs impostor mesh 的顶点/面数。
///
/// ── 为什么要看这个 ──
/// impostor 的收益来自"用极少几何替代中远景的高模"。
/// 如果 impostor mesh 本身顶点数没降下来（例如仍沿用原 LOD 的网格），
/// 那这轮改造只是换了贴图，几何开销没变，收益要打折扣。
/// 这里把每个 LOD 级实际引用的 Mesh 资产找出来，读 vertexCount / 三角面数，
/// 用数据回答"到底降了多少"，而不是假设。
///
/// 用法：-executeMethod TreeMeshStats.Run
/// 输出 Logs/tree-mesh-stats.txt
/// </summary>
public static class TreeMeshStats
{
    private const string Tag = "[MeshStats]";
    private const string ResultPath = "Logs/tree-mesh-stats.txt";
    private const string PrefabDir =
        "Assets/Procedural Worlds/Packages - Install/Gaia Pro Assets and Biomes/Prefabs/Trees";

    private static readonly string[] Targets =
    {
        "PW_Tree_Spruce_02", "PW_Tree_Spruce_06",
        "PW_Tree_Spruce_04", "PW_Tree_Spruce_07_Hero",
    };

    public static void Run()
    {
        var sb = new StringBuilder();
        void W(string s) { Debug.Log($"{Tag} {s}"); sb.AppendLine(s); }

        W("单位：verts = 顶点数，tris = 三角面数（按 indexCount/3 计）");
        W("");

        foreach (var name in Targets)
        {
            string path = $"{PrefabDir}/{name}.prefab";
            if (!File.Exists(path)) { W($"跳过：找不到 {name}"); continue; }

            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (prefab == null) continue;
            var lg = prefab.GetComponentInChildren<LODGroup>(true);
            if (lg == null) { W($"{name}: 无 LODGroup"); continue; }

            W($"===== {name} =====");
            var lods = lg.GetLODs();
            long lod0Tris = 0, impTris = 0;

            for (int i = 0; i < lods.Length; i++)
            {
                W($"  LOD{i} h={lods[i].screenRelativeTransitionHeight:F4}  renderers={lods[i].renderers.Length}");
                foreach (var r in lods[i].renderers)
                {
                    if (r == null) { W("     (null renderer)"); continue; }

                    var mf = r.GetComponent<MeshFilter>();
                    var mesh = mf != null ? mf.sharedMesh : null;
                    if (mesh == null)
                    {
                        W($"     [{r.name}] MeshFilter/Mesh 为空（可能是 impostor 运行时生成）");
                        continue;
                    }

                    long tris = 0;
                    for (int sm = 0; sm < mesh.subMeshCount; sm++)
                        tris += (long)mesh.GetIndexCount(sm) / 3;

                    // 多材质槽时 mesh 是共享的，这里按槽数说明
                    string matNames = string.Join("/", r.sharedMaterials
                        .Where(m => m != null).Select(m => m.name));

                    W($"     [{r.name}] mesh={mesh.name} verts={mesh.vertexCount:N0} tris={tris:N0} " +
                      $"subMesh={mesh.subMeshCount} mats={matNames}");

                    if (i == 0) lod0Tris += tris;

                    // 判断是不是 impostor：材质 shader 名里带 Amplify Impostors
                    bool isImp = r.sharedMaterials.Any(m => m != null && m.shader != null &&
                                                            m.shader.name.Contains("Amplify Impostors"));
                    if (isImp) impTris += tris;
                }
            }

            if (lod0Tris > 0 && impTris > 0)
            {
                W($"  → LOD0 合计 tris={lod0Tris:N0}，impostor tris={impTris:N0}，" +
                  $"降幅 {(1.0 - (double)impTris / lod0Tris) * 100:F1}%");
            }
            else if (impTris == 0)
            {
                W("  → 未检测到 impostor 的 mesh（可能是运行时按 AmplifyImpostorAsset 生成）");
            }
            W("");
        }

        // 单独把 impostor 的 AmplifyImpostorAsset 里记录的 mesh 信息打出来
        W("===== Impostor 资产内的 mesh 引用 =====");
        foreach (var p in AssetDatabase.FindAssets("t:ScriptableObject", new[] { "Assets" })
                     .Select(AssetDatabase.GUIDToAssetPath)
                     .Where(x => x.Contains("Impostor") && x.EndsWith(".asset"))
                     .OrderBy(x => x))
        {
            var obj = AssetDatabase.LoadAssetAtPath<ScriptableObject>(p);
            if (obj == null) continue;
            var t = obj.GetType();
            if (!t.Name.Contains("Impostor")) continue;

            W($"  {p}  (type={t.Name})");
            var meshField = t.GetField("m_mesh", System.Reflection.BindingFlags.Public |
                                                        System.Reflection.BindingFlags.NonPublic |
                                                        System.Reflection.BindingFlags.Instance);
            var meshProp = t.GetProperty("Mesh", System.Reflection.BindingFlags.Public |
                                                    System.Reflection.BindingFlags.Instance);
            var mesh = (meshField != null ? meshField.GetValue(obj) as Mesh : null)
                       ?? (meshProp != null ? meshProp.GetValue(obj) as Mesh : null);
            if (mesh != null)
            {
                long tris = 0;
                for (int sm = 0; sm < mesh.subMeshCount; sm++) tris += (long)mesh.GetIndexCount(sm) / 3;
                W($"     mesh={mesh.name} verts={mesh.vertexCount} tris={tris} subMesh={mesh.subMeshCount}");
            }
            else W("     (资产内无 mesh，运行时生成)");
        }

        Directory.CreateDirectory("Logs");
        File.WriteAllText(ResultPath, sb.ToString());
        if (Application.isBatchMode) EditorApplication.Exit(0);
    }
}
