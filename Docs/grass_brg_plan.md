# 草改 BRG 实现方案

> **本文件 341 行，是实施方案文档，动手前必须通读。**
> 目标：把草从 Terrain detail（CPU 提交）迁到 BatchRendererGroup（GPU 驱动剔除 + 单次提交）。
>
> 结论分三类：**代码事实**（file:line）、**实测数据**（日志/资产）、**待验证**（未实跑）。
> 相关背景见 [vegetation](vegetation.md)（选型）、[rendering_rules](rendering_rules.md)（批处理规则）、
> [world_implementation](world_implementation.md)（chunk 结构）。

## 0. 为什么必须改

**实测数据**（`Logs/Gaia/chunk-render-stats.txt`）：

| 项 | 数值 |
|---|---|
| 单 chunk detail 实例（全密度） | ≈570 万 |
| 36 chunk 合计 | **2.07 亿** |
| 当前渲染档（density=0.35、dist=70m）可见实例 | ≈136 万 |
| detail 数据分辨率 | 1024×1024（`WorldPipeline.cs:146`） |
| detail 原型数 | 7 个（`Logs/Gaia/tree-diag.txt`） |
| 原型形态 | **FBX mesh**（`Content Resources/Terrain Details/Mesh/*.fbx`） |

**为什么 Terrain detail 是瓶颈**（代码事实）：

- Terrain detail 由引擎原生 detail 渲染器绘制，**不进 SRP Batcher，也不进 BRG**；
- 即使 `drawInstanced=1`，仍是 CPU 按 patch 收集并提交；
- 136 万实例的提交开销与 patch 数量成正比，与"实际可见"无关——引擎没有 GPU 侧剔除。

BRG 的收益：**剔除在 GPU/Job 侧完成，draw call 与实例总量脱钩**。

## 1. 前置条件（不满足就别开始）

### 1.1 图形 API 必须显式配 Vulkan

**代码事实**：`ProjectSettings.asset` 的 `m_BuildTargetGraphicsAPIs` 里 Android 是
`m_APIs: 150000000b000000` + `m_Automatic: 1`——**Automatic 模式**。

**代码事实**：URP 14 的 BRG 参考实现 `BRGPicking.shader:31-32` 用的是：
```hlsl
#pragma target 4.5
#pragma exclude_renderers gles
```

→ **BRG shader 不能跑在 GLES 上**。

**已处理（2026-11-09）**：Android 图形 API 已改为**仅 Vulkan**。

| 项 | 改前 | 改后 |
|---|---|---|
| `m_APIs` | `150000000b000000` = `[Vulkan(21), OpenGLES3(11)]` | `15000000` = **`[Vulkan(21)]`** |
| `m_Automatic` | 1（允许自动回落 GLES3） | **0**（锁定） |

依据：`ProjectSettings.asset` 的 `m_BuildTargetGraphicsAPIs`（AndroidPlayer 条目）。
工具：`Assets/Editor/GraphicsApiSetup.cs`（`-executeMethod GraphicsApiSetup.SetAndroidVulkanOnly`，
另有 `Report` 只读检查）。用的是官方 `PlayerSettings.SetGraphicsAPIs` + `SetUseDefaultGraphicsAPIs`，
不手改 YAML。

> 改这一项会影响**整个工程的 Android 构建**，不只是草。若后续有必须跑 GLES 的机型需求，
> 需要重新评估（BRG 方案将不可用）。

### 1.2 必须自写 shader

**代码事实**：URP 14 全包只有 2 个 shader 含 `DOTS_INSTANCING_ON`（`BRGPicking` / `ObjectMotionVectors`），
**Lit/Unlit 系列都不支持 BRG**。

GTS 的 `PW_Details_Foliage_URP.shadergraph` 也不支持（是 ShaderGraph，无法加 `DOTS_INSTANCING_ON` 宏）。

→ 必须新写一个 URP 兼容 + BRG 兼容的草 shader。**这是本方案最大的工作量**。

### 1.3 静态合批已关闭

已由项目负责人关闭（见 [gts_srp_batcher §8.6](gts_srp_batcher.md)）。BRG 与静态合批互斥，无需额外处理。

## 2. 总体架构

```
编辑器期（一次性烘焙）                          运行时
─────────────────────────────              ─────────────────────────
TerrainData.GetDetailLayer(0..6)            GrassChunkManager
  → 遍历 1024×1024 每格                       ├─ 按 chunk 加载/卸载实例数据
  → 读高度 GetHeight(x,y)                     ├─ 构建 InstanceData[] (NativeArray)
  → 过滤 density/距离/坡度                    ├─ BRG.AddBatch(每 chunk 一个 batch)
  → 写 GrassInstance (pos, rot, scale, type)  ├─ OnPerformCulling 里做视锥/距离剔除
  → 存成 binary 资产（每 chunk 一个）          └─ 提交 BatchDrawCommand
                                              │
                                              └→ 草 shader (DOTS_INSTANCING_ON)
                                                 读 instance buffer 的矩阵/颜色
```

**为什么按 chunk 组织**：与现有 SECTR 流式对齐——chunk 加载时加载对应草数据，
卸载时移除 batch。避免"整个世界草常驻"。

## 3. 数据结构

### 3.1 实例数据（运行时）

```csharp
/// 32 字节，对齐到 16 的倍数便于 GPU 上传。
/// 不直接存 float4x4（64B），用压缩表示省一半带宽。
public struct GrassInstance
{
    public Vector3 position;   // 12B  世界坐标（含地形高度）
    public uint   packed;      // 4B   [0-7]type [8-15]scaleQ [16-31]yawQ
    public Vector3 normal;     // 12B  地表法线（用于贴合）
    public uint   color;       // 4B   RGBA8 tint
}
// 合计 32B × 136 万 ≈ 43MB（常驻 4 chunk 约 4.8MB）
```

**为什么存 normal**：草需要贴合坡面，否则陡坡上草会"插进地里"或"悬空"。

### 3.2 烘焙产物（磁盘）

每个 chunk 一个 `.bytes`：

```
Assets/Scenes/World/GrassData/<chunk 名>.bytes
  头部: [magic(4)] [version(4)] [count(4)] [boundsMin(12)] [boundsMax(12)]
  主体: count × 32B 的 GrassInstance
```

**为什么用 .bytes 而非 ScriptableObject**：纯二进制，加载快、可走 YooAsset 的 bundle，
不产生大量小资产。

## 4. 实现步骤

> **已定决策（2026-11-09）**：
> - 烘焙**按目标密度采样**，不存全量（见 §4.0）
> - **只做草，树后续单独处理**（树仍走 Terrain）

### 4.0 采样率与规模（已定）

> ⚠️ **本节结论已被 §4.1 与实测推翻，仅作推导过程留存。**
> 现行值是 **`SamplingRate = 0.035`**。首轮烘焙 1265 万实例 / 386MB；2026-11-12
> 密度减半后为 **435 万实例 / 133MiB**（`Logs/Grass/bake-report.txt`）。
> 本节「10% 采用」的推理错在：用「常驻内存量」对比「屏幕可见量」，两者不可比。

**实测基线**（`Logs/Gaia/chunk-render-stats.txt`）：

| 项 | 数值 |
|---|---|
| 36 chunk detail 总量（全密度全距离） | **207,116,244**（约 2.07 亿） |
| 单 chunk 均值 | ≈575 万 |
| detail 分辨率 | 1024×1024 × 7 层 = 7,340,032 格/chunk |
| 当前 Terrain 渲染档 | `density=0.35`、`dist=70m` |

**采样率对照**（`GrassInstance` = 32B）：

| 采样率 | 实例总数 | 磁盘 | 每 chunk | 常驻 4 块内存 |
|---|---|---|---|---|
| 100% | 207,116,244 | 6321 MB | 5,753,229 | 702 MB |
| 20% | 41,423,248 | 1264 MB | 1,150,646 | 140 MB |
| **10%（采用）** | **20,711,624** | **632 MB** | **575,323** | **70 MB** |
| 5% | 10,355,812 | 316 MB | 287,661 | 35 MB |

**为什么选 10%**：
- 当前 Terrain 可见实例约 136 万（`density=0.35` × 距离剔除后）。
- 10% 采样 → 常驻 4 块约 230 万，**与当前视觉密度同量级**，不会明显变稀。
- 磁盘 632MB、常驻内存 70MB，移动端可接受。
- 100% 的 6.3GB 纯属浪费——BRG 的剔除是运行时行为，数据存全量不带来任何视觉收益。

**采样规则**（不是简单丢 90%）：

```
对每层每格：
  n = detailLayer[x, y]                    // 该格实例数
  n' = round(n * SamplingRate)             // 按比例缩减
  if (n > 0 && n' == 0 && rand() < 0.3) n' = 1   // 保证稀疏处不出现空洞
  位置 = 格内均匀随机抖动                    // 避免网格状排列
  过滤：坡度 < 45°、高度 > 海平面 + 2m
```

> `SamplingRate = 0.10` 应做成 `GrassBaker` 的常量，便于后续调。

**验收**：36 个 `.bytes` 总实例数 ≈ 2000 万（±10%），单文件约 17.5MB。

### 步骤 1：烘焙工具（编辑器期）

新建 `Assets/Editor/GrassBaker.cs`：

```csharp
/// 从 TerrainData 提取 detail 数据并写成二进制资产。
/// 关键点：
/// 1. GetDetailLayer 返回 int[,]，每格是该层的实例数（不是布尔）。
/// 2. 每格内实例位置需要在该格范围内随机分布，否则草呈网格状。
/// 3. 必须用 TerrainData.GetHeight(x, y) 取真实高度，不能假设平地。
/// 4. 坡度过滤用 GetSteepness，太陡的地方草会悬空。
/// 5. 采样率 0.10（见 §4.0），带"稀疏处保底 1 个"规则。
public static class GrassBaker
{
    private const float SamplingRate = 0.10f;
    private const float MaxSteepness = 0.707f;   // 45° ≈ cos45
    private const float MinHeightAboveSea = 2f;
}
```

### 步骤 2：草 shader（最关键，工作量最大）

新建 `Assets/Grasses/Grass.shader`，要求：

| 要求 | 说明 |
|---|---|
| `#pragma target 4.5` | BRG 需要 |
| `#pragma exclude_renderers gles` | BRG 不支持 GLES（参考 `BRGPicking.shader:32`） |
| `#pragma multi_compile DOTS_INSTANCING_ON` | BRG 路径开关 |
| `UNITY_DOTS_INSTANCING_START(MaterialPropertyMetadata)` | 声明 per-instance 属性 |
| 顶点着色器读 `unity_ObjectToWorld` | BRG 通过它传每实例矩阵 |
| 支持 URP 主光 + 阴影 | 至少要 ForwardLit + ShadowCaster 两个 pass |
| 风摆动画 | 用顶点偏移，避免 CPU 逐帧改数据 |

**per-instance 属性声明方式**（BRG 的标准写法）：

```hlsl
UNITY_DOTS_INSTANCING_START(MaterialPropertyMetadata)
    UNITY_DOTS_INSTANCED_PROP(float4, _GrassColor)
    UNITY_DOTS_INSTANCED_PROP(float,  _GrassScale)
UNITY_DOTS_INSTANCING_END(MaterialPropertyMetadata)
```

> **待验证**：URP 14 + BRG 的完整 shader 能否编译通过、能否拿到主光阴影。
> 建议先用一个最小 shader（纯色 + 无光照）验证 BRG 链路，再加光照。

### 步骤 3：运行时管理器

新建 `Assets/Scripts/Runtime/Rendering/GrassChunkManager.cs`：

```csharp
/// 每个 chunk 一个 batch。
/// 注意：这个脚本要引用 BatchRendererGroup（UnityEngine.CoreModule），
/// 可以放进 asmdef 目录（BRG 不是第三方包，asmdef 能引用）。
public class GrassChunkManager : MonoBehaviour
{
    // 1. 创建 BRG，注册 OnPerformCulling 回调
    // 2. 每个 chunk：GraphicsBuffer 存 GrassInstance[]
    // 3. AddBatch(batchID, buffer, bounds, material)
    // 4. OnPerformCulling：
    //    - 读 cullingContext.cullingPlanes 做视锥剔除
    //    - 按距离剔除（超出 dist 不画）
    //    - 输出 BatchDrawCommand
}
```

**关键实现点**：

| 点 | 说明 |
|---|---|
| `OnPerformCulling` 必须 Burst 化 | 否则主线程卡顿；用 `[BurstCompile]` + `IJobParallelFor` |
| 剔除在 Job 里做 | 遍历实例、测视锥、写可见索引 |
| 每 chunk 一个 `BatchID` | 便于按 chunk 卸载 |
| `GraphicsBuffer` 用 `Structured` | 存 `GrassInstance[]` |
| 与 SECTR 联动 | chunk 加载 → `AddBatch`；卸载 → `RemoveBatch` |

**与 SECTR 的接线点**：`SECTR_Chunk` 有 `Changed` 回调（`SECTR_Chunk.cs` 里的 `LoadCallback`），
可在 chunk 加载/卸载时通知 `GrassChunkManager`。**但 SECTR 是第三方代码，不要改**——
用 `SECTR_Chunk.Changed` 事件订阅即可（待验证该事件是否在运行时可靠触发）。

### 步骤 4：关闭 Terrain 的草

**关键**：不能只加 BRG，必须同时关掉 Terrain 的 detail 绘制，否则**草画两遍**。

```csharp
terrain.drawTreesAndFoliage = false;   // 关掉树+草
// 或更精细：只关草
terrain.detailObjectDistance = 0;      // 距离 0 = 不画 detail
```

> 注意 `drawTreesAndFoliage` 同时管树和草。树目前仍走 Terrain，所以**用
> `detailObjectDistance = 0` 只关草**，保留树。

### 步骤 5：验证

| 验证项 | 方法 | 通过标准 |
|---|---|---|
| BRG 链路通 | 最小 shader + 少量实例 | Frame Debugger 出现 `BRG` 分组 |
| 剔除正确 | 移动相机 | 视野外实例不提交 |
| 与 Terrain 视觉一致 | 同位置对比 | 草的位置/密度/高度贴合地面 |
| 阴影正确 | 观察草影 | 有阴影且不闪烁 |
| 性能 | 真机 Profiler | 草项 GPU 时间在预算内（见 [performance_budget](performance_budget.md)） |
| 无重复绘制 | Frame Debugger | Terrain detail 不再出现 |

## 4.1 几何预算实测（2026-11-10，**推翻原方案的隐含假设**）

> 这一节是回答「草走 BRG 还是 GPU Instancing」时必须先看的数据。
> 源数据：`Logs/Grass/grass-budget.txt`（脚本 `GrassBudgetProbe.Run`）。

**实测（单 chunk，100% 密度全距离）**：

| 项 | 数值 |
|---|---|
| 实例总数 | 5,772,941 |
| 三角形总数 | 823,536,785 |
| **每实例加权平均三角形数** | **142.7** |
| 各层分布 | 主草 404 万（128 tri）、Daisy 45 万（322 tri）、Dandelions 49 万（234 tri）、Clover 73 万（60 tri） |

**关键推论 1：BRG 与 GPU Instancing 在顶点带宽上完全相同。**
两者提交的是同一批顶点，`可见实例数 × 每实例三角形数` 这个乘积不受批处理方案影响。
BRG 只优化「提交与剔除」，从不能减少顶点吞吐。
→ **用户担心的"顶点太多撑爆带宽"成立，但换批处理方案解决不了，只有 LOD 能解决。**

**关键推论 2：按 1.5M 三角形/帧预算，当前 mesh 只够 10,515 个实例。**
`1,500,000 / 142.7 ≈ 10,515`。而当前渲染档一个 chunk 就有 37,768 可见实例
（`density=0.35`、`dist=70m`），**超预算 3.6 倍**。

**关键推论 3：真正的手段是距离 LOD，不是 BRG。**

| 距离段 | 实例占比 | 方案 | 三角形/实例 |
|---|---|---|---|
| 0–15m | ~5% | 全模 | 142.7 |
| 15–40m | ~28% | LOD1 | ~30 |
| 40–70m | ~67% | billboard（2 tri） | 2 |

加权后 ≈ **16 tri/实例**（降 8.7 倍）→ 预算内可容纳 ≈ 93,700 实例，覆盖当前档的 2.5 倍余量。

**关键推论 4：原方案 §4.0 的采样率口径有误。**
原文用"常驻 4 chunk 230 万 ≈ 当前可见 136 万"论证 10% 采样「视觉同量级」，
但**常驻内存量 ≠ 屏幕可见量**。正确对比是屏幕可见密度：
10% 烘焙 vs 当前 `density=0.35`，可见密度只有 **28.6%**，会明显变稀。
→ 若要保持当前视觉密度，采样率应为 **0.35**（与 `detailObjectDensity` 对齐），
或改烘焙策略（见 §4.0 修订）。

## 4.2 距离 LOD 实测（2026-11-10，M1.5 已完成）

> 代码：`GrassBRGManager.BuildLodMeshes` / `CullJob.ResolveLod`
> 验证：`GrassLodVerify.Run` → `Logs/Grass/lod-verify.txt`

**实现方式：一个 batch、一个 instance buffer、三个 mesh、最多三条 draw command。**
LOD 切换只换 `BatchDrawCommand.meshID`，实例变换数据只存一份，不复制。

| 层 | 屏幕像素阈值（草高） | mesh | 三角形 | 1080p/60° 对应距离 |
|---|---|---|---|---|
| LOD0 | ≥ 100 px | 程序化 72 段草簇 | 144 | ≤ 8.7 m |
| LOD1 | ≥ 25 px | 程序化 12 段草簇 | 24 | ≤ 35 m |
| LOD2 | ≥ 8 px | 十字面片 | 4 | ≤ 110 m |
| 剔除 | < 8 px | — | 0 | > 110 m |

阈值用**屏幕像素高度**而不是固定米数，换分辨率/机型时视觉密度不漂移。
换算：`pixelH = nominalHeight × scale × screenH / (2·tan(fov/2)·dist)`。

**实测分层（`lod-verify.txt`）**：

| 距离 | LOD0 | LOD1 | LOD2 | 可见 | 三角形 | 预算占用 |
|---|---|---|---|---|---|---|
| 6 m | 28 | 209 | 0 | 237 | 9,048 | 0.6% |
| 20 m | 0 | 358 | 194 | 552 | 9,368 | 0.6% |
| 60 m | 0 | 0 | 1000 | 1000 | 4,000 | 0.3% |
| 100 m | 0 | 0 | 498 | 498 | 1,992 | 0.1% |

**LOD 相对收益（同视角 A/B 实测）**：

| 视角 | 开 LOD | 关 LOD | 节省 |
|---|---|---|---|
| 近 6 m | 36.2 tri/实例 | 144.0 | **74.9%** |
| 中 20 m | 17.0 tri/实例 | 144.0 | **88.2%** |
| 远 60 m | 4.0 tri/实例 | 144.0 | **97.2%** |

按距离占比加权约 **87% 节省**，与 §4.1 预估的 8.7 倍一致。

**M1.5 的已知简化（M3 必须处理）**：
1. LOD0 是程序化 72 段草簇（144 tri），**不是真实草种资产**。真实资产加权 142.7 tri，
   量级一致，所以预算统计有效；但视觉上是占位。
2. LOD1 全草种共用一个 24-tri 草簇，**中距离所有草种长得一样**。
   方案：M3 用 Amplify Impostors 做逐类 billboard（见 [vegetation](vegetation.md)）。
3. **无 crossfade**：LOD 切换有跳变。`BatchDrawCommandFlags.LODCrossFade` 可用，
   但需要每实例 8bit fade —— M3 再补。

**踩坑记录（都会导致"草消失"且无报错）**：
1. `nominalHeight` 必须乘 `_scaleMultiplier`。漏乘时 8 倍缩放的草按 1m 估算，
   119m 处算出 3.7px < 8px 阈值 → 整批被剔除（实测踩过）。
2. 验证脚本的 RT 分辨率必须与阈值设计档位一致。512px RT 会让像素阈值对应
   一半的距离，20m 就切到 LOD1，看起来像 LOD0 失效。
3. 关 LOD 时 `CullJob` 只能跑一遍。跑三遍会把同一批实例写三次，
   `visibleOffset` 全部重叠 → 画面全黑（实测：可见 3000 但 0 像素）。
4. `[ExecuteAlways]` 下 `AddComponent` 会立刻触发 `OnEnable → Initialize()`，
   此后改字段不会重建。所有 `Configure*ForValidation` 必须内部先 `Shutdown()`。
5. **绝不能在 Job 内 `Dispose()` native container**：
   `InvalidOperationException: The native container may not be deallocated on a job.`
   正确做法是给字段加 `[DeallocateOnJobCompletion]`，由 Job 系统在完成时释放。
   **这个坑特别隐蔽**——异常发生在数据已经写完之后，画面完全正常，
   只在 Editor.log 里刷异常（实测 222 条）。只看自输出的验证 txt 会完全漏掉，
   必须同时检查 Editor.log。

## 5. 风险与对策
| 风险 | 影响 | 对策 |
|---|---|---|
| **BRG 不支持 GLES** | Android 若 fallback 到 GLES，草消失 | 显式配 Vulkan（§1.1），构建后检查日志 |
| **URP 14 + BRG 的 shader 兼容性未验证** | 可能拿不到主光/阴影 | 先做最小 shader 验证，再逐步加光照 |
| **自写 shader 工作量大** | 可能 1~2 周 | 先用无光照纯色验证链路，光照后加 |
| **Burst 编译依赖** | 剔除 Job 需要 Burst | 已满足：`com.unity.burst 1.8.29` 作为 `entities` 的间接依赖存在（`packages-lock.json`，depth=1）。但**未在 `manifest.json` 直接声明**——asmdef 要直接 `using Unity.Burst` 时需提升为直依赖 |
| **SECTR 联动不确定** | chunk 加载时草不出现 | 先用固定范围加载验证，再处理流式 |
| **烘焙数据量** | 全量 6.6GB 不可接受 | 已定按采样：详见 §4.0 与 §4.1 推论的密度对齐要求 |
| **与 Terrain 高度不一致** | 草浮空/埋地 | 烘焙时用同一份 TerrainData 取高度；地形已 mesh 化但 Terrain 仍保留 TerrainData |
| **顶点带宽超预算**（§4.1 新发现） | 1.5M 预算只够 1.05 万实例，当前档需 3.8 万 | **必须做距离 LOD**（全模/LOD1/billboard 三段），目标 ≤16 tri/实例。BRG 不解决此项 |

## 6. 分阶段落地建议

**不要一次做完**。建议 3 个里程碑，每个都能独立验证：

### M1：最小 BRG 链路（1~2 天）

- 固定 1000 个实例，硬编码位置
- 最小 shader（纯色，无光照无阴影）
- 验证：Frame Debugger 出现 BRG 分组、实例能画出来

**M1 通过才继续**——如果 BRG 在 URP 14 上跑不通，整个方案要重估。

> **已完成（2026-11-10，实测通过）**。
> 交付物：
> - `Assets/Grasses/GrassBRG.shader`（纯色 + DOTS_INSTANCING_ON，target 4.5 + exclude gles）
> - `Assets/Scripts/Runtime/Rendering/GrassBRGManager.cs`（`FishDemo.Rendering` asmdef，
>   `allowUnsafeCode=true`——工程全局 `allowUnsafeCode: 0`，BRG 输出是裸指针，必须独立 asmdef）
> - 已挂进 `Assets/Scenes/World.unity` 的 `GrassBRG (M1)` 节点，打开场景即可看到
> - 工具：`GrassM1Verify`（链路）、`GrassSceneSetup`（摆进场景）、`GrassSceneVerify`（场景内可见性）、
>   `GrassMeshStats` / `GrassBudgetProbe`（几何规模）、`WorldProbe`（地表探测）
>
> 实测结果（D3D11 设备）：
> - 链路：`Logs/Grass/m1-verify.txt` —— 正向 1000/1000 可见、非背景 6.78%、
>   屏幕覆盖 100%×57%、绿色占比 100%；相机背对时可见 0（视锥剔除生效）。
> - 场景内：`Logs/Grass/scene-verify.txt` —— 主相机洋红像素 229、诊断俯视 25071，
>   `LastVisibleCount=1000`。图见 `Logs/Grass/scene-camera-view.png`。
>
> 与方案的偏差（以实测为准）：
> 1. §3.1「GraphicsBuffer 用 Structured」**是错的**——`AddBatch` 在 D3D11/Vulkan 上
>    只接受 raw buffer（`BatchRendererGroup.BufferTarget == RawBuffer`），
>    必须用 `Target.Raw` + stride 4 + 元素数=字节数/4。
> 2. 每实例 `unity_ObjectToWorld` 是 48B 的 float3x4（§3.1 的 32B 压缩格式
>    留给 M2 的 GrassInstance，矩阵本身无法再压缩）。
> 3. 剔除输出数组必须 `UnsafeUtility.Malloc(..., Allocator.TempJob)`（引擎释放），
>    不能用 NativeArray。
> 4. `splitVisibilityMask` 不能为 0，相机视图单 split 时要写 `0x01`，否则整批被丢弃。
> 5. **必须加 `[ExecuteAlways]`**：否则编辑模式下 `OnEnable` 不触发，BRG 不创建，
>    打开场景看到一片空，会误判成 BRG 不工作（实测踩过，`LastVisibleCount=-1`）。
> 6. **Play Mode 下 SECTR 异步加载 chunk**，`OnEnable` 可能早于 Terrain 出现，
>    贴地会失败。已加 `GroundResolved` + 0.5s 重试（`TryReground`）。
>    表现为"草在但位置不对"，容易误判成矩阵布局错误。
> 7. 主相机是 420m 高空鸟瞰（俯角 25°），视锥下边缘只到 -55°。
>    "相机正下方"和"视线与地面交点(200m)"都不可行，M1 最终放在
>    俯角 -50° 的视锥内侧（118m 处）+ 8 倍缩放才肉眼可辨。
>
> **M1 的一个重要副产品**：见 §4.1——几何量实测推翻了"BRG 能解决性能问题"的隐含假设。

### M2：数据管线（2~3 天）

- `GrassBaker` 烘焙 1 个 chunk（采样率 0.10，见 §4.0）
- `GrassChunkManager` 加载并渲染该 chunk
- 验证：与 Terrain 草的位置/密度对比

> **已完成（2026-11-10，实测通过）**。
> 交付物：
> - `Assets/Scripts/Runtime/Rendering/GrassInstance.cs`：32B 磁盘格式
>   （pos 12B + packed 4B[type/scaleQ/yawQ] + normal 12B + color 4B）+ `GrassChunkData.Read`
> - `Assets/Scripts/Editor/GrassBaker.cs`：逐 detail 层采样 + 格内抖动 + 坡度/高度过滤，
>   产出 `Assets/Scenes/World/GrassData/<chunk>.bytes`。菜单 `⑧`
> - `GrassBRGManager`：支持注入烘焙数据（`SetChunkData`），
>   GPU 上传改为 **32B 语义数据 SoA**（_GrassPos / _GrassNormalScale），
>   shader 按需重建变换，比 48B 矩阵省 33% 带宽
> - 验证：`GrassM2Verify`（菜单 `⑩`），全部 PASS
>
> 实测（单 chunk，`Logs/Grass/m2-verify.txt`）：
> 214.9 万实例 / 65.6MB；最近邻 CV=0.538（非规则网格，地形疏密生效）；
> LOD 开启时首帧 1.3 万可见 / 11.7 万三角形（预算 7.8%）；
> 第一人称渲染 72% 屏幕被草覆盖（`Logs/Grass/m2-render.png`）。
>
> **与方案的偏差**：
> 1. 采样率用 **0.35**（§4.1 推论 4），不是本节原文的 0.10。
> 2. **必须按 16MB 切分多 batch**：`kMaxBytesPerBatchRawBuffer`（EntitiesGraphicsSystem.cs:1156）
>    限制单 batch 的 raw buffer ≤16MB = 52.4 万实例。超限的实测表现是
>    shader 读到垃圾 → NaN → **全黑且无任何报错**（214 万 × 32B = 65.6MB 踩过）。
>    现在 `CullJob` 按 batch 分段扫描，命令矩阵 = batch × LOD。
> 3. 多视野下 `_lastCullHandle` 必须 `job.Schedule(_lastCullHandle)` 串链（M1 踩过，M2 沿用）。

### M2.1 密度与几何修正（2026-11-10，实测后返工）

**问题**：M2 首版烘焙后，用户实测相机内约 **1000 万顶点**，草过密、糊成一片。

**根因（两个叠加）**：
1. **采样率 0.35 过高**。实测密度 8.21 株/m²，而 Terrain 当前渲染档实际可见
   仅约 0.14 株/m²（577 万 × 0.35 × 距离占比），高了近 58 倍。
   §4.1 推论 4 的「对齐 density=0.35」只对齐了**采样比例**，没对齐**可见密度**。
2. **场景节点残留 `_scaleMultiplier = 8`**。这是 M1 为了让草在 420m 高空相机下
   可见而设的权宜值，M2 接入烘焙数据后忘了改回。它同时把 LOD 的
   `LodPixelScale` 放大 8 倍（`_nominalHeight × _scaleMultiplier × ...`），
   导致 LOD0 的判定距离从 9.4m 膨胀到 75m —— **大量实例停留在最精细层**。
   旧的 LOD0 是 288 顶点/144 三角形，叠加后顶点量爆炸。

**修正**：
| 项 | 改前 | 改后 |
|---|---|---|
| 采样率 | 0.35 | **0.035** |
| 每实例几何 | 72 片平行面片（288v/144tri） | **草簇 12 叶（48v/24tri）** |
| LOD1 / LOD2 | 48v/24tri、8v/4tri | **24v/12tri、8v/4tri** |
| 场景 `_scaleMultiplier` | 8 | **1** |
| 单 chunk 实例 | 214.9 万 | **35.2 万** |
| 密度 | 8.21 株/m² | **1.34 株/m²** |
| 36 chunk 磁盘 | 2357 MB | **386 MB** |

**草簇设计**（`BuildClumpMesh`）：12 片叶子绕 Y 轴按黄金角分布、各自向外倾斜
（根部半径 0.04 → 顶部 0.22），高度/倾角用确定性伪随机抖动。
密度降低后单实例即一小丛，不会露白；且顶点比旧的 72 段方案少 6 倍。

**实测（`Logs/Grass/vertex-budget.txt`，1080p / 60° / 站姿 1.7m）**：

| 视角 | LOD0 | LOD1 | LOD2 | 可见 | 三角形 | 顶点 |
|---|---|---|---|---|---|---|
| 平视 0° | 128 | 1,451 | 13,511 | 15,090 | 74,528 | 149,056 |
| 俯视 15° | 137 | 1,434 | 13,213 | 14,784 | 73,348 | 146,696 |
| 俯视 30° | 146 | 1,352 | 5,968 | 7,466 | 43,600 | 87,200 |
| 环视 90° | 157 | 1,465 | 13,458 | 15,080 | 75,180 | 150,360 |
| 环视 180° | 191 | 1,501 | 14,002 | 15,694 | 78,604 | 157,208 |
| 环视 270° | 146 | 1,378 | 13,178 | 14,702 | 72,752 | 145,504 |

**单帧最大 15.7 万顶点**（原 1000 万，降 64 倍），三角形 7.9 万（预算 1.5M 的 5.2%）。
LOD 收益：近 44.9% / 中 61.7% / 远 83.3% 三角形节省。

**教训**：
- 「采样率对齐渲染密度」这个换算必须用**可见密度**，不能用采样比例本身。
- M1 阶段为验证而加的权宜参数（`_scaleMultiplier=8`）必须在新阶段显式清理，
  它会同时污染 LOD 距离换算，症状是「密度看似正常但顶点爆炸」。
- 验证脚本的相机规格必须与目标平台一致（1080p），否则像素阈值对应的距离会错。

### M2 踩坑记录
1. **DOTS 块名必须是枚举名**：`UNITY_DOTS_INSTANCING_START` 只接受
   `BuiltinPropertyMetadata` / `MaterialPropertyMetadata` / `UserPropertyMetadata`
   （官方文档 dots-instancing-shaders.html）。自定义名（如 GrassInstanceMetadata）
   **不报错但 metadata 全部失效**，per-instance 数据读到 0 → 草全部叠在原点/消失。
   自定义属性用 `UserPropertyMetadata`。
2. **访问宏必须 #ifdef 分路径**：`UNITY_ACCESS_DOTS_INSTANCED_PROP*` 全部定义在
   `UnityDOTSInstancing.hlsl` 内部（整体被 `#ifdef UNITY_DOTS_INSTANCING_ENABLED` 包住），
   非 DOTS 变体下这些宏不存在。URP 的做法（UnlitInput.hlsl:13-38）：
   DOTS 分支里读缓存 + `#define _Prop unity_DOTS_Sampled_Prop` 重定向；
   非 DOTS 分支用 static 常量兜底。
3. **SubmitRenderRequest 不等首次剔除 Job 完成**：214 万实例的 CullJob 约 80ms，
   首次渲染请求拿到空输出 → 全黑；第二次请求正常。
   验证脚本必须渲染两次（或等待 JobHandle）再回读像素。
   M1 的 1000 实例 Job 瞬间完成，从未暴露此问题。
4. **每帧 4 遍全量扫描的性能**：CullJob 现在是 分段3遍 + LOD统计1遍 = 4 遍。
   214 万实例 ≈ 80ms/帧（Burst 单线程）——功能正确但性能不可接受，
   M3 必须：统计并入分段扫描（顺带累计）+ 换 IJobParallelFor。
5. **烘焙数量核对**：577 万全量 × 0.35 = 202 万，实测 214 万（+6% 来自稀疏保底规则）。
   与 §4.1 推论一致。「高度剔除 0」是正确的——实测地形高度 249~283m，远高于海平面 62m。

### M3：全量 + 流式 + 光照（3~5 天）

> **2026-11-11 实际进展**（详见 [grass_brg_progress](grass_brg_progress.md)）：
> - 全量烘焙**已完成**，但规模与本文预估不同：首轮 **1265 万实例 / 386MB**，
>   采样率取 **0.035** 而非 §4.0 定的 0.10（0.35 实测视觉过密，见 `GrassBaker.cs:45-58`）；
>   2026-11-12 密度减半后为 **435 万 / 133MiB**。
> - 关 Terrain detail **已完成**（36 chunk `DetailObjectDistance=0`）。
> - shader 主光 + 环境光 + 风摆**已完成**；阴影/透光/贴图仍未做。
> - **SECTR 流式已完成（2026-11-17）**：草数据改原生内存 + 逐 chunk 加载。
>   真机验证仍未做——这是 M3 剩余的主体。

- ~~36 chunk 全量烘焙~~ ✅ 已完成（当前 435 万实例 / 133MiB）
- 接 SECTR 流式 ✅（2026-11-17，`SectrGrassStreamer`）
- ~~shader 加光照~~ ✅ 部分完成（主光 / 环境光 / 风摆；阴影 / 透光 / 贴图 ⬜）
- ~~关掉 Terrain detail~~ ✅ 已完成（用 `detailObjectDistance = 0`，保留树）
- 真机验证 ⬜

> **范围限定**：本方案**只做草**。树（46,472 实例）仍走 Terrain，后续单独处理。
> 因此 M3 关草时必须用 `detailObjectDistance = 0` 而不是 `drawTreesAndFoliage = false`
> （后者会把树一起关掉）。

## 7. 待确认的问题

动手前需要你确认：

1. ~~图形 API~~ **已完成**：Android 已改为仅 Vulkan、`Automatic=0`（§1.1）。
2. ~~Burst 依赖~~ **已确认可用**：`com.unity.burst 1.8.29` / `com.unity.collections 2.6.8` / `com.unity.mathematics 1.3.2` 均为 `entities` 的间接依赖（depth=1）。若代码要直接引用，需在 `manifest.json` 显式加（避免依赖传递性被上游改动破坏）。
3. ~~草的目标密度~~ **已定且已落地**：`SamplingRate = 0.035`，密度减半后
   **435 万实例 / 133MiB**。§4.0 原定 0.10（≈2000 万）已被推翻——0.35 实测视觉过密
   （8.21 株/m² vs Terrain 档 0.14），0.035 → 0.82 株/m²，见 `GrassBaker.cs:45-58`。
4. ~~树要不要一起改~~ **已定**：只做草，树后续单独处理。
5. **Vulkan 的机型覆盖**：仅 Vulkan 会排除不支持 Vulkan 的低端机。目标机型 iQOO 16 支持，
   但需确认是否要保留更低端的兼容策略（若要，BRG 方案不适用）。
6. **草的视觉验收标准**：`Logs/grass-closeup.png` 是当前唯一贴地视觉证据（近景 PASS），
   但尚无「与 Terrain 草 A/B 对比」的记录。M3 真机验证时需要补。

## 8. 参考

| 主题 | 文档 |
|---|---|
| 草/植被选型与 LOD | [vegetation](vegetation.md) |
| 批处理优先级 | [rendering_rules](rendering_rules.md) |
| 性能预算 | [performance_budget](performance_budget.md) |
| chunk 结构与流式 | [world_implementation](world_implementation.md) |
| 地形 mesh 化经验 | [gts_srp_batcher](gts_srp_batcher.md) |

### M2.2 多 chunk + 空间分块剔除（2026-11-10）

**问题（用户实测反馈）**：
1. 「不是每个 chunk 都有草」——`GrassSceneSetupM2` 早先只加载 `files[0]`，35 个 chunk 没接入。
2. 「只看见一部分」——单 chunk 仅 511m，且 LOD 在 117m 外剔除。

**修复**：
- `GrassBRGManager.SetChunkDataList`：多 chunk 实例合并成一个连续数组，
  batch 划分只由 16MB 容量决定（一个 batch 可跨 chunk）。
- `GrassSceneSetupM2` 加载全部 36 个 `.bytes`。

**性能问题与解决（关键）**：
> 以下数据测于 2026-11-10 的 **1265 万实例**。当前是 435 万，剔除耗时未重测；
> 只用于对比方案演进，不要当当前值引用。

接入 1265 万实例后，剔除耗时实测：

| 版本 | 扫描量 | 单帧耗时 |
|---|---|---|
| 4 遍扫描（原始） | 1265 万 × 4 | **2466 ms** |
| 2 遍扫描 + 前缀和 | 1265 万 × 2 | **1244 ms** |
| + 空间分块（32m）+ 视锥测试 | 193 万（15.3%） | **527 ms** |
| + 块级距离剔除 | **1.1 万（0.1%）** | **21.9 ms** |

**最终 21.9ms，降 112 倍**，可见实例数（2244）与结果完全一致，正确性未破。

**空间分块设计**（`SortInstancesIntoBlocks` + `CullJob.BlockVisible`）：
1. 运行时在合并后的全局数组上做**计数排序**（O(n)），按 32m 网格重排实例，
   生成块表（每块：起始索引 + 数量 + AABB）。
2. 剔除时先测块：**距离剔除（AABB 最近点 > 117m 直接跳过）** 优先于视锥测试。
   这一步是决定性的——只做视锥测试时 1384/9216 块「可见」（因为相机 far=3000m
   使视锥覆盖整个世界），加距离剔除后只剩 **8 块**。
3. 再对可见块内实例做逐实例视锥 + LOD 判定。

**为什么分块在运行时做而不是烘焙时**：
分块必须在合并后的全局数组上做，否则跨 chunk 的块会被切开。
烘焙是逐 chunk 独立的，无法预知合并顺序。运行时排序是一次性开销（1265 万实例约 1~2 秒，
435 万下同比例下降，未重测）。

**验证**：
- `GrassMultiChunkVerify`：36/36 chunk 全部有草（每个 1.5~3 万洋红像素），0 个空。
- `GrassPerfProbe`：`Logs/Grass/perf-probe.txt`。
- 回归：M1 / LOD / M2 / 多 chunk 四项全 PASS。

**遗留（M3）**：
- 分块排序在运行时做，加载耗时约 1~2 秒（1265 万实例旧测）。SECTR 流式后按需加载可摊薄。
- 内存：全量路径仍是 435 万约 320MB；**Play Mode 已改为逐 chunk 流式**，
  15 块驻留约 100MB，硬上限 24 块。见 [grass_brg_progress](grass_brg_progress.md) §4.5。
- `_positions` 与 `_instances` 重复存位置：流式路径已只保留 positions；
  编辑器全量工具路径仍保留旧结构。
