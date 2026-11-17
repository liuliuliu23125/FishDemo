# YooAsset · Runtime/ResourceManager 规约

> 第三方源码，不改。引用计数细节见 [yooasset_3.0.7 §3.4](../../../../Docs/yooasset_3.0.7.md)，
> 泄漏陷阱见 [yooasset_practices §3–§5](../../../../Docs/yooasset_practices.md)。

## 1. 结构

`ResourceManager`（主线程，非线程安全）持有：

- `_providerDict`：`ProviderKey → ProviderBase`（同一资源按类型/加载方式区分）；
- `_bundleLoaderDict`：`BundleName → LoadBundleOperation`，受 `BundleLoadingMaxConcurrency` 限制；
- `_sceneHandles` / `_tempSceneHandles`：场景句柄的卸载跟踪。

Provider 类型：`AssetProvider` / `SubAssetsProvider` / `AllAssetsProvider` / `SceneProvider` / `BundleFileProvider` / `ErrorProvider`。
句柄类型与 Provider 一一对应。

## 2. 引用计数

```
LoadAssetAsync → Provider.CreateHandle → RefCount++ → 返回新 Handle
handle.Release() → Provider.ReleaseHandle → RefCount-- → 从 _handles 移除
                 → RefCount==0 时 Provider.TryUnloadBundle()
```

- 项目关闭 `AutoUnloadBundleWhenUnused`（见包 §6）时，`TryUnloadBundle()` 不做任何事，bundle 要等 `UnloadUnusedAssetsAsync` 清理。
- `ReleaseAllHandles` 会遍历 `_handles` 快照并逐个 `Release`。
- **同一句柄二次 `Release`**：`HandleBase.Release` 看到 `Provider==null` → warning + return（不抛）。
- **会抛的情况**：直接调 `Provider.ReleaseHandle` 重复释放；`RefCount<=0` 时再释放；`ReleaseAllHandles` 与业务 `Release` 并发。
- 零引用 Provider 仍留在 `_providerDict`，直到 `TryDestroyProviders`；所以 `UnloadUnusedAssetsAsync` 既是卸载 bundle，也是清字典。

## 3. Bundle 加载与卸载

- `LoadBundleOperation` 处理依赖链与加载器生命周期；源码注释：「互相引用的资源包无法卸载！」。
- 默认要求**所有引用我的 bundle 都已销毁**才允许卸载；构建期不做环检测。
- chunk 之间禁止互相依赖；公共资源单独成包。
- `UnloadAllAssetsAsync(shouldReleaseHandles:true)` 会强制释放外部句柄，只在销毁/切包时用。
- `UnloadUnusedAssetsAsync(maxLoopCount=10)` 反复尝试直到依赖链清空。

## 4. 场景句柄

`SceneHandle` 额外受 `SceneManager.sceneUnloaded` 影响；`UnloadSceneOperation` 与引用计数分开管理。场景加载失败时 `_tempSceneHandles` 负责回滚。

## 5. 项目封装

`Assets/Scripts/Runtime/Resources/AssetLoader.cs`：

- `LoadAsync<T>` / `InstantiateAsync` 每次取新句柄并记账；
- `Release(location, handle)` 经 `_handles` 去重后释放；
- `ReleaseAll()` 在 `Shutdown()` 前调用。

**业务层禁止直接持有/释放 Provider**，也不要缓存 `AssetHandle` 跨多个 owner。

## 6. 相关

| 文档 | 何时读 |
|---|---|
| [yooasset_3.0.7 §3.4](../../../../Docs/yooasset_3.0.7.md) | 两级计数、释放后果链、二次释放 |
| [yooasset_3.0.7 §3.5](../../../../Docs/yooasset_3.0.7.md) | bundle 生命周期、`AutoUnloadBundleWhenUnused` |
| [yooasset_practices §4–§6](../../../../Docs/yooasset_practices.md) | 交叉引用、模拟模式、`Shutdown` 风险 |
| [../ResourcePackage/AGENTS.md](../ResourcePackage/AGENTS.md) | 包级生命周期 |
