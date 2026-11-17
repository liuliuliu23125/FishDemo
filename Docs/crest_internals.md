# Crest 5.10 内部机制（第一性原理）

> 包：`Packages/com.waveharmonic.crest`（5.10.0，embed）。
> 本文解释「为什么」与数据流；本工程湖的配置/验收在 [water](water.md)，包级规约在
> [Packages/com.waveharmonic.crest/AGENTS.md](../Packages/com.waveharmonic.crest/AGENTS.md)。

## 1. 心智模型：水 = 数据场 + 表面几何

Crest 把水拆成两件独立的事：

1. **数据场**：每个模块（`Lod` 子类）维护一张 `Texture2DArray`，逐帧用 compute/材质把位移、深度、泡沫、裁剪等写进去。
2. **表面几何**：`SurfaceRenderer` 按材质采样这些纹理，画出水面（Chunks / Quad / Custom）。

`WaterRenderer` 只是编排者。排查时先定位是哪一半坏了：

- 数据不对：Visualize Data、Compute 输出、`Lod` 的 `Inputs` 与 slice 覆盖。
- 表面不对：材质 keyword、网格类型、Clip、pass/相机层。

## 2. 级联布局

- `LodLevels` = 数组的 slice 数（上限 15），决定水的水平范围；每加一级范围翻倍、成本线性增加。
- 每个 slice 的边长 = `Scale * 2^slice`（`WaterRenderer.CalcLodScale`）；texel 大小 = 边长 / `Resolution`（`CalcGridSize`）。
- 数据场以 `Position` 为中心；`Position.xz` 由视点 XZ 按 texel 周期吸附，避免纹素游走。
- `Scale` 由视点离水高度驱动，夹在 `ScaleRange` 内。视点越高，水越「大」但细节越粗。
- 同一个世界点会同时落在多个 slice 的范围内；shader 用 `g_Crest_LodIndex` / `Lod` 采样参数选择合适的一级。

## 3. 帧时间线

```
LateUpdate
  ├─ CalculateSettingsHash 变化 → Rebuild（重建所有 textures/meshes）
  ├─ WaterBody.UpdateClipSurfaceRegistration
  └─ ExecuteViewpoint(Viewer) 或 逐相机
       ├─ Position / ViewerHeight / Scale
       ├─ 每个启用 Lod: BuildCommandBuffer(SimulationBuffer)
       ├─ Graphics.ExecuteCommandBuffer(SimulationBuffer)   ← GPU 立即提交
       └─ Surface.LateUpdate（chunk bounds、材质 keyword、culling）

beginCameraRendering
  ├─ 相机注册/视点执行（多视点）
  ├─ chunk 可见性
  └─ 入队 Crest 的 URP pass
```

- 模拟不在 `Update()`；`LateUpdate` 负责生成命令并立即执行。
- `Lod.BuildCommandBuffer`：翻转缓冲 → 必要时清屏 → 绘制/派发输入 → blur → 清边界 → 查询。
## 4. 模块与成本

| 模块 | 数据 | 成本形态 |
|---|---|---|
| `AnimatedWaves` | 位移 + 法线；Gerstner/FFT | 每帧若干 compute（Gerstner 96²、shape transfer/combine/pack × slices） |
| `Level` | 水位/坡度 | 输入驱动 + 每帧少量 |
| `Depth` | 深度；可选 SDF | 每个相交 Terrain 一次 `DepthTexture.compute`（工程补丁过滤） |
| `Clip` | 裁剪 SDF | 1 次 `ClipTexture.compute` + 少量 `ClearBorder` |
| `Foam` | 泡沫模拟 | 每帧 ≥1 次模拟 dispatch |
| `DynamicWaves` | 动态波模拟 | 每帧 ≥1 次模拟 dispatch，关闭时为 0 |
| `Flow` / `Shadow` / `Albedo` / `Absorption` / `Scattering` | 可选场 | 关闭时为 0 |

成本近似：`启用的模块数 × 每帧 pass 数 × slices`；分辨率按面积平方增长。改 `LodLevels` / `Resolution` 前先按 [performance_budget](performance_budget.md) 估算。

## 5. Clip 数学

- 阈值 0.5：`>0.5` 裁掉，`<=0.5` 保留。
- `EverythingClipped` 清屏为白（1），`NothingClipped` 清屏为黑（0）。
- `ClipPrimitive.compute` 的 SDF：盒内 `<1`，盒外 `>1`。
- `Inverted=true`：`target = min(target, sdf)`。白底 + 盒内 → 盒内保留、盒外仍为白（裁掉）。
- `Inverted=false`：`target = max(target, 1 - sdf)`。白底时恒为 1 → **全裁**，这是最容易踩的坑。
- `WaterBody` 的整 chunk 剔除粒度粗；精确边界只能靠 Clip 的逐像素 SDF。

## 6. 透明与 CopyPass

SimpleTransparency 下水面走 alpha 混合，`ConfigureInput(None)`，Crest 自身的 CopyColor/CopyDepth 被编译掉——**因此为 0**；工程里剩的 CopyDepth 属于 Enviro。公式与项目值见 [Shaders §3](../Packages/com.waveharmonic.crest/Runtime/Shaders/AGENTS.md) 与 [water §4](water.md)。

## 7. 水下触发

`UnderwaterRenderer` 只在相机离水 `<=2m` 且位于 `Affects=Volume` 的 `WaterBody` 内执行；完整判定链见 [Volume §1](../Packages/com.waveharmonic.crest/Runtime/Scripts/Volume/AGENTS.md)。效果材质每帧从水面材质复制属性，所以水下参数写 `Water.mat`。

## 8. 设置生成的三层

设置资产 → PlayerSettings 宏 → 生成的 `Settings.Crest.*.hlsl`，三层缺一不可；操作步骤与 batchmode 坑见 [Editor/Scripts §2–§3](../Packages/com.waveharmonic.crest/Editor/Scripts/AGENTS.md)。

## 9. 升级清单

1. 重打 `DepthLod.TerrainDepthInput.TryGetIncludeArea` 补丁（见包 AGENTS §4.1）。
2. 检查 `package.json` 的 `unityRelease` 与工程版本（当前 62f3 / 62f2）。
3. 重新生成 `Settings.Crest.*.hlsl`。
4. 跑 `LakeCrestSetup.Report` + `LakeGeometryProbe` + 三个水面验收脚本。
5. 用 `CrestRenderProbe` 对比 pass；URP 内部 copy 用 Frame Debugger。
6. 材质被升级器改写时，按 [water §4](water.md) 复核水色/透明/水下。

文件位置见 [包 AGENTS §3](../Packages/com.waveharmonic.crest/AGENTS.md) 与各子目录 AGENTS.md。
