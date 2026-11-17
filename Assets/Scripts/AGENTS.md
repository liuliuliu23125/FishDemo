# Assets/Scripts 规约

> 本目录是**自研代码**，与 `Assets/Procedural Worlds/`（第三方）、`Packages/`（嵌入式包）区分开。
> 建议一次读完。

## 1. 三个程序集 + 一个"无程序集"目录，边界不能越

| 目录 | 程序集 | 平台 | 可引用 |
|---|---|---|---|
| `Editor/` | `FishDemo.Editor` | Editor only | `GaiaCore`、`GaiaEditor`、`YooAsset`、`YooAsset.Editor`、URP、Entities、`FishDemo.Resources`、`FishDemo.Rendering` |
| `Runtime/Resources/` | `FishDemo.Resources` | 全平台 | 仅 `YooAsset` |
| `Runtime/Rendering/` | `FishDemo.Rendering` | 全平台 | `Unity.Burst`、`Unity.Collections`、`Unity.Mathematics`；**`allowUnsafeCode=true`** |
| `Runtime/Gameplay/` | **无 asmdef** → `Assembly-CSharp` | 全平台 | 默认程序集，可引用 Pegasus / SECTR / GTS |

定义文件：`Editor/FishDemo.Editor.asmdef`、`Runtime/Resources/FishDemo.Resources.asmdef`、
`Runtime/Rendering/FishDemo.Rendering.asmdef`。

**硬规则**：

1. **Runtime 不得引用 Editor**，也不得引用 `GaiaCore`（Gaia 的 asmdef 不是 Editor-only，但它的 API 大多只能在编辑器期用）。
2. **`FishDemo.Editor` 不得引用 SECTR**。SECTR 没有 asmdef，编在 `Assembly-CSharp-Editor`，asmdef 程序集引用不到。需要调 SECTR 的代码放 `Assets/Editor/`（默认程序集）。
3. **`Runtime/Gameplay/` 不能加 asmdef**。`PegasusTourController.cs` 直接 `using Pegasus`，而 Pegasus 无 asmdef、编在 `Assembly-CSharp`；asmdef 程序集引用不到默认程序集，加了就编不过。
4. `FishDemo.Rendering` 必须 `allowUnsafeCode=true`（工程全局是 `false`）：BRG 的 `AddBatch` 只接受 raw buffer，剔除回调要写裸指针。
5. 新增第三方依赖前先查该包有没有 asmdef、属于哪个程序集，不要靠"应该有"。

## 2. 命名空间与程序集名分离

- 程序集名 `FishDemo.Resources`，**命名空间是 `FishDemo.AssetManagement`**。
- 原因：程序集名与命名空间同名时，C# 会优先解析为程序集，导致类型找不到。
- 同样：`FishDemo.Editor` 程序集里的命名空间是 `FishDemo.EditorTools`。
- `FishDemo.Rendering` 程序集与命名空间**同名**（`FishDemo.Rendering`）——这里能用，因为该程序集内没有以程序集名命名的类型。

## 3. 目录职责

```
Runtime/Resources/          YooAsset 初始化 + 句柄封装（见 Packages/com.tuyoogame.yooasset/AGENTS.md）
Runtime/Rendering/          草 BRG 运行时（GrassBRGManager / GrassInstance）
Runtime/Gameplay/           Pegasus 巡游演示控制器（无 asmdef）
Editor/                     Gaia 世界生成工具链 + 草烘焙/验证（菜单 FishDemo/*）
```

`Runtime/Streaming/` 仍**不存在**，不要假设它有东西。

**渲染分层**：`Runtime/Rendering/` 属于 Render Data 层，可以碰 `BatchRendererGroup` / `GraphicsBuffer`；
`Runtime/Gameplay/` 不得直接操作 `CommandBuffer` 或 `Graphics.*`（见 [rendering_rules](../../Docs/rendering_rules.md) §1）。

**新增 pass / RendererFeature 前**必须先读 [rendering_rules](../../Docs/rendering_rules.md) §1：
说明 pass 数、全屏 RT / blit 数、依赖哪张 depth/opaque、走哪条批处理路径。
不要为功能方便打开 `m_RequireOpaqueTexture` 或新增全屏 ping-pong。

## 4. 当前状态

- `YooAssetInitializer.InitializeAsync()` 与 `AssetLoader` 已实现但**仍无调用方**。
- 实际资源加载走 SECTR 的 `SceneManager.LoadSceneAsync`，不经过 YooAsset。
- 接线的**前置条件**：先跑菜单 `YooAsset/FishDemo/初始化资源收集配置`，再跑 `YooAsset/FishDemo/执行模拟构建并注入路径`；否则编辑器模拟模式会因包根目录为空而抛异常。
- **草 BRG 是当前唯一在跑的自研渲染逻辑**：`GrassBRGManager` 属 `FishDemo.Rendering`，
  已进 `FishDemo.Editor` 的引用列表（编辑器工具要写 `SetDetailPrototypeMeshes`）。
  它的草数据仍用 `System.IO.File.ReadAllBytes` 直读 `GrassData/`（编辑器/开发期通路），
  **打包后 `Assets/` 不存在：流式模式下禁用草并报错**；编辑器兜底合成最多 10000 实例。
  正式版必须改成经 YooAsset（[grass_brg_progress](../../Docs/grass_brg_progress.md) §6）。

## 5. 相关文档

| 文档 | 何时读 |
|---|---|
| [Editor/AGENTS.md](Editor/AGENTS.md) | 改 Gaia 世界生成工具链、草烘焙与验证 |
| [Runtime/AGENTS.md](Runtime/AGENTS.md) | 改资源层、接 YooAsset、改 BRG 草运行时 |
| [../Editor/AGENTS.md](../Editor/AGENTS.md) | 改 SECTR 切块/收尾/验证脚本 |
| [world_implementation](../../Docs/world_implementation.md) | 要理解 World 场景怎么生成的 |
| [rendering_rules](../../Docs/rendering_rules.md) | 写任何渲染代码前 |
| [grass_brg_progress](../../Docs/grass_brg_progress.md) | 改草相关代码前 |
| [GPUECSAnimationBaker/AGENTS.md](../GPUECSAnimationBaker/AGENTS.md) | 接 NPC/人群、写 VAT 接入代码前（细节见 [落地手册](../../Docs/gpu_ecs_animation_baker.md)） |
