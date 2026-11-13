# 水体：Crest Water 5（选型分析 + 落地记录）

> 分析对象：Crest Water 5 (Oceans, Rivers, Lakes) v5.10.0
> 目标环境：Unity 2022.3.62f2 + URP 14.0.12，Android 旗舰机，30fps
>
> **阅读顺序**：§1-10 是**选型阶段**的分析（2026-11-11 写，当时未装）。
> §11 是 **2026-11-13 的落地记录**——实际装了什么、怎么接的、踩了哪些坑、还剩什么没验证。
> 两者结论不一致时以 §11 为准（§1-10 里"建议采用""待验证"之类的措辞已过时）。

## 结论速览

> ⚠ 下表是选型阶段的评估。**实际落地配置见 §11.5**，与这里的"移动端建议"有差异
> （例如 Absorption/Scattering 最终是全关的，而表里建议保留）。

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

---

## 11. 落地记录（2026-11-12，中心人工湖 + Crest 接入）

> 本节是实施结果，与上面 §1-10 的"选型分析"区分。所有数字都有工具输出佐证。

### 11.1 为什么"看不到水"——根因

水面实例在 `y=60`（`WorldPipeline.cs:296`），而地形实测高度 **236~350m**
（`Logs/Lake/site-probe.txt`，9409 个采样点）。水面埋在地下约 200m。
这不是渲染问题，是**水位与地形完全错位**。

### 11.2 湖盆设计（第一性原理推导）

地形是缓坡，中心 ±800m 内**没有天然洼地**（最低点 260.6m 与周边只差 4m），
所以必须人工挖碗形盆地。参数由高度网格数据搜索得出：

| 参数 | 值 | 依据 |
|---|---|---|
| 湖心 | `(192, 96)` | `Logs/Lake/height-grid.csv` 全局搜索：挖方小、AABB 内不溢出 |
| 半径 R | 190m | 同上 |
| 水位 | **264m** | 碗沿 266m，AABB 内湖外最低原生地形 266.7m；取 264 留 2.7m 波浪余量 |
| 碗深 | 30m | 碗底 234m |
| 过渡带 | 90m | R..R+90 不动，岸线自然收边 |
| 挖方 | ~5.0M m³ | 只挖不填，零填方 |

**溢出判据必须按矩形 AABB，不能按圆形**：水面由 WaterBody 的矩形 AABB 决定渲染范围
（`SurfaceRenderer.ApplyWaterBodyCulling`），AABB 外的地形哪怕低于水位也不会露出水面。

### 11.3 施工链路（4 步，全部可逆）

| 步骤 | 脚本 | 改动 | 还原 |
|---|---|---|---|
| 1. 挖湖盆 | `LakeCarve.Apply` | 2 个 chunk 的高度图（113696 格） | `Restore`（`Tools/lake-backup/`，只备份受影响窗口） |
| 2. 清植被 | `LakeVegetationClear.Apply` | 删 608 棵树 + 57531 株草 | `Restore`（`Tools/lake-veg-backup/`） |
| 3. 重转 GTS | `GtsConvertOne.Run` ×2 → `GtsFixMeshRendering.RunAll` | 2 chunk 的地表 mesh | 重跑世界生成会覆盖 |
| 4. 接 Crest | `LakeCrestSetup.Apply` | 主场景加 `Crest Lake` 节点（保留 camera rig）；禁用旧 Gaia Water；按透明度模式设置 URP Depth/Opaque（当前 SimpleTransparency 为 0） | `Remove` |

### 11.4 踩坑记录（全部实测）

1. **unitypackage 必须连 `.meta` 一起解**。Crest 包里 asset:meta 是 1:1.28，
   只解 asset 会 GUID 全断、编译失败（`LodInputMode not found` 等 20+ 错误）。
   工具：`E:\pkg-inspect\work\unpack_upm2.py`。
2. **重转已挂 mesh 的 chunk，GTS 会崩**。`GTSMesh : GTSComponent`，
   `GTSProfile.GetGTSComponents()` 找到 chunk 里已有的 `GTSMesh` 后对它的
   gameObject 取 `GetComponent<Terrain>()` → MissingComponentException。
   修法：`GtsConvertOne` 转换前先删 `GTS_Mesh` 节点（不只是 `GTS_MeshTerrains`）。
3. **植被清除不能加全局高度判据**。地形整体 236~350m，世界边缘大片低于任何水位；
   `h < 水位` 会命中离湖 1km 外的草（首次统计误中 25 万株）。只用半径判据。
4. **`WaterRenderer.SeaLevel` 在编辑器批处理下恒为 0**。它读 internal `Position`，
   只在运行时 `RunUpdate` 赋值。编辑期校验水位要看 `transform.position.y`。
5. **验收相机必须等 chunk 加载完**。Play Mode 第 20 帧时 SECTR 还没加载湖边 chunk，
   `SampleHeight` 找不到 Terrain → 相机摆到 y=1.7 钻进地里（首跑截图证实）。
   修法：轮询 Terrain 覆盖站位点才摆相机，超时用探测网格的已知高度兜底。
6. **`SurfaceRenderer.Chunks` / `WaterChunkRenderer` 都是 internal**，
   编辑器程序集访问不到。统计水面 chunk 数改用
   `GetComponentsInChildren<MeshRenderer>()` 按水面材质过滤。

### 11.5 Crest 接入配置（2026-11-13；已按 §14 的性能模式更新）

| 配置 | 值 | 理由 |
|---|---|---|
| 位置 | `(192, 264, 96)` | 湖心 + 水位 |
| `LodLevels` | 5 | 固定视点 26m 时 Scale=16；4 级最外层半边长 256m（离岸仅 66m，像方框），5 级 512m |
| `LodResolution` | 128（Depth/Foam/Clip/AnimatedWaves/Level）；ShapeGerstner 96 | 默认 256/160/128 对小湖过密 |
| `ScaleRange` | `(1, 16)` | 湖面不需要远视距放大 |
| **Surface Clipping** | `EverythingClipped` + `ClipLodInput(Cube 640×640，半边长 320m)` | 见下：这是「只在湖盆出水」的唯一正确做法；320m 内湖外地形全部高于水位 |
| 模块 ON | AnimatedWaves / Foam / Depth / Level / Clip | 移动端 30fps 下的最小可用集 |
| 模块 OFF | DynamicWaves / Flow / Shadow / Albedo / **Absorption / Scattering** | 吸收/散射仿真对小湖没有视觉收益，且多两个每帧 pass |
| 平面反射 | **OFF** | 最贵的一项；Crest 无视锥剔除（§3） |
| Depth SDF | **OFF** | 场景无 `DepthProbe`，开着会把深度纹理从 R16 变成 R16G16，白付带宽 |
| Depth Terrain | **仅与湖区 AABB 相交的 Terrain** | `EverythingClipped` 下湖外水面不存在，湖外 Terrain 不会被采样 |
| WaterBody | `Include`，AABB 半边长 280m，`Affects=Volume+Physics` | 只管水下体积与物理限域，**不负责裁水面** |
| 水色 | absorption `(0.62,0.86,0.92,0.016)` / scattering `(0.012,0.026,0.036)` | 见 §12.1：30m 深湖盆需要更低的 extinction |
| URP | `m_RequireDepthTexture=0`、`m_RequireOpaqueTexture=0`（SimpleTransparency）；非 SimpleTransparency 才需要 1 | 当前水面不请求 opaque/depth，开着只是白付 CopyColor/CopyDepth |

**为什么必须用 Surface Clipping，而不是只靠 WaterBody**：
Crest 的水面是以**视点**为中心的 clipmap（`LateUpdatePosition` 把 `Position` 设为
`(视点.x, 水面y, 视点.z)`，各 chunk 相对它按 LOD 翻倍铺开，最外圈能到 ±数十公里）。
`WaterRenderer` 的 transform XZ 对水面**没有意义**；WaterBody 的 AABB 剔除是**整 chunk 粒度**，
挡不住巨型外圈 chunk（Chunks 模式实测 49 个 chunk 只有 8 个被剔除，水面仍铺满全世界；
Quad 模式虽然只有 1 次 DrawMesh，但那张 10000×10000 的四边形同样需要 Clip 才能只留在湖盆）。
只有 Clip 的逐像素 SDF 能精确裁出湖盆。

**`ClipLodInput.Inverted` 的语义（`ClipPrimitive.compute:99-104`）**：
初始 `EverythingClipped` 全写 1（>0.5 即裁）。
`Inverted=true` → `target = min(target, sdf)`：盒内 sdf<0 保留水，盒外 sdf 大保持 1 被裁。**这才是对的**。
`Inverted=false` → `target = max(target, 1-sdf) ≥ 1`：全裁，一滴水都不剩（2026-11-13 踩过）。

### 11.6 踩坑记录（全部实测）

按发现顺序：

1. **unitypackage 必须连 `.meta` 一起解**。Crest 包里 asset:meta 是 1:1.28，
   只解 asset 会 GUID 全断、编译失败（20+ 个 `LodInputMode not found`）。
   工具：`E:\pkg-inspect\work\unpack_upm2.py`。
2. **重转已挂 mesh 的 chunk，GTS 会崩**。`GTSMesh : GTSComponent`，
   `GTSProfile.GetGTSComponents()` 找到 chunk 里已有的 `GTSMesh` 后对它取
   `GetComponent<Terrain>()` → MissingComponentException。
   修法：`GtsConvertOne` 转换前先删 `GTS_Mesh` 节点（不只是 `GTS_MeshTerrains`）。
3. **植被清除不能加全局高度判据**。地形整体 236~350m，世界边缘大片低于任何水位；
   `h < 水位` 会命中离湖 1km 外的草（首次统计误中 25 万株）。只用半径判据。
4. **`WaterRenderer.SeaLevel` 在编辑器批处理下恒为 0**。它读 internal `Position`，
   只在运行时 `RunUpdate` 赋值。编辑期校验水位要看 `transform.position.y`。
5. **`SurfaceRenderer.Chunks` / `WaterChunkRenderer` 都是 internal**，
   编辑器程序集访问不到（CS1061）。统计水面 chunk 改用 `GetComponentsInChildren<MeshRenderer>()`。
6. **`Lod.Enabled` 的 getter 是 `_Enabled && _Valid`**，而 `_Valid` 只在运行时
   `Initialize()`（需要 GPU）后为 true。**编辑期读它必然 false**，不能当校验判据。
7. **Absorption / Scattering 默认关闭**（`Lod` 基类 `_Enabled` 默认 false，
   只有 AnimatedWaves / Shadow 在构造函数里置 true）。湖最终选择关闭它们、
   用材质恒定色：小湖的空间变化吸收色没有收益，且能省两个仿真 pass。
8. **水面材质需要显式写入 `_Crest_AbsorptionColor` / `_Crest_Absorption` / `_Crest_Scattering`**。
   这些值本应由材质 Inspector 的 `@OnChange` 触发 `UpdateAbsorptionFromColor` 计算，
   批处理下没人触发 → 取到 0 → 水面全黑。用 public API
   `WaterRenderer.CalculateAbsorptionValueFromColor` 按目标色算好写入。
9. **Pegasus 巡游控制器会覆盖相机位姿**（挂在 Main Camera 上，`Update()` 里驱动）。
   验收前必须 `tour.enabled = false`，否则脚本摆的机位每帧被覆盖
   ——这是「验了十几轮都看不到湖」的真正原因。
10. **SECTR 是异步流式加载，脚本瞬移相机后加载内容不确定**。
    同机位两次运行加载到的 chunk 完全不同（GTS subTile bounds 都不一样）。
    验收脚本改为**进 Play Mode 前把 36 个 chunk 全部 Additive 打开**，几何一定齐。
11. **世界场景没有环境光（未烘 GI）**。`m_AmbientMode=0`（Skybox）依赖烘焙环境探针，
    batchmode 从不烘 → 探针全黑 → 所有背光面和水面都渲染成 `(0,3,17)` 近黑。
    验收脚本必须显式 `RenderSettings.ambientMode = Flat` 给中性环境光。
    这曾长期被误判为「水面渲染错误」。
12. **GTS 地表在编辑/批处理下需要显式 `GTSMesh.Refresh()`**。splat 混合依赖一批
    全局 Vector 数组，由 `GTSComponent.UpdateMaterial()` 注入；不 Refresh 时
    地表全黑（现象：同一画面里树正常、地面黑）。
13. **改高度图后必须重转 GTS mesh，且要重跑 `GtsFixMeshRendering`**。
    重转会重建 `GTS_Mesh` 节点并换 mesh GUID，`GTSMesh` 组件丢失后
    worldSize 校正与全局数组注入全没了。

### 11.7 验收结果

> **历史数据**：本节是 Chunks 模式（§14 之前）的结果。当前 Quad + SimpleTransparency 下
> `CrestRenderProbe` 实测 chunk=0、URP pass=1、CopyColor/CopyDepth=0。

`-executeMethod LakeRenderVerify.Run` → `Logs/Lake/lake-render.txt` + `lake-render.png`

```
[1] 水位正确           : PASS (y=264.0)
[2] 水面 chunk 存在    : PASS (49)
[3] 中带出现水体色     : PASS (14.4%，阈值 12%)
[4] 画面有内容         : PASS (8954 色)
==== 结论：PASS ====
```

截图 `Logs/Lake/lake-render.png`：湖盆碗形完整、岸线平齐、岸边有泡沫线、
水色随深度由浅到深。树与草在湖区已清空。

### 11.8 当前水面观感与已知限制

`Logs/Lake/lake-render.png`（玩家视角）：湖盆碗形完整、岸线平齐、岸边有泡沫线、
水色为明亮的青蓝，**湖已正确显示在凹陷处**。水与地形衔接自然。

**已知限制（需真机/交互环境确认，batchmode 无法验证）**：
1. **水面看起来是纯平的**。`ShapeGerstner` 已挂、频谱 `WavesModerate`、风速 20 km/h，
   但 batchmode 的 `Camera.Render()` 不推进 Crest 的仿真时钟，位移纹理停在初始帧。
   真机 Play Mode 下应有正常起伏。**这一条未验证。**
2. ~~**水面偏不透明**~~ —— **已解决**，见 §12.1（消光系数）与 §14.4
   （SimpleTransparency 下的 `_Crest_TransparencyMinimumAlpha`）。
   当时的误判是"以为 alpha 混合没生效"：**非 SimpleTransparency 下** Crest 的透明靠
   体积消光、与 alpha 无关；**当前 SimpleTransparency 下** `o_Alpha` 会被写入并与
   min alpha 取 max，见 §14.4。
3. 截图里的**岸壁有拉伸条纹**（碗壁陡坡上 GTS 贴图被拉伸），
   是 30m 深碗的陡坡 + 256m/257 顶点的分辨率导致，属可接受范围。

### 11.9 遗留事项

- [ ] 真机（iQOO 16）实测帧率；Reflections / Underwater 的取舍要在真机上定
- [ ] 钓鱼玩法需要 CPU 波高查询时，开 `com.waveharmonic.crest.cpu-queries` 附加包
- [ ] Crest shader 变体数量未审计（Shader Control 待接入）
- [ ] 旧 Gaia Water 已禁用保留在场景中；确认不再回退后可删
- [x] **水面 chunk 数量不随湖缩小** → 已改 `WaterMeshType.Quad`（单四边形 1 次 DrawMesh），见 §14
- [x] `LodLevels` 最终为 5（固定视点 26m 时 4 级半边长 256m，离岸只有 66m；5 级 512m），分辨率 128（Gerstner 96），见 §14.5
- [x] `DepthLod` SDF 通道 → 已关闭（无 `DepthProbe`，R16G16 白付带宽）
- [x] `DepthLod` 每帧烘全部 Terrain → 已按湖区 AABB 过滤（仅与 Include WaterBody 相交的 Terrain）

## 12. 水面透明 + 水下效果 + 环湖轨道相机（2026-11-13 落地）

### 12.1 水面透明：机制与参数

**Crest 的透明有两种模式**（代码事实）：

- **当前 SimpleTransparency=1**：`Fragment.hlsl:825-828` 用 `volumeOpacity`、
  `_Crest_TransparencyMinimumAlpha`、foam、albedo 一起写 `o_Alpha`，水面走 alpha 混合。
  调透明度要动 `_Crest_TransparencyMinimumAlpha`（当前 0.12）和 DepthLod 的 volumeOpacity，
  **不是只调 Absorption/Scattering**。
- **非 SimpleTransparency (0)**：`o_Alpha` 恒为 1，材质的 SrcAlpha 混合形同虚设；
  透明来自 `Fragment.hlsl:625-670` 的折射合成 + 体积消光：
  `o_Emission *= 1-volumeOpacity; o_Emission += volumeLight*volumeOpacity`，
  `VolumeLighting.hlsl:22-25`：`volumeOpacity = 1 - exp(-(absorption+scattering) * 水中光程)`。
  此时才只动材质上的 `_Crest_Absorption` / `_Crest_Scattering`。
- 两种模式都保留了材质上的 absorption/scattering 系数；当前湖用它们做恒定水色，
  不跑 Absorption/Scattering 仿真。

**颜色→系数的换算**（`WaterRenderer.cs:519-527`）：
`absorption = -color.a * 32/5 * ln(color.rgb)`。alpha 是密度（越小越清澈），
rgb 是透水色。scattering 则是直接写入的每米散射系数。

**最终参数**（`LakeCrestSetup.ApplyWaterColor`，`LakeCrestSetup.Retune` 可单独重跑）：

| 参数 | 值 | 效果 |
|---|---|---|
| `_Crest_AbsorptionColor` | `(0.62, 0.86, 0.92, 0.016)` | 密度 0.016，清澈 |
| `_Crest_Absorption` | `(0.049, 0.015, 0.008)` /m | R 衰减最快 → 水下偏青 |
| `_Crest_Scattering` | `(0.012, 0.026, 0.036)` /m | 青蓝，**R 必须压低** |
| extinction 合计 | `(0.061, 0.041, 0.045)` /m | 1/e 深度 16/24/22m |
| 3m 处保留场景色 | 83%（R 最差） | 岸边看得见底 |
| 30m 处保留 | 16% | 湖心自然变深 |

**踩坑（实测）**：
- scattering 的 R 通道不能跟着 rgb 直觉走。第一版 `(0.034,0.046,0.050)` 三通道接近，
  入射光一乘雾就发白（水下画面均值 RGB 106/126/137）。压到 `(0.012,0.026,0.036)`
  → `scatteringAmount = scattering/extinction ≈ (0.20,0.63,0.80)`，雾色才偏青。
- **水下体积光的入射权重 `_Crest_AmbientTerm` / `_Crest_DirectTerm` 必须写在
  Water.mat（水面材质）上**：`UnderwaterRenderer.Effect.cs:247` 每帧执行
  `_VolumeMaterial.CopyMatchingPropertiesFromMaterial(_SurfaceMaterial)`，
  只写 Volume 材质会被水面材质覆盖回默认 1.0（静默失效）。取 0.35/0.40
  让水下雾"越深越暗"，不再是一片亮白。

### 12.2 水下效果：开关机制

**引擎自带判断，不需要自己写"相机进水检测"**（`UnderwaterRenderer.cs:299-307`）：
相机离水面 >2m 时 `ShouldRender` 直接 return false；在 2m 以内且相机 XZ 落在
Volume 型 WaterBody（`Lake Body` 节点，`Exclusion=Include`）内、且相机 cullingMask
含 Water 层（layer 4）时开启。三项场景里都已配置。

水下雾的浓度/颜色 = 水下材质的 absorption/scattering × `_Crest_ExtinctionMultiplier`，
每帧从水面材质拷贝（见 12.1 踩坑第二条）。

### 12.3 环湖轨道相机（不改原相机）

**没有用 Cinemachine**：工程没装（manifest.json 无该项），且下面三件事它一件都解决不了。

三个必须跟随相机的状态（`LakeCameraRig.Apply` 统一处理）：
1. **Crest 视点**：`WaterRenderer.Viewer` —— 水面 clipmap 以它为中心铺开，
   不切换就出现"湖在那个方向、水铺在别处"。
2. **SECTR chunk 加载**：`SECTR_RegionLoader` 按**自身 transform** 判定范围
   （`SECTR_RegionLoader.cs:83-84`），不看相机。两台相机各挂一个 loader，
   rig 只启用当台（两个同开会叠加加载范围）。
3. **`Camera.main` tag**：GrassBRG 剔除兜底等依赖它，rig 把 MainCamera tag 搬到当前相机。

| 文件 | 职责 |
|---|---|
| `Assets/Scripts/Runtime/Gameplay/LakeOrbitCamera.cs` | 轨道运动：半径 300m、水位+26m、120s/圈、俯仰 12°±3° 摆动；`Paused` 让出 transform；支持注入时间源（验收用） |
| `Assets/Scripts/Runtime/Gameplay/LakeCameraRig.cs` | **C** 切相机 + **F** 自由飞行：Camera/AudioListener/loader 三件套开关 + Viewer/tag 跟随；挂在 `Crest Lake` 上 |
| `Assets/Editor/LakeCameraSetup.cs` | 幂等写入 World.unity；`Apply`/`Remove`/`Report` |

组件级开关而**不是 SetActive(false)**：每台相机自带 AudioListener，
禁 GameObject 会把监听器一起关掉，Unity 报 "no audio listeners"。

**F 键自由飞行（两台相机都支持）**：
- `PegasusFlyingCamera` 挂在**当前渲染相机**上，WASD 移动 / QE 升降 / 右键转视角 / 滚轮调速。
- **必须暂停轨道脚本**：`LakeOrbitCamera` 在 `LateUpdate` 写 transform，比飞行的
  `Update` 更晚，不暂停就把飞行位移整帧覆盖（表现"按 F 没反应、镜头自己转"）。
  rig 用 `Paused` 开关解决，实测 15 帧漂移 0.000m。
- 巡游相机飞行时必须 `PauseFlythrough()`，否则 Pegasus 每帧把相机拖回路径点。
  退出时按最近路径点续接（同 `PegasusTourController.SnapToNearestPathPoint`）。
- **`PegasusTourController` 已被 rig 禁用**：它也监听 F，两者同帧会各挂一份飞行组件。
  rig 已实现它的全部职责并扩展到两台相机，故禁用而非删除（场景结构不变，可随时改回）。
- 退出飞行后轨道用 `ResumeFromCurrentPosition()` 按当前方位角续接，
  不用 `ResetPose()`（那会瞬移回起始角度，观感是镜头一跳）。

### 12.4 验收

| 脚本 | 判什么 | 命令 |
|---|---|---|
| `LakeWaterVerify.Run` | 水面透明（浅水见底）、水下吸收色调、水下有内容、开关对照、水上不误开 | 需真实图形设备 |
| `LakeOrbitVerify.Run` | 轨道几何（半径/高度恒定、扫过角度、朝湖心）+ 三项系统跟随 + 切换 | 可 `-nographics` |
| `LakeCrestSetup.Retune` | 只重写水色，打印 1/e 深度等光学参数 | 快速调参入口 |

2026-11-13 最终结果：`lake-water.txt` 6/6 PASS，`lake-orbit.txt` 9/9 PASS
（含 [8] F 键飞行位移保持、[9] 退出后轨道接管）。
截图在 `Logs/Lake/lake-water-*.png`（浅水俯视 / 水下开 / 水下关对照 / 轨道机位）。

**验收脚本的三个坑**：
- batchmode 下 `Time.deltaTime` 不推进（120 帧只走 0.6°），
  `LakeOrbitCamera.OverrideDeltaTime` 注入固定步长解决；
- 水下色调判据不能用绝对值 —— 湖只有 16m 深，碗底占屏幕大半，
  必须用「同机位开/关水下效果」的差值（`Underwater.Enabled = false` 对照组）；
- 验收机位不能站岸上：湖区植被只清到 r = R+8（`LakeVegetationClear`），
  岸外机位会落在树里，近处树干挡镜头（实测画面中间一条黑线）。

### 12.5 新增遗留事项

- [ ] `LakeCameraRig` 只在运行时搬 tag，退出 Play 后场景 tag 复原 ——
      `LakeRenderVerify` 这类编辑期脚本不受影响，但若将来加"编辑期也要 orbit 相机"
      的需求，需另行处理
- [ ] 水下体积光权重 0.35/0.40 是 batchmode 观感定参，真机 HDR/色调映射下需复核
- [ ] 轨道相机未接任何后处理 Volume（场景本就无 Volume），加后处理时两台相机要同步

## 13. 渲染开销分析（2026-11-13，实测）

> 工具：`-executeMethod CrestRenderProbe.Run` → `Logs/Crest/crest-render-probe.txt`。
> 它反射读 URP 的 `m_ActiveRenderPassQueue`（在 `beginCameraRendering` 里抓，因为
> `Render()` 结束会清空队列），并统计 chunk 数。

### 13.1 实测数据（历史：§14 之前的配置）

> 本节测于 SimpleTransparency + Quad 之前（Chunks + 宏=0）。当前配置见 §14：
> Quad、chunk=0、URP pass=1、LodLevels=5、LodResolution=128。

```
MeshType = Chunks,  InjectionPoint = Default,  CREST_SIMPLE_TRANSPARENCY = 0
LodLevels = 4, LodResolution = 160
chunk 总数 = 49   （LOD0: 13, LOD1: 12, LOD2: 12, LOD3: 12）

pass 队列（每帧稳定 4 个）：
  [0] WaterSurfaceRenderPass        event=BeforeRenderingTransparents
  [1] CopyTargetsRenderPass         event=AfterRenderingTransparents
  [2] CopyColorPass                 event=AfterRenderingTransparents
  [3] CopyDepthPass                 event=AfterRenderingTransparents
CopyColorPass = 1,  CopyDepthPass = 1
```

**关于"两次 copy color / copy depth"——实测不成立。** 每帧只有各 1 次。
`CopyTargetsRenderPass` 是 setup pass，`Execute` 是空的（`WaterRenderer.Universal.cs:177-185`），
不产生额外拷贝。它之所以单独存在，是为了在 `OnCameraSetup` 里拿到 URP 私有的
`m_OpaqueColor` / `m_DepthTexture`（反射），再 `Setup` 那两个真正的 copy pass。

### 13.2 chunk 为什么不合批（三条独立原因，任一条都足以拆批）

**① 提交方式是 `CommandBuffer.DrawRenderer` 逐个画**（`SurfaceRenderer.cs:958`）：

```csharp
foreach (var chunk in Chunks)
{
    if (!chunk._Visible) continue;
    buffer.DrawRenderer(renderer, material, submeshIndex: 0, pass);
}
```

`DrawRenderer` 是立即提交的单物体绘制，**根本不进 SRP Batcher 的批处理队列**。
SRP Batcher 只对"引擎正常收集的不透明/透明物体"生效，手工 CommandBuffer 提交的
draw 不走那条路。这一条与材质、CBUFFER 无关，是最直接的原因。

**② 每个 chunk 的常量不同，无法共享 CBUFFER**（`Geometry.hlsl:12-16`）：

```hlsl
// These are per cascade, set per chunk instance.
CBUFFER_START(CrestChunkGeometryData)
float _Crest_ChunkMeshScaleAlpha;
float _Crest_ChunkGeometryGridWidth;
CBUFFER_END
```

注释写得很明确："set per chunk instance"。SRP Batcher 的前提是
**同一 shader 变体 + 同一 CBUFFER 布局 + 逐物体数据都在引擎管理的 `UnityPerDraw` 里**。
这里逐 chunk 的 `_Crest_ChunkMeshScaleAlpha` 在独立 CBUFFER 中，引擎必须每个 chunk
上传一份常量，天然不能合并。

**③ 逐 chunk 的 MaterialPropertyBlock**（`WaterChunkRenderer.cs:185-202`）：

```csharp
internal void Bind()
{
    _MaterialPropertyBlock = _Water.Surface.PerCascadeMPB[_LodIndex];
    var wrapper = new PropertyWrapperMPB(_MaterialPropertyBlock);
    wrapper.SetSHCoefficients(Transform.position);   // ← 按 chunk 位置采样环境光
    ...
    Rend.SetPropertyBlock(_MaterialPropertyBlock);
}
```

MPB 会打断 SRP Batcher 的批（这也是工程里 `terrain-batching-reality` 记录过的结论：
"MPB 会拆批"）。而且 SH 系数是按 chunk 位置算的，本身就不可能相同。

### 13.3 为什么 chunk 是"分块"而不是一整块网格

这是 **clipmap LOD** 的必然结果，不是设计疏忽：
- 水面以**视点**为中心，每级 LOD 覆盖范围翻倍（`WaterBuilder.cs:389` 的 `scale = 2^lodIndex`）；
- 每级是一个 4×4 去掉中心的环（12 块），最内层 1 块 → `1 + 12×4 = 49`，与实测一致；
- 每块用不同的**顶点密度**，LOD 之间靠 `i_LodAlpha` 混合，避免接缝。

所以"分块"是为了**减少远处顶点数**，代价是 draw call 数 = chunk 数。
这在 PC 上无所谓（49 次 draw 不到 0.3ms），在移动端 tile-based GPU 上是主要瓶颈之一。

### 13.4 官方给的两个移动端选项（对应你的问题）

**简单透明模式（`CREST_SIMPLE_TRANSPARENCY`）**
- 编译期宏，改 `Runtime/Shaders/Library/Settings/Settings.Crest.{Default,Android}.hlsl`
  后要重新生成 shader（菜单 `Edit > Rendering > Generate Shader Includes`）。
- 效果（`Fragment.hlsl:490-500`）：**不采样场景色/深度**，改用 `waterDepth`（Depth 仿真）
  估算光程，`o_Emission` 用平均光照近似。
- 收益：`SurfaceRenderer.Universal.cs:61-63` 把 `ConfigureInput` 从
  `Color|Depth` 改成 `None` → **URP 不再需要 `_CameraOpaqueTexture` 和 `_CameraDepthTexture`**，
  省掉 CopyColor + CopyDepth 两个全屏 pass（移动端 tile 带宽上这是最大的一笔）。
- 代价：失去折射（水面下看到的景物不扭曲）、失去与地形的深度交互精度。

**简单网格模式（`WaterMeshType.Quad`）**
- 单个四边形，`SurfaceRenderer.cs:930-933` 只发 **1 次 DrawMesh**。
- 效果：`WaterBuilder` 完全不走，**置换只贡献法线**（水面几何不再起伏）。
- 收益：draw call 从 49 → 1，且没有任何 per-chunk CBUFFER/MPB。
- 代价：看不到真实波浪起伏，只有法线扰动带来的高光变化。

两者可**叠加**：Quad + SimpleTransparency 是 Crest 官方给低端移动端的组合。

### 13.5 对本工程的建议（按收益/风险排序）

> 本节是 §14 之前的建议；Quad、SimpleTransparency 已落地。LodLevels 最终为 5：
> 固定视点 26m 时 4 级最外层半边长 256m，方形边界离 R=190m 的岸线只有 66m，
> 观感像方框；5 级半边长 512m。实际结果与 compute 清单见 §14。

| 措施 | 收益 | 风险 | 建议 |
|---|---|---|---|
| **`WaterMeshType.Quad`** | draw call 49→1，去掉全部 chunk CBUFFER/MPB | 湖面变平（法线仍在，高光仍有变化） | **值得试**。湖只有 190m，远景不需要 clipmap LOD |
| **`CREST_SIMPLE_TRANSPARENCY=1`** | 省 CopyColor+CopyDepth 两个全屏 pass | 失去折射；需重新生成 shader | 移动端**强烈建议**，桌面可选 |
| 关 `WriteToColorTexture` | 省 CopyColor | 无折射 | 与上一条互斥（SimpleTransparency 时自动无效） |
| 关 `WriteToDepthTexture` | 省 CopyDepth | 岸边泡沫/深浅过渡失效 | 不建议，泡沫是主要视觉线索 |
| `LodLevels` 4→3 | chunk 49→37 | 远处精度下降 | 收益有限，先做上面两条 |
| 水下效果按需 | 水下 pass 只在入水时跑 | 无 | **已是现状**（引擎自带 >2m 关闭） |

**先做 Quad 还是先做 SimpleTransparency**：如果目标是 30fps 稳，
先上 `SimpleTransparency`（省的是带宽，移动端 tile GPU 上更值钱）；
如果目标是 draw call 数，先上 Quad。两者叠加是官方推荐的低端组合。

**必须先验证的**：Quad 模式下湖面是否还"像水"。
湖面平静、观察距离 300m，纯法线扰动可能足够；但若要做近景钓鱼视角的水面起伏，
Quad 就不够了 —— 那时应保留 Chunks 但把 `LodLevels` 压到 2~3。

### 13.6 一条容易误判的事

`WaterBody` 的 chunk 级剔除（`WaterBodyCulling=1`）对湖**几乎不起作用**：
实测 49 个 chunk 里 `_Culled=0`、`forceRenderingOff=0`。原因是水面 clipmap 跟着视点走，
外圈 chunk 尺寸巨大，AABB 必然相交。**真正把水面裁成湖盆形状的是 ClipLod**（§11.5），
不是 WaterBody。所以别指望靠 WaterBody 减少水面 chunk 数。

## 14. 性能模式：SimpleTransparency + Quad（2026-11-13 落地）

> 工具：`CrestPerformanceMode`（`ApplySimpleTransparency` / `ApplyQuad` / `ApplyBoth` / `Restore` / `Report`）
> 验证：`CrestRenderProbe.Run` 读 URP pass 队列 + `LakeWaterVerify.Run` 看画面。

### 14.1 实测收益

| 配置 | URP pass 总数 | CopyColor | CopyDepth | 水面 chunk | draw call |
|---|---|---|---|---|---|
| 原始（Chunks + 非 SimpleTransparency） | 4 | 1 | 1 | 49 | ~49 |
| **+ SimpleTransparency** | **1** | **0** | **0** | 49 | ~49 |
| **+ SimpleTransparency + Quad** | **1** | **0** | **0** | **0** | **1** |

两档画面都通过 `LakeWaterVerify` 6/6（浅水见底、水下吸收色调、水下有内容、
开关对照、水上不误开、水面有内容）。Quad 后浅水画面的标准差 26.9 / 色种 10044，
与 Chunks 的 26.9 / 10182 几乎一致 —— **视觉损失比预期小**，因为湖面平静，
"动"主要来自法线扰动的高光变化，几何起伏本来就看不出来。

### 14.2 SimpleTransparency 怎么改才对（三层，缺一不可）

1. **Crest Settings 资产**（`ProjectSettings/Packages/com.waveharmonic.crest/Settings.asset`）
   的 `_PlatformSettings*.SimpleTransparency = 1` + `_OverrideSimpleTransparency = 1`。
2. **scripting define** `d_Crest_SimpleTransparency`（影响 C# 的 `#if`，
   如 `SurfaceRenderer.Universal.cs:61` 的 `ConfigureInput`）。
3. **重新生成** `Runtime/Shaders/Library/Settings/Settings.Crest.*.hlsl`
   （`#define CREST_SIMPLE_TRANSPARENCY (1)`，影响 shader）。

**踩坑（全部实测）**：
- **不能只改 hlsl 文本**：`ShaderSettingsGenerator` 会在脚本重载时覆盖手改内容。
- **`AssetDatabase.LoadAllAssetsAtPath` 读不到**：该资产在 `ProjectSettings/` 目录，
  不在 `Assets/` 下，AssetDatabase 不索引它。
- **`SerializedObject` + `ScriptableSingleton.Save()` 无效**：内存里回读是 True，
  但落盘后文件仍是 0（`OnEnable` 的 hideFlags 与内部序列化路径不一致）。
  **正确做法是直接改文本**。
- **改完文本必须重载单例再调 `UpdateSymbols()`**：否则它读内存里的旧单例，
  会把 define 写回旧值（实测：文本 True → define 被写成 False）。
  逻辑照抄 Crest 自己的 `FileSystemWatcher.OnChanged`（`ProjectSettings.cs:440-455`）：
  销毁单例 → 清 `s_Instance` → 重新取（从磁盘反序列化）→ `UpdateSymbols()`。
- **`UpdateSymbols()` 内部只请求重编译**，真正的 hlsl 生成在域重载后
  `[DidReloadScripts]` 才发生。batchmode 单进程内不触发，
  必须直接反射调 `ShaderGeneratorUtility.GenerateAll()` 并等待完成。

### 14.3 为什么必须同时关 URP 资产的 Depth/Opaque 纹理

只改宏只让 **Crest 自己**不再请求。但 `Assets/Settings/UniversalRP.asset` 上
`m_RequireDepthTexture: 1` / `m_RequireOpaqueTexture: 1` 会让 **URP 对所有相机**
无条件 copy（`UniversalRenderPipeline.cs:1243-1244`）。
不关这两项，省下的只是 Crest 那 1 份，等于白改。

**关之前必须确认没有别的 shader 依赖**：已检查草 BRG（`Assets/Grasses/GrassBRG.shader`
不采样这两张图）、URP 资产自身。GTS 地表与树材质是 ShaderGraph 产物，
在 SRP Batcher 下正常不采样。Gaia/GeNa 的 VFX shader 有依赖但**未接入**
（AGENTS.md §2），接入时必须重新打开这两项。

### 14.4 SimpleTransparency 下必须调 `_Crest_TransparencyMinimumAlpha`

`Fragment.hlsl:825-828` 在 SimpleTransparency 分支里用
`max(length(volumeOpacity), _Crest_TransparencyMinimumAlpha, foam, albedo.a)` 写 `o_Alpha`，
该属性默认 **0.95** —— 水面最多 5% 透光，等于不透明。
本工程取 **0.12**（`CrestPerformanceMode.SimpleTransparencyMinAlpha`）。
这是切到 SimpleTransparency 后**最容易漏的一步**：不改就会看到"水面变实了"。

### 14.5 还剩下的开销

当前配置：`LodLevels=5`，Depth/Foam/Clip/AnimatedWaves/Level=128，ShapeGerstner=96，
Depth SDF 关闭。每帧 compute 由以下几部分组成：

- **DepthLod**：每个与湖区 AABB 相交的 active Terrain 一次 `DepthTexture.compute`（16×16×5）。
  `CrestRenderProbe` 实测：`Terrain.activeTerrains`（与 DepthLod 的 `Terrain.GetActiveTerrains` 同源）=15，与湖 AABB 相交=6；
  过滤后 Depth dispatch 从 15 降到 6（实际数随 SECTR 加载的 chunk 数变化）。湖外 Terrain 不再每帧重烘，
  这是之前最大的一笔可消除浪费。
- **AnimatedWaves**：Gerstner 生成（96²，z = 有效 cascade 数）、ShapeWavesTransfer（96²×5）、
  5 次 ShapeCombine（128²）、PackLevel（128²×5）。Quad 下这些只喂法线，不能直接关。
- **Foam**：`UpdateFoam` 每帧至少 1 次（`PersistentLod` 在 substeps==0 时也会做 delta=0 dispatch）。
  降 `SimulationFrequency` 只减少完整仿真步，**不减少 dispatch 次数**。
- **Clip**：`ClipPrimitive` 1 次（16×16×5）+ 2 次 `ClearBorder`（很小）。
- **Underwater**：`Underwater.Enabled=true`，入水时才有 pass（>2m 自动关），非入水零成本。
- **Queries**：没有 gameplay 注册查询时 0 次 dispatch（`Query.UpdateQueries` 检查 QueryCount）。

**Crest 源码补丁**：`DepthLod.TerrainDepthInput` 增加了 Include WaterBody AABB 过滤。
升级 Crest 时必须重打；否则会退回“每个 active Terrain 每帧一次”。
过滤只在水面 `EverythingClipped` 时生效；大洋模式（默认不裁）保留全部 Terrain。

**49 → 0 chunk 的连带收益**：`WaterChunkRenderer.Bind()` 里的 MPB + SH 采样
（`WaterChunkRenderer.cs:185-202`）全部消失，CPU 侧也省了一笔。

### 14.6 还原

`-executeMethod CrestPerformanceMode.Restore` 会把三项全部回退：
Chunks + 非 SimpleTransparency + URP Depth/Opaque 开启 +
`_Crest_TransparencyMinimumAlpha = 0.95`。
