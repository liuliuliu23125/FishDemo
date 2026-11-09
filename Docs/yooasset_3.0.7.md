# YooAsset 3.0.7 架构与使用手册

> **本文件 2000 行，是查阅型参考，不要通读。** 按需跳转：
>
> | 你要做什么 | 读哪节 |
> |---|---|
> | 理解"资源管理系统要解决什么问题" | §0 |
> | 搞清初始化/加载/释放的完整链路 | §1 |
> | 查某个模块的职责边界 | §2 |
> | 调异步调度、下载并发、缓存校验 | §3.1 ~ §3.3 |
> | 排查引用计数、bundle 卸载不掉 | §3.4 ~ §3.5 |
> | 抄一份能跑的初始化/加载代码 | §4 |
> | 调移动端参数 | §5 |
> | 遇到具体报错或行为异常 | §6（24 条陷阱） |
> | 判断能不能自定义某个接口 | §7 |
> | 配置 Bundle Collector / 构建管线 | §8 |
> | 查类型、枚举、参数默认值 | §9 |
> | 快速定位"改哪里" | 附录 A |
>
> **日常写资源代码**只需读 [包内 AGENTS.md](../Packages/com.tuyoogame.yooasset/AGENTS.md)（84 行）。**遇到具体问题**查 [实践细节](yooasset_practices.md)（按症状分节）。

> 分析对象：`E:/FishDemo/Packages/com.tuyoogame.yooasset/`，`package.json` 声明 `version = 3.0.7`，`unity = 2019.4`，仓库 revision `8cb910de899050d95b01644e7a3bd7a643f226c0`。
> 目标环境：Unity 2022.3.62f2 + URP 14，Android 旗舰（iQOO 16），30fps，大世界分块流式加载。
> 本文所有结论标注为 **【代码事实】**（可直接在源码中核对）、**【官方声称】**（CHANGELOG 原文）、**【推断】**（基于事实的推理，代码中无直接陈述）。
> 引用格式：`路径:行号`，路径相对包根目录 `Packages/com.tuyoogame.yooasset/`。行号基于 3.0.7，升级后需重新定位。

**阅读约定**：3.0.7 与 2.x 是两套不同的内部结构。本文只讲 3.0.7；凡写到"2.x 不同"的地方都是为了提醒迁移陷阱，不构成版本对比分析。

---

## 0. 系统要解决的问题（第一性原理）

### 0.1 从"玩家要看到某个资源"到"资源出现在屏幕上"

把 YooAsset 的 API 糖衣剥掉，运行时它只做一件事：**在"内存 / IO 带宽 / 主线程时间 / 显存"四个硬约束下，把 `string location` 稳定地映射为 `UnityEngine.Object`，并保证这份映射的生命周期正确。**

这条路径上必须经过 7 个环节，每个环节都有不可回避的物理约束：

| # | 环节 | 物理约束 | 约束失败的后果 | 3.0.7 的应对层 |
|---|---|---|---|---|
| 1 | **定位**：`location` → 清单条目（`PackageAsset`） | 内存：清单常驻内存，10 万条目 × 每条约 100~200B | 清单过大 → 常驻内存挤压资源预算 | `PackageManifest.AssetsByLocation`（`Runtime/ResourcePackage/PackageManifest.cs:88`），构建期可开 `ReplaceAssetPathWithAddress` 缩减体积 |
| 2 | **依赖闭包**：算出主包 + 全部依赖包 | 主线程：查表 + 构造 `List<BundleInfo>` | 闭包错误 → 运行时缺包 / 重复 IO | `FileSystemHost.GetDependentBundleInfos`（`Runtime/ResourcePackage/FileSystemHost.cs:194`）；清单里预存了 `DependentBundleIDs` |
| 3 | **字节就绪**：bundle 文件在本地可读 | 磁盘 IO + 网络 IO | 同步 IO 卡主线程；并发过高挤爆网络栈 | `BundleCache`（`Runtime/BundleCache/`）+ `DownloadSystem` 调度器 |
| 4 | **引擎载入**：字节 → `AssetBundle` | 主线程 + 内存（LZMA 需解压，LZ4 需块索引） | 同帧载入过多 → 掉帧；`AssetBundle.LoadFromFile` 压缩格式选错 → 内存翻倍 | `LoadLocalAssetBundleOperation`（`Runtime/BundleCache/Operations/Common/LoadLocalAssetBundleOperation.cs:134`） |
| 5 | **取对象**：`AssetBundle.LoadAsset` → `Object` | 主线程；纹理/网格在 GPU 侧另有显存 | 同步 `LoadAsset` 直接阻塞 | `ABHLoadAssetOperation`（`Runtime/BundleHandle/Services/AssetBundleHandle/Operations/ABHLoadAssetOperation.cs:63`） |
| 6 | **调度**：把上述异步工作分摊到多帧 | 主线程单帧预算（30fps = 33.3ms） | 单帧集中推进 → 掉帧 | `AsyncOperationSystem` 时间切片（`Runtime/AsyncOperation/AsyncOperationSystem.cs:68`） |
| 7 | **回收**：引用归零 → 真正卸载 | 何时 `Unload(true)`、何时 `Resources.UnloadUnusedAssets()` | 泄漏（内存涨）或过早卸载（花屏/黑图） | `ProviderBase.RefCount` + `LoadBundleOperation.RefCount` 两级计数 |

### 0.2 本项目的关键约束如何映射到这些环节

- **大世界分块流式（64~128m chunk）**：等价于"高频、小批量、位置驱动的第 3~5 环节请求"。要求 ① 单帧 IO 不能爆 ② bundle 粒度必须与 chunk 对齐（第 2 环节）③ 卸载必须及时（第 7 环节）。
- **30fps**：单帧总预算 33.3ms。YooAsset 自己能吃掉的预算必须显式设上限，否则默认值（见 §5）会吃掉整个预算。
- **Android 旗舰**：`streamingAssets` 在 APK 内（压缩的 zip 区域），必须解压到沙盒才能 `LoadFromFile` —— 这是 `IBundleUnpackPolicy` 存在的物理原因（`Runtime/Interfaces/IBundleUnpackPolicy.cs:36`，`DefaultBundleUnpackPolicy` 在 Android 对加密/Raw/Archive 返回 true）。

---

## 1. 完整调用链路

### 1.1 初始化

#### 1.1.1 三级入口

**【代码事实】** 初始化分三层，每层创建的对象不同：

```
YooAssets.Initialize()                      // 全局静态层，进程级单例
  └─ CreatePackage(packageName, priority)   // 包裹层，可多包裹
       └─ package.InitializePackageAsync(options)  // 包裹内部核心对象
```

**第 1 层：`YooAssets.Initialize(ILogger logger)`** — `Runtime/YooAssets.cs:49-70`

做的事只有 3 件（不创建任何资源相关对象）：

1. `YooLogger.Current = logger;`（`YooAssets.cs:54`）
2. 创建一个 `DontDestroyOnLoad` 的 `GameObject("[YooAssets]")`，挂 `YooAssetsDriver`（`YooAssets.cs:57-59`）；`#if DEBUG` 下再挂 `DiagnosticBehaviour`（`:63`）
3. `AsyncOperationSystem.Initialize();`（`YooAssets.cs:67`）→ 内部 `Stopwatch.StartNew()` 并创建**全局调度器** `YOOASSET_GLOBAL_SCHEDULER`（`Runtime/AsyncOperation/AsyncOperationSystem.cs:95-99`，优先级 `uint.MaxValue`）

驱动器的每帧回调是关键：`YooAssetsDriver.Update()` → `YooAssets.Update()` → `AsyncOperationSystem.Update()`（`Runtime/YooAssetsDriver.cs:21-24`）。**这是整个异步系统的时钟源，没有它任何异步操作都不会推进。**

重复调用会抛 `InvalidOperationException("YooAssets is already initialized.")`（`YooAssets.cs:51-52`）。

**第 2 层：`YooAssets.CreatePackage(packageName, packagePriority)`** — `Runtime/YooAssets.cs:127-140`

- `new ResourcePackage(packageName)`（`:133`）—— 此时 `_resourceManager` / `_fileSystemHost` 都还是 `null`（见构造函数 `Runtime/ResourcePackage/ResourcePackage.cs:58-60`）
- `AsyncOperationSystem.CreatePackageScheduler(packageName, packagePriority)`（`:137`）—— **每个包裹一个独立调度器**，这是 3.0 相对 2.x 的结构性差异（2.x 是全局单队列）

**第 3 层：`package.InitializePackageAsync(options)`** — `Runtime/ResourcePackage/ResourcePackage.cs:91-107`

- 若上次初始化是 `Failed`，先 `TryResetAfterFailure()` → `InternalDestroy()`（`:97`、`:108-114`）。**这意味着初始化失败可以重试，不需要重新 CreatePackage。**
- 若 `_initializeOp != null` 直接抛异常（`:100-101`）—— **同一个包裹只能初始化一次**（失败时走上面的 reset 分支除外）。
- 创建 `InitializePackageOperation` 并投入该包裹的调度器（`:104-105`）。

#### 1.1.2 `InitializePackageOperation` 内部五步

**【代码事实】** `Runtime/ResourcePackage/Operations/InitializePackageOperation.cs:10-18` 定义了 `ESteps`：

```
None → SetPlayMode → CheckOptions → CreateCore → InitFileSystem → Done
```

**步 1 `SetPlayMode`（`:43-59`）**：靠 **options 的具体类型** 判定 `EPlayMode`，不是靠枚举字段。`EditorSimulateModeOptions` → `EditorSimulateMode`，以此类推；未识别的类型直接 `NotImplementedException`。

**步 2 `CheckOptions`（`:61-73`）**：唯一校验是 `BundleLoadingMaxConcurrency > 0`（`:64`）。

**步 3 `CreateCore`（`:75-85`）** —— **这一层才是真正分配核心对象的地方**：

```csharp
string packageName = _package.PackageName;
var resourceManager = new ResourceManager(packageName);       // :78
var fileSystemHost  = new FileSystemHost(packageName);        // :79
resourceManager.Initialize(_options, fileSystemHost);         // :80
_fileSystemHost = fileSystemHost;
_package.InternalInitialize(resourceManager, fileSystemHost); // :83
```

`ResourceManager.Initialize` 做的事（`Runtime/ResourceManager/ResourceManager.cs:67-74`）：保存 host 引用、读 3 个全局选项（`BundleLoadingMaxConcurrency` / `AutoUnloadBundleWhenUnused` / `WebGLForceSyncLoadAsset`）、**订阅 `SceneManager.sceneUnloaded`**（`:73`，用于场景自动回收）。

**步 4 `InitFileSystem`（`:87-141`）**：按 playMode 选择不同的 `FileSystemParameters` 组合，交给 `FileSystemHost.InitializeAsync`：

| PlayMode | 文件系统组合 | 代码 |
|---|---|---|
| EditorSimulateMode | `[EditorFileSystem]` | `:94` |
| OfflinePlayMode | `[BuiltinFileSystem]` | `:99` |
| HostPlayMode | `[BuiltinFileSystem, SandboxFileSystem]` | `:104` |
| WebPlayMode | `[WebServerFileSystem, WebNetworkFileSystem]` | `:109` |
| CustomPlayMode | `options.FileSystemParameterList`（**列表最后一个为主文件系统**） | `:114` |

`InitializeFileSystemOperation`（`Runtime/ResourcePackage/Operations/Internal/InitializeFileSystemOperation.cs:67-93`）**按列表顺序逐个**创建并初始化，进度按等权折算（`:99-101`）。创建方式是**反射**：`fileSystemParams.CreateFileSystem(packageName)` → `Type.GetType(FileSystemTypeName)` + `Activator.CreateInstance`（`Runtime/FileSystem/FileSystemParameters.cs:62-85`）。

最后 `FileSystemHost.GetMainFileSystem()` 返回**列表最后一个**（`Runtime/ResourcePackage/FileSystemHost.cs:113-119`）——**这是"清单从哪个文件系统读"的判定依据**（`LoadPackageManifestOperation` 用它，见 `Runtime/ResourcePackage/Operations/LoadPackageManifestOperation.cs:70`）。所以 HostPlayMode 的清单来自 **SandboxFileSystem**（列表第二），不是 Builtin。

**步 5 `Done`**：成功即 `SetResult()`。**注意：初始化结束时 `ActiveManifest` 仍为 `null`** —— 清单要等 `LoadPackageManifestAsync` 才设置。

#### 1.1.3 三个核心对象的职责边界

**【代码事实】** 这是最容易混淆的地方，明确划分如下：

| 对象 | 文件 | 只做什么 | 明确不做什么 |
|---|---|---|---|
| **`FileSystemHost`** | `Runtime/ResourcePackage/FileSystemHost.cs`（571 行） | ① 持有 `List<IFileSystem>`（`:12`）② 持有 `ActiveManifest`（`:27`）③ **bundle → 归属文件系统** 的路由（`GetOwnerFileSystem`，`:126-139`，遍历 `CanAcceptBundle`）④ 构造 `BundleInfo`（`:147-160`）⑤ 生成下载/解压/导入列表（`:272-570`） | 不读写文件、不加载 bundle、不管引用计数 |
| **`ResourceManager`** | `Runtime/ResourceManager/ResourceManager.cs`（701 行） | ① `Provider` 字典（`_providerDict`，`:18`）② **`LoadBundleOperation` 字典**（`_bundleLoaderDict`，`:19`）—— 同一 bundle 在全包裹内只有一个 loader ③ bundle 并发闸门（`_bundleLoadingCounter` vs `BundleLoadingMaxConcurrency`，`:569-572`）④ 加载锁定（`:577-593`）⑤ 场景句柄表（`:20-21`） | 不碰字节、不碰 AssetBundle API、不碰缓存目录 |
| **`BundleCache`**（接口 `IBundleCache`） | `Runtime/BundleCache/Interfaces/IBundleCache.cs` | ① 字节层：`InitializeAsync` / `LoadBundleAsync` / `WriteCacheAsync` / `VerifyCacheAsync` / `ClearCacheAsync` ② 产出 **`IBundleHandle`**（引擎对象层的句柄）③ 缓存条目表与磁盘布局 | 不知道 `Provider` 是谁、不知道引用计数、不知道下载调度（下载是 `FileSystem` 的事） |

**关键洞察【推断】**：3.0 把 2.x 里"文件系统既管缓存又管加载"的职责拆成了 **FileSystem（路由 + 下载）→ BundleCache（字节 → 引擎对象）→ BundleHandle（对象级操作）** 三段。这三段都可以独立替换（`IFileSystem` / `IBundleCache` / `IBundleHandle` 都是 internal 接口），代价是调用链更长（见 1.2）。

#### 1.1.4 各文件系统的初始化内部（与移动端相关的）

**`SandboxFileSystem`**（HostPlayMode 的第二段，`Runtime/FileSystem/Services/SandboxFileSystem/Operations/SFSInitializeOperation.cs`）：

```
CheckPlatform → CheckParameter → CheckAppFootprint → InitializeBundleCache → CreateScheduler → Done
```

- **`CheckAppFootprint`（`:60-99`）**：读 `<缓存根>/ManifestFiles/ApplicationFootprint.bytes`，与当前 `Application.identifier` 比较。不一致 = 覆盖安装后首次启动 → 按 `InstallCleanupMode` 清缓存（`:69-93`）。**默认 `None`（什么都不做，`:65`）**，于是旧缓存会残留 —— 这是本项目必须显式配置的参数（见 §5）。
- **`InitializeBundleCache`（`:101-124`）**：`SandboxBundleCache.InitializeAsync()` → 扫描 + 校验（详见 §3.3）。**这是初始化耗时的主要来源。**
- **`CreateScheduler`（`:126-142`）**：创建 `DownloadSchedulerOperation` 并作为**独立任务**投入包裹调度器（`:137`）。`maxConcurrency = DownloadMaxConcurrency`，`maxRequestsPerFrame = DownloadMaxRequestsPerFrame`。

**`BuiltinFileSystem`**（`Runtime/FileSystem/Services/BuiltinFileSystem/Operations/BFSInitializeOperation.cs:8-20`）：

```
CheckPlatform → CheckAppFootprint → CopyPackageManifest → InitializeBuiltinBundleCache → InitializeUnpackBundleCache → CreateScheduler → Done
```

- 额外维护两个 cache：`BuiltinBundleCache`（**只读**，`Runtime/BundleCache/Services/BuiltinBundleCache/BuiltinBundleCache.cs:84`）和 `UnpackBundleCache`（可写 `SandboxBundleCache`，`:367`）。
- `BuiltinBundleCache` 的条目来自 **`BuiltinCatalog.bytes`**（`BuiltinCatalogConsts.BinaryFileName`，`Runtime/BundleCache/Services/BuiltinBundleCache/BuiltinCatalogConsts.cs:26`）。**如果 Building 时没生成 catalog，Builtin 会一个 bundle 都 `CanAcceptBundle` 返回 false**（`BuiltinFileSystem.CanAcceptBundle` 依赖 `IsCached`，`Runtime/FileSystem/Services/BuiltinFileSystem/BuiltinFileSystem.cs:399-402`）。

**`EditorFileSystem`**：`CheckPlatform → InitializeBundleCache → CreateScheduler`（`Runtime/FileSystem/Services/EditorFileSystem/Operations/EFSInitializeOperation.cs:8-13`）。**注意 `OnCreate` 在 `packageRoot` 为空时直接抛异常**（`Runtime/FileSystem/Services/EditorFileSystem/EditorFileSystem.cs:217-218`）—— 这就是 `CreateDefaultEditorFileSystemParameters(null)` 会失败的原因，必须传模拟构建的输出目录（见 §8.4）。

### 1.2 加载

#### 1.2.1 完整调用链（HostPlayMode，一次冷加载）

**【代码事实】** 主线如下（每层都是 `AsyncOperationBase` 的子操作，通过 `AddChildOperation` + `UpdateOperation()` 手动驱动）：

```
package.LoadAssetAsync<TObject>(location, priority)          ResourcePackage.cs:584
 └─ ConvertLocationToAssetInfo(location, typeof(TObject))    ResourcePackage.cs:587 → PackageManifest.cs:301
     └─ AssetsByLocation.TryGetValue(location)               PackageManifest.cs:311
 └─ LoadAssetInternal(assetInfo, false, priority)            ResourcePackage.cs:619
     └─ assetInfo.LoadMethod = LoadAsset                     ResourcePackage.cs:622
     └─ _resourceManager.LoadAssetAsync(assetInfo, priority)  ResourcePackage.cs:623 → ResourceManager.cs:200
         ├─ providerKey = "LoadAssetAsync" + assetInfo.AssetKey   ResourceManager.cs:219
         │    AssetKey = $"[{AssetPath}][{AssetType.Name}]"       AssetInfo.cs:76-88
         ├─ 命中 _providerDict → 复用（RefCount++，不重新加载）    ResourceManager.cs:220-221
         └─ 未命中 → new AssetProvider(...) + 投入调度器          ResourceManager.cs:223-226
             └─ ProviderBase 构造函数（关键！）                    ProviderBase.cs:102-126
                 ├─ manager.GetOrCreateMainBundleLoader(assetInfo)  :112
                 │    └─ FileSystemHost.GetMainBundleInfo           FileSystemHost.cs:167
                 │        └─ ActiveManifest.GetMainPackageBundle    PackageManifest.cs:146
                 ├─ manager.GetOrCreateDependBundleLoaders(assetInfo) :116
                 │    └─ FileSystemHost.GetDependentBundleInfos     FileSystemHost.cs:194
                 │        └─ PackageManifest.GetAllAssetDependencies :178
                 └─ 对每个 loader 调 Reference()  ★ 引用计数在这里就加了 :121-124
             └─ provider.CreateHandle<AssetHandle>()              ProviderBase.cs:285 → RefCount++ :288
```

**为什么 `Reference()` 在构造函数里而不是 `InternalStart` 里？** 源码给了明确注释（`ProviderBase.cs:108-109`）：

> 注意: 以下 bundle loader 初始化不可移至 InternalStart()，否则共享 loader 的引用计数会出错，导致资源被提前卸载。

**【推断】** 因为 `ResourceManager.LoadAssetAsync` 创建 provider 后会立即 `CreateHandle`，而 provider 可能还没被调度器执行过一次；如果 `Reference` 延后，这期间其他路径（`UnloadUnusedAssetsAsync` / `TryUnloadUnusedAsset`）可能看到 loader 的 `RefCount == 0` 而把它卸掉。

**Provider 的五步状态机**（`ProviderBase.cs:14-21`）：

```
None → StartBundleLoader → WaitBundleLoader → ProcessBundleHandle → Done
```

- `StartBundleLoader`（`:154-162`）：对每个 loader 调 `StartOperation()` + `AddChildOperation`
- `WaitBundleLoader`（`:164-211`）：驱动所有 loader 的 `UpdateOperation()`，进度按 `CalculateMultiStageProgress(0, 2, ...)`；全部 done 后取 `_mainBundleLoader.BundleHandle`（`:203`）
- `ProcessBundleHandle`（`:213-216`）→ 子类实现：`AssetProvider.InternalProcessBundleHandle`（`Runtime/ResourceManager/Providers/AssetProvider.cs:14-45`）

**`LoadBundleOperation` 的三步状态机**（`Runtime/ResourceManager/Operations/Internal/LoadBundleOperation.cs:12-18`）：

```
None → CheckConcurrency → LoadBundleFile → Done
```

- **`CheckConcurrency`（`:85-97`）**：这是 bundle 级并发闸门：
  ```csharp
  if (IsWaitForCompletion) { _steps = LoadBundleFile; }   // 同步等待：绕过闸门
  else {
      if (_resourceManager.IsBundleLoadingBusy()) return;  // 达到上限 → 本帧直接返回，下帧重试
      _steps = LoadBundleFile;
  }
  ```
- **`LoadBundleFile`（`:99-138`）**：`IncrementBundleLoadingCounter()`（`:104`）→ `LoadBundleInfo.CreateBundleLoader()` → 即 `IFileSystem.LoadPackageBundleAsync`（`Runtime/ResourcePackage/BundleInfo.cs:47-51`）→ `SandboxFileSystem.LoadPackageBundleAsync` → `SFSLoadPackageBundleOperation`。

**`SFSLoadPackageBundleOperation` 的状态机**（`Runtime/FileSystem/Services/SandboxFileSystem/Operations/SFSLoadPackageBundleOperation.cs:9-18`）：

```
None → Prepare → DownloadFile → AbortDownload → LoadBundle → CheckResult → Done
```

- **`Prepare`（`:40-59`）**：`BundleCache.IsCached(BundleGuid)`？
  - 是 → `LoadBundle`
  - 否 → 若 `DisableOnDemandDownload` 则**直接失败**（`:48-53`）；否则 `DownloadFile`
- **`DownloadFile`（`:72-98`）**：`new FSDownloadBundleOptions(Bundle, int.MaxValue)` —— **注意：边玩边下时重试次数是 `int.MaxValue`**（`:76`）
- **`LoadBundle`（`:116-122`）**：`BundleCache.LoadBundleAsync(...)` → `SandboxBundleCache.LoadBundleAsync`（`Runtime/BundleCache/Services/SandboxBundleCache/SandboxBundleCache.cs:134-157`），按 `EBundleType` 分派：`AssetBundle` → `SBCLoadAssetBundleOperation`，`RawBundle` → `SBCLoadRawBundleOperation`，`ArchiveBundle` → `SBCLoadArchiveBundleOperation`

**`SBCLoadAssetBundleOperation` 的状态机**（`Runtime/BundleCache/Services/SandboxBundleCache/Operations/SBCLoadAssetBundleOperation.cs:12-20`）：

```
None → GetEntry → LoadBundle → VerifyFile → TryFallback → Done
```

- `GetEntry`（`:48-60`）：从内存字典取 `SandboxBundleCacheEntry`（**不是重新查磁盘**）
- `LoadBundle`（`:62-106`）：`LoadLocalAssetBundleOperation`，其内部（`Runtime/BundleCache/Operations/Common/LoadLocalAssetBundleOperation.cs:47-92`）根据是否加密、解密器类型走 4 条路：
  - 明文 → `AssetBundle.LoadFromFileAsync`（`:139`）
  - `IBundleOffsetDecryptor` → `LoadFromFileAsync(path, 0, offset)`（`:151`，**零拷贝，只跳头部**）
  - `IBundleMemoryDecryptor` → 全量读入内存后 `LoadFromMemoryAsync`（`:165`，**内存峰值最高**）
  - `IBundleStreamDecryptor` → 包装 `Stream` 后 `LoadFromStreamAsync`（`:181`，**大文件推荐**）
- **`VerifyFile`（`:108-136`）**：**仅在引擎加载失败时**触发 —— 源码注释点明"Low 级别不足以保证完整性，引擎加载失败时必须重新验证"（`:110-111`）。验证用 `BCVerifyCacheOptions(bundle, true)`，失败即删缓存条目。
- **`TryFallback`（`:138-176`）**：读全文件到 `byte[]` 后 `AssetBundle.LoadFromMemory`。源码注释说明原因（`:141-142`）：
  > 在安卓移动平台，华为和三星真机上有极小概率加载资源包失败。大多数情况在首次安装下载资源到沙盒内，游戏过程中切换到后台再回到游戏内有很大概率触发！

**【推断】** 这条 fallback 路径是 Android 的实战补丁，代价是一次完整 `byte[]` 峰值（bundle 大小）。大 world chunk bundle 若达 20~50MB，fallback 会瞬间抬高内存峰值 —— 应通过"减少 bundle 体积 + High 校验减少触发率"来规避，而不是依赖它。

**最后一步：`AssetBundle.LoadAsset`** —— `ABHLoadAssetOperation`（`Runtime/BundleHandle/Services/AssetBundleHandle/Operations/ABHLoadAssetOperation.cs:56-118`）：

```csharp
// 异步路径（:70-72）
_request = _assetBundle.LoadAssetAsync(_assetInfo.AssetPath, _assetInfo.AssetType);
// CheckResult（:94）：_request.isDone 后才取 _request.asset
```

**注意 `loadAssetPath` 用的是 `AssetInfo.AssetPath`（工程内路径），不是 bundle 内文件名** —— 3.0 依赖构建期把 `AssetPath` 写入清单并作为 bundle 内 asset name（见 §8）。

#### 1.2.2 全链路对象时序图（含计数变化）

```
调用方           Provider            LoadBundleOperation   FileSystem        BundleCache        IBundleHandle
  │ LoadAssetAsync  │                       │                  │                  │                   │
  ├────────────────>│ new(RefCount=0)       │                  │                  │                   │
  │                 ├─GetOrCreate loader───────────────────────>│                  │                   │
  │                 ├─loader.Reference() → RefCount=1            │                  │                   │
  │                 └─CreateHandle → Provider.RefCount=1         │                  │                   │
  │                 │                       │                  │                  │                   │
  │                 │ (调度器 Update)        │                  │                  │                   │
  │                 ├─StartOperation───────>│ CheckConcurrency │                  │                   │
  │                 │                       ├─IsBundleLoadingBusy?  (闸门)         │                  │
  │                 │                       ├─counter++         │                  │                   │
  │                 │                       ├─CreateBundleLoader──────────────────>│                  │
  │                 │                       │                  ├─IsCached? ───────>│                   │
  │                 │                       │                  ├─(miss) Download   │                   │
  │                 │                       │                  ├─LoadBundleAsync──>│ GetEntry          │
  │                 │                       │                  │                  ├─LoadFromFileAsync│
  │                 │                       │                  │                  ├─BundleHandle ───>│
  │                 │                       │<──BundleHandle───┴──────────────────┴───────────────────┤
  │                 │ LoadedBundleHandle    │                  │                  │                   │
  │                 ├─LoadAssetAsync─────────────────────────────────────────────────────────────────>│
  │                 │                       │                  │                  │    LoadAssetAsync │
  │                 │<──────────────────────AssetObject───────────────────────────────────────────────┤
  │<──AssetHandle───┤ (Provider.RefCount=1) │                  │                  │                   │
```

### 1.3 释放

#### 1.3.1 `handle.Release()` 的完整后果链

**【代码事实】** `Runtime/ResourceManager/Handles/HandleBase.cs:28-39`：

```csharp
public void Release()
{
    if (CheckValidWithWarning() == false)   // :30  已释放/Provider 已销毁 → 只警告，直接 return
        return;
    Provider.ReleaseHandle(this);            // :32  → ProviderBase.cs:299  RefCount--
    if (Provider.RefCount == 0)             // :35
        Provider.TryUnloadBundle();         // :36  → 仅当 AutoUnloadBundleWhenUnused 才做
    Provider = null;                        // :38  ★ 这是"二次释放"能被检测到的原因
}
```

逐层展开：

**步骤 1：`CheckValidWithWarning()`（`HandleBase.cs:125-139`）** —— `Provider == null` 时打 warning `"Handle has already been released."`（`:134`），返回 false。**所以同一 handle 二次 `Release()` 不抛异常，只打 warning 并静默返回。**

**步骤 2：`ProviderBase.ReleaseHandle`（`ProviderBase.cs:299-309`）**：

```csharp
if (RefCount <= 0)
    throw new YooInternalException($"Attempting to release handle when RefCount is already zero..."); // :302
if (_handles.Remove(handle) == false)
    throw new YooInternalException($"Handle not found in cache list...");                              // :305
RefCount--;                                                                                            // :308
```

**这里会抛异常**，但触发条件是"同一个 handle 被两个不同路径重复 Release"或"Provider.RefCount 已经归零"。由于步骤 1 已经把 `Provider` 置 null，正常二次 `Release()` 到不了这里 —— 只有**绕过 HandleBase 直接调 `Provider.ReleaseHandle(同一个 handle)`** 才会命中。**【推断】** 实际项目里更常见的是 `Provider.ReleaseAllHandles()`（`ProviderBase.cs:314-321`）与业务代码 `handle.Release()` 并发/重复触发，此时 `_handles.Remove` 返回 false → 抛 `YooInternalException`。

**步骤 3：`Provider.TryUnloadBundle()`（`ProviderBase.cs:326-332`）**：

```csharp
if (_resourceManager.AutoUnloadBundleWhenUnused)          // ★ 默认 false（InitializePackageOptions.cs:18）
    _resourceManager.TryUnloadUnusedAsset(MainAssetInfo, 10);
```

**这是本项目最重要的默认值之一**：`AutoUnloadBundleWhenUnused = false`（`Runtime/ResourcePackage/Operations/InitializePackageOptions.cs:18`）意味着 **`handle.Release()` 不会自动卸载 bundle**。必须靠显式 `UnloadUnusedAssetsAsync()` 或 `TryUnloadUnusedAsset(location)` 回收。详见 §5、§6。

**步骤 4：`TryUnloadUnusedAsset` 的真正卸载逻辑（`ResourceManager.cs:90-144`）**：

```csharp
while (loopCount > 0) {                                   // 默认调用方传 10
    loopCount--;
    bool hasUnloaded = false;
    // 卸载主资源包加载器
    var mainLoader = GetBundleLoader(mainBundleName);
    if (mainLoader != null) {
        mainLoader.TryDestroyProviders();                  // :115  先销毁 provider
        if (mainLoader.CanDestroyLoader()) {               // :116
            mainLoader.DestroyLoader();                    // :118
            _bundleLoaderDict.Remove(mainBundleName);      // :119
            hasUnloaded = true;
        }
    }
    // 卸载依赖资源包加载器（只检查 CanDestroyLoader，不 TryDestroyProviders！）
    foreach (var dependID in assetInfo.Asset.DependentBundleIDs) { ... }   // :125-138
    if (hasUnloaded == false) break;                       // :141  没进展就提前退出
}
```

**注释明确说明为什么需要循环（`:103-104`）**：
> 例如：A依赖B，B依赖C，需要多次循环才能完全卸载

**步骤 5：`LoadBundleOperation.CanDestroyLoader()`（`LoadBundleOperation.cs:247-270`）** —— 这里有 3 个必须同时满足的条件：

1. `IsReleasable()`：`_steps != LoadBundleFile`（不能在加载中）且 `RefCount == 0`（`：276-286`）
2. `ReferrerBundleIDs` 链检查（`:255-267`）：
   ```csharp
   foreach (var bundleID in LoadBundleInfo.Bundle.ReferrerBundleIDs) {
   #if YOOASSET_EXPERIMENTAL
       if (_resourceManager.CheckBundleReleasable(bundleID) == false) return false;
   #else
       if (_resourceManager.CheckBundleDestroyed(bundleID) == false) return false;   // 默认分支
   #endif
   }
   ```
   **默认分支要求"所有引用我的 bundle 都已经销毁"**。源码注释（`:253-254`）：
   > 注意：互相引用的资源包无法卸载！

   **【推断】** 这是本项目最大的卸载风险点：如果 chunk A 的 bundle 与公共材质 bundle 形成"共用依赖"，而公共 bundle 又被别的 chunk 引用，则 chunk A 的 bundle 无法释放，直到所有引用者都释放。分块设计必须避免跨 chunk 的双向引用。

3. 无强引用（`RefCount == 0`）

**步骤 6：`DestroyLoader()`（`LoadBundleOperation.cs:181-203`）**：

```csharp
if (_steps == ESteps.LoadBundleFile)
    throw new YooInternalException("Cannot destroy loader while loading bundle...");   // :190
if (RefCount > 0)
    throw new YooInternalException($"Cannot destroy loader with non-zero ref count {RefCount}..."); // :193
if (BundleHandle != null)
    BundleHandle.UnloadBundle();          // :196  ★ 真正卸载
```

`BundleHandle.UnloadBundle()` 的实现（`Runtime/BundleHandle/Services/AssetBundleHandle/AssetBundleHandle.cs:30-41`）：

```csharp
if (_assetBundle != null)
    _assetBundle.Unload(true);      // :34  ★ unloadAllObjects = true
if (_bundleStream != null)
    _bundleStream.Dispose();        // :39  流式解密时释放流
```

**`Unload(true)` 意味着 bundle 内所有已加载对象一起销毁。** 这就是为什么 `ReferrerBundleIDs` 链检查必须严格：如果还有别的 provider 持有同一 bundle 的对象，`Unload(true)` 会让那些对象变成 missing。

**步骤 7：`Resources.UnloadUnusedAssets()` 只在 2 处触发** —— 全文只有 1 个调用点：

`Runtime/ResourceManager/Operations/UnloadAllAssetsOperation.cs:95`（`UnloadAllAssetsAsync` / `DestroyPackageAsync` 路径）。

**【代码事实】** `UnloadUnusedAssetsAsync`（`Runtime/ResourceManager/Operations/UnloadUnusedAssetsOperation.cs`）**不调用** `Resources.UnloadUnusedAssets()`，它只调 `DestroyUnusedBundle()`（`:43`）把 loader 从字典摘掉 → 触发 `BundleHandle.UnloadBundle()`。**所以 `Resources.UnloadUnusedAssets()` 这一昂贵操作只在"卸载全部/销毁包裹"时发生，正常流式卸载不会触发它** —— 对 30fps 是好消息。

#### 1.3.2 卸载 API 选择

**【代码事实】** 4 个卸载入口，行为不同：

| API | 作用域 | 是否释放句柄 | 是否锁加载 | 是否等 `UnloadUnusedAssets` | 代码 |
|---|---|---|---|---|---|
| `handle.Release()` | 单句柄 | 是（本句柄） | 否 | 否 | `HandleBase.cs:28` |
| `TryUnloadUnusedAsset(location)` | 单资源 + 依赖链 | 否（只切 loader） | 否 | 否 | `ResourcePackage.cs:308-313` |
| `UnloadUnusedAssetsAsync()` | 全包裹零引用 | 否 | 否 | **否** | `ResourcePackage.cs:282-301` |
| `UnloadAllAssetsAsync()` | 全包裹 | 默认 true | 默认 true | 默认 **false** | `ResourcePackage.cs:256-275` |
| `DestroyPackageAsync()` | 全包裹 + 拆对象 | true | true | **true** | `ResourcePackage.cs:120-130` |

`DestroyPackageAsync` 传的 options 是 `(shouldReleaseHandles: true, shouldLockLoading: true, shouldWaitUnloadUnused: true)`（`:123-126`）—— 三个都开，因为它要彻底拆掉。

`UnloadAllAssetsOperation` 的完整流程（`Runtime/ResourceManager/Operations/UnloadAllAssetsOperation.cs:20-116`）：

```
CheckOptions → ReleaseAll → RequestForceDestroy → CheckLoading → DestroyAll → [WaitUnloadUnused] → Done
```

- `CheckOptions`（`:44-54`）：按 `ShouldLockLoading` 加锁 → `AcquireLoadingLock()`，**此后所有新 `LoadAssetAsync` 都返回 `ErrorProvider`**（`ResourceManager.cs:202-209`）
- `RequestForceDestroy`（`:68-75`）：给所有 provider / loader 打 `ForceDestroyRequested = true`
- `CheckLoading`（`:77-84`）：等 `AreAllProvidersDone()` —— **正在加载中的不打断，等它自己完成**（`ProviderBase.cs:136-148`）
- `DestroyAll`（`:86-104`）：`DestroyAllProviders()` + `DestroyAllBundleLoaders()` + `Resources.UnloadUnusedAssets()`
- `InternalDispose`（`:118-127`）**总是**解除锁（用 `_lockAcquired` 标记幂等）

---

## 2. 模块职责矩阵

**【代码事实】** `Runtime/` 顶层模块与体量（`.cs` 文件数 / 行数，含 `.meta` 不计）：

| 模块 | 文件数 | 行数 | 解决什么问题 | 依赖谁 | 被谁依赖 | 关键类型 |
|---|---|---|---|---|---|---|
| `AsyncOperation/` | 6 | 1335 | 把异步工作分摊到多帧；包裹间优先级；`await` 桥接 | 无（最底层） | 所有模块 | `AsyncOperationBase`、`AsyncOperationSystem`、`AsyncOperationScheduler`、`OperationAwaiter`、`CustomAsyncOperation`、`EOperationStatus` |
| `BundleCache/` | 84 | 7726 | **字节层**：磁盘/网络字节 → `IBundleHandle`；缓存条目、校验、清理、淘汰 | `AsyncOperation`、`Interfaces`、`Utility`、`DownloadSystem` | `FileSystem`（每个文件系统持有一个或多个 cache） | `IBundleCache`、`SandboxBundleCache`、`BuiltinBundleCache`、`EditorBundleCache`、`WebServerBundleCache`、`WebNetworkBundleCache`、`EFileVerifyLevel`、`VerifyCacheFilesTask` |
| `BundleHandle/` | 46 | 2907 | **引擎对象层**：`IBundleHandle` 的多种实现（AssetBundle / Raw / Archive / Instant / Virtual / Web） | `AsyncOperation`、`Interfaces`、`Utility` | `BundleCache`（作为产物返回） | `IBundleHandle`、`AssetBundleHandle`、`RawBundleHandle`、`ArchiveBundleHandle`、`VirtualAssetBundleHandle`、`EBundleType` |
| `FileSystem/` | 90 | 9913 | **路由 + 下载 + 清单**：哪些 bundle 归我、怎么下载、清单/版本从哪读 | `BundleCache`、`DownloadSystem`、`AsyncOperation` | `ResourcePackage`（`FileSystemHost`） | `IFileSystem`、`FileSystemParameters`、`BuiltinFileSystem`、`SandboxFileSystem`、`EditorFileSystem`、`WebServerFileSystem`、`WebNetworkFileSystem`、`InitializeFileSystemOperation` |
| `ResourceManager/` | 23 | 3119 | **Provider + Handle**：引用计数、loader 去重、并发闸门、加载锁、场景回收 | `FileSystem`（间接）、`AsyncOperation` | `ResourcePackage` | `ResourceManager`、`ProviderBase`、`AssetProvider`/`SceneProvider`/`SubAssetsProvider`/`AllAssetsProvider`/`BundleFileProvider`/`ErrorProvider`、`HandleBase` 及 5 个句柄、`LoadBundleOperation` |
| `ResourcePackage/` | 37 | 6053 | **对外门面**：清单模型、公开 API、公开 Operation 类型、选项类型 | 上述全部 | 业务代码 | `ResourcePackage`、`PackageManifest`、`PackageBundle`、`PackageAsset`、`AssetInfo`、`BundleInfo`、`FileSystemHost`、`InitializePackageOptions` 家族、所有公开 `*Operation` |
| `DownloadSystem/` | 20 | 2097 | 下载请求抽象与调度：`IDownloadBackend` / `IDownloadRequest`、并发与每帧限制、重试、URL 选择、看门狗 | `AsyncOperation`、`Interfaces` | `FileSystem`、`BundleCache` | `IDownloadBackend`、`IDownloadRequest`、`DownloadSchedulerOperation`、`DownloadRetryController`、`UnityWebRequestBackend` |
| `Interfaces/` | 9 | 357 | **公开扩展点集中地**（9 个接口） | 无 | 业务代码实现，框架调用 | `IBundleDecryptor`（+3 派生）、`IBundleEncryptor`、`IManifestDecryptor`/`Encryptor`、`IDownloadRetryPolicy`、`IDownloadUrlPolicy`、`IRemoteService`、`IBundleUnpackPolicy`、`IBuiltinFileAccessor` |
| `Settings/` | 2 | 294 | 全局配置：`YooFolderName`、`PackageFilePrefix`、默认路径计算 | 无 | 全模块 | `YooAssetConfiguration`、`YooAssetSettings` |
| `Compatibility/` | 7 | 1388 | 2.3 API 兼容层 | 全部 | 业务代码（迁移期） | `CompatibleYooAssets`、`CompatibleResourcePackage`、`CompatibleHandles` 等 |
| `DiagnosticSystem/` | 11 | 684 | 编辑器远程调试（Editor ↔ Player 通信） | 无 | `YooAssets.Initialize`（DEBUG） | `DiagnosticBehaviour`、`DiagnosticReport`、`DiagnosticCommand` |
| `PackageBuilder/` | 4 | 169 | 运行时侧反射调用编辑器构建（仅编辑器） | 无 | 业务代码（编辑器工具） | `PackageBuildInvoker`、`EditorSimulateBuildInvoker`、`PackageBuildResult` |
| `Utility/` | 11 | 1619 | 通用工具 | 无 | 全模块 | `YooLogger`/`ILogger`、`YooException` 家族、`BufferReader`/`Writer`、`CRC32Algorithm`、`HashUtility`、`FileUtility`、`PathUtility`、`TextUtility`、`TimeUtility`、`StringUtility` |

### 2.1 依赖方向（谁调谁）

**【代码事实】** 由 `using`/类型引用归纳：

```
业务代码
   ↓
ResourcePackage（门面）
   ├─→ FileSystemHost ──→ IFileSystem 的实现（含 BundleCache）
   ├─→ ResourceManager ──→ LoadBundleOperation ──→ FileSystemHost ──→ IFileSystem
   │        └─→ ProviderBase ──→ LoadBundleOperation ──→ IBundleHandle（BundleCache 产出）
   └─→ AsyncOperationSystem

所有层 ──→ AsyncOperationBase（生命周期骨架）
所有层 ──→ Utility
```

**没有循环依赖，但 `ResourceManager` 与 `FileSystemHost` 是双向的**：`ResourceManager` 持有 `_fileSystemHost`（`ResourceManager.cs:23`、`:69`），`FileSystemHost` 不持有 `ResourceManager`。所以是单向。

### 2.2 `Compatibility/` 是死的

**【代码事实】** 7 个文件全部以 `#if YOOASSET_LEGACY_API` 开头，例如 `Runtime/Compatibility/CompatibleYooAssets.cs:1`、`CompatibleResourcePackage.cs:1`。`YOOASSET_LEGACY_API` 在任何 asmdef 的 `versionDefines` 里都没有定义（`Runtime/YooAsset.asmdef` 只定义了 `YOOASSET_3` / `YOOASSET_3_0` / `YOOASSET_3_0_OR_NEWER`）。

**【推断】** 兼容层默认不参与编译。要用必须自己在 Player Settings 的 Scripting Define Symbols 里加 `YOOASSET_LEGACY_API`。本项目已在 3.0.7 API 上，不需要它 —— 建议**保持不定义**。

---

## 3. 关键机制

### 3.1 异步调度

#### 3.1.1 三层结构

**【代码事实】**

```
YooAssetsDriver.Update()                          Runtime/YooAssetsDriver.cs:21-24
 └─ AsyncOperationSystem.Update()                 Runtime/AsyncOperation/AsyncOperationSystem.cs:105-136
     ├─ 帧起点：_frameStartTime = _stopwatch.ElapsedMilliseconds   :126
     ├─ 按优先级排序调度器（脏标记触发）           :111-123
     └─ for each scheduler:                        :129-135
         ├─ if (IsBusy) break;                     :131  ★ 全局预算
         └─ scheduler.Update()                     :134
             ├─ 移除已完成                                AsyncOperationScheduler.cs:80-87
             ├─ 合并 pending → running                    :89-94
             ├─ 操作级优先级排序（脏标记）                :96-109
             └─ for each operation:                       :112-123
                 ├─ if (AsyncOperationSystem.IsBusy) break;  :115  ★ 双重检查
                 └─ operation.UpdateOperation()              :122
```

#### 3.1.2 `MaxTimeSlice` 的语义与钳制

**【代码事实】** `Runtime/AsyncOperation/AsyncOperationSystem.cs`：

```csharp
private const long MinTimeSlice = 10;                      // :26
private static long _maxTimeSlice = long.MaxValue;         // :36  ★ 默认不限制

public static long MaxTimeSlice {                          // :45
    get { return _maxTimeSlice; }
    set {
        if (value < MinTimeSlice) {
            _maxTimeSlice = MinTimeSlice;                   // :55
            YooLogger.LogWarning($"MaxTimeSlice must be at least {MinTimeSlice} ms, clamped to {MinTimeSlice}.");
        } else _maxTimeSlice = value;
    }
}

public static bool IsBusy =>                                // :68
    _stopwatch == null ? false :
    _maxTimeSlice == long.MaxValue ? false :                // :75-76 ★ 默认永不 busy
    (_stopwatch.ElapsedMilliseconds - _frameStartTime >= _maxTimeSlice);  // :79
```

**语义要点（这些是踩坑高发区）**：

1. **默认 `long.MaxValue` = 不限制**。也就是说**不做任何设置时，YooAsset 单帧会把所有待推进 operation 全部跑完**。在 30fps + 大世界流式场景下，这是掉帧的直接原因。必须显式设置。
2. **钳制下限是 10ms**，低于 10 会被强制改成 10 并打 warning（`:53-57`）。所以"设 5ms 试试"是无效的。
3. **`IsBusy` 是"本帧已用时 ≥ 预算"**，不是"剩余预算"。判定粒度是**操作之间**（每个 operation 前后检查一次，`:115`、`:131`），**不是操作内部**。如果某个 `InternalUpdate` 单次耗时 20ms，即使 `MaxTimeSlice = 10`，本帧也会超到 20ms。
4. **同步等待会绕过预算**：`AsyncOperationBase.IsBusy`（`AsyncOperationBase.cs:41-49`）在 `IsWaitForCompletion == true` 时恒返回 false。所以 `WaitForAsyncComplete()` / `LoadAssetSync()` 路径**不受 MaxTimeSlice 约束**，会一路阻塞到完成。

**设置入口**：`YooAssets.SetAsyncOperationMaxTimeSlice(long milliseconds)`（`Runtime/YooAssets.cs:227-232`），负数抛 `ArgumentOutOfRangeException`。

**内部使用的 `IsBusy` 位置（用于分帧）**：

- 清单反序列化：`DeserializeManifestOperation.cs:184`、`:219`（每条资源/每个 bundle 后检查）
- 缓存文件扫描：`SearchCacheFilesOperation.cs:120`
- 缓存校验结果登记：`VerifyCacheFilesOperation.cs:97`
- 卸载循环：`UnloadUnusedAssetsOperation.cs:46`

**【推断】** 这些点决定了"锁预算"是否生效。清单反序列化是唯一在初始化阶段吃预算的地方；如果清单很大（10 万条），`MaxTimeSlice` 设置会让初始化从 1 帧变成 N 帧 —— **这是好事（避免启动卡帧），但会让初始化耗时变长**。

#### 3.1.3 优先级如何生效

**【代码事实】** 有**三级**优先级：

**① 调度器级（包裹级）**：`AsyncOperationScheduler.CompareTo`（`AsyncOperationScheduler.cs:174-184`）：

```csharp
int result = other.Priority.CompareTo(this.Priority);   // 降序：大的在前
if (result == 0) result = this.CreationOrder.CompareTo(other.CreationOrder);  // 同优先级按创建顺序
```

- 全局调度器优先级是 `uint.MaxValue`（`AsyncOperationSystem.cs:99`），**永远排第一**
- 包裹优先级：`CreatePackage(name, priority)` 或 `ResourcePackage.PackagePriority` setter（`ResourcePackage.cs:39-43`）
- 每次改优先级只设 `IsDirty`，下次 `Update()` 时统一排序（`AsyncOperationSystem.cs:111-123`）

**② 操作级**：`AsyncOperationBase.CompareTo`（`AsyncOperationBase.cs:705-708`）：

```csharp
public int CompareTo(AsyncOperationBase other) => other.Priority.CompareTo(this.Priority);
```

`Priority` setter 的脏标记同 `ResourceManager.cs:229`（`provider.Priority = priority`）。

**③ 优先级从哪里传入**：`LoadAssetAsync(location, priority)` 的 `priority` 参数一路传到 `provider.Priority`（`ResourceManager.cs:229`、`:268`、`:307`、`:346`）。**同一 provider 被复用时会覆盖其优先级**（后一次请求的 priority 生效）。

**【推断】** 实际精度：优先级只在**同一调度器内、且排序被触发时**有效。如果所有操作优先级都是 0（默认），则完全按插入顺序执行 —— 分块流式场景下"玩家当前位置最近 chunk 优先"必须显式传 priority，且要接受"已经排在前面的低优先级操作不会被抢占"。

#### 3.1.4 `OperationAwaiter` 如何实现 `await`

**【代码事实】** `Runtime/AsyncOperation/OperationAwaiter.cs:10-47`：

```csharp
public readonly struct OperationAwaiter : ICriticalNotifyCompletion
{
    private readonly AsyncOperationBase _operation;
    public bool IsCompleted => _operation.IsDone;                    // :24
    public void GetResult() { }                                       // :32-34  ★ 故意空实现
    public void OnCompleted(Action continuation) => UnsafeOnCompleted(continuation);
    public void UnsafeOnCompleted(Action continuation) {
        _operation.Completed += (op) => continuation();               // :45
    }
}
```

三个关键事实：

1. **`GetResult()` 是空实现**（`:32-34`，注释明确"业务失败不视为异常"）。**所以 `await op;` 之后必须自己检查 `op.Status`**，失败不会抛异常。这与 `Task` 的语义不同，是最常见误用。
2. **`IsCompleted` 读 `_operation.IsDone`**（`Succeeded || Failed`），所以同步完成的操作（如缓存命中且已加载）不会真正 `await`。
3. **回调在调度器的 `Update()` 里同步执行**（`InvokeCompletedCallbacks`，`AsyncOperationBase.cs:535-565`），即**在 `YooAssetsDriver.Update()` 的调用栈里**。如果在 `await` 后续代码里做了重活，会直接计入该帧预算 —— **`await` 之后的代码不会自动分帧**。

`HandleBase.GetAwaiter()`（`HandleBase.cs:146-151`）返回的是 `Provider.GetAwaiter()`，即 await 一个 handle 等价于 await 其 provider。

**【推断】** `await` 的 continuation 在 Unity 里**会切回主线程**（因为回调在主线程 Update 里触发，且 `UnsafeOnCompleted` 没有 `SynchronizationContext` 捕获，靠的是 C# 编译器生成的 `AsyncTaskMethodBuilder` 默认行为）。**但这取决于 `UnitySynchronizationContext` 是否在当前线程已安装** —— 在 `Update()` 调用栈里是的，所以安全。

### 3.2 下载调度

#### 3.2.1 三层并发控制

**【代码事实】** 下载路径上有**三个独立的并发/频率限制**，分别在不同层：

| 层 | 参数 | 默认值 | 作用点 | 语义 |
|---|---|---|---|---|
| **下载器（业务层）** | `ResourceDownloaderOptions.MaximumConcurrency` | 调用方传入 | `DownloaderOperation.cs:96` | **同时活跃的 `FSDownloadBundleOperation` 数量**，钳制到 `[1, 32]`（`MaxLoaderCount = 32`，`:20`） |
| | （隐含）每帧创建数 | **1** | `DownloaderOperation.cs:199-227` | **每帧只新建 1 个 block 下载器**（注释明确："将初始化开销分摊到多帧，避免移动端单帧尖峰"） |
| **下载调度器（文件系统层）** | `DownloadMaxConcurrency` | 8 | `SFSInitializeOperation.cs:134` → `DownloadSchedulerOperation.cs:131-153` | **同时 Running 的 `DownloadFileBaseOperation` 数量**，钳制 `[1, 32]`（`SandboxFileSystem.cs:257`） |
| | `DownloadMaxRequestsPerFrame` | 8 | 同上 | **每帧新启动的请求数**，钳制 `[1, 32]`（`SandboxFileSystem.cs:268`） |

**为什么有两层？** 【推断】第一层是"业务语义的下载器"（一次 `DownloaderOperation` 可以包含上千个文件），第二层是"物理连接的调度器"（多个 `DownloaderOperation` 可能同时存在，共享同一个文件系统调度器，所以物理并发必须在最底层统一）。`SFSDownloadBundleOperation` 通过 `DownloadScheduler.TryGetDownloadOperation` 去重（`SFSDownloadBundleOperation.cs:61`），**同一 bundle 只会有一个真实网络请求**。

**调度器的具体逻辑（`DownloadSchedulerOperation.cs:94-154`）**：

```csharp
// 每个已完成/零引用的任务被移除
if (downloader.IsDone) { RemoveChildOperation; _operationMap.Remove; continue; }         // :104-109
if (downloader.ReferenceCount <= 0) { downloader.AbortOperation(); ... continue; }        // :112-118

ActiveDownloadCount = GetActiveOperationCount();                                         // :124
if (Paused) return;                                                                       // :127-128
if (ActiveDownloadCount < maxConcurrency) {
    int startCount = maxConcurrency - ActiveDownloadCount;                                // :137
    if (startCount > maxRequestPerFrame) startCount = maxRequestPerFrame;                  // :138-139
    foreach (...) { operation.StartOperation(); startCount--; if (startCount <= 0) break; } // :141-151
}
```

**注意 `ReferenceCount <= 0` 会自动 abort**（`:112-118`）—— 这是"零引用自动取消下载"的机制。`SFSDownloadBundleOperation.InternalDispose` 里 `Release()`（`:165-172`），若加载方被中断（如 chunk 被卸载），下载会自动停。

#### 3.2.2 重试策略 `IDownloadRetryPolicy`

**【代码事实】** 接口在 `Runtime/Interfaces/IDownloadRetryPolicy.cs:7-26`（**public**，可扩展）；默认实现在 `Runtime/DownloadSystem/Policies/DefaultDownloadRetryPolicy.cs`：

```csharp
public bool IsRetryableError(string url, long httpCode, string httpError) {
    if (DownloadUrlHelper.IsLocalFileUrl(url)) return false;      // :30-31 本地路径不重试
    if (httpCode == 0) return true;                                // :34-35 网络瞬断可重试
    if (httpCode >= 400 && httpCode < 500)                        // :41-42
        return httpCode == 408 || httpCode == 416 || httpCode == 429;
    return true;                                                   // :45
}

public float CalculateRetryDelay(int retryCount, float previousDelay) {
    return Mathf.Clamp(previousDelay + 1f, 1f, 10f);               // :59 线性退避 +1s，上限 10s
}
```

**重试控制器**（`Runtime/DownloadSystem/DownloadRetryController.cs`）：

- `CanRetryRequest`（`:49-59`）：`_retryCount >= _maxRetryCount` 或策略拒绝 → false
- `StartRetryDelay`（`:76-82`）：`_retryCount++`，`_retryDelay = policy.CalculateRetryDelay(...)`
- `TickRetryDelay`（`:88-91`）：`RealtimeSinceStartup - _retryStartTime >= _retryDelay`

调用点在 `SFSDownloadBundleOperation.cs:126`：

```csharp
if (IsWaitForCompletion == false && _downloadRetryController.CanRetryRequest(url, httpCode, httpError))
```

**关键限制**：`IsWaitForCompletion == false` 才重试 —— **同步加载路径不重试**。并且**本地导入失败会跳过重试**（`:114-121`）。

重试次数来源：`FSDownloadBundleOptions.RetryCount`。**边玩边下时是 `int.MaxValue`**（`SFSLoadPackageBundleOperation.cs:76`）——【推断】意味着边玩边下会无限重试直到加载方放弃（加载方被 abort 时 `ReferenceCount` 归零 → 调度器 abort，`DownloadSchedulerOperation.cs:112`），这在弱网下是"卡住不报错"的表现。

#### 3.2.3 断点续传

**【代码事实】** `Runtime/FileSystem/Services/SandboxFileSystem/Operations/Internal/DownloadAndCacheFileOperation.cs:44-60`：

```csharp
_enableResume = Bundle.FileSize >= _fileSystem.ResumeDownloadMinimumSize;   // :48
if (_enableResume) { _downloadFileRequest = CreateResumeRequest(); ... }
else               { _downloadFileRequest = CreateNormalRequest(); ... }
```

- **默认 `ResumeDownloadMinimumSize = long.MaxValue`**（`SandboxFileSystem.cs:109`）→ **默认完全禁用断点续传**。要启用必须显式设置阈值。
- `CreateResumeRequest`（`:159-189`）：若临时文件已存在且 `Length < FileSize`，则 `resumeOffset = fileInfo.Length`，`appendToFile = true`，`removeFileOnAbort = false`；若 `Length >= FileSize` 则删掉重下（`:164-173`）
- **HTTP 416 特判**（`:101-106`）：`Range Not Satisfiable` 时删除临时文件，下次从头下
- **文件下载不设超时**：`int timeout = 0; //注意：文件下载不做超时检测`（`:176`、`:195`）

**【推断】** 断点续传对"大 world bundle + 弱网"很有价值，但需注意：临时文件路径是 `<缓存根>/TempFiles/<BundleGuid>`（`SandboxFileSystem.cs:436-444`），**没有 .tmp 后缀、与正式文件不同目录**，不会污染正式缓存。启用阈值建议取"大于 1MB"（小于 1MB 的续传收益低于开销）。

#### 3.2.4 看门狗

**【代码事实】** `DownloadWatchdogTimeout` 默认 0 = 禁用（`SandboxFileSystem.cs:104`）。实现在 `UnityWebRequestBase.UpdateWatchdog`（`Runtime/DownloadSystem/Services/UnityWebRequest/UnityWebRequestBase.cs:261-282`）：

```csharp
if (DownloadedBytes != _lastDownloadBytes) { _lastDownloadBytes = DownloadedBytes; _lastDataReceivedTime = now; }
else if (now - _lastDataReceivedTime > _requestArgs.WatchdogTimeout) {
    AbortRequest(); YooLogger.LogWarning($"Watchdog timeout after {deltaTime:F1}s for '{Url}'.");
}
```

**看门狗是"数据停滞检测"，不是总时长超时**。它与 `Timeout`（`UnityWebRequest.timeout`，`:303-304`）互补：`Timeout` 管总时长，`Watchdog` 管"活着但没数据"。

**【推断】** 移动网络（尤其是切后台/切基站）最容易出现"连接还在但没数据"，看门狗比 `Timeout` 更对症。但注意：`UnityWebRequest.timeout` 对**文件下载路径是 0（不设）**（`DownloadAndCacheFileOperation.cs:176`、`:195`），所以**不开看门狗 = 文件下载可能永久挂起**。这是本项目必须开的参数之一。

#### 3.2.5 URL 选择

**【代码事实】** `IDownloadUrlPolicy`（`Runtime/Interfaces/IDownloadURLPolicy.cs:8-30`，**public**）。默认实现 `DefaultDownloadUrlPolicy`（`Runtime/DownloadSystem/Policies/DefaultDownloadUrlPolicy.cs`）：失败计数轮转，成功保持粘滞。

候选 URL 由 `IRemoteService.GetRemoteUrls(fileName)` 提供（`Runtime/Interfaces/IRemoteService.cs:8-16`）。**注意签名接收的是 `fileName`（bundle 文件名，不是 location）**，见 `SFSDownloadBundleOperation.cs:181`。

### 3.3 缓存与校验

#### 3.3.1 五种 `IBundleCache` 实现与分层

**【代码事实】** `IBundleCache`（`Runtime/BundleCache/Interfaces/IBundleCache.cs:8-83`）有 5 个实现，`IsReadOnly` 属性区分读写能力：

| 实现 | 位置 | `IsReadOnly` | 字节来源 | 条目来源 | 适用 PlayMode |
|---|---|---|---|---|---|
| `BuiltinBundleCache` | `BundleCache/Services/BuiltinBundleCache/` | **true**（`:84`） | `StreamingAssets/yoo/<pkg>/` | `BuiltinCatalog.bytes`（`BBCInitializeOperation.cs:64-78`） | Offline / Host（第一段） |
| `SandboxBundleCache` | `BundleCache/Services/SandboxBundleCache/` | **false**（`:99`） | `<persistentDataPath>/yoo/<pkg>/BundleFiles/` | 目录扫描 + 信息文件校验（`SBCInitializeOperation.cs:39-88`） | Host（第二段） |
| `EditorBundleCache` | `BundleCache/Services/EditorBundleCache/` | `VirtualDownloadMode == false` 时为 true（`:83`） | **不读字节**（走 `AssetDatabase`） | `__marker` 文件扫描（`EBCInitializeOperation.cs:52-78`） | EditorSimulate |
| `WebServerBundleCache` | `BundleCache/Services/WebServerBundleCache/` | **true**（`:122`） | Unity 网络缓存 / URL | 无（按需） | Web（第一段） |
| `WebNetworkBundleCache` | `BundleCache/Services/WebNetworkBundleCache/` | **true**（`:104`） | 远端 URL + 平台策略 | 无（按需） | Web（第二段） |

**沙盒缓存的磁盘布局**（`SandboxBundleCache.cs:203-254` + `SandboxBundleCacheConsts.cs`）：

```
<SandboxFileSystem.PackageRoot>/BundleFiles/
  └─ <BundleGuid 前 2 字符>/          ← HashFolderNameLength = 2
      └─ <BundleGuid>/
          ├─ __data                  ← 真实字节
          ├─ __info                  ← 36 字节元信息（magic/version/crc/size/createdAt/lastAccessAt）
          ├─ __data.tmp              ← 写入临时
          └─ __info.tmp
```

`__info` 的二进制结构（`SBCWriteCacheOperation.cs:103-114`）：

```
uint32 Magic      = 0x31434F59 ("YOC1")
int32  Version    = 1
uint32 FileCrc
int64  FileSize
int64  CreatedAtTicks
int64  LastAccessAtTicks
```

**写入是两阶段原子提交**（`SBCWriteCacheOperation.cs:93-139`）：先写 `*.tmp` → 再 `File.Move` 覆盖正式文件（`:117-123`）。**【推断】** 这是为了防"下载中断留下半个文件被当成有效缓存"。`File.Move` 在同一卷上是原子的，所以崩溃点最多丢一个刚完成的 bundle，不会产生"看起来有效实际损坏"的条目。

#### 3.3.2 `EFileVerifyLevel` 三级的实际行为

**【代码事实】** `Runtime/BundleCache/Services/SandboxBundleCache/EFileVerifyLevel.cs:7-23`：`Low = 1`、`Middle = 2`、`High = 3`。

实际行为在 `VerifyCacheFilesTask.VerifyFile`（`Runtime/BundleCache/Services/SandboxBundleCache/Operations/Internal/VerifyCacheFilesTask.cs:180-221`）：

```csharp
// 所有级别共同的前置检查
if (File.Exists(InfoFilePath) == false) return InfoFileNotExisted;    // :184
if (File.Exists(DataFilePath) == false) return DataFileNotExisted;    // :186
byte[] infoData = ReadAllBytes(InfoFilePath);
if (infoData.Length < 36) return InfoFileInvalid;                     // :190
if (magic != 0x31434F59) return InfoFileMagicError;                   // :195
if (version != 1) return InfoFileVersionError;                        // :199

if (verifyLevel == Low)    return Succeed;                            // :207-208
if (verifyLevel == Middle) return FileVerifyHelper.VerifyFile(DataFilePath, dataFileSize, 0);          // :209-210
if (verifyLevel == High)   return FileVerifyHelper.VerifyFile(DataFilePath, dataFileSize, dataFileCRC); // :211-212
```

| 级别 | 检查内容 | 单文件开销 | 能发现的问题 |
|---|---|---|---|
| **Low** | `__info` 和 `__data` 存在 + `__info` 头合法 | ~1 次 `stat` + 读 36B | 目录被删、`__info` 损坏 |
| **Middle** | Low + `__data` 文件大小 == `__info` 记录值 | + 1 次 `stat`（无内容读取） | 下载中断、磁盘截断、部分写入 |
| **High** | Middle + `__data` 全文件 CRC32 == `__info` 记录值 | **+ 全文件读取 + CRC 计算** | 字节级损坏（坏块、拷贝错误） |

`FileVerifyHelper.VerifyFile`（`Runtime/BundleCache/Services/SandboxBundleCache/FileVerifyHelper.cs:18-56`）：`fileSize == 0` 表示跳过大小校验，`fileCrc == 0` 表示跳过 CRC —— 这个"0 即哨兵"的设计让 `Middle` 复用同一函数（传 `crc=0`）。

**CRC 是流式计算，不整读入内存**（`Runtime/Utility/Utilities/HashUtility.cs:129-138` → `FileStream` + `ComputeCrc32AsUInt(Stream)`）—— 【代码事实】这对内存友好；但 **IO 量是全文件**，1 万个 5MB bundle = 50GB 读取量。

**【推断】** 移动端选择建议：
- 出厂首启（冷启动、下载完成）用 `Middle`（性价比最高，能挡住 99% 的"半文件"问题）
- 追求启动速度 → `Low`
- `High` 只在"有可靠怀疑缓存损坏"时用，或对小文件（配置表）单独用

注意：**`High` 在 3.0.7 的并行校验里也走多线程**，所以 CPU 不是瓶颈，**磁盘 IO 才是**（见 3.3.3）。

#### 3.3.3 3.0.7"后台线程连续校验"的具体实现

**【官方声称】** `CHANGELOG.md`（3.0.7 条目）：

> 优化缓存文件并行校验机制。后台校验任务不再依赖主线程逐帧提交，工作线程可连续处理待校验文件，减少帧率对缓存校验效率的影响。校验完成后，由主线程分帧登记缓存和清理无效文件。
> 一万个小文件、并发 8 的测试中：30 FPS 下耗时从 42.91 秒降至 5.35 秒，提速约 8 倍。60 FPS 下从 21.86 秒降至 3.43 秒，提速约 6 倍。

**【代码事实】** 实现分三层，职责清晰：

**① `VerifyCacheFilesTask`（纯后台，无 AsyncOperationBase）** —— `Runtime/BundleCache/Services/SandboxBundleCache/Operations/Internal/VerifyCacheFilesTask.cs`

```csharp
// Start()（:100-134）：
lock (_startLock) {
    int workerCount = Math.Min(_maxConcurrency, TotalCount);      // :118
    for (int i = 0; i < workerCount; i++)
        if (ThreadPool.QueueUserWorkItem(callback)) _runningWorkers++;   // :121-122
}

// worker（:147-179）：
lock (_startLock) { }   // :151-154  只等提交完成，立刻释放锁
while (_stopVerification == false) {
    int index = Interlocked.Decrement(ref _nextVerifyIndex);      // :158  ★ 无锁取任务
    if (index < 0) return;
    var element = _verifyFiles[index];
    element.VerifyResultCode = (int)VerifyFile(element, _verifyLevel);   // :163-164
    Interlocked.Increment(ref _completedCount);                   // :165
}
```

**核心机制**：`_nextVerifyIndex` 从 `TotalCount` 开始递减（`:112`），worker **连续循环**取任务，不 sleep、不回主线程、不逐帧提交。完成的文件数通过 `Interlocked.Increment` 累计（`:165`），主线程只读 `CompletedCount`（`:60-63`）算进度。

**收尾用 `Interlocked.Decrement(ref _runningWorkers) != 0` 判断最后一个 worker**（`:224`），然后发布终态（`Failed` 或 `Succeeded`）。

**② `VerifyCacheFilesOperation`（主线程侧，分帧登记）** —— `Runtime/BundleCache/Services/SandboxBundleCache/Operations/Internal/VerifyCacheFilesOperation.cs`

```csharp
// 计算并发（:60-62）：
int processorCount = Environment.ProcessorCount * 2 + 1;
int maxConcurrency = Math.Min(processorCount, _verifyMaxConcurrency);
maxConcurrency = Math.Max(maxConcurrency, 1);

// 等待后台（:72-92）：
Progress = CalculateMultiStageProgress(0, 2, progressRemain, progressTotal);
if (taskStatus == Processing) return;                          // 主线程只是轮询，不做事

// 登记结果（:94-124）：
while (_processIndex < _verifyFiles.Length && IsBusy == false) {   // :97 ★ 受 MaxTimeSlice 约束
    if (resultCode == Succeed) _fileCache.AddEntry(...);
    else { LogWarning(...); verifyElement.DeleteCacheFolder(); }   // :108-109  ★ 删除坏目录
    _processIndex++;
}
```

**关键设计**：后台做完后，主线程只做"把结果搬进字典"（`AddEntry`）和"删坏目录"（IO 在 worker 已完成判定，删除本身在主线程 —— 【推断】因为 `Directory.Delete` 在主线程做会有一点点 IO 抖动，但只在坏文件时才触发，正常路径没有 IO）。

**③ 并发公式的实际含义**

`Environment.ProcessorCount * 2 + 1`（`:60`）与 `FileVerifyMaxConcurrency`（默认 8）取 min。iQOO 16 是 8 核 → `8*2+1 = 17`，与 8 取 min = **8**。所以**默认实际并发 = min(17, 8) = 8**。

**`FileVerifyMaxConcurrency` 被钳制到 `[1, 32]`**（`SandboxFileSystem.cs:242`）。**注意：即使你设 32，实际也不会超过 `ProcessorCount*2+1`** —— 【代码事实】这是硬上限，防止线程池被塞满。

**【推断】** 8 并发对 Android 是合理的：Android 存储（UFS 3.1/4.0）顺序读带宽高但随机 IOPS 一般，8 个并发读已经能打满，再高只会增加 IO 争用。如果 `High` 校验导致启动慢，优先降 `FileVerifyLevel` 而不是升并发。

#### 3.3.4 扫描阶段（`SearchCacheFilesOperation`）也是分帧的

**【代码事实】** `SearchCacheFilesOperation.SearchFiles`（`:86-125`）：

```csharp
while (_shardIndex < _shardFolders.Length) {
    if (_bundleEnumerator == null)
        _bundleEnumerator = Directory.EnumerateDirectories(shardFolder).GetEnumerator();   // :94
    if (_bundleEnumerator.MoveNext()) { ...创建 SearchFileInfo... }                        // :97-109
    else { _bundleEnumerator.Dispose(); _shardIndex++; Progress = ...; }                  // :113-117
    if (IsBusy) break;                                                                    // :120
}
```

**按 2 字符哈希分片目录逐个枚举**（`SandboxBundleCacheConsts.HashFolderNameLength = 2`，`SandboxBundleCache.cs:315-317`）。每处理一个条目检查 `IsBusy`。所以扫描本身受 `MaxTimeSlice` 约束，不会卡帧。

**【推断】** 这意味着**扫描耗时 ≈ 分片数 × 每帧预算**。2 字符哈希 = 最多 256 个子目录，1 万个 bundle 分散其中。若 `MaxTimeSlice=10ms`，扫描 1 万个条目大约需要几十帧（~1 秒）。这是初始化耗时的第一个大头。

### 3.4 引用计数

#### 3.4.1 两级计数的完整关系

**【代码事实】** 有**两个完全独立的引用计数**，层级不同：

| 计数 | 位置 | 语义 | 增加时机 | 减少时机 |
|---|---|---|---|---|
| **`ProviderBase.RefCount`** | `ProviderBase.cs:66` | 有多少个 `HandleBase` 指向这个 provider | `CreateHandle`（`:288`） | `ReleaseHandle`（`:308`） |
| **`LoadBundleOperation.RefCount`** | `LoadBundleOperation.cs:45` | 有多少个 provider（+ 手动路径）引用这个 bundle | ① provider 构造时对每个 loader `Reference()`（`ProviderBase.cs:123`）② `DownloadSchedulerOperation.RegisterDownloadOperation` 里 `downloadFileOp.Reference()`（`:217`，这是另一个类） | provider `DestroyProvider` 时对每个 loader `Release()`（`ProviderBase.cs:253-256`）、`TryUnloadUnusedAsset` 路径 |

**关系图**：

```
        1 个 location
             │
        [ProviderKey 去重]        ← ResourceManager.cs:220
             │
             ▼
   ProviderBase (RefCount = N 个 Handle)
             │  构造时 Reference() 每个 loader
             ▼
   LoadBundleOperation (RefCount = M 个 Provider)
             │  RefCount==0 且 Referrer 链都销毁
             ▼
      DestroyLoader → BundleHandle.UnloadBundle() → AssetBundle.Unload(true)
```

**为什么必须两级？** 【推断】因为 bundle 是共享的：10 个不同的 prefab 可能都在同一个 bundle 里。asset 级引用归零 ≠ bundle 可以卸载。反过来，bundle 级引用归零也 ≠ 立即可卸（可能正在 `LoadBundleFile` 等待 IO，`IsReleasable()` 会拦住，`LoadBundleOperation.cs:278-280`）。

#### 3.4.2 `AssetHandle.Release()` 的完整后果链（逐步）

已经在上文 1.3.1 详述。这里补一个**计数变化的精确表格**：

| 操作 | `Provider.RefCount` | `LoadBundleOperation.RefCount` |
|---|---|---|
| `LoadAssetAsync` 第一次（provider 新建） | 0 → **1** | 0 → **1**（`ProviderBase.cs:123`） |
| `LoadAssetAsync` 第二次（provider 复用） | 1 → **2** | 不变（仍是 1） |
| `LoadAssetAsync<DifferentType>(同 location)` | 新建 provider（key 含 type，`AssetInfo.cs:76-89`） | 1 → **2** |
| `handle.Release()`（其一） | 2 → **1** | 不变 |
| `handle.Release()`（最后一个） | 1 → **0** → `TryUnloadBundle()`（默认 no-op） | 不变（**仍是 1**） |
| `UnloadUnusedAssetsAsync()` | 触发 `TryDestroyProviders` → provider 销毁 | 1 → **0** |
| → `CanDestroyLoader()` 通过 → `DestroyLoader()` | — | 卸载 AssetBundle |
| `LoadAssetAsync<DifferentType>(同 location)` 后再 `LoadAssetSync<TObject>` | **新建 provider**（因为 type 不同） | 1 → **2** |

**【推断】** 最后一个 `handle.Release()` 之后，`Provider.RefCount == 0` 但 provider 仍留在 `_providerDict` 里（`HandleBase.Release` 不删字典项）。provider 只在 `TryDestroyProviders`（`LoadBundleOperation.cs:301-326`）里被移除字典。所以**长期运行后 `_providerDict` 可能积压大量零引用 provider** —— 这是 `UnloadUnusedAssetsAsync` 必须周期性调用的另一个原因（不只是为了卸载 bundle，也是为了清字典）。

#### 3.4.3 为什么二次释放会抛异常

**【代码事实】** 分两种情况：

**情况 A：同一 handle 调两次 `handle.Release()`** → **不抛异常**。

第一次：`Provider.ReleaseHandle` 成功 → `Provider = null`（`HandleBase.cs:38`）。
第二次：`CheckValidWithWarning()` 检测 `Provider == null` → **打 warning 并 return**（`HandleBase.cs:30`、`:133-135`）。

**情况 B：绕过 HandleBase 直接重复调用 `Provider.ReleaseHandle(handle)`** → **抛 `YooInternalException`**。

```csharp
if (_handles.Remove(handle) == false)
    throw new YooInternalException($"Handle not found in cache list...");   // ProviderBase.cs:305
```

因为 `_handles` 是 `HashSet<HandleBase>`（`ProviderBase.cs:93`），第一次成功后已移除，第二次 `Remove` 返回 false。

**情况 C：`Provider.RefCount` 已为 0 时又调 `ReleaseHandle`**（例如 `ReleaseAllHandles` 与业务 `Release` 竞争）→ 抛 `"Attempting to release handle when RefCount is already zero"`（`:302`）。

**【推断】** 项目里最容易踩的是 `using (var handle = ...)` + 手动 `Release()` 双重释放。因为 `HandleBase.Dispose()` → `this.Release()`（`HandleBase.cs:42-45`），第二次是情况 A（安全，只 warning）。真正的崩溃风险来自"业务层缓存了 handle 并在多处 Release"以及"`UnloadAllAssetsAsync(shouldReleaseHandles: true)` 与业务 Release 并发"。

### 3.5 Bundle 生命周期

#### 3.5.1 `IBundleHandle` 与 `IBundleCache` 的分工

**【代码事实】**

| 接口 | 位置 | 职责 | 生命周期 |
|---|---|---|---|
| `IBundleCache` | `BundleCache/Interfaces/IBundleCache.cs:8` | **进程级**长期对象，按 PlayMode 创建一次 | 随 `IFileSystem` 存活（`OnDestroy` 时 `Dispose`，`SandboxFileSystem.cs:355-361`） |
| `IBundleHandle` | `BundleHandle/Interfaces/IBundleHandle.cs:8` | **bundle 级**对象，每次 `LoadBundleAsync` 产出一个 | 随 `LoadBundleOperation` 存活，`DestroyLoader` 时 `UnloadBundle()`（`LoadBundleOperation.cs:196`） |

**`IBundleHandle` 有 7 个实现**（`Runtime/BundleHandle/Services/`）：

| 实现 | 底层对象 | `UnloadBundle()` 行为 | 用于 |
|---|---|---|---|
| `AssetBundleHandle` | `AssetBundle` + `Stream` | `Unload(true)` + `Stream.Dispose()`（`:34`、`:39`） | 本地 AssetBundle |
| `RawBundleHandle` | `RawBundle`（`byte[]`） | `RawBundle.Unload()` → 清 `byte[]` + `Destroy(RawFileObject)` | 原生文件 |
| `ArchiveBundleHandle` | `ArchiveBundle` | `ArchiveBundle.Unload()` | 归档包 |
| `VirtualAssetBundleHandle` | 无 | **空实现**（`:17-19`） | 编辑器模拟（走 `AssetDatabase`） |
| `VirtualRawBundleHandle` | 无 | 空 | 编辑器模拟 |
| `VirtualArchiveBundleHandle` | 无 | 空 | 编辑器模拟 |
| `WebAssetBundleHandle` | Unity 网络 AssetBundle | 通过 `IWebPlatformStrategy.UnloadAssetBundle` | WebGL |

**关键事实：`IBundleHandle` 不管字节，只管"已载入的引擎对象"**。字节的来源（文件/网络/内存）由 `BundleCache` 在 `LoadBundleAsync` 内部解决。

#### 3.5.2 bundle 何时真正从内存卸载

**【代码事实】** 必须同时满足 5 个条件（由 `DestroyLoader` 的守卫 + `CanDestroyLoader` 决定）：

1. **不在加载中**：`_steps != LoadBundleFile`（`LoadBundleOperation.cs:279`、`:189`）
2. **`LoadBundleOperation.RefCount == 0`**（`:282-283`、`:192-193`）
3. **所有引用它的 bundle 已销毁**：`ReferrerBundleIDs` 遍历 `CheckBundleDestroyed`（`:255-267，默认非 EXPERIMENTAL 分支）
4. **有路径真正调用 `DestroyLoader`**：`TryUnloadUnusedAsset` / `DestroyUnusedBundle` / `DestroyAllBundleLoaders` / `ForceDestroyAllBundleLoaders`
5. **（可选）`AutoUnloadBundleWhenUnused`**：只有它为 true 时，`handle.Release()` 才会自动走第 4 步

**`ReferrerBundleIDs` 从哪来**：构建期写入清单，运行时 `DeserializeManifestOperation.FillBundleReferrerBundleIDs`（`Runtime/ResourcePackage/Operations/Internal/DeserializeManifestOperation.cs:399-436`）反填。它是 `DependentBundleIDs` 的**反向索引**。

**"互相引用的资源包无法卸载"（`LoadBundleOperation.cs:254`）的具体后果**：

假设 chunk_01.bundle 依赖 shared_material.bundle，同时 shared_material.bundle 依赖 chunk_01.bundle（构建期形成了环）。此时：
- chunk_01 的 loader 想销毁 → 检查 `ReferrerBundleIDs` 含 shared_material → `CheckBundleDestroyed(shared_material)` false → **不销毁**
- shared_material 想销毁 → 检查含 chunk_01 → false → **不销毁**
- **两者永久泄漏**

**【推断】** 这是分块流式最大的架构风险。构建期应该用 Bundle Reporter（`Editor/BundleReporter/`）验证依赖图无环，或至少确认 chunk bundle 之间没有互相引用。YooAsset 本身不做环检测（`DependentBundleIDs` 是直接写入，`TaskCreateManifest.ProcessBundleDepends` 只排序，见 `Editor/BundleBuilder/BuildTasks/TaskCreateManifest.cs:226-245`）。

#### 3.5.3 `AutoUnloadBundleWhenUnused` 的取舍

**【代码事实】** 默认 `false`（`InitializePackageOptions.cs:18`）。

- `false`：`handle.Release()` 只减计数，**不卸载**。bundle 常驻内存直到显式 `UnloadUnusedAssetsAsync`。
- `true`：每次最后一个 handle 释放时，立刻 `TryUnloadUnusedAsset(MainAssetInfo, 10)`（`ProviderBase.cs:326-332`）。

**【推断】** 对分块流式：
- `true` 的收益：内存不涨，chunk 离开视野立刻释放
- `true` 的代价：① 每释放一个 chunk 就要遍历依赖链 × 10 次循环（CPU）② 卸载后立刻回到同一 chunk 会重新 IO + 重新 `LoadFromFile`（性能抖动）③ **`Unload(true)` 是同步的**（`AssetBundle.Unload` 没有异步版本），大 bundle 会卡主线程

**结论**：本项目**不建议开 `true`**，而应"`false` + 按距离/时间窗周期性 `UnloadUnusedAssetsAsync()`"，把卸载集中在可控的时机（例如"每 N 个 chunk 从视野移除后统一回收一次"）。详见 §5。

---

## 4. 代码模板（可直接用）

> 以下代码基于 3.0.7 真实签名，已与 `Assets/Scripts/Runtime/Resources/YooAssetInitializer.cs` 的实现对齐（该项目文件是 3.0.7 的正确用法参考）。
> 命名空间统一 `YooAsset`。

### 4.1 初始化（三种模式）

```csharp
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using YooAsset;

public static class YooBoot
{
    public const string PackageName = "DefaultPackage";

    public static ResourcePackage Package { get; private set; }

    /// <summary>
    /// 通用初始化。playMode 由 mode 参数决定。
    /// 注意：必须先 YooAssets.Initialize()，再 CreatePackage()，再 InitializePackageAsync()，
    /// 最后才是 RequestPackageVersionAsync + LoadPackageManifestAsync。缺任何一步都无法加载资源。
    /// </summary>
    public static async Task<bool> BootAsync(EBootMode mode, string hostServerURL = null)
    {
        if (!YooAssets.IsInitialized)
            YooAssets.Initialize();

        // 每帧最多 10ms 用于推进 YooAsset 异步操作（30fps 下占 33.3ms 的 30%）
        // 注意：内部硬下限是 10ms，填小于 10 的值会被钳制为 10（AsyncOperationSystem.cs:53-57）
        YooAssets.SetAsyncOperationMaxTimeSlice(10);

        if (!YooAssets.TryGetPackage(PackageName, out var package) || package == null)
            package = YooAssets.CreatePackage(PackageName, 0);

        Package = package;

        var initOp = package.InitializePackageAsync(BuildOptions(mode, hostServerURL));
        await initOp;
        if (initOp.Status != EOperationStatus.Succeeded)
        {
            Debug.LogError($"[Yoo] InitializePackage failed: {initOp.Error}");
            return false;
        }

        return await LoadManifestAsync(package);
    }

    private static async Task<bool> LoadManifestAsync(ResourcePackage package)
    {
        // 版本号是必需的：LoadPackageManifestOperation 在 PackageVersion 为空时直接失败
        // (Runtime/ResourcePackage/Operations/LoadPackageManifestOperation.cs:41-45)
        var versionOp = package.RequestPackageVersionAsync();   // 默认超时 60s
        await versionOp;
        if (versionOp.Status != EOperationStatus.Succeeded)
        {
            Debug.LogError($"[Yoo] RequestPackageVersion failed: {versionOp.Error}");
            return false;
        }

        var manifestOp = package.LoadPackageManifestAsync(
            new LoadPackageManifestOptions(versionOp.PackageVersion, 60));
        await manifestOp;
        if (manifestOp.Status != EOperationStatus.Succeeded)
        {
            Debug.LogError($"[Yoo] LoadPackageManifest failed: {manifestOp.Error}");
            return false;
        }
        return true;
    }

    private static InitializePackageOptions BuildOptions(EBootMode mode, string hostServerURL)
    {
        switch (mode)
        {
            case EBootMode.EditorSimulate:
                return new EditorSimulateModeOptions
                {
                    // 必须是模拟构建的输出目录，不能是 null
                    // EditorFileSystem.OnCreate 在 packageRoot 为空时抛异常
                    // (Runtime/FileSystem/Services/EditorFileSystem/EditorFileSystem.cs:217-218)
                    EditorFileSystemParameters = FileSystemParameters
                        .CreateDefaultEditorFileSystemParameters(GetSimulatePackageRoot()),
                    BundleLoadingMaxConcurrency = 4,
                    AutoUnloadBundleWhenUnused = false,
                };

            case EBootMode.Offline:
                return new OfflinePlayModeOptions
                {
                    BuiltinFileSystemParameters =
                        FileSystemParameters.CreateDefaultBuiltinFileSystemParameters(),
                    BundleLoadingMaxConcurrency = 4,
                    AutoUnloadBundleWhenUnused = false,
                };

            case EBootMode.Host:
            default:
            {
                var cacheParams = FileSystemParameters.CreateDefaultSandboxFileSystemParameters(
                    new RemoteService(hostServerURL));

                // 校验级别：出厂首启用 Middle（发现半文件），日常用 Low（启动快）
                cacheParams.AddParameter(EFileSystemParameter.FileVerifyLevel, EFileVerifyLevel.Middle);
                cacheParams.AddParameter(EFileSystemParameter.FileVerifyMaxConcurrency, 8);

                // 下载：并发 6 / 每帧 4（30fps 移动网络）
                cacheParams.AddParameter(EFileSystemParameter.DownloadMaxConcurrency, 6);
                cacheParams.AddParameter(EFileSystemParameter.DownloadMaxRequestPerFrame, 4);

                // 看门狗：8 秒无数据即中止重试（文件下载本身无总时长超时）
                cacheParams.AddParameter(EFileSystemParameter.DownloadWatchdogTimeout, 8);

                // 断点续传：>= 1MB 启用
                cacheParams.AddParameter(EFileSystemParameter.DownloadResumeMinimumSize, 1024L * 1024L);

                // 覆盖安装清理：清全部缓存，避免旧清单/旧 bundle 残留
                cacheParams.AddParameter(EFileSystemParameter.InstallCleanupMode,
                    EInstallCleanupMode.ClearAllCacheFiles);

                var builtinParams = FileSystemParameters.CreateDefaultBuiltinFileSystemParameters();
                builtinParams.AddParameter(EFileSystemParameter.InstallCleanupMode,
                    EInstallCleanupMode.ClearAllManifestFiles);

                return new HostPlayModeOptions
                {
                    BuiltinFileSystemParameters = builtinParams,
                    CacheFileSystemParameters = cacheParams,
                    BundleLoadingMaxConcurrency = 4,
                    AutoUnloadBundleWhenUnused = false,
                };
            }
        }
    }

    private static string GetSimulatePackageRoot()
    {
        // 模拟构建输出目录 = {ProjectPath}/Bundles/{BuildTarget}/{PackageName}/Simulate
        // 版本号固定为 "Simulate"（Editor/BundleBuilder/BundleSimulateBuilder.cs:29）
#if UNITY_EDITOR
        string projectPath = System.IO.Path.GetDirectoryName(Application.dataPath);
        string target = UnityEditor.EditorUserBuildSettings.activeBuildTarget.ToString();
        return $"{projectPath}/Bundles/{target}/{PackageName}/Simulate";
#else
        // 非编辑器平台不存在模拟模式；这里抛异常便于尽早暴露错误的调用
        throw new System.PlatformNotSupportedException("EditorSimulateMode is only available in the Unity Editor.");
#endif
    }

    public static void Shutdown()
    {
        Package = null;
        if (YooAssets.IsInitialized)
            YooAssets.Destroy();   // 会销毁所有 package 并 shutdown AsyncOperationSystem
    }
}

public enum EBootMode { EditorSimulate, Offline, Host }

public sealed class RemoteService : IRemoteService
{
    private readonly string _root;
    public RemoteService(string host) { _root = host.TrimEnd('/'); }

    // fileName 是 bundle 的"远端文件名"（由 EFileNameStyle 决定，默认是 hash 文件名）
    public IReadOnlyList<string> GetRemoteUrls(string fileName)
        => new[] { $"{_root}/{fileName}" };
}
```

### 4.2 加载 / 实例化 / 释放

```csharp
using System;
using System.Threading.Tasks;
using UnityEngine;
using YooAsset;

public static class YooLoad
{
    /// <summary>
    /// 加载单个资源并返回句柄。调用方负责 Release。
    /// 失败返回的句柄仍有效（IsDone=true, Status=Failed），取 AssetObject 得到 null。
    /// </summary>
    public static async Task<AssetHandle> LoadAsync<TObject>(string location, uint priority = 0)
        where TObject : UnityEngine.Object
    {
        var handle = YooBoot.Package.LoadAssetAsync<TObject>(location, priority);
        await handle;                        // OperationAwaiter：失败不抛异常
        if (handle.Status != EOperationStatus.Succeeded)
            Debug.LogError($"[Yoo] Load failed: {location}, {handle.Error}");
        return handle;
    }

    /// <summary>
    /// 加载 + 实例化。注意：实例化产出的 GameObject 与 handle 生命周期独立，
    /// 但 handle 必须在 GameObject 使用期间保持存活（否则 AssetBundle.Unload(true) 会把源资源销毁）。
    /// </summary>
    public static async Task<(GameObject go, AssetHandle handle)> InstantiateAsync(
        string location, Transform parent = null, bool inWorldSpace = false, uint priority = 0)
    {
        var handle = await LoadAsync<GameObject>(location, priority);
        if (handle.Status != EOperationStatus.Succeeded)
            return (null, handle);

        var options = parent != null
            ? new InstantiateOptions(true, parent, inWorldSpace)
            : new InstantiateOptions(true);

        var op = handle.InstantiateAsync(options);
        await op;
        if (op.Status != EOperationStatus.Succeeded)
        {
            Debug.LogError($"[Yoo] Instantiate failed: {location}, {op.Error}");
            return (null, handle);
        }
        return (op.Result, handle);
    }

    /// <summary>
    /// 加载场景。所有场景句柄互不共享 provider（ResourceManager.cs:178 每次都新建）。
    /// 场景被 SceneManager 卸载时，ResourceManager 会自动 Release 对应句柄
    /// （ResourceManager.cs:622-642 OnSceneUnloaded）。
    /// </summary>
    public static async Task<SceneHandle> LoadSceneAsync(
        string location,
        UnityEngine.SceneManagement.LoadSceneMode mode = UnityEngine.SceneManagement.LoadSceneMode.Additive,
        bool allowActivation = true, uint priority = 0)
    {
        var handle = YooBoot.Package.LoadSceneAsync(
            location, mode, UnityEngine.SceneManagement.LocalPhysicsMode.None, allowActivation, priority);
        await handle;
        if (handle.Status != EOperationStatus.Succeeded)
            Debug.LogError($"[Yoo] LoadScene failed: {location}, {handle.Error}");
        return handle;
    }

    /// <summary>
    /// 周期性回收。建议每 N 秒或每卸载 M 个 chunk 调用一次。
    /// 默认 MaxLoopCount = 10，处理深层依赖链。
    /// 注意：它不调用 Resources.UnloadUnusedAssets()，开销可控。
    /// </summary>
    public static async Task CollectAsync()
    {
        var op = YooBoot.Package.UnloadUnusedAssetsAsync();
        await op;
        if (op.Status != EOperationStatus.Succeeded)
            Debug.LogWarning($"[Yoo] UnloadUnusedAssets failed: {op.Error}");
    }
}
```

### 4.3 预下载

```csharp
using System;
using UnityEngine;
using YooAsset;

public static class YooDownloader
{
    /// <summary>
    /// 按标签预下载。events 可用于进度 UI。
    /// 注意：下载器不会自动启动，必须显式 StartDownload()。
    /// </summary>
    public static ResourceDownloaderOperation CreateByTags(string[] tags, int maxConcurrency = 6, int retry = 3)
    {
        var options = new ResourceDownloaderOptions(tags, maxConcurrency, retry);
        var downloader = YooBoot.Package.CreateResourceDownloader(options);

        downloader.DownloadProgressChanged += args =>
            Debug.Log($"[Yoo] Download {args.Progress:P1} " +
                      $"({args.CurrentDownloadCount}/{args.TotalDownloadCount} files, " +
                      $"{args.CurrentDownloadBytes}/{args.TotalDownloadBytes} bytes)");
        downloader.DownloadError += args =>
            Debug.LogError($"[Yoo] Download error: {args.FileName}, {args.ErrorInfo}");
        downloader.DownloadCompleted += args =>
            Debug.Log($"[Yoo] Download completed. Success={args.Succeeded}, Error={args.Error}");

        downloader.StartDownload();   // ★ 必须调用，否则永远不开始
        return downloader;
    }

    /// <summary>
    /// 按资源列表预下载（含依赖）。downloadDependencies 语义见
    /// FileSystemHost.GetBundleInfosByAssetInfos 的 recursiveDepend 参数
    /// (Runtime/ResourcePackage/FileSystemHost.cs:441-515)。
    /// </summary>
    public static ResourceDownloaderOperation CreateByAssets(
        AssetInfo[] assetInfos, bool downloadDependencies = true,
        int maxConcurrency = 6, int retry = 3)
    {
        var options = new BundleDownloaderOptions(assetInfos, downloadDependencies, maxConcurrency, retry);
        var downloader = YooBoot.Package.CreateResourceDownloader(options);
        downloader.StartDownload();
        return downloader;
    }

    /// <summary>查询某资源还需要下载多少字节（0 表示已就绪）。</summary>
    public static long GetDownloadSize(string location)
        => YooBoot.Package.GetDownloadSize(location);
}
```

### 4.4 编辑器内"无需构建即可运行"的模拟清单

```csharp
#if UNITY_EDITOR
using UnityEngine;
using YooAsset;

/// <summary>
/// 编辑器启动时自动生成模拟清单（免去手动点 Bundle Builder）。
/// 原理：EditorSimulateBuildPipeline 走 AssetDatabase 直接生成清单，
/// 不产出 AssetBundle（详见 §8.4）。
/// </summary>
public static class YooEditorSimulateBootstrap
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void EnsureSimulateManifest()
    {
        // 只在首次进入 Play 时做一次；失败不阻断（让初始化自己报错）
        try
        {
            EditorSimulateBuildInvoker.Build(YooBoot.PackageName, (int)EBundleType.VirtualAssetBundle);
        }
        catch (System.Exception ex)
        {
            Debug.LogWarning($"[Yoo] Simulate build skipped: {ex.Message}");
        }
    }
}
#endif
```

---

## 5. 参数调优（移动端 30fps 大世界）

**【代码事实】** 下表所有"默认值"都能在源码中找到；"推荐值"是 **【推断】**（基于 30fps 预算与 Android 存储/网络特性），需实机 Profile 后回写。

### 5.1 必须显式设置的参数

| 参数 | 设置位置 | 默认值 | 推荐值 | 理由（含证据） |
|---|---|---|---|---|
| `SetAsyncOperationMaxTimeSlice` | `YooAssets.cs:227` | `long.MaxValue`（不限） | **10~12ms** | 默认不限 = 单帧跑完所有待推进操作（`AsyncOperationSystem.cs:36`、`:75-76`）。30fps 下 12ms 约占 36% 预算。**硬下限是 10ms，填更小的值会被强制改成 10 并打 warning**（`:26`、`:53-57`），所以 `6` 之类的小值无效，只能从 10 起步 |
| `BundleLoadingMaxConcurrency` | `InitializePackageOptions.cs:13` | `int.MaxValue` | **2~4** | 默认不限 = 可同时发起无限个 `LoadBundleFile`（`ResourceManager.cs:569-572` 只做 `>=` 判定）。每个 bundle 的 `LoadFromFileAsync` 都会占主线程一小段 + IO，移动端 4 个并发已接近 UFS 随机读上限 |
| `AutoUnloadBundleWhenUnused` | `InitializePackageOptions.cs:18` | `false` | **保持 false** | 开 true 会让每次最后 handle 释放时同步遍历依赖链 ×10（`ProviderBase.cs:326-332` → `ResourceManager.cs:105-143`），且 `AssetBundle.Unload(true)` 是同步的。分块场景应集中回收，见 5.4 |
| `DownloadMaxConcurrency` | `SandboxFileSystem.cs:91` | `8` | **4~6** | 移动网络下 8 并发易被 CDN 限流；也增加本地内存与 socket 占用。钳制 `[1,32]`（`:257`） |
| `DownloadMaxRequestsPerFrame` | `SandboxFileSystem.cs:99` | `8` | **2~4** | 单帧创建 8 个 UnityWebRequest 有可观 CPU 与 GC 尖峰（每帧启动数在 `DownloadSchedulerOperation.cs:138-139` 生效） |
| `DownloadWatchdogTimeout` | `SandboxFileSystem.cs:104` | `0`（禁用） | **8~15 秒** | 文件下载路径 `timeout = 0`（`DownloadAndCacheFileOperation.cs:176`、`:195`），**不开看门狗则可能永久挂起**（`UnityWebRequestBase.cs:261-282`） |
| `ResumeDownloadMinimumSize` | `SandboxFileSystem.cs:109` | `long.MaxValue`（禁用） | **1MB** | 默认完全禁用断点续传（`DownloadAndCacheFileOperation.cs:48`）。大 world bundle 在弱网/切后台场景收益显著 |
| `FileVerifyLevel` | `SandboxFileSystem.cs:70` | `Low` | **首启 Middle，日常 Low** | 三级行为见 §3.3.2。`High` 要全量读文件算 CRC，1 万 bundle × 5MB = 50GB IO |
| `FileVerifyMaxConcurrency` | `SandboxFileSystem.cs:78` | `8` | **8**（保持） | 实际并发 = `min(CPU*2+1, 本值)`（`VerifyCacheFilesOperation.cs:60-62`）。钳制 `[1,32]`（`SandboxFileSystem.cs:242`） |
| `InstallCleanupMode` | `SandboxFileSystem.cs:65` | `None` | **`ClearAllCacheFiles`** | 默认不做任何事（`SFSInitializeOperation.cs:69-71`），覆盖安装后旧缓存残留会导致清单/版本错配 |

### 5.2 参数之间的相互制约

**【代码事实】** 有几个隐含的耦合，单独调一个没有意义：

1. **`MaxTimeSlice` × `BundleLoadingMaxConcurrency`**：`LoadBundleOperation` 在 `CheckConcurrency` 帧只是"放行或等待"（`LoadBundleOperation.cs:85-97`），真正耗时在 `LoadBundleFile` 的每帧 `UpdateOperation()`。如果 `MaxTimeSlice` 很小而并发很大，会出现"很多 loader 都放行了但每帧只能推进一两个"——**实际并发被时间片限制**。二者要一起调。
2. **`MaxTimeSlice` × `DownloadMaxRequestsPerFrame`**：`DownloadSchedulerOperation` 也在同一调度器里被驱动。每帧启动请求越少，单帧 CPU 越低，但下载爬坡越慢。
3. **下载页的 `MaximumConcurrency` × 文件系统的 `DownloadMaxConcurrency`**：前者是"业务下载器内的活跃数"，后者是"物理并发上限"。前者设 32 而后者是 6，实际只有 6 个在跑（`DownloadSchedulerOperation.cs:131-153`）。**填前者时不要超过后者太多**，否则只是排队占内存。
4. **`FileVerifyLevel=High` × `MaxTimeSlice`**：校验本身在后台线程不受时间片影响，但**主线程登记阶段**受（`VerifyCacheFilesOperation.cs:97`）。`High` 时后台 IO 密集，主线程登记可能被 IO 挤，`MaxTimeSlice` 过小会让登记拖很久。**【推断】** 用 `High` 时建议 `MaxTimeSlice` 至少 12。

### 5.3 初始化耗时预估（供启动预算参考）

**【推断】** 基于代码路径的分解（非实测）：

| 阶段 | 主要开销 | 受什么影响 | 调优方向 |
|---|---|---|---|
| `SearchCacheFilesOperation` | 目录枚举 1 万条目 | `MaxTimeSlice`（每帧 `IsBusy` 检查，`:120`） | 提高 `MaxTimeSlice`；或减少 bundle 数量（合并小文件） |
| `VerifyCacheFilesOperation` | 并发读 + 校验 | `FileVerifyLevel`、`FileVerifyMaxConcurrency`、磁盘带宽 | 降级别；限制首次安装后的校验范围 |
| 清单下载 + 反序列化 | 网络 + 逐条解析 | 清单条目数、`MaxTimeSlice`（`DeserializeManifestOperation.cs:184`、`:219`） | 降低清单体积（关 `IncludeAssetGuid`、开 `ReplaceAssetPathWithAddress`） |
| `BuiltinCatalog` 加载 | 读一个 json/bytes | 内置 catalog 大小 | 正常可忽略 |

**【推断】** 覆盖安装清理（`ClearAllCacheFiles`）会让首次启动的校验阶段变空（没有旧文件），但会增加一次全量重新下载——这是"首次更新后首启慢"的代价，通常值得。

### 5.4 分块流式的卸载策略（本项目重点）

**【推断】** 推荐的做法（结合 §3.4.2 关于 provider 字典积压、§3.5.2 关于可卸载条件的事实）：

```csharp
// 每卸载 M 个 chunk（或每 2~3 秒），调一次：
await YooBoot.Package.UnloadUnusedAssetsAsync();

// 特例：明确知道某个 chunk 不再需要、且要立刻腾内存时：
YooBoot.Package.TryUnloadUnusedAsset(chunkLocation, 10);
```

理由链：
1. `AutoUnloadBundleWhenUnused = false` → 释放 handle 不会卸载 bundle（`ProviderBase.cs:328`）
2. `UnloadUnusedAssetsAsync` 走 `DestroyUnusedBundle()`（`UnloadUnusedAssetsOperation.cs:43`），**不调用 `Resources.UnloadUnusedAssets()`**（全文仅 `UnloadAllAssetsOperation.cs:95` 一处）→ 开销是字典遍历 + `AssetBundle.Unload(true)`，可控
3. `MaxLoopCount = 10`（`ResourcePackage.cs:284`）处理深层依赖，且循环内 `IsBusy` 会让出（`UnloadUnusedAssetsOperation.cs:46`）→ **不会卡一帧**
4. `ReferrerBundleIDs` 检查（`LoadBundleOperation.cs:255-267`）保证不会误卸共享 bundle

**不要做的事**：
- 不要在每个 chunk 卸载时都调 `UnloadUnusedAssetsAsync()`（每次都遍历整个 loader 字典）
- 不要在 `OnDestroy` 里调 `Resources.UnloadUnusedAssets()`（那是 Unity 的全局操作，与 YooAsset 的卸载是两套）
- 不要依赖 `AutoUnloadBundleWhenUnused = true` 来实现"实时释放"——`ReferrerBundleIDs` 有交叉引用时它什么也不做，只会白白消耗 CPU

---

## 6. 常见陷阱

> 每条都给代码证据。分"会直接报错"和"静默错误"两类。

### 6.1 会直接报错

**① 空版本号加载清单**

```csharp
// 错误
package.LoadPackageManifestAsync(new LoadPackageManifestOptions(null, 60));
// 结果：Status = Failed, Error = "Package version is null or empty."
```

证据：`Runtime/ResourcePackage/Operations/LoadPackageManifestOperation.cs:41-45`。**必须先 `RequestPackageVersionAsync()` 拿到非空版本号**。即使离线模式也必须走这一步（`BFSRequestPackageVersionOperation` 读 `StreamingAssets/yoo/<pkg>/<pkg>.version`）。

**② 重复 `InitializePackageAsync`**

```csharp
package.InitializePackageAsync(options);   // 第二次
// 结果：InvalidOperationException("Resource package 'X' is already initialized.")
```

证据：`ResourcePackage.cs:100-101`。**只有上次是 `Failed` 时才能重调**（会先 `TryResetAfterFailure`，`ResourcePackage.cs:97`、`:108-114`）。

**③ `YooAssets.Initialize` 重复调用**

```csharp
// 结果：InvalidOperationException("YooAssets is already initialized.")
```

证据：`Runtime/YooAssets.cs:51-52`。**用 `if (!YooAssets.IsInitialized)` 保护**。

**④ 空包裹名**

```csharp
YooAssets.CreatePackage("");   // ArgumentException("Package name cannot be null or empty.")
```

证据：`Runtime/YooAssets.cs:251-252`。

**⑤ `BundleLoadingMaxConcurrency <= 0`**

```csharp
// 结果：初始化失败，Error = "BundleLoadingMaxConcurrency value must be greater than zero."
```

证据：`InitializePackageOperation.cs:64-70`。

**⑥ 模拟模式下 `EditorFileSystemParameters` 传 `null` 根目录**

```csharp
FileSystemParameters.CreateDefaultEditorFileSystemParameters(null);
// 结果：ArgumentException("EditorFileSystem package root is null or empty.")
```

证据：`Runtime/FileSystem/Services/EditorFileSystem/EditorFileSystem.cs:217-218`。**必须传模拟构建的输出目录**。

**⑦ 用 `Resources.Load` 加载 YooAsset 管理的资源**

不会报错，但会拿到 `null`（如果资源没打进 Resources 目录）。**【推断】** 文档层面的强约定，代码里没有强制检查。

### 6.2 静默错误（最危险，不报错但行为不对）

**⑧ `await op;` 后不检查 `Status`**

```csharp
await handle;
var obj = handle.AssetObject;   // 失败时是 null，之后 NullReferenceException
```

证据：`Runtime/AsyncOperation/OperationAwaiter.cs:32-34`（`GetResult()` 空实现，注释"业务失败不视为异常"）。**永远 `if (op.Status != EOperationStatus.Succeeded)`**。

**⑨ 枚举值写 `Succeed`（2.x 风格）**

```csharp
if (op.Status == EOperationStatus.Succeed)   // 编译失败（3.0 未定义）
```

证据：`EOperationStatus` 只有 `None/Processing/Succeeded/Failed`（`Runtime/AsyncOperation/EOperationStatus.cs:7-27`），`Succeed` 别名仅在 `#if YOOASSET_LEGACY_API` 下存在（`:29-34`）。

**⑩ `TryGetPackage(string)` 单参数重载**

3.0 只有 `TryGetPackage(string, out ResourcePackage)`（`Runtime/YooAssets.cs:162-167`）。单参数版本仅在兼容层（`CompatibleYooAssets.cs:23-27`，需 `YOOASSET_LEGACY_API`）。

**⑪ 句柄复用导致"类型不同却以为复用"**

```csharp
var h1 = package.LoadAssetAsync<Sprite>(loc);      // providerKey = "[path][Sprite]"
var h2 = package.LoadAssetAsync<Texture2D>(loc);   // providerKey = "[path][Texture2D]" → 新 provider！
```

证据：`AssetInfo.AssetKey` 含 `AssetType.Name`（`Runtime/ResourcePackage/AssetInfo.cs:76-88`），`ResourceManager.cs:219` 用它做 key。**【推断】** 后果：同一个 bundle 被加载两次 `LoadBundleOperation.Reference()` 两次，但**第二个 provider 会触发第二次 `ABHLoadAssetOperation.LoadAssetAsync`** —— AssetBundle 内部会缓存，实际不会重复解码，但会多一份 provider 与 handle 开销，且两个 handle 必须分别 Release。**用 `LoadAssetSync(location)`（无泛型）可以命中同一 provider。**

**⑫ `AutoUnloadBundleWhenUnused` 默认 false 导致"内存不降"**

```csharp
handle.Release();   // 以为卸载了，其实没有
```

证据：`InitializePackageOptions.cs:18` 默认 `false`；`ProviderBase.cs:328`。**必须显式 `UnloadUnusedAssetsAsync()`**。

**⑬ 边玩边下无限重试导致"加载卡死无报错"**

```csharp
// 未预下载就 LoadAssetAsync，bundle 缺失 → 触发边玩边下
// SFSLoadPackageBundleOperation.cs:76 传 int.MaxValue 作为重试次数
```

证据：`Runtime/FileSystem/Services/SandboxFileSystem/Operations/SFSLoadPackageBundleOperation.cs:76`（`new FSDownloadBundleOptions(_options.Bundle, int.MaxValue)`）。**【推断】** 弱网下表现是"加载进度不动，也没有失败日志"。**要么预下载，要么设 `DownloadDisableOndemand = true`**（`SandboxFileSystem.cs:83`、`SFSLoadPackageBundleOperation.cs:48-53`）让缺失 bundle 直接报错。

**⑭ 覆盖安装后旧缓存导致"版本对不上"**

默认 `InstallCleanupMode = None`（`SandboxFileSystem.cs:65`），`SFSInitializeOperation.cs:69-71` 只打 warning。结果：旧清单/旧 bundle 残留，可能和新版本资源 hash 冲突。**必须显式设置**。

**⑮ `Dispose()` 与 `Release()` 混用**

```csharp
using (var handle = package.LoadAssetSync<GameObject>(loc)) { ... }
handle.Release();   // 二次释放 → 只 warning，不抛异常，但逻辑已经乱了
```

证据：`HandleBase.cs:42-45`（`Dispose` → `Release`）、`:30`（二次 `Release` 走 `CheckValidWithWarning` 分支静默返回）。**【推断】** 虽然不崩，但"多减一次引用"不会发生（因为提前 return），所以实际是**安全但无意义**。真正危险的是"缓存 handle 到静态字段后 Release 两次"——第二次静默，第一次已经让 `Provider.RefCount` 归零，之后调 `handle.AssetObject` 会返回 null 并 warning。

**⑯ `BundleFileHandle` / `GetAssetInfo()` 的误区**

`handle.GetAssetInfo()` 返回的是创建时的 `AssetInfo` 快照（`HandleBase.cs:13`、`:21`），**Release 之后仍然可读**（`_assetInfo` 是 readonly 字段）。但 `handle.AssetObject`、`handle.Status` 等全部走 `Provider`，Release 后返回 null/0/None（`HandleBase.cs:59-120`）。

**⑰ 清单格式不兼容（跨版本）**

```csharp
// Error = "Manifest file version is not compatible: X != 2."
```

证据：`PackageManifestConsts.FileVersion = 2`（`Runtime/ResourcePackage/PackageManifestConsts.cs:22`），校验在 `DeserializeManifestOperation.cs:93-98`。**【代码事实】** 3.0.7 的清单格式与 2.x 完全不同（2.x 是 FileVersion 1 的另一种布局）。**升级 YooAsset 大版本必须重新构建全部 bundle**，不能复用旧清单。

**⑱ `LocationToLower` 与 `EnableAddressable` 冲突**

```csharp
// 结果：YooManifestInvalidException("Addressable mode does not support converting locations to lowercase.")
```

证据：`DeserializeManifestOperation.cs:122-123`。另一个组合限制：`ReplaceAssetPathWithAddress` 要求 `EnableAddressable = true`（`:124-125`）。

**⑲ 同一资源路径被两个 Collector 重复收集**

```
// 结果：InvalidOperationException("Collecting asset file already exists: 'X'")
```

证据：`Editor/BundleCollector/BundleCollectorPackage.cs:125-128`、`BundleCollectorGroup.cs:100-105`。**【推断】** 分块项目中"地形 prefab 同时被 chunk 收集器和公共收集器命中"是很常见的配置错误，构建时才报。

**⑳ 可寻址地址重复 / 地址以 `Assets/` 开头**

```
// InvalidOperationException("Address cannot be an asset path in package: 'X'")
// InvalidOperationException("Address already exists: 'Y'")
```

证据：`BundleCollectorPackage.cs:145-151`。

**㉑ 枚举注释里的"默认值"与代码不一致**

`Runtime/FileSystem/EFileSystemParameter.cs:45` 的 XML 注释写着 `DownloadMaxConcurrency` "默认值：10（推荐范围 1-32）"，`:50` 写着 `DownloadMaxRequestPerFrame` "默认值：5（推荐范围 1-10）"。但**代码里的真实默认值是 8 和 8**（`SandboxFileSystem.cs:91`、`:99`）。

**【推断】** 这是注释没跟随代码更新造成的偏差。**调参时必须以 `SandboxFileSystem` 的属性初始化值为准，不要信枚举注释。** 同类情况还有 `EditorFileSystem.cs:64`、`:72`（也是 8/8）与 `BuiltinFileSystem.cs:114`、`:122`（解压用的 8/8）。

### 6.3 与"流式加载"直接相关的陷阱

**㉒ `ReferrerBundleIDs` 成环 → 永久泄漏**

见 §3.5.2。代码证据：`LoadBundleOperation.cs:253-267`。**【推断】** 检测方式：构建后用 Bundle Reporter 检查依赖图；或在编辑器里对 `PackageManifest.BundleList` 做拓扑排序验证无环。

**㉓ 同步 API 绕过时间片**

`LoadAssetSync` / `WaitForAsyncComplete()` 会让 `AsyncOperationBase.IsBusy` 恒为 false（`AsyncOperationBase.cs:41-49`），从而**绕过 `MaxTimeSlice`，阻塞当前帧直到完成**。分块流式下**绝对不要用同步 API**。

**㉔ `await` 后续代码在同一帧执行**

`OperationAwaiter.UnsafeOnCompleted` 把 continuation 挂到 `Completed` 事件（`OperationAwaiter.cs:45`），而该事件在调度器 `Update()` 的调用栈里同步触发（`AsyncOperationBase.cs:535-565`）。所以：

```csharp
await handle;
// ↓ 这段代码直接跑在 YooAssetsDriver.Update() 的栈里
var go = handle.InstantiateSync();   // 同步实例化，直接吃本帧预算
```

**【推断】** 分块流式应在 `await` 之后把重活丢到自己的分帧队列，而不是直接做。

---

## 7. 扩展点与禁区

### 7.1 推荐的扩展点（public 接口，稳定）

**【代码事实】** 全部在 `Runtime/Interfaces/`，都是 `public`：

| 接口 | 位置 | 用途 | 注入方式 |
|---|---|---|---|
| `IBundleDecryptor` + 3 派生 | `IBundleDecryptor.cs:52-107` | bundle 解密。三选一：`IBundleOffsetDecryptor`（跳头，零拷贝）、`IBundleMemoryDecryptor`（全内存）、`IBundleStreamDecryptor`（流式，大文件首选） | `EFileSystemParameter.AssetBundleDecryptor` / `RawBundleDecryptor` / `ArchiveBundleDecryptor` |
| `IBundleEncryptor` | `IBundleEncryptor.cs:51-59` | 构建期加密 | 编辑器 `BuildParameters.BundleEncryptor` |
| `IManifestDecryptor` / `IManifestEncryptor` | `IManifestDecryptor.cs` | 清单加解密 | 运行时 `EFileSystemParameter.ManifestDecryptor`；构建期 `BuildParameters.ManifestDecryptor` |
| `IDownloadRetryPolicy` | `IDownloadRetryPolicy.cs:8-25` | 重试判定与退避 | `EFileSystemParameter.DownloadRetryPolicy` |
| `IDownloadUrlPolicy` | `IDownloadURLPolicy.cs:8-30` | 多 CDN / 灰度切换 | `EFileSystemParameter.DownloadUrlPolicy` |
| `IRemoteService` | `IRemoteService.cs:8-16` | `fileName → URL[]` 映射 | `CreateDefaultSandboxFileSystemParameters(remoteService)` |
| `IBundleUnpackPolicy` | `IBundleUnpackPolicy.cs:36-40` | 判定哪些内置 bundle 需解压到沙盒 | `EFileSystemParameter.BundleUnpackPolicy`（`BuiltinFileSystem.cs:306-309`） |
| `IBuiltinFileAccessor` | `IBuiltinFileAccessor.cs` | Android 直接读 StreamingAssets（如 BetterStreamingAssets） | `EFileSystemParameter.BuiltinFileAccessor` |
| `ILogger` | `Utility/YooLogger.cs:8-29` | 自定义日志 | `YooAssets.Initialize(logger)` |
| `CustomAsyncOperation` | `AsyncOperation/CustomAsyncOperation.cs` | 把自己的异步任务接入 YooAsset 调度器 | `YooAssets.StartOperation(op)` 或 `package.StartOperation(op)` |

**`IWebPlatformStrategy` / `IWebPreloadStrategy`** 也在 `Runtime/FileSystem/Interfaces/`，但**都是 `internal`**（`IWebPlatformStrategy.cs:50`、`IWebPreloadStrategy.cs:47`），只对 WebGL/小游戏有意义，移动端不涉及；如需实现必须放进 `YooAsset.Custom` 程序集。

### 7.2 推荐的自定义规则扩展（编辑器侧）

**【代码事实】** 全部是 public 接口 + `[DisplayName]` 特性，会被 `BundleCollectorSettingData` 反射收集：

| 接口 | 位置 | 作用 |
|---|---|---|
| `IBundlePackRule` | `Editor/BundleCollector/CollectRules/IBundlePackRule.cs:111-119` | 决定"哪些资源打进哪个 bundle" |
| `IAddressRule` | `Editor/BundleCollector/CollectRules/IAddressRule.cs` | 生成可寻址地址 |
| `IAssetFilterRule` | `Editor/BundleCollector/CollectRules/IAssetFilterRule.cs` | 目录内筛选哪些资源 |
| `IAssetIgnoreRule` | `Editor/BundleCollector/CollectRules/IAssetIgnoreRule.cs` | 全局忽略规则 |
| `IGroupActiveRule` | `Editor/BundleCollector/CollectRules/IGroupActiveRule.cs` | 分组是否启用（可接 CI 开关） |

注册机制：`BundleCollectorSettingData` 静态构造里 `EditorAssemblyUtility.GetAssignableTypes(typeof(IXXX))` 扫描所有程序集（`Editor/BundleCollector/BundleCollectorSettingData.cs:41-70`）。所以**放在任意 Editor 程序集里都会自动被发现**。

### 7.3 不要碰的区域（版本变动频繁 / 内部实现）

**【推断】** 基于文件结构、`internal` 可见性与 CHANGELOG 的变动频率：

| 区域 | 原因 | 证据 |
|---|---|---|
| `Runtime/**/Operations/Internal/` 下所有类 | 纯内部状态机，每个版本都在改。3.0.6 就重构了 `InternalAbort` → `InternalDispose` | `CHANGELOG.md` 3.0.6 条目 |
| `Runtime/BundleCache/Services/*/Operations/` | 缓存实现细节（尤其是 3.0.7 刚大改的校验） | `CHANGELOG.md` 3.0.7 条目 |
| `Runtime/DownloadSystem/Services/UnityWebRequest/` | 3.0.7 刚把 `IDownloadRequest.IsDone` 改成无副作用查询并新增 `UpdateRequest()`，明确说"通过友元程序集定制下载实现的项目需同步调整" | `CHANGELOG.md` 3.0.7 条目 |
| `Runtime/FileSystem/EFileSystemParameter` 枚举 | 参数名每个版本都在增删（已有 `DownloadMaxRequestPerFrame` 这种少 s 的坑） | `EFileSystemParameter.cs:52` |
| `Runtime/ResourcePackage/PackageManifest` / `PackageBundle` 的二进制布局 | 清单格式随版本变（`FileVersion`），改动会破坏兼容 | `PackageManifestConsts.cs:22` |
| `IsWaitForCompletion` / `ExecuteBatch` / `ExecuteUntilComplete` | 同步等待语义，用于内部实现，业务层用它会造成不可控阻塞 | `AsyncOperationBase.cs:162-206`、`:447-501` |

**如果必须改内部实现**：用 `YooAsset.Custom` 程序集名（`Runtime/Assembly/AssemblyInfo.cs:13-14` 已声明 `InternalsVisibleTo("YooAsset.Custom")` 和 `"YooAsset.Custom.Editor"`）。**这样不用改包源码，也能访问 internal 成员**。

### 7.4 一个具体的扩展示例（流式解密器，最适合大 world bundle）

```csharp
using System.IO;
using YooAsset;

/// <summary>
/// 基于流式解密的 bundle 解密器。
/// 相比内存解密（LoadFromMemory），它的内存峰值是 bufferSize 而非 bundle 大小。
/// 大 world chunk bundle（20~50MB）必须用这个。
/// </summary>
public sealed class ChunkStreamDecryptor : IBundleStreamDecryptor
{
    private readonly byte _key;

    public ChunkStreamDecryptor(byte key) { _key = key; }

    public int GetBufferSize(BundleDecryptArgs args) => 128 * 1024;

    public Stream CreateDecryptionStream(BundleDecryptArgs args)
    {
        // 调用方拥有返回值所有权，卸载时由 AssetBundleHandle.UnloadBundle 关闭 (AssetBundleHandle.cs:39)
        var inner = new FileStream(args.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024);
        return new XorStream(inner, _key);
    }

    private sealed class XorStream : Stream
    {
        private readonly Stream _inner;
        private readonly byte _key;
        public XorStream(Stream inner, byte key) { _inner = inner; _key = key; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int n = _inner.Read(buffer, offset, count);
            for (int i = 0; i < n; i++) buffer[offset + i] ^= _key;
            return n;
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _inner.Length;
        public override long Position { get => _inner.Position; set => throw new System.NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new System.NotSupportedException();
        public override void SetLength(long value) => throw new System.NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new System.NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) _inner.Dispose(); base.Dispose(disposing); }
    }
}
```

**【推断】** 注意 `CanSeek => false`：`AssetBundle.LoadFromStream` 要求流可读，**不要求可 seek**，但 Unity 会从头顺序读。这对 LZ4 bundle 是合适的；对 LZMA 也无影响（引擎自己解压到内存）。

---

## 8. 编辑器工作流

### 8.1 `BundleCollectorSetting` 的数据结构（Package → Group → Collector 三级）

**【代码事实】**

```
BundleCollectorSetting : ScriptableObject
├─ ShowPackageView : bool
├─ ShowEditorAlias : bool
├─ UniqueBundleName : bool                 // 全局：bundle 名是否加 package 前缀
└─ Packages : List<BundleCollectorPackage>
    ├─ PackageName : string
    ├─ PackageDesc : string
    ├─ EnableAddressable : bool            // 启用可寻址定位
    ├─ SupportExtensionless : bool         // 支持无后缀名 location（默认 true）
    ├─ LocationToLower : bool              // location 大小写不敏感
    ├─ IncludeAssetGUID : bool             // 清单包含 GUID（会增大清单）
    ├─ AutoCollectShaders : bool           // 自动收集所有 shader 到 unityshaders bundle（默认 true）
    ├─ IgnoreRuleName : string             // 默认 "NormalIgnoreRule"
    └─ Groups : List<BundleCollectorGroup>
        ├─ GroupName : string
        ├─ GroupDesc : string
        ├─ AssetTags : string              // 分号分隔
        ├─ ActiveRuleName : string         // 默认 "EnableGroup"
        └─ Collectors : List<BundleCollector>
            ├─ CollectPath : string        // 文件夹或单文件
            ├─ CollectorGUID : string      // 用于路径变更自愈
            ├─ CollectorType : ECollectorType
            ├─ AddressRuleName : string    // 默认 "AddressByFileName"
            ├─ PackRuleName : string       // 默认 "PackDirectory"
            ├─ FilterRuleName : string     // 默认 "CollectAll"
            ├─ AssetTags : string
            └─ UserData : string
```

证据：`Editor/BundleCollector/BundleCollectorSetting.cs:13-33`、`BundleCollectorPackage.cs:14-60`、`BundleCollectorGroup.cs:14-39`、`BundleCollector.cs:12-45`。

**配置文件的物理位置**：`SettingLoader.LoadSettingData<BundleCollectorSetting>()` 会在 `Assets/BundleCollectorSetting.asset` 自动创建（`Editor/SettingLoader.cs:20-29`）。**必须唯一**，多个会抛异常（`:33-40`）。

**`ECollectorType` 三选一**（`Editor/BundleCollector/ECollectorType.cs:9-30`）：

| 类型 | 进清单（可 `LoadAsset`） | 参与打包 |
|---|---|---|
| `MainAssetCollector` | 是 | 是 |
| `StaticAssetCollector` | 否 | 是 |
| `DependAssetCollector` | 否 | 仅在**被主资源引用**时 |

**【推断】** 分块流式项目：chunk 的 prefab 用 `MainAssetCollector`（要能按 location 加载），chunk 引用的地形数据/材质用 `DependAssetCollector`（自动跟随，不进清单）。

**配置校验**：`CheckConfigError` 会检查 ① `CollectPath` 的 GUID 有效 ② `CollectorType != None` ③ 三个 Rule 名都存在（`BundleCollector.cs:74-96`）。`FixConfigError` 能根据 `CollectorGUID` 修复路径变更（`:104-140`）。

### 8.2 打包规则（`IBundlePackRule`）各选项的实际行为

**【代码事实】** 默认实现在 `Editor/BundleCollector/DefaultRules/DefaultBundlePackRule.cs`：

| 类 | DisplayName | 行为 | 例子 |
|---|---|---|---|
| `PackSeparately`（`:59`） | 资源包名: 文件路径 | **每个文件一个 bundle** | `Assets/UI/a.png` → `assets_ui_a.bundle` |
| `PackDirectory`（`:77`） | 资源包名: 父类文件夹路径 | **同目录一个 bundle**（默认） | `Assets/UI/Shop/a.png` → `assets_ui_shop.bundle` |
| `PackTopDirectory`（`:96`） | 资源包名: 收集器下顶级文件夹路径 | 收集路径下**第一级子目录**一个 bundle | 收集 `Assets/UI`，`Assets/UI/Shop/x.png` → `assets_ui_shop.bundle` |
| `PackCollector`（`:126`） | 资源包名: 收集器路径 | **整个 collector 一个 bundle** | 收集 `Assets/UI` → `assets_ui.bundle` |
| `PackGroup`（`:152`） | 资源包名: 分组名称 | **整个 group 一个 bundle** | → `<GroupName>.bundle` |
| `PackRawFile`（`:167`） | 打包原生文件 | 每个原生文件一个 `rawfile` bundle | → `*.rawfile` |
| `PackVideoFile`（`:182`） | 打包视频文件 | 保留原扩展名 | → `*.mp4` 等 |
| `PackShader`（`:202`） | 打包着色器文件 | 全部塞进 `unityshaders.bundle` | — |
| `PackShaderVariants`（`:215`） | 打包着色器变种集合文件 | 同上 | — |

**命名最终格式**（`IBundlePackRule.cs:79-88`）：

```csharp
bundleName = 规则返回名.Replace('/', '_').Replace('.', '_').Replace(" ", "_").ToLower();
fullName = UniqueBundleName ? $"{packageName}_{bundleName}.{ext}" : $"{bundleName}.{ext}";
```

**【推断】** 对分块流式：
- chunk 的静态资源 → `PackTopDirectory`（一级子目录 = 一个 chunk）
- 公共材质/贴图 → 单独 collector + `PackDirectory`
- 每帧都在变的小资源 → `PackSeparately` 会产生大量小 bundle（增加文件数与校验开销），需权衡

**共享资源打包**（构建参数 `EnableSharePackRule`，`Editor/BundleBuilder/BuildParameters.cs:74`）：开启后，被多个 bundle 引用的资源会独立成 `share_<dir>.bundle`（`TaskGetBuildMap.cs:210-245`）。`SingleReferencedPackAlone`（`:80`，默认 `true`）控制"只被引用一次"的资源是否也独立打包。**【推断】** 大世界场景建议开启 `EnableSharePackRule`，避免公共地形材质被复制进每个 chunk bundle（否则包体和内存都会倍增）。

### 8.3 构建管线（`EBuildPipeline`）的区别与选择

**【代码事实】** 6 条管线（`Editor/BundleBuilder/EBuildPipeline.cs:7-38`）：

| 管线 | 枚举 | 任务序列 | 产出 | 用途 |
|---|---|---|---|---|
| **EditorSimulateBuildPipeline（ESBP）** | `EditorSimulateBuildPipeline` | Prepare → GetBuildMap → UpdateBundleInfo → CreateManifest | **只有清单，无 AB** | 编辑器模拟（见 8.4） |
| **LegacyBuildPipeline（LBP）** | `LegacyBuildPipeline` | 10 步（含 `TaskVerifyBuildResult_LBP`） | AB + 清单 | 旧 `BuildPipeline` API |
| **ScriptableBuildPipeline（SBP）** | `ScriptableBuildPipeline` | Prepare → GetBuildMap → Building → VerifyBuildResult → Encryption → UpdateBundleInfo → CreateManifest → CreateReport → CreatePackage → CopyBundledFiles → CreateCatalog（11 步） | AB + 清单 + catalog | **默认，推荐**（`BundleBuilderSetting.cs:20` 默认值） |
| **RawFileBuildPipeline（RFBP）** | `RawFileBuildPipeline` | 10 步（无 Verify） | 原始文件（不打包） | 视频/大二进制 |
| **InstantAssetBuildPipeline（IABP）** | `InstantAssetBuildPipeline` | 10 步 + Verify + CreateRecordFile | 团结引擎 InstantAsset | 团结引擎专用 |
| **ArchiveFileBuildPipeline（AFBP）** | `ArchiveFileBuildPipeline` | 10 步 | 单个归档文件 | 归档包（3.0.1 新增） |

证据：各 `*Pipeline.cs` 的 `GetDefaultBuildPipeline()`，例如 `ScriptableBuildPipeline.cs:29-45`、`EditorSimulateBuildPipeline.cs:32-41`、`RawFileBuildPipeline.cs:33-46`。

**选择建议【推断】**：

- **本项目（Android + 2022.3）→ `ScriptableBuildPipeline`**
  理由：① 它是 `BundleBuilderSetting.GetPackageBuildPipeline` 的默认值（`Editor/BundleBuilder/BundleBuilderSetting.cs:20`）② `TaskVerifyBuildResult_SBP` 能提前发现打包问题 ③ `TaskCreateCatalog_SBP` 生成 BuiltinCatalog（Builtin 文件系统初始化必需）④ `ScriptableBuildParameters` 提供 `CompressOption` / `DisableWriteTypeTree` / `StripUnityVersion` / `ReplaceAssetPathWithAddress` 等移动端关键开关

- **不要用 `LegacyBuildPipeline`**：Unity 已不推荐旧 AB 构建 API，且 3.0 里它不带 catalog。

- **`RawFileBuildPipeline` 用于视频**：`PackVideoFile` + RFBP，避免视频被塞进 AB（AB 对视频无压缩收益且加载需要额外内存）。

**压缩选项**（`Editor/BundleBuilder/ECompressOption.cs:7-23`、`ScriptableBuildParameters.cs:95-102`）：

| 值 | Unity 压缩 | 内存 | 加载速度 | 适用 |
|---|---|---|---|---|
| `Uncompressed` | `BuildCompression.Uncompressed` | 最低（无需解压） | 最快 | **大 world chunk**、频繁加载的 bundle |
| `LZ4` | `BuildCompression.LZ4` | 中（块索引，按需解压） | 快 | 通用（默认，`BundleBuilderSetting.cs:37-38`） |
| `LZMA` | `BuildCompression.LZMA` | 高（整体解压到内存） | 慢 | 首包压缩体积优先（**移动端不推荐**） |

**【推断】** 分块流式：**chunk bundle 用 `Uncompressed`**（避免每次进视野都解压），首包非频繁资源用 `LZ4`。这是本项目最重要的打包决策之一。代价是包体和下载体积变大（Android 的 APK 自带压缩，但远端下载不压缩）。

### 8.4 编辑器模拟模式（EditorSimulateMode）的原理

**【代码事实】** 关键问题："为什么不需要构建 AB？"

**① 构建阶段（ESBP）只产出清单**

`TaskBuilding_*` **不在** ESBP 的任务序列里（对比 `EditorSimulateBuildPipeline.cs:32-41` 与 `ScriptableBuildPipeline.cs:29-45`）。ESBP 只做 4 件事：

- `TaskPrepare_ESBP`（`:9-15`）：只校验参数
- `TaskGetBuildMap_ESBP`（`:10-21`）：收集资源 + 计算依赖（**不调引擎打包**）
- `TaskUpdateBundleInfo_ESBP`：bundle 的 hash/crc/size **全部是伪造的** —— `GetUnityHash` 返回固定 32 个 0（`:19-22`），`GetUnityCRC` 返回 0（`:23-26`），`GetBundleFileHash` 用"路径 + 最后写入时间 + 文件大小"算 MD5（`:28-32`），`GetBundleFileSize` = 该 bundle 内所有源文件的 size 之和（`:42-58`）
- `TaskCreateManifest_ESBP`：写清单（`:11-14`，且 `GetBundleDepends` 返回空数组，`:16-19` —— **模拟模式不写 bundle 间依赖**，因为不真的存在 bundle）

**【推断】** 所以 ESBP 产出的清单描述的是"逻辑上的 bundle 划分"，但每个 bundle 并没有对应的文件。

**② 运行阶段（EditorFileSystem）完全绕开字节层**

- `EditorBundleCache.IsCached` 在 `VirtualDownloadMode == false` 时**恒返回 true**（`EditorBundleCache.cs:143-149`）—— 意味着"永远命中缓存，永远不下载"
- `EditorBundleCache.LoadBundleAsync` 对 `VirtualAssetBundle` 返回 `VirtualAssetBundleHandle`（`:118-141`）
- `VirtualAssetBundleHandle.UnloadBundle()` 是**空实现**（`VirtualAssetBundleHandle.cs:17-19`）
- `VABHLoadAssetOperation` 直接用 `AssetDatabase.LoadAssetAtPath(path, type)` 加载（`Runtime/BundleHandle/Services/VirtualAssetBundleHandle/Operations/VABHLoadAssetOperation.cs:63-76`）

**所以整条链路是**：

```
LoadAssetAsync(location)
  → 查清单（真实清单，由 ESBP 生成）
  → BundleCache.IsCached 恒 true
  → VirtualAssetBundleHandle（空壳）
  → AssetDatabase.LoadAssetAtPath(AssetPath)   ← 直接从工程里读源文件
```

**代价与收益**：

| 方面 | 模拟模式 | 真实模式 |
|---|---|---|
| 改资源后生效 | **立即**（AssetDatabase 实时） | 必须重新构建 |
| 能否验证 AB 相关行为 | 不能（无压缩、无依赖、无内存布局） | 能 |
| 加载耗时 | 接近 0（同步 AssetDatabase） | 有真实 IO |
| 依赖关系 | `GetBundleDepends` 返回空（`TaskCreateManifest_ESBP.cs:16-19`），**清单里 bundle 依赖为空** | 真实依赖 |

**【推断】** 最后一行是关键陷阱：`TaskCreateManifest_ESBP.CreateManifestFile(false, true, false, ...)` 的第一个参数 `processBundleDepends = false`（`TaskCreateManifest_ESBP.cs:13`），意味着**模拟模式下 `PackageBundle.DependentBundleIDs` 是空的**。所以模拟模式**无法验证"依赖闭包是否正确"**。分块项目的依赖问题只能在真机/真实构建里发现。

**③ 如何触发模拟构建**

- 菜单：`YooAsset/Bundle Builder`（`Editor/BundleBuilder/BundleBuilderWindow.cs:16`）→ 选 `EditorSimulateBuildPipeline` → Build
- 代码：`EditorSimulateBuildInvoker.Build(packageName, (int)EBundleType.VirtualAssetBundle)`（`Runtime/PackageBuilder/EditorSimulateBuildInvoker.cs:15`，走反射调用 `YooAsset.Editor.BundleSimulateBuilder.SimulateBuild`，`Editor/BundleBuilder/BundleSimulateBuilder.cs:17`）
- `BundleSimulateBuilder` 里硬编码了 `PackageVersion = "Simulate"`（`:29`）与 `UseAssetDependencyDB = true`（`:33`）
- 输出目录：`{BuildOutputRoot}/{BuildTarget}/{PackageName}/Simulate`（`BuildParameters.cs:231-234`，`BuildOutputRoot` 默认 `{ProjectPath}/Bundles`，`Editor/BundleBuilder/BundleBuilderHelper.cs:18-22`）

**【推断】** 所以模拟模式的 `EditorFileSystemParameters` 必须指向上面的 `Simulate` 目录 —— 这与 §4.1 的 `GetSimulatePackageRoot()` 一致。

### 8.5 四个编辑器窗口

**【代码事实】**

| 窗口 | 菜单 | 用途 |
|---|---|---|
| Bundle Collector | `YooAsset/Bundle Collector`（priority 101） | 配置 Package/Group/Collector（`BundleCollectorWindow.cs:20`） |
| Bundle Builder | `YooAsset/Bundle Builder`（priority 102） | 选择管线 + 版本 + 压缩 + 加密，执行构建（`BundleBuilderWindow.cs:16`） |
| Bundle Reporter | `YooAsset/Bundle Reporter`（priority 103） | 查看构建报告（`BundleReporterWindow.cs:18`） |
| Bundle Debugger | `YooAsset/Bundle Debugger`（priority 104） | 运行时远程调试（`BundleDebuggerWindow.cs:18`） |

**【推断】** Bundle Reporter 是验证"依赖图是否有环 / 是否有 stray bundle"的主要工具。`TaskCreateManifest.ProcessBundleTags` 会对游离 bundle 打 warning（`Editor/BundleBuilder/BuildTasks/TaskCreateManifest.cs:283`），这是发现配置错误的第一信号。

---

## 9. 关键类型速查表

### 9.1 入口与门面

| 类型 | 位置 | 关键成员 |
|---|---|---|
| `YooAssets`（static） | `Runtime/YooAssets.cs` | `Initialize(ILogger)`、`Destroy()`、`CreatePackage(name, priority)`、`GetPackage`、`TryGetPackage(name, out pkg)`、`RemovePackage`、`StartOperation(CustomAsyncOperation)`、`SetAsyncOperationMaxTimeSlice(long)`、`IsInitialized` |
| `ResourcePackage` | `Runtime/ResourcePackage/ResourcePackage.cs` | `InitializePackageAsync(options)`、`DestroyPackageAsync()`、`RequestPackageVersionAsync()`、`LoadPackageManifestAsync(options)`、`LoadAssetAsync<T>(loc, priority)`、`LoadAssetSync<T>`、`LoadSubAssetsAsync`、`LoadAllAssetsAsync`、`LoadSceneAsync`、`LoadBundleFileAsync`、`UnloadUnusedAssetsAsync`、`UnloadAllAssetsAsync`、`TryUnloadUnusedAsset`、`CreateResourceDownloader`、`GetDownloadSize`、`PackageValid`、`InitializeStatus`、`PackagePriority` |

### 9.2 句柄

| 类型 | 位置 | 特有成员 |
|---|---|---|
| `HandleBase` | `Runtime/ResourceManager/Handles/HandleBase.cs` | `Release()`、`Dispose()`、`GetAssetInfo()`、`Status`、`Error`、`Progress`、`IsDone`、`IsValid`、`GetAwaiter()` |
| `AssetHandle` | `AssetHandle.cs` | `AssetObject`、`GetAssetObject<T>()`、`InstantiateSync/Async()`、`WaitForAsyncComplete()`、`Completed` |
| `SubAssetsHandle` | `SubAssetsHandle.cs` | `SubAssetObjects`、`GetSubAssetObject<T>()` |
| `AllAssetsHandle` | `AllAssetsHandle.cs` | `AllAssetObjects` |
| `SceneHandle` | `SceneHandle.cs` | `SceneName`、`SceneObject`、`ActivateScene()`、`AllowSceneActivation()`、`UnloadSceneAsync()`（`WaitForAsyncComplete` 是 **internal**，`:51`） |
| `BundleFileHandle` | `BundleFileHandle.cs` | 仅 `WaitForAsyncComplete()` + 基类成员 |

### 9.3 选项类型（3.0 全部是 struct/class，替代 2.x 的多参数重载）

| 类型 | 位置 | 关键字段 |
|---|---|---|
| `EditorSimulateModeOptions` | `InitializePackageOptions.cs:29` | `EditorFileSystemParameters` |
| `OfflinePlayModeOptions` | `:40` | `BuiltinFileSystemParameters` |
| `HostPlayModeOptions` | `:51` | `BuiltinFileSystemParameters`、`CacheFileSystemParameters` |
| `WebPlayModeOptions` | `:67` | `WebServerFileSystemParameters`、`WebNetworkFileSystemParameters` |
| `CustomPlayModeOptions` | `:83` | `FileSystemParameterList`（**最后一个为主 FS**） |
| `InitializePackageOptions`（基类） | `:8` | `BundleLoadingMaxConcurrency`、`AutoUnloadBundleWhenUnused`、`WebGLForceSyncLoadAsset` |
| `LoadPackageManifestOptions` | `LoadPackageManifestOptions.cs` | `PackageVersion`（**必填**）、`Timeout` |
| `RequestPackageVersionOptions` | `RequestPackageVersionOptions.cs` | `AppendTimeTicks`、`Timeout` |
| `ResourceDownloaderOptions` | `DownloaderOptions.cs:64` | `Tags`、`MaximumConcurrency`、`RetryCount` |
| `BundleDownloaderOptions` | `:7` | `AssetInfos`、`DownloadBundleDependencies`、`MaximumConcurrency`、`RetryCount` |
| `ResourceUnpackerOptions` | `:124` | `Tags`、`MaximumConcurrency`、`RetryCount` |
| `BundleImporterOptions` | `:184` | `BundleInfos`、`MaximumConcurrency`、`RetryCount` |
| `InstantiateOptions` | `InstantiateOptions.cs` | `IsActive`、`Parent`、`InWorldSpace`、`Position`、`Rotation` |
| `UnloadUnusedAssetsOptions` | `UnloadUnusedAssetsOptions.cs` | `MaxLoopCount` |
| `UnloadAllAssetsOptions` | `UnloadAllAssetsOptions.cs` | `ShouldReleaseHandles`、`ShouldLockLoading`、`ShouldWaitUnloadUnused` |
| `ClearCacheOptions` | `ClearCacheOptions.cs` | `ClearMethod`、`ClearParameter` |
| `EnsureBundleFileOptions` | `EnsureBundleFileOptions.cs` | `Location` 或 `AssetInfo` |

### 9.4 数据模型

| 类型 | 位置 | 说明 |
|---|---|---|
| `AssetInfo` | `Runtime/ResourcePackage/AssetInfo.cs` | 资源信息。`IsValid`（`:94`）、`AssetPath`、`Address`、`Tags`、`AssetType`；内部有 `LoadMethod`、`AssetKey` |
| `BundleInfo`（internal） | `BundleInfo.cs` | `Bundle`（`PackageBundle`）+ 所属 `IFileSystem`；`CreateBundleLoader` / `CreateBundleDownloader` |
| `PackageManifest`（internal） | `PackageManifest.cs` | 清单模型；`AssetsByLocation`（`:88`）、`BundlesByBundleName` / `ByFileName` / `ByGuid`（`:100-112`） |
| `PackageBundle`（internal） | `PackageBundle.cs` | `BundleName`、`FileHash`（兼作 `BundleGuid`，`:52-55`）、`FileCrc`、`FileSize`、`IsEncrypted`、`DependentBundleIDs`、`Tags`、`MainAssets`、`ReferrerBundleIDs` |
| `PackageAsset`（internal） | `PackageAsset.cs` | `Address`、`AssetPath`、`AssetGuid`、`BundleID`、`DependentBundleIDs`、`Tags` |
| `PackageTags` | `PackageTags.cs` | `IsTagged`、`TagCount`、`HasTag`、`HasAnyTag` |
| `PackageDetails` | `PackageDetails.cs` | 清单的只读投影；`AssetTotalCount`、`BundleTotalCount`、`BuildPipeline` 等 |

### 9.5 枚举

| 枚举 | 位置 | 值 |
|---|---|---|
| `EPlayMode` | `ResourcePackage/EPlayMode.cs:7` | `None`、`EditorSimulateMode`、`OfflinePlayMode`、`HostPlayMode`、`WebPlayMode`、`CustomPlayMode` |
| `EOperationStatus` | `AsyncOperation/EOperationStatus.cs:7` | `None`、`Processing`、`Succeeded`、`Failed`（+ LEGACY `Succeed` 别名） |
| `EBundleType` | `BundleHandle/EBundleType.cs:8` | `None=0`、`AssetBundle=2`、`RawBundle=3`、`ArchiveBundle=4`、`InstantBundle=5`、`VirtualAssetBundle=12`、`VirtualRawBundle=13`、`VirtualArchiveBundle=14` |
| `EFileVerifyLevel` | `SandboxBundleCache/EFileVerifyLevel.cs:7` | `Low=1`、`Middle=2`、`High=3` |
| `EFileNameStyle` | `ResourcePackage/EFileNameStyle.cs:7` | `HashName=0`、`BundleName=1`、`BundleName_HashName=2` |
| `EInstallCleanupMode` | `SandboxFileSystem/EInstallCleanupMode.cs:7` | `None=0`、`ClearAllCacheFiles=1`、`ClearAllBundleFiles=2`、`ClearAllManifestFiles=3` |
| `EFileSystemParameter` | `FileSystem/EFileSystemParameter.cs:7` | 31 个参数名（见 9.6） |
| `ECollectorType` | `Editor/BundleCollector/ECollectorType.cs:9` | `MainAssetCollector`、`StaticAssetCollector`、`DependAssetCollector`、`None` |
| `ECompressOption` | `Editor/BundleBuilder/ECompressOption.cs:7` | `Uncompressed=0`、`LZMA`、`LZ4` |
| `EBuildPipeline` | `Editor/BundleBuilder/EBuildPipeline.cs:7` | `EditorSimulateBuildPipeline`、`LegacyBuildPipeline`、`ScriptableBuildPipeline`、`RawFileBuildPipeline`、`InstantAssetBuildPipeline`、`ArchiveFileBuildPipeline` |
| `EBundledCopyOption` | `Editor/BundleBuilder/EBundledCopyOption.cs:7` | `None`、`ClearAndCopyAll`、`ClearAndCopyByTags`、`OnlyCopyAll`、`OnlyCopyByTags` |
| `EDownloadRequestStatus`（internal） | `DownloadSystem/EDownloadRequestStatus.cs` | `None`、`Running`、`Aborting`、`Succeeded`、`Failed` |

### 9.6 `EFileSystemParameter` 全表（含默认值与钳制）

**【代码事实】** 枚举定义在 `Runtime/FileSystem/EFileSystemParameter.cs:7-163`；默认值与钳制在对应文件系统的 `SetParameter` 里。

| 参数 | 类型 | 默认 | 钳制 | 生效文件系统 | 证据 |
|---|---|---|---|---|---|
| `FileVerifyLevel` | `EFileVerifyLevel` | `Low` | — | Sandbox, Builtin | `SandboxFileSystem.cs:70` |
| `FileVerifyMaxConcurrency` | `int` | `8` | `[1,32]` | Sandbox, Builtin | `:78`、`:242` |
| `InstallCleanupMode` | `EInstallCleanupMode` | `None` | 枚举 | Sandbox, Builtin | `:65` |
| `DisableUnityWebCache` | `bool` | `false` | — | Web | `WebServerFileSystem` |
| `UnityWebRequestCreator` | `UnityWebRequestCreator` | `null` | — | 全部 | `SandboxFileSystem.cs:221-224` |
| `DownloadDisableOndemand` | `bool` | `false` | — | Sandbox | `:83` |
| `DownloadBackend` | `IDownloadBackend` | `UnityWebRequestBackend` | — | 全部 | `:217-220`、`:332` |
| `DownloadMaxConcurrency` | `int` | `8` | `[1,32]` | Sandbox | `:91`、`:257` |
| `DownloadMaxRequestPerFrame` | `int` | `8` | `[1,32]` | Sandbox | `:99`、`:268` ★ **枚举名少个 s** |
| `DownloadWatchdogTimeout` | `int` | `0` | `>=0` | Sandbox | `:104`、`:273` |
| `DownloadResumeMinimumSize` | `long` | `long.MaxValue` | — | Sandbox | `:109`、`:277` |
| `VirtualWebglMode` | `bool` | `false` | — | Editor | `EditorFileSystem.cs:43` |
| `VirtualDownloadMode` | `bool` | `false` | — | Editor | `:48` |
| `VirtualDownloadSpeed` | `int` | `1024` | `>=1` | Editor | `:56`、`:175` |
| `AsyncSimulateMinFrame` | `int` | `1` | 与 Max 联动 | Editor | `:80`、`:227-228` |
| `AsyncSimulateMaxFrame` | `int` | `1` | 与 Min 联动 | Editor | `:88` |
| `CopyBuiltinPackageManifest` | `bool` | `false` | — | Builtin | `BuiltinFileSystem.cs:93` |
| `CopyBuiltinPackageManifestDestRoot` | `string` | `null` | — | Builtin | `:101` |
| `UnpackFileSystemRoot` | `string` | 默认缓存根 | — | Builtin | `:106`、`:331-334` |
| `RemoteService` | `IRemoteService` | `null`（Sandbox 必需） | — | Sandbox | `SandboxFileSystem.cs:114`；未设时初始化失败（`SFSInitializeOperation.cs:50-55`） |
| `AssetBundleDecryptor` | `IBundleDecryptor` | `null` | — | 全部 | `SandboxFileSystem.cs:119` |
| `RawBundleDecryptor` | `IBundleDecryptor` | `null` | — | 全部 | `:124` |
| `ArchiveBundleDecryptor` | `IBundleDecryptor` | `null` | — | 全部 | `:129` |
| `AssetBundleFallbackDecryptor` | `IBundleMemoryDecryptor` | `null` | — | Sandbox, Builtin | `:134` |
| `ManifestDecryptor` | `IManifestDecryptor` | `null` | — | 全部 | `:139` |
| `DownloadRetryPolicy` | `IDownloadRetryPolicy` | `DefaultDownloadRetryPolicy` | — | Sandbox | `:144`、`:336` |
| `DownloadUrlPolicy` | `IDownloadUrlPolicy` | `DefaultDownloadUrlPolicy` | — | Sandbox | `:149`、`:340` |
| `WebPlatformStrategy` | `IWebPlatformStrategy` | `DefaultWebPlatformStrategy` | — | Web | `WebNetworkFileSystem` |
| `WebPreloadStrategy` | `IWebPreloadStrategy` | `null` | — | Web | `WebNetworkFileSystem` |
| `BundleUnpackPolicy` | `IBundleUnpackPolicy` | `DefaultBundleUnpackPolicy` | — | Builtin | `BuiltinFileSystem.cs:152`、`:341-342` |
| `BuiltinFileAccessor` | `IBuiltinFileAccessor` | `null` | — | Builtin | `:157` |

### 9.7 异常类型

**【代码事实】** 全部继承 `YooException`（`Runtime/Utility/YooException.cs`）：

| 类型 | 抛出场景 |
|---|---|
| `YooInternalException` | 内部逻辑错误（引用计数异常、字典键缺失、类型不符）。`ProviderBase.cs:302`、`LoadBundleOperation.cs:190`、`:193` |
| `YooPackageInvalidException` | 包裹未初始化 / 初始化中 / 初始化失败 / 无清单（`ResourcePackage.cs:1009-1023`） |
| `YooManifestInvalidException` | 清单配置冲突（地址重复、GUID 重复、tag 索引越界、Addresable 与 LocationToLower 冲突）`DeserializeManifestOperation.cs:123`、`:125` |
| `YooHandleInvalidException` | 操作已释放的句柄时（仅 `Completed` 事件注册会抛，`AssetHandle.cs:28`） |

**注意**：`handle.Release()` 二次调用**不抛**（`HandleBase.cs:30` 提前返回，只 warning）。这与"句柄失效后注册事件"抛异常是两回事。

---

## 附录 A：一页纸的"改哪里"索引

| 我想…… | 改哪里 |
|---|---|
| 控制单帧异步开销 | `YooAssets.SetAsyncOperationMaxTimeSlice(10~12)`（`YooAssets.cs:227`；<10 会被钳制为 10） |
| 控制同时加载的 bundle 数 | `options.BundleLoadingMaxConcurrency = 2~4`（`InitializePackageOptions.cs:13`） |
| 让 chunk 卸载后立刻释放内存 | 保持 `AutoUnloadBundleWhenUnused = false`，周期性 `UnloadUnusedAssetsAsync()`（§5.4） |
| 限制下载并发/频率 | `DownloadMaxConcurrency=4~6`、`DownloadMaxRequestPerFrame=2~4`（`SandboxFileSystem.cs:91`、`:99`） |
| 解决弱网下载挂死 | `DownloadWatchdogTimeout=8~15`（`SandboxFileSystem.cs:104`） |
| 启用断点续传 | `DownloadResumeMinimumSize=1MB`（`SandboxFileSystem.cs:109`） |
| 加快缓存校验 | `FileVerifyLevel=Low`（`SandboxFileSystem.cs:70`） |
| 修覆盖安装缓存残留 | `InstallCleanupMode=ClearAllCacheFiles`（`SandboxFileSystem.cs:65`） |
| 让缺失 bundle 立刻报错（而非无限重试） | `DownloadDisableOndemand=true`（`SandboxFileSystem.cs:83`） |
| 减小清单内存 | 关闭 `IncludeAssetGUID`；SBP 开 `ReplaceAssetPathWithAddress`（`ScriptableBuildParameters.cs:40`） |
| 减小 bundle 加载内存 | `CompressOption=Uncompressed`（chunk）或 `LZ4`（通用）（`ScriptableBuildParameters.cs:18`） |
| 避免公共资源被复制进每个 chunk | 开 `EnableSharePackRule`（`BuildParameters.cs:74`） |
| 验证依赖图/体量 | Bundle Reporter 窗口（`YooAsset/Bundle Reporter`） |
| 排查运行时泄漏 | Bundle Debugger 窗口（`YooAsset/Bundle Debugger`）；诊断数据由 `YooAssets.GetDiagnosticReport`（**internal**，`YooAssets.cs:261`）生成，业务层只能通过 Debugger 窗口或 `[assembly: InternalsVisibleTo("YooAsset.Custom")]` 访问 |

## 附录 B：证据索引（本文引用的关键文件）

- 入口：`Runtime/YooAssets.cs`、`Runtime/YooAssetsDriver.cs`
- 门面：`Runtime/ResourcePackage/ResourcePackage.cs`（1052 行）、`FileSystemHost.cs`（571 行）
- Provider/Handle：`Runtime/ResourceManager/ResourceManager.cs`（701 行）、`Providers/ProviderBase.cs`（406 行）、`Handles/HandleBase.cs`（168 行）、`Operations/Internal/LoadBundleOperation.cs`（327 行）
- 调度：`Runtime/AsyncOperation/AsyncOperationSystem.cs`（298 行）、`AsyncOperationScheduler.cs`（187 行）、`AsyncOperationBase.cs`（734 行）、`OperationAwaiter.cs`
- 缓存：`Runtime/BundleCache/Services/SandboxBundleCache/`（含 `VerifyCacheFilesTask.cs`、`VerifyCacheFilesOperation.cs`、`SBCLoadAssetBundleOperation.cs`、`SBCWriteCacheOperation.cs`）
- 文件系统：`Runtime/FileSystem/Services/SandboxFileSystem/`、`BuiltinFileSystem/`、`EditorFileSystem/`、`FileSystemParameters.cs`、`EFileSystemParameter.cs`
- 下载：`Runtime/DownloadSystem/Operations/DownloadSchedulerOperation.cs`、`DownloadRetryController.cs`、`Services/UnityWebRequest/UnityWebRequestBase.cs`
- 清单：`Runtime/ResourcePackage/PackageManifest.cs`、`PackageBundle.cs`、`PackageAsset.cs`、`Operations/Internal/DeserializeManifestOperation.cs`
- 编辑器：`Editor/BundleCollector/`、`Editor/BundleBuilder/BuildParameters.cs`、`Editor/BundleBuilder/BuildPipeline/*/`、`Editor/BundleBuilder/BuildTasks/TaskCreateManifest.cs`
