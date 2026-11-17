# YooAsset · Runtime/FileSystem 规约

> 第三方源码，不改。五种实现与参数细节见 [yooasset_3.0.7 §1.1.4 / §3.2–§3.3](../../../../Docs/yooasset_3.0.7.md)。

## 1. 职责

`IFileSystem` 是「某个位置上的 bundle 从哪来、怎么变成可用文件」的抽象：

```
InitializeAsync
RequestPackageVersionAsync / LoadPackageManifestAsync
LoadPackageBundleAsync / EnsurePackageBundleAsync / DownloadBundleAsync
ClearCacheAsync
```

归属判定：

- `CanAcceptBundle(bundle)`：这个 bundle 归不归我管；
- `IsDownloadRequired` / `IsUnpackRequired` / `IsImportRequired`：需不需要下载/解压/导入。

`FileSystemHost` 持有一个**有序列表**，按顺序找第一个 `CanAcceptBundle` 的文件系统；**列表最后一个永远是主文件系统**（版本/清单请求走它）。

## 2. 五种实现

| 实现 | 用于 | 行为 |
|---|---|---|
| `BuiltinFileSystem` | Offline / Host 的内置部分 | 只认 `BuiltinBundleCache` 里有的 bundle；只读；不下载 |
| `EditorFileSystem` | EditorSimulate | 认领所有；用 `AssetDatabase` 模拟；根目录必须非空 |
| `SandboxFileSystem` | Host 的缓存部分 | **兜底认领所有**；可下载、断点续传、缓存校验 |
| `WebServerFileSystem` | WebGL 服务器资源 | 只认已缓存；不主动下载 |
| `WebNetworkFileSystem` | WebGL 网络资源 | **兜底认领所有**；由 `IWebPreloadStrategy` 决定下载 |

双文件系统组合（Host）：

```
InitializeAsync(BuiltinFileSystemParameters, CacheFileSystemParameters)
→ 列表 = [Builtin, Sandbox]，主文件系统 = Sandbox
→ Builtin 先认领内置 bundle，其余落到 Sandbox
```

## 3. 参数

`FileSystemParameters` 用**类型名 + 根目录 + 参数字典**创建实例（`Type.GetType` + `Activator`）。默认工厂：

- `CreateDefaultEditorFileSystemParameters(root)`
- `CreateDefaultBuiltinFileSystemParameters([root])`
- `CreateDefaultSandboxFileSystemParameters(remoteService[, root])`
- `CreateDefaultWebServerFileSystemParameters(disableUnityWebCache)`
- `CreateDefaultWebNetworkFileSystemParameters(...)`

参数键名与项目值见包 [AGENTS.md §6](../../AGENTS.md)；校验级别见 [BundleCache §3](../BundleCache/AGENTS.md)，只在怀疑缓存损坏时临时提高。

## 4. 编辑器模拟模式硬约束

`EditorFileSystem.OnCreate` 在 `packageRoot` 为空时抛异常，异常被操作层吞成 `Failed`，表现为「初始化一直 false 且没有明显报错」。根目录由 `YooAssetEditorBootstrap` 注入。

## 5. 修改禁区

- 不自定义 `IFileSystem`（内部接口，版本变动频繁）。
- 不绕过 `FileSystemHost` 直接按 bundle 路径读文件；归属判定与缓存校验都在这一层。
- 清理缓存用 `ClearCacheAsync`，不要手删沙盒目录（catalog/manifest 会不一致）。

## 6. 相关

| 文档 | 何时读 |
|---|---|
| [yooasset_3.0.7 §1.1.4](../../../../Docs/yooasset_3.0.7.md) | 各文件系统初始化内部 |
| [yooasset_3.0.7 §3.2–§3.3](../../../../Docs/yooasset_3.0.7.md) | 下载调度、缓存校验 |
| [yooasset_practices §1](../../../../Docs/yooasset_practices.md) | 模拟模式根目录 |
| [../BundleCache/AGENTS.md](../BundleCache/AGENTS.md) | 缓存实现 |
| [../DownloadSystem/AGENTS.md](../DownloadSystem/AGENTS.md) | 下载策略 |
