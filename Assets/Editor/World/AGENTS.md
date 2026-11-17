# Assets/Editor/World 规约（SECTR 切块 / chunk / 巡游）

> 默认程序集 `Assembly-CSharp-Editor`，无 asmdef。
> 本目录产出 `Assets/Scenes/World.unity` 与 `Assets/Scenes/World/Chunks/`（36 个 chunk）。

## 1. 脚本清单

| 脚本 | 职责 | 入口 |
|---|---|---|
| `SectrPipeline.cs` | 2×2 切块 + 导出 36 个 chunk 场景 | `-executeMethod SectrPipeline.SectorizeAndExport` |
| `WorldFinalize.cs` | 删残留地形 + 主相机挂 `SECTR_RegionLoader` | `-executeMethod WorldFinalize.FinalizeScene` |
| `StreamingVerify.cs` | 不依赖 Play Mode 验证空间判定 + 场景可加载 | `-executeMethod StreamingVerify.Run` |
| `FinalIntegrity.cs` | 重建 Build Settings + 全量校验 | `-executeMethod FinalIntegrity.Run` |
| `RichWorldVerify.cs` | 抽样 4 块核对起伏/贴图层/树/草 | `-executeMethod RichWorldVerify.Run` |
| `CleanupOrphanAssets.cs` | 按活跃批次清理孤儿 chunk / TerrainData / session | `-executeMethod CleanupOrphanAssets.Run` |
| `TerrainChunkTuning.cs` | 批量改/还原 36 块 Terrain 渲染参数（可逆）；`DisableDetail` 单独关草 | `Report` / `ApplyMobile` / `RestoreDesktop` / `DisableDetail` |
| `ChunkRenderStats.cs` | 静态统计 pass 数 / 树 / 草实例量 | `-executeMethod ChunkRenderStats.Run` |
| `GraphicsApiSetup.cs` | Android 图形 API 改为仅 Vulkan、关 Automatic | `-executeMethod GraphicsApiSetup.SetAndroidVulkanOnly` |
| `BatchingSetup.cs` | 查询/关闭平台静态合批（PlayerSettings 的 internal API，走反射） | `Report` / `FixAndroid` / `FixAll` |
| `PegasusTourSetup.cs` | 布置环绕巡游路线（16 POI / 高度烘焙进 POI） | 菜单 `FishDemo/World/② 布置 Pegasus 环绕巡游` |
| `PegasusTourVerify.cs` | 巡游运行时验收：驱动相机 / 环形半径 / SECTR 加载 / 贴地高度 / F 键切换 | `-executeMethod PegasusTourVerify.Run` |

## 2. 铁律

### 2.1 切块遍历 Terrain 组件，不要按 root 去重

Gaia 把 9 块地形挂在同一个 `Gaia Terrains` 根下。按 `transform.root` 去重只会得到 1 个**不含 Terrain 组件**的根，导致一块都没切（`SectrPipeline.cs`）。

正确做法：`FindObjectsOfType<Terrain>()` 取快照再逐个处理（`SectorizeTerrain` 结尾会把原地形 `SetActive(false)`，边遍历边改会漏）。

### 2.2 切块必须在植被撒点之后

`SectorizeTerrain` 会把原 TerrainData 的高度/Alpha/Detail/Tree 复制到新 Terrain，并把 sector 范围内的静态子物体收进 sector（`SECTR_TerrainWindow.cs:106` 起）。
切块后再撒点，点会落在已失活的原始地形上。

### 2.3 收尾必须删掉原始地形残留

切块只 `SetActive(false)`，不清 TerrainData。9 块 × 1025² 高度图约 945 万顶点会在加载主场景时全部进内存，与 36 个 chunk 重复，流式就失去意义。

只删 `!activeInHierarchy` 的，避免误删 sector 内容（`WorldFinalize.cs`）。
chunk 引用的 TerrainData 在 `World/TerrainSplits/`，与原始地形独立，删原始地形安全。

### 2.4 chunk 里的 Terrain 渲染参数会被重跑覆盖

`SectrPipeline` 重新切块会生成全新 chunk 场景，Terrain 渲染参数回到 Gaia Desktop 预设（`drawInstanced=0`、detail 距离 150、splat 1024 等）。

调参用 `TerrainChunkTuning.ApplyMobile`（幂等、可 `RestoreDesktop` 回滚），**不要手改 36 个场景**。
流水线重跑后必须再执行一次 `ApplyMobile`，否则参数会退回 PC 档位。

关草用 `TerrainChunkTuning.DisableDetail`，详见 [../Grass/AGENTS.md](../Grass/AGENTS.md) §2。

### 2.5 重跑流水线后必须重建 GTS 与湖/大气

`SectrPipeline` 会清掉 chunk 里的 `GTS_Mesh` 节点、`GTSMesh` 组件、`GtsRuntimeDataBootstrap`，也会让中心湖与 Enviro 节点消失。重跑后按顺序重做：

```
TerrainChunkTuning.ApplyMobile
  → TerrainChunkTuning.DisableDetail
  → GTS 转换与挂载（见 ../Gts/AGENTS.md）
  → 中心湖（见 ../Water/AGENTS.md §2）
  → 大气（见 ../Atmosphere/AGENTS.md §2）
```

### 2.6 数量断言硬编码了 36

`WorldFinalize.cs`、`FinalIntegrity.cs`、`RichWorldVerify.cs` 三处把 chunk/sector 数量写死为 36。

改世界尺寸或切块粒度时，这三处**必须同步改**，否则验证会误报 FAIL。
（`StreamingVerify.cs` 用的是 `sectors.Length`，不写死，无需改。）

## 3. 巡游与相机

- Pegasus 巡游路线半径 1100m，绕中心湖；POI 高度在布置时烘进点，运行时不再贴地采样。
- 验收前禁用 `PegasusTourController`，否则它会与验收脚本抢相机。
- 相机切换（Crest `Viewer` / SECTR loader / Enviro）与 F 键飞行的暂停规则见 [../Water/AGENTS.md](../Water/AGENTS.md) §4。

## 4. 相关文档

| 文档 | 何时读 |
|---|---|
| [world_implementation](../../../Docs/world_implementation.md) | 重跑流水线、理解 chunk 结构 |
| [terrain_streaming](../../../Docs/terrain_streaming.md) | 评估 SECTR 的定位与限制、流式补丁 |
| [gts_srp_batcher](../../../Docs/gts_srp_batcher.md) | 改 GTS 转换脚本、排查地表合批 |
| [pooling_lod](../../../Docs/pooling_lod.md) | 设计 chunk 加载/卸载策略 |
| [../Gts/AGENTS.md](../Gts/AGENTS.md) | GTS 转换的顺序与一进程一块 |
| [../Water/AGENTS.md](../Water/AGENTS.md) | 重跑后重建中心湖 |
| [../Atmosphere/AGENTS.md](../Atmosphere/AGENTS.md) | 重跑后恢复 Enviro |
