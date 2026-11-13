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
| 4. 接 Crest | `LakeCrestSetup.Apply` | 主场景加 `Crest Lake` 节点；禁用旧 Gaia Water；开 URP Depth/Opaque | `Remove` |

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

### 11.5 Crest 接入配置（最终，`LakeCrestSetup`）

| 配置 | 值 | 理由 |
|---|---|---|
| 位置 | `(192, 264, 96)` | 湖心 + 水位 |
| `LodLevels` | 4 | 默认 9 是无限海洋用的；湖半径 190m |
| `LodResolution` | 160 | 默认 384 对小湖过密 |
| `ScaleRange` | `(1, 16)` | 湖面不需要远视距放大 |
| **Surface Clipping** | `EverythingClipped` + `ClipLodInput(Cube)` | 见下：这是「只在湖盆出水」的唯一正确做法 |
| 模块 ON | AnimatedWaves / Foam / Depth / Level | 移动端 30fps 下的最小可用集 |
| 模块 OFF | DynamicWaves / Flow / Shadow / Albedo / **Absorption / Scattering** | 吸收/散射仿真对小湖没有视觉收益，且多两个每帧 pass |
| 平面反射 | **OFF** | 最贵的一项；Crest 无视锥剔除（§3） |
| WaterBody | `Include`，AABB 半边长 280m，`Affects=Volume+Physics` | 只管水下体积与物理限域，**不负责裁水面** |
| 水色 | absorption `(0.42,0.75,0.88,0.035)` / scattering `(0.10,0.42,0.50)` | 默认深海色配 30m 深湖盆会渲染成近黑 |
| URP | `m_RequireDepthTexture=1`、`m_RequireOpaqueTexture=1` | 水下/岸边泡沫依赖，不开**静默失效** |

**为什么必须用 Surface Clipping，而不是只靠 WaterBody**：
Crest 的水面是以**视点**为中心的 clipmap（`LateUpdatePosition` 把 `Position` 设为
`(视点.x, 水面y, 视点.z)`，各 chunk 相对它按 LOD 翻倍铺开，最外圈能到 ±数十公里）。
`WaterRenderer` 的 transform XZ 对水面**没有意义**；WaterBody 的 AABB 剔除是**整 chunk 粒度**，
挡不住巨型外圈 chunk（实测 49 个 chunk 只有 8 个被剔除，水面仍铺满全世界）。
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
2. **水面偏不透明**。材质是 `m_CustomRenderQueue: 3000` + `_BUILTIN_SrcBlend: 5`（SrcAlpha），
   透明混合管线是对的；但 absorption 恒定色模式下 alpha 输出接近 1，
   看不到水下地形。若要"能看见水底"，需要开 `Depth` 相关的水深透明度，
   或把 scattering 的 alpha 调低。**属于表现调优，不是功能缺陷。**
3. 截图里的**岸壁有拉伸条纹**（碗壁陡坡上 GTS 贴图被拉伸），
   是 30m 深碗的陡坡 + 256m/257 顶点的分辨率导致，属可接受范围。

### 11.9 遗留事项

- [ ] 真机（iQOO 16）实测帧率；Reflections / Underwater 的取舍要在真机上定
- [ ] 钓鱼玩法需要 CPU 波高查询时，开 `com.waveharmonic.crest.cpu-queries` 附加包
- [ ] Crest shader 变体数量未审计（Shader Control 待接入）
- [ ] 旧 Gaia Water 已禁用保留在场景中；确认不再回退后可删
- [ ] **水面仍会以视点为中心铺满全世界**（靠 Clip 裁掉），水面 chunk 数量不随湖缩小。
      若真机 draw call 压力大，可考虑改用 `WaterMeshType.Quad`（单四边形 + 自定义网格）
