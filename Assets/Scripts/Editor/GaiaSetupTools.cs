using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Gaia;

namespace FishDemo.EditorTools
{
    public static class GaiaSetupTools
    {
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

            Debug.Log($"[FishDemo] 当前 Gaia 渲染管线标记 = {settings.m_currentRenderer}");
            Debug.Log($"[FishDemo] 映射表条目数 = {settings.m_pipelineProfile.m_ShaderMappingLibrary?.Length ?? 0}");

            if (settings.m_currentRenderer != GaiaConstants.EnvironmentRenderer.Universal)
            {
                Debug.Log($"[FishDemo] 将 m_currentRenderer 从 {settings.m_currentRenderer} 修正为 Universal");
                settings.m_currentRenderer = GaiaConstants.EnvironmentRenderer.Universal;
                EditorUtility.SetDirty(settings);
                AssetDatabase.SaveAssets();
            }

            int beforeCount = CountBuiltInMaterials(settings);
            Debug.Log($"[FishDemo] 迁移前仍使用 BuiltIn shader 的材质数 = {beforeCount}");

            bool changed = GaiaUtils.ProcessMaterialLibrary();
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            int afterCount = CountBuiltInMaterials(settings);
            Debug.Log($"[FishDemo] ProcessMaterialLibrary 返回 {changed}；迁移后仍使用 BuiltIn shader 的材质数 = {afterCount}");

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
                EditorApplication.Exit(2);
            }
        }

        private static bool IsOtherPipelineVariant(string path)
        {
            string normalized = path.Replace('\\', '/');
            return normalized.Contains("/Water Prefabs/BuiltIn/")
                || normalized.Contains("/Water Prefabs/HDRP/")
                || normalized.Contains("/High Definition Pipeline/");
        }

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

            string waterUrp = "Assets/Procedural Worlds/Packages - Install/Gaia Water/Water Prefabs/URP/Gaia Water.prefab";
            Debug.Log($"Gaia Water (URP) 预制体: {(File.Exists(waterUrp) ? "存在" : "缺失")} {waterUrp}");
            Debug.Log("===================================");
            EditorApplication.Exit(0);
        }

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
