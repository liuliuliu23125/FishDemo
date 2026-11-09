# YooAsset 3.0.7 模块规约

> 本目录是**嵌入式包**（`Packages/com.tuyoogame.yooasset/`），已从 manifest 移除依赖，Unity 自动识别。

**配套文档——按需读取，不要默认全读**：

| 文档 | 只在以下情况读 |
|---|---|
| [架构全貌](../../Docs/yooasset_3.0.7.md) | 要理解内部调用链路、模块职责、状态机；或要改动本包源码；或排查"为什么这么设计" |
| [实践细节与陷阱](../../Docs/yooasset_practices.md) | 要调参数（查依据）、遇到引用计数/卸载异常、设计 chunk 分包方案、需要审查记录与验证状态 |

本文件（84 行）覆盖日常 90% 场景，**先读完本文件再决定要不要展开上面两份**。

## 1. 版本锁定

**精确锁定 3.0.7，不要用 `3.0.x` 范围。**

依据：3.0.x 半年内 4 次破坏性变更，其中 `SetFileSystemInstanceId` 在 3.0.4 加入、**6 天后 3.0.5 撤回**（见 `CHANGELOG.md`）。

若出问题，**降到 3.0.6**，不要回退 2.3.19（需改 12 个 API 点 + 全部 `await` 重写，因为 2.3.19 没有 `GetAwaiter()`）。

## 2. 必设参数（已在代码中落实）

默认值对移动端是危险默认，不设置会踩坑。完整依据见 [yooasset_practices §2](../../Docs/yooasset_practices.md)。

| 参数 | 默认 | 项目值 |
|---|---|---|
| `MaxTimeSlice` | `long.MaxValue` | 10（硬下限） |
| `BundleLoadingMaxConcurrency` | `int.MaxValue` | 8 |
| `DownloadMaxConcurrency` | 8 | 6 |
| `DownloadMaxRequestPerFrame` | 8 | 4 |
| `DownloadWatchdogTimeout` | 0（禁用） | 45 |
| `DownloadResumeMinimumSize` | `long.MaxValue` | 1MB |
| `AutoUnloadBundleWhenUnused` | `false` | 保持 `false` |

除 `InstallCleanupMode`（待决策）外均已在 `YooAssetInitializer.cs` 设置。改值时必须同步本表。

## 3. 四条铁律

**① 每次加载取新句柄，配对释放**
`LoadAssetAsync` 都会 `CreateHandle` + `RefCount++`。禁止在业务层复用同一句柄，禁止重复 `Release`（会抛 `YooInternalException`）。封装层返回 `AssetLease<T>`（资源 + 句柄）。

**② 清单必须先取版本号**
`LoadPackageManifestOperation` 在 `PackageVersion` 为空时直接 `SetError`。不存在"传 null 加载内置清单"，离线也必须先 `RequestPackageVersionAsync()`。

**③ chunk 之间禁止 bundle 交叉引用**
源码注释原文："互相引用的资源包无法卸载！"。构建期**不做环检测**，形成交叉引用会永久泄漏。公共资源必须单独成包。

**④ 编辑器模拟模式根目录必须非空**
`EditorFileSystem.OnCreate` 在 `packageRoot` 为空时抛异常，被吞成 `Failed`，表现为"初始化永远返回 false 且无明显报错"。根目录由 `YooAssetEditorBootstrap` 注入。

## 4. 扩展禁区

**不要碰**（版本变动最频繁）：
- `IDownloadBackend` / `IDownloadRequest.UpdateRequest()`
- `CustomAsyncOperation`
- 自定义 `IFileSystem` 实现
- 友元程序集级扩展

**推荐扩展点**：`IRemoteService`、`IBundleDecryptor`、`IManifestDecryptor`、`IDownloadRetryPolicy`、`IDownloadUrlPolicy`、`IBundlePackRule`、`IAddressRule`

## 5. 与本项目的接口

| 文件 | 职责 |
|---|---|
| `Assets/Scripts/Runtime/Resources/YooAssetInitializer.cs` | 初始化、清单加载、三模式切换 |
| `Assets/Scripts/Runtime/Resources/AssetLoader.cs` | 句柄获取/释放，返回 `AssetLease<T>` |
| `Assets/Scripts/Editor/YooAssetSetup.cs` | 菜单 `YooAsset/FishDemo/初始化资源收集配置` |
| `Assets/Scripts/Editor/YooAssetEditorBootstrap.cs` | 菜单 `YooAsset/FishDemo/执行模拟构建并注入路径` |

**禁止**在业务层直接调 `Package.LoadAssetAsync`，必须经 `AssetLoader`。

**命名空间是 `FishDemo.AssetManagement`，不是 `FishDemo.Resources`**：程序集名与命名空间不能同名，否则 C# 优先解析为程序集，类型找不到。asmdef 名保持 `FishDemo.Resources`，namespace 已分离。

**`Shutdown()` 前必须 `AssetLoader.ReleaseAll()`**：`Destroy()` 走 `AssetBundle.Unload(true)`，不检查 RefCount。

## 6. 代码位置索引

| 改什么 | 去哪 |
|---|---|
| 初始化流程 | `Runtime/ResourcePackage/ResourcePackage.cs` `InitializePackageAsync` |
| 异步调度 / 时间片 | `Runtime/AsyncOperation/AsyncOperationSystem.cs` |
| 下载调度 | `Runtime/FileSystem/Services/SandboxFileSystem/SandboxFileSystem.cs` |
| 缓存校验 | `Runtime/BundleCache/` |
| 句柄与引用计数 | `Runtime/ResourceManager/Providers/ProviderBase.cs` |
| 卸载逻辑 | `Runtime/ResourceManager/Operations/Internal/LoadBundleOperation.cs` |
| 收集器数据结构 | `Editor/BundleCollector/BundleCollectorSetting.cs` |
| 打包规则 | `Editor/BundleCollector/DefaultRules/DefaultBundlePackRule.cs` |

**行号会随版本漂移**：引用前先 `grep` 定位，不要盲信行号。依据是方法名和代码内容。
