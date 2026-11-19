# Docs 索引与项目现状

> 收纳项目级现状、文档索引与工程约定；根文件不重复。

## 1. 模块现状

> 「现状」是代码/资产里实际存在的；「规划」尚未落地。不一致时以现状为准。

| 模块 | 现状 | 规划 | 详见 |
|---|---|---|---|
| 地形生成 | Gaia Pro VS 已装 | 同 | [terrain_streaming](terrain_streaming.md) |
| 地形细节雕琢 | 未接入 | GeNa Pro | 同 |
| Terrain → Mesh | GTS 已落地：144 个 subTile，`saveResolution=Half` | 加 LOD 链、走 YooAsset | 同 + [gts_srp_batcher](gts_srp_batcher.md) |
| 地表合批 | SRP Batcher 已生效；纹理数组 ASTC_8x8 | 维持 | [gts_srp_batcher](gts_srp_batcher.md) |
| 大世界流式 | SECTR 承担全部 chunk 加载；已打节流/限流/缓存补丁 | SECTR 降为区域级 | [terrain_streaming](terrain_streaming.md) §12 |
| 水体 | Crest 5.10.0；中心湖已出水面（SimpleTransparency + Quad） | 真机验反射/水下 | [water](water.md) §1–§6 |
| 昼夜/天气 | Enviro 3.3.2b 已接入（昼夜/天气/雾/体积光）；体积云关；夜景曝光补偿 | 真机验性能/水下 | [atmosphere](atmosphere.md) §0 |
| 后处理 | 已启用：全局 Volume + ACES / HDR grading / Bloom / 调色（无 DOF） | 真机验 GPU 耗时 | [postprocess](postprocess.md) |
| 灯光 | 12 支环湖火把，默认 1 盏，`L` 循环 0/1/2/4/6，硬上限 6；无附加光阴影 | 真机验光照成本 | [rendering_rules](rendering_rules.md) §4 |
| 草 | BRG + 逐 chunk 流式已落地（36 chunk）；Terrain detail 已关 | 阴影/透光、真机 | [grass_brg_progress](grass_brg_progress.md) |
| 树 | Terrain 渲染 + Impostor（4 个云杉）；4.6 万实例 | 阴影/透光、BRG 化、真机 | [vegetation](vegetation.md) §6 |
| 植被 LOD | Amplify Impostors 1.1.0（4 个云杉） | 其余 41 个原型未烘 | [vegetation](vegetation.md) §6.6 |
| 玩家动画 | 未接入 | Animancer Pro（CPU） | [character_animation](character_animation.md) |
| NPC 动画 | GPU ECS Animation Baker 1.3.6：200 人整圈落地；Forward 渲染 PASS | 湖边人群视觉/岸坡优化、真机性能 | [gpu_ecs_animation_baker](gpu_ecs_animation_baker.md) |
| 资源管理 | YooAsset 3.0.7 **已实现但无调用方** | 唯一资源入口 | [模块规约](../Packages/com.tuyoogame.yooasset/AGENTS.md) |

## 2. 文档索引

> 按需读取；「长」只跳读需要的节。

| 文档 | 何时读 | 长度 |
|---|---|---|
| [rendering_rules](rendering_rules.md) | 写任何渲染代码前：管线铁律 + 批处理路径 | 短 |
| [performance_budget](performance_budget.md) | 评估性能、做验收（GPU / pass / 带宽） | 中 |
| [grass_brg_plan](grass_brg_plan.md) | 做草 BRG、查 Vulkan 约束、采样率与烘焙规模 | 长 |
| [grass_brg_progress](grass_brg_progress.md) | **改任何草代码前**：落地状态、实测、踩坑、复现命令 | 中 |
| [gts_srp_batcher](gts_srp_batcher.md) | 改 GTS shader、排查地形合批 / 视觉不一致 | 中 |
| [world_implementation](world_implementation.md) | 重跑世界生成、排查 chunk 加载、改世界尺寸 | 长 |
| [terrain_streaming](terrain_streaming.md) | 地形、流式加载、SECTR；**§12 流式补丁 / 缓存 / 内存归属** | 中 |
| [water](water.md) | 水体、水下、中心湖；**改水前先读 §1–§3**，§4 透明/水下，§6 性能 | 中 |
| [crest_internals](crest_internals.md) | 第一次改 Crest 前：数据流、帧时间线、pass 顺序、Clip/透明数学 | 中 |
| [atmosphere](atmosphere.md) | 昼夜、天气、体积雾光；**§0 落地配置** | 中 |
| [vegetation](vegetation.md) | 植被、树的密度/LOD/impostor/keyword；**改树前先读 §6** | 中 |
| [postprocess](postprocess.md) | 调色、后处理；**§0 当前参数 / pass 账 / 夜景曝光** | 短 |
| [character_animation](character_animation.md) | 玩家动画、NPC GPU 动画 | 中 |
| [gpu_ecs_animation_baker](gpu_ecs_animation_baker.md) | 接 NPC/人群、查角色清单、性能账与验证步骤 | 中 |
| [pooling_lod](pooling_lod.md) | 资源缓存、chunk 加载策略 | 中 |
| [yooasset_3.0.7](yooasset_3.0.7.md) | 理解 YooAsset 内部链路/模块职责；改包源码 | 长 |
| [yooasset_practices](yooasset_practices.md) | 具体症状：初始化失败、句柄失效、卸载不掉 | 短 |
| [project_todo](project_todo.md) | 查工程差距、待决策事项、渲染待办 | 短 |

## 3. 工程约定

- **Git LFS**：目录限定 + 精确扩展名，不要改成全局通配。Unity YAML（`.unity/.mat/.prefab/.asset`）刻意不进 LFS。收窄规则后必须用 `git lfs ls-files -n` + `git check-attr --stdin filter` 核对，不能留下 `filter: unspecified` 的 LFS 指针。
- **目录**：`Assets/Scripts/Runtime/{Rendering,Gameplay}` 已有，`Streaming/` 尚未创建。
- **默认程序集目录**：`Assets/Editor/`、`Assets/GTSRuntime/` 没有 asmdef，因为要引用同样无 asmdef 的 SECTR / GTS。不要把运行时脚本放进 `Assets/Editor/`，也不要放进有 asmdef 的 `Assets/Scripts/Runtime/`。
- **Gameplay 不能加 asmdef**：它要引用 Pegasus / Crest 类型，加了就编不过。
- **注释**：自研代码（含 shader）零注释；「为什么」写进最近的 AGENTS.md 或 Docs。**其余**第三方源码不改动，`[FishDemo]` 等补丁标记留在原处。
- **URP / core 是 embedded**：`Packages/com.unity.render-pipelines.universal` 与 `Packages/com.unity.render-pipelines.core` 是 **14.0.12 本地副本**（`source: embedded`），由项目维护；改 URP/ACES/shader 必须先读包内 `AGENTS.md`，改动行加 `[FishDemo]` 并登记；升级从 PackageCache 重新复制后重打补丁并回归 `LightingVerify` / `AtmosphereVerify`。
- **全局配置留档**：`~/.zcode/AGENTS.md` 的镜像在仓库 `global/AGENTS.md`（不进 Unity 构建）；改任意一边必须同步另一边，`diff` 校验后一起提交推送。
