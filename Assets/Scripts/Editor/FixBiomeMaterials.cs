using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace FishDemo.EditorTools
{
    /// <summary>
    /// 把 Gaia Pro 美术材质从 BuiltIn shader 直接替换为同目录下现成的 URP shader。
    ///
    /// ── 与 GaiaUtils.ProcessMaterialLibrary 的区别 ──
    /// Gaia 自带流程依赖 GaiaSettings.m_pipelineProfile.m_ShaderMappingLibrary 的字符串名字
    /// 做 Shader.Find，且会顺手改 m_currentRenderer 等全局状态，出问题时难以定位。
    /// 这里改成按 shader 的 GUID 精确匹配，只做"把材质上的 shader 引用指向哪个 shader"这一件事，
    /// 逐材质打印改动，可审计、可重复执行（幂等）。
    ///
    /// ── 为什么按 GUID 而不是名字 ──
    /// GUID 是资产身份，改名不影响；而 Shader.Find 依赖 shader 名与实际编译产物一致，
    /// 在 batchmode/首次导入时可能失败。
    ///
    /// 用法：
    ///   - 菜单 FishDemo/Gaia/② 修复植被材质为 URP
    ///   - 批处理：-executeMethod FishDemo.EditorTools.FixBiomeMaterials.Setup
    /// </summary>
    public static class FixBiomeMaterials
    {
        private const string Tag = "[FixBiome]";
        private const string ShaderDir = "Assets/Procedural Worlds/Packages - Install/Gaia/Shaders/PW General";
        private const string BiomeRoot = "Assets/Procedural Worlds/Packages - Install/Gaia Pro Assets and Biomes";

        /// <summary>BuiltIn shader GUID → URP shader GUID。</summary>
        private static readonly Dictionary<string, string> ShaderMap = new Dictionary<string, string>
        {
            // PWS/PW_General_Forward.shader → Shader Graphs/PW_General_ShaderGraph_URP
            { "b59fbf1ddafb64aac89ae315fbb65877", "896c4566c534d544085eac02e8d305d9" },
            // PWS/PW_General_Deferred.shader → 同上（URP 下 Forward/Deferred 共用同一 ShaderGraph）
            { "676d51a93fad6434bb7ee1f64ad89551", "896c4566c534d544085eac02e8d305d9" },
            // PWS/Details/PW_Details_Foliage_BuiltIn.shader → PWS/Details/PW_Details_Foliage_URP
            { "72462e7bb7591b4418a705a37fe544ac", "00efb9683d88ef54f91cfd4c0e362b1b" },
        };

        [MenuItem("FishDemo/Gaia/② 修复植被材质为 URP")]
        public static void Setup()
        {
            Run(false);
        }

        /// <summary>仅扫描报告，不修改任何资产。</summary>
        public static void Report()
        {
            Run(true);
        }

        private static void Run(bool dryRun)
        {
            var sb = new StringBuilder();
            void W(string s) { Debug.Log($"{Tag} {s}"); sb.AppendLine(s); }

            // 先验证目标 URP shader 都存在
            var urpShaders = new Dictionary<string, Shader>();
            foreach (var targetGuid in ShaderMap.Values.Distinct())
            {
                string path = AssetDatabase.GUIDToAssetPath(targetGuid);
                var sh = AssetDatabase.LoadAssetAtPath<Shader>(path);
                W($"目标 URP shader: {path} → {(sh != null ? sh.name : "加载失败")}");
                if (sh == null)
                {
                    Debug.LogError($"{Tag} 找不到 URP shader，GUID={targetGuid}，path={path}");
                    EditorApplication.Exit(1);
                    return;
                }
                urpShaders[targetGuid] = sh;
            }

            // 收集美术包里所有材质
            string[] guids = AssetDatabase.FindAssets("t:Material", new[] { BiomeRoot });
            W($"扫描 {BiomeRoot} 下材质 {guids.Length} 个{(dryRun ? "（演练模式，不修改）" : "")}");

            int changed = 0, alreadyOk = 0, unknown = 0;
            var unknownShaders = new Dictionary<string, int>();
            var changedList = new List<string>();

            foreach (string guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
                if (mat == null || mat.shader == null) continue;

                // 取当前材质引用的 shader GUID
                string shaderPath = AssetDatabase.GetAssetPath(mat.shader);
                string shaderGuid = AssetDatabase.AssetPathToGUID(shaderPath);

                if (!ShaderMap.TryGetValue(shaderGuid, out string targetGuid))
                {
                    // 已经是 URP shader 的算正常
                    if (urpShaders.Values.Any(x => x == mat.shader)) alreadyOk++;
                    else
                    {
                        unknown++;
                        string key = $"{mat.shader.name} ({shaderGuid})";
                        unknownShaders[key] = unknownShaders.TryGetValue(key, out int c) ? c + 1 : 1;
                    }
                    continue;
                }

                Shader target = urpShaders[targetGuid];
                if (!dryRun)
                {
                    mat.shader = target;
                    EditorUtility.SetDirty(mat);
                }
                changed++;
                if (changedList.Count < 15)
                    changedList.Add($"{path}: {mat.shader.name} → {target.name}");
            }

            if (!dryRun)
            {
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
            }

            W($"{(dryRun ? "将修改" : "已修改")} {changed} 个；已是 URP {alreadyOk} 个；未识别 {unknown} 个");
            foreach (var s in changedList) W($"  改动: {s}");
            if (unknown > 0)
            {
                W("未识别的 shader（未改动，需人工确认）:");
                foreach (var kv in unknownShaders.OrderByDescending(x => x.Value))
                    W($"  {kv.Value} 个 × {kv.Key}");
            }

            System.IO.Directory.CreateDirectory("Logs/Gaia");
            System.IO.File.WriteAllText("Logs/Gaia/fix-biome-materials.txt", sb.ToString());

            bool ok = changed == 0 || dryRun
                ? unknown == 0 || changed > 0 || alreadyOk > 0
                : true;
            EditorApplication.Exit(dryRun || unknown == 0 ? 0 : 2);
        }
    }
}
