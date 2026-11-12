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
    /// 把 7 种 detail 原型的 LOD0 mesh、albedo 图集、**精灵格位**、**真实尺寸** 与 tint
    /// 写进场景里的 GrassBRGManager。
    ///
    /// 为什么需要这个工具（而不是运行时按名字找）：
    /// detail 原型的 FBX 只被 TerrainData 以子资源引用，运行时
    /// Resources.FindObjectsOfTypeAll&lt;Mesh&gt;() 找不到（实测 0 命中，
    /// 全部退回程序化兜底草簇，7 种植物长得一模一样）。
    /// 且 FBX 内 mesh 名带后缀（PW_WildGrass_General_Lod1_E），不是文件名。
    /// 编辑器期用 AssetDatabase 拿到确切引用、序列化进组件，运行时直接读，
    /// 是唯一稳定且不在运行时引入 UnityEditor 依赖的做法。
    ///
    /// ── 精灵格位（DetailSpriteCells）为什么必须手写常量 ──
    /// 一张图集里平铺着几十个互不相关的图案。原型 mesh 自带指向正确格位的 UV，
    /// 但 LOD1/LOD2 的交叉片是程序化生成的，必须显式知道采哪一格。
    /// 这些常量是对两张图集做 alpha 连通域分析 + 与原型 mesh UV 命中率交叉验证
    /// 得出的（工具在 Tools/atlas-probe/，原始 UV 见 Logs/detail-uv-dump.txt）。
    ///
    /// ── 尺寸为什么从 TerrainData 读，不写死 ──
    /// Gaia 给每类定的 min/max 宽高差异极大（枯草块 5~8、雏菊 1~4、主草 1~2），
    /// 写死常量会在换 biome 时失配。这里直接读 chunk 的 detailPrototypes。
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
        /// PW_LawnGrass_00_D，wild 系（0/2/3）共用 PW_WildGrass_00_D。
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
        /// 各类型在图集里的精灵格（UV 矩形：x=uMin, y=vMin, width, height）。
        ///
        /// 数据来源（可复现）：
        ///   1. 对两张图集做 alpha>96 的 8 连通域分析，得到每个图案的 UV 包围盒；
        ///      剔除 fill>0.97 的纯色测试矩形（图集里有渐变条与色块，不是植株）。
        ///   2. 把每个原型 mesh 的三角形重心 UV 光栅化，统计落进哪个连通域，
        ///      命中率最高的即该类型的格位。
        ///   3. 逐个裁图目视复核，确认是「草簇/枯叶/阔叶苗/枯枝/粉花/白花/黄花」。
        ///
        /// 关键坑：雏菊与蒲公英的原型 mesh UV 包围盒横跨了图集顶部那条
        /// u[0.290,0.740] v[0.920,1.000] 的**纯色绿渐变条**（占其采样面积
        /// 94% / 77%）。包围盒看着对，实际采到的大半是绿条。所以这里给的是
        /// 净植株格，不是 mesh 的 UV bbox。
        /// </summary>
        private static readonly Rect[] DetailSpriteCells =
        {
            // 0 主草：wild 图集里的草簇
            new Rect(0.6982f, 0.2334f, 0.1534f, 0.1553f),
            // 1 枯草块：lawn 图集里的枯叶堆（mesh UV 命中率 100%）
            new Rect(0.6357f, 0.2480f, 0.1426f, 0.1094f),
            // 2 林下叶：wild 图集里的阔叶苗
            new Rect(0.4277f, 0.8457f, 0.1035f, 0.1270f),
            // 3 林下枯枝：wild 图集里的 Y 形枝
            new Rect(0.6914f, 0.7510f, 0.0732f, 0.0888f),
            // 4 三叶草花：lawn 图集里的粉色三叶草花
            new Rect(0.8662f, 0.4756f, 0.1143f, 0.2402f),
            // 5 雏菊：lawn 图集里的白色雏菊（避开顶部绿条）
            new Rect(0.0098f, 0.8770f, 0.1006f, 0.1054f),
            // 6 蒲公英：lawn 图集里的黄花（避开顶部绿条）
            new Rect(0.1152f, 0.7666f, 0.0733f, 0.0762f),
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
                  $"{mesh.triangles.Length / 3} tri / 高 {mesh.bounds.size.y:F2}m）");
            }

            // ── 注入 albedo 图集 ──
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

            // ── 从 TerrainData 读 Gaia 的真实尺寸，换算成材质倍率 ──
            // 取所有 chunk 里第一个有 detailPrototypes 的地形。经 TerrainDetailProbe
            // 实测，36 个 chunk 的 7 层配置完全一致，所以取一个即可。
            var scales = BuildTypeScales(meshes, W);

            // 一次写入，且**不触发重建**。
            //
            // 为什么不重建：每次 Initialize → BuildInstances 要处理上千万实例
            // （分块排序 1~2 秒）。原来连续调两个 setter 会重建两次，
            // 实测整脚本跑过 60 秒被截断，连场景保存都走不到。
            // 编辑器期只把引用写进序列化字段、保存场景即可；
            // 下次打开场景时 OnEnable 会自然重建一次。
            // ── Gaia 的两个通用 billboard（LOD1/LOD2 的真实几何）──
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

        /// <summary>
        /// 由 TerrainData.detailPrototypes 的 min/max 宽高 + 原型 mesh 包围盒，
        /// 算出每类型的 (水平倍率, 竖直倍率, 网格高度)。
        ///
        /// 语义推导（Unity 的 detail 渲染约定）：
        ///   世界尺寸 = 原型 mesh 的包围盒尺寸 × 随机取值(minW..maxW, minH..maxH)
        /// 所以「倍率」相对原型 mesh 本身，取区间中点作为类型基准倍率；
        /// 个体差异仍由 GPU buffer 的 8 位抖动承担（ScaleQ → [0.5,1.5]，±50%）。
        ///
        /// 若找不到含 detailPrototypes 的 chunk，各类型退回 (1,1,meshH)。
        /// </summary>
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
                // 下限保护：Gaia 里若有 0 值会让该类型消失。
                result[i] = new Vector3(
                    Mathf.Max(0.1f, midW),
                    Mathf.Max(0.1f, midH),
                    result[i].z);
                w($"[{i}] Gaia 尺寸 W[{p.minWidth:F1},{p.maxWidth:F1}] H[{p.minHeight:F1},{p.maxHeight:F1}]" +
                  $" → 基准 {result[i].x:F2}×{result[i].y:F2}，网格高 {result[i].z:F2}m");
            }
            return result;
        }

        /// <summary>加载 FBX 里第一个 Mesh 子资源；失败返回 null。</summary>
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
