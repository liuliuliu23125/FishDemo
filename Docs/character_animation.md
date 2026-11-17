# 角色动画选型分析（Animancer Pro / GPU ECS Animation Baker）

> 分析对象：Animancer Pro 8.2.2 / GPU ECS Animation Baker 1.3.6
> 目标环境：Unity 2022.3.62f2 + URP 14.0.12，Android 旗舰机，30fps

## 结论速览

| 包 | 用途 | 是否可用 | 关键风险 |
|---|---|---|---|
| Animancer Pro 8.2.2 | **玩家** CPU 动画 | ✅ 完美匹配 | 几乎无 |
| GPU ECS Animation Baker 1.3.6 | **NPC** GPU 动画 | ✅ 已导入；200 人整圈验证（Forward 渲染 PASS） | 依赖多，已下架；详见 [gpu_ecs_animation_baker](gpu_ecs_animation_baker.md) |

**分工明确：玩家选型 Animancer，NPC 选型 GPU Baker。两者不冲突；玩家未接入，NPC 已接 1 人。**

## 1. Animancer Pro 8.2.2

### 版本兼容

`package.json` 原文：

```json
{
  "name": "com.kybernetik.animancer",
  "version": "8.2.2",
  "unity": "2022.3",
  "dependencies": {
    "com.unity.modules.animation": "1.0.0"
  }
}
```

**完美匹配**：要求 2022.3，工程是 2022.3.62f2。依赖只有 `com.unity.modules.animation`（Unity 内置模块），**无任何第三方依赖**。

**结构**：UPM 包（`Packages/com.kybernetik.animancer`），干净。

### 为什么适合玩家角色

Animancer 的核心价值是**按需播放动画，直接控制细节**，不受 Animator Controller 的状态机限制。

对钓鱼游戏的玩家角色，这意味着：

| 需求 | Animancer 优势 |
|---|---|
| 钓鱼动作（抛竿/收线/等待/起鱼） | 直接 `Play(clip)`，无需建状态机 |
| 动作打断（如起鱼时切到惊讶） | `CrossFade` 精确控制 |
| 上半身/下半身分离 | 支持 Layer + Avatar Mask |
| 动画事件 | 原生支持 |
| 动态混合（如走路速度影响步频） | 直接控制 `Speed` / `Time` |

相比 Animator Controller：
- ✅ 无需在编辑器里连线
- ✅ 代码即配置，易于版本管理
- ✅ 无状态机开销

### 性能注意

Animancer 底层仍用 Unity 的 Playable API（`Animator` + `PlayableGraph`），所以：
- 仍是 **CPU 蒙皮**（除非开 GPU skinning）
- `Animator.cullingMode` 仍需设置
- 骨骼数仍需控制（≤ 60）

**它不会让 CPU 动画变便宜，但会让动画逻辑更可控。**

### 与项目其他部分的配合

- 玩家只有一个，CPU 动画成本可控。
- 需要与 `Animancer` 的 `PlayableGraph` 生命周期管理（避免泄漏）。
- 与 Crest 水体的交互（玩家在水中的动画切换）可以用 Animancer 的 `FadeMode` 平滑过渡。

## 2. GPU ECS Animation Baker 1.3.6

**落地状态（2026-11-17）**：已导入 `Assets/GPUECSAnimationBaker`（Engine + Samples，两个程序集已生成 DLL）；工程内已接 **200 人整圈**（`LakesideCrowd`，Ch19/Ch14/Ch09/Ch46 四种角色，4× 大小，r=191–205，Y=267.06–301.84），Forward 下渲染 PASS；121/200 站在高于水位 10m 以上的陡坎；样例未跑。
原理、角色清单、烘焙流程、性能账与验证步骤见 [gpu_ecs_animation_baker](gpu_ecs_animation_baker.md)。

### 技术原理

Manual 原文：

```
It works by baking all vertex bone weights into the UV channels of the meshes (uv1, uv2 & uv3)
+ baking all the bone transforms per frame into animation textures.
A custom vertex shader will take care of the rest.
An An ECS animation system takes care of updating the shader values each frame.
```

VAT（Vertex Animation Texture）+ 骨骼矩阵贴图方案。

### 硬性前置条件

| 依赖 | 状态 | 说明 |
|---|---|---|
| Unity 2022.2.20f1+ | ✅ 满足 | Manual 要求 |
| Entities 1.0.8+ | ✅ **已安装 1.4.8** | `Packages/manifest.json` |
| Entities Graphics | ✅ **已安装 1.4.21** | `Packages/manifest.json` |
| DOTween.dll | ✅ 已存在 | `Assets/Plugins/Demigiant/DOTween/DOTween.dll` |
| Newtonsoft.Json.dll | ✅ 3.2.2 | `com.unity.nuget.newtonsoft-json` |

asmdef 原文：

```json
"precompiledReferences": [
    "DOTween.dll",
    "DOTweenEditor.dll",
    "Newtonsoft.Json.dll",
    ""
]
```

**DOTween 已解决**（工程内 DOTween Pro 1.0.381）。**Newtonsoft.Json 也已解决**：`com.unity.nuget.newtonsoft-json` 3.2.2 已锁定，asmdef 已编译。

### Forward+ 的限制

Manual 建议：

```
It's best to change the rendering path to URP Forward+. Entities Graphics requires this.
You will get a warning if you don't.
```

**修正**：工程用的 URP 14.0.12 **已实现 Forward+**（`RenderingMode.ForwardPlus = 2`，`UniversalRenderer.cs` 有完整渲染实现，官方文档也包含 Forward+ 章节）。旧结论“2022.3 没有 Forward+”不成立。但 `Assets/Settings/UniversalRenderer.asset` 当前仍是 **Forward**（`m_RenderingMode: 0`），per-object limit = 4；Entities Graphics 1.4 文档要求 Forward+，非 Forward+ 时只打 warning。**200 人 VAT 已在 Forward 下渲染 PASS，决定不切 Forward+**，见 [gpu_ecs_animation_baker](gpu_ecs_animation_baker.md) §1.2。

对本项目影响不小：Entities Graphics 1.4 文档要求 Forward+，而工程当前是 Forward（per-object limit = 4）。先跑样例确认实际表现，再决定是否切换；切换的回归范围见 [gpu_ecs_animation_baker](gpu_ecs_animation_baker.md) §1.2。

### 适用场景

- ✅ NPC 数量多（VAT 路径；工程当前上限 200，200 人 Editor/Forward 已验证，真机待测）
- ✅ 远处 NPC 的批量渲染
- ❌ 不适合玩家（需要精确的动画事件和打断控制，Animancer 更合适）

## 3. 两套系统的共存

```
玩家角色：
  SkinnedMeshRenderer + Animancer (PlayableGraph) + CPU 蒙皮
  → 骨骼 ≤ 60，Animator.cullingMode = CullUpdateTransforms

NPC：
  MeshRenderer + GPU ECS Animation Baker (VAT) + ECS
  → 走 Entities Graphics 渲染
```

**视觉一致性与批处理是两件事**：
- 玩家：SkinnedMeshRenderer + Animancer，走 CPU 蒙皮 + SRP Batcher。
- NPC：MeshRenderer + Entities Graphics，走 BRG + DOTS instancing。
- 两者**不会合批**，共不共用 shader 都不改变这一点。共用 PBR 外观的目的只是视觉一致。

**做法**：把 `GpuEcsAnimator` Subgraph 接进项目的 PBR ShaderGraph，用 keyword（如 `_USE_VAT`）区分玩家/NPC 变体；
NPC 的实体批次仍由 Entities Graphics 管理。

## 4. 落地顺序建议

1. **先接 Animancer**（依赖少，风险低）→ 玩家动画跑通。
2. **验证 GPU Baker（200 人已按 Forward 跑通整圈）**：
   - Entities / Entities Graphics / Newtonsoft.Json 已装并锁定
   - Engine asmdef 已编译（`Library/ScriptAssemblies/*.dll`）
   - 200 人整圈：`LakesideCrowdVerify.Run` PASS，实体 200/200、r=191–205、Y=267.06–301.84
   - 样例 `0_BasicsEntities.unity` / `FestivalEntities.unity` 仍未跑
   - 视觉/性能待优化：121/200 在陡坎上；真机 200 人 30fps 未测；见 [gpu_ecs_animation_baker](gpu_ecs_animation_baker.md) §9
3. **最后接 NPC 系统**（与地形 chunk 流式协调；角色实体不受 SECTR 管理，生命周期需自研）。

## 5. 风险汇总

| 风险 | 等级 | 应对 |
|---|---|---|
| GPU Baker 已下架 | 🟢 低 | 已 vendor 进仓库 `Assets/GPUECSAnimationBaker` |
| Entities 1.4.8 与 URP 14 的实际兼容性 | 🟢 低 | 200 人已在 Forward 渲染 PASS；真机待验 |
| 工程当前是 Forward，Entities Graphics 要求 Forward+ | 🔴 高 | 先跑样例；切 Forward+ 需全项目回归 Crest/Enviro/GTS/草 |
| 两套 shader 不一致 | 🟡 中 | 同一套 PBR ShaderGraph 加 VAT keyword；保证视觉一致，但不会让实体与 SRP Batcher 合批 |
| VAT 贴图内存 | 🟡 中 | 控制动画数量/帧率 |

## 待验证项

- 200 人阴影开销、真机 30fps、湖岸视觉（121/200 在陡坎）
- 切 Forward+ 的收益（当前决定不切；Forward 下 200 人已 PASS）
- VAT Subgraph 接入项目 PBR ShaderGraph 的视觉一致性
- IL2CPP / ARM64 / Vulkan 构建、Entities Graphics shader 变体剥离
