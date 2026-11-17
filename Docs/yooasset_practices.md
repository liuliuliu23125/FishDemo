# YooAsset 3.0.7 项目实践细节

> **本文件是查阅型文档，不要通读。** 按需跳转：
>
> | 你要做什么 | 读哪节 |
> |---|---|
> | 排查"编辑器里初始化一直失败" | §1 |
> | 调下载/加载参数，想知道依据 | §2 |
> | 遇到重复释放、句柄失效 | §3 |
> | 设计 chunk 分包，担心内存泄漏 | §4 |
> | 决定何时调卸载 | §5 |
> | 退出游戏时资源没释放 | §6 |
> | 想知道之前审查发现了什么 | §7 |
> | 确认哪些验证做了、哪些没做 | §8 |
>
> 日常写资源代码读 [包内 AGENTS.md](../Packages/com.tuyoogame.yooasset/AGENTS.md) 即可。要看内部调用链路读 [架构全貌](yooasset_3.0.7.md)。

## 1. 编辑器模拟模式的根目录硬约束

`EditorFileSystem.OnCreate` 在 `packageRoot` 为空或空串时**直接抛 `ArgumentException`**（`Packages/com.tuyoogame.yooasset/Runtime/FileSystem/Services/EditorFileSystem/EditorFileSystem.cs:217-218`）。

异常会被 `AsyncOperationBase` 兜底吞成 `Failed`，表现为**“编辑器里初始化永远返回 false”且无明显报错**——这是最难排查的一类问题。

| 做法 | 结果 |
|---|---|
| `CreateDefaultEditorFileSystemParameters(null)` | ❌ 一定失败 |
| 传任意非空字符串 | ❌ 后续找不到清单文件 |
| 传 `BundleSimulateBuilder.SimulateBuild()` 返回的 `PackageRootDirectory` | ✅ 正确 |

**本项目的处理**：
- `YooAssetEditorBootstrap.EnsureSimulateBuild()` 跑模拟构建，拿到 `PackageRootDirectory`
- 注入 `YooAssetInitializer.EditorSimulatePackageRoot`（Runtime 不能引用 `YooAsset.Editor`，故用静态字段传递）
- 若未注入，`BuildOptions` 抛 `InvalidOperationException` 并提示先执行菜单

## 2. 参数选择依据

### 2.1 三种并发是不同层面

| 参数 | 作用层面 | 默认值 | 项目值 | 失控后果 |
|---|---|---|---|---|
| `BundleLoadingMaxConcurrency` | 同时**打开**多少 bundle | `int.MaxValue` | 8 | 进区域时一次性打开过多 AB，内存尖峰 |
| `DownloadMaxConcurrency` | 同时**下载**多少文件 | 8 | 6 | 被 CDN 限流、移动网络拥塞 |
| `DownloadMaxRequestPerFrame` | 每帧**发起**多少请求 | 8 | 4 | 单帧主线程卡顿 |

### 2.2 危险默认值

| 参数 | 默认值 | 项目值 | 不设的后果 |
|---|---|---|---|
| `MaxTimeSlice` | `long.MaxValue` | 10 | 单帧跑完所有 operation，掉帧 |
| `DownloadWatchdogTimeout` | `0`（禁用） | 45 | 弱网下请求永久挂起 |
| `DownloadResumeMinimumSize` | `long.MaxValue` | 1MB | 断点续传完全失效 |
| `InstallCleanupMode` | `None` | **待决策** | 覆盖安装后旧缓存残留 |
| `AutoUnloadBundleWhenUnused` | `false` | 保持 `false` | 设 `true` 后交叉引用时白耗 CPU 且不卸载 |

### 2.3 `MaxTimeSlice` 的硬下限

`AsyncOperationSystem.cs:26` 定义 `MinTimeSlice = 10`，`:53-57` 处钳制。**低于 10 会被强制改成 10 并打 warning**，所以 10 是能取到的最小值，不存在"设小一点更安全"。

### 2.4 枚举名与属性名不一致

枚举键是 `DownloadMaxRequestPerFrame`（**无 s**），属性是 `DownloadMaxRequestsPerFrame`（**有 s**）。`AddParameter` 必须用枚举键。

### 2.5 注释与代码不一致

`EFileSystemParameter.cs:45,50` 注释写"默认值 10 / 5"，但 `SandboxFileSystem.cs:91,99` 实际是 **8 / 8**。**调参信代码不信注释。**

## 3. 引用计数机制

每次 `LoadAssetAsync` 都会 `CreateHandle` 并 `RefCount++`（`ProviderBase.CreateHandle`）。

```
LoadAssetAsync → Provider.CreateHandle → RefCount++ → 返回新句柄
handle.Release() → Provider.ReleaseHandle → RefCount-- → 从 _handles 移除
```

**为什么不能复用句柄**：多个调用方共享一个句柄时，任一方 `Release` 都会让 RefCount 归零，其他持有者拿到的资源已销毁。

**二次 Release 的实际行为（3.0.7）**：同一 handle 第二次 `Release()` **不抛**——`HandleBase.Release` 发现 `Provider == null`，打 warning 后返回。会抛的是直接调 `Provider.ReleaseHandle`（`_handles.Remove` 失败），以及 `ReleaseAllHandles` / `UnloadAllAssets(shouldReleaseHandles:true)` 与业务 `Release` 竞争（`RefCount <= 0`）。真正的规则是**不要多处持有并释放同一个 handle**。

**因此 `AssetLoader` 返回 `AssetLease<T>`**（资源 + 句柄），调用方必须持有句柄才能释放。

## 4. 分块流式的两个陷阱

### 4.1 bundle 交叉引用导致永久泄漏

`LoadBundleOperation.cs:253-264` 源码注释原文：

```
// 注意：互相引用的资源包无法卸载！
```

默认分支（非 `YOOASSET_EXPERIMENTAL`）要求**所有引用我的 bundle 都已销毁**才允许卸载。chunk 之间形成交叉引用会永久泄漏，而 YooAsset 构建期**不做环检测**。

**落地规则**：
- chunk bundle 只能依赖共享 bundle（材质、shader、公共贴图）
- **chunk 之间不得互相依赖**
- 公共资源必须单独成包，不能散落在某个 chunk 里

### 4.2 编辑器模拟模式测不出依赖问题

模拟模式产出的清单里 `DependentBundleIDs` 是**空的**（`TaskCreateManifest_ESBP.cs:16-19`，`processBundleDepends=false`）。

**后果**：4.1 的交叉引用问题**在编辑器里永远测不出来**，只能在真实构建 + 真机发现。

## 5. 卸载策略

```csharp
// 每卸载 M 个 chunk（或每 2~3 秒）调一次
await Package.UnloadUnusedAssetsAsync();
```

- `UnloadUnusedAssetsAsync` **不调用** `Resources.UnloadUnusedAssets()`（全库仅 `UnloadAllAssetsOperation.cs:95` 一处调用），开销可控。
- `MaxLoopCount = 10`（`ResourcePackage.cs:284`）处理深层依赖，循环内 `IsBusy` 会让出，**不会卡一帧**。
- ❌ 不要每个 chunk 卸载都调（每次遍历整个 loader 字典）。

## 6. `Shutdown()` 的风险

`YooAssets.Destroy()` 最终走 `ForceDestroyLoader()` → `AssetBundle.Unload(true)`，**不检查 RefCount**。存活句柄会静默失效（访问时拿到已销毁对象）。

**调用前必须确保 `AssetLoader.ReleaseAll()` 已执行。**

## 7. 对抗性审查记录（2026-11-09）

对本模块做过一次独立对抗审查，结论为「不通过」，暴露的问题已全部修复。

| 级别 | 问题 | 现状 |
|---|---|---|
| P0 | `CreateDefaultEditorFileSystemParameters(null)` 必然失败 | ✅ 改由 `YooAssetEditorBootstrap` 注入真实路径 |
| P1 | `LoadAssetAsync<T>` 只返回 `T`，句柄被吞无法释放 | ✅ 改返回 `AssetLease<T>` |
| P1 | 7 个必设参数只设 2 个，`MaxTimeSlice` 完全没调 | ✅ 已补齐 |
| P1 | 嵌入包未生效，实际仍用 `PackageCache` 的 registry 副本 | ✅ lock 已变 `"source": "embedded"` |
| P1 | `IsReady` 用 `InitializeStatus`，清单失败仍返回 true | ✅ 改独立标志 |
| P1 | 初始化失败后无法重试 | ✅ 检测 `Failed` 先 `RemovePackage` |
| P1 | `Shutdown()` 不检查引用计数 | ✅ 已标注约束 |
| P2 | 3 处行号错误（35-42→38-44、301→302、30→31 个参数） | ✅ 已修正 |
| P2 | 程序集名与命名空间同名导致类型解析失败 | ✅ namespace 改为 `FishDemo.AssetManagement` |

**审查中确认“不是问题”的**（避免后人重复怀疑）：
- 嵌入包 `.meta` 完整，GUID 与 PackageCache 副本零差异，不会重生成
- `ReleaseAll` 不会二次释放抛异常（`Release` 先从 list 移除，`HandleBase.Release` 二次调用只 warning）
- `YooAssetSetup` 重复执行不产生重复分组（`RemoveAll(PackageName)` 是整包重建）

## 8. 验证状态

| 项 | 结果 |
|---|---|
| `FishDemo.Resources.dll` / `FishDemo.Editor.dll` 编译 | ✅ 0 error（2026-11-09） |
| 嵌入包生效 | ✅ `packages-lock.json`: `"source": "embedded"` |
| 菜单模拟构建产出 | ⏳ 未测 |
| 运行时初始化 | ⏳ 未测（无 Bootstrap 入口调用） |

## 9. 行号引用说明

本文与 `Docs/` 中的 `文件:行号` 均基于 **3.0.7**。升级版本后行号必然偏移，**引用前先用 `grep` 定位，不要盲信行号**。行号只用于快速跳转，真正依据是方法名和代码内容。
