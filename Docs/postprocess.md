# 后处理与调色选型分析

> 分析对象：Fast Mobile Post Processing 1.7 / Beautify 3 30.2 / Radiant GI 32.3
> 目标环境：Unity 2022.3.62f2 + URP 14.0.12，Android 旗舰机，30fps

## 结论速览

| 包 | 是否可用 | 定位 | 结论 |
|---|---|---|---|
| Fast Mobile Post Processing 1.7 | ❌ **不可用** | 移动端后处理 | 用 `OnRenderImage`，URP 下不生效 |
| Beautify 3 | ✅ 可用 | 大而全后处理 | 功能重叠 URP 内置，按需取用 |
| Radiant GI | ⚠️ 谨慎 | 屏幕空间 GI | 移动端成本高，非必需 |

## 1. 版本兼容性核实

### Fast Mobile Post Processing —— 不可用

**决定性证据**（`MobilePostProcessing.cs:147`）：

```csharp
void OnRenderImage(RenderTexture source, RenderTexture destination)
```

`OnRenderImage` 是 **Built-in 渲染管线**的回调。**在 URP 下这个函数完全不会被调用**，整个效果静默失效。

包名里的 "LWRP" 是 URP 的旧名（Lightweight RP），但这个包显然只是当年适配了 LWRP，没有迁移到 URP 14 的 `ScriptableRendererFeature` 体系。

其他问题：
```csharp
blurTex = RenderTexture.GetTemporary(Screen.width / 2, Screen.height / 2, ...);
```
用 `Screen.width` 而非相机 render target 尺寸，在高 DPI / 分屏 / XR 下会出错。

**结论：不可用。** 如果一定要用，需要自己重写成 RendererFeature，工作量约等于重写。

### Beautify 3 —— 可用

**证据**（`BeautifyRendererFeature.cs:199`）：

```csharp
public partial class BeautifyRendererFeature : ScriptableRendererFeature
```

标准 URP 集成，且正确处理了 depth（`BeautifyRendererFeature.cs:428`）：

```csharp
builder.UseTexture(resourceData.activeDepthTexture, AccessFlags.Read);
ConfigureInput(ScriptableRenderPassInput.Depth);
```

**结论：可用。**

## 2. Beautify 3 分析

**功能面**（`Beautify.cs:57-63`）：

```csharp
public class LUT : SettingsGroup { }
public class Bloom : SettingsGroup { }
public class AnamorphicFlares : SettingsGroup { }
public class ChromaticAberration : SettingsGroup { }
public class DepthOfField : SettingsGroup { }
public class EyeAdaptation : SettingsGroup { }
```

大而全：LUT、Bloom、变形宽银幕光晕、色差、景深、眼适应。

**优势**：
- 有 `BeautifyOptCompiler.cs`（编译期优化器），可以把不用的功能编译掉 → 关闭的功能零开销。
- LUT 支持混合（`LUTBlending.cs` 在 Demo 里）。

**劣势**：
- 功能与 URP 内置高度重叠（Bloom、LUT、Vignette、Film Grain 内置都有）。
- 引入它意味着**两套后处理系统共存**，容易重复计算。

**结论**：只有在需要 **Anamorphic Flares（变形光晕）** 或 **高质量 DOF** 时才有引入价值。如果只要 LUT + Bloom + 调色，**URP 内置完全够用**。

## 3. 与 URP 14 内置后处理的对比

| 能力 | URP 14 内置 | Beautify 3 | 需要第三方？ |
|---|---|---|---|
| Tonemapping | ✅ ACES/Neutral | ✅ | ❌ 不需要 |
| Color Grading LUT | ✅ | ✅ | ❌ 不需要 |
| Bloom | ✅ | ✅（更可调） | ⚠️ 内置够用 |
| Vignette | ✅ | ✅ | ❌ 不需要 |
| Film Grain | ✅ | ✅ | ❌ 不需要 |
| DOF | ✅（Bokeh） | ✅（更高质量） | ⚠️ 看需求 |
| Anamorphic Flares | ❌ | ✅ | ✅ 需要时 |
| 色差 | ❌ | ✅ | ⚠️ 可后处理实现 |
| Sharpen | ❌ | ✅ | ⚠️ 可自研 |

**核心判断**：对马岛风格 = 强对比 + 低饱和 + 青橙 + 轻微颗粒。这些 **URP 内置的 Color Adjustments + LUT + Bloom + Film Grain 就能做到**，不需要 Beautify。

**建议**：
- **默认用 URP 内置**（零额外依赖，零额外 pass）。
- 如果后期发现内置 LUT 精度不够，或需要 Anamorphic Flares，再引入 Beautify。

## 4. Radiant GI 评估

**技术路线**：屏幕空间 GI + 反射阴影图（`RadiantGlobalIllumination.cs:20-24`）：

```csharp
Raycast = 20,
RaycastAccumulated = 21,
Downscaled = 30,
ReflectiveShadowMap = 40,
TemporalAccumulationBuffer = 60,
```

有 `rayBounce`（额外一次弹射）、`ReflectiveShadowMap`、`TemporalAccumulationBuffer`。

**移动端评估**：
- 屏幕空间 raycast + 时间累积 = 需要历史帧缓冲 + motion vector，成本不低。
- 时间累积在移动端容易产生拖影（尤其在快速移动的钓鱼视角）。
- 与 URP 内置 SSAO 定位重叠。

**结论**：非必需。如果只是要环境光遮蔽，URP 内置 SSAO 足够。Radiant GI 的价值在于"彩色间接光反弹"，这对风格化画面有帮助，但**优先级低于水体、植被、大气**。

## 5. 移动端性能分析（TBDR 视角）

移动 GPU 是 TBDR（Tile-Based Deferred Rendering），关键约束是**带宽**而非算力：

1. **每次 fullscreen blit = 一次 tile 的 load + store**。后处理 pass 数越多，带宽越大。
2. **ping-pong（A→B→A）代价翻倍**。应尽量合并成单 pass 多效果。
3. **`RenderTexture.GetTemporary` 全分辨率**是昂贵的。

**Beautify 的问题**：它的多效果默认是分开的 pass。虽然有 `BeautifyOptCompiler` 可裁剪，但仍需确认关闭的效果是否真的零开销。

**URP 内置的优势**：Color Grading + Tonemapping 合并在一次 Uber pass 里，Bloom 用降采样链。已经针对移动端优化过。

## 6. 推荐方案

```
调色 / Bloom / Vignette / Film Grain  →  URP 内置 Volume（默认方案）
LUT                                  →  URP 内置 Color Lookup
需要 Anamorphic Flares 时             →  再引入 Beautify 3
Fast Mobile Post Processing          →  不使用（URP 下失效）
Radiant GI                           →  暂不使用（优先级低）
```

**理由**：
1. URP 内置后处理已经覆盖 90% 需求，且经过移动端优化。
2. 减少一个第三方包 = 减少一层 shader 变体、一层 pass 冲突风险、一层升级负担。
3. 视觉风格靠 **LUT + 光照**实现，不靠后处理堆叠。

## 待验证项

- Beautify 3 在 URP 14 的 pass 顺序是否会与 Enviro/水体冲突
- URP 内置 LUT 的精度是否足够（32 级 vs 更高）
