# Assets/Grasses 规约（草 BRG shader）

> 自研 shader：`GrassBRG.shader`（`Shader "FishDemo/Grass/BRGMinimal"`）。
> 运行时与烘焙在 `Assets/Scripts/`；落地状态与踩坑见 [grass_brg_progress](../../Docs/grass_brg_progress.md)。

## 1. 合同（不要改坏）

| 项 | 值 / 约定 |
|---|---|
| 管线 | URP；唯一 pass `UniversalForward`，**没有 ShadowCaster**（草不投影） |
| 队列 | `RenderType=TransparentCutout`、`Queue=AlphaTest`；`Cull Off`、`ZWrite On`、`ZTest LEqual` |
| 目标 | `#pragma target 4.5` + `#pragma exclude_renderers gles` |
| 实例数据 | 32B / 实例；DOTS instancing（`DOTS_INSTANCING_ON`） |
| 材质变体 | `_USE_ALBEDO_TEX`（`shader_feature_local`）、`multi_compile_fog` |
| 显色 | albedo 图集；`_TypeTint` / `_TypeScaleBase` 按类型覆盖，不改 buffer 布局 |

## 2. 硬约束

- **Android 只允许 Vulkan**。回落 GLES 时该 shader 被 `exclude_renderers gles` 排除，草会整片消失。
- **显色靠图集，不是 tint**：三层 LOD 的每类型材质都要启用 `_USE_ALBEDO_TEX`；`shader_feature` 只 `SetFloat` 不切换变体（静默失效，草会变成兜底白模或纯 tint）。
- 竖直面片不能用地表法线做 Lambert；用朝相机混合 + 包裹光照。
- **不开 `AlphaToMask`**（MSAA=1 时 alpha-to-coverage 退化，近景草会碎）。
- 风：`_WindSpeed` / `_WindStrength` 由 `AtmosphereController` 每 0.25s 从 Enviro 桥接；树风仍由 PW shader 管，两者不要混。
- `_GrassPos` / `_GrassNormalScale` 是 DOTS 实例属性，由 BRG 写入；不要在材质上手工设置。

## 3. 相关文档

| 文档 | 何时读 |
|---|---|
| [grass_brg_progress](../../Docs/grass_brg_progress.md) | **改 shader 前**：变体、图集、LOD、踩坑 |
| [grass_brg_plan](../../Docs/grass_brg_plan.md) §1.1 | Vulkan / BRG 的原始约束 |
| [rendering_rules](../../Docs/rendering_rules.md) | 批处理路径与 pass 性能账 |
| [../Editor/Grass/AGENTS.md](../Editor/Grass/AGENTS.md) | 草验证脚本与 detail 原型 |
