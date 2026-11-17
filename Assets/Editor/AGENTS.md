# Assets/Editor 规约（SECTR / GTS / 水 / 大气 / 验证脚本）

> 默认程序集 `Assembly-CSharp-Editor`（**没有 asmdef**）。
> 脚本按模块分目录，**进入子目录写代码前先读该目录的 AGENTS.md**；本文件只放跨模块约束。

## 1. 为什么这个目录不能加 asmdef

SECTR/GTS 没有 asmdef，其编辑器 API（`SECTR_TerrainWindow.SectorizeTerrain`、`SECTR_StreamExport.ExportSceneChunks`、`GTSProfile.ConvertToMesh`）编进默认的 `Assembly-CSharp-Editor`；asmdef 程序集引用不到它，所以调用这些 API 的脚本必须留在这里。`World/SectrPipeline.cs` 是原始记录，**不要加 asmdef**。

代价：拿不到 `FishDemo.Editor` 的引用；复用 Gaia 工具时用 `-executeMethod` 分两步跑。运行时脚本不能放这里，见 [Docs/AGENTS.md](../../Docs/AGENTS.md) §3。

## 2. 目录索引

| 目录 | 内容 | 何时读 |
|---|---|---|
| [Common](Common/AGENTS.md) | `ChunkLoadScope`：按区域筛选 chunk | 写验证脚本、要避免全量加载 36 块时 |
| [World](World/AGENTS.md) | SECTR 切块/导出/收尾、chunk 参数、合批与图形 API、Pegasus 巡游 | 重跑世界流水线、改 chunk 渲染参数、调巡游路线 |
| [Gts](Gts/AGENTS.md) | Terrain→Mesh 转换、挂载、ASTC 压缩 | 重烘 GTS、排查地表合批/贴图密度 |
| [Grass](Grass/AGENTS.md) | 草 BRG 验收、detail 原型探针 | 排查草消失/花不可见/流式驻留 |
| [Vegetation](Vegetation/AGENTS.md) | 树材质、LOD、impostor、密度 | **改任何树脚本/资产前必读** |
| [Water](Water/AGENTS.md) | 中心湖、Crest 水面、环湖相机 | 改湖、改水面、水下效果 |
| [Atmosphere](Atmosphere/AGENTS.md) | Enviro 接入、昼夜/天气验收、HUD | 改大气、重跑世界后恢复 Enviro |
| [Crowd](Crowd/AGENTS.md) | 湖边人群布置与验收 | 改人群规模/岸线/机位 |

## 3. 通用铁律

### 3.1 Exit code 语义

| 值 | 含义 |
|---|---|
| 0 | 成功 / PASS |
| 1 | 前置条件不满足或异常中止 |
| 2 | 校验不通过（数量不符、边界为空、场景打不开等） |

批处理命令行**不要加 `-quit`**，由脚本自己 `EditorApplication.Exit`。

### 3.2 重复执行要幂等

这些脚本会被反复重跑。新增逻辑时：

- 挂组件前先清掉已有的（`World/WorldFinalize.cs` 的做法）；
- 重建 Build Settings 时整体覆盖而不是追加（`World/FinalIntegrity.cs`）；
- 清理资产时按**当前场景实际引用的批次**判断，不要盲删目录（`World/CleanupOrphanAssets.cs`）。

### 3.3 诊断脚本只写文件、不逐条打 Console

逐原型诊断会产生几千行输出。**batchmode 下逐条 `Debug.Log` 会触发 Unity 自己的日志缓冲崩溃**（实测 `EditorMonoConsole::LogToConsoleImplementation` 处 `RaiseException`）。做法：明细全写文件，Console 只打汇总。

### 3.4 验证脚本的三条提速规则

**① 只加载与目标区域相交的 chunk**，用 `ChunkLoadScope.LoadRelevant(...)`，不要 `OpenScene` 全部 36 个；数量与定位见 [Common](Common/AGENTS.md)。

**② 结束时直接 `EditorApplication.Exit`**，不要先 `isPlaying = false`。退出 Play Mode 会触发同步资产重导入与域重载，而进程紧接着就退出。

**③ 截图预热帧数按渲染模式定**：SimpleTransparency 下 8 帧足够，关闭后调回 40；具体见 [Water §5](Water/AGENTS.md)。

### 3.5 新增 pass / RendererFeature 的验收

按 [rendering_rules](../../Docs/rendering_rules.md) §1 交性能账。本目录只额外记一条：`Water/CrestRenderProbe` 只能数 Crest/自定义 feature 的 pass；URP 内部 `CopyDepthPass` 要用 Frame Debugger。

### 3.6 需要真实图形设备的验证不能加 `-nographics`

BRG 的剔除回调在 `-nographics` 下不执行，`LastVisibleCount` 恒为 `-1`，「草没被画出来」和「草被正确剔除」无法区分。

| 脚本 | 可否 `-nographics` |
|---|---|
| `World/PegasusTourVerify` | 可以（只验相机位移/半径/chunk 加载，不验像素） |
| `Grass/GrassCloseupVerify` / `GrassFlowerVerify` | **不行**，必须真实图形设备 |
| `FishDemo.EditorTools` 里的 `GrassMultiChunkVerify` / `GrassPerfProbe` / `GrassM*Verify` | **不行** |

而且**批处理前必须关闭 Unity 编辑器**，否则报 `HandleProjectAlreadyOpenInAnotherInstance` 并崩溃（项目锁被另一进程占用，不是脚本问题）。

### 3.7 两个环境坑

- `Temp/UnityLockfile` 在 Unity 崩溃/被 kill 后不会自动清理，会挡住后续批处理，需手动删；
- batchmode 下 Profiler 的 `Batches Count` / `Draw Calls Count` **恒为 0**，draw call 数只能在编辑器 Frame Debugger 或真机上看。

## 4. 相关文档

| 文档 | 何时读 |
|---|---|
| [rendering_rules](../../Docs/rendering_rules.md) | 写任何渲染代码前 |
| [world_implementation](../../Docs/world_implementation.md) | 重跑流水线、理解 chunk 结构 |
| [gts_srp_batcher](../../Docs/gts_srp_batcher.md) | 改 GTS 转换脚本、排查地表合批 |
| [grass_brg_progress](../../Docs/grass_brg_progress.md) | 改草烘焙/验证脚本 |
| [vegetation](../../Docs/vegetation.md) §6 | 改树相关脚本/资产前 |
| [water](../../Docs/water.md) | 改湖/水面/水下前 |
| [atmosphere](../../Docs/atmosphere.md) §0 | 改大气、重跑流水线后恢复 Enviro |
| [../Scripts/Editor/AGENTS.md](../Scripts/Editor/AGENTS.md) | 另一半（Gaia）流水线 |
