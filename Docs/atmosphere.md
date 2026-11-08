# 大气系统选型分析（昼夜 / 天气 / 体积雾 / 体积光）

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
3. 三者都做 fullscreen blit → 应该尽量合并，或至少避免 ping-pong 过多。

## 9. 推荐方案组合

```
昼夜 + 天气 + 全局大气雾 + 体积云  →  Enviro 3
局部雾体积（山谷/海面）            →  Volumetric Fog Mist 2
灯笼/窗户光柱                      →  Volumetric Light Beam 2
关键点光源体积光（≤3 个）           →  Volumetric Lights 2
```

**不推荐**：Enviro 3 + Weather Maker 同时使用（功能重叠，pass 冲突，包体翻倍）。

## 待验证项（需要导入工程后才能确认）

- Enviro 3 在 2022.3 的旧路径是否真的完整（RenderGraph 分支之外的部分）
- Enviro 雾能否改成半分辨率（源码层面）
- Weather Maker 在 Vulkan 移动端的实际表现
- 三个包的 pass 顺序能否协调
