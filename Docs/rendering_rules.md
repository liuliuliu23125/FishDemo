# 渲染与批处理规约

> 写渲染代码前必读，全文约 84 行，建议一次读完。性能数值见 [performance_budget](performance_budget.md)。

## 1. 架构分层

```
Gameplay → World Streaming（chunk + 流式）
         → Render Data（BRG / 实例 buffer）
         → URP（Renderer + RendererFeature）
              ├ Opaque（地形/植被/建筑/角色）
              ├ Water（反射 / 水下）
              ├ Atmosphere（天空/雾/体积）
              ├ Transparent
              └ Post（Bloom/Tonemap/LUT/AA/水下）
```

**硬性约定**：Gameplay 层不得直接操作 `CommandBuffer` 或 `Graphics.*`。所有渲染数据通过 Render Data 层的接口提交。

## 2. 批处理优先级

**SRP Batcher（必须）→ GPU Instancing（必须）→ BRG（目标）→ 静态合批（谨慎）**

| 手段 | 适用 | 注意 |
|---|---|---|
| SRP Batcher | 所有材质 | 必须共享 shader + 相同 keyword 组合 + 相同 cbuffer 布局 |
| GPU Instancing | 同 mesh 同材质的大量对象（植被、岩石、鱼群） | 走 `Graphics.DrawMeshInstancedIndirect` |
| BRG | 大规模实例 | **草已落地**：`GrassBRGManager`，剔除在 Burst Job 里做 |
| 静态合批 | 完全静态、不可剔除的小物件 | 会增大内存，大世界慎用 |
| 动态合批 | — | **保持关闭**（SRP Batcher 下有害） |

**当前各类内容走哪条路径**（排查批次问题先看这张表）：

| 内容 | 路径 |
|---|---|
| 地表（GTS mesh） | SRP Batcher |
| 树（Terrain 自带，46.5 万实例） | Terrain 渲染 + SRP Batcher |
| **草（1265 万实例）** | **BRG**，不经过 SRP Batcher |

**判定标准**：任何新增渲染内容，必须说明它走哪一条批处理路径。说不出来就是设计缺陷。

### 2.1 SRP Batcher 的实际经验（2026-11-09）

地形 mesh 化后吃过一次完整的 SRP Batcher 排查，结论值得复用：

| 认知 | 说明 |
|---|---|
| 合批单位是 **CBUFFER 布局**，不是材质对象 | 36 个不同材质（不同贴图）只要 `UnityPerMaterial` 布局一致就能合批 |
| **贴图不参与 CBUFFER** | 所以"每块地形不同贴图"不妨碍合批，不需要 MaterialPropertyBlock |
| `MaterialPropertyBlock` **会拆批** | 它让每个 renderer 的常量不同，SRP Batcher 判定状态不一致 |
| per-object 数据不能进材质 CBUFFER | 如 `[PerRendererData]` 标量，会让批次内不一致 |
| 静态合批与 SRP Batcher **互相干扰** | 静态合批先合并成大 mesh，破坏 SRP Batcher 的归并粒度，二选一 |

具体改造过程见 [gts_srp_batcher](gts_srp_batcher.md)。

## 3. 光照约束

- 主光：单一 directional，按时间曲线驱动角度/颜色/强度。
- 夜晚 additional lights ≤ 6，其余靠 **emissive + Bloom**。
- 2022.3 无 Forward+，per-object light limit 为 **4**（`UniversalRP.asset: m_AdditionalLightsPerObjectLimit`）。

## 4. 禁止事项

- ❌ 假设 URP 14 有 Forward+ / RenderGraph / 内置水体 / 内置体积雾。
- ❌ 每帧无条件开水面反射相机。
- ❌ 依赖 Terrain 自带 grass/detail 做大规模植被（CPU 提交）。
  **草已迁到 BRG（2026-11-11）**：36 chunk / 1265 万实例，Terrain detail 已关
  （`DetailObjectDistance=0`）。见 [grass_brg_progress](grass_brg_progress.md)。
  新增植被内容仍不得回到 Terrain detail。
- ❌ 用 `drawTreesAndFoliage = false` 关草。它同时关树（46.5 万实例）。
  关草只用 `detailObjectDistance = 0`。
- ❌ 在 `Update` 里逐对象设材质属性（破坏 SRP Batcher）。
- ❌ 用 `MaterialPropertyBlock` 传 per-object 数据（会拆 SRP Batcher 批次）。
- ❌ 运行时逐像素算调色 / 大气散射。
- ❌ 用 `Resources.Load` 做流式加载。
- ❌ 在 Gameplay 层直接写 `CommandBuffer`。

## 5. 代码注释要求

渲染相关代码必须注明：
1. 依赖的 URP 版本行为
2. 是否依赖深度图
3. 能否在低端设备降级
4. 非显而易见的实现原因
