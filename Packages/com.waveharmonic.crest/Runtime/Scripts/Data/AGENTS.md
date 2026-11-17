# Crest · Runtime/Scripts/Data 规约（LOD 数据场）

> 第三方源码，不改。这里定义每帧写到 GPU 的数据与成本。
> 上层调用见 [../AGENTS.md](../AGENTS.md)；表面采样见 [../Surface/AGENTS.md](../Surface/AGENTS.md)。

## 1. `Lod` 基类

- 每个 `Lod` 是一张 `Texture2DArray`，slice 数 = `LodLevels`（上限 15）；级联数学见 [crest_internals §2](../../../../../Docs/crest_internals.md)。
- `Resolution`：模块 `_OverrideResolution ? _Resolution : Water.LodResolution`；修改会 `ReAllocate`。
- 格式：`LodTextureFormatMode` = `Automatic` / `Performance` / `Precision` / `Manual`；`Automatic` 常跟随 AnimatedWaves。
- `BufferCount`：需要运动矢量的模块为 2，其余 1。
- `Inputs` 是 `SortedList<int, ILodInput>`，按 Queue 排序；`BuildCommandBuffer` 每帧：翻转缓冲 → 清屏 → `SubmitDraws(Inputs)` → 可选 blur → `ClearBorder` → queries。
- `_TargetsToClear`：没有输入时下一帧会清空，避免上一帧残留。

## 2. `Enabled` 不是持久字段

`Lod.Enabled => _Enabled && _Valid`。`_Valid` 在 `Initialize()` 里设置：

- `-nographics` / Null device / 无可用格式 → `_Valid=false`，`Enabled=false`；
- 编辑期未进 Play Mode 时 GPU 初始化不完整，**不要用 `Enabled` 做资产校验**；用 `_Enabled` / `DefaultClippingState` 等持久字段。

## 3. 模块表（本项目 `World.unity` 配置）

| 模块 | 语义 | 格式 | 项目状态 |
|---|---|---|---|
| `AnimatedWavesLod` | Gerstner/FFT 位移 + 法线；提供 `ICollisionProvider` | `R16G16B16A16_SFloat` | 开，分辨率跟随全局 128 |
| `LevelLod` | 水位/坡度（水平面高度） | `R16_SFloat`（跟随 AnimatedWaves 精度） | 开 |
| `DepthLod` | 水面到湖底深度；可选 SDF | `R16_SFloat` / SDF 时 `R16G16_SFloat` | 开，128，SDF 关 |
| `FoamLod` | 泡沫模拟（Persistent） | `R16_SFloat` | 开，128，`SimulationFrequency=30` |
| `DynamicWavesLod` | 动态波模拟（Persistent） | `R16G16_SFloat` | 关 |
| `FlowLod` | 流动场 | `R16G16_SFloat` | 关 |
| `ShadowLod` | 水面阴影（2 buffer） | `R8G8_UNorm` | 关 |
| `AlbedoLod` | 水面反照率 | `R8G8B8A8_UNorm`，默认 768 | 关 |
| `AbsorptionLod` / `ScatteringLod` | 吸收/散射场 | 输入驱动 | 关 |
| `ClipLod` | 表面裁剪 SDF | `R8_UNorm` | 开，128，`EverythingClipped` |

模块成本 ≈ `启用的 Lod 数 × 每帧 dispatch × slices`；Persistent 模拟即使 `substeps=0` 也会跑 1 次 delta=0 的 advect dispatch（`PersistentLod`）。

## 4. Clip 注册

- 阈值/清屏/`Inverted` 的 min/max 数学见 [crest_internals §5](../../../../../Docs/crest_internals.md)；`Inverted=false` + 白底会全裁。
- `WaterBody` 在 `Affects` 含 `Surface` 时，`Precise` + `Exclusion != None` 会注册 `ClipInput`；`OverrideMaterials` 也会强制 clip。
- 整 chunk 剔除来自 `WaterBody` AABB，粒度粗；精确边界只能靠 Clip。

## 5. Depth 的工程补丁

`TerrainDepthInput.TryGetIncludeArea()`（`DepthLod.cs`）：

- 仅 `ClipLod.DefaultClippingState == EverythingClipped` 时生效；
- 取所有激活、`Exclusion == Include` 的 `WaterBody.AABB` 并集，只对相交 Terrain 做 `DepthTexture.compute`；
- 否则（大洋/无 Include）保留全部 Terrain。

升级 Crest 丢失后：画面仍正确，但湖外 Terrain 每帧仍被 dispatch（实测 15→6 的收益消失）。

## 6. 查询

- `AnimatedWavesLod`：`QuerySource = GPU` 走 AsyncGPUReadback；`CPU` 需要 `com.waveharmonic.crest.cpu-queries` 与 baked wave data。
- `DepthLod`：只有 GPU 查询；`_MaximumQueryCount` 默认 512。
- `FlowLod`：默认 1024。
- 没有注册查询时每帧 0 dispatch；接钓鱼玩法（波高/水深）前先定查询源。

## 7. 相关

| 文档 | 何时读 |
|---|---|
| [crest_internals](../../../../../Docs/crest_internals.md) §4–§5 | 级联数学、模块成本、Clip/Depth 原理 |
| [water §3–§4](../../../../../Docs/water.md) | 本工程 Clip / 透明 / 水下的实际参数 |
| [../Surface/AGENTS.md](../Surface/AGENTS.md) | 这些数据如何被表面采样 |
