# 角色动画选型分析（Animancer Pro / GPU ECS Animation Baker）

> 分析对象：Animancer Pro 8.2.2 / GPU ECS Animation Baker 1.3.6
> 目标环境：Unity 2022.3.62f2 + URP 14.0.12，Android 旗舰机，30fps

## 结论速览

| 包 | 用途 | 是否可用 | 关键风险 |
|---|---|---|---|
| Animancer Pro 8.2.2 | **玩家** CPU 动画 | ✅ 完美匹配 | 几乎无 |
| GPU ECS Animation Baker 1.3.6 | **NPC** GPU 动画 | ✅ 可用 | 依赖多，已下架 |

**分工明确：玩家用 Animancer，NPC 用 GPU Baker。两者不冲突。**

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

**DOTween 已解决**（用户已加入 DOTween Pro 1.0.381）。还需确认 Newtonsoft.Json 是否可通过 UPM 包提供。

### Forward+ 的限制

Manual 建议：

```
It's best to change the rendering path to URP Forward+. Entities Graphics requires this.
You will get a warning if you don't.
```

**2022.3 的 URP 14 没有 Forward+**（Unity 6 / URP 17 才有）。Entities Graphics 会退回普通 Forward，光照数量受 per-object limit 限制。

对本项目影响有限（夜晚光源本就 ≤ 6），但要知道这个限制。

### 适用场景

- ✅ NPC 数量多（10-20 个）时的 GPU 动画
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

**关键：两者必须共用同一套 PBR 材质/shader 才能视觉一致。**

GPU Baker 的 VAT 是 ShaderGraph Subgraph，可以插入项目 PBR shader。但要注意：
- 玩家的 shader 不需要 VAT 节点
- NPC 的 shader 需要 VAT 节点
- **如果两者用不同 shader，会破坏 SRP Batcher 的材质一致性**

**建议**：用同一个 shader，通过 keyword（如 `_USE_VAT`）区分。虽然 keyword 会增加变体，但能保证合批一致性。

## 4. 落地顺序建议

1. **先接 Animancer**（依赖少，风险低）→ 玩家动画跑通。
2. **再验证 GPU Baker**：
   - 装 Entities + Entities Graphics
   - 确认 Newtonsoft.Json 来源
   - 导入 Engine 文件夹，验证 asmdef 编译
   - 跑自带 Sample
3. **最后接 NPC 系统**（与地形 chunk 流式协调）。

## 5. 风险汇总

| 风险 | 等级 | 应对 |
|---|---|---|
| GPU Baker 已下架 | 🔴 高 | **必须 vendor 进仓库** |
| Entities 1.4.8 与 URP 14 的实际兼容性 | 🟡 中 | 包已装，启用 VAT 前先做最小验证 |
| 无 Forward+ | 🟡 中 | 接受，靠 emissive |
| 两套 shader 不一致 | 🟡 中 | 用 keyword 统一 |
| VAT 贴图内存 | 🟡 中 | 控制动画数量/帧率 |

## 待验证项

- Entities 1.x 与 URP 14 的实际兼容性
- Newtonsoft.Json 通过 UPM 引入后 asmdef 能否正确解析
- VAT Subgraph 接入自定义 PBR shader 的效果
- 同屏 20 个 NPC 的实际 GPU 成本
