# World 场景实现分析

> **本文件 394 行，是查阅型参考，不要通读。**
> 只在以下情况打开：要重跑世界生成、排查 chunk 加载不出来、要改世界尺寸/海平面、要接 YooAsset 替换 SECTR、或核对"这个场景到底是怎么来的"。
>
> 结论分三类标注：**代码事实**（file:line）、**产物事实**（解析 `World.unity` / 资产文件得到）、**推断**（注明依据）。
> 本文只描述**已实现**的东西。选型层面的"应该用什么"见 [terrain_streaming](terrain_streaming.md)。

## 0. 一分钟速览

| 项 | 值 | 依据 |
|---|---|---|
| 场景文件 | `Assets/Scenes/World.unity`（80719 行 / 2.1MB） | 产物事实 |
| 世界规模 | 3×3 tile × 1024m = 3072m × 3072m | `WorldPipeline.cs:38-40` |
| 地形高度 | 700m，海平面 **60**（代码）/ **85**（session） | 见 §6.1 冲突 |
| 分块 | 每 tile 2×2 → 9×4 = **36 个 chunk**，每块 512m | `WorldPipeline.cs:43`、`SectrPipeline.cs:28` |
| **地表渲染形态** | **GTS Mesh**（每 chunk 4 个 256m subTile），Terrain 只留碰撞，`drawHeightmap=false` | 见 §4.1 |
| 流式实现 | SECTR `SECTR_RegionLoader` + `SECTR_Chunk`（`SceneManager.LoadSceneAsync`） | `SECTR_Chunk.cs:238` |
| 资源入口 | **绕过 YooAsset**（SECTR 直接按场景路径加载） | 同上一行 |
| 验证状态 | `Logs/Gaia/final-integrity.txt` = PASS | 工具输出 |

## 1. 为什么是一个"生成流水线"而不是手搭场景

**代码事实**：World.unity 里的地形、chunk、撒点物全部由 `-executeMethod` 批处理生成，脚本在 `Assets/Scripts/Editor/`（Gaia 工具链）与 `Assets/Editor/`（SECTR 工具链）。

由此产生一条硬约束：

> **World.unity 与 `Assets/Scenes/World/` 下的 chunk、TerrainSplits 都是产物，不要手改。**
> 重跑流水线会覆盖它们；手改的部分会丢。要改结果，改脚本或改参数后重跑。

**为什么脚本分两处**（这是最容易踩的编译问题）：

| 目录 | 程序集 | 能引用什么 | 原因 |
|---|---|---|---|
| `Assets/Scripts/Editor/` | `FishDemo.Editor`（asmdef） | `GaiaCore`、`GaiaEditor`、`YooAsset.Editor` | Gaia 有 asmdef |
| `Assets/Editor/` | `Assembly-CSharp-Editor`（默认） | 一切 + SECTR | **SECTR 没有 asmdef**，其 `SECTR_TerrainWindow` / `SECTR_StreamExport` 编进默认程序集；asmdef 程序集引用不到它 |

`SectrPipeline.cs:11-15` 的注释就是这个结论的原始记录。**把 SECTR 调用写进 `Assets/Scripts/Editor/` 会编译失败。**

## 2. 五阶段流水线

```
① WorldPipeline        建场景 + 9 tile 地形 + 放水      (FishDemo.Editor)
② TerrainStamper       9 张印章平铺塑形                  (默认程序集，被①调用)
③ BiomeSpawner         Biome 撒点：地表材质/树/草/石头   (FishDemo.Editor，被①调用)
④ SectrPipeline        2×2 切块 + 导出 36 chunk 场景      (默认程序集)
⑤ WorldFinalize        删残留地形 + 挂 RegionLoader       (默认程序集)
   然后 FinalIntegrity  重建 Build Settings + 全量校验
```

### 2.1 ① 世界创建（`WorldPipeline.cs`）

- 参数在 `WorldPipeline.cs:38-43`：`TilesX/Z=3`、`TileSize=1024`、`TileHeight=700`、`SeaLevel=60`、`SectorsPerSide=2`。
- `PrepareScene`（`:100-121`）：新建空场景 → 相机 `(0, 420, -1228.8)` 俯视 25° → 平行光 → 存 `World.unity`。
- `StartWorldCreation`（`:123-166`）：
  - `m_targetSizePreset=Custom`，`m_qualityPreset=Desktop`；
  - **`m_createInScene=false`（`:135`）**：关掉 Gaia 自带的 TerrainLoader 场景流式，避免与 SECTR 双重加载；
  - `GaiaDefaults`：heightmap 1025 / control 1024 / basemap 1024 / detail 1024，`m_terrainSize=1024`、`m_terrainHeight=700`（`:143-149`）。
- **状态机而非同步等待**（`:15-21` 注释）：`CreateOrUpdateWorld` 把协程挂到 `EditorApplication.update`；在 `-executeMethod` 里 while 等待会占死主线程、协程永不前进（实测死等）。所以入口立即返回，由 `Tick`（`:176-242`）分帧推进，完成时显式 `EditorApplication.Exit`。
  - **命令行不要加 `-quit`**，否则进程会在流水线跑完前退出。
  - 单阶段超时 40 分钟（`:58`）。

### 2.2 ② 地貌塑形（`TerrainStamper.cs`）

- `CreateOrUpdateWorld` **只建空白网格，高度恒为 0**；必须显式调 `GaiaSessionManager.Stamp`（`GaiaSessionManager.cs:2038`）才有起伏。
- 9 张官方 exr 印章 3×3 平铺，`StampSize=1400m` 覆盖 3072m 世界（重叠约 20%），`TerrainStamper.cs:28-30`。
- **关键换算**（`:32-41`、`:144-147`）：`StamperSettings.m_height` 不是米，是 0..50 的垂直缩放系数：
  ```
  scaleY = 50 × 目标米数 / terrainData.size.y
  ```
  早期误填 260 导致纹理被压扁、起伏只剩 0.0085（`:36-39` 注释记录了这次实测）。
- 成功判据：抽样 `max-min > 0.01`（归一化），`SampleRelief`（`:166-177`）。

### 2.3 ③ Biome 撒点（`BiomeSpawner.cs`）

照抄 `BiomeControllerEditor` 的 GUI 流程：`CreateBiome(true)` → `UpdateSpawnerList()` → `Spawner.HandleAutoSpawnerStack(...)` → 轮询 `m_spawnComplete`（`:43-111`）。

- 预设：`Gaia Pro Assets and Biomes/Biomes/Alpine Meadow.asset`（`:34-35`）。
- 撒点是异步协程，必须分帧等（`TickBiomeSpawn`，`:97-111`）。

**⚠ 这个脚本会写第三方资产**（`FixTreeHeightMasksOnPreset`，`:151-212`）：

- 预设里树规则的 HeightMask 原本按"世界高 115m / 海平面 25m"标定，与本工程（700m / 60m）错位，导致 **0 棵树**（`:132-140` 有实测对照：清空掩码单块地形 217558 棵）。
- 修法：把 `HeightMaskType` 改为 `WorldSpace` + `Meter`，区间 `[海平面+2, 地形高×0.95]` = `[62, 665]`。
- **副作用**：修改直接落在 `Alpine Meadow/Alpine Meadow-Trees.asset` 上，是持久化的第三方资产改动。已核实该文件当前 `m_heightMaskType: 0`、`m_absoluteHeightMin: 62`、`Max: 665`（产物事实）。换预设或重装 Gaia 会丢，需重跑。
- 只改 TerrainTree（树/灌木/蕨），草（TerrainDetail）与石头（GameObject）不动（`:148-150`）。

### 2.4 ④ 切块与导出（`Assets/Editor/SectrPipeline.cs`）

- 遍历 `FindObjectsOfType<Terrain>()`，**不要按 transform.root 去重**（`:39-43`）：Gaia 把 9 块地形挂在同一 `Gaia Terrains` 根下，按 root 去重只会得到 1 个不含 Terrain 的根，导致一块都没切。
- 每块调 `SECTR_TerrainWindow.SectorizeTerrain(terrain, 2, 2, 1, splitTerrain:true, createPortalGeo:false, includeStatic:true, includeDynamic:false, sortGaiaObjects:true)`（`:59-68`；方法定义在 `SECTR_TerrainWindow.cs:106`）。
  - `includeDynamic:false` → 水面不进 chunk（全局平面分块会产生接缝）。
  - `sortGaiaObjects:true` → 撒点物按位置归入各自 sector。
- 校验 Sector 数 = 9×2×2 = 36（`:71-78`），然后 `SECTR_StreamExport.ExportSceneChunks()`（`:87`，定义 `SECTR_StreamExport.cs:945`）把每个 sector 导出成独立 `.unity`。
- 校验"已冻结（已导出）"的 Sector 数 = 36（`:90-96`）。

**工序顺序不可颠倒**（`WorldPipeline.cs:23-29`）：地形 → 植被 → 水 → 切块 → 导出。
因为 `SectorizeTerrain` 会把原 TerrainData 的高度/Alpha/Detail/Tree 按块复制，并在结尾把原地形 `SetActive(false)`；切块后再撒点会撒到失活地形上。

### 2.5 ⑤ 收尾（`Assets/Editor/WorldFinalize.cs`）

两件事：

1. **删原始地形残留**（`:37-53`）：切块只 `SetActive(false)`，不清 TerrainData。9 块 × 1025² 高度图约 945 万顶点，会在加载主场景时全部进内存，与 36 个 chunk 重复。只删 `!activeInHierarchy` 的，避免误删 sector 内容。
2. **挂 `SECTR_RegionLoader`**（`:71-81`）：挂到主相机上，`LoadSize=(768,1000,768)`、`UnloadBuffer=0.25`、`LayersToLoad=~0`。
   - 768 ≈ 1.5 块（512m），常驻约 3×3 chunk。
   - `UnloadBuffer=0.25` → 离开 25% 才卸载，防边界抖动。

校验项（`:84-110`）：残留 Terrain=0、Chunk=36、Sector=36、无零边界 Sector；否则 exit 2。

## 3. World.unity 实际构成

**产物事实**（解析 YAML 得到）。共 14 个根对象：

| 根对象 | 内容 |
|---|---|
| `Main Camera` | Camera + AudioListener + **`SECTR_RegionLoader`**（LoadSize 768/1000/768，UnloadBuffer 0.25） |
| `Directional Light` | Light + URP `UniversalAdditionalLightData` |
| `Gaia Water` | PrefabInstance → `Gaia Water/Water Prefabs/URP/Gaia Water.prefab`，scale 0.215，位置 `(0, 60, 0)` |
| `Terrain_0_0` … `Terrain_2_2`（9 个） | **只有 Sector×4 + Portal×4，无 Terrain 组件**（`!u!218` 计数 = 0） |
| `Gaia Terrains` | 空容器（`GaiaSceneReadMe`） |
| `Gaia Runtime` | `Terrain Loader Manager` + `Gaia Lighting` |
| `Gaia Tools` | `Session Manager` + `Alpine Meadow Biome` |

另有 10 个 `Alpine Meadow-*` 撒点器对象（Textures/Trees/Grass/Stone/Bushes/Fern/Mushrooms/VFX/Audio/Tree Leaves Texture），多数位于 `(768, 0, 768)`，且**每个都挂着 Gaia `TerrainLoader` 组件**。

**推断（依据）**：这 10 个 `TerrainLoader` 是 `CreateBiome` 自动挂的，属 Gaia 自带多地形加载体系；本工程 `m_createInScene=false` 已关闭该体系，且主场景已无 Terrain，因此它们大概率是**空转的残留组件**。未做运行时实测验证。

## 4. chunk 内部结构

单个 chunk（如 `World_SECTR Terrain_0_0-20261109 - 162007 0-0-0 Sector.unity`）：

```
<场景名>                        (场景根)
└── SECTR Terrain_..._Sector_Chunk   ← 挂 SECTR_ChunkRef / SECTR_Member，pos=(-1536,0,-1536)
    ├── Terrain_..._Terrain          ← Terrain + TerrainCollider（drawHeightmap=false，只留碰撞）
    │   └── GTS_Mesh                 ← 地表渲染（2026-11-09 新增）
    │       ├── SubTile_00           ← MeshFilter + MeshRenderer + GTSMesh + GtsRuntimeDataBootstrap
    │       ├── SubTile_10
    │       ├── SubTile_20
    │       └── SubTile_30
    └── Gaia_Spawns                  ← 4 个子物体（撒点物）
```

Terrain 仍保留的关键参数（草/树/碰撞仍由它负责）：

| 字段 | 值 | 影响 |
|---|---|---|
| `m_DrawHeightmap` | **0** | 地表不再由 TerrainRenderer 绘制，改由 GTS mesh |
| `m_DrawInstanced` | **1** | 草/细节走 instanced（§6.6 调优后） |
| `m_DetailObjectDistance` | 70 | 草可见距离（§6.6 调优后） |
| `m_TreeDistance` | 600 | 树可见距离（§6.6 调优后） |
| `m_TreeBillboardDistance` | 60 | 树切 billboard 距离（§6.6 调优后） |
| `m_HeightmapPixelError` | 10 | |
| `m_SplatMapDistance` | 512 | |
| `m_TerrainData` | `World/TerrainSplits/Terrain_..._0-0-0.asset`（≈4.68MB） | 与主场景独立 |
| TerrainData 内容 | 8 层 splat、13 树原型、7 细节原型、约 1.26 万树/块 | `Logs/Gaia/proto-materials.txt`、`rich-verify.txt` |

### 4.1 地表已切到 GTS Mesh（2026-11-09）

**产物事实**：36 个 chunk 的地表全部改为 mesh 渲染，Terrain 退化为碰撞/植被载体。

| 项 | 数值 |
|---|---|
| subTile 数 | 36 chunk × 4 = **144 个** |
| 每 subTile 尺寸 | 256m × 256m（chunk 512m 再切 2×2） |
| 每 subTile 顶点/三角 | 16641 / 32768（`saveResolution=Half`，513→257 边长） |
| 三角总量 | 4,718,592（原 Terrain 1887 万的 **25%**） |
| 常驻 4 块估算 | ≈52.4 万三角 |
| GTS 资产体积 | **298 MB**（mesh 273.8MB + png 180 个 + mat 36 个） |
| 材质 | 36 个（每 chunk 一个），共用 shader `PW/GTS_URP_Compiled` |
| 每个 subTile 组件 | `MeshFilter` + `MeshRenderer` + `GTSMesh` + `GtsRuntimeDataBootstrap` |

**为什么每 chunk 一个材质**：`_Control0/_Control1/_SplatmapIndex/_WorldNormalMap` 是 per-chunk
的 splat 数据，绑在材质上，因此材质无法跨 chunk 共享。**SRP Batcher 仍能合批**——
它按 CBUFFER 布局归并，不按材质对象归并（详见 [gts_srp_batcher](gts_srp_batcher.md)）。

**渲染分工**（三者解耦，可单独回退）：

| 内容 | 谁负责 |
|---|---|
| 地表外观（高度图 + splat 混合） | GTS mesh + `PW/GTS_URP_Compiled` |
| 树 / 草 | Terrain（`drawTreesAndFoliage` 仍为 true） |
| 碰撞 | `TerrainCollider` |

生成链路与坑见 [gts_srp_batcher](gts_srp_batcher.md)（含 shader 改造、数据注入、worldSize 校正）。

## 5. 流式加载机制

**代码事实**：

1. `SECTR_RegionLoader.Update()`（`SECTR_RegionLoader.cs:82-89`）每帧：
   - 以自身 `transform.position` 为中心，用 `LoadSize` / `LoadSize×(1+UnloadBuffer)` 构造两个 AABB；
   - 调 `SECTR_Sector.GetContaining(ref list, bounds)`（`SECTR_Sector.cs:81-93`）——遍历所有 sector，`TotalBounds.Intersects(bounds)` 命中即入列。
2. 新进入 load 范围的 sector → `SECTR_Chunk.AddReference()`（`SECTR_Chunk.cs:73`）；离开 unload 范围 → `RemoveReference()`（`:88`）。
3. 引用计数到 0 时加载/卸载：
   - 加载：`SceneManager.LoadSceneAsync(ScenePath, LoadSceneMode.Additive)`（`:238`），并进 `activationQueue` 节流激活；
   - 卸载：`SceneManager.UnloadSceneAsync(ScenePath)`（`:285`）。

**因此**：
- 加载中心 = 挂 `RegionLoader` 的物体（当前是主相机）。玩家移动 = 相机移动，无需额外代码。
- **chunk 加载完全绕过 YooAsset**，走 Unity 原生场景系统 + Build Settings 登记。这是当前实现与"YooAsset 是唯一入口"规约的直接冲突点，见 §6.2。

## 6. 已知缺陷与风险

> 本节只记录 World 实现相关的问题。其他工程差距见 [project_todo](project_todo.md)。

### 6.1 海平面三处不一致（会互相覆盖）

| 位置 | 值 | 依据 |
|---|---|---|
| 生成代码常量 | 60 | `WorldPipeline.cs:42` |
| Gaia session `GS-20261109 - 162006` | 85 | `Assets/Gaia User Data/Sessions/GS-20261109 - 162006.asset:26` |
| 水面实例位置 y | 60 | `World.unity` 中 Gaia Water 的 `m_LocalPosition.y` |
| 水面实例覆盖的 `m_seaLevel` | 85 | `World.unity` PrefabInstance 修改项（目标为嵌套 `PWS_WaterSystem`） |
| 水面预制体默认 `m_seaLevel` | 25 | `Water Surface.prefab:329` |

**运行时会发生什么**（代码事实）：`PWS_WaterSystem.UpdateMode()`（`PWS_WaterSystem.cs:371-378`）在非 `m_updatingSeaLevel` 时执行
`if (transform.position.y != SeaLevel) SeaLevel = transform.position.y;`
即 **y=60 会覆盖字段 85**。随后 `SeaLevel` setter 调 `UpdateSeaLevel`（`:890`），在编辑器非播放态还会 `m_gaiaSession.SetSeaLevel(60)`（`:927`），把 session 也改成 60。

**风险**：`GaiaUnderwaterEffects.Start()`（`GaiaURPUnderwaterEffects.cs:154-190`）用 `PWS_WaterSystem.Instance.SeaLevel` 初始化 `m_seaLevel`，并据此判定初始是否在水下。若脚本执行顺序导致它先读到 85，而水面随后降到 60，水下判定会错位。
**未做运行时验证**——这是推断，需实机确认。

### 6.2 SECTR 绕过 YooAsset

`SECTR_Chunk` 直接 `SceneManager.LoadSceneAsync`，意味着：
- chunk 场景必须在 Build Settings 里（当前已登记 37 个）；
- 无法走 YooAsset 的 bundle 分包/热更/引用计数；
- 与 `Packages/com.tuyoogame.yooasset/AGENTS.md` 的"YooAsset 是唯一入口"冲突。

要改成 YooAsset 驱动，需要替换 `SECTR_Chunk` 的加载实现或改用自研 chunk 管理器。当前 `YooAssetInitializer` / `AssetLoader` 全工程无调用方。

### 6.3 磁盘上有两批 chunk，只有一批在用

**产物事实**：

| 位置 | 162007 批次 | 140021 批次 |
|---|---|---|
| `World/Chunks/*.unity` | 36（在用） | 36（孤儿） |
| `World/TerrainSplits/*.asset` | 36（在用，≈4.68MB/个） | 36（孤儿，≈582KB/个） |
| Build Settings | 36 | 0 |

主场景与 Build Settings 只引用 `162007`。`140021` 批次未被任何场景引用，属重跑残留。
`CleanupOrphanAssets.cs` 正是为此写的（按 `NodeName` 里的批次 token 清理），但**清理后又跑过一次生成**，所以又留下一批。

`Assets/Gaia User Data` 已占 657MB，含多个历史 session（125622/125900/131434/135303/135717/140020/152233/154923/155434/162006）。

### 6.4 渲染配置与本实现不匹配

| 问题 | 依据 | 影响 |
|---|---|---|
| 深度图未开 | `UniversalRP.asset:22` `m_RequireDepthTexture: 0` | 水下、雾、软粒子、水面深度依赖它 |
| 不透明图未开 | `UniversalRP.asset:23` `m_RequireOpaqueTexture: 0` | 折射/热浪类效果不可用 |
| 阴影距离 50m / 级联 1 | `UniversalRP.asset:48-49` | 3072m 世界下只有极近处有阴影 |
| 主场景无 Volume | `World.unity` 无 Volume 组件 | 后处理（Bloom/Tonemap）当前无配置载体 |
| 草走 Terrain detail | 8 层 splat + 单块约 570 万 detail 实例 | 违反 `rendering_rules` "不依赖 Terrain 自带 grass/detail" |
| 无反射/光照探针 | `!u!215` / `!u!220` 计数 = 0 | |

> `m_DrawInstanced` / `m_DetailObjectDistance` 已于 2026-11-09 调整，见 §6.6。

### 6.5 世界尺寸与流式的粒度问题

- 36 个 chunk × 512m；`LoadSize=768` 单点最多选中 4 块（`final-integrity.txt`）。
- 每块 TerrainData ≈4.68MB，常驻 4 块 ≈19MB（未含贴图/树资源）。
- 单块 1.26 万棵树 + 570 万 detail 密度，是 PC 桌面预设（`m_qualityPreset=Desktop`，`WorldPipeline.cs:127`）的量级。

### 6.6 chunk 渲染参数已调优（2026-11-09）

**产物事实**：36 个活跃 chunk 的 Terrain 字段已由脚本批量修改，孤儿批次（140021）未动。

| 字段 | 原值（Gaia Desktop） | 现值（移动端档） |
|---|---|---|
| `m_DrawInstanced` | 0 | **1** |
| `m_DetailObjectDistance` | 150 | **70** |
| `m_DetailObjectDensity` | 0.5 | **0.35** |
| `m_TreeDistance` | 1000 | **600** |
| `m_TreeBillboardDistance` | 90 | **60** |
| `m_TreeMaximumFullLODCount` | 50 | **32** |
| `m_HeightmapPixelError` | 5 | **10** |
| `m_SplatMapDistance` | 1024 | **512** |

**工具**：`Assets/Editor/TerrainChunkTuning.cs`，三个入口 `Report` / `ApplyMobile` / `RestoreDesktop`，双向可逆（`RestoreDesktop` 恢复的是 Gaia 原始值常量，不是猜测值）。

**实测提交量变化**（`Logs/Gaia/chunk-render-stats*.txt`，静态统计，非 GPU 耗时）：

| 指标 | 桌面档 | 移动档 | 变化 |
|---|---|---|---|
| 草可见实例（密度×距离粗估） | 8,894,657 | **1,355,925** | **−85%** |
| 地形 pass 数（全部 chunk） | 72 | 72 | 不变（仍 8 层） |
| 树实例（36 块合计） | 464,843 | 464,843 | 不变（只改了可见距离） |

**仍未解决**：8 层 splat 仍是每块 2 pass（`TerrainLitAdd`），要消除必须降到 4 层或走 GTS。这一项与 `m_DrawInstanced` 无关，是独立瓶颈。

**验证**：调参后 `FinalIntegrity.Run` 仍 PASS（36 chunk / 36 sector / 起伏 114.5m / 单点最多 4 块 / 全覆盖 36）。

## 7. 重跑与验证

```bash
# ① 地形 + 水（状态机，禁止 -quit）
Unity.exe -batchmode -projectPath <proj> \
  -executeMethod FishDemo.EditorTools.WorldPipeline.RunStage -logFile Logs/Gaia/build-world.log

# ② 切块 + 导出（默认程序集）
Unity.exe -batchmode -projectPath <proj> \
  -executeMethod SectrPipeline.SectorizeAndExport -logFile Logs/Gaia/sectr.log

# ③ 清残留地形 + 挂 RegionLoader
Unity.exe -batchmode -projectPath <proj> \
  -executeMethod WorldFinalize.FinalizeScene -logFile Logs/Gaia/finalize.log

# ④ 重建 Build Settings + 全量校验（exit 0 = PASS）
Unity.exe -batchmode -projectPath <proj> \
  -executeMethod FinalIntegrity.Run -logFile Logs/Gaia/final.log
```

### 7.1 地形转 GTS Mesh（2026-11-09 新增，④ 之后执行）

```bash
# ⑤ 逐块转 mesh —— 必须一进程一块。
#    GTS 的 CreateTextureArrays → StartProfile → GetGTSComponents 会遍历当前进程内
#    所有场景的 GTSComponent，单进程连续 OpenScene 必定 MissingReferenceException。
#    首块加 -gtsFirst 1，负责生成全局纹理数组；其余块 -gtsFirst 0 复用。
Unity.exe -batchmode -projectPath <proj> -executeMethod GtsConvertOne.Run \
  -gtsChunk "<chunk 文件名>" -gtsFirst 1 -logFile Logs/Gaia/gts-one.log

# ⑥ 把 mesh 挂进 chunk 场景（Terrain 保留碰撞，关掉 drawHeightmap）
Unity.exe -batchmode -projectPath <proj> \
  -executeMethod GtsApplyMeshToChunks.RunAll -logFile Logs/Gaia/gts-apply.log

# ⑦ 补 GTSMesh 组件 + 校正 worldSize（视觉一致性的关键，见 gts_srp_batcher §8）
Unity.exe -batchmode -projectPath <proj> \
  -executeMethod GtsFixMeshRendering.RunAll -logFile Logs/Gaia/gts-fix.log
```

辅助入口：

| 脚本 | 用途 |
|---|---|
| `StreamingVerify.Run` | 不依赖 Play Mode 验证空间判定 + 场景可加载性，写 `Logs/Gaia/streaming-verify-result.txt` |
| `RichWorldVerify.Run` | 抽样 4 块核对起伏/贴图层/树/草 |
| `CleanupOrphanAssets.Run` | 按活跃批次清理孤儿 chunk / TerrainData / session |
| `TerrainChunkTuning.Report` | 导出 36 块当前渲染参数分布（不改文件），写 `Logs/Gaia/terrain-tuning-before.txt` |
| `TerrainChunkTuning.ApplyMobile` | 应用移动端参数档（见 §6.6） |
| `TerrainChunkTuning.RestoreDesktop` | 恢复 Gaia Desktop 原值，用于 A/B 对比 |
| `ChunkRenderStats.Run` | 静态统计每块地形 pass 数 / 树 / 草实例量，写 `Logs/Gaia/chunk-render-stats.txt` |
| `GtsConvertOne.Run` | 单块 Terrain→GTS Mesh（一进程一块，见 §7.1） |
| `GtsApplyMeshToChunks.RunAll` | 把 GTS mesh 挂进 36 个 chunk 场景 |
| `GtsFixMeshRendering.RunAll` | 补 GTSMesh 组件 + 写 worldSizeOverride（修视觉差异） |
| `FishDemo.EditorTools.GaiaSetupTools.UpgradeMaterialsToURP` | Gaia 材质 BuiltIn→URP 迁移（先把 `m_currentRenderer` 改 Universal） |
| `FishDemo.EditorTools.FixBiomeMaterials.Setup` | 按 GUID 把美术材质换到 URP shader |
| `FishDemo.EditorTools.FixWaterMaterials.Setup` | 修 2 个水面残留 BuiltIn 材质 |

> **重跑流水线会覆盖 §6.6 的调参结果与 GTS mesh**：`SectrPipeline` 重新切块会生成全新 chunk 场景，
> 参数回到 Gaia Desktop 预设、GTS mesh 节点消失。重跑后需依次执行
> `TerrainChunkTuning.ApplyMobile` → `GtsConvertOne.Run`（逐块）→ `GtsApplyMeshToChunks.RunAll` → `GtsFixMeshRendering.RunAll`。
> 另外 shader 改动在 `Assets/Procedural Worlds/.../GTS/Shaders/`，**重装 GTS 会被覆盖**，备份在 `Tools/gts-shader-backup/`。

**验证证据**（`Logs/Gaia/final-integrity.txt`）：

```
主场景：Chunk=36, Sector=36, RegionLoader=True, Terrain=0
Build Settings 重建为 37 个场景（含主场景），缺失 0
高度起伏：全局 [0.337,0.501] 跨 114.5m，平坦/空 chunk 数=0
流式：单点最多 4 块（应<36），全覆盖 36/36
==== 结论：PASS ====
```

注意：PASS 只覆盖"结构正确 + 空间判定正确 + 场景可打开"，**不覆盖运行时帧率、显存、加载卡顿**。

## 8. 与规划方案的差距

| 规划（AGENTS.md §2） | 当前实现 | 差距 |
|---|---|---|
| GTS Terrain→Mesh 分块 | **已落地**：144 个 subTile mesh，`saveResolution=Half` | ✅ 完成；LOD 链未生成（`lodCount=1`） |
| 地形走 SRP Batcher | **已落地**：改 GTS shader 加 `UnityPerMaterial` CBUFFER | ✅ 完成，见 [gts_srp_batcher](gts_srp_batcher.md) |
| SECTR 仅作"区域级"可选流式 | SECTR 承担全部 chunk 流式 | 粒度是 512m 场景，场景切换有固定开销 |
| YooAsset 唯一资源入口 | SECTR 直接 LoadSceneAsync | 未接线；GTS mesh 也未进 bundle |
| Crest Water 5 | Gaia Water (URP) | Crest 未安装（只在 `Reference/`） |
| Enviro 3 | 无大气系统 | 未安装；主场景只有一个平行光 |
| 自研 GPU instancing 草 | Terrain detail 草（`drawInstanced=1`） | **未实现**，下一步 |
| 夜晚光源 ≤ 6 + emissive | 无昼夜系统 | 未实现 |

## 9. 改动前必读

- 改世界尺寸：`WorldPipeline.cs:38-43` 的常量与 `BiomeSpawner.cs:33` 的 `SeaLevel` **必须同步**，否则植被高度判定错位（注释原话）。
- 改海平面：同时处理 §6.1 的 5 个位置，否则运行时会被水面 transform 覆盖。
- 改切块粒度：`SectorsPerSide` 在 `WorldPipeline.cs:43` 与 `SectrPipeline.cs:28` 各有一份，且 chunk/sector 数量（36）写死在 `WorldFinalize.cs:109`、`FinalIntegrity.cs:82`、`RichWorldVerify.cs:83`。
- 改加载半径：`WorldFinalize.cs:29` 的 `LoadSizeXY`，改完跑 `StreamingVerify` 确认"单点最多 < 36 且全覆盖 36"。
- **改 GTS mesh 相关**：先读 [gts_srp_batcher](gts_srp_batcher.md)。三个易踩点：
  1. shader 改动在第三方目录，重装 GTS 会覆盖（备份在 `Tools/gts-shader-backup/`）
  2. `GTSMesh` 组件的 `worldSize` 决定贴图 tiling，必须按实际 subTile 尺寸（256m）设置，不能用默认 1024
  3. 真机必须靠 `GtsRuntimeDataBootstrap` 注入全局数组，`[ExecuteAlways]` 打包后不生效
- 任何新增渲染内容：按 [rendering_rules](rendering_rules.md) 说明走哪条批处理路径。
