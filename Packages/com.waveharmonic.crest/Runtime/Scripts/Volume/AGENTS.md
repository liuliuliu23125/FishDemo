# Crest · Runtime/Scripts/Volume 规约（水下）

> 第三方源码，不改。表面与数据场见 [../Surface/AGENTS.md](../Surface/AGENTS.md)、[../Data/AGENTS.md](../Data/AGENTS.md)。

## 1. 什么时候才会做水下

`UnderwaterRenderer.ShouldRender(camera)` 依次检查：

1. `_Enabled` 且有 `_Material`；
2. 相机 cullingMask 包含 Water 层；
3. 相机排除规则（`_CameraExclusions`，默认排除 Hidden/Reflection）；
4. 视点离水面 **> 2m 直接关闭**（`_DisableHeightAboveWaterOptimization` 只用于调试）；
5. 相机 XZ 落在某个 `Affects` 含 `Volume` 的 `WaterBody` 内（`Exclusion=Include`），或被 `Exclude` 裁掉；
6. 反射相机走单独分支。

因此**水上视角零水下 pass 成本**；项目水下依赖 `Lake Body` 这个 Volume 型 WaterBody。

## 2. 材质：水面是源，Volume 是副本

- `UnderwaterRenderer.Effect.cs` 每帧（或水面材质变化时）执行
  `_VolumeMaterial.CopyMatchingPropertiesFromMaterial(_SurfaceMaterial)`。
- 源材质是 `Surface.AboveOrBelowSurfaceMaterial`（若 WaterBody 覆盖则用覆盖后的材质），工程里就是 `Water.mat`。所以**水下参数写在水面材质上**，写 `Water Volume.mat` 会被每帧覆盖回默认值。
- `d_Crest_NoMaskColor` / `d_Crest_NoMaskDepth` 由 `UpdateEffectMaterial` 在运行时 `SetKeyword`：
  - `NoMaskColor`：相机远离水面（< -8m）或没有 mask 时走全量 shader；
  - `NoMaskDepth`：`!_MaskRead || RenderBeforeTransparency`。
- 这两个 keyword 会随编辑器保存落进 `Water Volume.mat` 的 `m_ValidKeywords`，是运行时状态记录，不要手删。
- 工程还写 `_Crest_AmbientTerm` / `_Crest_DirectTerm`（0.35 / 0.40）；同样以水面材质为准。

## 3. 非 Legacy 模式（工程当前）

`Settings.asset` 的 `_LegacyUnderwater=0`，所以：

- `UseLegacyMask = false`，`RenderBeforeTransparency = true`；
- 水下效果 pass 的 `renderPassEvent = BeforeRenderingTransparents`（`WaterRenderer.k_WaterRenderPassEvent`）；
- 效果 pass `ConfigureInput(Color | Depth)`，只在相机入水时入队；
- 需要 stencil 时额外入队 `CopyDepthBufferPass`，事件为 `AfterRenderingOpaques`；
- `MaskRendererURP` 的 mask pass 在 `BeforeRenderingPrePasses`，只在 `ShouldExecute(camera)` 时执行。

切回 Legacy（`_LegacyUnderwater=1`）会改变 pass 与 mask 行为，同时 `d_Crest_LegacyUnderwater` 宏也会变；必须重新生成设置并验收。

## 4. 视觉参数

所有水下颜色/消光参数以水面材质为准（每帧复制）；具体数值见 [water §4](../../../../../Docs/water.md)。水下雾由 `AtmosphereController` 关闭，Crest 不做大气检测。

## 5. 排查

1. 水下没效果：相机是否在水体 AABB 内、水位是否在 2m 内、Water 层是否在 cullingMask、`Lake Body` 是否 `Affects=Volume`。
2. 水上误触发：检查 `ViewerHeightAboveWater` 与 `_DisableHeightAboveWaterOptimization`。
3. 颜色不对但参数已改：是否写到了 Volume 材质而不是水面材质。
4. 多出 CopyDepth：确认是水下 pass 请求（相机入水）还是 Enviro 的全局 depth 需求，用 Frame Debugger。

## 6. 相关

| 文档 | 何时读 |
|---|---|
| [water §4](../../../../../Docs/water.md) | 本工程透明/水下参数与 6 项验收 |
| [crest_internals](../../../../../Docs/crest_internals.md) §7 | 水下触发条件与 pass 顺序 |
| [../Surface/AGENTS.md](../Surface/AGENTS.md) | 水面材质与 WaterBody |
| [../../../../../Assets/Enviro 3 - Sky and Weather/AGENTS.md](../../../../../Assets/Enviro%203%20-%20Sky%20and%20Weather/AGENTS.md) | 水下雾与大气的边界 |
