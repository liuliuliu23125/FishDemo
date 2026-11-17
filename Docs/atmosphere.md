# 大气：Enviro 3 落地记录（昼夜 / 天气 / 体积雾光）

> Unity 2022.3.62f2 + URP 14.0.12，目标 iQOO 16 / 30fps。
> 当前用 **Enviro 3.3.2b** 一个包覆盖四项；本文只记录现状、约束与验收。
> 逐包选型分析已删除，结论见 §1。

## 0. 当前状态

### 0.1 选型

| 需求 | 实现 | 为什么够 |
|---|---|---|
| 昼夜 | `Time/Lighting/Sky`：太阳/月亮方向、颜色、强度、环境光、星空 | 主光、环境光、天空盒一站式 |
| 天气 | `Weather`：Clear/Cloudy 1-4/Foggy/Rain/Snow/Storm + 雨雪粒子 | 9 个预设够 demo |
| 体积雾 | `Fog` 的 volumetrics：Low = 1/4 res raymarch + 双边模糊上采样 | 湖面没有山谷/海面局部雾需求 |
| 体积光 | 体积雾 shader 采样主光阴影图（`EnviroVolumetricsURP.shader`） | 树影可形成柔和光柱；灯柱类对象尚无 |

### 0.2 接入点

| 文件/资产 | 职责 |
|---|---|
| `Assets/Enviro 3 - Sky and Weather/` | Enviro 3.3.2b 源码 |
| `Assets/Editor/AtmosphereSetup.cs` | 幂等工具：`EnsureDefines` / `Apply` / `Remove` / `Report` |
| `Assets/Scripts/Runtime/Gameplay/AtmosphereController.cs` | 质量档、时间流速、天气轮换、草风桥接、HUD |
| `Assets/Editor/AtmosphereVerify.cs` | Play Mode 验收：8 张截图 + 状态断言 |
| `Assets/Settings/Enviro Mobile Quality.asset` | 移动端质量档（**唯一来源**） |
| `Assets/Settings/UniversalRenderer.asset` | 子资产 `Enviro Render Feature` |
| `World.unity` 根节点 `Enviro 3` / `Atmosphere` | EnviroManager + Sun/Moon + Effects/Audio；`AtmosphereController` |
| `World.unity` 旧 `Directional Light` | Light 组件 `m_Enabled=0`，保留供 `AtmosphereSetup.Remove` 回滚 |
| `LakeCameraRig.Apply()` | 切相机时同步 `EnviroManager.ChangeCamera` |
| `GrassBRGManager.SetWeatherWind()` | 把风速/湍流映射到 7 个草类型材质 |

### 0.3 移动端质量档

| 项 | 值 | 原因 |
|---|---|---|
| Sky Mode | Simple | 跳过 front/back 多层颜色计算 |
| 体积云 | **关** | 默认 128 步 raymarch，与 30fps 冲突 |
| Flat clouds | 开，shadow steps 4 | 2D 云，天气预设带 coverage |
| Fog | `fogQualityMode=Simple`、`volumetrics=true`、`quality=Low`、`steps=32`、maxRange 600m | 1/4 res raymarch；height fog 合成另计 |
| Aurora | 关 | 不需要 |
| 粒子发射率 | 0.5 | 雨雪减半 |
| 全局反射探针 | 关 | 默认约 0.6s 渲一次 cubemap |
| 环境探针刷新 | 0.5s | 默认 0.1s 过重 |

### 0.4 三个接入期踩过的坑

1. **`ENVIRO_URP` 宏必须手动加**：Enviro 自带定义只加 `ENVIRO_3`；缺 `ENVIRO_URP` 时 `EnviroURPRenderFeature` 整个类型被 `#if` 掉，天空/雾/云**静默失效**。`AtmosphereSetup.EnsureDefines` 会加到 Standalone + Android；改完要重新编译。
2. **保存场景前必须 `EnviroManager.LoadConfiguration()`**：模块是编辑期从 configuration 资产赋值的引用，不是预制体对象。实例化后立刻保存会存成 null，Play Mode 下 `StartModules()` 直接跳过（实测全 False）。
3. **雾 pass 顺序被改到透明之后**：原始 `BeforeRenderingTransparents - 1` 会让 Crest 水面完全不受雾（雾天一条饱和绿带）。补丁在 `EnviroURPRenderPass.cs` 构造函数，标了 `[FishDemo]`；**升级 Enviro 会丢，必须重打**。

### 0.5 验收

```
Unity.exe -batchmode -projectPath E:\FishDemo -executeMethod AtmosphereSetup.EnsureDefines
# 重新编译后
Unity.exe -batchmode -projectPath E:\FishDemo -executeMethod AtmosphereSetup.Apply
Unity.exe -batchmode -projectPath E:\FishDemo -executeMethod AtmosphereSetup.Report
Unity.exe -batchmode -projectPath E:\FishDemo -executeMethod AtmosphereVerify.Run
```

2026-11-15 全项 PASS：正午天空顶部 1/3 平均亮度 0.629、午夜 0.047；雾天 `fogDensity=0.809`（晴 0.019）；`_EnviroVolumetricFogTex` 非空；启用方向光 = 1；旧主光已禁用。无实机 GPU 数据。

### 0.6 运行时操作

`AtmosphereController.OnGUI` 左上角：**最上面是昼夜/天气操作说明**，其下显示时刻/天气/流速；相机操作放在末尾。

**HUD 内容（自上而下）**：键盘操作提示标题 → 昼夜/天气/流速 → 按键说明。
托管堆状态已移出 HUD；`GCMode` 接管也已移除（大块数据改走原生内存 + 流式，
见 [grass_brg_progress](grass_brg_progress.md) §6）。

**IMGUI 的四个坑**（2026-11-17 逐个踩过）：
1. `<size=-2>` 不生效——IMGUI 只认绝对像素 `<size=16>`，相对值等于负字号，**整段不渲染**。
   小字改用独立 `GUIStyle` + 自己的 `fontSize`。
2. 默认文字色是深色，压在半透明黑底上等于看不见。必须显式 `textColor = Color.white`，
   并加 1px 深色投影（IMGUI 没有描边），否则压在树叶/天空上发虚。
3. 整块 `CalcSize` 会低估含 `
` 的行高，最后一行被裁半截。用 `MeasureBlock` 逐行量。
4. 宽度必须用 `ComputeHudWidth` 夹取；且绘制时的左右内边距要和它用同一个 `HudInset`，
   否则每行比可用宽度宽 2×`HudInset`，右端被裁。

验收：`HudPixelVerify.Run`（**需真实图形设备**，`ScreenCapture` 抓真图并数像素）。
该脚本必须在编辑器里跑，不能加 `-batchmode`——batchmode 下 `OnGUI` 根本不被调用。

| 键 | 作用 |
|---|---|
| `1`–`5` | 天气：Clear / Cloudy 2 / Rain / Foggy / Storm |
| `T` | 暂停/恢复时间 |
| `[` / `]` | ±1 小时 |
| `\` | 流速：20 / 5 / 2 / 1 分钟一天 |

`AtmosphereTimeVerify.Run`（可 `-nographics`）验时刻设置、主光响应、昼夜亮度差、暂停/流速。太阳落山后 Enviro 用**月亮**做主光，18:00 与 00:00 光向接近是正常行为。

### 0.7 天空盒 / CopyDepth / Blit

**天空盒**：Enviro 是唯一来源。

- 场景 `m_SkyboxMaterial` 是内联 `Enviro/Skybox`，`m_AmbientMode=0`（Skybox）；运行时 `EnviroSkyModule.SetupSkybox()` 还会写 `RenderSettings.skybox`（`EnviroSkyModule.cs:137-148`）。
- **默认管线天空盒已经不在用，无需再删**。不要加第二个天空盒/天空球/pass；不要清空 `m_SkyboxMaterial`，否则环境光一起黑。

**CopyDepth**：`m_RequireDepthTexture=1` 是 Enviro 的硬依赖（`EnviroURPRenderPass.cs:83` 的 `ConfigureInput(Depth)`）；`m_RequireOpaqueTexture=0`。所以管线当前有 **1 次 CopyDepth、0 次 CopyColor**。

这 1 次拷贝**当前无法便宜地消掉**：URP 14 在“透明之后还要采样深度”时必然 enqueue 一次 `CopyDepthPass`（`UniversalRenderer.cs:860-861`、`:1151-1154`），且 `ConfigureInput(Depth)` 会把时机固定在 `AfterRenderingTransparents - 1`（`:715-720`），改 `m_CopyDepthMode` 也不会移动它。改成 depth prepass 会重绘一遍不透明几何（地形/树），比拷贝更贵；降分辨率或自建深度 RT 要改 Enviro，升级即丢。完整取舍见 [rendering_rules](rendering_rules.md) §1.2。**结论：保留 1 次，不要再加第二份。**

**Blit 账**（体积云关、Fog Low）：

| 项 | 次数 / 分辨率 |
|---|---|
| 全屏 color copy（cameraColor → sourceTemp） | 1 / 全分辨率（`EnviroURPRenderPass.cs:220-228`） |
| height fog 合成（sourceTemp → cameraColor） | 1 / 全分辨率（`:298-302`） |
| 体积雾 raymarch + 双边模糊 | 1/4 分辨率 |

开启体积云会再增加全分辨率 `temp1`/`temp2`（`:239-279`），必须重算带宽，优先保持 flat clouds。

### 0.8 已知限制 / 待办

- **水下**：`AtmosphereController` 在相机低于 Crest `SeaLevel` 时关闭 Enviro fog/volumetrics，交给 Crest 水下效果；但 Enviro 主光/环境光仍影响水下，组合观感需真机复核。
- **水面雾是近似**：雾在透明之后，按水面背后的不透明深度取强度；近处略饱和。
- **树风未接天气**：草已由 `SetWeatherWind` 桥接；树仍用 PW shader 自己的风参数。
- **光柱不明显**：体积雾 Low（1/4 res）下是柔和逆光；要明显光柱需加密遮挡或接 VLB2/VL2。
- **重跑世界生成会丢**：`AtmosphereSetup.Apply` 必须加进重跑清单。
- **真机未测**：iQOO 16 的 GPU 耗时与 30fps 稳定性待 Profile。
- **CopyDepth 的验证方法**：`CrestRenderProbe` 在 `beginCameraRendering` 抓队列，**看不到 URP 在 `renderer.Setup()` 里入队的 CopyDepthPass**；现有 0/0 不代表没有拷贝。确认用 Frame Debugger。

## 1. 其他包什么时候再接

| 包 | 定位 | 结论 |
|---|---|---|
| Weather Maker | 昼夜+天气+体积云 | 功能重叠，**不引入**；compute 依赖更重 |
| Volumetric Fog Mist 2 | 局部雾体积 | 出现山谷/海面局部雾时再接 |
| Volumetric Light Beam 2 | 灯柱/光锥，解析式，便宜 | 出现灯笼/窗户时再接 |
| Volumetric Lights 2 | 逐光源 raymarch | 火堆等关键光源 ≤ 3 个时再接 |

四套 fullscreen pass 同时上的排序与 30fps 风险不可控；当前场景没有局部雾/灯对象，装了也无处挂。
