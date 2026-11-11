using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

/// <summary>
/// 检查 detail 原型 mesh 能否被运行时路径找到。
///
/// 背景：GrassBRGManager 用 Resources.FindObjectsOfTypeAll&lt;Mesh&gt;() 按名字找原型 mesh，
/// 实测全部落空（LOD0 三角形 = 24，说明用的是程序化兜底草簇）。
/// 本脚本对比三条路径，确定哪条才是可靠的：
///   1. AssetDatabase.LoadAssetAtPath（编辑器专用，确定可靠）
///   2. Resources.FindObjectsOfTypeAll&lt;Mesh&gt;()（运行时可用，但只找已加载对象）
///   3. 先经 AssetDatabase 强制加载，再看 (2) 能否命中
/// </summary>
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

        // 路径 2：直接扫已加载对象
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

            // 路径 1：AssetDatabase 按路径加载（FBX 里的 mesh 名可能带层级前缀）
            string fbx = $"{FbxDir}/{n}.fbx";
            var assets = AssetDatabase.LoadAllAssetsAtPath(fbx);
            var meshes = assets.OfType<Mesh>().ToArray();
            W($"  [1] AssetDatabase 加载 {fbx}");
            W($"      子资源数 = {assets.Length}，其中 Mesh = {meshes.Length}");
            foreach (var m in meshes.Take(4))
                W($"      mesh: name={m.name} verts={m.vertexCount} tris={m.triangles.Length / 3}");

            // 路径 3：加载后再查 FindObjectsOfTypeAll
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
