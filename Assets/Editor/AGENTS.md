# Assets/Editor 规约（SECTR + GTS 流水线）

> 默认程序集 `Assembly-CSharp-Editor`（**没有 asmdef**）。
> 改这些脚本前建议一次读完。

## 1. 为什么这个目录不能加 asmdef

SECTR 与 GTS 都没有自己的 asmdef，其编辑器 API（`SECTR_TerrainWindow.SectorizeTerrain`、`SECTR_StreamExport.ExportSceneChunks`、`GTSProfile.ConvertToMesh`）编译进默认的 `Assembly-CSharp-Editor`。

**asmdef 程序集无法引用 `Assembly-CSharp-Editor`**（依赖方向是反的）。所以凡是要调这些 API 的脚本，必须放在这里、走默认程序集。

`SectrPipeline.cs` 是这条结论的原始记录。**不要给 `Assets/Editor/` 加 asmdef**，加了 SECTR/GTS 调用就编不过。

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
| `TerrainChunkTuning.cs` | 批量改/还原 36 块 Terrain 渲染参数（可逆）；`DisableDetail` 单独关草 | `Report` / `ApplyMobile` / `RestoreDesktop` / `DisableDetail` |
| `ChunkRenderStats.cs` | 静态统计 pass 数 / 树 / 草实例量 | `-executeMethod ChunkRenderStats.Run` |
| `GraphicsApiSetup.cs` | Android 图形 API 改为仅 Vulkan、关 Automatic | `-executeMethod GraphicsApiSetup.SetAndroidVulkanOnly` |
| `BatchingSetup.cs` | 查询/关闭平台静态合批（PlayerSettings 的 internal API，走反射） | `Report` / `FixAndroid` / `FixAll` |
| `TerrainDetailProbe.cs` | 逐个 chunk 打印 detail 原型 / 当前 detail 距离与密度 / 各层实例数 | `-executeMethod TerrainDetailProbe.Run` |
| `DetailMeshProbe.cs` | 对比三条路径能否拿到 detail 原型 mesh（决定运行时怎么取） | `-executeMethod DetailMeshProbe.Run` |
| `DetailUvProbe.cs` | 打印 7 个原型的 UV 范围 / subMesh 数 / 图集尺寸（排查贴图采样错位） | `-executeMethod DetailUvProbe.Run` |
| `TreeRenderProbe.cs` | 诊断树的批次构成：原型 / LOD / 材质 / shader keyword | `-executeMethod TreeRenderProbe.Run` |
| `GrassCloseupVerify.cs` | **需真实图形设备**：贴地视角验证草可见 + LOD 分层 + 落截图 | `-executeMethod GrassCloseupVerify.Run` |
| `GrassNativeDataVerify.cs` | **可 `-nographics`**：验原生读盘/排序与托管读取逐实例一致、块表覆盖与类型连续 | `-executeMethod GrassNativeDataVerify.Run` |
| `GrassStreamingVerify.cs` | 空场景验流式增删/重加/渲染/低内存清空（渲染需真实图形设备） | `-executeMethod GrassStreamingVerify.Run` |
| `GrassWorldStreamingVerify.cs` | World 集成：管理器是否流式、SECTR 是否驱动驻留、驻留是否远小于全量 | `-executeMethod GrassWorldStreamingVerify.Run` |
| `PegasusTourSetup.cs` | 布置环绕巡游路线（半径 1100m / 16 POI / 高度烘焙进 POI） | 菜单 `FishDemo/World/② 布置 Pegasus 环绕巡游` |
| `PegasusTourVerify.cs` | 巡游运行时验收：驱动相机 / 环形半径 / SECTR 加载 / 贴地高度 / F 键切换 | `-executeMethod PegasusTourVerify.Run` |
| `GtsConvertOne.cs` | 单块 Terrain→GTS Mesh（**一进程一块**） | `-gtsChunk "<名>" -gtsFirst 0/1` |
| `GtsApplyMeshToChunks.cs` | 把 GTS mesh 挂进 chunk 场景 | `RunAll` / `RunOne -gtsChunk` |
| `GtsFixMeshRendering.cs` | 补 GTSMesh 组件 + 写 worldSizeOverride | `-executeMethod GtsFixMeshRendering.RunAll` |
| `LakeSiteProbe.cs` | **只读**：把真实地形高度采成 32m 网格（`Logs/Lake/height-grid.csv`），选湖址用 | `-executeMethod LakeSiteProbe.Run` |
| `LakeCarve.cs` | 按碗形改 TerrainData 高度图造湖盆；`Restore` 可还原 | `Apply` / `Restore` / `Report` |
| `LakeVegetationClear.cs` | 清湖区内树与草（`.bytes` 直接改写）；`Restore` 可还原 | `Apply` / `Restore` / `Report` |
| `LakeGeometryProbe.cs` | **只读**：同时校验 Terrain 高度与 GTS mesh 顶点，抓「数据改了但没重烘」 | `-executeMethod LakeGeometryProbe.Run` |
| `LakeCrestSetup.cs` | 把 Crest 水面接进主场景（裁剪/水色/波浪/URP 前置）；`Remove` 可撤；`Retune` 只重写水色不动场景 | `Apply` / `Remove` / `Report` / `Retune` |
| `LakeRenderVerify.cs` | **需真实图形设备**：玩家视角截图 + 像素判定，证明湖里有水 | `-executeMethod LakeRenderVerify.Run` |
| `LakeCameraSetup.cs` | 环湖轨道相机 + 切换 rig 写进主场景（幂等）；`Remove` 撤并复原原相机 | `Apply` / `Remove` / `Report` |
| `LakeWaterVerify.cs` | **需真实图形设备**：水面透明 / 水下效果开·关对照 / 水上不误开，6 项像素判定 | `-executeMethod LakeWaterVerify.Run` |
| `LakeOrbitVerify.cs` | 轨道几何（半径 / **地形跟随不穿模** / 朝向 / 切换） + Crest 视点 / SECTR loader / tag 跟随（可 `-nographics`） | `-executeMethod LakeOrbitVerify.Run` |
| `CrestRenderProbe.cs` | **只读**：在 `beginCameraRendering` 读队列，统计 **Crest/自定义 feature** 的 CopyColor/CopyDepth、水面 chunk 数与 MeshType。**看不到 URP 在 `renderer.Setup()` 里入队的 CopyDepthPass**，确认 URP 拷贝要用 Frame Debugger | `-executeMethod CrestRenderProbe.Run` |
| `ChunkLoadScope.cs` | 验证脚本共用：按区域筛选 chunk（36→8），避免全量加载 | 由验证脚本调用 |
| `CrestPerformanceMode.cs` | 切水面性能模式（SimpleTransparency / Quad），三项联动可逆 | `ApplySimpleTransparency` / `ApplyQuad` / `ApplyBoth` / `Restore` / `Report` |
| `AtmosphereSetup.cs` | 把 Enviro 3 接进 World/URP：宏、模块、质量档、RendererFeature、depth；幂等可撤 | `EnsureDefines` / `Apply` / `Remove` / `Report` |
| `AtmosphereVerify.cs` | **需真实图形设备**：Play Mode 验昼夜/天气/体积雾/光，8 张截图 + 状态断言 | `-executeMethod AtmosphereVerify.Run` |
| `AtmosphereTimeVerify.cs` | **可 `-nographics`**：验昼夜控制接口（设时刻 / 平移 / 暂停 / 流速 / 主光响应） | `-executeMethod AtmosphereTimeVerify.Run` |
| `HudPixelVerify.cs` | **需真实图形设备、且不能加 `-batchmode`**（batchmode 下 `OnGUI` 不被调用）：切窄屏抓真图，数 HUD 区域像素，证明文字真的可见 | `-executeMethod HudPixelVerify.Run` |
| `LakesideShoreProbe.cs` | **只读**：扫一圈湖岸高度（r=188..240），找可站立低岸 | `-executeMethod LakesideShoreProbe.Run` |
| `LakesideCrowdSetup.cs` | 湖边人群：采样岸线、创建 `LakesideCrowd.unity` + World SubScene；菜单 ③ | `ApplyOne` / `ApplyThirty` / `ApplyCrowd`（200） |
| `LakesideCrowdVerify.cs` | **需真实图形设备；可 `-batchmode`（离屏 RT 截图）**：Play Mode 验实体数/岸线高度/动画帧变化；预加载 9 chunk，抓图前等 10s | `-executeMethod LakesideCrowdVerify.Run` |

## 3. 铁律

### 3.0 GTS 转换必须一进程一块

`GtsConvertOne.Run` 每次只处理一个 chunk，**不要写循环在单进程里连跑**。

原因：`GTSProfile.CreateTextureArrays()` → `StartProfile()` → `GetGTSComponents()` 会遍历
当前进程内所有场景的 `GTSComponent`。连续 `OpenScene(Single)` 时上一场景组件已被销毁，
但 GTS 内部仍持有引用 → `MissingReferenceException`。实测单进程内刷新引用无效。

首块必须带 `-gtsFirst 1`（负责生成全局纹理数组），其余 `-gtsFirst 0` 复用。
非首块的材质要手动回绑 `_AlbedoArray`/`_NormalArray`，否则走 FallbackError。

### 3.1 切块遍历 Terrain 组件，不要按 root 去重


Gaia 把 9 块地形挂在同一个 `Gaia Terrains` 根下。按 `transform.root` 去重只会得到 1 个**不含 Terrain 组件**的根，导致一块都没切（`SectrPipeline.cs`）。

正确做法：`FindObjectsOfType<Terrain>()` 取快照再逐个处理（`SectorizeTerrain` 结尾会把原地形 `SetActive(false)`，边遍历边改会漏）。

### 3.2 切块必须在植被撒点之后

`SectorizeTerrain` 会把原 TerrainData 的高度/Alpha/Detail/Tree 复制到新 Terrain，并把 sector 范围内的静态子物体收进 sector（`SECTR_TerrainWindow.cs:106` 起）。
切块后再撒点，点会落在已失活的原始地形上。

### 3.3 收尾必须删掉原始地形残留

切块只 `SetActive(false)`，不清 TerrainData。9 块 × 1025² 高度图约 945 万顶点会在加载主场景时全部进内存，与 36 个 chunk 重复，流式就失去意义。

只删 `!activeInHierarchy` 的，避免误删 sector 内容（`WorldFinalize.cs`）。
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
  → TerrainChunkTuning.DisableDetail      （关 Terrain 草，否则与 BRG 草重复绘制）
  → GtsConvertOne.Run（逐块，一进程一块）
  → GtsApplyMeshToChunks.RunAll
  → GtsFixMeshRendering.RunAll
```

`GtsFixMeshRendering` 不只是"补组件"——它写入的 `m_worldSizeOverride`（256m）
决定贴图 tiling，漏掉会让地表贴图密度错 4 倍。详见 [gts_srp_batcher](../../Docs/gts_srp_batcher.md)。

### 3.5.2 中心湖是独立的人工改造，重跑流水线会全部丢失

湖盆不是 Gaia 生成的，是脚本后挖的。**重跑 `WorldPipeline` / `SectrPipeline` 后湖会消失**，
必须按顺序重做：

```
LakeCarve.Apply                 （挖碗形湖盆，改 2 个 chunk 的高度图）
  → LakeVegetationClear.Apply   （清湖区树与草）
  → GtsConvertOne.Run × 受影响 chunk（一进程一块）→ GtsApplyMeshToChunks.RunAll
  → GtsFixMeshRendering.RunAll
  → LakeCrestSetup.Apply        （重建 Crest Lake 节点，保留同名根上的 LakeCameraRig）
  → CrestPerformanceMode.ApplyBoth（仅在性能模式被 Restore 过时需要；Apply 会保留当前模式）
  → LakeGeometryProbe.Run + LakeRenderVerify.Run（校验）
```

湖盆参数的**唯一来源**是 `LakeCarve` 的常量（`Center` / `Radius` / `WaterLevel` / `Depth` / `Blend`）。
改湖先改那里，不要手改 36 个 chunk 的高度图。

> `LakeCrestSetup.Apply` 只保留**已存在**的 `LakeCameraRig`，不会新建相机节点。
> 场景从零重建（没有 `Lake Orbit Camera` / rig）时，仍需跑 `LakeCameraSetup.Apply`。
> 静态合批由 `BatchingSetup.FixAll` 关闭（Android/Standalone 0/0）；`GtsApplyMeshToChunks` 已改为
> `go.isStatic = false`，重跑流水线不会再把 subTile 标成 static。
> 接了大气候，`LakeCrestSetup.Apply` 的 `HasOtherDepthOrOpaqueConsumers` 会检测到 Enviro，
> **保持 `m_RequireDepthTexture=1`**、opaque 仍为 0；不要手动把 depth 改回 0。

`LakeCarve` 的溢出判据按**矩形 AABB** 而非圆形：水面由 Crest WaterBody 的矩形范围决定，
AABB 外的地形再低也不会露出水面。详见 [water](../../Docs/water.md) §2。

### 3.5.3 大气（Enviro）接入也会被重跑覆盖

重跑 `WorldPipeline` / `SectrPipeline` 后，`Enviro 3` 与 `Atmosphere` 根节点会消失，
旧 `Directional Light` 的 `m_Enabled=0` 也会被重置。按顺序重做：

```
AtmosphereSetup.EnsureDefines   （干净导入/换机时一次；加 ENVIRO_URP 后需重新编译）
  → AtmosphereSetup.Apply       （实例化 Enviro、LoadConfiguration、质量档、RendererFeature、depth=1）
  → AtmosphereSetup.Report      （确认只有 1 盏方向光、旧主光禁用、feature 已挂）
  → AtmosphereVerify.Run        （需真实图形设备）
```

三个必须记住的坑（详见 [atmosphere](../../Docs/atmosphere.md) §0.4）：
1. `ENVIRO_URP` 宏不是自动加的，缺了天空/雾静默失效；
2. `Apply` 保存场景前必须 `EnviroManager.LoadConfiguration()`，否则 Play Mode 下模块全 null；
3. Enviro 雾 pass 的 `renderPassEvent` 被改到 `AfterRenderingTransparents`（Crest 水面），升级 Enviro 会丢。

### 3.5.1 关草用 `DisableDetail`，不要动 `drawTreesAndFoliage`

草已迁到 BRG，Terrain 自带的 detail 渲染现在是**重复绘制**（同一块地表画两遍草，
且 detail 各原型是独立 draw call，无法与 BRG 合批）。36 个 chunk 已用
`TerrainChunkTuning.DisableDetail` 设成 `DetailObjectDistance=0` / `DetailObjectDensity=0`。

- `detailObjectDistance = 0` 是 Unity 文档规定的「关闭 detail 渲染」方式，**不改 TerrainData 里的撒点数据**，随时可 `RestoreDesktop` 回滚。
- **不要用 `drawTreesAndFoliage = false`**：它同时管树和草，会把 46,472 棵树一起关掉。
- 代价：CloverFlower / Daisy / Dandelions 这些花也是 detail 原型，一并消失。
  要保留花，应把花也做成 BRG 实例，而不是恢复 detail 渲染。

重跑 `SectrPipeline` 后 detail 参数会回到 Gaia Desktop 预设，必须再跑一次 `DisableDetail`。

### 3.6 数量断言硬编码了 36

`WorldFinalize.cs`、`FinalIntegrity.cs`、`RichWorldVerify.cs` 三处把 chunk/sector 数量写死为 36。

改世界尺寸或切块粒度时，这三处**必须同步改**，否则验证会误报 FAIL。
（`StreamingVerify.cs` 用的是 `sectors.Length`，不写死，无需改。）

### 3.7 Exit code 语义

| 值 | 含义 |
|---|---|
| 0 | 成功 / PASS |
| 1 | 前置条件不满足或异常中止 |
| 2 | 校验不通过（数量不符、边界为空、场景打不开等） |

批处理命令行**不要加 `-quit`**，由脚本自己 `EditorApplication.Exit`。

### 3.8 重复执行要幂等

这些脚本会被反复重跑。新增逻辑时：

- 挂组件前先清掉已有的（`WorldFinalize.cs` 的做法）；
- 重建 Build Settings 时整体覆盖而不是追加（`FinalIntegrity.cs`）；
- 清理资产时按**当前场景实际引用的批次**判断，不要盲删目录（`CleanupOrphanAssets.cs`）。

### 3.9 诊断脚本只写文件、不逐条打 Console

`TreeRenderProbe` 这类逐原型诊断会产生几千行输出。**batchmode 下逐条 `Debug.Log`
会触发 Unity 自己的日志缓冲崩溃**（实测 `EditorMonoConsole::LogToConsoleImplementation`
处 `RaiseException`）。做法：明细全写文件，Console 只打汇总
（`TreeRenderProbe.cs` 的 `W` / `C` 两个委托）。

### 3.10 验证脚本的三条提速规则（2026-11-13，实测 9m50s → 1m02s）

**① 只加载与目标区域相交的 chunk**，用 `ChunkLoadScope.LoadRelevant(ChunkLoadScope.Area.FromLake())`。
原先三个验证脚本都 `OpenScene` 全部 36 个 chunk。按湖 AABB 筛选后只需 **8~9 个**。
`ChunkLoadScope` 的定位方式：读场景 YAML 里**所有非零 `m_LocalPosition` 取 x/z 最小值**
作为 chunk 世界原点（每个 chunk 场景里有正负对称两组位置，最小值恒为地形）。
**不要用文件名网格坐标**：本工程文件名里 `Terrain_X_Y` 是 1024m 大区块、
`a-b-0` 是 512m 子块，直接取末尾数字只有 4 种重复值。

**② 结束时不要先 `EditorApplication.isPlaying = false`**，直接 `EditorApplication.Exit`。
这是最大的一笔：退出 Play Mode 会触发 `StopAssetImportingV2(ForceSynchronousImport |
ForceDomainReload)`，实测 **20s 资产重导入 + 10s 备份场景落盘**。
本进程紧接着就退出，Play Mode 状态不会保留到下次运行。
（`QuitAfter` 的等待帧数也从 30 降到 3。）

**③ 截图预热帧数按渲染模式定**。`LakeWaterVerify` 原为每张 40 帧 × 4 张。
切到 SimpleTransparency 后 shader 不再采样仿真纹理、也没有 copy pass，
**8 帧足够**（`WarmupFrames` 常量）。若关掉 SimpleTransparency 要调回 40。

提速后实测：`LakeWaterVerify` 62s、`CrestRenderProbe` 62s、`LakeOrbitVerify` 38s。
**2026-11-17 起 `LakeOrbitVerify` 会预加载湖区 9 个 chunk 并跑完整圈（120 帧固定步长 1s）**，
用于验证地形跟随不穿模，耗时约 60~70s；不再扫 18° 就算通过。
其中脚本内实际只有 ~17s（打开 World 7s + 加载 chunk 9.5s + 4 张截图 1.9s），
其余是 Unity 启动与脚本编译 —— 属环境固有开销，改脚本无收益。

### 3.11 新增 pass / RendererFeature 的验收（2026-11-17）

任何新增 pass 或后处理都必须按 [rendering_rules](../../Docs/rendering_rules.md) §1 交性能账：

- `CrestRenderProbe` 只能数 Crest/自定义 feature 的 pass；URP 内部 `CopyDepthPass` 要用 Frame Debugger；
- 确认没有在已有纹理上再 `GetTemporary(cameraTargetDescriptor)` 做 ping-pong；
- 若确需打开 `m_RequireDepthTexture` / `m_RequireOpaqueTexture`，写明消费者与
  为什么不能复用已有 depth/opaque；
- 验收脚本输出 pass 数，不只截图。

### 3.12 需要真实图形设备的验证不能加 `-nographics`

BRG 的剔除回调在 `-nographics` 下不执行，`LastVisibleCount` 恒为 `-1`，
「草没被画出来」和「草被正确剔除」无法区分。

| 脚本 | 可否 `-nographics` |
|---|---|
| `PegasusTourVerify` | 可以（只验相机位移/半径/chunk 加载，不验像素） |
| `GrassCloseupVerify` | **不行**，必须真实图形设备 |
| `GrassMultiChunkVerify` / `GrassPerfProbe` / `GrassM*Verify` | **不行** |

而且**批处理前必须关闭 Unity 编辑器**，否则报
`HandleProjectAlreadyOpenInAnotherInstance` 并崩溃（这是另一进程占用项目锁，不是脚本问题）。

## 4. 相关文档

| 文档 | 何时读 |
|---|---|
| [gts_srp_batcher](../../Docs/gts_srp_batcher.md) | 改 GTS 转换脚本、排查 mesh 地形合批/视觉不一致 |
| [grass_brg_progress](../../Docs/grass_brg_progress.md) | 改草烘焙/验证脚本、排查草消失 |
| [vegetation](../../Docs/vegetation.md) §6 | **改任何树相关的脚本/资产前必读**：树的表现、批次、密度、impostor |
| [world_implementation](../../Docs/world_implementation.md) | 要重跑流水线、理解 chunk 结构、查已知缺陷 |
| [terrain_streaming](../../Docs/terrain_streaming.md) | 评估 SECTR 的定位与限制 |
| [pooling_lod](../../Docs/pooling_lod.md) | 设计 chunk 加载/卸载策略 |
| [../Scripts/Editor/AGENTS.md](../Scripts/Editor/AGENTS.md) | 另一半（Gaia）流水线 |

## 5. 树相关脚本（2026-11-12 新增）

改树之前先看 [vegetation](../../Docs/vegetation.md) §6。这批脚本按用途分四组，
**全部有备份与还原入口**，出问题按表中「还原」列回滚。

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

**验收相机一律玩家视角**（人眼 1.7m、平视、FOV 60°）。高空俯瞰看不到真实观感，
曾因此误判密度和 LOD 跳变。

**两个环境坑**：
- `Temp/UnityLockfile` 在 Unity 崩溃/被 kill 后不会自动清理，会挡住后续批处理，需手动删；
- batchmode 下 Profiler 的 `Batches Count` / `Draw Calls Count` **恒为 0**，
  draw call 数只能在编辑器 Frame Debugger 或真机上看。
