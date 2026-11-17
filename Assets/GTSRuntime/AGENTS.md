# Assets/GTSRuntime 规约

> **运行时**程序集 `Assembly-CSharp`（没有 asmdef）。它必须留在 Editor 目录之外。

## 1. 为什么放在这

GTS 无 asmdef，编进 `Assembly-CSharp`；`GtsRuntimeDataBootstrap` 必须进播放器程序集。因此不能放 `Assets/Editor/`、不能放有 asmdef 的 `Runtime/`，也不能自己加 asmdef（引用不到 `GTSMesh` / `GTSProfile`）。通用程序集约定见 [Docs/AGENTS.md](../../Docs/AGENTS.md) §3。

## 2. 职责

`GtsRuntimeDataBootstrap` 由 `Assets/Editor/Gts/GtsFixMeshRendering.cs` 挂到每个 `GTS_Mesh` 节点：

1. `Start()` 取同物体的 `GTSMesh`；
2. 若 `GTSMesh.profile` 为空，回填序列化的 `profile`；
3. 调 `GTSMesh.Refresh()`。

chunk 场景是流式加载的，`GTSMesh` 的运行时初始化必须由场景内组件驱动，不能依赖管理器跨场景查找。

## 3. 重跑后的状态

`SectrPipeline` 会清掉该组件；重跑后必须 `GtsFixMeshRendering.RunAll`，顺序见 [../Editor/Gts/AGENTS.md](../Editor/Gts/AGENTS.md) §2.4。

## 4. 相关文档

| 文档 | 何时读 |
|---|---|
| [gts_srp_batcher](../../Docs/gts_srp_batcher.md) | GTS 渲染路径、worldSizeOverride、贴图数组 |
| [../Editor/Gts/AGENTS.md](../Editor/Gts/AGENTS.md) | 转换/挂载脚本与重跑顺序 |
