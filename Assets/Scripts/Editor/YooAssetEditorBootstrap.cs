using System;
using FishDemo.AssetManagement;
using UnityEditor;
using UnityEngine;
using YooAsset;
using YooAsset.Editor;

namespace FishDemo.EditorTools
{
    /// <summary>
    /// 编辑器侧的 YooAsset 引导。
    ///
    /// 存在的必要性：
    /// EditorSimulateMode 的文件系统在 OnCreate 时要求 packageRoot 非空
    /// （EditorFileSystem.cs:217-218），而该目录只能由模拟构建产物提供
    /// （BundleSimulateBuilder.SimulateBuild 返回的 PackageRootDirectory）。
    /// Runtime 程序集不能引用 YooAsset.Editor，所以由这里负责跑构建并把路径注入
    /// YooAssetInitializer.EditorSimulatePackageRoot。
    /// </summary>
    [InitializeOnLoad]
    public static class YooAssetEditorBootstrap
    {
        /// <summary>上次模拟构建的产物根目录，静态缓存避免每次 Play 都重新构建。</summary>
        private static string s_simulatePackageRoot;

        static YooAssetEditorBootstrap()
        {
            // 进入 Play 模式前确保根目录已就绪。
            // 放在 delayCall 里是因为 InitializeOnLoad 期间 AssetDatabase 可能尚未就绪。
            EditorApplication.delayCall += InjectPackageRootIfNeeded;
        }

        /// <summary>
        /// 手动触发：菜单 YooAsset/FishDemo/执行模拟构建并注入路径。
        /// 资源有变动（新增/删除/改收集配置）后需要重新执行。
        /// </summary>
        [MenuItem("YooAsset/FishDemo/执行模拟构建并注入路径", false, 2)]
        public static void EnsureSimulateBuild()
        {
            // 模拟构建要先有收集配置，否则 BundleCollectorSetting.GetPackage 会抛
            // "Package not found: 'DefaultPackage'"。SetupCollector 是幂等的，直接确保一次。
            if (!BundleCollectorSettingData.HasSettingAsset())
            {
                YooAssetSetup.SetupCollector();
            }

            var parameters = new PackageBuildParameters(YooAssetInitializer.DefaultPackageName)
            {
                BuildPipelineName = EBuildPipeline.EditorSimulateBuildPipeline.ToString(),

                // 必须显式指定，不能依赖默认值 0。
                // BuildBundleType 的默认值是 0，而 0 恰好是 EBundleType.None，
                // BuildParameters.CheckBuildParameters() 会把 None 判为非法并抛
                // "[ErrorCode117] Build bundle type is unknown: 0"（BuildParameters.cs:151-155）。
                // 模拟构建管线只接受 Virtual 系列（EditorSimulateBuildParameters.cs:14-19），
                // 与 YooAsset 自身 CompatiblePackageBuilder 的调用保持一致。
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

        /// <summary>
        /// 若尚未注入根目录则自动跑一次模拟构建。
        /// 只在编辑器、非播放状态下执行，避免运行中重建资源。
        /// </summary>
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
