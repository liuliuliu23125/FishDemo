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

        private const string TexDir =
            "Assets/Procedural Worlds/Packages - Install/Gaia Pro Assets and Biomes/Content Resources/Terrain Details/Textures";

        /// <summary>
        /// 类型索引 → LOD0 albedo 图集。
        /// 依据：7 个原型 FBX 内嵌贴图引用解析——lawn 系（1/4/5/6）共用
        /// PW_LawnGrass_00_D，wild 系（0/2/3）共用 PW_WildGrass_00_D；
        /// 与 Terrain Details/Materials 里 PW_Detail_Lawn / PW_Detail_WildGrass
        /// 两个材质的 _BaseMap guid 解析结果一致。
        /// 花的真实颜色（雏菊白瓣黄芯/三叶草粉/蒲公英黄）都在这两张 RGBA
        /// 图集的像素里，alpha 是透明背景。
        /// </summary>
        private static readonly string[] DetailTextureNames =
        {
            "PW_WildGrass_00_D",        // 0 主草（wild 系）
            "PW_LawnGrass_00_D",        // 1 枯草块（lawn 系）
            "PW_WildGrass_00_D",        // 2 林下叶（wild 系）
            "PW_WildGrass_00_D",        // 3 林下枯枝（wild 系）
            "PW_LawnGrass_00_D",        // 4 三叶草花（lawn 系）
            "PW_LawnGrass_00_D",        // 5 雏菊（lawn 系）
            "PW_LawnGrass_00_D",        // 6 蒲公英（lawn 系）
        };

        /// <summary>
        /// 各类型的 tint（写入材质 _TypeTint）。图集已是最终颜色，默认全白；
        /// 枯草块给轻微黄色调，让它的枯枝在草地上可辨（不追求准确复刻
        /// 原材质的 healthy/dry 混合）。
        /// </summary>
        private static readonly Color[] DetailTints =
        {
            new Color(1.00f, 1.00f, 1.00f),   // 0 主草
            new Color(1.00f, 0.92f, 0.80f),   // 1 枯草块（偏黄）
            new Color(1.00f, 1.00f, 1.00f),   // 2 林下叶
            new Color(1.00f, 1.00f, 1.00f),   // 3 林下枯枝
            new Color(1.00f, 1.00f, 1.00f),   // 4 三叶草花
            new Color(1.00f, 1.00f, 1.00f),   // 5 雏菊
            new Color(1.00f, 1.00f, 1.00f),   // 6 蒲公英
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

            // ── 注入 LOD0 albedo 图集 ──
            // 图集与 mesh 分开加载：图集是独立 PNG 文件（不是 FBX 子资源），
            // LoadAssetAtPath 一步到位。
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

            // 一次写入 mesh + 图集 + tint，且**不触发重建**。
            //
            // 为什么不重建：每次 Initialize → BuildInstances 要处理 1265 万实例
            // （分块排序 ~1~2 秒）。原来连续调两个 setter 会重建两次，
            // 实测整脚本跑过 60 秒被截断，连场景保存都走不到。
            // 编辑器期只把引用写进序列化字段、保存场景即可；
            // 下次打开场景时 OnEnable 会自然重建一次。
            mgr.ApplyDetailPrototypeSetup(meshes, textures, DetailTints, rebuild: false);

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            AssetDatabase.SaveAssets();

            W($"已写入 {ok}/{DetailFbxNames.Length} 个原型 mesh、" +
              $"{texOk}/{DetailTextureNames.Length} 张图集并保存场景");
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
