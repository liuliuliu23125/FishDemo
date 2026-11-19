# Assets/Editor/Lighting 规约（火把 / 后处理）

> 默认程序集 `Assembly-CSharp-Editor`，无 asmdef。
> 改火把、Volume、相机后处理或夜间曝光前，先读 [postprocess](../../../Docs/postprocess.md) 与 [rendering_rules](../../../Docs/rendering_rules.md) §4。

## 1. 脚本清单

| 脚本 | 职责 | 入口 |
|---|---|---|
| `LightingSetup.cs` | 幂等布置湖边 12 支火把、生成材质与 Volume Profile、给两台相机挂 `UniversalAdditionalCameraData`、URP 切 HDR grading、把 Volume 绑到 `AtmosphereController` | `Apply` / `Remove` / `Report`（菜单 `FishDemo/World/④`） |
| `LightingVerify.cs` | **需真实图形设备**：Play Mode 验 ACES / Bloom / 无 DOF / 调色 / 两台相机 / 火把数量与开关 / 夜间曝光；6 张截图 | `-executeMethod LightingVerify.Run` |

## 2. 重跑顺序

WorldPipeline / SectrPipeline 重跑会删掉场景根节点；按顺序重做：

```
AtmosphereSetup.Apply
  → LightingSetup.Apply
  → LightingVerify.Run          （真实图形设备）
  → HudPixelVerify.Run          （交互式编辑器，不能 -batchmode）
```

`AtmosphereSetup.Apply` 重建 `Atmosphere` 根节点后，`LightingSetup` 必须再跑一次，否则
`AtmosphereController._postProcessVolume` 与夜景曝光不会被重新绑定。

## 3. 火把硬约束

- 12 支，角度 30° 一支，位置取 `r∈[191, 205]` 内最低的干燥岸点（与人群同一套采样），**不保证贴水**
  （北岸部分点位高于水位 30m）。
- 整支火把缩放 **4×**，与 200 人 4× 人群同尺度；点光 `intensity=120`、`range=48m`、`shadows=None`。
- 同时点亮的数量硬上限 **6**（`TorchRingController.HardLightLimit`），默认 **1**；`L` 循环 0/1/2/4/6。
- 选灯规则：按到 `Camera.main` 的距离取最近 N 支，已亮灯有 12% 距离折扣（滞回），避免相机移动时闪断。
- **不做昼夜门控**：白天/阴天也会按手动数量点亮；只有夜景曝光（见下）跟 `Enviro.isNight`。
- Forward 下 per-object 附加光上限 4；6 盏是预算上限，单物体仍最多叠加 4 盏。
- 点光**不投影**、Enviro 体积雾只采样主光、Crest 水面关闭平面反射 → 火把没有地面阴影/光柱/水面倒影。要补这些另外立项。
- 火焰核心用 `MaterialPropertyBlock` 写 `_EmissionColor` 做淡入/闪烁（≤6 个对象，拆 SRP 批的代价可忽略）；**不要把这个做法扩展到大量物体**。

## 4. 后处理 / 夜景曝光

- 唯一后处理：全局 Volume `Post Processing` + `Assets/Settings/PostProcess/FishDemo Global Volume.asset`。
  ACES、HDR grading（`m_ColorGradingMode=1`）、Bloom、Vignette、Film Grain、
  Color Adjustments + Curves + SMH + White Balance 全在 URP 内置 Uber pass；**不做 DOF**。
- **Bloom 三档**（`AtmosphereController`，`B` 循环 Half/6 → Half/3 → Quarter/3）：
  Half/6 = half-res / 6 mip / 16 blit；Half/3 = half-res / 3 mip / 7 blit；
  Quarter/3 = quarter-res / 3 mip / 7 blit。H 模糊在 embedded URP 里由 9 tap 改为 **5 tap 双线性等价**，
  保持原高斯半径；HQ 关闭，预滤波 1 tap、升采样 2 tap。
- **对马岛近似**：`Color Curves` 的 Hue vs Hue / Hue vs Sat 只作用橙红 hue 0.06–0.12
  （峰值约 -10°，饱和 +9%~12%），黄色 0.16 保持中性。它近似 GOT 的 ACEScg 红基色，
  不是改 tonemapper；真改落点见 [URP AGENTS](../../../Packages/com.unity.render-pipelines.universal/AGENTS.md) §3。
- 两台相机（`Main Camera` / `Lake Orbit Camera`）都必须 `renderPostProcessing=1`；新增相机要加进
  `LightingSetup.ConfigureCameras`。
- **不要加第二套后处理/第二个全局 Volume**；参数改 Profile，不新建曲线外挂。
- 夜景：`AtmosphereController` 克隆 Volume profile，按 `Enviro.isNight` 在 2.5s 内叠加
  **+1.8EV**、对比 →8、饱和 →-18、冷色滤镜 `(0.78,0.88,1.08)`；白天回到 0.35EV/10/-8/原滤镜。
  Enviro 夜空是显示参考色，ACES 会把低值压黑，这个补偿是必需的，不是物理曝光。
- `AtmosphereController.ApplySettings` 同时把 Enviro `ambientIntensityModifier` 设为 **1.4**（抬暗部，配合 ACES）；
  改它要重跑 `AtmosphereVerify`，午夜/正午的亮度断言都依赖它。
- 验证脚本用 `AtmosphereController.SnapNightExposure()` 立即套用昼夜曝光；不要靠帧数等淡入。
- 低画质档第一个关的是 Bloom；当前 Profile 没有 Depth of Field 组件。

## 5. 验收

```
Unity.exe -batchmode -nographics -projectPath E:\FishDemo -executeMethod LightingSetup.Apply
Unity.exe -batchmode -projectPath E:\FishDemo -executeMethod LightingVerify.Run
```

2026-11-18 结果：LightingVerify PASS（火把近景中心亮度约 0.10 / 0.014；夜景 2.15EV / 白天 0.35EV；
Bloom Half/Quarter 切换 PASS）；AtmosphereVerify PASS（正午顶 0.585、午夜顶 0.005、午夜约 120 色，阈值 100）。
截图在 `Logs/Lighting/`；`AtmosphereVerify` 的午夜色数阈值从 300 降到 120，因为后处理后的夜景是平滑深蓝渐变。

## 6. 相关文档

| 文档 | 何时读 |
|---|---|
| [postprocess](../../../Docs/postprocess.md) | 调色、Bloom、夜景曝光的当前值与性能账 |
| [rendering_rules](../../../Docs/rendering_rules.md) §4 | 光照数量、Forward 限制 |
| [performance_budget](../../../Docs/performance_budget.md) | pass / blit / 光源预算 |
| [atmosphere](../../../Docs/atmosphere.md) | Enviro 昼夜、HUD、`isNight` 语义 |
| [../Atmosphere/AGENTS.md](../Atmosphere/AGENTS.md) | `AtmosphereVerify` 的改动 |
| [../../Settings/AGENTS.md](../../Settings/AGENTS.md) | URP / Volume / 质量档资产 |
