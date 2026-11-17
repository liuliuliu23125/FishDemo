# YooAsset · Runtime/ResourcePackage 规约

> 第三方源码，不改。完整链路见 [yooasset_3.0.7 §1](../../../../Docs/yooasset_3.0.7.md)。
> 包级入口见 [../../AGENTS.md](../../AGENTS.md)。

## 1. 职责

`ResourcePackage` 是**一个更新单位**的公开门面，持有两个核心对象：

- `FileSystemHost`：文件系统 + 当前激活清单 + bundle 归属判定；
- `ResourceManager`：Provider/Handle 引用计数与 bundle 加载并发。

包有效性：`PackageValid => FileSystemHost != null && ActiveManifest != null`。

## 2. 初始化状态机

`InitializePackageAsync(InitializePackageOptions)` → `InitializePackageOperation`：

```
SetPlayMode → CheckOptions → CreateCore → InitFileSystem → Done
```

- Options 子类决定 PlayMode：`EditorSimulateModeOptions` / `OfflinePlayModeOptions` / `HostPlayModeOptions` / `WebPlayModeOptions` / `CustomPlayModeOptions`。
- 校验：`BundleLoadingMaxConcurrency > 0`。
- **同一实例重复初始化会抛** `already initialized`。失败后 `TryResetAfterFailure()` 会销毁内部对象，但项目层仍应先 `RemovePackage` 再重建。
- `RemovePackage` 要求 `InitializeStatus == None`；销毁包裹用 `DestroyPackageAsync()`（内部会 `UnloadAllAssetsOptions(shouldReleaseHandles:true, shouldLockLoading:true, shouldWaitUnloadUnused:true)`）。

## 3. 版本与清单

必须按顺序：

```
RequestPackageVersionAsync()  →  versionOp.PackageVersion
LoadPackageManifestAsync(new LoadPackageManifestOptions(version, timeout))
```

- `LoadPackageManifestOperation` 在 `PackageVersion` 为空时直接 `SetError("Package version is null or empty.")`。
- 已激活同版本清单会直接成功（`ActiveManifest.PackageVersion == options.PackageVersion`）。
- `PrefetchManifestAsync` 用于预取，不改变激活清单。
- 当前版本/备注/详情：`GetPackageVersion()` / `GetPackageNote()` / `GetPackageDetails()`。

## 4. 资源 API（全部返回句柄）

| API | 句柄 |
|---|---|
| `LoadAssetAsync` | `AssetHandle` |
| `LoadSubAssetsAsync` | `SubAssetsHandle` |
| `LoadAllAssetsAsync` | `AllAssetsHandle` |
| `LoadSceneAsync` | `SceneHandle` |
| `LoadBundleFileAsync` | `BundleFileHandle` |

同步版本（`LoadAssetSync` 等）只应在编辑器/极小资源使用；移动端优先异步，避免主线程尖峰。

## 5. 下载 / 清理 / 预取

- `CreateResourceDownloader(ResourceDownloaderOptions)` → `ResourceDownloaderOperation`（下载 bundle）。
- `CreateResourceUnpacker` / `CreateResourceImporter`：解压/导入内置或流式资源。
- `ClearCacheAsync(ClearCacheOptions)`：按版本/全部清理缓存。
- `EnsureBundleFileAsync`：确保指定 bundle 文件就绪（不加载成 AssetBundle）。
- 下载细节见 [../DownloadSystem/AGENTS.md](../DownloadSystem/AGENTS.md) 与 [../FileSystem/AGENTS.md](../FileSystem/AGENTS.md)。

## 6. 数据模型

`PackageManifest` → `PackageAsset` / `PackageBundle`；构建侧用 `BuildAsset` / `BuildBundle` / `BuildManifest`。`AssetInfo` 是运行时轻量描述（位置、GUID、类型、依赖）。

## 7. 项目接口

`Assets/Scripts/Runtime/Resources/YooAssetInitializer.cs`：

- 三种模式：编辑器 `EditorSimulateModeOptions`、无远端 `OfflinePlayModeOptions`、有远端 `HostPlayModeOptions`。
- 初始化参数（并发/下载/续传）见包 [AGENTS.md §6](../../AGENTS.md)。
- `Shutdown()` → `YooAssets.Destroy()`；调用方必须先 `AssetLoader.ReleaseAll()`。

## 8. 相关

| 文档 | 何时读 |
|---|---|
| [yooasset_3.0.7 §1](../../../../Docs/yooasset_3.0.7.md) | 初始化/加载/释放全链路 |
| [../ResourceManager/AGENTS.md](../ResourceManager/AGENTS.md) | 句柄与引用计数 |
| [../FileSystem/AGENTS.md](../FileSystem/AGENTS.md) | 文件系统与 PlayMode |
