# Assets/Editor/Grass 规约（草 BRG 验收 + detail 原型探针）

> 默认程序集 `Assembly-CSharp-Editor`，无 asmdef。
> 草的运行时与烘焙工具在 `Assets/Scripts/`，本目录只放**编辑器期验证/探针**。

## 1. 脚本清单

| 脚本 | 职责 | 入口 |
|---|---|---|
| `GrassCloseupVerify.cs` | **需真实图形设备**：贴地视角验证草可见 + LOD 分层 + 落截图 | `-executeMethod GrassCloseupVerify.Run` |
| `GrassFlowerVerify.cs` | **需真实图形设备**：验证花在 LOD0 下可见，输出 `Logs/grass-flower-lod.png` / `-nolod.png` | `-executeMethod GrassFlowerVerify.Run` |
| `GrassNativeDataVerify.cs` | **可 `-nographics`**：验原生读盘/排序与托管读取逐实例一致、块表覆盖与类型连续 | `-executeMethod GrassNativeDataVerify.Run` |
| `GrassStreamingVerify.cs` | 空场景验流式增删/重加/渲染/低内存清空（渲染需真实图形设备） | `-executeMethod GrassStreamingVerify.Run` |
| `GrassWorldStreamingVerify.cs` | World 集成：管理器是否流式、SECTR 是否驱动驻留、驻留是否远小于全量 | `-executeMethod GrassWorldStreamingVerify.Run` |
| `TerrainDetailProbe.cs` | 逐个 chunk 打印 detail 原型 / 当前 detail 距离与密度 / 各层实例数 | `-executeMethod TerrainDetailProbe.Run` |
| `DetailMeshProbe.cs` | 对比三条路径能否拿到 detail 原型 mesh（决定运行时怎么取） | `-executeMethod DetailMeshProbe.Run` |
| `DetailUvProbe.cs` | 打印 7 个原型的 UV 范围 / subMesh 数 / 图集尺寸（排查贴图采样错位） | `-executeMethod DetailUvProbe.Run` |

## 2. 关草只能用 `DisableDetail`

草已迁到 BRG，Terrain 自带的 detail 渲染现在是**重复绘制**（同一块地表画两遍草，且 detail 各原型是独立 draw call，无法与 BRG 合批）。36 个 chunk 已用 `TerrainChunkTuning.DisableDetail` 设成 `DetailObjectDistance=0` / `DetailObjectDensity=0`。

- `detailObjectDistance = 0` 是 Unity 文档规定的「关闭 detail 渲染」方式，**不改 TerrainData 里的撒点数据**，随时可 `RestoreDesktop` 回滚。
- **不要用 `drawTreesAndFoliage = false`**：它同时管树和草，会把 46,472 棵树一起关掉。
- 代价：CloverFlower / Daisy / Dandelions 这些花也是 detail 原型，一并消失。要保留花，应把花也做成 BRG 实例，而不是恢复 detail 渲染。
- 重跑 `SectrPipeline` 后 detail 参数会回到 Gaia Desktop 预设，必须再跑一次 `DisableDetail`。

## 3. detail 原型探针的用途

`TerrainDetailProbe` / `DetailMeshProbe` / `DetailUvProbe` 只读，用来回答三件事：

1. 每个 chunk 的 detail 原型顺序、`detailObjectDistance` / `density`、各层实例数；
2. 运行时能否拿到原型 mesh（实测 0 命中；根因与替代见 [grass_brg_progress](../../../Docs/grass_brg_progress.md) §5 第 11 条）；
3. 原型 UV 与图集是否对应（贴图错位时先跑 `DetailUvProbe`，不要猜）。

图集映射与烘焙约定见 [grass_brg_progress](../../../Docs/grass_brg_progress.md) §2。

## 4. 相关文档

| 文档 | 何时读 |
|---|---|
| [grass_brg_progress](../../../Docs/grass_brg_progress.md) | **改任何草代码前**：落地状态、实测、踩坑、复现命令 |
| [grass_brg_plan](../../../Docs/grass_brg_plan.md) | 查 Vulkan 约束、采样率与烘焙规模 |
| [rendering_rules](../../../Docs/rendering_rules.md) | 改 BRG / shader / pass 前 |
| [../World/AGENTS.md](../World/AGENTS.md) | 重跑流水线后恢复 chunk 参数 |
