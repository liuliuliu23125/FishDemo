# Assets/Editor/Atmosphere 规约（Enviro 接入 / 昼夜天气 / HUD）

> 默认程序集 `Assembly-CSharp-Editor`，无 asmdef。
> 改大气前先读 [atmosphere](../../../Docs/atmosphere.md) §0；本目录只记录工具与重建顺序。

## 1. 脚本清单

| 脚本 | 职责 | 入口 |
|---|---|---|
| `AtmosphereSetup.cs` | 把 Enviro 3 接进 World/URP：宏、模块、质量档、RendererFeature、depth；幂等可撤 | `EnsureDefines` / `Apply` / `Remove` / `Report` |
| `AtmosphereVerify.cs` | **需真实图形设备**：Play Mode 验昼夜/天气/体积雾/光，8 张截图 + 状态断言 | `-executeMethod AtmosphereVerify.Run` |
| `AtmosphereTimeVerify.cs` | **可 `-nographics`**：验昼夜控制接口（设时刻 / 平移 / 暂停 / 流速 / 主光响应） | `-executeMethod AtmosphereTimeVerify.Run` |
| `HudPixelVerify.cs` | **需真实图形设备、且不能加 `-batchmode`**（batchmode 下 `OnGUI` 不被调用）：切窄屏抓真图，数 HUD 区域像素 | `-executeMethod HudPixelVerify.Run` |

## 2. 重跑流水线后恢复大气

重跑 `WorldPipeline` / `SectrPipeline` 后，`Enviro 3` 与 `Atmosphere` 根节点会消失，旧 `Directional Light` 的 `m_Enabled=0` 也会被重置。按顺序重做：

```
AtmosphereSetup.EnsureDefines   （干净导入/换机时一次）
  → AtmosphereSetup.Apply       （实例化 Enviro、LoadConfiguration、质量档、RendererFeature、depth=1）
  → AtmosphereSetup.Report      （确认只有 1 盏方向光、旧主光禁用、feature 已挂）
  → AtmosphereVerify.Run        （需真实图形设备）
```

## 3. 三个必须记住的坑

`ENVIRO_URP` 手动加、保存前 `LoadConfiguration()`、雾 pass 补丁升级会丢——细节见 [atmosphere](../../../Docs/atmosphere.md) §0.4，补丁位置见 [Enviro 包规约](../../Enviro%203%20-%20Sky%20and%20Weather/AGENTS.md) §3。

## 4. 质量档 ownership

- 移动端质量档的唯一资产与 ownership 见 [Settings §2](../../Settings/AGENTS.md)。
- `AtmosphereController` 负责时间流速、天气轮换、草风桥接、HUD 与水下雾开关；质量档见 [atmosphere](../../../Docs/atmosphere.md) §0.3，运行时操作见 §0.6。

## 5. HUD

IMGUI：宽度用 `ComputeHudWidth`、逐行 `MeasureBlock`；四个坑（字号/颜色/行高/内边距）见 [atmosphere](../../../Docs/atmosphere.md) §0.6。

## 6. 相关文档

| 文档 | 何时读 |
|---|---|
| [atmosphere](../../../Docs/atmosphere.md) §0 | 落地配置、质量档、验收数据、HUD |
| [rendering_rules](../../../Docs/rendering_rules.md) | 改 pass / RendererFeature 前 |
| [../World/AGENTS.md](../World/AGENTS.md) | 重跑世界流水线 |
| [../../Settings/AGENTS.md](../../Settings/AGENTS.md) | URP / Enviro 质量档资产 |
