# GPU ECS Animation Baker 1.3.6 模块规约

> 第三方 VAT（Vertex Animation Texture）角色动画包，位于 `Assets/GPUECSAnimationBaker`（unitypackage 导入，不是 UPM 包）。
> 本目录只读使用；**不改第三方源码**。细节、角色清单、性能账、验证与风险见
> [Docs/gpu_ecs_animation_baker.md](../../Docs/gpu_ecs_animation_baker.md)。

## 0. 定位

- 用途：**NPC / 人群**的 GPU 动画。工程内已接 **200 人整圈**（`LakesideCrowd`，Ch19/Ch14/Ch09/Ch46 四种角色，4× 大小，r=191–205）。玩家选型 Animancer（CPU），见 [character_animation](../../Docs/character_animation.md)。
- 原理：骨骼权重烘进 UV1/2/3，逐帧骨骼矩阵烘进 2D 纹理；顶点着色器采样，ECS 每帧写 per-instance 动画状态。
- 渲染路径：Entities Graphics（BatchRendererGroup + DOTS instancing），**不走 SRP Batcher**。

## 1. 硬约束

| # | 约束 |
|---|---|
| 1 | **不改第三方源码**（`Engine/`、`Samples/`）。自研接入代码放 `Assets/Scripts/Runtime/Gameplay/`（无 asmdef，编入 `Assembly-CSharp`）。 |
| 2 | 网格只能用 **UV0**。UV1/2/3 已被骨骼权重占用；使用了这些通道的模型不能直接烘焙。 |
| 3 | 材质必须是 **ShaderGraph**，并包含 `GpuEcsAnimator` Subgraph。必须有 `_EnableAnimation`（Hybrid Per Instance）、`_AnimatedBoneMatrices`、`_AnimationState`（Hybrid Per Instance）三个属性。 |
| 4 | 运行时只用 **`BakedAssets_*/<Name>_GpuEcsAnimator.prefab`**。源 prefab（SkinnedMeshRenderer + Animator）只用于烘焙；改源资产后必须重烘。 |
| 5 | **经典 Animator 运行时不运行**；IK 等依赖运行时骨骼计算的功能不可用；动画事件只能走 `GpuEcsAnimatorEventBufferElement`。 |
| 6 | 实体由 Entities Graphics 绘制，与 SRP Batcher 是两条路。不要为实体改 URP 的 SRP Batcher 配置；普通物件仍靠 SRP Batcher。 |
| 7 | Entities Graphics 1.4 文档声称 URP **仅支持 Forward+**，引擎会打 warning。工程**决定保持 Forward**（`UniversalRenderer.asset` `m_RenderingMode: 0`）。**200 人混合角色已在 Forward 下渲染 PASS**（俯瞰 draw=4/rendered=95，弧段 draw=8/rendered=37）；真机性能仍未验证。 |
| 8 | URP 14.0.12 **已有** Forward+（`RenderingMode.ForwardPlus = 2`）。旧文档“2022.3 没有 Forward+”不成立；当前工程仍按 Forward / per-object limit 4 运行。 |
| 9 | 动画控制只写 `GpuEcsAnimatorControlComponent` / `GpuEcsAnimatorControlStateComponent`；不要挂 Animator 或 Playable。 |
| 10 | 烘焙产物（mesh / animation texture / material / prefab）是生成物，不手改；改动画、LOD、材质都回到源 prefab 重烘。 |
| 11 | 角色实体不受 SECTR chunk 管理；生成、销毁、驻留上限由自研系统负责，不要指望 chunk 卸载自动回收。 |
| 12 | 不创建第二套 ECS World；Entities Graphics 1.4 不支持多 World 渲染。 |

## 2. 依赖（已锁定）

| 包 | 版本 | 来源 |
|---|---|---|
| Unity | 2022.3.62f2 | `ProjectSettings/ProjectVersion.txt` |
| Entities | 1.4.8 | `packages-lock.json` |
| Entities Graphics | 1.4.21（`unity: 2022.3.20f1`） | `packages-lock.json` |
| Burst / Collections / Mathematics | 1.8.29 / 2.6.8 / 1.3.2 | `packages-lock.json` |
| Newtonsoft.Json | 3.2.2 | `packages-lock.json` |
| DOTween | `Assets/Plugins/Demigiant/DOTween/DOTween.dll`、`.../Editor/DOTweenEditor.dll` | 工程内 |

`GPUECSAnimationBakerEngine.asmdef` 的 GUID 引用均已解析（Entities / Entities.Graphics / Transforms / Hybrid / Burst / Collections / Mathematics）；
`Library/ScriptAssemblies/GPUECSAnimationBakerEngine.dll` 与 `GPUECSAnimationBakerSamples.dll` 已生成。
`LakesideCrowd` 接入后已重新编译并跑通 1 人验证。

## 3. 关键目录与符号

| 路径 / 符号 | 职责 |
|---|---|
| `Engine/AnimatorSystem/GpuEcsAnimatorBehaviour.cs` | 烘焙入口 `GpuEcsAnimatorBaker`；写入动画数据与初始控制组件 |
| `Engine/AnimatorSystem/GpuEcsAnimatorSystem.cs` | 每帧推进动画状态，写 `GpuEcsAnimatorShaderDataComponent` |
| `Engine/AnimatorSystem/GpuEcsAnimatedMeshSystem.cs` | 把 animator 的 shader data 复制到 mesh 实体的 `GpuEcsMaterialAnimationState` |
| `GpuEcsAnimatorControlComponent` / `AnimatorInfo` | 动画 ID、blendFactor、speedFactor、startNormalizedTime、transitionSpeed |
| `GpuEcsAnimatorAspect` | `RunAnimation / StartAnimation / StopAnimation` 便捷封装 |
| `Engine/Shader/GpuEcsAnimator.cginc` | VAT 采样；每顶点最多 6 组权重、每骨骼 3 列、帧间插值 |
| `Engine/Baker/GpuEcsAnimationBakeServices.cs` | 编辑器批量烘焙 API |
| `Samples/SampleScenes/Festival/CrowdSpawnerSystem/` | 官方多人同屏样例（网格人群 + 随机动画） |
| `Samples/Characters/*/BakedAssets_*/` | 12 个已烘焙 prefab（角色 + 附件） |
| `Assets/Scripts/Runtime/Gameplay/LakesideCrowdBehaviour.cs` | 自研湖边人群 Baker + Spawn 系统（200 人、4 种 prefab、4× 缩放） |
| `Assets/Editor/Crowd/LakesideCrowdSetup.cs` | 岸线采样 + authoring scene + World SubScene（菜单 ③） |
| `Assets/Editor/Crowd/LakesideCrowdVerify.cs` | Play Mode 验证：实体数 / 岸线高度 / 动画帧 / RT 截图 |

## 4. 烘焙与使用

1. 源对象：带 `Animator` + 一个或多个 `SkinnedMeshRenderer` + 可选 `LODGroup`。
2. 材质接入 `GpuEcsAnimator` Subgraph（三个属性见硬约束 3）。
3. 用包的菜单 / `GpuEcsAnimationBakerBehaviour` 配置动画、Bone Usage、Transform Usage Flags，生成 `BakedAssets_<Name>/`。
4. 运行时把 `*_GpuEcsAnimator.prefab` 当 ECS prefab：Baker + `EntityCommandBuffer.Instantiate`，再设 `LocalTransform` 与 `GpuEcsAnimatorControlComponent`。
5. 官方样例：`CrowdSpawnerSystem.cs`（Festival）与 `RunnerSpawnerSystem.cs`（Marathon）。样例场景**不在 Build Settings**，要跑先加入。

## 5. 相关文档

| 文档 | 何时读 |
|---|---|
| [Docs/gpu_ecs_animation_baker.md](../../Docs/gpu_ecs_animation_baker.md) | 接 NPC / 人群、改材质或 shader、查性能与验证前 |
| [Docs/character_animation.md](../../Docs/character_animation.md) | 确认 Animancer 与 GPU Baker 的分工 |
| `GPU ECS Animation Baker Manual.pdf` | 官方手册：Requirements / Limitations / GPU Instancing / Bake Services |
| [根 AGENTS.md](../../AGENTS.md) | 项目级硬约束与目录入口 |
| [Docs/AGENTS.md](../../Docs/AGENTS.md) | 项目级模块现状与文档索引 |
