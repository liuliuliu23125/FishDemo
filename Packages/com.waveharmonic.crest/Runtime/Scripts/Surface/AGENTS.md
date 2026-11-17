# Crest · Runtime/Scripts/Surface 规约（表面几何 / 材质 / WaterBody）

> 第三方源码，不改。数据场见 [../Data/AGENTS.md](../Data/AGENTS.md)；上层循环见 [../AGENTS.md](../AGENTS.md)。

## 1. 三种网格

| `WaterMeshType` | 几何 | 特点 |
|---|---|---|
| `Chunks` | LOD0 为 1 个 interior + 12 个环 patch，其余 LOD 为 12 个环 patch；最外 LOD 带朝地平线的裙边 | 逐 chunk 视锥/区域剔除；位移参与几何；项目实测 5 级时 **49 个 chunk** |
| `Quad` | 单张 `10000×10000` 四边形，绕 X 轴 90° | **1 次 DrawMesh**；位移只进法线，不改变几何 |
| `Custom` | 仅用户自己的 MeshRenderer | 需要材质开 `_CREST_CUSTOM_MESH` |

- 切换在 `SurfaceRenderer._MeshType`；项目用 `Quad`，由 `CrestPerformanceMode` 写入。
- `Chunks` 的网格由 `WaterBuilder` 在运行时生成，密度 = `0.25 * LodResolution / GeometryDownSampleFactor`。
- `Quad` 仍是整张巨面，**必须 Clip**，否则湖外也画水。

## 2. 材质

- `_Material`：水面以上/通用；`_VolumeMaterial`：水下体积（可空）。
- `WaterBody.OverrideMaterials` 可按 AABB 覆盖 `AboveSurfaceMaterial` / `BelowSurfaceMaterial` / `VolumeMaterial`。
- 透明判定：`IsTransparent(material)` 看 `_SURFACE_TYPE_TRANSPARENT`（URP/HDRP）或 `_BUILTIN_SURFACE_TYPE_TRANSPARENT`（BiRP）。
- `LateUpdate` 每帧写 `_CREST_CUSTOM_MESH` local keyword（`Quad` 为 true）；不要手写进材质。
- 水面层：`_Layer` 默认 **4 Water**；相机 cullingMask 不含该层就不画。

## 3. `WaterBody`

- 以 `transform` 的 AABB 界定区域；**表面只看 XZ**，垂直范围只影响 Volume/Physics。
- `Affects`：`Surface` / `Volume` / `Physics` / `Everything`。
- `Exclusion`：
  - `None`：不做区域裁剪（默认）；
  - `Include`：配合 `EverythingClipped`，只在该 AABB 内保留水（需 `Affects` 含 `Surface`）；
  - `Exclude`：在该 AABB 内裁水。
- `Precise`：用 `ClipLod` 的逐像素 SDF 精确边界；否则只按整 chunk 剔除（粗）。
- `Conservative`：Exclude 时要求 chunk 完全包含才剔除，避免误裁边界。
- 本项目湖的 `ClipLodInput` / `WaterBody` 实际配置见 [water §1、§3](../../../../../Docs/water.md)。

## 4. URP pass

- `WaterSurfaceRenderPass` 构造时 `renderPassEvent = BeforeRenderingTransparents`。
- `ConfigureInput`：
  - 普通透明：`Color | Depth`（需要折射/水下）；
  - **`d_Crest_SimpleTransparency` 定义时：`None`**，不请求 copy。
- `InjectionPoint`：
  - `Default`：主表面由 Unity 的透明队列绘制；pass 只补 `RenderAdditionalChunks`；
  - `BeforeTransparent`：pass 用 RendererList 自己画。
- 项目 `_InjectionPoint=0`（Default）、`_WriteToColorTexture=1` 但 `GetWriteToColorTexture()` 只在 `BeforeTransparent` 或 Meniscus 需要时为 true，所以当前不产生 Color copy。

## 5. 反射

`WaterReflections` 是平面反射模块；反射相机被 `ShouldRender` 排除。项目关闭的原因见 [water §1](../../../../../Docs/water.md)。

## 6. 排查顺序

1. 水面不出现：相机 cullingMask 是否含 Water 层；`Surface.Enabled`；Material 是否为空。
2. 水面出界/铺满：`ClipLod.DefaultClippingState`、`ClipLodInput.Inverted`、WaterBody 的 `Include` AABB。
3. 水面变实/透明错误：SimpleTransparency 三层配置 + `_Crest_TransparencyMinimumAlpha`，见 [water §4](../../../../../Docs/water.md)。
4. 水面 draw call 多：`MeshType` 是否 Quad；`Chunks` 模式下看 chunk culling。

## 7. 相关

| 文档 | 何时读 |
|---|---|
| [crest_internals](../../../../../Docs/crest_internals.md) §5–§6 | Clip 数学、透明与 pass 顺序 |
| [water](../../../../../Docs/water.md) | 本工程湖的网格/材质/透明参数 |
| [../Data/AGENTS.md](../Data/AGENTS.md) | Clip/Depth 数据场 |
| [../Volume/AGENTS.md](../Volume/AGENTS.md) | 水下材质覆盖 |
