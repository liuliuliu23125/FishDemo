# 水体：Crest Water 5（中心湖落地记录）

> Crest 5.10.0（`Packages/com.waveharmonic.crest`），Unity 2022.3.62f2 + URP 14。
> 本文只记录当前配置、施工链路、踩坑与验收；历史逐包选型分析已删除。
> 选型结论：Crest 原生支持 2022.3；因**没有反射视锥剔除**，湖的平面反射保持关闭。

## 1. 当前配置

| 项 | 值 |
|---|---|
| 湖心 / 半径 / 水位 / 碗深 | `(192, 96)` / 190m / **264m** / 30m |
| 渲染模式 | **SimpleTransparency + Quad**（Crest 自身 CopyColor/CopyDepth=0，水面 1 次 DrawMesh） |
| Surface Clipping | `EverythingClipped` + `ClipLodInput(Cube 640×640, Inverted=true)` |
| LOD / 分辨率 | `LodLevels=5`；Depth/Foam/Clip/AnimatedWaves/Level=128；Gerstner=96；Depth SDF 关 |
| 模块 ON | AnimatedWaves / Foam / Depth / Level / Clip |
| 模块 OFF | DynamicWaves / Flow / Shadow / Albedo / Absorption / Scattering |
| 平面反射 | **OFF**（最贵且无视锥剔除） |
| Depth Terrain | 仅与湖区 AABB 相交的 Terrain（补丁过滤，实测 15→6 dispatch） |
| URP | `m_RequireDepthTexture=1`（Enviro 体积雾/云）、`m_RequireOpaqueTexture=0` |
| 管线 CopyDepth | **1 次，属于 Enviro**，不是 Crest；见 [rendering_rules](rendering_rules.md) §1.2 |
| Enviro 雾 blit | 每帧 1 次全屏 color copy + 1 次合成（见 [atmosphere](atmosphere.md) §0.7） |

> `LodLevels=5` 的原因：固定视点 26m 时 4 级最外层半边长只有 256m，离 R=190m 的岸线仅 66m，边界像方框；5 级为 512m。

## 2. 湖盆与施工链路

地形是缓坡，中心 ±800m 内没有天然洼地（最低点 260.6m，与周边只差 4m），必须人工挖碗形盆地。
参数唯一来源是 `LakeCarve`（`Center/Radius/WaterLevel/Depth/Blend`），改湖先改那里。

| 参数 | 值 | 依据 |
|---|---|---|
| 湖心 | `(192, 96)` | 高度网格全局搜索：挖方小、AABB 内不溢出 |
| 半径 / 过渡带 | 190m / 90m | R..R+90 不挖，岸线自然收边 |
| 水位 / 碗深 | 264m / 30m | 碗沿 266m、湖外最低原生地形 266.7m；留 2.7m 波浪余量 |
| 挖方 | ~5.0M m³ | 只挖不填 |

**溢出判据必须按矩形 AABB，不能按圆形**：水面范围由 WaterBody 的矩形 AABB 决定，AABB 外地形再低也不会露出水面。

重跑 `WorldPipeline` / `SectrPipeline` 后湖会全部丢失，按顺序重做：

```
LakeCarve.Apply → LakeVegetationClear.Apply
  → GtsConvertOne.Run × 受影响 chunk（一进程一块）→ GtsApplyMeshToChunks.RunAll
  → GtsFixMeshRendering.RunAll → LakeCrestSetup.Apply
  → CrestPerformanceMode.ApplyBoth（仅在性能模式被 Restore 过时需要）
  → LakeGeometryProbe.Run + LakeRenderVerify.Run
```

`LakeCrestSetup.Apply` 保留已存在的 `LakeCameraRig`，不新建相机；场景从零重建时仍需 `LakeCameraSetup.Apply`。当前场景已无 `Gaia Water` 节点。

## 3. 为什么必须用 Clip

Crest 的水面是以**视点**为中心的 clipmap：`LateUpdatePosition` 把位置设为 `(视点.x, 水面y, 视点.z)`，各 chunk 按 LOD 翻倍铺开，最外圈能到 ±数十公里。`WaterRenderer` 的 transform XZ 对水面没有意义。

- `WaterBody` 的 AABB 剔除是**整 chunk 粒度**，挡不住巨型外圈 chunk（Chunks 模式实测 49 个 chunk 只有 8 个被剔除）。
- Quad 模式虽然只有 1 次 DrawMesh，但那张 10000×10000 的四边形同样需要 Clip 才能只留在湖盆。
- 只有 Clip 的逐像素 SDF 能精确裁出湖盆。

**`ClipLodInput.Inverted` 语义**（`ClipPrimitive.compute:99-104`）：初始全写 1（裁）。
`Inverted=true` → `target = min(target, sdf)`：盒内保留、盒外裁掉，**正确**。
`Inverted=false` → `target = max(target, 1-sdf) ≥ 1`：全裁，一滴水不剩（踩过）。

## 4. 透明与水下

**SimpleTransparency=1（当前）**：`Fragment.hlsl:825-828` 用
`max(length(volumeOpacity), _Crest_TransparencyMinimumAlpha, foam, albedo.a)` 写 `o_Alpha`，水面走 alpha 混合。
调透明度要同时看 `_Crest_TransparencyMinimumAlpha`（当前 **0.12**）和 DepthLod 的 volumeOpacity，不是只调 Absorption/Scattering。

**非 SimpleTransparency（切回时）**：`o_Alpha` 恒为 1，透明来自折射合成 + 体积消光
（`Fragment.hlsl:625-670`、`VolumeLighting.hlsl:22-25`）；此时才只动 `_Crest_Absorption` / `_Crest_Scattering`。

材质上的 absorption/scattering 系数两种模式都保留；当前湖用它们做恒定水色，不跑仿真。

| 参数 | 值 | 说明 |
|---|---|---|
| `_Crest_AbsorptionColor` | `(0.62, 0.86, 0.92, 0.016)` | alpha 是密度，越小越清澈 |
| `_Crest_Absorption` | `(0.049, 0.015, 0.008)` /m | R 衰减最快 → 水下偏青 |
| `_Crest_Scattering` | `(0.012, 0.026, 0.036)` /m | R 必须压低，否则雾发白 |
| extinction 合计 | `(0.061, 0.041, 0.045)` /m | 1/e 深度 16/24/22m |

**水下开关是引擎自带的**（`UnderwaterRenderer.cs:299-307`）：相机离水面 >2m 自动关；2m 内且 XZ 在 Volume 型 WaterBody（`Lake Body`，`Exclusion=Include`）内、cullingMask 含 Water 层时开启。不要自己写进水检测。

**`_Crest_AmbientTerm` / `_Crest_DirectTerm` 必须写在 Water.mat（水面材质）上**：
`UnderwaterRenderer.Effect.cs:247` 每帧把水面材质属性覆盖到 Volume 材质，只写后者会被覆盖回默认 1.0（静默失效）。当前取 0.35/0.40。

## 5. 相机 rig

没有用 Cinemachine（工程未装，且解决不了下面三件事）。`LakeCameraRig.Apply` 统一切换：

1. **Crest 视点**：`WaterRenderer.Viewer`，水面 clipmap 以它为中心。
2. **SECTR chunk 加载**：`SECTR_RegionLoader` 按自身 transform 判定，不看相机；两台相机各挂一个，只启用当台。
3. **`Camera.main` tag**：GrassBRG 剔除兜底等依赖它，rig 把 MainCamera tag 搬到当前相机。
4. **Enviro 相机**：`EnviroManager.ChangeCamera`（RenderFeature 只对 manager.Camera/Cameras 生效）。

| 文件 | 职责 |
|---|---|
| `Gameplay/LakeOrbitCamera.cs` | 半径 300m；高度 = 地表+18m 且不低于水位+26m（指数平滑）；120s/圈；俯仰 12°±3°；`Paused` 让出 transform |
| `Gameplay/LakeCameraRig.cs` | **C** 切相机 + **F** 自由飞行；同步 Camera/AudioListener/loader/Viewer/tag/Enviro |
| `Assets/Editor/LakeCameraSetup.cs` | 幂等写入场景；`Apply`/`Remove`/`Report` |

- **组件级开关，不要 `SetActive(false)`**：每台相机自带 AudioListener，禁 GameObject 会报 "no audio listeners"。
- **F 键飞行前必须 `LakeOrbitCamera.Paused`**：它在 `LateUpdate` 写 transform，比飞行的 `Update` 晚，不暂停会把位移整帧覆盖（“按 F 没反应”）。
- 巡游相机飞行时必须 `PauseFlythrough()`，退出按最近路径点续接；`PegasusTourController` 已被 rig 禁用（它也监听 F）。
- 退出飞行用 `ResumeFromCurrentPosition()`，不要 `ResetPose()`（会瞬移）。

## 6. 性能模式

| 配置 | Crest pass | Crest CopyColor | Crest CopyDepth | 水面 chunk | draw call |
|---|---|---|---|---|---|
| 原始（Chunks + 非 SimpleTransparency） | 4 | 1 | 1 | 49 | ~49 |
| + SimpleTransparency | 1 | 0 | 0 | 49 | ~49 |
| **+ SimpleTransparency + Quad** | **1** | **0** | **0** | **0** | **1** |
| + Enviro 大气（当前） | 1 | 0 | **1（Enviro）** | 0 | 1 |

> 表的 pass/copy 列来自 `CrestRenderProbe`，**只统计 Crest/自定义 feature 在 `beginCameraRendering` 时已入队的 pass**。URP 在 `renderer.Setup()` 里入队的 CopyDepthPass 不在其中；当前 Enviro 的 1 次 CopyDepth 要由 Frame Debugger 或 URP 源码路径确认。
>
> 2026-11-17 实测：`MeshType=Quad`、`CREST_SIMPLE_TRANSPARENCY=1`、水面 chunk=0、Crest pass=1、Crest CopyColor/CopyDepth=0。probe 输出里的 `CopyDepthPass=0` 只代表 Crest 没有入队 copy。

### 6.1 SimpleTransparency 三层配置（缺一不可）

1. Crest Settings 资产（`ProjectSettings/Packages/com.waveharmonic.crest/Settings.asset`）的 `_PlatformSettings*.SimpleTransparency=1` + `_OverrideSimpleTransparency=1`。
2. scripting define `d_Crest_SimpleTransparency`（影响 C# 的 `#if`，如 `SurfaceRenderer.Universal.cs:61` 的 `ConfigureInput`）。
3. 重新生成 `Runtime/Shaders/Library/Settings/Settings.Crest.*.hlsl`（`CREST_SIMPLE_TRANSPARENCY (1)`）。

坑：不能只改 hlsl 文本；`AssetDatabase` 读不到 `ProjectSettings/` 资产；`ScriptableSingleton.Save()` 不落盘。**正确做法是直接改文本 → 重载单例 → `UpdateSymbols()` → 直接反射调 `ShaderGeneratorUtility.GenerateAll()`**（batchmode 不触发域重载）。工具：`CrestPerformanceMode`。

**切 SimpleTransparency 必须同时把 `_Crest_TransparencyMinimumAlpha` 从 0.95 调到 0.12**，否则水面几乎不透明。

### 6.2 当前还剩的 compute

- **DepthLod**：每个与湖区 AABB 相交的 active Terrain 一次 `DepthTexture.compute`（16²×5）。补丁过滤后 15→6 dispatch。
- **AnimatedWaves**：Gerstner 96²、ShapeWavesTransfer 96²×5、5 次 ShapeCombine 128²、PackLevel 128²×5。Quad 下只喂法线，不能直接关。
- **Foam**：每帧至少 1 次 dispatch；降 `SimulationFrequency` 只减少完整仿真步，不减少 dispatch。
- **Clip**：1 次（16²×5）+ 2 次很小的 `ClearBorder`。
- **Underwater**：入水才有 pass（>2m 自动关），非入水零成本。
- **Queries**：没有 gameplay 注册时 0 dispatch。

**Crest 源码补丁**：`DepthLod.TerrainDepthInput` 加了 Include WaterBody AABB 过滤；升级 Crest 必须重打，否则每个 active Terrain 每帧 dispatch。

**`CrestPerformanceMode.Restore`** 会回退 Chunks + 非 SimpleTransparency + URP Depth/Opaque 开启 + minAlpha 0.95。**接了大气候不要直接跑**：Enviro 仍需要 depth=1，还原后重跑 `AtmosphereSetup.Apply`，确认 depth=1、opaque=0。

## 7. 验收

| 脚本 | 判什么 | 条件 |
|---|---|---|
| `LakeWaterVerify.Run` | 浅水见底、水下吸收色调、水下有内容、开关对照、水上不误开 | 需真实图形设备 |
| `LakeOrbitVerify.Run` | 轨道半径/地形跟随不穿模/朝向 + Crest/SECTR/tag 跟随 + 切换 | 可 `-nographics` |
| `LakeRenderVerify.Run` | 水位、水面 chunk、水体色、画面内容 | 需真实图形设备 |
| `LakeCrestSetup.Retune` | 只重写水色，打印 1/e 深度 | 调参入口 |

2026-11-13：`lake-water.txt` 6/6 PASS，`lake-orbit.txt` 9/9 PASS。2026-11-17 起 `LakeOrbitVerify` 预加载湖区 9 个 chunk 跑完整圈（120 帧固定步长 1s），验证地形跟随不穿模。

**验收三个坑**：
- batchmode 下 `Time.deltaTime` 不推进：用 `LakeOrbitCamera.OverrideDeltaTime` 注入固定步长。
- 水下色调不能用绝对值：湖 16m 深，碗底占屏幕大半，必须用同机位开/关水下效果的差值。
- 验收机位不能站岸上：植被只清到 R+8，岸外会落在树里。

## 8. 踩坑速查

1. **unitypackage 必须连 `.meta` 一起解**，否则 GUID 全断（20+ 编译错误）。
2. **重转已挂 mesh 的 chunk 前先删 `GTS_Mesh` 节点**，否则 GTS 对已有 `GTSMesh` 取 `GetComponent<Terrain>()` 崩；转换必须一进程一块（见 [Assets/Editor/AGENTS.md](../Assets/Editor/AGENTS.md) §3.0）。
3. **植被清除只用半径判据**，不要加 `h < 水位`：世界边缘大片低于水位，会误删 1km 外的草。
4. **`WaterRenderer.SeaLevel` 编辑期恒为 0**（读 internal `Position`）；校验水位看 `transform.position.y`。
5. **`Lod.Enabled` 编辑期必为 false**（getter 是 `_Enabled && _Valid`，`_Valid` 要运行时 GPU 初始化）；不能当校验判据，用 `DefaultClippingState` 等持久字段。
6. **Absorption/Scattering 默认关闭**；材质必须显式写 `_Crest_AbsorptionColor` / `_Crest_Absorption` / `_Crest_Scattering`，否则水面全黑（批处理下没人触发 `UpdateAbsorptionFromColor`）。
7. **验收前禁 `PegasusTourController`**：它每帧改相机位姿，会覆盖验收机位。
8. **验收补两件事**：`RenderSettings.ambientMode = Flat`（未烘 GI，探针全黑）；对每个 `GTSMesh` 调 `Refresh()`（否则地表黑）。
9. **SECTR 异步加载不确定**：验收前把相关 chunk 全部 Additive 打开，或等 Terrain 覆盖站位点。

## 9. 遗留

- [ ] iQOO 16 实测帧率；Reflections / Underwater 取舍在真机定。
- [ ] 钓鱼玩法需要 CPU 波高查询时，开 `com.waveharmonic.crest.cpu-queries`。
- [ ] Crest shader 变体未审计（Shader Control 待接入）。
- [ ] 水下体积光权重 0.35/0.40 是 batchmode 定参，真机 HDR/色调映射下复核。
- [ ] 后处理接入时两台相机要同步 Volume。
