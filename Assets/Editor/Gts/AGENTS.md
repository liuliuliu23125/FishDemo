# Assets/Editor/Gts 规约（Terrain → GTS Mesh）

> 默认程序集 `Assembly-CSharp-Editor`，无 asmdef。GTS 无 asmdef，其 API 编进默认程序集。
> 输入是 `Assets/Scenes/World/Chunks/*.unity`，输出在 `Assets/GTS User Data/Scenes/<chunk>/`。

## 1. 脚本清单

| 脚本 | 职责 | 入口 |
|---|---|---|
| `GtsConvertOne.cs` | 单块 Terrain→GTS Mesh（**一进程一块**） | `-gtsChunk "<名>" -gtsFirst 0/1` |
| `GtsApplyMeshToChunks.cs` | 把 GTS mesh 挂进 chunk 场景（`GTS_Mesh` 节点） | `RunAll` / `RunOne -gtsChunk` |
| `GtsFixMeshRendering.cs` | 补 `GTSMesh` 组件 + 写 `m_worldSizeOverride` | `-executeMethod GtsFixMeshRendering.RunAll` |
| `GtsAstcCompress.cs` | 把 Profile 的纹理数组压成 `ASTC_8x8` 并重烘 | `-executeMethod GtsAstcCompress.Run` |

## 2. 铁律

### 2.1 转换必须一进程一块

`GtsConvertOne.Run` 每次只处理一个 chunk，**不要写循环在单进程里连跑**。

原因：`GTSProfile.CreateTextureArrays()` → `StartProfile()` → `GetGTSComponents()` 会遍历当前进程内所有场景的 `GTSComponent`。连续 `OpenScene(Single)` 时上一场景组件已被销毁，但 GTS 内部仍持有引用 → `MissingReferenceException`。实测单进程内刷新引用无效。

首块必须带 `-gtsFirst 1`（负责生成全局纹理数组），其余 `-gtsFirst 0` 复用。
非首块的材质要手动回绑 `_AlbedoArray`/`_NormalArray`，否则走 FallbackError。

### 2.2 `GtsFixMeshRendering` 不只是「补组件」

它写入的 `m_worldSizeOverride`（256m）决定贴图 tiling，漏掉会让地表贴图密度错 4 倍。详见 [gts_srp_batcher](../../../Docs/gts_srp_batcher.md)。

### 2.3 纹理数组用 `ASTC_8x8`，不用 6x6

GTS 的 `ASTC_6x6(50)` 实际映射到无 alpha 的 `ASTC_RGB_6x6`，而 alpha 是高度混合权重。必须 `ASTC_8x8`。
地形 albedo 数组要压成 `ASTC_8x8`；`GtsAstcCompress` 会改 `Assets/Gaia User Data/GTS World Profile.asset` 的 `textureArraySettings` 并重烘数组。

### 2.4 重跑流水线后的固定顺序

```
TerrainChunkTuning.ApplyMobile
  → TerrainChunkTuning.DisableDetail
  → GtsConvertOne.Run × 每块（一进程一块，首块 -gtsFirst 1）
  → GtsApplyMeshToChunks.RunAll
  → GtsFixMeshRendering.RunAll
```

漏掉 `GtsFixMeshRendering` 时 chunk 里只有 mesh 没有 `GTSMesh` 组件，SRP Batcher 不生效且运行时 `GtsRuntimeDataBootstrap` 找不到组件。

`GtsApplyMeshToChunks` 给 subTile 设 `go.isStatic = false`；平台静态合批由 `World/BatchingSetup.FixAll` 关闭（Android/Standalone 0/0）。重跑流水线不要重新打开静态合批。

## 3. 运行时衔接

运行时由 `Assets/GTSRuntime/GtsRuntimeDataBootstrap.cs` 回填 `profile` 并 `Refresh()`；放置原因与职责见 [GTSRuntime/AGENTS.md](../../GTSRuntime/AGENTS.md)。

## 4. 相关文档

| 文档 | 何时读 |
|---|---|
| [gts_srp_batcher](../../../Docs/gts_srp_batcher.md) | 改 GTS shader、排查地形合批 / 视觉不一致 |
| [terrain_streaming](../../../Docs/terrain_streaming.md) | 地形流式与 SECTR 的衔接 |
| [../World/AGENTS.md](../World/AGENTS.md) | 切块/收尾与重跑顺序 |
| [../../GTSRuntime/AGENTS.md](../../GTSRuntime/AGENTS.md) | 运行时回填组件 |
