using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using FishDemo.Rendering;

namespace FishDemo.EditorTools
{
    public static class DetailBrgSetup
    {
        private const string Tag = "[DetailBrgSetup]";
        private const string WorldScenePath = "Assets/Scenes/World.unity";
        private const string NodeName = "GrassBRG (M1)";
        private const string FbxDir =
            "Assets/Procedural Worlds/Packages - Install/Gaia Pro Assets and Biomes/Content Resources/Terrain Details/Mesh";
        private const string ResultPath = "Logs/Grass/detail-brg-setup.txt";

        private static readonly string[] DetailFbxNames =
        {
            "PW_WildGrass_General_Lod1",
            "PW_LawnGrass_DeadPatch",
            "PW_WildGrass_Understory_Foliage",
            "PW_WildGrass_Understory_Sticks",
            "PW_LawnGrass_CloverFlower",
            "PW_LawnGrass_Daisy",
            "PW_LawnGrass_Dandelions",
        };

        private const string TexDir =
            "Assets/Procedural Worlds/Packages - Install/Gaia Pro Assets and Biomes/Content Resources/Terrain Details/Textures";

        private static readonly string[] DetailTextureNames =
        {
            "PW_WildGrass_00_D",
            "PW_LawnGrass_00_D",
            "PW_WildGrass_00_D",
            "PW_WildGrass_00_D",
            "PW_LawnGrass_00_D",
            "PW_LawnGrass_00_D",
            "PW_LawnGrass_00_D",
        };

        private static readonly Rect[] DetailSpriteCells =
        {
            new Rect(0.6982f, 0.2334f, 0.1534f, 0.1553f),
            new Rect(0.6357f, 0.2480f, 0.1426f, 0.1094f),
            new Rect(0.4277f, 0.8457f, 0.1035f, 0.1270f),
            new Rect(0.6914f, 0.7510f, 0.0732f, 0.0888f),
            new Rect(0.8662f, 0.4756f, 0.1143f, 0.2402f),
            new Rect(0.0098f, 0.8770f, 0.1006f, 0.1054f),
            new Rect(0.1152f, 0.7666f, 0.0733f, 0.0762f),
        };

        private static readonly Color[] DetailTints =
        {
            new Color(1.00f, 1.00f, 1.00f),
            new Color(1.00f, 0.92f, 0.80f),
            new Color(1.00f, 1.00f, 1.00f),
            new Color(1.00f, 1.00f, 1.00f),
            new Color(1.00f, 1.00f, 1.00f),
            new Color(1.00f, 1.00f, 1.00f),
            new Color(1.00f, 1.00f, 1.00f),
        };

        [MenuItem("FishDemo/Rendering/⑮ 写入 detail 原型 mesh", false, 117)]
        public static void Run()
        {
            var sb = new StringBuilder();
            void W(string s) { Debug.Log($"{Tag} {s}"); sb.AppendLine(s); }

            var scene = EditorSceneManager.OpenScene(WorldScenePath, OpenSceneMode.Single);

            GrassBRGManager mgr = null;
            foreach (var root in scene.GetRootGameObjects())
            {
                if (root.name == NodeName) { mgr = root.GetComponent<GrassBRGManager>(); break; }
            }
            if (mgr == null)
            {
                W($"场景里找不到 {NodeName}");
                Flush(sb);
                EditorApplication.Exit(1);
                return;
            }

            var meshes = new Mesh[DetailFbxNames.Length];
            int ok = 0;
            for (int i = 0; i < DetailFbxNames.Length; i++)
            {
                string name = DetailFbxNames[i];
                string fbx = $"{FbxDir}/{name}.fbx";
                var all = AssetDatabase.LoadAllAssetsAtPath(fbx);
                var mesh = all.OfType<Mesh>().FirstOrDefault();
                if (mesh == null)
                {
                    W($"[{i}] {name}: 找不到 mesh（{fbx}）");
                    continue;
                }
                meshes[i] = mesh;
                ok++;
                W($"[{i}] {name} → mesh '{mesh.name}'（{mesh.vertexCount} 顶点 / " +
                  $"{mesh.triangles.Length / 3} tri / 高 {mesh.bounds.size.y:F2}m）");
            }

            var textures = new Texture2D[DetailTextureNames.Length];
            int texOk = 0;
            for (int i = 0; i < DetailTextureNames.Length; i++)
            {
                string texPath = $"{TexDir}/{DetailTextureNames[i]}.png";
                var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(texPath);
                if (tex == null)
                {
                    W($"[{i}] {DetailTextureNames[i]}: 找不到贴图（{texPath}）");
                    continue;
                }
                textures[i] = tex;
                texOk++;
                W($"[{i}] {DetailTextureNames[i]} → '{tex.name}'（{tex.width}x{tex.height}）");
            }

            var scales = BuildTypeScales(meshes, W);

            var bbMedium = LoadFirstMesh($"{FbxDir}/PW_DetailCrossPlanes.fbx");
            var bbLow = LoadFirstMesh($"{FbxDir}/PW_DetailQuad.fbx");
            if (bbMedium != null)
                W($"LOD1 billboard = '{bbMedium.name}'（{bbMedium.vertexCount} 顶点 / " +
                  $"{bbMedium.triangles.Length / 3} tri）");
            else
                W("警告：找不到 PW_DetailCrossPlanes，LOD1 会退回 LOD0 mesh");
            if (bbLow != null)
                W($"LOD2 billboard = '{bbLow.name}'（{bbLow.vertexCount} 顶点 / " +
                  $"{bbLow.triangles.Length / 3} tri）");
            else
                W("警告：找不到 PW_DetailQuad，LOD2 会退回 LOD0 mesh");

            mgr.ApplyDetailPrototypeSetup(meshes, textures, DetailTints,
                DetailSpriteCells, scales, bbMedium, bbLow, rebuild: false);

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            AssetDatabase.SaveAssets();

            W($"已写入 {ok}/{DetailFbxNames.Length} 个原型 mesh、" +
              $"{texOk}/{DetailTextureNames.Length} 张图集、" +
              $"{DetailSpriteCells.Length} 个精灵格、{scales.Length} 组尺寸并保存场景");
            Flush(sb);
            EditorApplication.Exit(0);
        }

        private static Vector3[] BuildTypeScales(Mesh[] meshes, System.Action<string> w)
        {
            var result = new Vector3[DetailFbxNames.Length];
            for (int i = 0; i < result.Length; i++)
            {
                float meshH = meshes[i] != null ? Mathf.Max(0.01f, meshes[i].bounds.size.y) : 1f;
                result[i] = new Vector3(1f, 1f, meshH);
            }

            DetailPrototype[] protos = null;
            foreach (var s in EditorBuildSettings.scenes)
            {
                if (!s.enabled || s.path.EndsWith("World.unity")) continue;
                if (!File.Exists(s.path)) continue;

                var sc = EditorSceneManager.OpenScene(s.path, OpenSceneMode.Additive);
                var terrain = sc.GetRootGameObjects()
                    .SelectMany(r => r.GetComponentsInChildren<Terrain>())
                    .FirstOrDefault(t => t != null && t.terrainData != null);
                var ps = terrain != null ? terrain.terrainData.detailPrototypes : null;
                EditorSceneManager.CloseScene(sc, true);

                if (ps != null && ps.Length >= DetailFbxNames.Length)
                {
                    protos = ps;
                    w($"尺寸来源：{Path.GetFileNameWithoutExtension(s.path)}（{ps.Length} 层）");
                    break;
                }
            }

            if (protos == null)
            {
                w("警告：找不到含 detailPrototypes 的 chunk，尺寸退回 1.0 倍");
                return result;
            }

            for (int i = 0; i < result.Length && i < protos.Length; i++)
            {
                var p = protos[i];
                float midW = (p.minWidth + p.maxWidth) * 0.5f;
                float midH = (p.minHeight + p.maxHeight) * 0.5f;
                result[i] = new Vector3(
                    Mathf.Max(0.1f, midW),
                    Mathf.Max(0.1f, midH),
                    result[i].z);
                w($"[{i}] Gaia 尺寸 W[{p.minWidth:F1},{p.maxWidth:F1}] H[{p.minHeight:F1},{p.maxHeight:F1}]" +
                  $" → 基准 {result[i].x:F2}×{result[i].y:F2}，网格高 {result[i].z:F2}m");
            }
            return result;
        }

        private static Mesh LoadFirstMesh(string fbxPath)
        {
            if (!File.Exists(fbxPath)) return null;
            return AssetDatabase.LoadAllAssetsAtPath(fbxPath).OfType<Mesh>().FirstOrDefault();
        }

        private static void Flush(StringBuilder sb)
        {
            Directory.CreateDirectory("Logs/Grass");
            File.WriteAllText(ResultPath, sb.ToString());
        }
    }
}
