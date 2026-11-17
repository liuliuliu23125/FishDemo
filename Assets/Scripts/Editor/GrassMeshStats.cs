using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace FishDemo.EditorTools
{
    public static class GrassMeshStats
    {
        private const string Tag = "[GrassMesh]";
        private const string ResultPath = "Logs/Grass/mesh-stats.txt";
        private const string DetailMeshDir =
            "Assets/Procedural Worlds/Packages - Install/Gaia Pro Assets and Biomes/Content Resources/Terrain Details/Mesh";

        [MenuItem("FishDemo/Rendering/② 统计草 mesh 规模", false, 101)]
        public static void Run()
        {
            var sb = new StringBuilder();
            void W(string s) { Debug.Log($"{Tag} {s}"); sb.AppendLine(s); }

            string[] guids = AssetDatabase.FindAssets("t:Mesh", new[] { DetailMeshDir });
            W($"目录：{DetailMeshDir}");
            W($"mesh 资产数 = {guids.Length}");
            W("");
            W($"{"Name",-40} {"Verts",8} {"Tris",8} {"Bytes(V+Idx)",14}");

            int maxVerts = 0;
            string maxName = "";

            foreach (var guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                var mesh = AssetDatabase.LoadAssetAtPath<Mesh>(path);
                if (mesh == null)
                    continue;

                int verts = mesh.vertexCount;
                int tris = mesh.triangles.Length / 3;
                long bytes = (long)verts * 48 + (long)tris * 3 * 2;
                W($"{mesh.name,-40} {verts,8} {tris,8} {bytes,14:N0}");

                if (verts > maxVerts)
                {
                    maxVerts = verts;
                    maxName = mesh.name;
                }
            }

            W("");
            W($"最大顶点数：{maxName} = {maxVerts}");
            W($"若 2000 万实例全用该 mesh：顶点数 = {(long)maxVerts * 20_000_000:N0}");

            Directory.CreateDirectory(Path.GetDirectoryName(ResultPath));
            File.WriteAllText(ResultPath, sb.ToString());

            if (Application.isBatchMode)
                EditorApplication.Exit(0);
        }
    }
}
