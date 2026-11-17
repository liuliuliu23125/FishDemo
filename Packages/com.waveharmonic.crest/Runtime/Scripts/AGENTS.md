# Crest · Runtime/Scripts 规约

> 运行时核心。第三方源码，不改；项目配置见 [../../../../Docs/water.md](../../../../Docs/water.md)。
> 先读 [crest_internals](../../../../Docs/crest_internals.md) §1–§3 建立心智模型。

## 1. 心智模型

`WaterRenderer` 是唯一管理器，只负责视点/尺度、让 `Lod` 写数据场、让 `SurfaceRenderer` 画几何。排查先分两半：数据场（`Lod`）与表面（材质/网格/pass）。完整推导见 [crest_internals §1](../../../../Docs/crest_internals.md)。

## 2. 帧时间线

模拟不在 `Update`；`OnEnable` 初始化 Lod/Surface/pass，`LateUpdate` → `ExecuteViewpoint` 用 `Graphics.ExecuteCommandBuffer` 立即提交；CPU 查询在 `LateUpdate` 末尾。逐句时间线见 [crest_internals §3](../../../../Docs/crest_internals.md)。

## 3. 视点

- `Viewer` 解析顺序：`_Camera` → `Camera.main`；编辑器可跟随 SceneView。
- `Viewpoint` 覆盖中心点；`ViewpointDataOverride` 只移动数据场，不移动表面。
- `WaterCamera` 组件用于多视点/绕过相机排除规则。

## 4. 模块与提供者

`Simulations` 顺序：Depth → Level → Flow → DynamicWaves → AnimatedWaves → Foam → Absorption → Scattering → Clip → Albedo → Shadow。

- `CollisionProvider` ← `AnimatedWavesLod`；`DepthProvider` ← `DepthLod`；`FlowProvider` ← `FlowLod`。
- GPU 查询走 `AsyncGPUReadback`；CPU 查询要单独装 `com.waveharmonic.crest.cpu-queries`（`d_WaveHarmonic_Crest_CPUQueries`）。没有 gameplay 查询时开销为 0。
- 关一个模块前看依赖：`AnimatedWaves` 仍提供法线/位移；`Depth`/`Clip`/`Level` 是基础场。

## 5. 时间

`TimeProviders` 是栈：底部恒有 `DefaultTimeProvider`（`Time.time` / `deltaTime`），场景里的 `TimeProvider` 组件压栈。`CustomTimeProvider` 可冻结/覆盖时间，用于自动化验收。

## 6. 相机门控

- `ShouldRender(camera, layer)`：相机 cullingMask 必须包含 `Surface.Layer`（默认 Water=4）。
- `WaterCameraExclusion`：`Hidden` / `Reflection` / `NonMainCamera`；`WaterCamera` 组件强制通过。
- 预览/DepthProbe 相机被硬排除。

## 7. 无图形环境

`-nographics` 下 LOD 不分配纹理、`_Valid=false`，只能验逻辑不能验像素；见 [Data §2](Data/AGENTS.md)。

## 8. 相关

| 文档 | 何时读 |
|---|---|
| [crest_internals](../../../../Docs/crest_internals.md) | 帧流程、pass 顺序、成本模型 |
| [Data/AGENTS.md](Data/AGENTS.md) | 改 LOD / 模拟 / 查询 |
| [Surface/AGENTS.md](Surface/AGENTS.md) | 改网格 / 材质 / WaterBody |
| [Volume/AGENTS.md](Volume/AGENTS.md) | 改水下 |
| [../../../../Docs/water.md](../../../../Docs/water.md) | 本工程湖的参数与验收 |
