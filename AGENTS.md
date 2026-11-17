# FishDemo 项目规约

Unity 2022.3 + URP 的 **PBR 写实钓鱼游戏 Demo**，视觉对标《对马岛之魂》。
目标 **iQOO 16 / 30fps**，大世界分块流式加载，含水下与昼夜。

## 0. 协作红线

1. **只陈述有据可查的事实**：文件路径 + 行号、工具输出、或文档原文。不确定就说"不知道"，禁止补全历史、禁止虚构。
2. **结论分三类标注，不可混写**：代码事实 / 文档声称（未验证）/ 推断（注明依据）。
3. **版本兼容必须查证**：看 `package.json` 的 `unity` 字段、asmdef、源码宏、官方文档。**包名和宣传语不可作依据。**

## 1. 硬约束

| 项 | 值 |
|---|---|
| Unity / URP | 2022.3.62f2 / 14.0.12 |
| 色彩空间 / 后端 / 压缩 | Linear / IL2CPP+ARM64 / **ASTC** |
| 帧率 / 机型 | 30fps / iQOO 16 |
| 同屏角色上限 | 20 |
| 联网 | 将来需要，**当前不实现网络层**，只保证客户端表现可承载多人 |
| 合批 | **SRP Batcher 唯一路径；静态合批已关闭**（PlayerSettings Android/Standalone 0/0；GtsApplyMeshToChunks 不再标 static，存量标记保留但不再参与合批） |
| **Android 图形 API** | **仅 Vulkan，排除 GLES**（`m_APIs=15000000`，`m_Automatic=0`） |

**Android 仅 Vulkan 的原因**：草改 BRG 后，BRG shader 需要 `#pragma target 4.5` +
`#pragma exclude_renderers gles`（依据 URP 14 的 `BRGPicking.shader:31-32`）。
若回落 GLES，草会消失。改这一项影响整个 Android 构建，不只草。
工具：`Assets/Editor/GraphicsApiSetup.cs`。详见 [grass_brg_plan](Docs/grass_brg_plan.md) §1.1。

**2022.3 没有的东西**（不要假设存在）：GPUResidentDrawer、Forward+、内置体积雾/光、内置水体、RenderGraph。

**ASTC 细节**（地形纹理数组已按此配置，见 [gts_srp_batcher](Docs/gts_srp_batcher.md) §8.5）：
- 用 `ASTC_8x8`，**不要用 6x6**——GTS 枚举 `ASTC_6x6(50)` 实际映射到 `ASTC_RGB_6x6`，**无 alpha**
- 地形的 albedo 数组 alpha 是高度混合权重，丢了会让材质过渡变硬

## 2. 模块与选型

> **「现状」列是代码/资产里实际存在的，「规划」列是选型结论、尚未落地。**
> 两者不一致时以「现状」为准，不要假设规划中的包已安装。

| 模块 | 现状（已落地） | 规划 | 详见 |
|---|---|---|---|
| 地形生成 | Gaia Pro VS（已装） | 同 | [terrain_streaming](Docs/terrain_streaming.md) |
| 地形细节雕琢 | 未接入 | GeNa Pro | 同上 |
| **Terrain → Mesh 分块** | **GTS 已落地**：144 个 subTile，`saveResolution=Half` | 加 LOD 链、走 YooAsset | 同上 + [gts_srp_batcher](Docs/gts_srp_batcher.md) |
| **地表合批** | **SRP Batcher 已生效**；静态合批已关；纹理数组 ASTC_8x8 | 维持 | [gts_srp_batcher](Docs/gts_srp_batcher.md) |
| 大世界流式 | **SECTR**（承担全部 chunk 加载；已打激活节流/限流/缓存补丁） | SECTR 降为区域级，chunk 走 YooAsset | 同上 + [terrain_streaming](Docs/terrain_streaming.md) §12 |
| **水体** | **Crest 5.10.0 已落地**：中心人工湖（湖心 `(192,96)` / 半径 190m / 水位 264m / 深 30m），Crest 包在 `Packages/com.waveharmonic.crest`；靠 Surface Clipping 裁出湖盆；旧 Gaia Water 已不在场景（水面唯一来源是 Crest）；**当前渲染模式 = SimpleTransparency + Quad**（Crest pass 4→1，水面 draw call 49→1）；`LodLevels=5` / 分辨率 128（Gerstner 96）/ Depth SDF 关闭；Depth 只烘湖区 Terrain；环湖轨道相机 + C/F 键切换 | 真机验反射/水下开关 | [water](Docs/water.md) §1–§6 |
| **昼夜/天气/大气雾** | **Enviro 3.3.2b 已落地**：Time/Lighting/Sky（日/月/星空/环境光）+ Weather（9 预设）+ Fog（volumetrics Low / 1/4 res / steps 32）；体积云关、flat clouds 开；旧 `Directional Light` 已禁用 | 真机验性能 / 水下 | [atmosphere](Docs/atmosphere.md) §0 |
| **体积雾/光** | **Enviro 体积雾/光已落地**：雾 shader 采样主光阴影（树影光柱）；雨雪粒子；草风由 `AtmosphereController` 桥接到 BRG 材质 | 局部雾体积（VFM2）/ 灯柱（VLB2）按需 | 同上 |
| 后处理 | URP 内置（主场景无 Volume）；调色/ACES 规划走内置 Uber pass | 不引入第二套后处理 | [postprocess](Docs/postprocess.md) |
| **草** | **BRG 已落地 + 逐 chunk 流式**：36 chunk 烘焙 **435 万实例 / 133MB**；Terrain detail 已关（`DetailObjectDistance=0`）；单 shader `FishDemo/Grass/BRGMinimal`，**三层 LOD 全部采 albedo 图集精灵格**；Play Mode 由 `SectrGrassStreamer` 跟随 SECTR 加载，原生 `NativeArray` + `GraphicsBuffer` 池 | 阴影/透光、真机 | [grass_brg_progress](Docs/grass_brg_progress.md) |
| **树** | **Terrain 渲染 + Impostor**：实际用 **7 原型**（4 个云杉占 99.99%）；实例已降 **1/10 → 4.6 万**；每 prefab **2 级 LOD**（LOD0 原 mesh / LOD1 = Amplify Impostor 8v6t）；`TreeDistance=450`、`maxFullLOD=16`；材质 keyword 组合 **2 种**（树干 `WIND` / 针叶 `ALPHATEST+SSS+WIND+WORLDMAP`） | 阴影/透光、BRG 化、真机 | [vegetation](Docs/vegetation.md) §6 |
| 植被 LOD | **Amplify Impostors 1.1.0 已接入**（4 个云杉原型，URP14 分支） | 其余 41 个原型未烘 | [vegetation](Docs/vegetation.md) §6.6 |
| Shader 变体裁剪 | 未接入 | Shader Control | 同上 |
| 玩家动画 | 未接入 | Animancer Pro（CPU） | [character_animation](Docs/character_animation.md) |
| NPC / 远程玩家动画 | 未接入 | GPU ECS Animation Baker（VAT） | 同上 |
| **资源管理** | **YooAsset 3.0.7 已实现但无调用方** | 唯一资源入口 | [模块规约](Packages/com.tuyoogame.yooasset/AGENTS.md) |

## 3. 高频规则（写代码前必看）

**渲染管线流程铁律（2026-11-17）**——写任何 pass / RendererFeature / 后处理前必须遵守，依据见 [rendering_rules](Docs/rendering_rules.md) §1：

1. **天空盒只有一个来源**：Enviro 的 `Enviro/Skybox` 由 `EnviroSkyModule.SetupSkybox()` 写入 `RenderSettings.skybox`，场景 `m_SkyboxMaterial` 也是它。URP 默认天空盒已不再使用，**不要再加第二个天空盒或天空 pass**；`m_AmbientMode=0`（Skybox）依赖它，不能置空。
2. **CopyColor / CopyDepth 不是默认项**：只有 shader 真的采样 `_CameraOpaqueTexture` / `_CameraDepthTexture`（或 pass `ConfigureInput`）才开。当前 `m_RequireOpaqueTexture=0`；`m_RequireDepthTexture=1` **只因 Enviro 体积雾/云**。**这 1 次 CopyDepth 是 URP 14 “透明后采样深度”的固有成本**；不要用 `ForcePrepass`/depth priming 去换（会重绘不透明几何，更贵），也不要再叠第二份。完整取舍见 [rendering_rules](Docs/rendering_rules.md) §1.2。
3. **完成功能必须同时给性能账**：新增内容要说清 pass 数、全屏 RT 数、blit 数、批处理路径，以及能否在低画质档关闭。说不出来就是没做完。
4. **管线里尽量不 Blit**：优先调 pass 顺序、直接读写相机 color/depth、复用已有上下文纹理；禁止全分辨率 ping-pong。第三方包固有的 blit 要计数并说明（Enviro 雾当前固有 1 次全屏 copy + 1 次合成）。
5. **后处理 / 调色 / ACES 走 URP 内置 Uber pass**：Tonemap 用 ACES 时不要另起全屏 pass；LUT 保持 32；不要叠第二套后处理（Beautify 等）；每加一项都要按 [performance_budget](Docs/performance_budget.md) 的 2.5ms 预算核算。

**批处理**：SRP Batcher（地表/树/常规物体）→ GPU Instancing → **BRG（草，已落地）**。动态合批保持关闭。**任何新增渲染内容必须说明走哪条批处理路径**。

**草与树是两条完全不同的路径，不要混谈**：
- 草走 BRG（`GrassBRGManager` + `GrassBRG.shader`），不经过 SRP Batcher，也不受 Terrain 管理。
- 树走 Terrain 自带渲染（`drawTreesAndFoliage=1`），由 TerrainRenderer 提交，SRP Batcher 覆盖。
- 因为两者都在改「植被」，**改草时用 `detailObjectDistance`，不要用 `drawTreesAndFoliage`**——后者会把树一起关掉。
- **树有运行时密度参数吗？没有**。草有 `detailObjectDensity`（可逆），树的实例数只能改
  `TerrainData.treeInstances` 或重跑 Gaia 撒点。别照草的经验去找树的 density 字段。

**树改动的四条硬约束**（2026-11-12 全部踩过）：
1. **一个材质槽缺失 = 整批树放弃 GPU instancing**。日志会报
   `The tree X couldn't be instanced because one of the materials is missing.`。
   查"树批次暴增"先 grep 这条，别先怀疑 keyword。
2. **keyword 不能强行统一**。它跟 `_PW_ShaderMode` 严格对应：树干(`WIND`) /
   树叶(`ALPHATEST+SSS+WIND+WORLDMAP`) / 远景(+`BILLBOARD`)。树干开 `ALPHATEST`
   会透明，树叶关会丢镂空。只有 `_PW_SF_COVER_ON` 是空转（`progress=0` 时无任何输出），已清除。
3. **改材质 keyword 必须直接改 .mat 文本**。`Material.DisableKeyword` 对 ShaderGraph
   材质不持久——`m_ValidKeywords` 会在重编译时写回。
4. **Terrain 树能吃 impostor**。Terrain 渲染树时沿用 prefab 的 LODGroup renderer，
   改 prefab 即可，不需要把树导出成 GameObject。`m_lodReplacement` 只在烘焙时生效，
   已烘过但要改结构必须手动改 LODGroup。

**验收相机一律用玩家视角**（人眼 1.7m、平视、FOV 60°）。高空俯瞰看不出玩家真实观感，
曾因此误判密度和 LOD 跳变。

**草的显色靠 albedo 图集，不是 tint**（2026-11-12 踩过）：
花的颜色（雏菊白瓣黄芯/三叶草粉/蒲公英黄）在 `PW_LawnGrass_00_D` / `PW_WildGrass_00_D`
两张 RGBA 图集的像素里，**传 tint 显不出花色**（tint 乘在绿色渐变上，乘什么都是绿）。
- **三层 LOD 的每类型材质都要启用 `_USE_ALBEDO_TEX`**。曾经只有 LOD0 启用，
  而可见实例里 LOD0 只占 0.2%，等于 99.8% 的草是纯色、花看不见。
  排查「花看不见」先看 `GetLastLodCount` 的分布，别急着怀疑贴图。
- LOD1/LOD2 是**每类型采样自己图集格的精灵交叉片**，不是共用的程序化草簇。
  精灵格不能用原型 mesh 的 UV 包围盒（雏菊/蒲公英的 bbox 横跨图集顶部的纯色绿条）。
- 竖直面片**不能用地表法线做 Lambert**：低太阳角下会全黑，要用朝相机混合 + 包裹光照。
- `shader_feature` 必须显式 `EnableKeyword`，只 `SetFloat` 不切换变体（静默失效）。
- **不要开 `AlphaToMask`**：工程 `Assets/Settings/UniversalRP.asset` 是 `m_MSAA: 1`，
  无 MSAA 时 alpha-to-coverage 退化，近景草会被切成亮绿碎屑。
- tint / 类型尺寸走**材质属性**（每类型一个材质）而非 per-instance 数据，GPU buffer 保持 32B/实例。

**Crest 水体（2026-11-13 落地，改水前必读 [water](Docs/water.md) §1–§6）**：
- **Crest 是海洋系统**：水面以**视点**为中心铺开 clipmap，`WaterRenderer` 的 transform XZ 对水面无意义，
  且最外圈能铺到 ε±数十公里。`WaterBody` 的剔除是整 chunk 粒度，**裁不出小湖**。
  只有一个办法：`ClipLod.DefaultClippingState = EverythingClipped` + `ClipLodInput(Cube, Inverted=true)`。
  `Inverted=false` 会全裁（一滴水不剩），语义见 `ClipPrimitive.compute:99-104`。
- **Absorption / Scattering 默认是关闭的**。湖选择不开（省两个每帧 pass），改用材质恒定色；
  但材质必须显式写入 `_Crest_AbsorptionColor` / `_Crest_Absorption` / `_Crest_Scattering`，
  否则水面渲染成纯黑（批处理下没人触发 `UpdateAbsorptionFromColor`）。
- **`Lod.Enabled` 的 getter 是 `_Enabled && _Valid`，`_Valid` 只在运行时 GPU 初始化后为真**。
  编辑期/批处理读它必然是 false，**不能当校验判据**（校验用 `DefaultClippingState` 等持久字段）。
- **验收前必须禁 `PegasusTourController`**：它挂在 Main Camera 上每帧改相机位姿，
  会把验收脚本摆的机位覆盖掉（曾导致十几轮验收全部误判）。
- **验收还要补两件事**，否则画面全黑但不是水的错：① `RenderSettings.ambientMode = Flat`
  （场景未烘 GI，环境探针全黑）；② 对每个 `GTSMesh` 调 `Refresh()`（否则地表黑）。
- 湖盆参数唯一来源是 `LakeCarve`（Center/Radius/WaterLevel/Depth）。改湖先改那里，
  再跑 `LakeCarve.Apply` → `LakeVegetationClear.Apply` → 重转 GTS → `LakeCrestSetup.Apply`。
  `LakeCrestSetup.Apply` 现在保留根节点上的 `LakeCameraRig`，并会写回 Quad/分辨率/SDF/URP 开关；
  若性能模式被 `CrestPerformanceMode.Restore` 回退过，重建后再跑一次 `CrestPerformanceMode.ApplyBoth`。
  场景从零重建时相机节点不会自动出现，仍需 `LakeCameraSetup.Apply`。

**水的透明度由模式决定**（2026-11-13，详见 [water](Docs/water.md) §4/§6.1）：
- **当前 SimpleTransparency=1**：`o_Alpha = max(length(volumeOpacity), _Crest_TransparencyMinimumAlpha, foam, albedo.a)`
  （`Fragment.hlsl:825-828`），min alpha=0.12，volumeOpacity 由 DepthLod 提供。
  调透明度要同时看 `_Crest_TransparencyMinimumAlpha` 与 depth，不是只调 Absorption/Scattering。
- 若切回 `CREST_SIMPLE_TRANSPARENCY (0)`：`o_Alpha` 恒为 1，透明靠折射合成 + 体积消光，
  此时才只动 `_Crest_Absorption` / `_Crest_Scattering`（找 alpha 是死路）。
- 水下效果开关是引擎自带的（离水面 >2m 自动关），不要自己写进水检测；
  但 `_Crest_AmbientTerm`/`_Crest_DirectTerm` 必须写在 **Water.mat（水面材质）**上——
  `UnderwaterRenderer.Effect.cs:247` 每帧把水面材质覆盖到 Volume 材质，只改后者静默失效。
- 相机切换 rig（`LakeCameraRig`，C 切相机 / F 自由飞行）会跟随切换 Crest `Viewer` /
  SECTR loader / MainCamera tag。新增"跟着相机走的状态"必须加进 `Apply()`，验收见 `LakeOrbitVerify`。
- **F 键飞行前必须暂停轨道脚本**（`LakeOrbitCamera.Paused`）：它在 `LateUpdate` 写 transform，
  比飞行的 `Update` 更晚，不暂停会把飞行位移整帧覆盖（"按 F 没反应"）。

**水面性能模式（2026-11-13 已启用，详见 [water](Docs/water.md) §6）**：
- 当前是 **SimpleTransparency + Quad**：URP pass 4→1（**Crest 自身**不再请求
  CopyColor/CopyDepth；管线里仍有 1 次 CopyDepth，那是 Enviro 的 `m_RequireDepthTexture=1`），
  水面 chunk 49→0（单四边形 1 次 DrawMesh）。
- **SimpleTransparency 是三层配置**：Crest Settings 资产字段 + scripting define
  `d_Crest_SimpleTransparency` + 重新生成的 `Settings.Crest.*.hlsl`。改文本后必须
  重载单例再调 `UpdateSymbols()`，然后**直接调 `ShaderGeneratorUtility.GenerateAll()`**
  （`UpdateSymbols` 只请求重编译，batchmode 不会触发域重载）。工具：`CrestPerformanceMode`。
- **切 SimpleTransparency 必须同时把 `_Crest_TransparencyMinimumAlpha` 从 0.95 调到 0.12**，
  否则水面几乎不透明（`Fragment.hlsl:825-828` 用它兜底 alpha）。
- **URP 资产的 `m_RequireDepthTexture/m_RequireOpaqueTexture` 已关**。接入任何依赖
  `_CameraDepthTexture`/`_CameraOpaqueTexture` 的新 shader（如 Gaia/GeNa VFX）前必须重新打开。
- 水面 chunk 不合批是**三重原因**：`CommandBuffer.DrawRenderer` 逐个提交（不进 SRP Batcher）、
  逐 chunk 的 `CrestChunkGeometryData` CBUFFER（`Geometry.hlsl:12-16`）、逐 chunk MPB。
  Quad 模式一次性绕开全部三条。
- **当前 compute 规模**：`LodLevels=5`（固定视点 26m 下 4 级最外层半边长 256m，离岸仅 66m；5 级 512m）；Depth/Foam/Clip/AnimatedWaves/Level=128，ShapeGerstner=96，
  Depth SDF 关闭。Depth 只对与湖区 AABB 相交的 active Terrain dispatch（`EverythingClipped` 时；
  大洋模式不裁则保留全部；`CrestRenderProbe` 实测 15→6）。Foam 频率保持 30：
  `PersistentLod` 在 substeps==0 时仍 dispatch 一次，
  降频不减少 dispatch 次数。详细清单见 water.md §6.2。

**大气系统（2026-11-15 落地，改之前必读 [atmosphere](Docs/atmosphere.md) §0）**：
- **`ENVIRO_URP` 必须手动加**：Enviro 自带的 `EnviroDefineSymbol` 只加 `ENVIRO_3`。没有 `ENVIRO_URP` 时 `EnviroURPRenderFeature` 整个类型被 `#if` 掉，天空/雾/云**静默失效**。工具：`AtmosphereSetup.EnsureDefines`（Standalone + Android）。
- **保存场景前必须 `EnviroManager.LoadConfiguration()`**：Enviro 的模块（Time/Fog/Weather/...）不是预制体对象，而是编辑期从 configuration 资产赋值的引用。`AtmosphereSetup.Apply` 若实例化后立刻保存，Play Mode 下模块全 null，`StartModules()` 直接跳过（实测全 False）。
- **雾 pass 被改到透明之后**：原 `BeforeRenderingTransparents-1` 下 Crest 水面完全不受雾（雾天一条饱和绿带）。补丁在 `EnviroURPRenderPass.cs` 构造函数，标了 `[FishDemo]`。升级 Enviro 时补丁会丢，必须重打。
- **`m_RequireDepthTexture=1`** 是体积雾/云的硬依赖（会多一次 depth prepass）；`m_RequireOpaqueTexture` 仍为 0。
- **切相机必须同步 Enviro**：RenderFeature 只对 `EnviroManager.Camera` / `Cameras` 生效。`LakeCameraRig.Apply()` 已调用 `ChangeCamera`；新增跟相机走的状态也要加进去。
- **质量档唯一来源**：`Assets/Settings/Enviro Mobile Quality.asset`（体积云关 / flat clouds 开 / 雾 volumetrics Low 1/4 res steps 32 / 全局反射关 / 环境探针 0.5s / 粒子 0.5）。不要在场景里手改模块质量字段，`QualityModule` 每帧会用这个资产覆盖。
- **草风桥接**：`AtmosphereController` 每 0.25s 调 `GrassBRGManager.SetWeatherWind`（改 7 个类型材质的 `_WindSpeed/_WindStrength`）。树的风仍由 PW shader 自己管，未接天气。
- **昼夜操作**：`AtmosphereController.OnGUI` 左上角 HUD，自上而下是**键盘操作提示标题 → 昼夜/天气/流速 → 按键说明**（托管堆状态已从 HUD 移除）；`T` 暂停时间、`[` `]` ±1h、`\` 切流速（20/5/2/1 分钟一天）、`1-5` 天气。注意太阳落山后 Enviro 用**月亮**做主光，18:00 与 00:00 光向接近属正常。
- **HUD 三件套不能省**：宽度用 `ComputeHudWidth` 夹取（直接 `CalcSize().x` 在窄视图出屏）；尺寸用 `MeasureBlock` 逐行量（整块 `CalcSize` 会低估行高、裁掉最后一行）；文字显式设白色 + 1px 深色投影（默认色是深色，压在深底上等于看不见）。**不要用 `<size=-2>`**：IMGUI 只认绝对像素，相对尺寸会让整段不渲染。
- **托管 GC 不接管、且增量已关闭**：`gcIncremental: 0`（2026-11-17 决策），**不用 `GCMode=Manual/Disabled`**。系统仍会在不可控时机触发**完整**回收，所以唯一有效的策略是让大块数据根本不进托管堆：草实例走 `NativeArray` + 逐 chunk 流式（见 [grass_brg_progress](Docs/grass_brg_progress.md) §6），不再常驻 260MB+ 草数组。见 [terrain_streaming](Docs/terrain_streaming.md) §12.5。
- **草的驻留跟随 SECTR**：`Gameplay/SectrGrassStreamer` 每 0.1s 对齐一次——chunk `IsLoaded` 才读对应 `.bytes`，卸载即释放；buffer 走 32MB 上限的复用池，驻留 chunk 数硬上限 24。树不需要池：树在 `TerrainData` 的 native 数据里，chunk 缓存已经覆盖。
- **Enviro 与 Crest 的边界**：Enviro 没有水下检测，体积雾在水下仍会应用；水面雾按背后不透明深度近似。改水/改雾时两者一起看。

**渲染分层**：Gameplay 层不得直接操作 `CommandBuffer` 或 `Graphics.*`。

**资源加载**：YooAsset 是唯一入口，禁止 `Resources.Load`。改资源层前先读 [模块规约](Packages/com.tuyoogame.yooasset/AGENTS.md)。

**反复加载用缓存解决，不用池解决**。chunk 卸载不 Destroy Mesh，池化复用。

**夜晚光源** ≤ 6，其余靠 emissive + Bloom（无 Forward+，per-object limit 为 4）。

详见 [rendering_rules](Docs/rendering_rules.md)。

## 4. 子目录规约

> 进入对应目录写代码前读该目录的 `AGENTS.md`，不要默认全读。

| 目录 | 行数 | 何时读 |
|---|---|---|
| [Assets/Scripts](Assets/Scripts/AGENTS.md) | 70 | 写任何自研代码前（程序集边界、命名空间约定） |
| [Assets/Scripts/Editor](Assets/Scripts/Editor/AGENTS.md) | 124 | 改 Gaia 世界生成工具链、草烘焙与 BRG 验证 |
| [Assets/Scripts/Runtime](Assets/Scripts/Runtime/AGENTS.md) | 134 | 改资源层、接 YooAsset、改 BRG 草运行时、改相机 rig |
| [Assets/Editor](Assets/Editor/AGENTS.md) | 292 | 改 SECTR 切块 / GTS 转换 / 地形调参 / 树的密度与 LOD / 验证脚本 |
| [Assets/Procedural Worlds](Assets/Procedural%20Worlds/AGENTS.md) | 137 | 用第三方插件、排查 Gaia/SECTR/GTS/Pegasus 问题 |
| [Assets/Scenes](Assets/Scenes/AGENTS.md) | 96 | 改场景、重跑世界生成、重接草/Pegasus |
| [Packages/com.tuyoogame.yooasset](Packages/com.tuyoogame.yooasset/AGENTS.md) | 90 | 改资源层代码前 |

## 5. 文档索引

> **按需读取，不要默认全读。** 下表"何时读"是触发条件——条件不满足就不打开。

**常读（短，建议一次读完）**

| 文档 | 行数 | 何时读 |
|---|---|---|
| [rendering_rules](Docs/rendering_rules.md) | 99 | **写任何渲染代码前**：管线铁律（天空盒 / CopyDepth 取舍 / 少 Blit / ACES）+ 批处理路径表 |
| [performance_budget](Docs/performance_budget.md) | 50 | 评估性能、做验收（含 pass / 带宽预算） |
| [Packages/…/AGENTS.md](Packages/com.tuyoogame.yooasset/AGENTS.md) | 90 | 改资源层代码前 |

**按模块查（只读对应模块那份）**

| 文档 | 何时读 |
|---|---|
| [grass_brg_plan](Docs/grass_brg_plan.md) | 做草改 BRG、排查草渲染/剔除、查 Vulkan 约束、采样率与烘焙规模。**635 行，只跳读需要的节** |
| [grass_brg_progress](Docs/grass_brg_progress.md) | **改任何草相关代码前先读**：当前落地状态、实测数据、13 条「草消失且无报错」的踩坑、复现命令。**372 行** |
| [gts_srp_batcher](Docs/gts_srp_batcher.md) | 改 GTS shader、排查地形合批（SRP Batcher）不生效、mesh 地形与 Terrain 视觉不一致。**223 行** |
| [world_implementation](Docs/world_implementation.md) | 重跑世界生成、排查 chunk 加载、改世界尺寸/海平面、核对"场景怎么来的"。**411 行，只跳读需要的节** |
| [terrain_streaming](Docs/terrain_streaming.md) | 做地形、流式加载、SECTR。**§12 流式补丁 / 缓存 / 内存归属** |
| [water](Docs/water.md) | 做水体、水下、中心湖、性能模式。**改水/改湖前先读 §1–§3**（配置/湖盆/Clip），§4 透明与水下，§5 相机，§6 性能，§8 踩坑速查 |
| [atmosphere](Docs/atmosphere.md) | 做昼夜、天气、体积雾光。**§0 落地配置 + §0.7 天空盒/管线开销** |
| [vegetation](Docs/vegetation.md) | 做植被、树的密度/LOD/impostor/keyword、Shader 变体裁剪。**改树之前先读 §6** |
| [postprocess](Docs/postprocess.md) | 调色、后处理。**接入前先读 §0 性能规则（ACES/LUT 不另起 pass）** |
| [character_animation](Docs/character_animation.md) | 玩家动画、NPC GPU 动画 |
| [pooling_lod](Docs/pooling_lod.md) | 资源缓存、对象池、chunk 加载策略（托管 GC 见 terrain_streaming §12.5） |

**查阅型（长，只跳读需要的节）**

| 文档 | 行数 | 只在以下情况打开 |
|---|---|---|
| [yooasset_3.0.7](Docs/yooasset_3.0.7.md) | 2039 | 要理解 YooAsset 内部链路/模块职责；改动包源码；排查"为什么这么设计"。**顶部有分节导读** |
| [yooasset_practices](Docs/yooasset_practices.md) | 154 | 遇到具体症状：初始化失败、句柄失效、卸载不掉、参数依据。**顶部按症状分节** |
| [project_todo](Docs/project_todo.md) | 58 | 查工程差距、待决策事项、渲染待办 |

## 6. 工程约定

- **Git LFS**：已配置（`.gitattributes`，2026-11-11）。规则是**目录限定 + 精确扩展名**，
  不要改成全局通配（会让 2000+ 历史裸二进制立刻显示为"已修改"，等于顺带做一次全库迁移）。
  Unity 的 YAML 文本（`.unity/.mat/.prefab/.asset`）刻意不进 LFS，保留 diff 能力。
  当前走 LFS 的：`GrassData/**/*.bytes`、Pegasus 的 fbx/psd/bmp/png/tga、
  **`Assets/PW_Tree_*_Impostor_*.tga|exr`（2026-11-12 新增，114MB）**。
  **2026-11-13 教训**：把 Unity YAML 移出 LFS 时，规则收窄漏掉了 1319 个仍被 LFS
  跟踪的二进制文件（GTS mesh、GeNa DLL、贴图等）。它们在索引里还是 LFS 指针，
  却不再匹配任何 LFS 规则 → Unity 把指针当资产读，GTS mesh 全部 corrupted，
  GeNa DLL 无法加载，GaiaCore 编译失败。收窄规则后必须用
  `git lfs ls-files -n` + `git check-attr --stdin filter` 核对，确认没有
  `filter: unspecified` 的 LFS 指针。本次已按精确路径补回（`.gitattributes` 末尾）。
- **目录**：`Assets/Scripts/Runtime/{Rendering,Streaming,Gameplay}`、`Assets/ThirdParty/`。
  `Rendering/`（草 BRG）与 `Gameplay/`（Pegasus 巡游控制器）已存在；`Streaming/` 尚未创建。
- **默认程序集目录**：`Assets/Editor/`（Editor 专用）、`Assets/GTSRuntime/`（运行时）。
  两者都**没有 asmdef**，因为它们要引用同样无 asmdef 的 SECTR / GTS。
  不要把运行时脚本放进 `Assets/Editor/`（打包后不存在），
  也不要放进有 asmdef 的 `Assets/Scripts/Runtime/`（引用不到 `Assembly-CSharp`）。
- **Pegasus 同样无 asmdef**：`PegasusTourController.cs` 因此放在 `Assets/Scenes` 之外的
  `Assets/Scripts/Runtime/Gameplay/`——该目录**没有 asmdef**，编进 `Assembly-CSharp`，
  才能引用 Pegasus 类型。给 Gameplay 加 asmdef 会让这个脚本编不过。
- **注释**：代码不写注释，只留锚点（第三方补丁 `[FishDemo]`、`TODO`/`FIXME`）；
  从代码推不出的「为什么」写进本文件或 `Docs/`。规则见全局 AGENTS.md「代码不写注释」。
- **全局配置留档**：本机全局指令文件 `~/.zcode/AGENTS.md` 的镜像在仓库 `global/AGENTS.md`
  （不在 `Assets/` 下，不进 Unity 构建）。改任意一边必须同步另一边，`diff` 校验后一起提交推送。
