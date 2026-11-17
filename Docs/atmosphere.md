# 大气系统选型分析（昼夜 / 天气 / 体积雾 / 体积光）

> **本文档 §0 是 2026-11-15 的实际落地状态；§1 起是接入前的选型分析，保留作为决策依据。**
> §1 里“四包组合”的结论已被 §0 取代：先只上 Enviro，另外三个包等有具体对象/场景需求再接。

## 0. 当前落地状态（2026-11-15）

### 0.1 实际选型：Enviro 3 一个包覆盖四项

| 需求 | 实现 | 为什么当前不需要第二个包 |
|---|---|---|
| 昼夜 | Enviro `Time/Lighting/Sky`：太阳/月亮方向、颜色、强度、环境光、星空 | 主光、环境光、天空盒一站式；场景只有一盏方向光 |
| 天气 | Enviro `Weather`：Clear/Cloudy 1-4/Foggy/Rain/Snow/Storm + `Effects` 雨雪粒子 + 风/湿度/温度 | 9 个预设已够 demo；雨/雪粒子在包内 |
| 体积雾 | Enviro `Fog` 的 `volumetrics`：Low 档 = 1/4 分辨率 raymarch + 双边模糊上采样 | VFM2 的强项是“局部雾体积”，当前湖面没有山谷/海面局部雾需求 |
| 体积光 | Enviro 体积雾 shader **采样主光阴影图**（`EnviroVolumetricsURP.shader`）；树影可形成光柱（本场景湖岸树线稀疏，观感偏柔和，见 §0.6） | VLB2/VL2 需要灯笼/火堆对象；场景里一个都没有，装了也无处挂 |

**未安装**：Volumetric Fog Mist 2 / Volumetric Light Beam 2 / Volumetric Lights 2。
接入条件：出现“局部雾体积（山谷晨雾）”或“灯笼/窗户/火堆”对象时，再按 [§5](#5-体积雾方案对比) / [§6](#6-体积光方案对比) 接入；不要一次性四个包全上，四套 fullscreen pass 的排序与 30fps 风险不可控。

### 0.2 工程接入点

| 文件/资产 | 职责 |
|---|---|
| `Assets/Enviro 3 - Sky and Weather/` | Enviro 3.3.2b 源码（unitypackage 解包，保留 GUID/meta） |
| `Assets/Editor/AtmosphereSetup.cs` | 幂等工具：`EnsureDefines` / `Apply` / `Remove` / `Report` |
| `Assets/Scripts/Runtime/Gameplay/AtmosphereController.cs` | 运行时配置：移动端质量档、时间流速、天气轮换、草风桥接；**左上角 HUD（时刻/天气/流速）与时间热键** |
| `Assets/Editor/AtmosphereVerify.cs` | Play Mode 验收：8 张截图 + 状态断言 |
| `Assets/Settings/Enviro Mobile Quality.asset` | 移动端 EnviroQuality（**唯一质量档来源**） |
| `Assets/Settings/UniversalRenderer.asset` | 子资产 `Enviro Render Feature`（`EnviroURPRenderFeature`） |
| `Assets/Settings/UniversalRP.asset` | `m_RequireDepthTexture = 1` |
| `World.unity` 根节点 `Enviro 3` | EnviroManager + Sun/Moon + Effects/Audio 层级；Enviro 在编辑器 OnEnable 里会自行 Unpack，场景里是普通层级 |
| `World.unity` 根节点 `Atmosphere` | `AtmosphereController` |
| `World.unity` 旧 `Directional Light` | **Light 组件的 `m_Enabled=0`**，保留对象便于回滚；Enviro 自带 Sun/Moon 方向光 |
| `LakeCameraRig.Apply()` | 切相机时同步 `EnviroManager.ChangeCamera`（RenderFeature 只对 manager.Camera 生效） |
| `GrassBRGManager.SetWeatherWind()` | 把 Enviro 的 `windSpeed/windTurbulence` 映射到 7 个草类型材质的 `_WindSpeed/_WindStrength` |

### 0.3 移动端配置（`Enviro Mobile Quality.asset`）

| 项 | 值 | 原因 |
|---|---|---|
| Sky Mode | Simple | 跳过 front/back 多层颜色计算；flat clouds 仍保留 |
| 体积云 | **关** | 默认 128 步 raymarch，与 30fps 预算冲突 |
| Flat clouds | 开，shadow steps 4 | 2D 云，天气预设自带 coverage |
| Fog | 开；`fogQualityMode=Simple`；`volumetrics=true`；`quality=Low`；`steps=32` | Low = 1/4 分辨率 raymarch；Simple height fog 单次 blit |
| Fog maxRange | 600m | 湖面尺度；默认 1000m 无收益 |
| Aurora | 关 | 该纬度/场景不需要 |
| 粒子发射率 | 0.5 | 雨雪粒子减半；仍可见（验收截图有雨丝） |
| 全局反射探针 | **关** | 默认约每 0.6s 渲一次 cubemap；水面反射本来就是关的 |
| 环境探针刷新 | 0.5s（不是每帧） | 默认 `DynamicGI.UpdateEnvironment` 0.1s 一次，移动端过重 |

**代价**：`m_RequireDepthTexture=1` 会多一次 depth prepass。这是体积雾/云的硬依赖，计入 GPU 预算；不接大气系统时可以改回 0。

**运行时覆盖**：低太阳角（`solarTime < 0.6` 且非夜）时，`AtmosphereController` 在
`beginCameraRendering` 里把 `Fog.Settings.scattering` 提到 `_lowSunScattering=0.18`；
否则 Clear 天气预设的 0.045 让体积光几乎不可见。天气混合每帧会把值拉回去，所以必须每帧 pass 前改。

### 0.4 三个接入期踩过的坑（改之前必读）

1. **`ENVIRO_URP` 宏**：Enviro 自带的 `EnviroDefineSymbol` 只加 `ENVIRO_3`。没有 `ENVIRO_URP` 时 `EnviroURPRenderFeature` 整个类型被 `#if` 掉，天空/雾/云**静默失效**。`AtmosphereSetup.EnsureDefines` 会加到 Standalone + Android；改完必须重新编译。
2. **模块必须序列化进场景**：Enviro 的模块（Time/Fog/Weather/...）不是预制体对象，而是编辑期 `EnviroManagerBase.LoadConfiguration()` 从 configuration 资产赋值的引用。`Apply` 如果在同一帧实例化后立刻保存，`Update` 还没跑，存下来的模块字段全是 null；进 Play Mode 后 `StartModules()` 因 null 直接跳过（实测 `Time=False Lighting=False ...` 全 False）。`AtmosphereSetup.Apply` 在保存前显式 `LoadConfiguration()` 并 `SetDirty`。
3. **雾 pass 顺序**：原始 `EnviroURPRenderPass` 是 `BeforeRenderingTransparents - 1`，Crest 水面在 Transparent 队列，画在雾之后 → 雾天湖面完全不受雾影响（实测是一条饱和绿色带）。本项目把该 pass 改到 **`AfterRenderingTransparents`**（补丁在 `EnviroURPRenderPass.cs` 构造函数，注释标了 `[FishDemo]`）。代价：透明表面按背后的不透明深度取雾强度；体积云当前关闭，将来开启时要重新评估顺序。

### 0.5 验收

```
Unity.exe -batchmode -projectPath E:\FishDemo -executeMethod AtmosphereSetup.EnsureDefines
# 重新编译后
Unity.exe -batchmode -projectPath E:\FishDemo -executeMethod AtmosphereSetup.Apply
Unity.exe -batchmode -projectPath E:\FishDemo -executeMethod AtmosphereSetup.Report
Unity.exe -batchmode -projectPath E:\FishDemo -executeMethod AtmosphereVerify.Run
```

`Logs/Atmosphere/atmosphere-verify.txt` 与 8 张玩家视角截图（noon / cloudy / sunset / lowsun / moonrise / midnight / fog / rain）。
2026-11-15 实测全项 PASS，关键数据：

- 正午天空顶部 1/3 平均亮度 0.629，午夜 0.047；日落 17:10 主光 `(0.89,0.50,0.11)`、强度 1.049（暖色）。
- 雾天 `fogDensity=0.809`（晴天 0.019）；雨 `wetnessTarget=1.00`，Rain 粒子 `playing=True`。
- `_EnviroVolumetricFogTex` 非空 → 体积雾 pass 真的执行了。
- 启用的方向光 = 1；旧主光已禁用；depth=1；Renderer feature 已挂。

**没有实机 GPU 数据**：batchmode 下 Profiler 的 draw call/GPU 耗时不可靠。真机 30fps 需按 [performance_budget](performance_budget.md) 用 iQOO 16 校准。

### 0.5.1 运行时操作（HUD / 时间控制）

`AtmosphereController.OnGUI` 在左上角显示：当前时刻（HH:MM + 时段）、天气名、时间流速、热键提示。

| 键 | 作用 |
|---|---|
| `1`–`5` | 切天气：Clear Sky / Cloudy 2 / Rain / Foggy / Storm（带过渡） |
| `T` | 暂停 / 恢复时间流逝（`EnviroTime.Settings.simulate`） |
| `[` / `]` | 时刻 −1h / +1h（暂停时也能用，用于逐时刻观察光色） |
| `\` | 轮换时间流速：20 / 5 / 2 / 1 分钟一天 |
| `C` / `F` | 相机 rig 的切相机 / 自由飞行（见 [water](water.md) §11） |

验收：`AtmosphereTimeVerify.Run`（可 `-nographics`）断言四个时刻设置准确、主光朝向随时间变化、
正午明显亮于午夜、`ShiftTimeByHours` 平移正确、暂停/恢复与流速档生效。
注意 Enviro 在太阳落到地平线以下后会用**月亮**做主光，因此 18:00 与 00:00 的主光方向可能接近，
这是正常行为，不是 bug。

### 0.6 已知限制 / 待办

- **水下**：`AtmosphereController` 在相机低于 Crest `SeaLevel` 时关闭 Enviro `fog/volumetrics`
  （在 `beginCameraRendering` 里按当前相机判断，因为验收脚本会在同一帧内沉相机并 Render），
  交给 Crest 水下效果。但 Enviro 的**主光/环境光仍影响水下**，9:00 暖光下水面偏暖/橄榄色；
  `LakeWaterVerify` 为保持旧判据会临时禁用 Enviro（隔离 Crest），组合观感需真机/美术复核。
- **水面雾是近似**：雾 pass 在透明之后，按水面背后的不透明深度取雾强度；湖面近处仍比岸线略饱和。
- **树的风不跟天气**：草已由 `GrassBRGManager.SetWeatherWind` 桥接；树的 Procedural Worlds shader 用自己的风参数，未接。
- **雨雪粒子密度**：质量档 modifier=0.5；`AtmosphereVerify` 会等到粒子积累后再截图。要更密的雨，改 `particeEmissionRateModifier`。
- **光柱不明显**：Enviro 体积雾 pass 已执行（`_EnviroVolumetricFogTex` 非空），低太阳角时
  `AtmosphereController` 会把 `scattering` 提到 0.18；但本场景湖岸是草坡/稀疏树线，
  体积雾又是 Low（1/4 res + 双边模糊），截图里是柔和逆光而不是清晰光柱。
  要明显光柱：加密树林/近景遮挡，或改用 VLB2/VL2 灯柱；`m_ShadowDistance=50` 也只覆盖近处树影。
- **重跑世界生成会丢**：`AtmosphereSetup.Apply` 必须加进重跑清单；`Enviro 3` / `Atmosphere` 根节点会被重新生成覆盖。
- **Enviro 源码补丁**：升级 Enviro 时 `EnviroURPRenderPass` 的 renderPassEvent 补丁会丢，需重打。

---

> 以下为接入前的选型分析（2026-11-08），保留作为决策依据。

> 分析对象：Enviro 3 3.3.2b / Weather Maker 7.3.6 / Volumetric Fog Mist 2 / Volumetric Light Beam 2.2.9 / Volumetric Lights 2 v20.2.1
> 目标环境：Unity 2022.3.62f2 + URP 14.0.12，Android 旗舰机，30fps

## 结论速览

| 包 | 是否可用 | 定位 | 移动端可行性 | 关键风险 |
|---|---|---|---|---|
| Enviro 3 | ✅ 可用 | 昼夜+天气+雾+云 全套 | ⚠️ 中等，需调参 | 雾走全分辨率多次 blit |
| Weather Maker | ✅ 可用（有前提） | 昼夜+天气+体积云 全套 | ⚠️ 偏重 | 依赖 Compute Shader + texture array |
| Volumetric Fog Mist 2 | ✅ 可用 | 局部体积雾 | ✅ 较友好 | 需控制雾体积数量 |
| Volumetric Light Beam 2 | ✅ 可用 | 局部光锥/光柱 | ✅ 很便宜 | 只适合灯柱类效果 |
| Volumetric Lights 2 | ✅ 可用 | 逐光源体积光 | ⚠️ 成本随光源线性增长 | 每个光源一次 raymarch |

## 1. 版本兼容性核实

### Enviro 3 —— 双路径设计，2022.3 可用

关键证据（`EnviroURPRenderGraph.cs:1`）：

```csharp
#if ENVIRO_URP && UNITY_6000_0_OR_NEWER
using UnityEngine.Rendering.RenderGraphModule;
```

整个 RenderGraph 实现被 `UNITY_6000_0_OR_NEWER` 包住。**2022.3 会自动走 `EnviroURPRenderFeature.cs` + `EnviroURPRenderPass.cs` 的旧路径**。

这证明 Enviro 3 是**为 Unity 6 写了 RenderGraph 版本，同时保留了 2022.3 的 ScriptableRenderPass 路径**，是双版本兼容的。

移动端适配的正面证据（`EnviroVolumetricCloudsModule.cs:1285`）：

```csharp
RenderTextureFormat format = RenderTextureFormat.ARGBFloat;
#if UNITY_IOS || UNITY_ANDROID
    format = RenderTextureFormat.ARGBHalf;
```

有明确的 Android 降精度分支，说明作者考虑过移动端。

### Weather Maker —— 有明确前提

README 原文：

```
For URP Weather Maker requires Unity 2021.2 or newer and URP 11 or newer.
Weather Maker requires texture array support.
Weather Maker does NOT support WebGL, OpenGL, OpenGL ES or DirectX 9.0.
```

**结论**：2022.3 + URP 14 满足版本要求。但：
- **不支持 OpenGL ES** → Android 必须用 **Vulkan**（当前工程 `m_BuildTargetGraphicsAPIs` 是 `Automatic`，需显式配 Vulkan）
- **要求 texture array** → 现代移动 GPU 支持，但要确认目标机型

### 体积雾/体积光三个包
均为标准 `ScriptableRendererFeature` 实现，无版本宏限制：
- `VolumetricFogRenderFeature : ScriptableRendererFeature`（`VolumetricFogRenderFeature.cs:17`）
- Volumetric Lights 2 有 `VolumetricLightsRenderFeatureEditor`，确认是 RendererFeature
- Volumetric Light Beam 有独立的 `ShaderSpecificURP.cginc`，确认 URP 支持

## 2. Enviro 3 分析

**雾的实现**（`EnviroURPRenderPass.cs:214-260`）：

```csharp
RenderTexture sourceTemp = RenderTexture.GetTemporary(renderingData.cameraData.cameraTargetDescriptor);
// ...
RenderTexture temp1 = RenderTexture.GetTemporary(renderingData.cameraData.cameraTargetDescriptor);
// ...
RenderTexture temp2 = RenderTexture.GetTemporary(renderingData.cameraData.cameraTargetDescriptor);
```

**这是关键性能问题**：雾在**全分辨率** `cameraTargetDescriptor` 下做临时 RT 分配和多次 blit。移动端 TBDR 架构下，每次全屏 RT 的 store/load 都是带宽杀手。

配合 `EnviroBilateralBlur.shader`（存在双边模糊），说明它想做高质量上采样，但**基础分辨率没有降**。

**雾的可调参数**（`EnviroFogModule.cs:22`）：

```csharp
public int steps = 32;
public Quality quality;   // Low / Medium / High
```

默认 32 步，偏高。移动端建议降到 16 或更低，并配合 Low 质量档。

**体积云**：ARGBHalf（Android），有降采样，但仍是 raymarch，成本不低。

**评价**：功能最全（昼夜+天气+雾+云+极光+闪电+音频），一站式。但**雾的实现方式对移动端不够友好**，需要自己改造或接受开销。

## 3. Weather Maker 分析

**优势**：体积云质量高，天气系统完整（雨雪冰雹），有 `DownSampleScale` 枚举（Half/Quarter）。

**劣势**：
1. **强依赖 Compute Shader**（`WeatherMakerAtmosphereProfileScript.cs:233`）：
   ```csharp
   !SystemInfo.supportsComputeShaders
   ```
   大气 LUT 计算需要 compute。移动端支持 compute，但性能和兼容性有差异。

2. **依赖 asyncGPUReadback**（`WeatherMakerFullScreenCloudsScript.cs:800`）用于云探测，移动端支持但延迟高。

3. **代码体量大**：889 个资源，包含水、音频、特效等大量与需求无关的模块。

**评价**：如果只要昼夜+天气，它偏重；如果想要高体积云质量，它是选项。但与 Enviro 二选一即可，不需要两个都上。

## 4. Enviro 3 vs Weather Maker

| 维度 | Enviro 3 | Weather Maker |
|---|---|---|
| 昼夜 | ✅ | ✅ |
| 天气 | ✅ | ✅（更细，含冰雹） |
| 体积云 | ✅（ARGBHalf on Android） | ✅（质量更高，可降采样） |
| 雾 | ⚠️ 全分辨率多 blit | ⚠️ 有降采样选项 |
| Compute 依赖 | 少 | **强** |
| 代码体量 | 350 资源 | 889 资源 |
| 2022.3 路径 | 有旧路径 fallback | URP 11+ 原生支持 |

**推荐：Enviro 3**。理由：
1. 有明确的 2022.3 兼容路径（不依赖 RenderGraph）。
2. 有 Android 降精度分支。
3. 体量更小，耦合更少。
4. 它的雾虽然全分辨率，但至少能改；Weather Maker 的 compute 依赖是结构性的。

## 5. 体积雾方案对比

### Volumetric Fog Mist 2 —— 推荐作为补充

实现质量最高，专为性能设计：

```csharp
// VolumetricFogManager.cs:56-62
[Tooltip("Uses higher resolution intermediate buffers and edge-aware upsampling filter")]
public bool scatteringHighQuality;
[Range(1, 8)] public float downscaling = 1;
[Tooltip("Depth-based detection threshold for the upscaling reconstruction filter")]
public float downscalingEdgeDepthThreshold = 0.05f;
```

- ✅ `downscaling` 1-8 可调
- ✅ 深度感知上采样（edge-aware）
- ✅ 抖动（dithering）
- ✅ `MAX_ITERATIONS` 编译期可调（`CommonsURP.hlsl:9`）

raycast 循环有提前退出（`Raymarch2D.cginc:312`）：

```glsl
if (sum.a > 0.99) {
    break;
}
```

**评价**：这是三个体积雾/光包里工程质量最好的。适合做**局部雾体积**（山谷、海面低雾）。

### 与 Enviro 内置雾的分工建议

- **全局大气雾** → Enviro（跟随昼夜/天气变化）
- **局部雾体积**（山谷晨雾、海面水汽）→ Volumetric Fog Mist 2

两者可以共存，但要注意：都读 depth、都做 fullscreen blit，**pass 顺序必须协调**，否则会互相覆盖。

## 6. 体积光方案对比

### Volumetric Light Beam 2 —— 解析式，最便宜

不是 raymarch，是**基于 cone 几何的解析计算**（`VolumetricLightBeamSharedSD.cginc`）：

```glsl
float2 coneSlopeCosSin = VLB_GET_PROP(_ConeSlopeCosSin);
float3 normalObjectSpace = (float3(cosSinFlat.x * coneSlopeCosSin.x, ...));
```

成本 = 一次透明 draw，与"光线穿过的距离"无关。

**适用**：灯笼、窗户、灯塔射出的光柱。**不适合**：大面积体积光（如阳光穿过树林）。

### Volumetric Lights 2 —— 逐光源 raymarch

```csharp
// VolumetricLight.Settings.cs:21-27
[Range(1, 256)] public int raymarchQuality = 8;
public float raymarchMinStep = 0.1f;
public int raymarchMaxSteps = 200;
public float jittering = 0.5f;
```

**成本随光源数量线性增长**。默认 8 质量、200 最大步数。

**适用**：少量关键光源的体积光（火堆、主光）。

### 推荐组合

| 效果 | 方案 |
|---|---|
| 灯笼/窗户光柱 | Volumetric Light Beam 2（便宜） |
| 火堆/关键点光源体积光 | Volumetric Lights 2（限 2-3 个） |
| 全局大气透视 | Enviro 雾 |
| 局部低雾 | Volumetric Fog Mist 2 |

**夜景光源预算**：Volumetric Lights 2 控制在 2-3 个，其余用 Volumetric Light Beam + emissive。

## 7. 性能预估与质量档位建议

| 效果 | 高画质 | 中画质 | 低画质 |
|---|---|---|---|
| Enviro 雾 steps | 32 | 16 | 8 |
| Enviro 体积云 | 开 | 降采样 | 关（用 2D 云） |
| Fog Mist 2 downscaling | 2 | 4 | 关 |
| Volumetric Lights 2 数量 | 3 | 2 | 0 |
| Volumetric Light Beam | 全开 | 全开 | 减半 |

## 8. 与自研 URP pass 的冲突风险

**必须协调的 pass 顺序**（从早到晚）：

```
1. Depth prepass
2. Enviro 天空 / 雾          ← RenderPassEvent.BeforeRenderingTransparents - 1
3. Volumetric Fog Mist 2     ← 需要 depth
4. 水面（不透明部分）
5. 不透明物体
6. Volumetric Lights 2       ← 透明队列
7. Volumetric Light Beam     ← 透明队列
8. 水面（透明部分）
9. 后处理
```

**冲突点**：
1. Enviro 的 `renderPassEvent = BeforeRenderingTransparents - 1` 已经在透明之前，Fog Mist 2 如果也在类似位置，顺序不确定。
2. 三者都读 depth → 必须确保 depth 已生成（`m_RequireDepthTexture = 1`）。
   > **已执行（2026-11-15）**：`UniversalRP.asset` 已改为 1。
   > `m_RequireOpaqueTexture` 仍为 0——Enviro 只用 depth，不用 opaque texture。
   > Crest SimpleTransparency 不受影响（它不依赖 camera depth）。
3. 三者都做 fullscreen blit → 应该尽量合并，或至少避免 ping-pong 过多。

## 9. 推荐方案组合

```
昼夜 + 天气 + 全局大气雾 + 体积云  →  Enviro 3
局部雾体积（山谷/海面）            →  Volumetric Fog Mist 2
灯笼/窗户光柱                      →  Volumetric Light Beam 2
关键点光源体积光（≤3 个）           →  Volumetric Lights 2
```

**不推荐**：Enviro 3 + Weather Maker 同时使用（功能重叠，pass 冲突，包体翻倍）。

## 待验证项（2026-11-15 更新）

- ✅ Enviro 3 在 2022.3 的旧路径完整：已编译并实跑（`AtmosphereVerify` PASS）。
- ✅ Enviro 雾可降档：Low 档为 1/4 分辨率 raymarch + 双边模糊上采样（`EnviroFogModule.cs`）。
- ⏳ Weather Maker 在 Vulkan 移动端的表现：未导入，当前不需要（Enviro 覆盖需求）。
- ✅ pass 顺序：Enviro 一个 pass，已与 Crest 水面协调（移到透明之后）；Fog Mist 2/VL2 未接入，暂无需协调。
- ⏳ 真机 iQOO 16 的 GPU 耗时与 30fps 稳定性：待实机 Profile。
