# Assets/Editor/Crowd 规约（湖边人群）

> 默认程序集 `Assembly-CSharp-Editor`，无 asmdef。
> 人群实体走 GPU ECS Animation Baker（VAT / Entities Graphics），细节见
> [gpu_ecs_animation_baker](../../../Docs/gpu_ecs_animation_baker.md)。

## 1. 脚本清单

| 脚本 | 职责 | 入口 |
|---|---|---|
| `LakesideShoreProbe.cs` | **只读**：扫一圈湖岸高度（r=188..240），找可站立低岸 | `-executeMethod LakesideShoreProbe.Run` |
| `LakesideCrowdSetup.cs` | 湖边人群：采样岸线、创建 `LakesideCrowd.unity` + World SubScene | 菜单 `FishDemo/World/③ 布置湖边人群 (1人/30人/200人)`；`ApplyOne` / `ApplyThirty` / `ApplyCrowd` |
| `LakesideCrowdVerify.cs` | **需真实图形设备；可 `-batchmode`（离屏 RT 截图）**：Play Mode 验实体数/岸线高度/动画帧变化 | `-executeMethod LakesideCrowdVerify.Run` |

`LakesideShoreProbe` 同时挂在菜单 `③ 湖边岸线探针`。

## 2. 当前布局

布局与验收状态见 [gpu_ecs_animation_baker §9.6](../../../Docs/gpu_ecs_animation_baker.md)。场景在 `Assets/Scenes/LakesideCrowd.unity`，通过 World SubScene 加载；可站立低岸由 `LakesideShoreProbe` 采样写入。

## 3. 验收注意

- 预加载、等待与截图细节见 [gpu_ecs_animation_baker §9.6](../../../Docs/gpu_ecs_animation_baker.md)；可在 `-batchmode` 下跑（离屏 RT）。
- 实体不显示的 VAT 硬约束见 [GPUECSAnimationBaker §1](../../GPUECSAnimationBaker/AGENTS.md)。

## 4. 相关文档

| 文档 | 何时读 |
|---|---|
| [gpu_ecs_animation_baker](../../../Docs/gpu_ecs_animation_baker.md) | 接 NPC/人群、查角色清单、性能账与验证步骤 |
| [../../GPUECSAnimationBaker/AGENTS.md](../../GPUECSAnimationBaker/AGENTS.md) | 包级规约、VAT 材质/shader |
| [../Water/AGENTS.md](../Water/AGENTS.md) | 湖岸几何与相机 rig |
