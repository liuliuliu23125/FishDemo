# Assets/Gaia User Data 规约（世界生成数据）

> Gaia 的工程数据目录。**不要手工改动**；由 `FishDemo.EditorTools.WorldPipeline` / Gaia 生成。
> 地形与世界的重跑流程见 [world_implementation](../../Docs/world_implementation.md)。

## 1. 内容

| 路径 | 内容 | 谁在用 |
|---|---|---|
| `GTS World Profile.asset` | GTS Profile：mesh 设置 + `textureArraySettings` | `Editor/Gts/GtsConvertOne`、`GtsAstcCompress`（固定路径） |
| `GTS World Profile_GTSData/` | `Albedo Texture Array.asset`、`Normal Texture Array.asset` | GTS shader；转换时由 Profile 重建 |
| `GTS Test Profile_GTSData/` | 早期 GTS 测试档的数据目录 | 无代码引用 |
| `Sessions/` | Gaia session（世界生成快照，`GS-*.asset`） | Gaia 的 `GaiaSessionManager`；`WorldPipeline` 建/更世界时写入 |
| `Settings/Package Config Settings.asset` | Gaia 包配置 | Gaia 编辑器 |

## 2. 硬约束

- **GTS 纹理数组必须是 `ASTC_8x8`**，入口是 `Editor/Gts/GtsAstcCompress.Run`；原因见 [Editor/Gts/AGENTS.md](../Editor/Gts/AGENTS.md) §2.3。
- **`GTS World Profile.asset` 的路径是脚本常量**：`Assets/Gaia User Data/GTS World Profile.asset`。移动/改名会让 GTS 转换找不到 Profile。
- 重跑世界生成会覆盖 Profile 与纹理数组；之后必须重跑 GTS 转换与挂载（见 [../Editor/Gts/AGENTS.md](../Editor/Gts/AGENTS.md)）。
- `Sessions/` 是 Gaia 的生成会话记录（`CleanupOrphanAssets` 也按活跃批次处理 session）。清理用 `Editor/World/CleanupOrphanAssets.Run`，不要手删。

## 3. 相关文档

| 文档 | 何时读 |
|---|---|
| [world_implementation](../../Docs/world_implementation.md) | 重跑 Gaia 世界生成 |
| [gts_srp_batcher](../../Docs/gts_srp_batcher.md) | GTS Profile、纹理数组、ASTC |
| [../Procedural Worlds/AGENTS.md](../Procedural%20Worlds/AGENTS.md) | Gaia/GTS 包级约定 |
