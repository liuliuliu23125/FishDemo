# 后处理与调色

> 目标 Unity 2022.3 + URP 14。**当前主场景没有 Volume，也没有启用任何后处理**；
> 本文只保留接入规则与选型结论。

## 0. 接入规则（2026-11-17）

当前资产：`m_SupportsHDR=1`、`m_ColorGradingMode=0`（LDR grading）、`m_ColorGradingLutSize=32`。

1. **只保留 URP 内置一套**，用全局 Volume 控制；不引入 Beautify 等第二套调色系统。
2. **Tonemapping + Color Grading + LUT 在同一个 Uber pass**。ACES 只是 Tonemapping 模式，**不另起全屏 pass**。
3. **LUT 保持 32**，只在参数变化时重建，不要每帧生成。
4. **Bloom 保持 URP 默认降采样链**，不改全分辨率。
5. **水下效果由 Crest 提供**（入水才执行），不再叠一层全屏后处理。
6. 每加一个 Volume 效果，用 Frame Debugger 核对 pass 数与全屏 RT 数；预算 2.5ms。

**对马岛风格落点**：强对比 + 低饱和 + 青橙 + 轻微颗粒，靠 `Color Adjustments + LUT + Bloom + Film Grain` 实现，全部在内置 Uber pass 体系内。

## 1. 选型结论

| 包 | 可用性 | 结论 |
|---|---|---|
| **Fast Mobile Post Processing 1.7** | ❌ 不可用 | 用 `OnRenderImage`（Built-in 回调），URP 下**完全不会执行** |
| **Beautify 3** | ✅ URP 可用 | 功能与内置高度重叠；只有需要 Anamorphic Flares / 高质量 DOF 时才考虑 |
| **Radiant GI** | ⚠️ 谨慎 | 屏幕空间 GI + 时间累积，移动端成本高；SSAO 用内置即可 |

### 为什么不用 Fast Mobile Post Processing

`MobilePostProcessing.cs:147` 是 `void OnRenderImage(...)`。URP 不调用这个回调，整包静默失效；且用 `Screen.width` 而非相机 target 尺寸。要改等于重写。

### 为什么不默认用 Beautify

标准 `ScriptableRendererFeature` 集成没问题，但 LUT/Bloom/Vignette/Film Grain/DOF 都与 URP 内置重叠。引入后是**两套后处理共存**，容易重复计算、pass 顺序不可控。内置 Uber pass 已针对移动端合并，零额外依赖。

### 为什么暂不用 Radiant GI

屏幕空间 raycast + 时间累积需要历史帧缓冲/motion vector，移动端易拖影，且与内置 SSAO 定位重叠。优先级低于水体/植被/大气。

## 2. 移动端注意点

- TBDR 下带宽比算力更贵：每次全屏 blit 都是一次 tile load+store；ping-pong 翻倍。
- URP 的 Color Grading + Tonemapping 已合并在一次 Uber pass；Bloom 用降采样链。
- `m_ColorGradingMode=0` 是 LDR grading。若 ACES 需要更高精度，可实验切到 HDR（1），但会增加 LUT 成本，必须实测。

## 3. 待验证

- 接入后处理的 pass 数 / GPU 耗时（iQOO 16 实机）；当前无数据。
- URP 内置 32 级 LUT 精度是否够；不够再评估，不要先换第三方。
- ACES 下 LDR/HDR grading 的取舍。
