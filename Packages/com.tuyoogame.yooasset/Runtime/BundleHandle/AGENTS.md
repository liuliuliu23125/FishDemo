# YooAsset · Runtime/BundleHandle 规约

> 第三方源码，不改。与缓存的分工见 [yooasset_3.0.7 §3.5.1](../../../../Docs/yooasset_3.0.7.md)。

## 1. 职责

`IBundleHandle` 表示**一个已装载的 bundle**，只做装载与从 bundle 取对象：

```
UnloadBundle
LoadAssetAsync / LoadAllAssetsAsync / LoadSubAssetsAsync / LoadSceneAsync
```

`IBundleCache` 负责「文件在不在缓存」，`IBundleHandle` 负责「文件变成 AssetBundle/Raw/Archive 后的内存实体」。

## 2. 实现

| 实现 | 用途 |
|---|---|
| `AssetBundleHandle` | 标准 AssetBundle；`Unload(true)` |
| `RawBundleHandle` | 原生文件（bytes） |
| `ArchiveBundleHandle` | 归档文件包 |
| `InstantBundleHandle` | 团结引擎 InstantAsset |
| `WebAssetBundleHandle` | WebGL AssetBundle（`WebRequest` 资源包） |
| `Virtual*Handle` | 编辑器模拟构建的虚拟 bundle |

构建侧 `EBundleType` 决定产物类型；运行时由文件系统返回对应 handle。

## 3. 硬约束

- **不要直接调 `UnloadBundle()`**：bundle 的卸载必须由 `ProviderBase` 在 `RefCount==0` 后触发；手工卸载会让仍持有资源对象的句柄失效。
- `Unload(true)` 会连带卸载仍然存活的 Asset，项目在 `Shutdown` 前必须先 `AssetLoader.ReleaseAll()`。
- Web 平台的 `WebAssetBundleHandle` 依赖 UnityWebRequest 生命周期，不能跨帧缓存请求对象。
- `Virtual*` 只在编辑器模拟模式出现，不要在产品代码里判断/依赖具体实现类型。

## 4. 相关

| 文档 | 何时读 |
|---|---|
| [yooasset_3.0.7 §3.5](../../../../Docs/yooasset_3.0.7.md) | bundle 生命周期、卸载时机 |
| [../BundleCache/AGENTS.md](../BundleCache/AGENTS.md) | 文件缓存 |
| [../ResourceManager/AGENTS.md](../ResourceManager/AGENTS.md) | 引用计数与卸载 |
