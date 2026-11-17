using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

public static class DetailMeshProbe
{
    private const string Tag = "[DetailMeshProbe]";
    private const string FbxDir =
        "Assets/Procedural Worlds/Packages - Install/Gaia Pro Assets and Biomes/Content Resources/Terrain Details/Mesh";
    private const string ResultPath = "Logs/detail-mesh-probe.txt";

    private static readonly string[] Names =
    {
        "PW_WildGrass_General_Lod1", "PW_LawnGrass_DeadPatch",
        "PW_WildGrass_Understory_Foliage", "PW_WildGrass_Understory_Sticks",
        "PW_LawnGrass_CloverFlower", "PW_LawnGrass_Daisy", "PW_LawnGrass_Dandelions",
    };

    public static void Run()
    {
        var sb = new StringBuilder();
        void W(string s) { sb.AppendLine(s); Debug.Log($"{Tag} {s}"); }

        var loadedByName = Resources.FindObjectsOfTypeAll<Mesh>()
            .Where(m => m != null && !string.IsNullOrEmpty(m.name))
            .GroupBy(m => m.name)
            .ToDictionary(g => g.Key, g => g.First());
        W($"Resources.FindObjectsOfTypeAll<Mesh> 可见 mesh 数 = {loadedByName.Count}");

        foreach (var n in Names)
        {
            W("");
            W($"-- {n} --");
            W($"  [2] 已加载对象中存在: {loadedByName.ContainsKey(n)}");

            string fbx = $"{FbxDir}/{n}.fbx";
            var assets = AssetDatabase.LoadAllAssetsAtPath(fbx);
            var meshes = assets.OfType<Mesh>().ToArray();
            W($"  [1] AssetDatabase 加载 {fbx}");
            W($"      子资源数 = {assets.Length}，其中 Mesh = {meshes.Length}");
            foreach (var m in meshes.Take(4))
                W($"      mesh: name={m.name} verts={m.vertexCount} tris={m.triangles.Length / 3}");

            bool foundAfter = Resources.FindObjectsOfTypeAll<Mesh>().Any(m => m != null && m.name == n);
            W($"  [3] AssetDatabase 加载后再查 FindObjectsOfTypeAll: {foundAfter}");

            if (meshes.Length > 0)
            {
                var preferred = meshes.FirstOrDefault(m => m.name == n) ?? meshes[0];
                W($"      → 应使用: {preferred.name}（{preferred.vertexCount} 顶点 / " +
                  $"{preferred.triangles.Length / 3} tri）");
            }
        }

        Directory.CreateDirectory("Logs");
        File.WriteAllText(ResultPath, sb.ToString());
        EditorApplication.Exit(0);
    }
}
