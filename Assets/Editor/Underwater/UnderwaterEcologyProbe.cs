using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

public static class UnderwaterEcologyProbe
{
    private const string OutDir = "Logs/Underwater";
    private const string ResultPath = OutDir + "/underwater-probe.txt";
    private const string UwRoot = "Assets/Underwater world - Coral Reef & Marine Life";
    private const string OcRoot = "Assets/Davis3D/OceanEnvironmentPack";

    public static void Run()
    {
        var sb = new StringBuilder();
        void W(string s) { Debug.Log($"[UnderwaterProbe] {s}"); sb.AppendLine(s); }

        try
        {
            Directory.CreateDirectory(OutDir);
            var prefabs = new List<string>();
            foreach (var n in new[] { "Coral_1", "Coral_2", "Coral_6", "Coral_7", "Coral_8", "Coral_9", "Rock_2", "Ship" })
                prefabs.Add($"{UwRoot}/Coral Reef/Prefabs/{n}.prefab");
            foreach (var n in new[] { "Prefab_Kelp_A", "Prefab_Kelp_B", "Prefab_SeaWeed_Multi", "Prefab_SeaWeed_Single", "Prefab_Anemone", "Prefab_Coral_A", "Prefab_Coral_B", "Prefab_Shell" })
                prefabs.Add($"{OcRoot}/Prefabs/{n}.prefab");
            foreach (var n in new[] { "Prefab_SeaSponge_Group_Large_A", "Prefab_SeaSponge_Group_Large_B", "Prefab_SeaSponge_Group_Small" })
                prefabs.Add($"{OcRoot}/Prefabs/SeaSponge/{n}.prefab");

            long total = 0;
            foreach (var path in prefabs)
            {
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (prefab == null) { W($"MISSING {path}"); continue; }

                long lod0 = LOD0Triangles(prefab);
                total += lod0;
                W($"{Path.GetFileNameWithoutExtension(path)}LOD0={lod0}");
            }
            W($"staticSum={total}");

            foreach (var path in new[] { "Coral_Fish_1", "Coral_Fish_2", "Coral_Fish_4", "Leatherback_turtle", "Beige_stingray" })
            {
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>($"{UwRoot}/Marine Life/Prefabs/{path}.prefab");
                if (prefab == null) continue;
                W($"{path}LOD0={LOD0Triangles(prefab)}");
            }
            foreach (var path in new[] { "SM_Bluetang.FBX", "SM_Medaka.FBX", "SM_Jellyfish.FBX" })
            {
                var meshes = AssetDatabase.LoadAllAssetsAtPath($"{OcRoot}/Meshes/Creatures/{path}").OfType<Mesh>().ToArray();
                foreach (var m in meshes)
                    W($"{path}:{m.name} tris={m.triangles.Length / 3} verts={m.vertexCount} uv1={m.uv2.Length}");
            }

            ProbeGtsHeight(W);
            ProbeSceneMaterials(W);

            W("==== 结论：PASS ====");
            Flush(sb, 0);
        }
        catch (Exception ex)
        {
            W($"异常：{ex}");
            Flush(sb, 1);
        }
    }

    private static void ProbeGtsHeight(Action<string> W)
    {
        UnityEditor.SceneManagement.EditorSceneManager.OpenScene("Assets/Scenes/World.unity",
            UnityEditor.SceneManagement.OpenSceneMode.Single);
        ChunkLoadScope.LoadRelevant(ChunkLoadScope.Area.FromLake(20f), s => W(s));

        float cx = 192f, cz = 246f;
        foreach (var t in UnityEngine.Object.FindObjectsOfType<Terrain>())
        {
            var p = t.transform.position;
            var size = t.terrainData.size;
            if (cx < p.x || cx > p.x + size.x || cz < p.z || cz > p.z + size.z) continue;

            float terrainY = p.y + t.SampleHeight(new Vector3(cx, 0f, cz));
            float best = float.MaxValue;
            float bestY = float.NaN;
            var root = t.transform.Find("GTS_Mesh");
            if (root != null)
            {
                foreach (var mf in root.GetComponentsInChildren<MeshFilter>(true))
                {
                    var m = mf.sharedMesh;
                    if (m == null) continue;
                    var verts = m.vertices;
                    var mw = mf.transform.localToWorldMatrix;
                    for (int i = 0; i < verts.Length; i += 4)
                    {
                        var wp = mw.MultiplyPoint3x4(verts[i]);
                        float dx = wp.x - cx, dz = wp.z - cz;
                        float d = dx * dx + dz * dz;
                        if (d < best) { best = d; bestY = wp.y; }
                    }
                }
            }
            W($"GTS 采样 ({cx},{cz})：terrainY={terrainY:F2} gtsY={bestY:F2} delta={(bestY - terrainY):F2} terrain={t.name}");
        }
    }

    private static void ProbeSceneMaterials(Action<string> W)
    {
        UnityEditor.SceneManagement.EditorSceneManager.OpenScene("Assets/Scenes/World.unity",
            UnityEditor.SceneManagement.OpenSceneMode.Single);
        var root = GameObject.Find("Underwater Ecology");
        if (root == null) { W("no underwater ecology"); return; }
        var benthic = root.transform.Find("Benthic");
        if (benthic == null) return;

        foreach (Transform child in benthic)
        {
            if (!(child.name.Contains("Ship") || child.name.Contains("Kelp")
                  || child.name.Contains("Coral") || child.name.Contains("Sponge")
                  || child.name.Contains("SeaWeed") || child.name.Contains("Anemone")
                  || child.name.Contains("Rock"))) continue;

            var renderers = child.GetComponentsInChildren<Renderer>(true);
            foreach (var r in renderers)
            {
                var m = r.sharedMaterial;
                W($"{child.name}/{r.name}: mat={(m != null ? m.name : "null")} " +
                  $"shader={(m != null && m.shader != null ? m.shader.name : "null")} " +
                  $"bounds={r.bounds.center.ToString("F1")}/{r.bounds.size.ToString("F1")}");
            }
            W($"  transform pos={child.position.ToString("F2")} scale={child.localScale.ToString("F2")}");
        }
    }

    private static long LOD0Triangles(GameObject prefab)
    {
        var go = UnityEngine.Object.Instantiate(prefab);
        long sum = 0;
        var counted = new HashSet<Renderer>();
        foreach (var lg in go.GetComponentsInChildren<LODGroup>(true))
        {
            var lods = lg.GetLODs();
            if (lods.Length == 0) continue;
            foreach (var r in lods[0].renderers) counted.Add(r);
        }
        foreach (var r in go.GetComponentsInChildren<Renderer>(true))
        {
            if (r.GetComponentInParent<LODGroup>() != null && !counted.Contains(r)) continue;
            Mesh mesh = null;
            if (r is SkinnedMeshRenderer smr) mesh = smr.sharedMesh;
            else if (r.TryGetComponent<MeshFilter>(out var filter)) mesh = filter.sharedMesh;
            if (mesh != null) sum += mesh.triangles.Length / 3;
        }
        UnityEngine.Object.DestroyImmediate(go);
        return sum;
    }

    private static void Flush(StringBuilder sb, int code)
    {
        Directory.CreateDirectory(OutDir);
        File.WriteAllText(ResultPath, sb.ToString());
        EditorApplication.Exit(code);
    }
}
