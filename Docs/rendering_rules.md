# 渲染与批处理规约

> 写渲染代码前必读，全文约 80 行，建议一次读完。性能数值见 [performance_budget](performance_budget.md)。

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
| BRG | 大规模实例（2022.3 需手写） | culling 在 Job 里做 |
| 静态合批 | 完全静态、不可剔除的小物件 | 会增大内存，大世界慎用 |
| 动态合批 | — | **保持关闭**（SRP Batcher 下有害） |

**判定标准**：任何新增渲染内容，必须说明它走哪一条批处理路径。说不出来就是设计缺陷。

## 3. 光照约束

- 主光：单一 directional，按时间曲线驱动角度/颜色/强度。
- 夜晚 additional lights ≤ 6，其余靠 **emissive + Bloom**。
- 2022.3 无 Forward+，per-object light limit 为 **4**（`UniversalRP.asset: m_AdditionalLightsPerObjectLimit`）。

## 4. 禁止事项

- ❌ 假设 URP 14 有 Forward+ / RenderGraph / 内置水体 / 内置体积雾。
- ❌ 每帧无条件开水面反射相机。
- ❌ 依赖 Terrain 自带 grass/detail 做大规模植被（CPU 提交）。
- ❌ 在 `Update` 里逐对象设材质属性（破坏 SRP Batcher）。
- ❌ 运行时逐像素算调色 / 大气散射。
- ❌ 用 `Resources.Load` 做流式加载。
- ❌ 在 Gameplay 层直接写 `CommandBuffer`。

## 5. 代码注释要求

渲染相关代码必须注明：
1. 依赖的 URP 版本行为
2. 是否依赖深度图
3. 能否在低端设备降级
4. 非显而易见的实现原因
