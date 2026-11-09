# Assets/Editor 规约（SECTR + GTS 流水线）

> 默认程序集 `Assembly-CSharp-Editor`（**没有 asmdef**）。
> 本文件约 122 行，改这些脚本前建议一次读完。

## 1. 为什么这个目录不能加 asmdef

SECTR 与 GTS 都没有自己的 asmdef，其编辑器 API（`SECTR_TerrainWindow.SectorizeTerrain`、`SECTR_StreamExport.ExportSceneChunks`、`GTSProfile.ConvertToMesh`）编译进默认的 `Assembly-CSharp-Editor`。

**asmdef 程序集无法引用 `Assembly-CSharp-Editor`**（依赖方向是反的）。所以凡是要调这些 API 的脚本，必须放在这里、走默认程序集。

`SectrPipeline.cs:11-15` 是这条结论的原始记录。**不要给 `Assets/Editor/` 加 asmdef**，加了 SECTR/GTS 调用就编不过。

代价：这里的脚本拿不到 `FishDemo.Editor` 的引用（默认程序集能引用 asmdef，反向不行），需要复用 Gaia 工具时用 `-executeMethod` 分两步跑。

**注意运行时脚本不能放这里**：`GtsRuntimeDataBootstrap.cs` 必须在 `Assets/GTSRuntime/`（同样无 asmdef，
但不在 Editor 目录），否则会被编进 Editor 程序集、打包后不存在。

## 2. 脚本清单

| 脚本 | 职责 | 入口 |
|---|---|---|
| `SectrPipeline.cs` | 2×2 切块 + 导出 36 个 chunk 场景 | `-executeMethod SectrPipeline.SectorizeAndExport` |
| `WorldFinalize.cs` | 删残留地形 + 主相机挂 `SECTR_RegionLoader` | `-executeMethod WorldFinalize.FinalizeScene` |
| `StreamingVerify.cs` | 不依赖 Play Mode 验证空间判定 + 场景可加载 | `-executeMethod StreamingVerify.Run` |
| `FinalIntegrity.cs` | 重建 Build Settings + 全量校验 | `-executeMethod FinalIntegrity.Run` |
| `RichWorldVerify.cs` | 抽样 4 块核对起伏/贴图层/树/草 | `-executeMethod RichWorldVerify.Run` |
| `CleanupOrphanAssets.cs` | 按活跃批次清理孤儿 chunk / TerrainData / session | `-executeMethod CleanupOrphanAssets.Run` |
| `TerrainChunkTuning.cs` | 批量改/还原 36 块 Terrain 渲染参数（可逆） | `Report` / `ApplyMobile` / `RestoreDesktop` |
| `ChunkRenderStats.cs` | 静态统计 pass 数 / 树 / 草实例量 | `-executeMethod ChunkRenderStats.Run` |
| `GtsConvertOne.cs` | 单块 Terrain→GTS Mesh（**一进程一块**） | `-gtsChunk "<名>" -gtsFirst 0/1` |
| `GtsApplyMeshToChunks.cs` | 把 GTS mesh 挂进 chunk 场景 | `RunAll` / `RunOne -gtsChunk` |
| `GtsFixMeshRendering.cs` | 补 GTSMesh 组件 + 写 worldSizeOverride | `-executeMethod GtsFixMeshRendering.RunAll` |

## 3. 铁律

### 3.0 GTS 转换必须一进程一块

`GtsConvertOne.Run` 每次只处理一个 chunk，**不要写循环在单进程里连跑**。

原因：`GTSProfile.CreateTextureArrays()` → `StartProfile()` → `GetGTSComponents()` 会遍历
当前进程内所有场景的 `GTSComponent`。连续 `OpenScene(Single)` 时上一场景组件已被销毁，
但 GTS 内部仍持有引用 → `MissingReferenceException`。实测单进程内刷新引用无效。

首块必须带 `-gtsFirst 1`（负责生成全局纹理数组），其余 `-gtsFirst 0` 复用。
非首块的材质要手动回绑 `_AlbedoArray`/`_NormalArray`，否则走 FallbackError。

### 3.1 切块遍历 Terrain 组件，不要按 root 去重


Gaia 把 9 块地形挂在同一个 `Gaia Terrains` 根下。按 `transform.root` 去重只会得到 1 个**不含 Terrain 组件**的根，导致一块都没切（`SectrPipeline.cs:39-43`）。

正确做法：`FindObjectsOfType<Terrain>()` 取快照再逐个处理（`SectorizeTerrain` 结尾会把原地形 `SetActive(false)`，边遍历边改会漏）。

### 3.2 切块必须在植被撒点之后

`SectorizeTerrain` 会把原 TerrainData 的高度/Alpha/Detail/Tree 复制到新 Terrain，并把 sector 范围内的静态子物体收进 sector（`SECTR_TerrainWindow.cs:106` 起）。
切块后再撒点，点会落在已失活的原始地形上。

### 3.3 收尾必须删掉原始地形残留

切块只 `SetActive(false)`，不清 TerrainData。9 块 × 1025² 高度图约 945 万顶点会在加载主场景时全部进内存，与 36 个 chunk 重复，流式就失去意义。

只删 `!activeInHierarchy` 的，避免误删 sector 内容（`WorldFinalize.cs:44-52`）。
chunk 引用的 TerrainData 在 `World/TerrainSplits/`，与原始地形独立，删原始地形安全。

### 3.4 chunk 里的 Terrain 渲染参数会被重跑覆盖

`SectrPipeline` 重新切块会生成全新 chunk 场景，Terrain 渲染参数回到 Gaia Desktop 预设（`drawInstanced=0`、detail 距离 150、splat 1024 等）。

调参用 `TerrainChunkTuning.ApplyMobile`（幂等、可 `RestoreDesktop` 回滚），**不要手改 36 个场景**。
流水线重跑后必须再执行一次 `ApplyMobile`，否则参数会退回 PC 档位。

### 3.5 chunk 里的 GTS mesh 也会被重跑覆盖

同上：重跑 `SectrPipeline` 后，chunk 里的 `GTS_Mesh` 节点、`GTSMesh` 组件、
`GtsRuntimeDataBootstrap` 全部消失。重跑后必须依次执行：

```
TerrainChunkTuning.ApplyMobile
  → GtsConvertOne.Run（逐块，一进程一块）
  → GtsApplyMeshToChunks.RunAll
  → GtsFixMeshRendering.RunAll
```

`GtsFixMeshRendering` 不只是"补组件"——它写入的 `m_worldSizeOverride`（256m）
决定贴图 tiling，漏掉会让地表贴图密度错 4 倍。详见 [gts_srp_batcher](../../Docs/gts_srp_batcher.md)。

### 3.6 数量断言硬编码了 36

`WorldFinalize.cs:109`、`FinalIntegrity.cs:82`、`RichWorldVerify.cs:83` 三处把 chunk/sector 数量写死为 36。

改世界尺寸或切块粒度时，这三处**必须同步改**，否则验证会误报 FAIL。
（`StreamingVerify.cs:104-108` 用的是 `sectors.Length`，不写死，无需改。）

### 3.7 Exit code 语义

| 值 | 含义 |
|---|---|
| 0 | 成功 / PASS |
| 1 | 前置条件不满足或异常中止 |
| 2 | 校验不通过（数量不符、边界为空、场景打不开等） |

批处理命令行**不要加 `-quit`**，由脚本自己 `EditorApplication.Exit`。

### 3.8 重复执行要幂等

这些脚本会被反复重跑。新增逻辑时：

- 挂组件前先清掉已有的（`WorldFinalize.cs:71-75` 的做法）；
- 重建 Build Settings 时整体覆盖而不是追加（`FinalIntegrity.cs:32-40`）；
- 清理资产时按**当前场景实际引用的批次**判断，不要盲删目录（`CleanupOrphanAssets.cs:33-44`）。

## 4. 相关文档

| 文档 | 何时读 |
|---|---|
| [gts_srp_batcher](../../Docs/gts_srp_batcher.md) | 改 GTS 转换脚本、排查 mesh 地形合批/视觉不一致 |
| [world_implementation](../../Docs/world_implementation.md) | 要重跑流水线、理解 chunk 结构、查已知缺陷 |
| [terrain_streaming](../../Docs/terrain_streaming.md) | 评估 SECTR 的定位与限制 |
| [pooling_lod](../../Docs/pooling_lod.md) | 设计 chunk 加载/卸载策略 |
| [../Scripts/Editor/AGENTS.md](../Scripts/Editor/AGENTS.md) | 另一半（Gaia）流水线 |
