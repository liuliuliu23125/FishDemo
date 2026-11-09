# Assets/Scripts 规约

> 本目录是**自研代码**，与 `Assets/Procedural Worlds/`（第三方）、`Packages/`（嵌入式包）区分开。
> 本文件约 51 行，建议一次读完。

## 1. 两个程序集，边界不能越

| 目录 | 程序集 | 平台 | 可引用 |
|---|---|---|---|
| `Editor/` | `FishDemo.Editor` | Editor only | `GaiaCore`、`GaiaEditor`、`YooAsset`、`YooAsset.Editor`、URP、Entities、`FishDemo.Resources` |
| `Runtime/Resources/` | `FishDemo.Resources` | 全平台 | 仅 `YooAsset` |

定义文件：`Editor/FishDemo.Editor.asmdef`、`Runtime/Resources/FishDemo.Resources.asmdef`。

**硬规则**：

1. **Runtime 不得引用 Editor**，也不得引用 `GaiaCore`（Gaia 的 asmdef 不是 Editor-only，但它的 API 大多只能在编辑器期用）。
2. **`FishDemo.Editor` 不得引用 SECTR**。SECTR 没有 asmdef，编在 `Assembly-CSharp-Editor`，asmdef 程序集引用不到。需要调 SECTR 的代码放 `Assets/Editor/`（默认程序集）。
3. 新增第三方依赖前先查该包有没有 asmdef、属于哪个程序集，不要靠"应该有"。

## 2. 命名空间与程序集名分离

- 程序集名 `FishDemo.Resources`，**命名空间是 `FishDemo.AssetManagement`**。
- 原因：程序集名与命名空间同名时，C# 会优先解析为程序集，导致类型找不到。
- 同样：`FishDemo.Editor` 程序集里的命名空间是 `FishDemo.EditorTools`。

## 3. 目录职责

```
Runtime/Resources/          YooAsset 初始化 + 句柄封装（见 Packages/com.tuyoogame.yooasset/AGENTS.md）
Editor/                     Gaia 世界生成工具链（菜单 FishDemo/*）
```

规划中但**尚不存在**的目录（不要凭 AGENTS.md 根文件去假设它们存在）：
`Runtime/Rendering/`、`Runtime/Streaming/`、`Runtime/Gameplay/`。

## 4. 当前状态

- `YooAssetInitializer.InitializeAsync()` 与 `AssetLoader` 已实现但**全工程无调用方**。
- 实际资源加载走 SECTR 的 `SceneManager.LoadSceneAsync`，不经过 YooAsset。
- 接线的**前置条件**：先跑菜单 `YooAsset/FishDemo/初始化资源收集配置`，再跑 `YooAsset/FishDemo/执行模拟构建并注入路径`；否则编辑器模拟模式会因包根目录为空而抛异常。

## 5. 相关文档

| 文档 | 何时读 |
|---|---|
| [Editor/AGENTS.md](Editor/AGENTS.md) | 改 Gaia 世界生成工具链 |
| [Runtime/AGENTS.md](Runtime/AGENTS.md) | 改资源层、接 YooAsset |
| [../Editor/AGENTS.md](../Editor/AGENTS.md) | 改 SECTR 切块/收尾/验证脚本 |
| [world_implementation](../../Docs/world_implementation.md) | 要理解 World 场景怎么生成的 |
| [rendering_rules](../../Docs/rendering_rules.md) | 写任何渲染代码前 |
