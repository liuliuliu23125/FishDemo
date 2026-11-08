# 水体选型分析：Crest Water 5

> 分析对象：Crest Water 5 (Oceans, Rivers, Lakes) v5.10.0
> 目标环境：Unity 2022.3.62f2 + URP 14.0.12，Android 旗舰机，30fps

## 结论速览

| 项 | 结论 |
|---|---|
| 版本兼容 | ✅ **近乎完美**（要求 2022.3.62f3，工程是 62f2） |
| URP 支持 | ✅ 原生 URP + 独立 RenderGraph 分支 |
| 水面反射 | ✅ 有 planar reflection，**但没有"视锥内无水体就跳过"的自动判断** |
| 水下效果 | ✅ 完整（吸收/散射/caustics/Snell's window） |
| 移动端 | ⚠️ 可用，但需关掉部分 LOD 模块 |
| 与 Gaia | ✅ 官方有集成脚本 |

**总体评价：Crest 是本项目水体方案的最佳选择，但"反射按需"需要自己补一层。**

## 1. 版本兼容性

`package.json` 原文：

```json
{
    "name": "com.waveharmonic.crest",
    "version": "5.10.0",
    "unity": "2022.3",
    "unityRelease": "62f3"
}
```

**工程是 2022.3.62f2，Crest 要求 62f3** —— 差一个补丁号。这是全部分析中最精确的版本匹配，说明 Crest 就是针对这个版本线做的。

**结构优势**：Crest 以 UPM 包形式提供（`Packages/com.waveharmonic.crest`），不是散落在 `Assets/` 里。这意味着：
- 升级/替换干净
- 不污染 `Assets/`
- 但需要确认是 embedded package 还是 registry package（本项目应作为 embedded 或本地 tarball）

## 2. 架构：LOD 模块化

Crest 的核心设计是 **Lod 系统**，每个功能是一个独立 LOD 模块：

```
Runtime/Scripts/Data/
  AnimatedWavesLod.cs    ← 波浪位移
  DynamicWavesLod.cs     ← 动态交互波（船、鱼）
  FoamLod.cs             ← 泡沫
  FlowLod.cs             ← 流向
  ClipLod.cs             ← 裁剪（水下/水面边界）
  ShadowLod.cs           ← 阴影
  DepthLod.cs            ← 深度
  AbsorptionLod.cs       ← 吸收（水下颜色）
  AlbedoLod.cs           ← 反照率
  ColorLod.cs            ← 颜色
  LevelLod.cs            ← 水位
```

**这是巨大的性能优势**：每个模块可以单独关闭。移动端可以只保留 AnimatedWaves + Foam + Absorption，关掉 DynamicWaves、Flow、Shadow。

## 3. 镜面反射分析（重点）

### 现有能力

`WaterReflections.cs` 提供 planar reflection：

```csharp
[Tooltip("Whether planar reflections are enabled.\n\nAllocates/releases resources if state has changed.")]
internal bool _Enabled;

int _Resolution = 256;          // 反射分辨率，默认 256
int _RefreshPerFrames = 1;      // 每 x 帧刷新
bool _RenderOnlySingleCamera;   // 只为主相机渲染
```

**好的设计**：
1. `_Resolution = 256` 默认很低，对移动端友好。
2. `_RefreshPerFrames` 可以隔帧刷新（如每 2 帧）。
3. `_UseObliqueMatrix` 用斜投影裁剪，减少 overdraw。

### 关键缺口：没有"视锥内无水体就跳过"

Crest 只有**基于相机高度**的跳过（`WaterReflections.cs:240-241`）：

```csharp
bool SkipAbove => !_Water._ActiveModules.HasFlag(WaterRenderer.ActiveModules.Portal) && _Water.ViewerHeightAboveWater <= -1f;
bool SkipBelow => !_Water._ActiveModules.HasFlag(WaterRenderer.ActiveModules.Volume);
```

这两个判断的是"相机是否在水下"和"是否启用了对应模块"，**不是"水面是否在视锥内"**。

**你提出的需求"摄像机里没有水体就不处理"需要自己补**。实现方案：

```csharp
// 在 WaterReflections 的 OnPreRender 之前插入可见性判断
var waterBounds = _Water.Bounds;  // 需要从 WaterRenderer 暴露
if (!GeometryUtility.TestPlanesAABB(planes, waterBounds))
{
    _Enabled = false;  // 或跳过反射渲染
}
```

注意：Crest 的 `WaterRenderer` 是否暴露 `Bounds` 需要验证（`WatertightHull.cs` 可能有相关几何信息）。

## 4. 水下效果

Crest 的水下实现完整度很高：

| 能力 | 实现位置 | 状态 |
|---|---|---|
| RGB 分离吸收 | `UnderwaterRenderer.cs:437` `_Extinction` | ✅ |
| 环境光衰减 | `UnderwaterRenderer.cs:45` out-scattering | ✅ |
| Caustics | `Caustics.hlsl` + `Textures/Caustics/Caustics.png` | ✅ |
| 水下雾 | `Underwater.hlsl` | ✅ |
| 水面背面 | `UnderwaterMaskPass` | ✅ |
| URP 集成 | `UnderwaterEffectPassURP.cs` | ✅ |
| RenderGraph 版本 | `UnderwaterEffectPassURP.RenderGraph.cs` | ✅（Unity 6 用） |

**亮点**：`UnderwaterRenderer.cs:434-437` 会自动从水面材质读取 absorption 参数，保证水上水下颜色一致：

```csharp
if (_SurfaceMaterial.HasVector(WaterRenderer.ShaderIDs.s_Absorption))
{
    _Extinction = _SurfaceMaterial.GetVector(WaterRenderer.ShaderIDs.s_Absorption);
```

**注意**：Crest 的 caustics 是**贴图投影式**（`Caustics.png`），不是实时计算。这对移动端是好事（便宜），但动态感不如实时 caustics。

## 5. URP 集成深度

Crest 对 URP 的集成比一般包深。证据（`WaterRenderer.Universal.cs:160-175`）：

```csharp
var queue = s_ActiveRenderPassQueue.GetValue(renderer) as List<ScriptableRenderPass>;
queue.Remove(_CopyColorPass);
queue.Remove(_CopyDepthPass);
```

它**直接操作 URP 内部的 render pass 队列**（通过反射拿 `s_ActiveRenderPassQueue`），移除自己不需要的 pass 以避免重复拷贝。

**双刃剑**：
- 优点：性能优化到位，避免了多余的 CopyColor/CopyDepth。
- 风险：依赖 URP 内部字段（反射），**URP 升级可能失效**。锁 2022.3 反而规避了这个风险。

## 6. 移动端性能评估

### 必须关闭/降低的项

| 模块 | 移动端建议 | 理由 |
|---|---|---|
| DynamicWavesLod | 关或降到最低 | 交互波需要额外 sim pass |
| FlowLod | 关 | 钓鱼场景不需要大范围流向 |
| ShadowLod | 关 | 水面接收阴影成本高 |
| AnimatedWaves 分辨率 | 128 或 256 | 512 对移动端过重 |
| Reflection 分辨率 | 128~256 | 默认 256 已合理 |
| Reflection 刷新率 | 每 2 帧 | 水面反射变化慢，隔帧无感 |
| Caustics | 开（贴图式，便宜） | |

### 波高采样成本

Crest 提供 `CollisionProvider` / `RayTraceHelper` 用于 CPU 端查询水面高度（钓鱼玩法需要知道水面位置）。这部分在移动端要注意：
- 每次查询有计算成本
- 应缓存结果，不要每帧对每个浮标查询

## 7. 与 Gaia 的集成

Crest 自带 Gaia 集成（`Editor/Scripts/Integrations/Gaia/GRC_Crest.cs`）。这意味着：
- Gaia 生成的地形可以直接接入 Crest 水体
- 海岸线会自动适配

**这是 Crest 相对自研水体的重要优势** —— 省掉海岸线适配的工作量。

## 8. 与项目的契合度评估

| 项目需求 | Crest 支持 | 备注 |
|---|---|---|
| 丰富水体表现 | ✅ 优秀 | 模块化，效果全面 |
| 镜面反射 | ✅ 有，需补可见性判断 | 见 §3 |
| 水下效果 | ✅ 完整 | 含 caustics |
| 高性能 | ⚠️ 需裁剪 | 关掉 4-5 个 LOD |
| 移动端 | ⚠️ 需验证 | 无明确移动端分支，需实测 |
| 大世界 | ✅ 支持 | LOD 按距离分级 |
| 与 Gaia 配合 | ✅ 官方集成 | |

## 9. 风险清单

| 风险 | 等级 | 应对 |
|---|---|---|
| 无移动端专门优化 | 🟡 中 | 实机 Profile，关模块 |
| 反射无可见性跳过 | 🟡 中 | 自研补一层 |
| 反射依赖 URP 内部字段 | 🟡 中 | 锁 2022.3 规避 |
| 包体较大（760 资源） | 🟢 低 | 只保留必要模块 |
| 版本要求 62f3 vs 工程 62f2 | 🟢 低 | 实际应无影响，但需验证 |

## 10. 建议

**采用 Crest 作为水体方案**，理由：
1. 版本匹配度最高（2022.3.62f3）。
2. 模块化 LOD 设计，性能可裁剪。
3. 水下效果完整，省去自研。
4. 官方 Gaia 集成，省海岸线适配。

**但必须做三件事**：
1. **补"反射可见性跳过"**（你的明确需求，Crest 没有）。
2. **实机验证移动端性能**，确定关哪些 LOD。
3. **确认 embedded package 的引入方式**（不要放 `Assets/`）。

## 待验证项

- `WaterRenderer` 是否暴露水面 Bounds（用于可见性判断）
- 移动端关闭各 LOD 后的实际帧率
- Crest 的 shader 变体数量（配合 Shader Control 裁剪）
- 与 Enviro 的雾/天空在水面反射中的交互
