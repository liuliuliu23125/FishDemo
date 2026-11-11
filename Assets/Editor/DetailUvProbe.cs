using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

/// <summary>
/// 诊断 detail 原型 mesh 的 UV 是否与图集匹配。
///
/// 背景：给 GrassBRG.shader 加 albedo 图集采样后，花的颜色应来自图集像素，
/// 但截图显示草变成碎片状、前景有大片纯色块 —— 典型「UV 采错区域」的表现。
/// 需要确认：
///   1. 原型 mesh 到底有没有 UV0（没有的话采样恒为同一个点）；
///   2. UV 的实际范围（是否落在 [0,1]，是否只占图集某一小块）；
///   3. 材质槽数量（多材质槽时 submeshIndex=0 只会画第一个）。
///
/// 用法：-executeMethod DetailUvProbe.Run
/// 结果写 Logs/detail-uv-probe.txt
/// </summary>
public static class DetailUvProbe
{
    private const string Tag = "[DetailUvProbe]";
    private const string ResultPath = "Logs/detail-uv-probe.txt";
    private const string FbxDir =
        "Assets/Procedural Worlds/Packages - Install/Gaia Pro Assets and Biomes/Content Resources/Terrain Details/Mesh";

    private static readonly string[] Names =
    {
        "PW_WildGrass_General_Lod1",
        "PW_LawnGrass_DeadPatch",
        "PW_WildGrass_Understory_Foliage",
        "PW_WildGrass_Understory_Sticks",
        "PW_LawnGrass_CloverFlower",
        "PW_LawnGrass_Daisy",
        "PW_LawnGrass_Dandelions",
    };

    public static void Run()
    {
        var sb = new StringBuilder();
        void W(string s) { Debug.Log($"{Tag} {s}"); sb.AppendLine(s); }

        for (int i = 0; i < Names.Length; i++)
        {
            string fbx = $"{FbxDir}/{Names[i]}.fbx";
            var all = AssetDatabase.LoadAllAssetsAtPath(fbx);
            var mesh = all.OfType<Mesh>().FirstOrDefault();
            W("");
            W($"===== [{i}] {Names[i]} =====");
            if (mesh == null) { W("  找不到 mesh"); continue; }

            W($"  顶点 {mesh.vertexCount} / 三角形 {mesh.triangles.Length / 3}");
            W($"  subMesh 数 = {mesh.subMeshCount}");
            for (int s = 0; s < mesh.subMeshCount; s++)
                W($"    subMesh[{s}] indexCount = {mesh.GetIndexCount(s)}");

            // UV0 是否存在 + 实际范围
            var uvs = mesh.uv;
            W($"  uv0 数量 = {uvs.Length}");
            if (uvs.Length == 0)
            {
                W("  → 没有 UV0！采样恒为 (0,0) 附近，必须用别的映射");
            }
            else
            {
                float uMin = float.MaxValue, uMax = float.MinValue;
                float vMin = float.MaxValue, vMax = float.MinValue;
                foreach (var uv in uvs)
                {
                    if (uv.x < uMin) uMin = uv.x;
                    if (uv.x > uMax) uMax = uv.x;
                    if (uv.y < vMin) vMin = uv.y;
                    if (uv.y > vMax) vMax = uv.y;
                }
                W($"  UV 范围 u[{uMin:F3}, {uMax:F3}] v[{vMin:F3}, {vMax:F3}]");
                W($"  UV 跨度 u={uMax - uMin:F3} v={vMax - vMin:F3}" +
                  ((uMax - uMin) < 0.9f || (vMax - vMin) < 0.9f
                      ? "  → 只占图集一部分（必须配对应区域采样）"
                      : "  → 接近整张图集"));
                // 前 6 个顶点的 UV，便于肉眼比对
                var head = new StringBuilder("  前 6 UV: ");
                for (int k = 0; k < Mathf.Min(6, uvs.Length); k++)
                    head.Append($"({uvs[k].x:F3},{uvs[k].y:F3}) ");
                W(head.ToString());
            }

            // 网格包围盒（判断比例是否合理）
            var b = mesh.bounds;
            W($"  bounds size = ({b.size.x:F2}, {b.size.y:F2}, {b.size.z:F2})");
        }

        // 图集本身的信息
        W("");
        W("===== 图集 =====");
        string texDir = FbxDir.Replace("/Mesh", "/Textures");
        foreach (var n in new[] { "PW_LawnGrass_00_D", "PW_WildGrass_00_D" })
        {
            var tex = AssetDatabase.LoadAssetAtPath<Texture2D>($"{texDir}/{n}.png");
            if (tex == null) { W($"{n}: 找不到"); continue; }
            var imp = AssetImporter.GetAtPath($"{texDir}/{n}.png") as TextureImporter;
            W($"{n}: {tex.width}x{tex.height} wrap={imp?.wrapMode} " +
              $"sRGB={imp?.sRGBTexture} maxSize={imp?.maxTextureSize}");
        }

        Directory.CreateDirectory("Logs");
        File.WriteAllText(ResultPath, sb.ToString());
        EditorApplication.Exit(0);
    }
}
