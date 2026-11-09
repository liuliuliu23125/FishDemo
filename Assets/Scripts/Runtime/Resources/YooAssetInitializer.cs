using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using YooAsset;

namespace FishDemo.AssetManagement
{
    /// <summary>
    /// YooAsset 运行时初始化器。
    /// 负责创建资源包、按运行模式初始化文件系统，并提供初始化与释放的统一入口。
    ///
    /// 设计约束：
    /// 1. 全局只有单一资源包（DefaultPackageName），简化引用计数与清单管理。
    /// 2. 编辑器走 EditorSimulateMode，真机走 HostPlayMode 或 OfflinePlayMode。
    /// 3. 严禁 Resources.Load；所有流式资源必须经此入口。
    ///
    /// 注意：本文件基于 YooAsset 3.0.7 的 API。3.0 与 2.x 差异较大：
    /// 状态枚举是 Succeeded（Succeed 仅在 YOOASSET_LEGACY_API 宏下作为别名存在），
    /// TryGetPackage 是 out 参数形式，清单加载必须提供非空版本号。
    /// </summary>
    public static class YooAssetInitializer
    {
        /// <summary>默认资源包名，必须与 Bundle Collector 中配置的包名一致。</summary>
        public const string DefaultPackageName = "DefaultPackage";

        /// <summary>当前资源包实例，初始化成功后可用。</summary>
        public static ResourcePackage Package { get; private set; }

        /// <summary>初始化是否已成功完成（包含清单加载）。</summary>
        public static bool IsReady { get; private set; }

        /// <summary>
        /// 编辑器下模拟模式的包根目录。
        ///
        /// 必须由 Editor 侧注入：EditorFileSystem.OnCreate 在 packageRoot 为空时直接抛
        /// ArgumentException（EditorFileSystem.cs:217-218），而该目录来自模拟构建的产物
        /// （BundleSimulateBuilder.SimulateBuild 返回的 PackageRootDirectory），
        /// Runtime 程序集无法引用 YooAsset.Editor，因此通过此字段由编辑器代码赋值。
        /// </summary>
        public static string EditorSimulatePackageRoot { get; set; }

        /// <summary>
        /// 初始化 YooAsset 与默认资源包。必须在进入任何依赖资源的场景前调用一次。
        /// 失败后可以再次调用重试（内部会先释放失败的包）。
        /// </summary>
        /// <param name="hostServerURL">远端资源服务器地址；为空表示纯内置（离线）。</param>
        /// <returns>初始化是否成功。</returns>
        public static async Task<bool> InitializeAsync(string hostServerURL = null)
        {
            if (IsReady)
            {
                return true;
            }

            // 限制每帧异步操作时间片。
            // 默认值是 long.MaxValue（不限制），会把当帧所有待推进的 operation 全跑完，造成掉帧。
            // 注意硬下限：低于 10ms 会被库强制钳制为 10ms 并打 warning
            // （AsyncOperationSystem.cs:26,53-57），所以这里设 10 是能取到的最小值。
            YooAssets.SetAsyncOperationMaxTimeSlice(10);

            if (!YooAssets.IsInitialized)
            {
                // 创建 YooAssets 单例驱动，后续所有操作都依赖它
                YooAssets.Initialize();
            }

            // 上次初始化失败会残留一个已存在的包，直接复用会命中 "already initialized"。
            // 因此失败重试时先移除旧包，再重新创建。
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

            // YooAsset 的 AsyncOperationBase 自带 GetAwaiter，可直接 await
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

            // 只有清单也加载成功，才算真正可用。
            // 不能用 InitializeStatus 判断：它只反映文件系统初始化，清单失败时仍是 Succeeded。
            IsReady = true;
            return true;
        }

        /// <summary>
        /// 加载资源清单。
        ///
        /// 关键约束：无论离线还是联机，都必须先拿到非空的版本号。
        /// LoadPackageManifestOperation 在 PackageVersion 为空时会直接报错
        /// （LoadPackageManifestOperation.cs:39-45），不存在"传 null 加载内置清单"的用法。
        /// </summary>
        private static async Task<bool> LoadManifestAsync()
        {
            // 离线模式下该操作返回内置包的版本号，联机模式下返回远端最新版本
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

        /// <summary>
        /// 按当前运行环境构造初始化选项。
        /// 编辑器用模拟模式，避免每次改资源都要重新构建 AB。
        /// </summary>
        private static InitializePackageOptions BuildOptions(string hostServerURL)
        {
#if UNITY_EDITOR
            // 根目录必须非空，且必须指向模拟构建产物目录。
            // 若未注入，说明没有跑过 BundleSimulateBuilder，直接给出明确错误而不是让库抛异常。
            if (string.IsNullOrEmpty(EditorSimulatePackageRoot))
            {
                throw new System.InvalidOperationException(
                    "[YooAsset] 编辑器模拟模式的包根目录未设置。请先调用 YooAssetEditorBootstrap.EnsureSimulateBuild() 再初始化。");
            }

            return new EditorSimulateModeOptions
            {
                EditorFileSystemParameters =
                    FileSystemParameters.CreateDefaultEditorFileSystemParameters(EditorSimulatePackageRoot),
                // 同时加载 Bundle 的并发上限。默认 int.MaxValue，会在进入区域时一次性打开过多 bundle。
                BundleLoadingMaxConcurrency = 8
            };
#else
            if (string.IsNullOrEmpty(hostServerURL))
            {
                // 纯内置：所有资源打进安装包，适合首包完整体验
                return new OfflinePlayModeOptions
                {
                    BuiltinFileSystemParameters =
                        FileSystemParameters.CreateDefaultBuiltinFileSystemParameters(),
                    BundleLoadingMaxConcurrency = 8
                };
            }

            // 联机：内置包打底 + 沙盒缓存承载增量更新
            var cacheParameters = FileSystemParameters.CreateDefaultSandboxFileSystemParameters(
                new RemoteService(hostServerURL));

            // 显式配置下载调度参数。
            // 默认值（并发 8 / 每帧 8）是通用场景的折中，不完全适配「移动网络 + 30fps」：
            // 单帧发起过多请求会挤占主线程，移动网络下并发过高也易被 CDN 限流。
            // 注意：枚举键是 DownloadMaxRequestPerFrame（无 s），属性名才是 DownloadMaxRequestsPerFrame。
            cacheParameters.AddParameter(EFileSystemParameter.DownloadMaxConcurrency, 6);
            cacheParameters.AddParameter(EFileSystemParameter.DownloadMaxRequestPerFrame, 4);

            // 看门狗超时。默认 0 表示禁用，弱网下请求会永久挂起（SandboxFileSystem.cs:104）。
            cacheParameters.AddParameter(EFileSystemParameter.DownloadWatchdogTimeout, 45);

            // 断点续传最小文件尺寸。默认 long.MaxValue 等于完全禁用续传（SandboxFileSystem.cs:109），
            // 移动网络下大 bundle 中断后要整包重下。
            cacheParameters.AddParameter(EFileSystemParameter.DownloadResumeMinimumSize, 1024L * 1024L);

            return new HostPlayModeOptions
            {
                BuiltinFileSystemParameters =
                    FileSystemParameters.CreateDefaultBuiltinFileSystemParameters(),
                CacheFileSystemParameters = cacheParameters,
                // 同时加载 Bundle 的并发上限。默认 int.MaxValue，会在进入区域时一次性打开过多 bundle。
                BundleLoadingMaxConcurrency = 8
            };
#endif
        }

        /// <summary>
        /// 释放资源包。用于退出游戏或切换大版本时回收。
        ///
        /// 注意：Destroy 会走 ForceDestroyLoader → AssetBundle.Unload(true)，
        /// 不检查引用计数，存活的句柄会静默失效。调用前必须确保 AssetLoader 已 ReleaseAll。
        /// </summary>
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

    /// <summary>
    /// 远端资源地址服务。YooAsset 只要求「文件名 → URL 列表」的映射，
    /// 具体 CDN 目录结构可在此按平台/渠道调整。
    /// </summary>
    public sealed class RemoteService : IRemoteService
    {
        private readonly string _hostServerURL;

        public RemoteService(string hostServerURL)
        {
            _hostServerURL = hostServerURL.TrimEnd('/');
        }

        public IReadOnlyList<string> GetRemoteUrls(string fileName)
        {
            // 目前返回单一 CDN 地址；需要灰度或多 CDN 时可返回多个候选（按优先级排序）
            return new[] { $"{_hostServerURL}/{fileName}" };
        }
    }
}
