# Assets/GTS User Data 规约（GTS 转换中间产物）

> GTS 的工程数据目录，**由转换脚本生成，不要手改**。
> 上游是 `Assets/Scenes/World/Chunks/*.unity`，下游是 chunk 里的 `GTS_Mesh` 节点。

## 1. 内容

`Scenes/<chunkName>/`：每个 chunk 一个目录，内含 GTS 导出的 sector 场景与 `Meshes/*.mesh`。

- 当前 **36 个 chunk** 各一个目录。
- `Editor/Gts/GtsConvertOne.Run` 按 `Scenes/<chunkName>/**/*.mesh` 读取产物。
- `Editor/Gts/GtsApplyMeshToChunks` 把 mesh 挂进 chunk 场景的 `GTS_Mesh` 节点。

## 2. 硬约束

- **不要手工编辑/删除**：目录结构、mesh 名与层级由 GTS 决定；手改会让 `GtsApplyMeshToChunks` 找不到产物。
- 重跑 `SectrPipeline` 不清理本目录，但旧产物与新 chunk 名不再对应，需要重新转换；顺序见 [Editor/Gts/AGENTS.md](../Editor/Gts/AGENTS.md) §2.4。
- 目录体积大，不属于运行时资源；构建内容由 chunk 场景引用决定，不要把它塞进 YooAsset 收集规则。

## 3. 重建

顺序见 [../Editor/Gts/AGENTS.md](../Editor/Gts/AGENTS.md) §2.4。

## 4. 相关文档

| 文档 | 何时读 |
|---|---|
| [gts_srp_batcher](../../Docs/gts_srp_batcher.md) | 转换参数、合批、worldSizeOverride |
| [../Editor/Gts/AGENTS.md](../Editor/Gts/AGENTS.md) | 转换/挂载脚本 |
| [../Gaia User Data/AGENTS.md](../Gaia%20User%20Data/AGENTS.md) | GTS Profile 与纹理数组 |
