# Assets/Settings 规约（URP / Enviro 资产）

> 这三个资产是渲染配置的**事实源**。改之前先读 [rendering_rules](../../Docs/rendering_rules.md) §1 与 [atmosphere](../../Docs/atmosphere.md) §0。

## 1. 资产清单

| 资产 | 作用 | 关键当前值（代码事实） |
|---|---|---|
| `UniversalRP.asset` | 全局管线设置 | `m_RequireDepthTexture=1`、`m_RequireOpaqueTexture=0`、`m_UseSRPBatcher=1`、`m_MSAA=1`、`m_SupportsHDR=1`、主光阴影 2048、附加光 per-object 4、`m_ShadowDistance=50`、软阴影关 |
| `UniversalRenderer.asset` | Forward Renderer + Feature | `m_RenderingMode=0`（Forward）、`m_DepthPrimingMode=0`、`m_IntermediateTextureMode=1`；子资产 `Enviro Render Feature`（`m_Active=1`） |
| `Enviro Mobile Quality.asset` | Enviro 移动端质量档 | 体积云关、Fog `Simple + volumetrics + qualities 0 + steps 32`、Flat clouds 开（shadow steps 4）、Aurora 关、粒子发射率 0.5 |

## 2. 硬约束

1. **Forward，不切 Forward+**：`m_RenderingMode=0`。200 人 VAT 人群已在 Forward 下渲染 PASS；切 Forward+ 会影响 per-object 光照与 VAT，见 [gpu_ecs_animation_baker](../../Docs/gpu_ecs_animation_baker.md) §1.2。
2. **SRP Batcher 必须开**，静态/动态合批保持关闭；规则见 [rendering_rules](../../Docs/rendering_rules.md) §3。
3. **depth/opaque 不得随意改**；值见 §1，消费者与禁止项见 [rendering_rules](../../Docs/rendering_rules.md) §1.2。
4. **不叠第二套后处理**；规则见 [postprocess](../../Docs/postprocess.md) §0。
5. **`Enviro Mobile Quality.asset` 是移动端质量档唯一来源**：不要在 `World.unity` 的 `AtmosphereController` 里再覆盖一份。改质量档后跑 `AtmosphereVerify` 验收。

## 3. 相关文档

| 文档 | 何时读 |
|---|---|
| [rendering_rules](../../Docs/rendering_rules.md) | 改管线 / pass / depth / opaque |
| [atmosphere](../../Docs/atmosphere.md) §0 | 质量档字段、Enviro 接入 |
| [water](../../Docs/water.md) §6 | Crest 的 depth / 透明依赖 |
| [postprocess](../../Docs/postprocess.md) | 调色、Uber pass、LUT |
| [../Enviro 3 - Sky and Weather/AGENTS.md](../Enviro%203%20-%20Sky%20and%20Weather/AGENTS.md) | Enviro 补丁与接入约束 |
