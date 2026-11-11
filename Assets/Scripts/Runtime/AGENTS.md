# Assets/Scripts/Runtime 规约

> 三个子目录，**两套程序集**：`Resources/`（`FishDemo.Resources`）、
> `Rendering/`（`FishDemo.Rendering`）、`Gameplay/`（**无 asmdef**，编进 `Assembly-CSharp`）。
> 本文件约 79 行，建议一次读完。

## 1. 目录与程序集

| 目录 | 程序集 | 命名空间 | 说明 |
|---|---|---|---|
| `Resources/` | `FishDemo.Resources` | `FishDemo.AssetManagement` | YooAsset 初始化 + 句柄封装 |
| `Rendering/` | `FishDemo.Rendering` | `FishDemo.Rendering` | 草 BRG 运行时，`allowUnsafeCode=true` |
| `Gameplay/` | **无** → `Assembly-CSharp` | `FishDemo.Gameplay` | Pegasus 巡游演示控制器 |

**`Gameplay/` 不能加 asmdef**：`PegasusTourController.cs` 直接 `using Pegasus`，
Pegasus 没有 asmdef、编在 `Assembly-CSharp`。asmdef 程序集引用不到默认程序集。
同 `Assets/GTSRuntime/` 的处理方式。

`Streaming/` 仍**不存在**。

## 2. Resources（YooAsset）：已实现，仍未接线

| 文件 | 状态 |
|---|---|
| `Resources/YooAssetInitializer.cs` | 初始化三模式（编辑器模拟 / 离线 / 联机）、清单加载、下载参数 |
| `Resources/AssetLoader.cs` | 句柄获取/释放，返回 `AssetLease<T>`（资源 + 句柄成对） |

**全工程无调用方**：`InitializeAsync()` 没有被任何脚本调用；实际资源加载走 SECTR 的 `SceneManager.LoadSceneAsync`。

接线的前置条件（缺一不可）：

1. 菜单 `YooAsset/FishDemo/初始化资源收集配置` —— 生成 `Assets/BundleCollectorSetting.asset`。
2. 菜单 `YooAsset/FishDemo/执行模拟构建并注入路径` —— 否则 `EditorSimulatePackageRoot` 为空，初始化会抛 `InvalidOperationException`。
3. 调用方必须在进入依赖资源的场景**之前** `await YooAssetInitializer.InitializeAsync()`。

**地址规则必须是 `AddressByFolderAndFileName`**（`YooAssetSetup.cs:96`）。
`Assets/Scenes` 下场景与烘焙数据同名（`World/Chunks/*Sector.unity` 与 `World/GrassData/*Sector.bytes`），
纯文件名规则会让两者撞 Address，收集阶段直接抛 `Address already exists`（`BundleCollector.cs:226`）。

## 3. Rendering（草 BRG）

| 文件 | 职责 |
|---|---|
| `Rendering/GrassBRGManager.cs` | 实例构建、32m 空间分块、多 batch（16MB/批）、Burst 剔除 Job、3 级距离 LOD |
| `Rendering/GrassInstance.cs` | 32B 磁盘格式 + `GrassChunkData.Read`（魔数/版本/大小校验） |

**它属于 Render Data 层**，可以碰 `BatchRendererGroup` / `GraphicsBuffer`；
Gameplay 层不得直接操作 `CommandBuffer` / `Graphics.*`（[rendering_rules](../../../Docs/rendering_rules.md) §1）。

三条最容易踩的实现约束（细节见 [grass_brg_progress](../../../Docs/grass_brg_progress.md) §5）：

1. `AddBatch` 只接受 **raw buffer**（`Target.Raw` + stride 4 + count = 字节数/4），`Structured` 直接失败。
2. 单个 batch 的 raw buffer 上限 16MB → **32B/实例 = 524,288 实例/批**。超限表现是「全黑且无任何报错」。
3. `OnPerformCulling` 的输出数组必须 `UnsafeUtility.Malloc(..., Allocator.TempJob)`，由引擎释放。
   Job 内**不能** `Dispose()` native container，字段要加 `[DeallocateOnJobCompletion]`。

**数据加载是临时通路**：`TryAutoLoadChunkData()` 用 `System.IO.File.ReadAllBytes` 直读
`Assets/Scenes/World/GrassData/*.bytes`（编辑器/开发期）。打包后 `Assets/` 不存在，
会回退到 M1 合成方阵。正式版必须改成经 YooAsset。

## 4. 必须遵守的约束

详细依据见 [YooAsset 模块规约](../../../Packages/com.tuyoogame.yooasset/AGENTS.md)，这里只列最容易犯的：

- **禁止 `Resources.Load`**，也禁止业务层直接调 `Package.LoadAssetAsync`，必须经 `AssetLoader`。
- **句柄成对释放**：`AssetLoader.LoadAsync` 返回 `AssetLease<T>`，用完 `Release(location, handle)`。禁止复用同一句柄、禁止重复 `Release`。
- **清单必须先取版本号**：`RequestPackageVersionAsync()` 之后才能 `LoadPackageManifestAsync`，离线也一样。
- **`Shutdown()` 前必须 `ReleaseAll()`**：`Destroy()` 走 `AssetBundle.Unload(true)`，不检查引用计数。

## 5. 相关文档

| 文档 | 何时读 |
|---|---|
| [YooAsset 模块规约](../../../Packages/com.tuyoogame.yooasset/AGENTS.md) | 改资源层代码前（必读） |
| [grass_brg_progress](../../../Docs/grass_brg_progress.md) | 改草 BRG 运行时、排查「草消失」 |
| [grass_brg_plan](../../../Docs/grass_brg_plan.md) | 要理解 BRG 数据/file 格式的设计取舍 |
| [yooasset_practices](../../../Docs/yooasset_practices.md) | 遇到初始化失败、句柄失效、卸载不掉、要查参数依据 |
| [yooasset_3.0.7](../../../Docs/yooasset_3.0.7.md) | 要理解内部链路；改包源码 |
| [pooling_lod](../../../Docs/pooling_lod.md) | 设计缓存、池、chunk 加载策略 |
