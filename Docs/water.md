# 水体：Crest Water 5（中心湖落地记录）

> Crest 5.10.0（`Packages/com.waveharmonic.crest`），Unity 2022.3.62f2 + URP 14。
> 本文只记录当前配置、施工链路、踩坑与验收；历史逐包选型分析已删除。
> 包内机制与补丁清单见 [crest_internals](crest_internals.md) 与 [Crest 包规约](../Packages/com.waveharmonic.crest/AGENTS.md)。
> 选型结论：Crest 原生支持 2022.3。平面反射已开启，但外面包了一层**视锥门控**（`PlanarReflectionController`），只反射 Default 层。

## 1. 当前配置

| 项 | 值 |
|---|---|
| 湖心 / 半径 / 水位 / 碗深 | `(192, 96)` / 190m / **264m** / 30m |
| 渲染模式 | **SimpleTransparency + Quad**（Crest 自身 CopyColor/CopyDepth=0，水面 1 次 DrawMesh） |
| Surface Clipping | `EverythingClipped` + `ClipLodInput(Cube 640×640, Inverted=true)` |
| LOD / 分辨率 | `LodLevels=5`；Depth/Foam/Clip/AnimatedWaves/Level=128；Gerstner=96；Depth SDF 关 |
| 模块 ON | AnimatedWaves / Foam / Depth / Level / Clip |
| 模块 OFF | DynamicWaves / Flow / Shadow / Albedo / Absorption / Scattering |
| 平面反射 | **ON**：Above only / 512×256 / Default 层；相机看不到湖时 `PlanarReflectionController` 关闭；草在 layer 6 不反射 |
| Depth Terrain | 仅与湖区 AABB 相交的 Terrain（补丁；细节见 [Data §5](../Packages/com.waveharmonic.crest/Runtime/Scripts/Data/AGENTS.md)） |
| 管线 depth/opaque | depth=1（Enviro）、opaque=0；消费者见 [rendering_rules](rendering_rules.md) §1.2 |

> `LodLevels=5` 的原因：固定视点 26m 时 4 级最外层半边长只有 256m，离 R=190m 的岸线仅 66m，边界像方框；5 级为 512m。

## 2. 湖盆与施工链路

地形是缓坡，中心 ±800m 内没有天然洼地（最低点 260.6m，与周边只差 4m），必须人工挖碗形盆地。
参数唯一来源是 `LakeCarve`（`Center/Radius/WaterLevel/Depth/Blend`），改湖先改那里。

| 参数 | 值 | 依据 |
|---|---|---|
| 湖心 | `(192, 96)` | 高度网格全局搜索：挖方小、AABB 内不溢出 |
| 半径 / 过渡带 | 190m / 90m | R..R+90 不挖，岸线自然收边 |
| 水位 / 碗深 | 264m / 30m | 碗沿 266m、湖外最低原生地形 266.7m；留 2.7m 波浪余量 |
| 挖方 | ~5.0M m³ | 只挖不填 |

**溢出判据必须按矩形 AABB，不能按圆形**：水面范围由 WaterBody 的矩形 AABB 决定，AABB 外地形再低也不会露出水面。

重跑 `WorldPipeline` / `SectrPipeline` 后湖会全部丢失，按顺序重做：

```
LakeCarve.Apply → LakeVegetationClear.Apply
  → GtsConvertOne.Run × 受影响 chunk（一进程一块）→ GtsApplyMeshToChunks.RunAll
  → GtsFixMeshRendering.RunAll → LakeCrestSetup.Apply
  → CrestPerformanceMode.ApplyBoth（仅在性能模式被 Restore 过时需要）
  → LakeGeometryProbe.Run + LakeRenderVerify.Run
```

`LakeCrestSetup.Apply` 保留已存在的 `LakeCameraRig`，不新建相机；场景从零重建时仍需 `LakeCameraSetup.Apply`。当前场景已无 `Gaia Water` 节点。

## 3. 为什么必须用 Clip

Crest 水面是以视点为中心的 clipmap，最外圈可到数十公里，`WaterRenderer` 的 transform XZ 对水面没有意义；机制见 [crest_internals](crest_internals.md) §2。

- `WaterBody` 的 AABB 剔除是**整 chunk 粒度**，挡不住巨型外圈 chunk（Chunks 模式实测 49 个 chunk 只有 8 个被剔除）。
- Quad 模式虽然只有 1 次 DrawMesh，但那张 10000×10000 的四边形同样需要 Clip 才能只留在湖盆。
- 只有 Clip 的逐像素 SDF 能精确裁出湖盆。

**`ClipLodInput.Inverted` 语义**（`ClipPrimitive.compute:99-104`）：初始全写 1（裁）。
`Inverted=true` → `target = min(target, sdf)`：盒内保留、盒外裁掉，**正确**。
`Inverted=false` → `target = max(target, 1-sdf) ≥ 1`：全裁，一滴水不剩（踩过）。

## 4. 透明与水下

SimpleTransparency 的 `o_Alpha` 公式与非 Simple 模式的行为见 [Shaders §3](../Packages/com.waveharmonic.crest/Runtime/Shaders/AGENTS.md)。本工程 `_Crest_TransparencyMinimumAlpha=0.12`，volumeOpacity 由 DepthLod 提供；调透明度要同时看两者，不是只调 Absorption/Scattering。

材质上的 absorption/scattering 系数两种模式都保留；当前湖用它们做恒定水色，不跑仿真。

| 参数 | 值 | 说明 |
|---|---|---|
| `_Crest_AbsorptionColor` | `(0.62, 0.86, 0.92, 0.016)` | alpha 是密度，越小越清澈 |
| `_Crest_Absorption` | `(0.049, 0.015, 0.008)` /m | R 衰减最快 → 水下偏青 |
| `_Crest_Scattering` | `(0.012, 0.026, 0.036)` /m | R 必须压低，否则雾发白 |
| extinction 合计 | `(0.061, 0.041, 0.045)` /m | 1/e 深度 16/24/22m |

水下开关判定与材质覆盖规则见 [Volume §1–§2](../Packages/com.waveharmonic.crest/Runtime/Scripts/Volume/AGENTS.md)。本工程由 `Lake Body`（Volume|Physics、Include）触发，`_Crest_AmbientTerm` / `_Crest_DirectTerm` 取 **0.35 / 0.40**（写 `Water.mat`）。

**焦散**由 `UnderwaterEcologySetup.Apply` 写 `Water Volume.mat` 的 `_Crest_CausticsTexture`（Crest `Caustics.png`）、`_Crest_CausticsEnabled=1` 与 `_Crest_Caustics*`；`Water.mat` 只有同组浮点参数，surface 的贴图属性当前未序列化。复用已有 underwater pass，**0 新 pass / 0 RT / 0 blit**，只增加 2 次焦散纹理采样。礁区、鱼群与海床见 [underwater_ecology](underwater_ecology.md)。

**水面睡莲/浮萍**由 `LakeDuckweedSetup.Apply` 沿 `LakeCarve` 岸线布置（193 株、4 网格、1 材质、材质开 instancing，≈372k tris）；固定水位 +0.06m，不新增 pass/RT。见 [duckweed](duckweed.md)。

## 5. 相机 rig

没有用 Cinemachine（工程未装，且解决不了下面三件事）。`LakeCameraRig.Apply` 统一切换：

1. **Crest 视点**：`WaterRenderer.Viewer`，水面 clipmap 以它为中心。
2. **SECTR chunk 加载**：`SECTR_RegionLoader` 按自身 transform 判定，不看相机；两台相机各挂一个，只启用当台。
3. **`Camera.main` tag**：GrassBRG 剔除兜底等依赖它，rig 把 MainCamera tag 搬到当前相机。
4. **Enviro 相机**：`EnviroManager.ChangeCamera`（RenderFeature 只对 manager.Camera/Cameras 生效）。

| 文件 | 职责 |
|---|---|
| `Gameplay/LakeOrbitCamera.cs` | 半径 300m；高度 = 地表+18m 且不低于水位+26m（指数平滑）；120s/圈；俯仰 12°±3°；`Paused` 让出 transform |
| `Gameplay/LakeCameraRig.cs` | **C** 切相机 + **F** 自由飞行；同步 Camera/AudioListener/loader/Viewer/tag/Enviro |
| `Assets/Editor/Water/LakeCameraSetup.cs` | 幂等写入场景；`Apply`/`Remove`/`Report` |

- **组件级开关，不要 `SetActive(false)`**：每台相机自带 AudioListener，禁 GameObject 会报 "no audio listeners"。
- **F 键飞行前必须 `LakeOrbitCamera.Paused`**：它在 `LateUpdate` 写 transform，比飞行的 `Update` 晚，不暂停会把位移整帧覆盖（“按 F 没反应”）。
- 巡游相机飞行时必须 `PauseFlythrough()`，退出按最近路径点续接；`PegasusTourController` 已被 rig 禁用（它也监听 F）。
- 退出飞行用 `ResumeFromCurrentPosition()`，不要 `ResetPose()`（会瞬移）。

## 6. 性能模式

| 配置 | Crest pass | Crest CopyColor | Crest CopyDepth | 水面 chunk | draw call |
|---|---|---|---|---|---|
| 原始（Chunks + 非 SimpleTransparency） | 4 | 1 | 1 | 49 | ~49 |
| + SimpleTransparency | 1 | 0 | 0 | 49 | ~49 |
| **+ SimpleTransparency + Quad** | **1** | **0** | **0** | **0** | **1** |
| + Enviro 大气 | 1 | 0 | **1（Enviro）** | 0 | 1 |
| **+ 平面反射（当前）** | 1 | 0 | **0**（`CaptureDepth=false`） | 0 | 1 + 反射场景（无 final blit） |

> 表的 pass/copy 列来自 `CrestRenderProbe`，**只统计 Crest/自定义 feature 在 `beginCameraRendering` 时已入队的 pass**。URP 在 `renderer.Setup()` 里入队的 CopyDepthPass 不在其中；当前 Enviro 的 1 次 CopyDepth 要由 Frame Debugger 或 URP 源码路径确认。
>
> 2026-11-17 实测：`MeshType=Quad`、`CREST_SIMPLE_TRANSPARENCY=1`、水面 chunk=0、Crest pass=1、Crest CopyColor/CopyDepth=0。probe 输出里的 `CopyDepthPass=0` 只代表 Crest 没有入队 copy。

### 6.1 SimpleTransparency 三层配置（缺一不可）

1. Crest Settings 资产（`ProjectSettings/Packages/com.waveharmonic.crest/Settings.asset`）的 `_PlatformSettings*.SimpleTransparency=1` + `_OverrideSimpleTransparency=1`。
2. scripting define `d_Crest_SimpleTransparency`（影响 C# 的 `#if`，如 `SurfaceRenderer.Universal.cs:61` 的 `ConfigureInput`）。
3. 重新生成 `Runtime/Shaders/Library/Settings/Settings.Crest.*.hlsl`（`CREST_SIMPLE_TRANSPARENCY (1)`）。

实现细节（为何不能只改 hlsl、batchmode 怎么做）见 [Crest Editor/Scripts §3](../Packages/com.waveharmonic.crest/Editor/Scripts/AGENTS.md)；工具：`CrestPerformanceMode`。

**切 SimpleTransparency 必须同时调低 `_Crest_TransparencyMinimumAlpha`（当前值见 §4）**，否则水面几乎不透明。

### 6.2 当前还剩的 compute

- **DepthLod**：每个与湖区 AABB 相交的 active Terrain 一次 `DepthTexture.compute`（16²×5）。补丁过滤后只对相交 Terrain dispatch。
- **AnimatedWaves**：Gerstner 96²、ShapeWavesTransfer 96²×5、5 次 ShapeCombine 128²、PackLevel 128²×5。Quad 下只喂法线，不能直接关。
- **Foam**：每帧至少 1 次 dispatch；降 `SimulationFrequency` 只减少完整仿真步，不减少 dispatch。
- **Clip**：1 次（16²×5）+ 2 次很小的 `ClearBorder`。
- **Underwater**：入水才有 pass（>2m 自动关），非入水零成本。
- **Queries**：没有 gameplay 注册时 0 dispatch。

**Crest 源码补丁**：本湖依赖 `DepthLod` 的 Include AABB 过滤；升级必须重打，细节见 [Data §5](../Packages/com.waveharmonic.crest/Runtime/Scripts/Data/AGENTS.md)。

**`CrestPerformanceMode.Restore`** 会回退 Chunks + 非 SimpleTransparency + URP Depth/Opaque 开启 + minAlpha 0.95。**接了大气候不要直接跑**：Enviro 仍需要 depth=1，还原后重跑 `AtmosphereSetup.Apply`，确认 depth=1、opaque=0。

### 6.3 平面反射（当前）

| 项 | 值 |
|---|---|
| 模式 / 层 | Above only；`cullingMask=Default`（水面=4、草=6、UI、TransparentFX 都不反射） |
| 贴图 | **512×256**；`WaterReflections.ResolutionY` 是工程补丁，0 表示沿用方形 `Resolution` |
| 管线 | `Assets/Settings/ReflectionRenderer.asset`（`m_RendererFeatures=[]`、`m_IntermediateTextureMode=0`/Auto）作为 URP 的 **renderer index 1**；反射相机直接渲染到 512×256 RT，**无 intermediate color / final blit**。依赖 URP 补丁 `RenderSingleCameraInternal` 对 `CameraType.Reflection` 读取 UACD |
| 深度 | `CaptureDepth=false`：反射相机 `requiresDepthTexture=false`，不跑 CopyDepth；`_Crest_ReflectionDepthTexture` 保持清零，`Reflection.hlsl` 的漏光抑制不生效（斜裁剪已挡住水下几何） |
| 开关 | `PlanarReflectionController` 用相机视锥 ∩ `WaterBody` AABB；10m padding，不可见 0.5s 后关；水下关 |
| Enviro | 反射相机走无 Feature 的 ReflectionRenderer（index 1），Enviro 的 `AddRenderPasses` 不会被调用；**不要**用 `EnviroHelper.CanRenderOnCamera` 排除 `CameraType.Reflection`（会误伤 Enviro 自己的全局反射探针）。该隔离依赖 URP 补丁；补丁丢失时反射相机退回 renderer 0，Enviro 会重新参与且 `PlanarReflectionVerify` FAIL |
| 草 | `GrassBRGManager` 的 BRG layer 改为 6；`PlanarReflectionController` 把反射相机登记到 `GrassBRGManager.ReflectionCamera`，剔除回调按相机位置比对（1cm 容差）后跳过，避免白跑 4.29M 实例的 cull job；容差不能放大到米级，主相机贴近水面时会被误判 |
| 材质 | `Water.mat` 的 `_Crest_PlanarReflectionsEnabled=1`；Intensity/Roughness/Distortion 用默认 1 |
| 其他 | `Sky=true`、`DisableShadows=true`、`DisablePixelLights=true`、`Overscan=1.25`、`RenderOnlySingleCamera=true`、每帧刷新；`renderShadows=false` 后仅剩 URP 的空 shadowmap 绑定 pass（无 shadow caster 绘制） |
| 仍未排除 | 200 人 VAT 人群在 Default 层，会进入反射；要排除需给 SubScene prefab 加独立层并重烘（当前未做） |

**Crest 源码补丁**：反射贴图从方形改为可非方形（`_ResolutionY` + `ResolutionY`，`ComputeHorizonPositionAndNormal` 的 NDC offset 改 `Vector2`）；新增 `_CaptureDepth` / `CaptureDepth` 关闭反射相机深度拷贝。不要额外暴露 `RefreshInterval`（`_RefreshPerFrames` 默认即 1）。升级 Crest 必须重打；回归 `PlanarReflectionVerify`。

**URP 源码补丁**：`Runtime/UniversalRenderPipeline.cs` 的 `RenderSingleCameraInternal` 对 `CameraType.Reflection` 也读取 `UniversalAdditionalCameraData`。不打补丁时反射相机看不到 RendererIndex/CaptureDepth/renderShadows/post，会退回默认 renderer（CopyDepth + FinalBlit）。升级 URP 必须重打，见 [URP AGENTS](../Packages/com.unity.render-pipelines.universal/AGENTS.md) §3。

补丁后的反射 pass 队列（2026-11-19 实测）：`MainLightShadowCasterPass DrawObjectsPass DrawSkyboxPass DrawObjectsPass InvokeOnRenderObjectCallbackPass`；无 `CopyDepthPass`、无 `FinalBlitPass`。

### 6.4 雨纹补丁（骨架）

- `Runtime/Shaders/Surface/RainRipples.hlsl` + `Fragment.hlsl` 调用：全局雨纹法线叠加到水面法线，只在 `!i_Underwater` 生效。
- 参数：`_Crest_RainRippleNormal/Intensity/Scale/Speed`；**0 新 pass、+1 次水面纹理采样、无新 shader 变体**。
- 雨纹贴图必须是可平铺 Normal map；采样用 `sampler_LinearRepeat`（`GlobalSamplers.hlsl`），不改材质 keyword。
- 控制器 `Gameplay/RainRippleController.cs`：Enviro `wetness` → intensity；贴图为空时 intensity=0。
- 当前状态：补丁已加；`RainRippleController` 已挂 `Crest Lake`，临时雨纹 `Assets/Textures/Water/RainRipples_Temp.png` 已赋；`AutoFromEnviro=true`，晴天 intensity=0；参数 `Scale=2 / Speed=0.05 / MaxIntensity=0.7`。
- 雨纹单独一层视觉偏弱：同一帧开/关平均通道差约 0.4/255（13% 像素差 >2）。完整雨感还需叠加泡沫/涟漪贴花或降低水面 smoothness。
- 升级 Crest 必须重打，见 [包 AGENTS](../Packages/com.waveharmonic.crest/AGENTS.md) §4.1。

## 7. 验收

| 脚本 | 判什么 | 条件 |
|---|---|---|
| `LakeWaterVerify.Run` | 浅水见底、水下吸收色调、水下有内容、开关对照、水上不误开 | 需真实图形设备 |
| `LakeOrbitVerify.Run` | 轨道半径/地形跟随不穿模/朝向 + Crest/SECTR/tag 跟随 + 切换 | 可 `-nographics` |
| `LakeRenderVerify.Run` | 水位、水面 chunk、水体色、画面内容 | 需真实图形设备；Quad 下「水面 chunk」一项预期 FAIL，只看水体色 |
| `PlanarReflectionVerify.Run` | 反射相机 512×256、反射开/关像素差、相机停用后自动关反射 | 需真实图形设备 |
| `PlanarReflectionSetup.Report` | 反射配置 / 层 / controller / 材质属性只读校验 | 可 `-nographics` |
| `RainRippleVerify.Run` | 雨纹补丁结构 + `Crest/Water` 全部 pass 编译 | batchmode；编译的是 D3D11 变体，Vulkan 仍以真机为准 |
| `RainRipplePreview.Run` | **需真实图形设备**：同一帧雨纹开/关截图 + 像素差 | `Logs/RainRipple/rain-ripple-preview-*.png` |
| `LakeCrestSetup.Retune` | 只重写水色，打印 1/e 深度 | 调参入口 |

2026-11-13：`lake-water.txt` 6/6 PASS，`lake-orbit.txt` 9/9 PASS。2026-11-17 起 `LakeOrbitVerify` 预加载湖区 9 个 chunk 跑完整圈（120 帧固定步长 1s），验证地形跟随不穿模。
2026-11-19：`PlanarReflectionSetup.Report` PASS；`PlanarReflectionVerify` PASS（反射相机 512×256、`requiresDepth=false`、`renderShadows=false`、`requiresColor=false`、`post=false`、renderer index 1、pass 队列无 CopyDepth/FinalBlit、开/关像素差 70.7%、相机停用 1s 后 `DisableCount=1`）；`GrassMultiChunkVerify` PASS（layer 6 下 36 个 chunk 都能渲染）。同日删除 Enviro `CanRenderOnCamera` 补丁后复跑，`Report` 与 `PlanarReflectionVerify` 仍 PASS（`rendererIndex1=True`，Enviro 隔离由 renderer index 1 承担）。

**验收三个坑**：
- batchmode 下 `Time.deltaTime` 不推进：用 `LakeOrbitCamera.OverrideDeltaTime` 注入固定步长。
- 水下色调不能用绝对值：湖 16m 深，碗底占屏幕大半，必须用同机位开/关水下效果的差值。
- 验收机位不能站岸上：植被只清到 R+8，岸外会落在树里。

## 8. 踩坑速查

1. **unitypackage 必须连 `.meta` 一起解**，否则 GUID 全断（20+ 编译错误）。
2. **重转已挂 mesh 的 chunk 前先删 `GTS_Mesh` 节点**，否则 GTS 对已有 `GTSMesh` 取 `GetComponent<Terrain>()` 崩；转换必须一进程一块（见 [Assets/Editor/Gts/AGENTS.md](../Assets/Editor/Gts/AGENTS.md) §2.1）。
3. **植被清除只用半径判据**，不要加 `h < 水位`：世界边缘大片低于水位，会误删 1km 外的草。
4. **`WaterRenderer.SeaLevel` 编辑期恒为 0**（读 internal `Position`）；校验水位看 `transform.position.y`。
5. **`Lod.Enabled` 编辑期必为 false**（getter 是 `_Enabled && _Valid`，`_Valid` 要运行时 GPU 初始化）；不能当校验判据，用 `DefaultClippingState` 等持久字段。
6. **Absorption/Scattering 默认关闭**；材质必须显式写 `_Crest_AbsorptionColor` / `_Crest_Absorption` / `_Crest_Scattering`，否则水面全黑（批处理下没人触发 `UpdateAbsorptionFromColor`）。
7. **验收前禁 `PegasusTourController`**：它每帧改相机位姿，会覆盖验收机位。
8. **验收补两件事**：`RenderSettings.ambientMode = Flat`（未烘 GI，探针全黑）；对每个 `GTSMesh` 调 `Refresh()`（否则地表黑）。
9. **SECTR 异步加载不确定**：验收前把相关 chunk 全部 Additive 打开，或等 Terrain 覆盖站位点。

## 9. 遗留

- [ ] iQOO 16 实测帧率；平面反射的显存/带宽与 Underwater 取舍在真机定。
- [ ] 钓鱼玩法需要 CPU 波高查询时，开 `com.waveharmonic.crest.cpu-queries`。
- [ ] Crest shader 变体未审计（Shader Control 待接入）。
- [ ] 水下体积光权重（见 §4）是 batchmode 定参，真机 HDR/色调映射下复核。
- [ ] 水下生态（礁区/鱼群/海床）真机 30fps 未验；近景 LOD0 约 1.27M，降级与取舍见 [underwater_ecology](underwater_ecology.md) §6、§8。
- [ ] 水面睡莲/浮萍固定在水位 +6cm，不跟随 Crest 浪；无 LOD，真机未验，见 [duckweed](duckweed.md) §6。
- [ ] 临时雨纹贴图待替换为正式资源；雨纹单独一层视觉偏弱，需叠加泡沫/涟漪/粗糙度（见 §6.4）。
- [x] 两台相机已同步后处理（`UniversalAdditionalCameraData.renderPostProcessing=1` + 全局 Volume），见 [postprocess](postprocess.md) §0。
