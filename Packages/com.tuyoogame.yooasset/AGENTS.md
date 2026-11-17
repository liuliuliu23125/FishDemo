# Packages/com.tuyoogame.yooasset 规约（YooAsset 3.0.7）

> 第三方源码（TuYoo Games），**不改**。包是 **embedded**（`packages-lock.json` 为 `file:com.tuyoogame.yooasset`）。
> 本文件是日常入口；内部链路见 [yooasset_3.0.7](../../Docs/yooasset_3.0.7.md)，实践陷阱见 [yooasset_practices](../../Docs/yooasset_practices.md)。

## 1. 版本与平台

| 项 | 值 |
|---|---|
| 包版本 | **3.0.7**（精确锁定，不用 `3.0.x` 范围） |
| package.json | `unity: 2019.4`；依赖 SBP `1.21.25` |
| 回退 | 出问题降到 **3.0.6**；不要回退 2.3.19（12 个 API 点 + `await` 重写） |
| 图形/平台 | 纯 CPU/IO；与 Vulkan/ASTC 无关 |

3.0.x 半年内有 4 次破坏性变更，`SetFileSystemInstanceId` 在 3.0.4 加入、**6 天后 3.0.5 撤回**（见 `CHANGELOG.md`）。

## 2. 程序集边界

| 程序集 | 平台 | 说明 |
|---|---|---|
| `YooAsset` | 全平台 | 运行时；`autoReferenced=true`；宏 `YOOASSET_3` / `YOOASSET_3_0` / `YOOASSET_3_0_OR_NEWER` |
| `YooAsset.Editor` | Editor | `autoReferenced=true`；引用 `Unity.ScriptableBuildPipeline[.Editor]` |

- 项目程序集通过 asmdef 引用 `YooAsset`（`FishDemo.Resources` 已引用）。
- **`FishDemo.Editor` 可引用 `YooAsset.Editor`**（编辑器侧工具）。
- 运行时程序集**不得**引用 `YooAsset.Editor`。

## 3. 目录索引

| 目录 | 内容 | 何时读 |
|---|---|---|
| [Runtime/ResourcePackage](Runtime/ResourcePackage/AGENTS.md) | `ResourcePackage` 生命周期、版本/清单、下载/清理、选项类型 | 改初始化/更新流程、排查 PackageValid、操作失败 |
| [Runtime/ResourceManager](Runtime/ResourceManager/AGENTS.md) | Provider/句柄、引用计数、bundle 加载器、卸载路径 | 改加载/卸载、排查泄漏与二次释放 |
| [Runtime/FileSystem](Runtime/FileSystem/AGENTS.md) | `IFileSystem`、五种文件系统、双文件系统、参数 | 改 PlayMode、下载/解压/导入、缓存根目录 |
| [Runtime/BundleCache](Runtime/BundleCache/AGENTS.md) | `IBundleCache`、校验级别、淘汰策略 | 改缓存校验/清理、排查缓存损坏 |
| [Runtime/BundleHandle](Runtime/BundleHandle/AGENTS.md) | `IBundleHandle`、AssetBundle/Raw/Archive/Virtual | 改 bundle 装载方式、排查 `Unload(true)` |
| [Runtime/AsyncOperation](Runtime/AsyncOperation/AGENTS.md) | 调度器、时间片、优先级、`await` | 改启动预算、排查帧卡顿/操作不推进 |
| [Runtime/DownloadSystem](Runtime/DownloadSystem/AGENTS.md) | 下载并发、重试、断点续传、看门狗 | 改下载策略、排查下载卡死 |
| [Editor/BundleCollector](Editor/BundleCollector/AGENTS.md) | 收集配置模型、地址/过滤/打包规则 | 改分包、排查构建内容不对 |
| [Editor/BundleBuilder](Editor/BundleBuilder/AGENTS.md) | 六种构建管线、任务链、模拟构建 | 改构建流程、接 SBP/加密/报告 |

## 4. 核心心智模型

```
YooAssets (静态门面)
  └─ ResourcePackage (一个包 = 一个更新单位)
       ├─ FileSystemHost   ← 选择/组合 IFileSystem（Builtin/Sandbox/Editor/Web）
       │    └─ IBundleCache ← 缓存校验/写入/淘汰
       ├─ ResourceManager  ← Provider/Handle 引用计数、bundle 加载并发
       └─ AsyncOperationSystem ← 每帧时间片 + 包优先级调度
```

- **一切皆异步操作**：`InitializePackageAsync` / `RequestPackageVersionAsync` / `LoadAssetAsync` 返回 `AsyncOperationBase` 派生对象，由调度器分帧推进。
- **版本 → 清单 → Bundle → Asset** 是四层：版本决定清单，清单决定 bundle，bundle 提供 asset。
- **句柄是资源生命周期的唯一凭据**：每次加载拿新句柄，释放只释放自己的。

## 5. 日常铁律

1. **每次加载取新句柄，配对释放**；二次释放语义见 [ResourceManager §2](Runtime/ResourceManager/AGENTS.md)。
2. **清单必须先取版本号**，离线也一样；见 [ResourcePackage §3](Runtime/ResourcePackage/AGENTS.md)。
3. **chunk 之间禁止 bundle 交叉引用**；见 [ResourceManager §3](Runtime/ResourceManager/AGENTS.md)。
4. **编辑器模拟模式根目录必须非空**；见 [FileSystem §4](Runtime/FileSystem/AGENTS.md)。
5. **业务层禁止直接调 `Package.LoadAssetAsync`**，必须经 `AssetLoader`（返回 `AssetLease<T>`）。
6. **`Shutdown()` 前必须 `AssetLoader.ReleaseAll()`**；销毁路径会 `Unload(true)`。
7. **一个 `ResourcePackage` 只对应一个 PlayMode**；失败后 `RemovePackage` 再重建。
8. **模拟模式只验证流程**，上线前至少跑一次真实打包；见 [BundleBuilder §5](Editor/BundleBuilder/AGENTS.md)。

## 6. 必设参数（项目已在 `YooAssetInitializer.cs` 落实）

默认值对移动端危险，完整依据见 [yooasset_practices §2](../../Docs/yooasset_practices.md)。

| 参数 | 默认 | 项目值 |
|---|---|---|
| `MaxTimeSlice` | `long.MaxValue` | **10**（`SetAsyncOperationMaxTimeSlice`，最小钳制 10） |
| `BundleLoadingMaxConcurrency` | `int.MaxValue` | 8 |
| `DownloadMaxConcurrency` | 8 | 6 |
| `DownloadMaxRequestPerFrame` | 8 | 4 |
| `DownloadWatchdogTimeout` | 0（禁用） | 45 |
| `DownloadResumeMinimumSize` | `long.MaxValue` | 1 MB |
| `AutoUnloadBundleWhenUnused` | `false` | 保持 `false` |
| `InstallCleanupMode` | `None` | **待决策** |

本表是参数唯一权威；改值只改这里与 `YooAssetInitializer.cs`。

## 7. 与本项目的接口

| 文件 | 职责 |
|---|---|
| `Assets/Scripts/Runtime/Resources/YooAssetInitializer.cs` | 初始化、三模式 Options、版本+清单加载、`Shutdown` |
| `Assets/Scripts/Runtime/Resources/AssetLoader.cs` | 句柄获取/释放、`AssetLease<T>`、`ReleaseAll` |
| `Assets/Scripts/Editor/YooAssetSetup.cs` | 菜单 `YooAsset/FishDemo/初始化资源收集配置` |
| `Assets/Scripts/Editor/YooAssetEditorBootstrap.cs` | 菜单 `YooAsset/FishDemo/执行模拟构建并注入路径` |
| `Assets/BundleCollectorSetting.asset` | 收集配置落盘（当前只有 `DefaultPackage/Scene` 一组） |

- 命名空间是 **`FishDemo.AssetManagement`**，程序集名是 `FishDemo.Resources`；同名会让 C# 优先解析成程序集。
- 当前 YooAsset **已实现但无业务调用方**；SECTR/草/树仍走各自路径。

## 8. 扩展禁区与推荐点

**不要碰**（版本变动最频繁）：`IDownloadBackend` / `IDownloadRequest.UpdateRequest()`、`CustomAsyncOperation`、自定义 `IFileSystem`、友元程序集扩展。

**推荐扩展点**：`IRemoteService`、`IBundleDecryptor`、`IManifestDecryptor`、`IDownloadRetryPolicy`、`IDownloadURLPolicy`、`IBundlePackRule`、`IAddressRule`。

## 9. 代码位置

具体文件见 §3 各模块 AGENTS.md。**行号会随版本漂移**：引用前先 `grep` 符号，不要盲信行号。
