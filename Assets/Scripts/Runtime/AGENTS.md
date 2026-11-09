# Assets/Scripts/Runtime 规约

> 程序集 `FishDemo.Resources`，命名空间 `FishDemo.AssetManagement`。
> 本文件约 46 行，建议一次读完。

## 1. 现状：已实现，未接线

| 文件 | 状态 |
|---|---|
| `Resources/YooAssetInitializer.cs` | 初始化三模式（编辑器模拟 / 离线 / 联机）、清单加载、下载参数 |
| `Resources/AssetLoader.cs` | 句柄获取/释放，返回 `AssetLease<T>`（资源 + 句柄成对） |

**全工程无调用方**：`InitializeAsync()` 没有被任何脚本调用；实际资源加载走 SECTR 的 `SceneManager.LoadSceneAsync`。

接线的前置条件（缺一不可）：

1. 菜单 `YooAsset/FishDemo/初始化资源收集配置` —— 生成 `Assets/BundleCollectorSetting.asset`。
2. 菜单 `YooAsset/FishDemo/执行模拟构建并注入路径` —— 否则 `EditorSimulatePackageRoot` 为空，初始化会抛 `InvalidOperationException`。
3. 调用方必须在进入依赖资源的场景**之前** `await YooAssetInitializer.InitializeAsync()`。

## 2. 必须遵守的约束

详细依据见 [YooAsset 模块规约](../../../Packages/com.tuyoogame.yooasset/AGENTS.md)，这里只列最容易犯的：

- **禁止 `Resources.Load`**，也禁止业务层直接调 `Package.LoadAssetAsync`，必须经 `AssetLoader`。
- **句柄成对释放**：`AssetLoader.LoadAsync` 返回 `AssetLease<T>`，用完 `Release(location, handle)`。禁止复用同一句柄、禁止重复 `Release`。
- **清单必须先取版本号**：`RequestPackageVersionAsync()` 之后才能 `LoadPackageManifestAsync`，离线也一样。
- **`Shutdown()` 前必须 `ReleaseAll()`**：`Destroy()` 走 `AssetBundle.Unload(true)`，不检查引用计数。

## 3. 规划中但尚不存在的目录

根 `AGENTS.md` §5 提到 `Runtime/{Rendering,Streaming,Gameplay}`，**当前都不存在**。
新建时注意：

- 渲染相关必须遵守 [rendering_rules](../../../Docs/rendering_rules.md) 的分层（Gameplay 不得直接操作 `CommandBuffer` / `Graphics.*`）。
- 流式相关先读 [pooling_lod](../../../Docs/pooling_lod.md)（反复加载用缓存，不用池）。
- 若要从 SECTR 切到 YooAsset 驱动的 chunk 加载，先读 [world_implementation §6.2](../../../Docs/world_implementation.md) 了解当前耦合点。

## 4. 相关文档

| 文档 | 何时读 |
|---|---|
| [YooAsset 模块规约](../../../Packages/com.tuyoogame.yooasset/AGENTS.md) | 改资源层代码前（必读） |
| [yooasset_practices](../../../Docs/yooasset_practices.md) | 遇到初始化失败、句柄失效、卸载不掉、要查参数依据 |
| [yooasset_3.0.7](../../../Docs/yooasset_3.0.7.md) | 要理解内部链路；改包源码 |
| [pooling_lod](../../../Docs/pooling_lod.md) | 设计缓存、池、chunk 加载策略 |
