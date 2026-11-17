# Assets/Enviro 3 - Sky and Weather 规约（第三方插件）

> **第三方源码，除下方标记的补丁外不要改动**。
> 接入方式与质量档见 [atmosphere](../../Docs/atmosphere.md) §0；工具在 [../Editor/Atmosphere/AGENTS.md](../Editor/Atmosphere/AGENTS.md)。

## 1. 版本

- 运行时版本串：`EnviroManagerBase.version => "3.3.2"`（代码事实）。
- `version.txt` 首行仍是 `Enviro 3.2.0`，但同一文件的更新记录末尾是 **v3.3.2b**，项目按 **3.3.2b** 使用。
- 升级前先确认 URP 14（Unity 2022.3）分支仍编译；本包同时带 URP17 / HDRP 代码。

## 2. 目录

| 路径 | 内容 |
|---|---|
| `Scripts/Runtime/` | 运行时：Manager、Time/Lighting/Sky/Fog/Weather、URP RendererFeature |
| `Scripts/Editor/` | 编辑器：配置/天气/质量的 Inspector 与 updater |
| `Scripts/ThirdPartySupport/` | WAPI / 其他集成，项目未使用 |
| `Profiles/Configurations/` | `Default Enviro Configuration 3_3_2.asset` |
| `Profiles/Quality/` | `Low/Medium/High/Insane/Reflection`；项目质量档见 [Settings §2](../Settings/AGENTS.md) |
| `Profiles/Weather Types/` | 9 个天气预设 |
| `Prefabs/` + `Enviro 3.prefab` | 场景实例化源 |
| `Resources/Shader/` | 运行时 shader |

## 3. 本项目唯一补丁

`Scripts/Runtime/Base/Renderer/EnviroURPRenderPass.cs` 构造函数：

```csharp
renderPassEvent = RenderPassEvent.AfterRenderingTransparents;
```

- 原值 `BeforeRenderingTransparents - 1`；补丁原因与代价见 [atmosphere §0.4](../../Docs/atmosphere.md)。
- 标记 `[FishDemo 2026-11-15]`；**升级 Enviro 会丢，必须重打**。
- Unity 6 的 RenderGraph 分支（`EnviroURPRenderFeature` 内 `#if UNITY_6000_0_OR_NEWER`）使用 `BeforeRenderingTransparents -1`；本工程是 2022.3，走的是非 RenderGraph 分支，不受该差异影响。

## 4. 接入硬约束

`ENVIRO_URP` 缺了会让 `EnviroURPRenderFeature` 整个类型被 `#if` 掉，天空/雾/云静默失效；必须手动定义（Android / Standalone 已有）。

其余接入规则（`LoadConfiguration`、depth/opaque、质量档、水下雾）见 [Atmosphere/AGENTS.md](../Editor/Atmosphere/AGENTS.md) 与 [atmosphere §0](../../Docs/atmosphere.md)。

## 5. 相关文档

| 文档 | 何时读 |
|---|---|
| [atmosphere](../../Docs/atmosphere.md) §0 | 选型、接入点、质量档、验收数据 |
| [../Editor/Atmosphere/AGENTS.md](../Editor/Atmosphere/AGENTS.md) | `AtmosphereSetup` 的 Apply/Remove 与重跑顺序 |
| [../Settings/AGENTS.md](../Settings/AGENTS.md) | URP Asset / RendererFeature / 质量档 |
| [rendering_rules](../../Docs/rendering_rules.md) | 改 pass 顺序或 depth 前 |
