using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using FishDemo.Rendering;

namespace FishDemo.EditorTools
{
    /// <summary>
    /// 把 7 种 detail 原型的 LOD0 mesh 写进场景里的 GrassBRGManager。
    ///
    /// 为什么需要这个工具（而不是运行时按名字找）：
    /// detail 原型的 FBX 只被 TerrainData 以子资源引用，运行时
    /// Resources.FindObjectsOfTypeAll&lt;Mesh&gt;() 找不到（实测 0 命中，
    /// 全部退回程序化兜底草簇，7 种植物长得一模一样）。
    /// 且 FBX 内 mesh 名带后缀（PW_WildGrass_General_Lod1_E），不是文件名。
    /// 编辑器期用 AssetDatabase 拿到确切引用、序列化进组件，运行时直接读，
    /// 是唯一稳定且不在运行时引入 UnityEditor 依赖的做法。
    ///
    /// 幂等：重复执行会覆盖为当前资产的引用。
    ///
    /// 用法：菜单 FishDemo/Rendering/⑮ 写入 detail 原型 mesh
    /// </summary>
    public static class DetailBrgSetup
    {
        private const string Tag = "[DetailBrgSetup]";
        private const string WorldScenePath = "Assets/Scenes/World.unity";
        private const string NodeName = "GrassBRG (M1)";
        private const string FbxDir =
            "Assets/Procedural Worlds/Packages - Install/Gaia Pro Assets and Biomes/Content Resources/Terrain Details/Mesh";
        private const string ResultPath = "Logs/Grass/detail-brg-setup.txt";

        /// <summary>
        /// 类型索引 → FBX 文件名。顺序必须与 TerrainData.detailPrototypes 的层序一致
        /// （依据：TerrainDetailProbe 实测输出）。
        /// </summary>
        private static readonly string[] DetailFbxNames =
        {
            "PW_WildGrass_General_Lod1",        // 0 主草
            "PW_LawnGrass_DeadPatch",           // 1 枯草块
            "PW_WildGrass_Understory_Foliage",  // 2 林下叶
            "PW_WildGrass_Understory_Sticks",   // 3 林下枯枝
            "PW_LawnGrass_CloverFlower",        // 4 三叶草花
            "PW_LawnGrass_Daisy",               // 5 雏菊
            "PW_LawnGrass_Dandelions",          // 6 蒲公英
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
                  $"{mesh.triangles.Length / 3} tri）");
            }

            mgr.SetDetailPrototypeMeshes(meshes);

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            AssetDatabase.SaveAssets();

            W($"已写入 {ok}/{DetailFbxNames.Length} 个原型 mesh 并保存场景");
            Flush(sb);
            EditorApplication.Exit(0);
        }

        private static void Flush(StringBuilder sb)
        {
            Directory.CreateDirectory("Logs/Grass");
            File.WriteAllText(ResultPath, sb.ToString());
        }
    }
}
