using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

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
                var head = new StringBuilder("  前 6 UV: ");
                for (int k = 0; k < Mathf.Min(6, uvs.Length); k++)
                    head.Append($"({uvs[k].x:F3},{uvs[k].y:F3}) ");
                W(head.ToString());
            }

            var b = mesh.bounds;
            W($"  bounds size = ({b.size.x:F2}, {b.size.y:F2}, {b.size.z:F2})");
        }

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
