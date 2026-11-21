# Assets/Editor/Water 规约（中心湖 / Crest）

> 默认程序集 `Assembly-CSharp-Editor`，无 asmdef。
> 改水前先读 [water](../../../Docs/water.md) §1–§3；本节只记录工具与重建顺序。

## 1. 脚本清单

| 脚本 | 职责 | 入口 |
|---|---|---|
| `LakeSiteProbe.cs` | **只读**：把真实地形高度采成 32m 网格（`Logs/Lake/height-grid.csv`），选湖址用 | `-executeMethod LakeSiteProbe.Run` |
| `LakeCarve.cs` | 按碗形改 TerrainData 高度图造湖盆；`Restore` 可还原 | `Apply` / `Restore` / `Report` |
| `LakeVegetationClear.cs` | 清湖区内树与草（`.bytes` 直接改写）；`Restore` 可还原 | `Apply` / `Restore` / `Report` |
| `LakeGeometryProbe.cs` | **只读**：同时校验 Terrain 高度与 GTS mesh 顶点，抓「数据改了但没重烘」 | `-executeMethod LakeGeometryProbe.Run` |
| `LakeCrestSetup.cs` | 把 Crest 水面接进主场景（裁剪/水色/波浪/URP 前置）；`Remove` 可撤；`Retune` 只重写水色不动场景 | `Apply` / `Remove` / `Report` / `Retune` |
| `LakeRenderVerify.cs` | **需真实图形设备**：玩家视角截图 + 像素判定，证明湖里有水 | `-executeMethod LakeRenderVerify.Run` |
| `LakeCameraSetup.cs` | 环湖轨道相机 + 切换 rig 写进主场景（幂等）；`Remove` 撤并复原原相机 | `Apply` / `Remove` / `Report` |
| `LakeWaterVerify.cs` | **需真实图形设备**：水面透明 / 水下效果开·关对照 / 水上不误开，6 项像素判定 | `-executeMethod LakeWaterVerify.Run` |
| `LakeOrbitVerify.cs` | 轨道几何（半径 / 地形跟随不穿模 / 朝向 / 切换） + Crest 视点 / SECTR loader / tag 跟随（可 `-nographics`） | `-executeMethod LakeOrbitVerify.Run` |
| `CrestRenderProbe.cs` | **只读**：统计 Crest/自定义 feature 的 copy 与水面 chunk | `-executeMethod CrestRenderProbe.Run` |
| `CrestPerformanceMode.cs` | 切水面性能模式（SimpleTransparency / Quad），三项联动可逆 | `ApplySimpleTransparency` / `ApplyQuad` / `ApplyBoth` / `Restore` / `Report` |
| `PlanarReflectionSetup.cs` | 配置平面反射（Above / 512×256 / Default 层 / 视锥门控）并挂 `PlanarReflectionController`；`LakeCrestSetup.Apply` 会调用 `Configure` | `Apply` / `Report` |
| `PlanarReflectionVerify.cs` | **需真实图形设备**：反射相机 512×256、开/关像素差、相机停用后自动关反射 | `-executeMethod PlanarReflectionVerify.Run` |
| `RainRippleVerify.cs` | 雨纹补丁结构 + `Crest/Water` 全部 pass 编译检查 | `-executeMethod RainRippleVerify.Run` |
| `RainRippleSetup.cs` | 把 `RainRippleController` 挂到 `Crest Lake` 并赋临时雨纹；切自动/手动强度 | `Apply` / `Remove` / `Report` / `PreviewOn` / `PreviewOff` |
| `RainRipplePreview.cs` | **需真实图形设备**：同一帧雨纹开/关截图 + 像素差 | `-executeMethod RainRipplePreview.Run` |

`CrestRenderProbe` **看不到 URP 在 `renderer.Setup()` 里入队的 CopyDepthPass**，确认 URP 拷贝要用 Frame Debugger。

## 2. 重跑流水线后重建中心湖

重跑 `WorldPipeline` / `SectrPipeline` 后湖会消失。重建顺序、`LakeCarve` 常量唯一来源、AABB 溢出判据、`Apply` 保留已存在 `LakeCameraRig` 的规则全部见 [water §2](../../../Docs/water.md)；本目录脚本表是各步骤入口。

`LakeCrestSetup.Apply` 末尾会调用 `PlanarReflectionSetup.Configure`，所以重跑水流水线不会丢平面反射；只改反射参数时跑 `PlanarReflectionSetup.Apply` 即可，不必重建水面。

## 3. Crest 硬约束

- `Lod.Enabled` 不能当校验判据；原因见 [Data §2](../../../Packages/com.waveharmonic.crest/Runtime/Scripts/Data/AGENTS.md) 与 [water §8.5](../../../Docs/water.md)。
- SimpleTransparency 由 **PlayerSettings 宏 `d_Crest_SimpleTransparency`** 选择，不是材质开关；三层配置见 [water §6.1](../../../Docs/water.md)。
- `d_Crest_NoMaskColor` 不要手工增删；语义见 [Volume §2](../../../Packages/com.waveharmonic.crest/Runtime/Scripts/Volume/AGENTS.md)。
- 反射贴图是非方形 **512×256**，依赖 Crest 的 `ResolutionY` / `CaptureDepth` 补丁；升级 Crest 会退回方形/重新请求深度，`PlanarReflectionSetup.Report` 会报 FAIL。
- 反射相机必须指向 `Assets/Settings/ReflectionRenderer.asset`（URP renderer index **1**，无 Feature、Auto）；它负责去掉 intermediate/FinalBlit。不要把反射相机的 `RendererIndex` 改回 0。
- 这套配置依赖 URP 补丁 `RenderSingleCameraInternal` 对 `CameraType.Reflection` 读取 UACD；升级 URP 不重打时，`PlanarReflectionVerify` 会看到 `rendererIndex1=False` 且 pass 队列重新出现 CopyDepth/FinalBlit。

Clip 语义、Absorption/Scattering、水下参数与最小 alpha 见 [water §3–§4](../../../Docs/water.md) 与 [Crest 包规约](../../../Packages/com.waveharmonic.crest/AGENTS.md)。

## 4. 相机 rig

所有相机切换统一走 `LakeCameraRig.Apply()`；F 键飞行、Pegasus、组件级开关的规则见 [water §5](../../../Docs/water.md)。

## 5. 验收预热

SimpleTransparency 下 shader 不采样仿真纹理、也没有 copy pass，截图预热 **8 帧**足够（`WarmupFrames` 常量）。若关掉 SimpleTransparency 要调回 **40 帧**。

## 6. 相关文档

| 文档 | 何时读 |
|---|---|
| [water](../../../Docs/water.md) | 改水体、水下、中心湖；§1–§3 必读，§6 性能 |
| [crest_internals](../../../Docs/crest_internals.md) | 第一次改 Crest 前：数据流、帧时间线、Clip/透明数学 |
| [Crest 包规约](../../../Packages/com.waveharmonic.crest/AGENTS.md) | 包内约束、工程补丁、升级清单 |
| [rendering_rules](../../../Docs/rendering_rules.md) | 改 pass / RendererFeature 前 |
| [atmosphere](../../../Docs/atmosphere.md) §0 | 水下雾、Enviro 雾 pass 顺序 |
| [../World/AGENTS.md](../World/AGENTS.md) | 重跑世界流水线 |
