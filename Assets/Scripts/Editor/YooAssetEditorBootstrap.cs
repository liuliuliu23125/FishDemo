using System;
using FishDemo.AssetManagement;
using UnityEditor;
using UnityEngine;
using YooAsset;
using YooAsset.Editor;

namespace FishDemo.EditorTools
{
    [InitializeOnLoad]
    public static class YooAssetEditorBootstrap
    {
        private static string s_simulatePackageRoot;

        static YooAssetEditorBootstrap()
        {
            EditorApplication.delayCall += InjectPackageRootIfNeeded;
        }

        [MenuItem("YooAsset/FishDemo/执行模拟构建并注入路径", false, 2)]
        public static void EnsureSimulateBuild()
        {
            if (!BundleCollectorSettingData.HasSettingAsset())
            {
                YooAssetSetup.SetupCollector();
            }

            var parameters = new PackageBuildParameters(YooAssetInitializer.DefaultPackageName)
            {
                BuildPipelineName = EBuildPipeline.EditorSimulateBuildPipeline.ToString(),

                BuildBundleType = (int)EBundleType.VirtualAssetBundle,
            };

            PackageBuildResult result;
            try
            {
                result = BundleSimulateBuilder.SimulateBuild(parameters);
            }
            catch (Exception e)
            {
                Debug.LogError($"[YooAsset] 模拟构建失败: {e.Message}");
                return;
            }

            s_simulatePackageRoot = result.PackageRootDirectory;
            YooAssetInitializer.EditorSimulatePackageRoot = s_simulatePackageRoot;
            Debug.Log($"[YooAsset] 模拟构建完成，包根目录: {s_simulatePackageRoot}");
        }

        private static void InjectPackageRootIfNeeded()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                return;
            }

            if (!string.IsNullOrEmpty(YooAssetInitializer.EditorSimulatePackageRoot))
            {
                return;
            }

            if (!string.IsNullOrEmpty(s_simulatePackageRoot))
            {
                YooAssetInitializer.EditorSimulatePackageRoot = s_simulatePackageRoot;
                return;
            }

            EnsureSimulateBuild();
        }
    }
}
