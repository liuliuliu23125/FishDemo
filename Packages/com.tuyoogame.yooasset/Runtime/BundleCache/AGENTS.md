# YooAsset · Runtime/BundleCache 规约

> 第三方源码，不改。缓存校验机制见 [yooasset_3.0.7 §3.3](../../../../Docs/yooasset_3.0.7.md)。

## 1. 职责

`IBundleCache` 管理**一个位置上的 bundle 文件缓存**：写入、校验、加载、清理、淘汰。它是 `IFileSystem` 的内部组件，业务层不应直接持有。

```
InitializeAsync / WriteCacheAsync / VerifyCacheAsync / LoadBundleAsync / ClearCacheAsync
IsCached(bundleGuid) / GetCacheFilePath(bundleGuid)
```

## 2. 五种实现

| 实现 | 对应文件系统 | 特点 |
|---|---|---|
| `BuiltinBundleCache` | Builtin | 只读；catalog 描述内置资源 |
| `EditorBundleCache` | Editor | 基于 `AssetDatabase` 扫描；只读 |
| `SandboxBundleCache` | Sandbox | 可写；校验级别 + 淘汰策略；移动端主路径 |
| `WebServerBundleCache` | WebServer | 只读，WebGL 服务器资源 |
| `WebNetworkBundleCache` | WebNetwork | 网络缓存 |

## 3. 校验（Sandbox）

`EFileVerifyLevel`：

| 级别 | 行为 | 代价 |
|---|---|---|
| `Low`（默认） | 只验证文件存在 | 最便宜 |
| `Middle` | 验证文件大小 | 中 |
| `High` | 读全文件算 CRC | 1 万 bundle × 5MB ≈ 50GB IO |

项目在启动预算内保持 `Low`；只在怀疑缓存损坏时临时提高，不要常驻 `High`。

## 4. 淘汰策略

`ICacheEvictionPolicy` 实现：`EvictionAllPolicy` / `EvictionByLocationsPolicy` / `EvictionByTagsPolicy` / `EvictionUnusedPolicy`。清理通过 `ClearCacheOptions` 选择策略，不要手删目录。

## 5. 硬约束

- 缓存目录与 catalog/manifest 必须一致；手删文件会让 `IsCached` 与真实状态脱节。
- `SpaceOccupied` / `FileCount` 是统计值，不要在每帧调用做大范围扫描。
- 缓存文件按 `BundleGuid` 命名；构建产物的 GUID 变更会导致旧缓存成为孤儿，按淘汰策略清理。

## 6. 相关

| 文档 | 何时读 |
|---|---|
| [yooasset_3.0.7 §3.3](../../../../Docs/yooasset_3.0.7.md) | 五种缓存、校验实现、后台连续校验 |
| [../FileSystem/AGENTS.md](../FileSystem/AGENTS.md) | 文件系统归属与参数 |
| [../../AGENTS.md](../../AGENTS.md) | 包级入口与参数表 |
