# FishDemo 项目规约

Unity 2022.3 + URP 的 **PBR 写实钓鱼游戏 Demo**，视觉对标《对马岛之魂》。
目标 **iQOO 16 / 30fps**，大世界分块流式加载，含水下与昼夜。

## 0. 协作红线

1. **只陈述有据可查的事实**：文件路径 + 行号、工具输出、或文档原文。不确定就说"不知道"，禁止补全历史、禁止虚构。
2. **结论分三类标注，不可混写**：代码事实 / 文档声称（未验证）/ 推断（注明依据）。
3. **版本兼容必须查证**：看 `package.json` 的 `unity` 字段、asmdef、源码宏、官方文档。**包名和宣传语不可作依据。**

详见 [collaboration_rules](Docs/collaboration_rules.md)。

## 1. 硬约束

| 项 | 值 |
|---|---|
| Unity / URP | 2022.3.62f2 / 14.0.12 |
| 色彩空间 / 后端 / 压缩 | Linear / IL2CPP+ARM64 / **ASTC** |
| 帧率 / 机型 | 30fps / iQOO 16 |
| 同屏角色上限 | 20 |
| 联网 | 将来需要，**当前不实现网络层**，只保证客户端表现可承载多人 |
| 合批 | **SRP Batcher 唯一路径；静态合批已关闭**（二者互相干扰） |
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
| 大世界流式 | **SECTR**（承担全部 chunk 加载） | SECTR 降为区域级，chunk 走 YooAsset | 同上 |
| 水体 | **Gaia Water (URP)** | Crest Water 5 | [water](Docs/water.md) |
| 昼夜/天气/大气雾 | **无**（仅一盏平行光） | Enviro 3 | [atmosphere](Docs/atmosphere.md) |
| 体积雾/光 | 未接入 | Fog Mist 2 / Light Beam 2 / Lights 2 | 同上 |
| 后处理 | URP 内置（主场景无 Volume） | 同 | [postprocess](Docs/postprocess.md) |
| **草** | **BRG 已落地（M2.2）**：36 chunk 烘焙 **1265 万实例 / 387MB**；Terrain detail 已关（`DetailObjectDistance=0`）；单 shader `FishDemo/Grass/BRGMinimal`，LOD0 采 albedo 图集 + alpha 裁剪 | 阴影/透光、SECTR 流式、真机 | [grass_brg_progress](Docs/grass_brg_progress.md) |
| **树** | **仍走 Terrain**：45 原型 / **46.5 万实例**；单 shader `PW_General_ShaderGraph_URP`、单 keyword 组合；每 prefab 3~4 级 LODGroup | 后续单独处理（BRG / Impostor） | [grass_brg_plan](Docs/grass_brg_plan.md) §6 |
| 植被 LOD | 未接入 | Amplify Impostors | 同上 |
| Shader 变体裁剪 | 未接入 | Shader Control | 同上 |
| 玩家动画 | 未接入 | Animancer Pro（CPU） | [character_animation](Docs/character_animation.md) |
| NPC / 远程玩家动画 | 未接入 | GPU ECS Animation Baker（VAT） | 同上 |
| **资源管理** | **YooAsset 3.0.7 已实现但无调用方** | 唯一资源入口 | [模块规约](Packages/com.tuyoogame.yooasset/AGENTS.md) |

## 3. 高频规则（写代码前必看）

**批处理**：SRP Batcher（地表/树/常规物体）→ GPU Instancing → **BRG（草，已落地）**。动态合批保持关闭。**任何新增渲染内容必须说明走哪条批处理路径**。

**草与树是两条完全不同的路径，不要混谈**：
- 草走 BRG（`GrassBRGManager` + `GrassBRG.shader`），不经过 SRP Batcher，也不受 Terrain 管理。
- 树走 Terrain 自带渲染（`drawTreesAndFoliage=1`），由 TerrainRenderer 提交，SRP Batcher 覆盖。
- 因为两者都在改「植被」，**改草时用 `detailObjectDistance`，不要用 `drawTreesAndFoliage`**——后者会把树一起关掉。

**草的显色靠 albedo 图集，不是 tint**（2026-11-12 踩过）：
花的颜色（雏菊白瓣黄芯/三叶草粉/蒲公英黄）在 `PW_LawnGrass_00_D` / `PW_WildGrass_00_D`
两张 RGBA 图集的像素里，**传 tint 显不出花色**（tint 乘在绿色渐变上，乘什么都是绿）。
- LOD0 材质启用 `_USE_ALBEDO_TEX`：`SAMPLE_TEXTURE2D` + `clip(a - _Cutoff)`，图集约一半像素 alpha==0。
- `shader_feature` 必须显式 `EnableKeyword`，只 `SetFloat` 不切换变体（静默失效）。
- **不要开 `AlphaToMask`**：工程 `Assets/Settings/UniversalRP.asset` 是 `m_MSAA: 1`，
  无 MSAA 时 alpha-to-coverage 退化，近景草会被切成亮绿碎屑。
- tint 走**材质属性**（每类型一个材质）而非 per-instance 数据，GPU buffer 保持 32B/实例。

**渲染分层**：Gameplay 层不得直接操作 `CommandBuffer` 或 `Graphics.*`。

**资源加载**：YooAsset 是唯一入口，禁止 `Resources.Load`。改资源层前先读 [模块规约](Packages/com.tuyoogame.yooasset/AGENTS.md)。

**反复加载用缓存解决，不用池解决**。chunk 卸载不 Destroy Mesh，池化复用。

**夜晚光源** ≤ 6，其余靠 emissive + Bloom（无 Forward+，per-object limit 为 4）。

详见 [rendering_rules](Docs/rendering_rules.md)。

## 4. 子目录规约

> 进入对应目录写代码前读该目录的 `AGENTS.md`，不要默认全读。

| 目录 | 行数 | 何时读 |
|---|---|---|
| [Assets/Scripts](Assets/Scripts/AGENTS.md) | 66 | 写任何自研代码前（程序集边界、命名空间约定） |
| [Assets/Scripts/Editor](Assets/Scripts/Editor/AGENTS.md) | 124 | 改 Gaia 世界生成工具链、草烘焙与 BRG 验证 |
| [Assets/Scripts/Runtime](Assets/Scripts/Runtime/AGENTS.md) | 96 | 改资源层、接 YooAsset、改 BRG 草运行时 |
| [Assets/Editor](Assets/Editor/AGENTS.md) | 166 | 改 SECTR 切块 / GTS 转换 / 地形调参 / 验证脚本 |
| [Assets/Procedural Worlds](Assets/Procedural%20Worlds/AGENTS.md) | 105 | 用第三方插件、排查 Gaia/SECTR/GTS/Pegasus 问题 |
| [Assets/Scenes](Assets/Scenes/AGENTS.md) | 85 | 改场景、重跑世界生成、重接草/Pegasus |
| [Packages/com.tuyoogame.yooasset](Packages/com.tuyoogame.yooasset/AGENTS.md) | 90 | 改资源层代码前 |

## 5. 文档索引

> **按需读取，不要默认全读。** 下表"何时读"是触发条件——条件不满足就不打开。

**常读（短，建议一次读完）**

| 文档 | 行数 | 何时读 |
|---|---|---|
| [rendering_rules](Docs/rendering_rules.md) | 84 | 写任何渲染代码前（批处理路径归属表 + SRP Batcher 实战结论） |
| [performance_budget](Docs/performance_budget.md) | 42 | 评估性能、做验收 |
| [Packages/…/AGENTS.md](Packages/com.tuyoogame.yooasset/AGENTS.md) | 90 | 改资源层代码前 |

**按模块查（只读对应模块那份）**

| 文档 | 何时读 |
|---|---|
| [grass_brg_plan](Docs/grass_brg_plan.md) | 做草改 BRG、排查草渲染/剔除、查 Vulkan 约束、采样率与烘焙规模。**628 行，只跳读需要的节** |
| [grass_brg_progress](Docs/grass_brg_progress.md) | **改任何草相关代码前先读**：当前落地状态、实测数据、13 条「草消失且无报错」的踩坑、复现命令。**264 行** |
| [gts_srp_batcher](Docs/gts_srp_batcher.md) | 改 GTS shader、排查地形合批（SRP Batcher）不生效、mesh 地形与 Terrain 视觉不一致。**212 行** |
| [world_implementation](Docs/world_implementation.md) | 重跑世界生成、排查 chunk 加载、改世界尺寸/海平面、核对"场景怎么来的"。**396 行，只跳读需要的节** |
| [terrain_streaming](Docs/terrain_streaming.md) | 做地形、流式加载、SECTR |
| [water](Docs/water.md) | 做水体、水下、反射 |
| [atmosphere](Docs/atmosphere.md) | 做昼夜、天气、体积雾光 |
| [vegetation](Docs/vegetation.md) | 做植被、LOD、Shader 变体裁剪 |
| [postprocess](Docs/postprocess.md) | 调色、后处理 |
| [character_animation](Docs/character_animation.md) | 玩家动画、NPC GPU 动画 |
| [pooling_lod](Docs/pooling_lod.md) | 资源缓存、对象池、chunk 加载策略 |

**查阅型（长，只跳读需要的节）**

| 文档 | 行数 | 只在以下情况打开 |
|---|---|---|
| [yooasset_3.0.7](Docs/yooasset_3.0.7.md) | 2039 | 要理解 YooAsset 内部链路/模块职责；改动包源码；排查"为什么这么设计"。**顶部有分节导读** |
| [yooasset_practices](Docs/yooasset_practices.md) | 154 | 遇到具体症状：初始化失败、句柄失效、卸载不掉、参数依据。**顶部按症状分节** |
| [project_todo](Docs/project_todo.md) | 49 | 查工程差距、待决策事项 |
| [collaboration_rules](Docs/collaboration_rules.md) | 75 | 协作方式、历史踩坑 |

## 6. 工程约定

- **Git LFS**：大世界资源必须配。
  ⚠️ 当前**尚未配置**（无 `.gitattributes`），而 `Assets/GTS User Data` 298MB、
  `Assets/Gaia User Data` 657MB、**`Assets/Scenes/World/GrassData` 387MB**（36 个 `.bytes`）。
  提交前必须先配 LFS。
- **目录**：`Assets/Scripts/Runtime/{Rendering,Streaming,Gameplay}`、`Assets/ThirdParty/`。
  `Rendering/`（草 BRG）与 `Gameplay/`（Pegasus 巡游控制器）已存在；`Streaming/` 尚未创建。
- **默认程序集目录**：`Assets/Editor/`（Editor 专用）、`Assets/GTSRuntime/`（运行时）。
  两者都**没有 asmdef**，因为它们要引用同样无 asmdef 的 SECTR / GTS。
  不要把运行时脚本放进 `Assets/Editor/`（打包后不存在），
  也不要放进有 asmdef 的 `Assets/Scripts/Runtime/`（引用不到 `Assembly-CSharp`）。
- **Pegasus 同样无 asmdef**：`PegasusTourController.cs` 因此放在 `Assets/Scenes` 之外的
  `Assets/Scripts/Runtime/Gameplay/`——该目录**没有 asmdef**，编进 `Assembly-CSharp`，
  才能引用 Pegasus 类型。给 Gameplay 加 asmdef 会让这个脚本编不过。
- **注释**：关键函数写中文注释，说明意图、约束、非显而易见的原因。
