# Packages/com.waveharmonic.crest 规约（Crest 5.10.0）

> 第三方源码（Wave Harmonic），**除工程补丁外不要改动**。
> 包是 embed 的本地包：`Packages/packages-lock.json` 里为 `file:com.waveharmonic.crest`。
> 项目落地配置与验收在 [../../Docs/water.md](../../Docs/water.md)；内部机制与第一性原理见 [../../Docs/crest_internals.md](../../Docs/crest_internals.md)。

## 1. 版本与平台约束

| 项 | 值 |
|---|---|
| 包版本 | **5.10.0** |
| package.json | `unity: 2022.3`、`unityRelease: 62f3`；工程实际 **2022.3.62f2**（补丁号低一位） |
| 依赖 | URP core / ShaderGraph `14.0.12` |
| 图形 | 必须 **Compute + 2D Array + Shader Level >= 4.5**；**不支持 OpenGL / OpenGLES / WebGL（非 WebGPU）** |
| 本工程 | Android 仅 Vulkan，与上述限制一致；`WaterRenderer.VerifyRequirements()` 不满足时直接 `enabled=false` |

## 2. 程序集边界

| 程序集 | 平台 | 说明 |
|---|---|---|
| `WaveHarmonic.Crest` | 全平台 | 运行时核心；`defineConstraints: UNITY_2022_3_OR_NEWER` |
| `WaveHarmonic.Crest.Editor` | Editor | `autoReferenced=false` |
| `WaveHarmonic.Crest.Scripting` | 全平台 | 脚本扩展 API |
| `WaveHarmonic.Crest.Shared` | 全平台 | 共享工具/shader |
| `WaveHarmonic.Crest.Integration.Gaia` | Editor | 可选 Gaia 集成，工程未使用 |

- 默认程序集脚本（`Assembly-CSharp` / `Assembly-CSharp-Editor`）可直接 `using WaveHarmonic.Crest`；asmdef 程序集需引用 `WaveHarmonic.Crest`。
- 版本宏由 asmdef 的 `versionDefines` 注入：`d_UnityURP`、`d_UnityHDRP`、`d_UnitySRP`、`d_Unity_Terrain`、`d_UnityModuleWind`、`d_ModuleUnityDirector`、`d_UnityModuleVR`；可选包宏 `d_CrestPaint` / `d_CrestSpline` / `d_CrestPortals` / `d_WaveHarmonic_Crest_CPUQueries` / `d_WaveHarmonic_Crest_ShiftingOrigin`。

## 3. 目录索引

| 目录 | 内容 | 何时读 |
|---|---|---|
| [Runtime/Scripts](Runtime/Scripts/AGENTS.md) | `WaterRenderer` 主循环、视点/尺度、模块与查询、时间、相机门控 | 改运行时逻辑、接查询、排查水不动/不渲染 |
| [Runtime/Scripts/Data](Runtime/Scripts/Data/AGENTS.md) | LOD 数据场：级联/slice、格式、输入、Clip/Depth 语义、sim 频率 | 改模块、分辨率、Clip/Depth、模拟成本 |
| [Runtime/Scripts/Surface](Runtime/Scripts/Surface/AGENTS.md) | 表面几何：Chunks/Quad/Custom、材质、`WaterBody`、反射 | 改水面网格、透明、材质、区域裁剪 |
| [Runtime/Scripts/Volume](Runtime/Scripts/Volume/AGENTS.md) | 水下效果与 mask、材质覆盖、pass 顺序 | 改水下、排查水上看不到/水下不触发 |
| [Runtime/Shaders](Runtime/Shaders/AGENTS.md) | 生成式 shader 设置、keyword 链、透明公式 | 改 shader/keyword/透明前 |
| [Editor/Scripts](Editor/Scripts/AGENTS.md) | 设置资产 → 宏 → HLSL 生成、Inspector 校验 | 切性能特性、改平台设置、升级后校验 |

## 4. 本工程的补丁与生成物

### 4.1 代码补丁（升级 Crest 必须重打）

`Runtime/Scripts/Data/DepthLod.cs` 的 `TerrainDepthInput` 增加了 `TryGetIncludeArea()` Terrain 过滤，使湖外 Terrain 不再回烘 Depth。**升级 Crest 必须重打**；源码无 `[FishDemo]` 标记，按 `TryGetIncludeArea` 定位。启用条件、边界与收益见 [Data §5](Runtime/Scripts/Data/AGENTS.md)。

`Runtime/Scripts/Surface/WaterReflections.cs`：新增 `_ResolutionY` / `ResolutionY`，反射贴图支持非方形（工程用 512×256）；`ComputeHorizonPositionAndNormal` 的 NDC offset 从 float 改为 `Vector2`；新增 `_CaptureDepth` / `CaptureDepth`，关闭时反射相机不请求深度、不执行 CopyDepth。**升级 Crest 必须重打**；按 `ResolutionY` / `CaptureDepth` 与 `[FishDemo]` 标记定位，回归 `PlanarReflectionVerify`。

`Runtime/Shaders/Surface/RainRipples.hlsl` + `Fragment.hlsl` 的一处调用：水面法线叠加全局雨纹层（`_Crest_RainRippleNormal/Intensity/Scale/Speed`），只在 `!i_Underwater` 生效；0 新 pass、+1 次水面纹理采样、无新变体。**升级 Crest 必须重打**；按 `[FishDemo]` 与 `RainRippleNormal` 定位，回归雨开/关截图。控制器 `Assets/Scripts/Runtime/Gameplay/RainRippleController.cs` 用 Enviro `wetness` 驱动，当前挂在 `Crest Lake`；贴图为空时 intensity=0。

### 4.2 工程改过的资产

- `Runtime/Materials/Water.mat` / `Water Volume.mat`：水色、透明、水下参数、`_Crest_PlanarReflectionsEnabled=1`，以及运行时写入的 keyword（`_CREST_CUSTOM_MESH`、`d_Crest_NoMaskColor`）。
- 水下焦散：`UnderwaterEcologySetup.Apply` 往 `Water Volume.mat` 写 `_Crest_CausticsTexture`（包内 `Caustics.png`）、`_Crest_CausticsEnabled=1` 与 `_Crest_Caustics*`；`Water.mat` 只保留浮点参数，贴图属性未序列化。复用已有 underwater pass，不新增 pass/RT/blit。`Remove` 会关掉并清空。见 [underwater_ecology](../../Docs/underwater_ecology.md) §5。
- `Runtime/Shaders/Library/Settings/Settings.Crest.*.hlsl`：`CREST_SIMPLE_TRANSPARENCY (1)`。
- 权威设置是 `ProjectSettings/Packages/com.waveharmonic.crest/Settings.asset`（工程外，不在 Assets 下）。

## 5. 修改硬约束

1. `WaterRenderer` 是单例：一个场景只应有一个。
2. 水面是以视点为中心的 clipmap；`transform.position.xz` 不决定水面位置，移动靠 `Viewpoint`。
3. 小水面必须 Clip；整 chunk 剔除挡不住最外圈。语义见 [Surface §3](Runtime/Scripts/Surface/AGENTS.md)。
4. SimpleTransparency 是三层配置，缺一层会「水面变实 / 仍有 copy pass」；操作见 [water §6.1](../../Docs/water.md)。
5. 关模块前看依赖；不要手改生成物。详见 [Data](Runtime/Scripts/Data/AGENTS.md) 与 [Shaders](Runtime/Shaders/AGENTS.md)。
6. 升级后按 [crest_internals §9](../../Docs/crest_internals.md) 跑验收，并复核 §4.1 补丁。

## 6. 相关文档

| 文档 | 何时读 |
|---|---|
| [crest_internals](../../Docs/crest_internals.md) | **第一次改 Crest 前**：数据流、帧时间线、pass 顺序、成本模型 |
| [water](../../Docs/water.md) | 本工程中心湖的配置、透明/水下、性能模式与验收 |
| [rendering_rules](../../Docs/rendering_rules.md) | 改 pass、depth/opaque、blit 前 |
| [../Editor/Water/AGENTS.md](../../Assets/Editor/Water/AGENTS.md) | `LakeCrestSetup` / `CrestPerformanceMode` / 验收脚本 |
