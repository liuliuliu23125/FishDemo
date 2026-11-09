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
| 色彩空间 / 后端 / 压缩 | Linear / IL2CPP+ARM64 / ASTC |
| 帧率 / 机型 | 30fps / iQOO 16 |
| 同屏角色上限 | 20 |
| 联网 | 将来需要，**当前不实现网络层**，只保证客户端表现可承载多人 |

**2022.3 没有的东西**（不要假设存在）：GPUResidentDrawer、Forward+、内置体积雾/光、内置水体、RenderGraph。

## 2. 模块与选型

> **「现状」列是代码/资产里实际存在的，「规划」列是选型结论、尚未落地。**
> 两者不一致时以「现状」为准，不要假设规划中的包已安装。

| 模块 | 现状（已落地） | 规划 | 详见 |
|---|---|---|---|
| 地形生成 | Gaia Pro VS（已装） | 同 | [terrain_streaming](Docs/terrain_streaming.md) |
| 地形细节雕琢 | 未接入 | GeNa Pro | 同上 |
| **Terrain → Mesh 分块** | **GTS 已落地**：144 个 subTile，`saveResolution=Half` | 加 LOD 链、走 YooAsset | 同上 + [gts_srp_batcher](Docs/gts_srp_batcher.md) |
| **地表合批** | **SRP Batcher 已生效**（改 GTS shader 加 CBUFFER） | 视 Frame Debugger 结果决定是否去 `isStatic` | [gts_srp_batcher](Docs/gts_srp_batcher.md) |
| 大世界流式 | **SECTR**（承担全部 chunk 加载） | SECTR 降为区域级，chunk 走 YooAsset | 同上 |
| 水体 | **Gaia Water (URP)** | Crest Water 5 | [water](Docs/water.md) |
| 昼夜/天气/大气雾 | **无**（仅一盏平行光） | Enviro 3 | [atmosphere](Docs/atmosphere.md) |
| 体积雾/光 | 未接入 | Fog Mist 2 / Light Beam 2 / Lights 2 | 同上 |
| 后处理 | URP 内置（主场景无 Volume） | 同 | [postprocess](Docs/postprocess.md) |
| 草 | Terrain detail（`drawInstanced=true`，可见实例约 136 万） | 自研 GPU instancing / BRG | [vegetation](Docs/vegetation.md) |
| 植被 LOD | 未接入 | Amplify Impostors | 同上 |
| Shader 变体裁剪 | 未接入 | Shader Control | 同上 |
| 玩家动画 | 未接入 | Animancer Pro（CPU） | [character_animation](Docs/character_animation.md) |
| NPC / 远程玩家动画 | 未接入 | GPU ECS Animation Baker（VAT） | 同上 |
| **资源管理** | **YooAsset 3.0.7 已实现但无调用方** | 唯一资源入口 | [模块规约](Packages/com.tuyoogame.yooasset/AGENTS.md) |

## 3. 高频规则（写代码前必看）

**批处理**：SRP Batcher（必须）→ GPU Instancing（必须）→ BRG（目标）。动态合批保持关闭。**任何新增渲染内容必须说明走哪条批处理路径**。

**渲染分层**：Gameplay 层不得直接操作 `CommandBuffer` 或 `Graphics.*`。

**资源加载**：YooAsset 是唯一入口，禁止 `Resources.Load`。改资源层前先读 [模块规约](Packages/com.tuyoogame.yooasset/AGENTS.md)。

**反复加载用缓存解决，不用池解决**。chunk 卸载不 Destroy Mesh，池化复用。

**夜晚光源** ≤ 6，其余靠 emissive + Bloom（无 Forward+，per-object limit 为 4）。

详见 [rendering_rules](Docs/rendering_rules.md)。

## 4. 子目录规约

> 进入对应目录写代码前读该目录的 `AGENTS.md`，不要默认全读。

| 目录 | 行数 | 何时读 |
|---|---|---|
| [Assets/Scripts](Assets/Scripts/AGENTS.md) | 51 | 写任何自研代码前（程序集边界、命名空间约定） |
| [Assets/Scripts/Editor](Assets/Scripts/Editor/AGENTS.md) | 80 | 改 Gaia 世界生成工具链 |
| [Assets/Scripts/Runtime](Assets/Scripts/Runtime/AGENTS.md) | 46 | 改资源层、接 YooAsset |
| [Assets/Editor](Assets/Editor/AGENTS.md) | 122 | 改 SECTR 切块 / GTS 转换 / 验证脚本 |
| [Assets/Procedural Worlds](Assets/Procedural%20Worlds/AGENTS.md) | 81 | 用第三方插件、排查 Gaia/SECTR/GTS 问题 |
| [Assets/Scenes](Assets/Scenes/AGENTS.md) | 64 | 改场景、重跑世界生成 |
| [Packages/com.tuyoogame.yooasset](Packages/com.tuyoogame.yooasset/AGENTS.md) | 90 | 改资源层代码前 |

## 5. 文档索引

> **按需读取，不要默认全读。** 下表"何时读"是触发条件——条件不满足就不打开。

**常读（短，建议一次读完）**

| 文档 | 行数 | 何时读 |
|---|---|---|
| [rendering_rules](Docs/rendering_rules.md) | 73 | 写任何渲染代码前（含 SRP Batcher 实战结论） |
| [performance_budget](Docs/performance_budget.md) | 42 | 评估性能、做验收 |
| [Packages/…/AGENTS.md](Packages/com.tuyoogame.yooasset/AGENTS.md) | 90 | 改资源层代码前 |

**按模块查（只读对应模块那份）**

| 文档 | 何时读 |
|---|---|
| [gts_srp_batcher](Docs/gts_srp_batcher.md) | 改 GTS shader、排查地形合批（SRP Batcher）不生效、mesh 地形与 Terrain 视觉不一致。**173 行** |
| [world_implementation](Docs/world_implementation.md) | 重跑世界生成、排查 chunk 加载、改世界尺寸/海平面、核对"场景怎么来的"。**394 行，只跳读需要的节** |
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
  `Assets/Gaia User Data` 657MB。提交前必须先配 LFS。
- **目录**：`Assets/Scripts/Runtime/{Rendering,Streaming,Gameplay}`、`Assets/ThirdParty/`。
- **默认程序集目录**：`Assets/Editor/`（Editor 专用）、`Assets/GTSRuntime/`（运行时）。
  两者都**没有 asmdef**，因为它们要引用同样无 asmdef 的 SECTR / GTS。
  不要把运行时脚本放进 `Assets/Editor/`（打包后不存在），
  也不要放进有 asmdef 的 `Assets/Scripts/Runtime/`（引用不到 `Assembly-CSharp`）。
- **注释**：关键函数写中文注释，说明意图、约束、非显而易见的原因。
