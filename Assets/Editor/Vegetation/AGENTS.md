# Assets/Editor/Vegetation 规约（树材质 / LOD / impostor / 密度）

> 默认程序集 `Assembly-CSharp-Editor`，无 asmdef。
> 改树之前先读 [vegetation](../../../Docs/vegetation.md) §6；本目录脚本按用途分组如下，全部可回滚。

## 1. 脚本清单

| 脚本 | 用途 | 还原 |
|---|---|---|
| `TreeMaterialAudit` | 运行时审计：地形实际在用哪些原型、每槽 shader/贴图/keyword | 只读 |
| `TreeMaterialFix` | 补齐 Spruce 材质丢失的 albedo/normal/metallic 引用 | `RestoreAll`（`Tools/tree-material-backup/`） |
| `TreeEndingMaterialFix` | 重建丢失的 `PW_Tree_Ending.mat`（复用原 guid） | 删该 .mat |
| `TreeKeywordCleanup` | 清掉空转的 `_PW_SF_COVER_ON` | `RestoreAll`（`Tools/tree-keyword-backup/`） |
| `TreeImpostorSampleBake` | 单棵树 impostor 烘焙验证 | — |
| `TreeImpostorAllBake` | 4 个在用云杉批量烘 impostor + LOD 压成 2 级 | prefab 见 `Tools/tree-prefab-backup/` |
| `TreeImpostorCompare` | 真树 vs impostor 并排渲染对比 | 只读 |
| `TreeDensityReduce` | 树实例数按比例削减（`Apply10` / `Apply5` / `Apply3`） | `RestoreAll`（`Tools/tree-instance-backup/`） |
| `TreeVisibleCount` | 按机位算距离裁剪后的可见树量 | 只读 |
| `TreeMeshStats` | 逐 LOD 的顶点/面数统计 | 只读 |
| `TreeRenderVerify` | 近景玩家视角渲染验收（偏粉检测） | 只读 |
| `TreeDistanceVisualCheck` | 玩家视角远景验收（秃边界检测） | 只读 |
| `TreeRenderProbe` | 诊断树的批次构成：原型 / LOD / 材质 / shader keyword | 只读 |

## 2. 验收相机一律玩家视角

人眼 1.7m、平视、FOV 60°。高空俯瞰看不到真实观感，曾因此误判密度和 LOD 跳变。

## 3. 三条硬约束

细节与日志原文见 [vegetation §6](../../../Docs/vegetation.md)：

1. 材质槽缺失 → 整批树失去 instancing；先 grep 日志再怀疑 keyword。
2. keyword 与 `_PW_ShaderMode` 严格对应；改 keyword 直接改 `.mat` 文本。
3. Terrain 树能吃 impostor：改 prefab 的 LODGroup 即可。

## 4. impostor

- 当前只烘 4 个在用云杉（`Spruce_02/06/04/07_Hero`），其余 41 个原型未烘。
- `TreeImpostorAllBake` 给 prefab 加 `AmplifyImpostor` 组件、烘 octahedron、把 4 级 LOD 压成 2 级（`ReplaceAllExceptFirst`）。
- 产物在 `Assets/*_Impostor.asset` + 同名贴图；shader 是 `Hidden/Amplify Impostors/Octahedron Impostor URP`。
- `TreeMaterialAudit` 的「`_MainTex` 为空」是误报，原因见 [AmplifyImpostors §3 第 4 条](../../AmplifyImpostors/AGENTS.md)。
- 插件细节见 [../../AmplifyImpostors/AGENTS.md](../../AmplifyImpostors/AGENTS.md)。

## 5. 树与草是两条路径

- 树走 Terrain（`drawTreesAndFoliage=1`），由 TerrainRenderer 提交，SRP Batcher 覆盖；没有运行时密度参数。
- 草走 BRG，不受 Terrain 管理。关草见 [../Grass/AGENTS.md](../Grass/AGENTS.md) §2。
- 树的 BRG 化未排期：`TreeInstance` 没有法线，草那套贴坡做法不能直接复用。

## 6. 相关文档

| 文档 | 何时读 |
|---|---|
| [vegetation](../../../Docs/vegetation.md) §3、§6 | **改任何树相关脚本/资产前必读** |
| [gts_srp_batcher](../../../Docs/gts_srp_batcher.md) | 排查批次与 SRP Batcher |
| [rendering_rules](../../../Docs/rendering_rules.md) | 改材质/shader/pass 前 |
