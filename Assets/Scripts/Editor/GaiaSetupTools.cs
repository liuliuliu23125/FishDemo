using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Gaia;

namespace FishDemo.EditorTools
{
    /// <summary>
    /// Gaia 安装后的流水线配置工具：URP shader 迁移、状态检查。
    ///
    /// 为什么需要它：
    /// Gaia Pro 的美术材质引用的是 BuiltIn 管线 shader（PWS/PW_General_Forward 等），
    /// 在 URP 工程里会渲染成粉色。Gaia 自带的映射表（Unity Pipeline Gaia Profile.asset）
    /// 记录了 BuiltIn → URP 的替换关系，由 GaiaUtils.ProcessMaterialLibrary 执行批量替换。
    /// 但该流程平时只能在 Gaia Manager 窗口里点按钮触发，batchmode 下无法操作 GUI，
    /// 所以这里把它包装成可直接 -executeMethod 调用的静态入口。
    ///
    /// 用法：
    ///   - 菜单 FishDemo/Gaia/执行 URP 材质迁移
    ///   - 批处理：-executeMethod FishDemo.EditorTools.GaiaSetupTools.UpgradeMaterialsToURP
    /// </summary>
    public static class GaiaSetupTools
    {
        /// <summary>
        /// 把 Gaia 目录下仍使用 BuiltIn shader 的材质批量替换为 URP 版本。
        ///
        /// 前置条件：GaiaSettings.asset 的 m_pipelineProfile 必须指向 Unity Pipeline Gaia Profile，
        /// 且 m_currentRenderer 已被设为 Universal。后者由 GaiaManagerEditor 的 Upgrade 按钮负责，
        /// 这里若发现不是 Universal 会直接报错退出，避免"以为迁移了其实没迁"。
        /// </summary>
        public static void UpgradeMaterialsToURP()
        {
            GaiaSettings settings = GaiaUtils.GetGaiaSettings();
            if (settings == null)
            {
                Debug.LogError("[FishDemo] 找不到 GaiaSettings.asset，Gaia 可能未正确安装。");
                EditorApplication.Exit(1);
                return;
            }

            if (settings.m_pipelineProfile == null)
            {
                Debug.LogError("[FishDemo] GaiaSettings 的 m_pipelineProfile 为空，无法执行 shader 映射。");
                EditorApplication.Exit(1);
                return;
            }

            // 记录迁移前的渲染管线状态，便于对比
            Debug.Log($"[FishDemo] 当前 Gaia 渲染管线标记 = {settings.m_currentRenderer}");
            Debug.Log($"[FishDemo] 映射表条目数 = {settings.m_pipelineProfile.m_ShaderMappingLibrary?.Length ?? 0}");

            // 必须先修正 m_currentRenderer，否则迁移方向是反的。
            // 依据 GaiaUtils.ProcessMaterialLibrary 的分支逻辑（GaiaUtils.cs:824-845）：
            //   标记为 BuiltIn 时 targetShader 取 m_builtInShaderName，会把 URP 材质改回 BuiltIn；
            //   只有标记为 Universal 时 targetShader 才取 m_URPReplacementShaderName。
            // 这里只改 Gaia 自己的标记字段，不调用 SetPipelineAsset，
            // 因为工程的 URP 资产（Assets/Settings/UniversalRP.asset）已手工配好，不能被 Gaia 覆盖。
            if (settings.m_currentRenderer != GaiaConstants.EnvironmentRenderer.Universal)
            {
                Debug.Log($"[FishDemo] 将 m_currentRenderer 从 {settings.m_currentRenderer} 修正为 Universal");
                settings.m_currentRenderer = GaiaConstants.EnvironmentRenderer.Universal;
                EditorUtility.SetDirty(settings);
                AssetDatabase.SaveAssets();
            }

            // 迁移前统计：有多少材质还在用 BuiltIn shader
            int beforeCount = CountBuiltInMaterials(settings);
            Debug.Log($"[FishDemo] 迁移前仍使用 BuiltIn shader 的材质数 = {beforeCount}");

            bool changed = GaiaUtils.ProcessMaterialLibrary();
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            int afterCount = CountBuiltInMaterials(settings);
            Debug.Log($"[FishDemo] ProcessMaterialLibrary 返回 {changed}；迁移后仍使用 BuiltIn shader 的材质数 = {afterCount}");

            // 排除刻意保留的 BuiltIn / HDRP 变体目录后，剩余才算真正迁移失败
            var remaining = EnumerateBuiltInMaterialPaths(settings)
                .Where(p => !IsOtherPipelineVariant(p))
                .ToList();

            foreach (string path in remaining)
            {
                Debug.LogWarning($"[FishDemo] 未迁移材质：{path}");
            }

            if (remaining.Count == 0)
            {
                Debug.Log($"[FishDemo] URP 材质迁移完成。残留 {afterCount} 个 BuiltIn shader 材质，" +
                          "均属于 BuiltIn/HDRP 变体目录，属预期。");
                EditorApplication.Exit(0);
            }
            else
            {
                Debug.LogError($"[FishDemo] 仍有 {remaining.Count} 个材质未迁移到 URP。");
                // 返回非 0 让批处理能感知失败
                EditorApplication.Exit(2);
            }
        }

        /// <summary>
        /// 判断某个材质路径是否属于"刻意保留其它管线版本"的目录。
        ///
        /// Gaia 的水资源包同时提供 BuiltIn / HDRP / URP 三套材质，
        /// 它们各自放在对应管线命名的子目录里。BuiltIn 与 HDRP 目录下的材质
        /// 本来就该使用 BuiltIn shader，不能当作迁移失败。
        /// </summary>
        private static bool IsOtherPipelineVariant(string path)
        {
            string normalized = path.Replace('\\', '/');
            return normalized.Contains("/Water Prefabs/BuiltIn/")
                || normalized.Contains("/Water Prefabs/HDRP/")
                || normalized.Contains("/High Definition Pipeline/");
        }

        /// <summary>
        /// 只做检查、不改动任何资产。用于确认安装结果。
        /// </summary>
        public static void ReportStatus()
        {
            GaiaSettings settings = GaiaUtils.GetGaiaSettings();
            Debug.Log("========== Gaia 状态检查 ==========");
            Debug.Log($"GaiaSettings: {(settings == null ? "缺失" : AssetDatabase.GetAssetPath(settings))}");
            if (settings != null)
            {
                Debug.Log($"渲染管线标记: {settings.m_currentRenderer}");
                Debug.Log($"Pipeline Profile: {(settings.m_pipelineProfile == null ? "空" : settings.m_pipelineProfile.name)}");
                if (settings.m_pipelineProfile != null)
                {
                    var lib = settings.m_pipelineProfile.m_ShaderMappingLibrary;
                    Debug.Log($"Shader 映射条目: {lib?.Length ?? 0}");
                    if (lib != null)
                    {
                        foreach (var e in lib)
                        {
                            Shader urpShader = Shader.Find(e.m_URPReplacementShaderName);
                            Debug.Log($"  {e.m_builtInShaderName} -> {e.m_URPReplacementShaderName} " +
                                      $"(URP shader {(urpShader == null ? "未找到" : "已找到")})");
                        }
                    }
                }
                Debug.Log($"仍使用 BuiltIn shader 的材质数: {CountBuiltInMaterials(settings)}");
            }

            // 地形与水资源的可用性
            string waterUrp = "Assets/Procedural Worlds/Packages - Install/Gaia Water/Water Prefabs/URP/Gaia Water.prefab";
            Debug.Log($"Gaia Water (URP) 预制体: {(File.Exists(waterUrp) ? "存在" : "缺失")} {waterUrp}");
            Debug.Log("===================================");
            EditorApplication.Exit(0);
        }

        /// <summary>
        /// 统计 Gaia 目录下仍在使用 BuiltIn shader 的材质数量。
        /// 判定依据与 GaiaManagerEditor 的检查逻辑一致：shader 名命中映射表的 builtInShaderName。
        /// </summary>
        private static int CountBuiltInMaterials(GaiaSettings settings)
        {
            return EnumerateBuiltInMaterialPaths(settings).Count();
        }

        private static IEnumerable<string> EnumerateBuiltInMaterialPaths(GaiaSettings settings)
        {
            if (settings?.m_pipelineProfile?.m_ShaderMappingLibrary == null)
            {
                yield break;
            }

            var builtInNames = new HashSet<string>(
                settings.m_pipelineProfile.m_ShaderMappingLibrary
                    .Select(e => e.m_builtInShaderName)
                    .Where(n => !string.IsNullOrEmpty(n)));

            if (builtInNames.Count == 0)
            {
                yield break;
            }

            // 只扫 Gaia 安装目录与包安装目录，避免把第三方插件的材质算进来
            string[] searchRoots = new[]
            {
                GaiaDirectories.GetGaiaDirectory(),
                GaiaDirectories.GetPackageInstallDirectory()
            }.Where(d => !string.IsNullOrEmpty(d) && Directory.Exists(d)).ToArray();

            if (searchRoots.Length == 0)
            {
                yield break;
            }

            foreach (string guid in AssetDatabase.FindAssets("t:Material", searchRoots))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (path.EndsWith("shadergraph", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                Material mat = AssetDatabase.LoadAssetAtPath<Material>(path);
                if (mat != null && mat.shader != null && builtInNames.Contains(mat.shader.name))
                {
                    yield return $"{path}  [{mat.shader.name}]";
                }
            }
        }
    }
}
