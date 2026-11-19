# 后处理与调色

> 目标 Unity 2022.3 + URP 14。当前主场景已有全局后处理；本文记录现状、参数、性能账与验收。

## 0. 当前状态（2026-11-18）

### 0.1 接入点

| 资产/文件 | 作用 |
|---|---|
| `World.unity` 根节点 `Post Processing` | 唯一全局 `Volume`（priority 0、weight 1） |
| `Assets/Settings/PostProcess/FishDemo Global Volume.asset` | 唯一后处理 Profile |
| `Assets/Settings/UniversalRP.asset` | `m_ColorGradingMode=1`（**HDR grading**）、`m_ColorGradingLutSize=32`、`m_SupportsHDR=1` |
| `Main Camera` / `Lake Orbit Camera` | `UniversalAdditionalCameraData.renderPostProcessing=1`、`dithering=1`、`volumeLayerMask=Default` |
| `AtmosphereController` | 克隆 Volume profile，按 `Enviro.isNight` 做夜景曝光/冷色补偿；切换相机不重建 |

**唯一后处理**：不引入 Beautify 等第二套；所有效果在 URP 内置 Uber pass 体系内。

### 0.2 参数

| 效果 | 值 | 说明 |
|---|---|---|
| Tonemapping | **ACES** | 只是 Uber 内的 tonemap 模式，不另起 pass |
| Color Adjustments | 0.35EV / 对比 +10 / 饱和 -8 / 滤镜 (1, 0.99, 0.96) | 白天基线 |
| Color Curves | master S 曲线 (0,0)-(0.25,0.24)-(0.5,0.54)-(0.75,0.8)-(1,1) | 抬中间调对比，不抬黑 |
| Color Curves Hue vs Hue | 橙红 hue 0.06–0.12 偏移 -0.022…-0.018，峰值在 0.09 约 -0.028（-10°）；黄 hue 0.16 保持 0.5 | 近似 GOT 的 ACEScg 红基色，减少 ACES 红→黄 |
| Color Curves Hue vs Sat | 红橙 hue 0.04–0.12 的饱和倍率 1.08–1.12；黄保持 1.0 | 补偿 ACES 高光去饱和 |
| White Balance | 温度 +10 / tint -3 | 暖调 |
| Shadows/Midtones/Highlights | 暗部 (0.85,0.95,1.12)、中间调中性、高光 (1.12,1,0.85) | 冷暗部 + 暖高光 |
| Bloom | 阈值 0.85 / 强度 0.4 / scatter 0.65 / tint (1,0.9,0.78) / Half/6 默认，`B` 循环 Half/6 → Half/3 → Quarter/3 / HQ 关 | 火把与星光的主要来源；H 模糊 9→5 tap（双线性等价） |
| Vignette | 0.22 / 平滑 0.45 | 电影感 |
| Film Grain | Medium1 / 0.12 / response 0.8 | 轻微颗粒 |

**夜景补偿**（`AtmosphereController`，2.5s 过渡）：

| 参数 | 白天 | 夜晚 |
|---|---|---|
| postExposure | 0.35EV | **+1.8EV（2.15EV）** |
| contrast | 10 | 8 |
| saturation | -8 | -18 |
| colorFilter | (1,0.99,0.96) | (0.78,0.88,1.08) 冷蓝 |

原因：Enviro 的夜空是显示参考色，ACES 会把低值压到近黑（实测午夜顶部亮度 0.047 → 0.001，色数 35）。+1.8EV + 冷色滤镜把午夜恢复到可见（顶部 0.005、约 120 色），白天保持原曝光。这不是物理眼适应。验证脚本用 `SnapNightExposure()` 立即套用。

### 0.3 对马岛风格落点

强对比 + 低饱和 + 冷暗部/暖高光 + 轻颗粒；夜晚靠冷蓝环境与火把暖光形成互补。落点全部在 Uber 的 Color Adjustments / Curves / SMH / Bloom / Vignette / Grain 内，不引入额外全屏 pass。

### 0.4 Pass / RT / Blit 账

| 项 | 次数 | 分辨率 / RT |
|---|---|---|
| Uber pass（ACES + HDR LUT + 调色 + 曲线 + SMH + Vignette + Grain） | 1 | 全屏 1 个 |
| Bloom Half/6（默认） | 1 预滤波 + 2×5 降采样 + 5 升采样 = 16 blit | half-res 金字塔 6 级，12 张 half-res RT；无 full-res RT |
| Bloom Half/3（`B`） | 1 预滤波 + 2×2 降采样 + 2 升采样 = 7 blit | half-res 金字塔 3 级，6 张 half-res RT；无 full-res RT |
| Bloom Quarter/3（`B`） | 1 预滤波 + 2×2 降采样 + 2 升采样 = 7 blit | quarter-res 金字塔 3 级，6 张 RT；无 full-res RT |
| HDR LUT | 32²×32，参数变化才重建 | 夜景过渡期间每帧变化会重建，约 2.5s/次；之后稳定 |

**Bloom 采样**：H 模糊由 9 tap 改为 **5 tap 双线性等价**（与 V 相同的 5-tap 核，保持原高斯半径/形状）；
非 HQ 时预滤波 1 tap、升采样 2 tap（high+low）；HQ 关闭。每级 H+V 约 10 个纹理采样（原 14）；
Half/6 总计约 61 次（原 81，-25%），Half/3 与 Quarter/3 约 25 次。
High 档与优化前截图对比：午夜/日落平均像素差 0.12/0.33，夜空 `>2` 差异像素 0.2%；
火把近景 1.73（含粒子相位差），说明 5-tap H 基本保持原观感。
**不做 DOF**：管线不新增 DOF 的 blit / 临时 RT，也不依赖 `_CameraDepthTexture`。
**低画质档**：`B` 循环到 Bloom Half/3 或 Quarter/3（7 blit / 25 采样）；再关 Bloom；
HDR→LDR grading 不可为了省一点而牺牲夜色，改回前必须重跑夜景截图。

### 0.5 验收

```
Unity.exe -batchmode -projectPath E:\FishDemo -executeMethod LightingVerify.Run
Unity.exe -batchmode -projectPath E:\FishDemo -executeMethod AtmosphereVerify.Run
```

2026-11-18：LightingVerify PASS（ACES/Bloom/HDR grading/无 DOF/两相机/12 火把/6 灯上限/夜景曝光自适应）；
AtmosphereVerify PASS（正午顶 0.585、午夜顶 0.005、午夜约 120 色，色数阈值 100）。
截图：`Logs/Lighting/lighting-*.png`、`Logs/Atmosphere/atmosphere-*.png`。

## 1. 选型结论

| 包 | 可用性 | 结论 |
|---|---|---|
| **Fast Mobile Post Processing 1.7** | ❌ 不可用 | 用 `OnRenderImage`（Built-in 回调），URP 下**完全不会执行** |
| **Beautify 3 (30.2.1)** | ✅ URP 可用（Renderer Feature + Volume override） | 20+ 效果单 pass，但与内置 ACES/LUT/Bloom/DOF/Vignette/Grain 高度重叠；独有主要是 Anamorphic/Sun Lens Flares、Advanced Outline、AA、Night/Thermal。**当前不需要**，见下节评估 |
| **Radiant GI** | ⚠️ 谨慎 | 屏幕空间 GI + 时间累积，移动端成本高；SSAO 用内置即可 |

### 为什么不用 Fast Mobile Post Processing

`MobilePostProcessing.cs:147` 是 `void OnRenderImage(...)`。URP 不调用这个回调，整包静默失效；且用 `Screen.width` 而非相机 target 尺寸。要改等于重写。

### 为什么不默认用 Beautify

标准 `ScriptableRendererFeature` 集成没问题，但 ACES/AGX、LUT、Bloom、Vignette、Film Grain 都与 URP 内置重叠。引入后是**两套后处理共存**，容易重复计算、pass 顺序不可控；若用它的 “Ignore Post Processing” 模式省 blit，又会关掉 URP 后处理，导致现有 Volume（HDR grading、Color Curves、SMH、夜景曝光）全部失效，需要整体迁移。内置 Uber pass 已针对移动端合并，零额外依赖。

### Beautify 3 30.2.1 能力评估（2026-11-18）

版本事实（Kronnect 产品页 / URP Setup 文档）：Unity 2022.3+、URP 版是 **Renderer Feature + Volume override**，
支持 “Ignore Post Processing”（移动端推荐，但会绕过 URP 后处理），20+ 效果集成在单 pass。

| Beautify 能力 | 当前是否用得上 | 说明 |
|---|---|---|
| Bloom | ❌ 重复 | 已有 URP Bloom half-res |
| Depth of Field (Bokeh) | ❌ 不需要 | 已按要求移除 DOF |
| ACES / AGX / LUT | ❌ 重复 | 已有 ACES + HDR grading LUT |
| Vignette / Film Grain / CA | ❌ 重复/次要 | 现有 Uber pass 已覆盖 |
| **Anamorphic Flares** | ⚠️ 可选 | 电影感横向拉丝；GOT 风格不是必需，且要额外全屏栈 |
| **Sun Lens Flares（带遮挡）** | ⚠️ 可选 | 日落湖面可能出效果；更便宜的做法是专用 sprite/flare，而非整套后处理 |
| Eye Adaptation | ❌ 不建议 | 自动曝光会在环湖/看火把时 pumping；现有 `isNight` + 2.5s 固定补偿更可控 |
| Purkinje | ⚠️ 已有近似 | 夜景 colorFilter 已做冷蓝补偿；Beautify 的更物理，但不值得为它引入第二套 |
| Advanced Outline / AA | ❌ 不需要 | 写实钓鱼场景无描边需求；AA 可走 URP MSAA/SMAA |
| Night/Thermal Vision / Frame / Pixelate | ❌ 不需要 | 与项目风格无关 |

结论：**现在不需要引入 Beautify 3**。核心 GOT 调色/ACES/Bloom/夜景曝光已在 URP 单栈内完成；
只有将来明确需要 anamorphic/sun lens flare 时再评估，且优先做专用 cheap flare，不要为此把整套后处理换成 Beautify。

### 为什么暂不用 Radiant GI

屏幕空间 raycast + 时间累积需要历史帧缓冲/motion vector，移动端易拖影，且与内置 SSAO 定位重叠。优先级低于水体/植被/大气。

## 2. 移动端注意点

- TBDR 下带宽比算力更贵：每次全屏 blit 都是一次 tile load+store；Bloom 以半分辨率为主，避免全屏 ping-pong。
- HDR grading（LogC LUT）比 LDR 多一次 LUT 查表/精度开销，但 ACES 下能保留暗部；当前选择 HDR。
- `m_ColorGradingLutSize=32` 保持；只在参数变化时重建，不要每帧生成。

## 3. 待验证

- iQOO 16 实机：Uber + Bloom 的 GPU 耗时；当前无真机数据。
- 夜景曝光 +1.8EV 是否在真实 HDR 屏上过亮；需要时改 `AtmosphereController._nightExposureEv`。
- ACES + HDR grading 的 32 级 LUT 精度；不够再评估，不要先换第三方。

## 4. ACES 与对马岛的差异（2026-11-18 调研）

权威来源：SIGGRAPH 2021 Advances in Real-Time Rendering 课程
"Real-Time Samurai Cinema: Lighting, Atmosphere, and Tonemapping in Ghost of Tsushima"
（Jasmin Patry / Sucker Punch）。

- **GOT 在 ACEScg（AP1）里做 tonemap**，并把 ACEScg 的红色基色 x 从 **0.713 改到 0.75**（y 不变，0.293）。
  原因：标准 ACEScg 会把红/橙推向黄色，美术总监不接受日落偏黄；改红基色后减少黄移，
  同时不过度压低黄色（金色森林仍成立），代价是靠近蓝基色的颜色偏品红。
- **不是只改一条曲线**：还有 bilateral grid 局部 tonemap（64×32×64 log 亮度，40% bilateral + 60% 宽高斯，
  PS4 1080p 约 250µs）、Bradford LMS 的 Von Kries 白平衡（折进 Rec.709→tonemap 矩阵）、32³ sRGB 艺术 LUT、
  夜景 Purkinje shift。
- **URP 14 的 ACES 不是这套**：`unity_to_ACES` 先转 AP0，再用固定 RRT/ODT fitting 输出 Rec.709；
  Volume 的 `Tonemapping` 没有“改红基色”选项。

### Color Adjustments 能做什么

| 参数 | 作用 | 能否修 GOT 的红→黄 |
|---|---|---|
| postExposure | EV 曝光 | 否 |
| contrast | 全局对比（HDR grading 在 ACEScc 中） | 否 |
| colorFilter | 全局乘色 | 否 |
| hueShift | 全局色相旋转 | 否（会把所有颜色一起转） |
| saturation | 全局饱和 | 否 |

结论：Color Adjustments 只能定**全局基线**（亮度/对比/饱和/整体色调），
不能选择性修 ACES 的 red→yellow，也不能改 tonemapper 的基色。

### 可用的近似与真改

- **零额外 pass 的近似（已落地）**：`White Balance`（HDR grading 下在 tonemap 前，LMS 白平衡）+
  `Color Curves` 的 Hue vs Hue / Hue vs Sat（选择性拉回橙红、控制高光饱和）。
  当前曲线：橙红 hue 0.06–0.12 回拉约 -6.5°…-10°，饱和倍率 1.08–1.12；黄 hue 0.16 保持中性。
  实测日落图（顶部 55% 亮部，v>0.35）平均 hue 从约 24.8° 降到约 22.1°，
  平均饱和度从约 0.62 升到约 0.70；p75 hue 从 37.2° 降到 30.8°。
- **真改 ACES**：替换 URP tonemap 为 Rec.709→修改版 ACEScg→RRT/ODT→Rec.709。
  URP/core 已 embedded（`Packages/com.unity.render-pipelines.*`），补丁落点见
  [URP AGENTS](../../Packages/com.unity.render-pipelines.universal/AGENTS.md) §3；
  **当前仍未修改 tonemap**，3×3 矩阵需由 x=0.75、y=0.293 与 D60 白点生成，并单独测局部 tonemap。
  若走自定义全屏 pass，会违反“单 Uber pass / 不叠第二套后处理”的移动端约束。

来源：
- [SIGGRAPH 2021 slides (PDF)](https://www.realtimerendering.com/advances/s2021/jpatry_advances2021.pdf)
- [Jasmin Patry 的 HTML 版 slides](http://www.glowybits.com/talks/real-time_samurai_cinema/index.html)
- [中文整理](https://nothingtosay0031.github.io/Engine/Real-Time-Samurai-Cinema-Lighting,-Atmosphere,-and-Tonemapping-in-Ghost-of-Tsushima)
