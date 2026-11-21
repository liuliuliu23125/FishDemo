using System;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

public static class LilyModelProbe
{
    private const string Tag = "[LilyModelProbe]";
    private const string OutDir = "Logs/Water";
    private const string ResultPath = OutDir + "/lily-probe.txt";

    private static readonly string[] Models =
    {
        "Assets/WaterPlants/Source/SwampForest/Models/swamp_lily_Pink01.fbx",
        "Assets/WaterPlants/Source/SwampForest/Models/swamp_lily_Pink02.fbx",
        "Assets/WaterPlants/Source/SwampForest/Models/swamp_lily_White03.fbx",
        "Assets/WaterPlants/Source/SwampForest/Models/swamp_lily_White04.fbx",
    };

    public static void Run()
    {
        var sb = new StringBuilder();
        void W(string s)
        {
            Debug.Log($"{Tag} {s}");
            sb.AppendLine(s);
        }

        try
        {
            Directory.CreateDirectory(OutDir);
            foreach (var path in Models)
            {
                var root = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (root == null)
                {
                    W($"MISSING {path}");
                    continue;
                }

                var filters = root.GetComponentsInChildren<MeshFilter>(true);
                var bounds = new Bounds();
                bool first = true;
                int verts = 0, tris = 0, subMeshes = 0;
                foreach (var filter in filters)
                {
                    var mesh = filter.sharedMesh;
                    if (mesh == null) continue;
                    verts += mesh.vertexCount;
                    tris += (int)(mesh.GetIndexCount(0) / 3);
                    subMeshes += mesh.subMeshCount;
                    var matrix = root.transform.worldToLocalMatrix * filter.transform.localToWorldMatrix;
                    var meshBounds = mesh.bounds;
                    for (int i = 0; i < 8; i++)
                    {
                        var corner = new Vector3(
                            (i & 1) == 0 ? meshBounds.min.x : meshBounds.max.x,
                            (i & 2) == 0 ? meshBounds.min.y : meshBounds.max.y,
                            (i & 4) == 0 ? meshBounds.min.z : meshBounds.max.z);
                        var p = matrix.MultiplyPoint3x4(corner);
                        if (first) { bounds = new Bounds(p, Vector3.zero); first = false; }
                        else bounds.Encapsulate(p);
                    }
                }

                W($"{Path.GetFileName(path)} filters={filters.Length} verts={verts} tris={tris} subMeshes={subMeshes}");
                W($"  center={bounds.center} size={bounds.size} min={bounds.min} max={bounds.max}");
            }

            W("==== 结论：PASS ====");
            File.WriteAllText(ResultPath, sb.ToString());
            if (Application.isBatchMode) EditorApplication.Exit(0);
        }
        catch (Exception ex)
        {
            W($"异常：{ex}");
            W("==== 结论：FAIL ====");
            File.WriteAllText(ResultPath, sb.ToString());
            if (Application.isBatchMode) EditorApplication.Exit(1);
        }
    }
}
