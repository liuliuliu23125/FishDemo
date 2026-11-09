# 资源加载、缓存与对象池策略

> 目标环境：Unity 2022.3.62f2 + URP 14.0.12，Android 旗舰机，30fps

## 结论速览

| 层 | 方案 | 说明 |
|---|---|---|
| Asset 层（chunk 资源） | **YooAsset + 延迟卸载 + LRU** | 核心策略 |
| GPU 资源层（Mesh / Buffer） | **对象池复用，不 Destroy** | 比 GameObject 池更关键 |
| GameObject 层（小物件） | `UnityEngine.Pool`（暂不引入 PoolManager） | 按需 |
| 植被 | GraphicsBuffer 池，无 GameObject | BRG/instancing |

**核心原则：反复加载用「缓存」解决，不用「池」解决。**

## 1. 为什么区分"频繁创建销毁"与"反复加载"

这两件事成本来源完全不同，用错方案会白费力气。

| 场景 | 成本来源 | 正确方案 |
|---|---|---|
| **频繁创建销毁**（鱼、水花、浮标、特效） | `Instantiate` CPU 开销 + `Destroy` 的 GC 压力 | **对象池** |
| **反复加载**（玩家在 chunk 边界来回走） | Bundle IO + 反序列化 + 显存分配 | **缓存 + 延迟卸载** |

对象池解决不了反复加载，因为 `LoadAssetAsync` 的开销在 IO 和解序列化，无法预先"泡"在池里。

## 2. 三层资源模型

"反复加载"在 Unity 里实际分三层，每层解法不同：

| 层 | 反复加载的真实成本 | 解法 |
|---|---|---|
| **Asset 层**（YooAsset） | Bundle IO + 反序列化 | 引用计数 + LRU + 延迟卸载 |
| **GPU 资源层**（Mesh / GraphicsBuffer） | 显存分配 + 顶点数据上传 | 池化复用 buffer，只改数据 |
| **GameObject 层** | Instantiate / Destroy | GameObject 池 |

## 3. Asset 层：延迟卸载 + LRU（核心）

### 3.1 延迟卸载

玩家离开 chunk 后**不立即释放**，等 N 秒或走出更大范围才释放。

SECTR 的 `RegionLoader` 就是这个设计（`SECTR_RegionLoader.cs:21-22`）：

```csharp
public Vector3 LoadSize = new Vector3(20f, 10f, 20f);
// 卸载距离 = LoadSize 的百分比，用于防抖
```

有独立的卸载滞后阈值，避免玩家在边界反复横跳导致频繁加载卸载。

**本项目参数建议**（初始，需实测校准）：

| 参数 | 建议值 | 说明 |
|---|---|---|
| 加载范围 | 相机前方 + 两侧，半径 2.5 个 chunk | |
| 卸载滞后 | 超出加载范围 1.5 个 chunk 才卸载 | 防边界抖动 |
| 延迟时间 | 离开后 2~3 秒 | 应对短暂回头 |

### 3.2 LRU 缓存

保留最近 K 个 chunk 的资源引用，命中就不重新加载。

```
缓存容量 K = max(当前可见 chunk 数 × 1.5, 最小值)
超容量时淘汰最久未使用的
```

**注意**：LRU 淘汰的是 **Asset 引用**（`AssetHandle.Release`），不是 GameObject。两者生命周期分开管理。

### 3.3 预加载

按移动方向预加载前方 chunk，消除"走到才加载"的卡顿。

```
if (velocity.magnitude > threshold)
    preload(neighborChunksInDirection(velocity));
```

### 3.4 分帧与预算

- 加载必须在预算内（≤ 3ms/帧，30fps 下可放宽到 5ms）。
- 使用分帧 + 异步上传。
- 每帧最多激活 N 个 chunk（参考 SECTR 的 `activationQueue` 节流思路）。

## 4. GPU 资源层：Mesh / Buffer 池

**这一层最容易被忽略，但往往比 GameObject 池更贵。**

### 4.1 Mesh

`new Mesh()` + `SetVertices` 会触发显存分配和上传，**比 Instantiate 贵得多**。

**规则**：chunk 卸载时**不要 Destroy Mesh**，应池化复用：

```csharp
// 卸载
meshPool.Return(chunk.mesh);   // 不是 Destroy(chunk.mesh)

// 加载
var mesh = meshPool.Rent(vertexCount);
mesh.SetVertices(...);          // 只更新数据，不重新分配
```

**要求**：池按顶点数分桶（如 1k / 4k / 16k），减少重新分配。

### 4.2 GraphicsBuffer（植被 BRG）

- 按**最大容量**预分配，chunk 切换时只改内容和 count。
- 不要为每个 chunk 新建 buffer。

## 5. GameObject 层

### 5.1 地形 chunk root

chunk 的 GameObject 可以池化复用（结构一致，只换 mesh 和碰撞体）。

### 5.2 小物件：暂不引入 PoolManager

**结论：暂缓引入 PoolManager。**

理由：
1. Unity 2021.2+ 已内置 `UnityEngine.Pool.ObjectPool<T>`，够用。
2. PoolManager 的 `Dictionary<string, PrefabPool>` 按字符串索引，有额外开销。
3. 目前没有明确的"高频 Spawn/Destroy"场景清单。

**何时引入**：当确认鱼群、水花、浮标等场景的内置池不够用（如需要 Inspector 配置、prewarm、自动 culling）时。

**明确不适用**：
- ❌ 地形 chunk（需要 YooAsset 管 Asset 生命周期，池只管 GameObject）
- ❌ 植被（走 GPU instancing/BRG，没有 GameObject）

## 6. 本项目的完整分工

```
鱼群 / 水花 / 浮标 / 钓具      → UnityEngine.Pool（GameObject 池）
地形 chunk 的 Mesh            → 自建 Mesh 池（复用，不 Destroy）
地形 chunk 的 GameObject      → 对象池（chunk root 复用）
Chunk 的 Asset 加载           → YooAsset + LRU + 延迟卸载
植被（BRG/instancing）        → GraphicsBuffer 池，无 GameObject
```

## 7. 与 SECTR 的关系

SECTR 已经实现了「区域判定 + 异步加载 + 激活队列节流 + 卸载滞后」。

**建议**：
- 如果地形 chunk 用 SECTR 管理，直接复用它的 `RegionLoader` 滞后逻辑。
- 但 SECTR 是 **scene 粒度**，且跨 scene 不合批。**地形 mesh 分块建议自己做 YooAsset**，SECTR 只用于区域级内容切换。

## 8. YooAsset 接入（已实施）

### 8.1 安装（嵌入式）

YooAsset 3.0.7 已作为**嵌入式包**放在 `Packages/com.tuyoogame.yooasset/`，**不在 `manifest.json` 中声明依赖**（Unity 自动识别 `Packages/` 下的包目录）。

选择嵌入而非 registry 依赖的原因：
- 升级可控，版本锁定在仓库内
- OpenUPM 不可达时工程仍能打开
- 源码可查可改（代价是改动需标注为 fork）

包体 4.3MB（已排除 `Samples~` 的 29MB）。版本依据：OpenUPM `dist-tags.latest` = 3.0.7，`unity` 字段 = `2019.4`，兼容当前 2022.3.62f2。

**模块规约**见 [Packages/com.tuyoogame.yooasset/AGENTS.md](../Packages/com.tuyoogame.yooasset/AGENTS.md)，架构全貌见 [yooasset_3.0.7.md](yooasset_3.0.7.md)。

### 8.2 编辑器配置

**3.0.7 的 `YooAssetSettings` 会自动创建**（`SettingLoader.cs:26` 逻辑：找不到就 `CreateAsset("Assets/YooAssetSettings.asset")`）。官方文档说的"右键创建并放入 Resources"是旧版本流程，**以代码为准**。

菜单入口（`MenuItem` 特性确认）：
- `YooAsset/Bundle Collector` — 资源配置
- `YooAsset/Bundle Builder` — 构建
- `YooAsset/Bundle Reporter` / `Bundle Debugger` — 分析调试

项目已提供一键初始化：**`YooAsset/FishDemo/初始化资源收集配置`**（`Assets/Scripts/Editor/YooAssetSetup.cs`），会创建包 `DefaultPackage` 和 6 个分组（Scene/Terrain/Vegetation/Character/Water/UI）。

### 8.3 版本选择：3.0.7 vs 2.3.19

完整对比见 `E:/pkg-inspect/notes/yooasset_version_compare.md`（807 行，含逐项代码证据）。结论摘要：

**保留 3.0.7**，核心理由是默认下载调度参数：

| 参数 | 2.3.19 | 3.0.7 |
|---|---|---|
| `DownloadMaxConcurrency` | `int.MaxValue`（无限制） | `8`，钳制 1..32 |
| `DownloadMaxRequestPerFrame` | `int.MaxValue` | `8`，钳制 1..32 |
| 缓存校验 | 依赖主线程逐帧提交 | 后台线程连续处理（官方称 30fps 下 42.91s→5.35s） |

证据：`yoo2/.../DefaultCacheFileSystem.cs:92,97` vs `yoo/.../SandboxFileSystem.cs:91,99`。

大世界进区块时可能同时请求 20~50 个 chunk，2.3.19 的"并发无上限"是结构性隐患。

**但必须遵守三条约束**：

1. **锁定精确版本 `3.0.7`**，不要用 `3.0.x` 范围。3.0.x 半年内 4 次破坏性变更，其中 `SetFileSystemInstanceId` 3.0.4 加入、6 天后 3.0.5 撤回。
2. **显式配置下载参数**，不要依赖默认值（见 8.4 代码）。
3. **不做友元程序集级扩展**（`IDownloadBackend` / `CustomAsyncOperation` / 自定义 `IFileSystem`），这三块正是版本变动最频繁的区域。

**不建议回退 2.3.19**：迁移成本约 12 个 API 点 + 全部 `await` 重写（2.3.19 的 `AsyncOperationBase` **没有 `GetAwaiter()`**）+ 所有 bundle 重建。若 3.0.7 出问题，应降到 3.0.6 而非跨大版本回退。

### 8.4 运行时 API（3.0.7 实际签名）

```csharp
// 初始化
YooAssets.Initialize();
var package = YooAssets.CreatePackage("DefaultPackage");

// 3.0.7 用 Options 对象，不是旧版的 Parameters
var cacheParams = FileSystemParameters.CreateDefaultSandboxFileSystemParameters(remoteService);
// 枚举键是 DownloadMaxRequestPerFrame（无 s），属性名才是 DownloadMaxRequestsPerFrame
cacheParams.AddParameter(EFileSystemParameter.DownloadMaxConcurrency, 6);
cacheParams.AddParameter(EFileSystemParameter.DownloadMaxRequestPerFrame, 4);

var options = new HostPlayModeOptions {
    BuiltinFileSystemParameters = FileSystemParameters.CreateDefaultBuiltinFileSystemParameters(),
    CacheFileSystemParameters = cacheParams
};
await package.InitializePackageAsync(options);

// 清单：必须先拿到非空版本号（离线模式同样如此）
var versionOp = await package.RequestPackageVersionAsync();
var manifestOp = await package.LoadPackageManifestAsync(
    new LoadPackageManifestOptions(versionOp.PackageVersion, 60));

// 加载：每次 Load 都返回新句柄，调用方必须配对 Release
var handle = package.LoadAssetAsync<GameObject>("location");
await handle;
var asset = handle.GetAssetObject<GameObject>();
handle.Release();
```

**关键差异（3.0.7 vs 2.x）**：

| 项 | 2.x | 3.0.7 |
|---|---|---|
| 初始化参数 | `InitializePackageParameters` | `InitializePackageOptions` |
| 清单更新 | `UpdatePackageManifestAsync(version)` | `LoadPackageManifestAsync(LoadPackageManifestOptions)` |
| 状态枚举 | `EOperationStatus.Succeed` | `EOperationStatus.Succeeded`（`Succeed` 仅在 `YOOASSET_LEGACY_API` 宏下作为 `[Obsolete]` 别名） |
| 取包 | `GetPackage(name)` 返回包 | `TryGetPackage(name, out package)` 返回 bool |
| await | **无 `GetAwaiter()`** | 自带，可直接 `await`，不需要 UniTask |

**三条硬约束（代码事实）**：

1. `LoadPackageManifestOperation` 在 `PackageVersion` 为空时直接 `SetError`（`LoadPackageManifestOperation.cs:35-42`）。**不存在"传 null 加载内置清单"的用法**，离线也必须先 `RequestPackageVersionAsync()`。
2. `YooAssets.SetAsyncOperationMaxTimeSlice()` 有 `MinTimeSlice = 10` 的钳制（`AsyncOperationSystem.cs:24,53-57`），**低于 10ms 会被强制改成 10ms 并打 warning**。
3. 每次 `LoadAssetAsync` 都会 `CreateHandle` 并让 `RefCount++`（`ProviderBase.cs:288-291`）。**不能在业务层缓存并复用同一 handle**，否则重复 Release 会命中 `ProviderBase.cs:302` 的 throw。

### 8.5 项目代码

| 文件 | 职责 |
|---|---|
| `Assets/Scripts/Runtime/Resources/YooAssetInitializer.cs` | 初始化、清单加载、运行模式切换、`IRemoteService` 实现 |
| `Assets/Scripts/Runtime/Resources/AssetLoader.cs` | 句柄获取/释放，返回 `AssetLease<T>`（资源 + 句柄） |
| `Assets/Scripts/Editor/YooAssetSetup.cs` | 一键生成 Collector 配置 |
| `Assets/Scripts/Editor/YooAssetEditorBootstrap.cs` | 编辑器侧执行模拟构建，注入 `EditorSimulatePackageRoot` |

**`AssetLease<T>` 的必要性**：YooAsset 只有句柄能释放资源。若封装层只返回资源对象，调用方拿不到句柄就永远卸不掉。因此 `LoadAsync<T>` / `InstantiateAsync` 必须同时交出 `Asset` 和 `Handle`。

**验证状态（2026-11-09 已实测）**：

| 项 | 结果 |
|---|---|
| `FishDemo.Resources.dll` / `FishDemo.Editor.dll` 编译 | ✅ 0 error |
| 嵌入包生效（`packages-lock.json`） | ✅ `"source": "embedded"` |
| 菜单模拟构建产出 | ⏳ 未测（需在编辑器内手动触发） |
| 运行时初始化 | ⏳ 未测（尚无 Bootstrap 入口调用 `InitializeAsync`） |

**已知的命名陷阱**：程序集名与命名空间不能同名。此前 `FishDemo.Resources` 既是 asmdef 名又是 namespace，导致 C# 把 `FishDemo.Resources` 优先解析为程序集，`YooAssetInitializer` 无法解析。现已改为 namespace `FishDemo.AssetManagement`。

## 待验证项

- chunk 加载的实际耗时分布（IO vs 反序列化 vs 显存上传）
- Mesh 池分桶的粒度
- LRU 容量 K 的最优值（内存 vs 命中率）
- 延迟卸载时长对内存峰值的影响
- YooAsset 首次构建产物的加载性能（EditorSimulateMode 与真机差异）
