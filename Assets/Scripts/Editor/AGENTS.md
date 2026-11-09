# Assets/Scripts/Editor 规约（Gaia 世界生成工具链）

> 程序集 `FishDemo.Editor`，命名空间 `FishDemo.EditorTools`。
> 本文件约 80 行，改这些脚本前建议一次读完。

## 1. 这些脚本干什么

编辑器期的世界生成流水线，**不是运行时逻辑**。产出是 `Assets/Scenes/World.unity` 与 `Assets/Scenes/World/` 下的 chunk。

| 脚本 | 职责 | 入口 |
|---|---|---|
| `WorldPipeline.cs` | 建场景 + 9 tile 地形 + 放水，状态机 | 菜单 `FishDemo/World/① 生成世界（地形+水）`；`-executeMethod FishDemo.EditorTools.WorldPipeline.RunStage` |
| `TerrainStamper.cs` | 应用 9 张地貌印章 | 被 `WorldPipeline` 调用 |
| `BiomeSpawner.cs` | Biome 撒点（材质/树/草/石） | 被 `WorldPipeline` 调用 |
| `GaiaSetupTools.cs` | Gaia 材质 BuiltIn→URP 迁移、状态检查 | 菜单 `FishDemo/Gaia/执行 URP 材质迁移` |
| `FixBiomeMaterials.cs` | 按 GUID 换美术材质 shader | 菜单 `FishDemo/Gaia/② 修复植被材质为 URP` |
| `FixWaterMaterials.cs` | 修 2 个水面残留 BuiltIn 材质 | 菜单 `FishDemo/Gaia/③ 修复水面残留 BuiltIn 材质` |
| `UrpPipelineSetup.cs` | 2D 模板 → 3D 通用渲染器 | 菜单 `FishDemo/Rendering/切换到 3D 通用渲染器` |
| `YooAssetSetup.cs` | 生成资源收集配置 | 菜单 `YooAsset/FishDemo/初始化资源收集配置` |
| `YooAssetEditorBootstrap.cs` | 模拟构建 + 注入包根目录 | 菜单 `YooAsset/FishDemo/执行模拟构建并注入路径` |

完整工序与参数见 [world_implementation)](../../../Docs/world_implementation.md)。

## 2. 铁律

### 2.1 不要用同步等待包异步

`CreateOrUpdateWorld`、Biome 撒点、`GaiaSessionManager.Stamp` 都挂到 `EditorApplication.update` 上分帧推进。
在 `-executeMethod` 里 `while` 等待会占死主线程，协程永不前进（**实测死等**，`WorldPipeline.cs:15-21`）。

正确形态：入口立即返回 → `EditorApplication.update += Tick` → 状态机分帧推进 → 完成时 `EditorApplication.Exit(code)`。

**批处理命令行不要加 `-quit`**，否则进程会在流水线完成前退出。

### 2.2 工序顺序不可颠倒

```
地形 → 植被 → 水 → 切块 → 导出
```

- 植被必须在切块前：`SectorizeTerrain` 会复制 TerrainData 并把原地形 `SetActive(false)`，切块后再撒点会撒到失活地形上。
- 水面必须在切块前且不进 chunk：全局平面分块会产生接缝。
- `m_createInScene` 必须 `false`：否则 Gaia 自带 TerrainLoader 与 SECTR 双重加载。

### 2.3 会写第三方资产，改之前想清楚

`BiomeSpawner.FixTreeHeightMasksOnPreset`（`:151-212`）会**直接改写** `Gaia Pro Assets and Biomes/Biomes/Alpine Meadow/Alpine Meadow-Trees.asset` 里的 HeightMask。

- 现状：`m_heightMaskType: 0`（WorldSpace）、`m_absoluteHeightMin: 62`、`m_absoluteHeightMax: 665`。
- 原因：预设按"世界高 115m / 海平面 25m"标定，与本工程 700m / 60m 错位，导致 0 棵树。
- 风险：换预设或重装 Gaia 会丢，需重跑；重跑会再次覆盖。

新增类似的"改预设/改第三方资产"逻辑时，必须在注释里写清副作用与恢复方式。

### 2.4 单位陷阱

- `StamperSettings.m_height` 是 **0..50 的垂直缩放系数，不是米**。
  换算：`scaleY = 50 × 目标米数 / terrainData.size.y`（`TerrainStamper.cs:32-41`）。
  误填大值会把纹理压扁成近平地。
- `ImageMask.m_heightMaskType`：`0=WorldSpace`、`1=RelativeToSeaLevel`（`ImageMask.cs:31`）。

### 2.5 海平面有多份，改一处不够

`WorldPipeline.cs:42` 的 `SeaLevel` 与 `BiomeSpawner.cs:33` 的 `SeaLevel` 必须一致。
但**还有第三处**：Gaia session 与水面预制体字段（见 [world_implementation §6.1)](../../../Docs/world_implementation.md)）。
只改常量不改 session，运行时水面 transform 会把字段覆盖回去。

## 3. 新增脚本放哪

- 只依赖 Gaia / URP / YooAsset → 放这里，进 `FishDemo.Editor`。
- 需要调 SECTR（`SECTR_TerrainWindow`、`SECTR_StreamExport`、`SECTR_Sector` 的编辑器 API）→ **必须放 `Assets/Editor/`**，否则编译失败。

## 4. 相关文档

| 文档 | 何时读 |
|---|---|
| [world_implementation)](../../../Docs/world_implementation.md) | 要重跑世界生成、排查 chunk 问题、核对参数 |
| [terrain_streaming)](../../../Docs/terrain_streaming.md) | 做地形选型、流式方案对比 |
| [../AGENTS.md](../AGENTS.md) | 程序集边界、命名空间约定 |
| [../../Editor/AGENTS.md](../../Editor/AGENTS.md) | 改 SECTR 那一半流水线 |
