# FishDemo 项目规约

Unity 2022.3 + URP 的 **PBR 写实钓鱼游戏 Demo**，视觉对标《对马岛之魂》。
目标 **iQOO 16 / 30fps**，大世界分块流式加载，含水下与昼夜。
仓库内的性能数字都是估值，真机数据尚未采集。

## 0. 协作红线

1. **只陈述有据可查的事实**：文件路径 + 符号、工具输出、或文档原文。不确定就说"不知道"，禁止补全历史、禁止虚构。
2. **结论分三类标注**：代码事实 / 文档声称（未验证）/ 推断（注明依据），不可混写。
3. **版本兼容必须查证**：看 `package.json` 的 `unity` 字段、asmdef、源码宏、官方文档；包名与宣传语不可作依据。
4. **自研代码的引用不写行号**：用「文件 + 符号 + grep」；第三方源码引用保留行号，升级包后必须复核。

## 1. 硬约束

| 项 | 值 |
|---|---|
| Unity / URP | 2022.3.62f2 / 14.0.12 |
| 色彩空间 / 后端 / 压缩 | Linear / IL2CPP+ARM64 / ASTC 8x8 |
| 帧率 / 机型 | 30fps / iQOO 16（未真机验证） |
| 同屏角色上限 | 200 |
| 联网 | 将来需要，当前不实现；客户端表现需可承载多人 |
| 合批 | **SRP Batcher 唯一路径；静态与动态合批均关闭**（PlayerSettings Android/Standalone 0/0） |
| Android 图形 API | **仅 Vulkan，排除 GLES**（`m_APIs=15000000`，`m_Automatic=0`） |

**Android 仅 Vulkan**：BRG shader 需要 `#pragma target 4.5` + `#pragma exclude_renderers gles`；回落 GLES 草会消失。工具 `GraphicsApiSetup`，详见 [grass_brg_plan](Docs/grass_brg_plan.md) §1.1。

**2022.3 没有**：GPUResidentDrawer、内置体积雾/光/水体、RenderGraph。**Forward+ 例外**：URP 14.0.12 已实现（`RenderingMode.ForwardPlus=2`），但**当前决定不切**；工程保持 Forward（`m_RenderingMode=0`），200 人 VAT 已在 Forward 下渲染 PASS。见 [gpu_ecs_animation_baker](Docs/gpu_ecs_animation_baker.md) §1.2。

**ASTC**：地形 albedo 数组用 `ASTC_8x8`，不用 6x6（GTS 的 `ASTC_6x6(50)` 实际映射到无 alpha 的 `ASTC_RGB_6x6`，而 alpha 是高度混合权重）。见 [gts_srp_batcher](Docs/gts_srp_batcher.md) §8.5。

## 2. 模块与选型

> 「现状」是代码/资产里实际存在的；「规划」尚未落地。不一致时以现状为准。

| 模块 | 现状 | 规划 | 详见 |
|---|---|---|---|
| 地形生成 | Gaia Pro VS 已装 | 同 | [terrain_streaming](Docs/terrain_streaming.md) |
| 地形细节雕琢 | 未接入 | GeNa Pro | 同上 |
| Terrain → Mesh | GTS 已落地：144 个 subTile，`saveResolution=Half` | 加 LOD 链、走 YooAsset | 同上 + [gts_srp_batcher](Docs/gts_srp_batcher.md) |
| 地表合批 | SRP Batcher 已生效；纹理数组 ASTC_8x8 | 维持 | [gts_srp_batcher](Docs/gts_srp_batcher.md) |
| 大世界流式 | SECTR 承担全部 chunk 加载；已打节流/限流/缓存补丁 | SECTR 降为区域级 | [terrain_streaming](Docs/terrain_streaming.md) §12 |
| 水体 | Crest 5.10.0；中心湖 `(192,96)` / 半径 190m / 水位 264m / 深 30m；SimpleTransparency + Quad；`LodLevels=5`、分辨率 128、Depth SDF 关 | 真机验反射/水下 | [water](Docs/water.md) §1–§6 |
| 昼夜/天气 | Enviro 3.3.2b：Time/Lighting/Sky + Weather 9 预设 + Fog Low 1/4 res；体积云关 | 真机验性能/水下 | [atmosphere](Docs/atmosphere.md) §0 |
| 后处理 | URP 内置；主场景无 Volume | 内置 Uber pass 做调色/ACES | [postprocess](Docs/postprocess.md) |
| 草 | BRG + 逐 chunk 流式：36 chunk 烘焙 **435 万实例 / 133MB**；Terrain detail 已关；三层 LOD 均采 albedo 图集 | 阴影/透光、真机 | [grass_brg_progress](Docs/grass_brg_progress.md) |
| 树 | Terrain 渲染 + Impostor：7 原型（4 云杉占 99.99%）；4.6 万实例；每 prefab 2 级 LOD；`TreeDistance=450` | 阴影/透光、BRG 化、真机 | [vegetation](Docs/vegetation.md) §6 |
| 植被 LOD | Amplify Impostors 1.1.0（4 个云杉，URP14） | 其余 41 个原型未烘 | [vegetation](Docs/vegetation.md) §6.6 |
| 玩家动画 | 未接入 | Animancer Pro（CPU） | [character_animation](Docs/character_animation.md) |
| NPC 动画 | GPU ECS Animation Baker 1.3.6：**200 人整圈落地**（Ch19/Ch14/Ch09/Ch46 四种角色，4× 大小，r=191–205，Y=267.06–301.84）；Forward 下渲染 PASS，真机未验 | 湖边人群视觉/岸坡优化、真机性能 | [gpu_ecs_animation_baker](Docs/gpu_ecs_animation_baker.md) |
| 资源管理 | YooAsset 3.0.7 **已实现但无调用方** | 唯一资源入口 | [模块规约](Packages/com.tuyoogame.yooasset/AGENTS.md) |

## 3. 高频规则

### 3.1 渲染管线（写 pass / RendererFeature / 后处理前）

1. **天空盒唯一来源是 Enviro**：`EnviroSkyModule.SetupSkybox()` 写 `RenderSettings.skybox`；`m_AmbientMode=0` 依赖它。不要再加第二个天空盒或天空 pass。
2. **CopyDepth / CopyColor 按需**：当前 `m_RequireDepthTexture=1`（Enviro 体积雾/云硬依赖）、`m_RequireOpaqueTexture=0`。不要用 ForcePrepass/depth priming 去换，也不要叠第二份。唯一事实源见 [rendering_rules](Docs/rendering_rules.md) §1.2。
3. **完成功能必须同时给性能账**：pass 数、全屏 RT 数、blit 数、批处理路径、低画质档能否关闭。说不出来就是没做完。
4. **管线里尽量不 Blit**：优先调 pass 顺序、复用已有上下文纹理；禁止全分辨率 ping-pong。第三方固有 blit 要计数（Enviro 雾 1 copy + 1 合成）。
5. **后处理 / 调色 / ACES 走 URP 内置 Uber pass**：不另起全屏 pass，LUT 保持 32，不叠第二套后处理。

### 3.2 批处理

SRP Batcher（地表/树/常规物体）→ GPU Instancing → BRG（草）。动态合批关闭。新增渲染内容必须说明走哪条路径。

### 3.3 草与树（两条路径，不要混谈）

- 草走 BRG（`GrassBRGManager` + `GrassBRG.shader`），不受 Terrain 管理，也不进 SRP Batcher。
- 树走 Terrain（`drawTreesAndFoliage=1`），由 TerrainRenderer 提交，SRP Batcher 覆盖。
- 关草用 `detailObjectDistance`，**不要用 `drawTreesAndFoliage`**（会连树一起关）。
- 树没有运行时密度参数；改实例数只能改 `TerrainData.treeInstances` 或重跑 Gaia。

**树四条硬约束**（详见 [vegetation](Docs/vegetation.md) §6）：
1. 一个材质槽缺失 = 整批树放弃 GPU instancing（日志 `couldn't be instanced because one of the materials is missing`），先 grep 这条再怀疑 keyword。
2. keyword 跟 `_PW_ShaderMode` 严格对应：树干 `WIND` / 针叶 `ALPHATEST+SSS+WIND+WORLDMAP` / 远景 `+BILLBOARD`；不要强行统一。
3. 改 keyword 必须直接改 `.mat` 文本；`Material.DisableKeyword` 对 ShaderGraph 材质不持久。
4. Terrain 树能吃 impostor：改 prefab 的 LODGroup 即可，不需要导出成 GameObject。

### 3.4 草

- **显色靠 albedo 图集，不是 tint**：三层 LOD 的每类型材质都要启用 `_USE_ALBEDO_TEX`；`shader_feature` 只 `SetFloat` 不切换变体（静默失效）。
- 竖直面片不能用地表法线做 Lambert；用朝相机混合 + 包裹光照。
- 不开 `AlphaToMask`（MSAA=1 时 alpha-to-coverage 退化，近景草会碎）。
- tint / 类型尺寸走材质属性，GPU buffer 保持 32B/实例。
- 编辑器走 `GrassData/*.bytes`；打包后 `Assets/` 不存在，必须经 YooAsset。当前打包路径会禁用草并报错，不再合成大数组。

### 3.5 水（Crest）

- 湖盆参数唯一来源是 `LakeCarve`（Center/Radius/WaterLevel/Depth）；重做顺序见 [Assets/Editor/AGENTS.md](Assets/Editor/AGENTS.md) §3.5.2。
- 小湖必须 `DefaultClippingState=EverythingClipped` + `ClipLodInput(Cube, Inverted=true)`；`Inverted=false` 会全裁。
- Absorption / Scattering 默认关闭；材质必须显式写 `_Crest_AbsorptionColor` / `_Crest_Absorption` / `_Crest_Scattering`，否则水面纯黑。
- `Lod.Enabled` 的 getter 依赖运行时 `_Valid`，编辑期恒 false，不能当校验判据。
- 当前 **SimpleTransparency + Quad**：`_Crest_TransparencyMinimumAlpha=0.12`；透明由 depth + min alpha 决定，不是只调 Absorption/Scattering。
- 水下项必须写在 **Water.mat（水面材质）**上；`UnderwaterRenderer` 每帧把水面材质覆盖到 Volume 材质。
- 切相机要同步 Crest `Viewer` / SECTR loader / MainCamera tag / Enviro 相机，全部加进 `LakeCameraRig.Apply()`。
- F 键飞行前必须 `LakeOrbitCamera.Paused`，否则 LateUpdate 会覆盖飞行位移。
- 验收前禁 `PegasusTourController`；并设 `ambientMode=Flat`、对每个 `GTSMesh` 调 `Refresh()`。

### 3.6 大气（Enviro）

- `ENVIRO_URP` 必须手动加（`AtmosphereSetup.EnsureDefines`），否则天空/雾/云静默失效。
- 保存场景前必须 `EnviroManager.LoadConfiguration()`，否则 Play Mode 模块全 null。
- 雾 pass 被改到透明之后（`EnviroURPRenderPass` 构造函数，`[FishDemo]` 补丁）；升级 Enviro 会丢，必须重打。
- `m_RequireDepthTexture=1` 是体积雾/云的硬依赖；`m_RequireOpaqueTexture=0`。
- 质量字段分散在 `Enviro Mobile Quality.asset` 与 `AtmosphereController` 场景字段，改前读 [atmosphere](Docs/atmosphere.md) §0。
- 水下雾由 `AtmosphereController` 关闭；Enviro 自身没有水下检测。
- HUD 用 `ComputeHudWidth` + `MeasureBlock`，白字 + 1px 投影；不用 `<size=-2>`。
- 草风由 `AtmosphereController` 每 0.25s 桥接；树风仍由 PW shader 管。

### 3.7 资源与内存

- YooAsset 是**规划**的唯一入口，当前无调用方；SECTR/草/树仍走各自路径。禁止 `Resources.Load`；新代码接资源必须经 `AssetLoader`。
- 反复加载用缓存解决，不用池解决；chunk 卸载由 SECTR 场景卸载承担，Mesh 池未实现。
- 托管 GC 增量已关闭，不要依赖 `GCMode=Manual/Disabled`；大块数据不进托管堆。
- 草驻留跟随 SECTR，buffer 复用池上限 32MB，驻留 chunk 硬上限 24。
- 渲染分层：Gameplay 不得直接操作 `CommandBuffer` / `Graphics.*`。
- 夜晚光源 ≤ 6，其余靠 emissive + Bloom（当前 Forward，per-object limit 4；Forward+ 未启用）。

### 3.8 角色动画

- 玩家选型 **Animancer（CPU，未接入）**；NPC/人群选型 **GPU ECS Animation Baker（VAT，200 人整圈落地，4 种角色 × 4× 大小）**。两者可用同一套 PBR ShaderGraph + VAT keyword 保持视觉一致，但渲染路径不同、不会合批。
- VAT 硬约束：网格只用 UV0；材质必须是 ShaderGraph + `GpuEcsAnimator` Subgraph；运行时只用 `BakedAssets_*` prefab，改源资产必须重烘。
- 实体走 Entities Graphics（BRG），**不是 SRP Batcher**；普通物件仍靠 SRP Batcher，URP Asset 的 SRP Batcher 保持开启。
- Entities Graphics 1.4 文档要求 URP Forward+，工程保持 **Forward**；200 人 VAT 实测 PASS，不切 Forward+。详见 [包规约](Assets/GPUECSAnimationBaker/AGENTS.md)。

## 4. 子目录规约

> 进入对应目录写代码前读该目录的 `AGENTS.md`，不要默认全读。

| 目录 | 何时读 |
|---|---|
| [Assets/Scripts](Assets/Scripts/AGENTS.md) | 写任何自研代码前（程序集边界、命名空间） |
| [Assets/Scripts/Editor](Assets/Scripts/Editor/AGENTS.md) | 改 Gaia 工具链、草烘焙与 BRG 验证 |
| [Assets/Scripts/Runtime](Assets/Scripts/Runtime/AGENTS.md) | 改资源层、BRG 草运行时、相机 rig |
| [Assets/Editor](Assets/Editor/AGENTS.md) | 改 SECTR 切块 / GTS 转换 / 地形调参 / 树的密度与 LOD / 验证脚本 |
| [Assets/Procedural Worlds](Assets/Procedural%20Worlds/AGENTS.md) | 用第三方插件、排查 Gaia/SECTR/GTS/Pegasus |
| [Assets/Scenes](Assets/Scenes/AGENTS.md) | 改场景、重跑世界生成、重接草/Pegasus |
| [Assets/GPUECSAnimationBaker](Assets/GPUECSAnimationBaker/AGENTS.md) | 接 NPC/人群、改 VAT 材质或 shader、排查实体不显示 |
| [Packages/com.tuyoogame.yooasset](Packages/com.tuyoogame.yooasset/AGENTS.md) | 改资源层代码前 |

## 5. 文档索引

> **按需读取，不要默认全读。** 「何时读」是触发条件。

**常读**

| 文档 | 何时读 |
|---|---|
| [rendering_rules](Docs/rendering_rules.md) | 写任何渲染代码前：管线铁律 + 批处理路径 |
| [performance_budget](Docs/performance_budget.md) | 评估性能、做验收（GPU / pass / 带宽） |

**按模块查**

| 文档 | 何时读 |
|---|---|
| [grass_brg_plan](Docs/grass_brg_plan.md) | 做草 BRG、查 Vulkan 约束、采样率与烘焙规模（长，跳读） |
| [grass_brg_progress](Docs/grass_brg_progress.md) | **改任何草代码前**：落地状态、实测、踩坑、复现命令 |
| [gts_srp_batcher](Docs/gts_srp_batcher.md) | 改 GTS shader、排查地形合批 / 视觉不一致 |
| [world_implementation](Docs/world_implementation.md) | 重跑世界生成、排查 chunk 加载、改世界尺寸（长，跳读） |
| [terrain_streaming](Docs/terrain_streaming.md) | 地形、流式加载、SECTR；**§12 流式补丁 / 缓存 / 内存归属** |
| [water](Docs/water.md) | 水体、水下、中心湖；**改水前先读 §1–§3**，§4 透明/水下，§6 性能 |
| [atmosphere](Docs/atmosphere.md) | 昼夜、天气、体积雾光；**§0 落地配置** |
| [vegetation](Docs/vegetation.md) | 植被、树的密度/LOD/impostor/keyword；**改树前先读 §6** |
| [postprocess](Docs/postprocess.md) | 调色、后处理；**§0 性能规则** |
| [character_animation](Docs/character_animation.md) | 玩家动画、NPC GPU 动画 |
| [gpu_ecs_animation_baker](Docs/gpu_ecs_animation_baker.md) | 接 NPC/人群、查角色清单、性能账与验证步骤 |
| [pooling_lod](Docs/pooling_lod.md) | 资源缓存、chunk 加载策略 |

**查阅型（长，只跳读需要的节）**

| 文档 | 只在以下情况打开 |
|---|---|
| [yooasset_3.0.7](Docs/yooasset_3.0.7.md) | 理解 YooAsset 内部链路/模块职责；改包源码 |
| [yooasset_practices](Docs/yooasset_practices.md) | 具体症状：初始化失败、句柄失效、卸载不掉 |
| [project_todo](Docs/project_todo.md) | 查工程差距、待决策事项、渲染待办 |

## 6. 工程约定

- **Git LFS**：目录限定 + 精确扩展名，不要改成全局通配。Unity YAML（`.unity/.mat/.prefab/.asset`）刻意不进 LFS。收窄规则后必须用 `git lfs ls-files -n` + `git check-attr --stdin filter` 核对，不能留下 `filter: unspecified` 的 LFS 指针。
- **目录**：`Assets/Scripts/Runtime/{Rendering,Gameplay}` 已有，`Streaming/` 尚未创建。
- **默认程序集目录**：`Assets/Editor/`、`Assets/GTSRuntime/` 没有 asmdef，因为要引用同样无 asmdef 的 SECTR / GTS。不要把运行时脚本放进 `Assets/Editor/`，也不要放进有 asmdef 的 `Assets/Scripts/Runtime/`。
- **Gameplay 不能加 asmdef**：它要引用 Pegasus / Crest 类型，加了就编不过。
- **注释**：自研代码（含 shader）零注释；「为什么」写进本文件或 `Docs/`。第三方源码不改动，`[FishDemo]` 等补丁标记留在原处。
- **全局配置留档**：`~/.zcode/AGENTS.md` 的镜像在仓库 `global/AGENTS.md`（不进 Unity 构建）；改任意一边必须同步另一边，`diff` 校验后一起提交推送。
