# Assets/Scenes 规约

> 本文件约 64 行，改场景或重跑世界生成前建议一次读完。

## 1. World.unity 是产物，不是手搭场景

`Assets/Scenes/World.unity` 及其 `World/` 子目录全部由批处理脚本生成：

```
Assets/Scenes/World.unity                    主场景（14 个根对象）
Assets/Scenes/World/Chunks/*.unity           36 个 chunk 场景（按批次命名）
Assets/Scenes/World/TerrainSplits/*.asset    36 份 chunk TerrainData
```

**手改会在下次重跑时被覆盖。** 要改结果，改脚本参数后重跑，或改完立即提交并接受"重跑会丢"的代价。

生成链路与参数见 [world_implementation](../../Docs/world_implementation.md)。

## 2. 批次命名规则

chunk / TerrainData 文件名形如：

```
World_SECTR Terrain_0_0-20261109 - 162007 0-1-0 Sector.unity
              └ tile(2D) ┘ └─ 批次 token ─┘ └sector(3D)┘
```

- **批次 token**（`YYYYMMDD - HHMMSS`）来自 Gaia session，每次重跑都变。
- 只有主场景与 Build Settings 引用的那一批是"活跃批次"。其余是孤儿。
- 当前：磁盘 72 个 chunk（162007 与 140021 各 36），**只有 162007 在用**。
- 清理用 `-executeMethod CleanupOrphanAssets.Run`，它按活跃 chunk 的 `NodeName` 反推批次，不会盲删。

## 3. 主场景构成

| 根对象 | 说明 |
|---|---|
| `Main Camera` | 挂 `SECTR_RegionLoader`（加载中心） |
| `Directional Light` | 唯一光源 |
| `Gaia Water` | 水面预制体实例，y=60 |
| `Terrain_x_x` ×9 | 只有 Sector×4 + Portal×4，**无 Terrain 组件**（地形在 chunk 里） |
| `Gaia Terrains` / `Gaia Runtime` / `Gaia Tools` | Gaia 运行时容器 |
| `Alpine Meadow-*` ×10 | 撒点器，挂着空转的 Gaia `TerrainLoader` |

**不要**往主场景加地形或静态大物件 —— 它们不会被 SECTR 分块，会常驻内存。可流式的内容应放进 chunk 或走资源层。

## 4. Build Settings

- 必须包含 `World.unity` + 36 个活跃 chunk 场景（共 37 个）。
- 由 `FinalIntegrity.Run` 整体重建，不要手工维护。
- SECTR 通过 `SceneManager.LoadSceneAsync(ScenePath)` 加载，**场景必须登记在 Build Settings**，否则运行时静默失败。

## 5. 其他场景

| 场景 | 说明 |
|---|---|
| `SampleScene.unity` | 3D 模板示例，由 `UrpPipelineSetup` 重建。不参与世界生成 |

## 6. 相关文档

| 文档 | 何时读 |
|---|---|
| [world_implementation](../../Docs/world_implementation.md) | 重跑生成、排查 chunk、查已知缺陷 |
| [../Editor/AGENTS.md](../Editor/AGENTS.md) | 切块/收尾/验证脚本的约定 |
| [pooling_lod](../../Docs/pooling_lod.md) | chunk 加载与缓存策略 |
