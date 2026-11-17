using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using YooAsset;

namespace FishDemo.AssetManagement
{
    public static class YooAssetInitializer
    {
        public const string DefaultPackageName = "DefaultPackage";

        public static ResourcePackage Package { get; private set; }

        public static bool IsReady { get; private set; }

        public static string EditorSimulatePackageRoot { get; set; }

        public static async Task<bool> InitializeAsync(string hostServerURL = null)
        {
            if (IsReady)
            {
                return true;
            }

            YooAssets.SetAsyncOperationMaxTimeSlice(10);

            if (!YooAssets.IsInitialized)
            {
                YooAssets.Initialize();
            }

            if (YooAssets.TryGetPackage(DefaultPackageName, out var existing) && existing != null)
            {
                if (existing.InitializeStatus == EOperationStatus.Failed)
                {
                    YooAssets.RemovePackage(DefaultPackageName);
                    existing = null;
                }
            }

            if (existing == null)
            {
                existing = YooAssets.CreatePackage(DefaultPackageName);
            }

            Package = existing;

            var initOp = Package.InitializePackageAsync(BuildOptions(hostServerURL));
            await initOp;

            if (initOp.Status != EOperationStatus.Succeeded)
            {
                Debug.LogError($"[YooAsset] 资源包初始化失败: {initOp.Error}");
                return false;
            }

            if (!await LoadManifestAsync())
            {
                return false;
            }

            IsReady = true;
            return true;
        }

        private static async Task<bool> LoadManifestAsync()
        {
            var versionOp = Package.RequestPackageVersionAsync();
            await versionOp;

            if (versionOp.Status != EOperationStatus.Succeeded)
            {
                Debug.LogError($"[YooAsset] 请求资源版本失败: {versionOp.Error}");
                return false;
            }

            var manifestOp = Package.LoadPackageManifestAsync(
                new LoadPackageManifestOptions(versionOp.PackageVersion, 60));
            await manifestOp;

            if (manifestOp.Status != EOperationStatus.Succeeded)
            {
                Debug.LogError($"[YooAsset] 加载资源清单失败: {manifestOp.Error}");
                return false;
            }

            return true;
        }

        private static InitializePackageOptions BuildOptions(string hostServerURL)
        {
#if UNITY_EDITOR
            if (string.IsNullOrEmpty(EditorSimulatePackageRoot))
            {
                throw new System.InvalidOperationException(
                    "[YooAsset] 编辑器模拟模式的包根目录未设置。请先调用 YooAssetEditorBootstrap.EnsureSimulateBuild() 再初始化。");
            }

            return new EditorSimulateModeOptions
            {
                EditorFileSystemParameters =
                    FileSystemParameters.CreateDefaultEditorFileSystemParameters(EditorSimulatePackageRoot),
                BundleLoadingMaxConcurrency = 8
            };
#else
            if (string.IsNullOrEmpty(hostServerURL))
            {
                return new OfflinePlayModeOptions
                {
                    BuiltinFileSystemParameters =
                        FileSystemParameters.CreateDefaultBuiltinFileSystemParameters(),
                    BundleLoadingMaxConcurrency = 8
                };
            }

            var cacheParameters = FileSystemParameters.CreateDefaultSandboxFileSystemParameters(
                new RemoteService(hostServerURL));

            cacheParameters.AddParameter(EFileSystemParameter.DownloadMaxConcurrency, 6);
            cacheParameters.AddParameter(EFileSystemParameter.DownloadMaxRequestPerFrame, 4);

            cacheParameters.AddParameter(EFileSystemParameter.DownloadWatchdogTimeout, 45);

            cacheParameters.AddParameter(EFileSystemParameter.DownloadResumeMinimumSize, 1024L * 1024L);

            return new HostPlayModeOptions
            {
                BuiltinFileSystemParameters =
                    FileSystemParameters.CreateDefaultBuiltinFileSystemParameters(),
                CacheFileSystemParameters = cacheParameters,
                BundleLoadingMaxConcurrency = 8
            };
#endif
        }

        public static void Shutdown()
        {
            IsReady = false;
            Package = null;

            if (YooAssets.IsInitialized)
            {
                YooAssets.Destroy();
            }
        }
    }

    public sealed class RemoteService : IRemoteService
    {
        private readonly string _hostServerURL;

        public RemoteService(string hostServerURL)
        {
            _hostServerURL = hostServerURL.TrimEnd('/');
        }

        public IReadOnlyList<string> GetRemoteUrls(string fileName)
        {
            return new[] { $"{_hostServerURL}/{fileName}" };
        }
    }
}
