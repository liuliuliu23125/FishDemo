# 植被与 LOD 优化选型分析

> 分析对象：Amplify Impostors v1.1.0 / Shader Control v8.2.3
> 目标环境：Unity 2022.3.62f2 + URP 14.0.12，Android 旗舰机，30fps
>
> **2026-11-09 更新**：草已确定改 **BRG（BatchRendererGroup）**，实施方案见
> [grass_brg_plan](grass_brg_plan.md)。本文的选型结论（Amplify Impostors / Shader Control）
> 仍然有效，草的具体实现以 grass_brg_plan 为准。
>
> **2026-11-12 更新**：草 BRG **已落地**（36 chunk 烘焙 435 万实例 / 133MiB；
> 湖区清草后场景当前 429 万；Terrain detail 已关），状态与实测见
> [grass_brg_progress](grass_brg_progress.md)。**树仍走 Terrain**；树的分析见本文 §6。

## 结论速览

| 包 | 是否可用 | 定位 | 结论 |
|---|---|---|---|
| Amplify Impostors | ✅ 可用 | 中远景 LOD | **采用**，必须用自带 shader |
| Shader Control | ✅ 可用 | Shader 变体裁剪 | 采用 |
| ~~Vibrant Grass Shader~~ | ❌ **移除** | 近景草 | 与流式加载架构冲突，见 §1 |
| ~~TVE Amplify Impostors Module~~ | ❌ **移除** | TVE 集成 | 只有 Unity 6 的 URP 包 |

## 1. 为什么移除 Vibrant Grass Shader

### 技术方案回顾

它是"编辑器期把所有草烘焙进一个 mesh，运行时单个 MeshRenderer"（`GrassMeshMaster.cs:20`）。理论上 draw call 极低。

### 两个致命的架构冲突

**冲突 1：每个草场实例创建一个独立材质**

`GrassMeshMaster.cs:134`：

```csharp
Material MainMatToAdd = new Material(MatApplied);
MainMatInstanced = MainMatToAdd;
MainMatInstanced.name = MainMatToUse.name + "_Instanced";
```

**每块草场 `new Material(...)`** → 每块一个材质实例 → **破坏 SRP Batcher**。

对分块流式加载是灾难：每个 chunk 加载都新增一个材质实例，chunk 卸载时若没正确销毁（它要求手动调 `DestroyInstancedMaterials_Event`），材质会持续累积。

**冲突 2：设计单位是 300m 见方的"草场"，与 chunk 粒度不匹配**

`_Setup.pdf` 原文：

```
-Keep each VibrantGrassShader instance under 400 grass fields (300x300meters),
but you can stack them next to each other.
```

它的设计单位是**一片 300×300 米**的区域，且明确建议"实例数控制在 400 个草场以内"。而本项目的地形 chunk 是 64~128 米。两者粒度差 2~5 倍，且它的 mesh 无法按 chunk 局部剔除。

### 结论

**移除。** 不是它不好，而是它的设计前提（有限区域、单 mesh、每实例独立材质）与本项目的大世界分块流式 + SRP Batcher 要求根本冲突。

**替代方案**：草走 GPU instancing / BRG，实例位置来自 chunk 的 `ComputeBuffer`。这本来就在 AGENTS.md 的批处理总则里。

## 2. 植被渲染链路（移除 Vibrant Grass 后）

```
近景（0-30m）
  └─ 草：BRG（✅ 已落地，位置来自 GrassData/*.bytes）
  └─ 树：Terrain 自带渲染，LOD0 mesh

中景（30-80m）
  └─ 树 LOD1 简化 mesh
  └─ 草切 LOD1 草簇（12 tri）

远景（80-200m）
  └─ 草 LOD2 十字面片（4 tri）
  └─ Amplify Impostor（Octahedron）— 未接入

极远（200m+）
  └─ GTS 地形 splat 贴图
```

## 3. Amplify Impostors

### 版本兼容

`Readme.txt` 原文：

```
Requirements
  * Unity 2022.3 or newer
  * URP or HDRP 14 or newer, and Shader Graph
```

**完美匹配** Unity 2022.3 + URP 14。

### 核心能力

把 3D 模型烘焙成 **Octahedron（八面体）** 或 **Spherical（球面）** impostor —— 用贴图替代整个 mesh，保留视角变化。

### ⚠️ 关键性能警告

`Readme.txt` 原文：

```
The Shader Graph versions write per-pixel depth without conservative depth, which disables
early depth testing (early-z) and makes heavy overdraw noticeably more expensive on the GPU.
The included Amplify Impostors shaders and the Amplify Shader Editor shaders use conservative
depth and keep early-z.
```

**必须用自带的 Amplify Impostors shader，不要用 ShaderGraph 版。**

对植被这种大量重叠、overdraw 严重的场景，early-z 的有无是数量级的差别。

### 烘焙注意

- 编辑器期烘焙，需纳入资产管线。
- Octahedron 通常比 Spherical 更省。
- 移动端贴图建议 512~1024。

## 4. Shader Control

### 功能

扫描工程里所有 shader，找出 `shader_feature` / `multi_compile` 产生的变体，标记未被任何材质使用的 keyword，生成裁剪配置。

### 为什么本项目需要

引入的第三方包（Enviro、Crest、Amplify Impostors、Volumetric 系列、GTS）**每个都带来大量 shader 变体**：

1. **变体爆炸** → 构建时间极长
2. **包体膨胀**
3. **运行时内存**

### 使用建议

- 在**所有包导入完成、材质配置定型后**跑一次。
- 重点裁剪：Enviro 的天气类型变体、Crest 的 LOD 模块变体、Volumetric 的质量档变体、GTS 的 splat 层数变体。
- 配合 `GraphicsSettings.asset` 的 `m_InstancingStripping`、`m_BrgStripping`。

### 风险

- 修改 shader 源码的 keyword 声明，**升级包后需重跑**。
- 裁剪过度会导致 keyword 缺失 → 材质变粉红。**必须真机验证**。

## 5. 草：现状（2026-11-11，已落地）

| 项 | 值 | 来源 |
|---|---|---|
| 路径 | BRG（`GrassBRGManager` + `GrassBRG.shader`） | 代码 |
| 实例 | 烘焙 **4,350,225**；场景当前 **4,292,694**（湖区清草后） | `Logs/Grass/bake-report.txt`、`World.unity` |
| 烘焙产物 | **132.8 MiB**，32B/实例 | `GrassData/*.bytes` |
| 烘焙采样率 | `0.0175`（≈0.41 株/m²） | `GrassBaker` 的 `SamplingRate` |
| LOD | 3 级，阈值 100/25/8 px，三层均采 albedo 图集精灵格 | `GrassBRGManager.cs` |
| 剔除 | 32m 空间分块 + 块级距离剔除；21.9ms 是 1265 万实例旧测，当前未重测 | `Logs/Grass/perf-probe.txt` |
| Terrain detail | **已关**（`DetailObjectDistance=0`） | 36 个 chunk 场景 |
| 内存 | 流式：≈100 MB（15 块驻留，CPU 位置 ~44 + GPU ~58）；全量工具路径仍约 320 MB | 估算，未重测 |

**尚未做**：真机验证、阴影/透光。**SECTR 流式已完成**（`SectrGrassStreamer`，2026-11-17）。

详见 [grass_brg_progress](grass_brg_progress.md)。

## 6. 树：现状与分析（2026-11-12 实测修订）

**树仍走 Terrain 自带渲染**（`drawTreesAndFoliage=1`），由 TerrainRenderer 提交。
这与草不同，不要混谈。

**树没有对象池可做**（2026-11-17 澄清）：树不是 GameObject，实例数据在
`TerrainData.treeInstances`（native），chunk 场景只引用 TerrainData。chunk 卸载后
引用归零，`Resources.UnloadUnusedAssets` 回收 native 数据；`SectrChunkCache` 的
TTL/LRU 已经承担了「离开后保留一段时间」的缓存职责。**不要为树新建 GameObject 池**——
那会引入 46,472 个 GameObject、Renderer 和 LODGroup，比 native 数据贵一到两个数量级。
真正可能反复加载的是共享的 7 个树原型（mesh/material/impostor 贴图）；
是否需要用常驻引用固定它们，应先看 Profiler 的资产卸载记录，不要凭猜测加。

> ⚠️ **本节旧版有两处错误结论，已于 2026-11-12 用运行时实测推翻**，详见 §6.3。

| 项 | 值 | 依据 |
|---|---|---|
| 目录里 prefab 总数 | 45 个 | `Assets/…/Prefabs/Trees/*.prefab` |
| **地形实际在用原型** | **7 个** | `Logs/tree-material-audit.txt`（运行时读 `terrainData.treePrototypes`） |
| 实例总数 | **464,843**（1/10 前；当前 46,472） | 同上 |
| 占 99.99% 的原型 | `PW_Tree_Spruce_02/06/04/07_Hero`，共 464,810 | 同上 |
| shader | **只有 1 个**：`Shader Graphs/PW_General_ShaderGraph_URP` | 同上 |
| 材质 keyword | 6 种，见下表（**不是「全部为空」**） | `m_ValidKeywords` |
| shader+keyword 组合数 | **5** | 同上 |
| LOD | 每 prefab 1 个 LODGroup，3~4 级 | prefab YAML |

### 6.1 keyword 实际分布

Unity 2022.3 材质的 keyword 序列化字段是 **`m_ValidKeywords`**，
`m_ShaderKeywords` 已废弃（旧脚本 `Tools/analyze_tree_variants.py` 解析废弃字段，
所以恒读到 0 项，得出「keyword 全空」的错误结论）。

| keyword | 出现次数（改前，92 个树材质） |
|---|---|
| `_PW_SF_WIND_ON` | 82 |
| `_PW_SF_COVER_ON` | 69 → **已全部清除** |
| `_ALPHATEST_ON` | 61 |
| `_PW_SF_SSS_ON` | 50 |
| `_PW_SF_WORLDMAP_ON` | 48 |
| `_PW_SF_BILLBOARD_ON` | 35 |

**每个 keyword 的作用与是否必要**（依据 `PW_GeneralFuncs.cginc` 的实际分支）：

| keyword | 作用 | 是否必要 |
|---|---|---|
| `_PW_SF_WIND_ON` | 顶点随风摆动 | ✅ 全部需要 |
| `_ALPHATEST_ON` | 叶片镂空 | ✅ 树叶必须，树干不需要 |
| `_PW_SF_SSS_ON` | 叶片背光透光（`AddLighting` 里的 `transLight`） | ✅ 树叶必须 |
| `_PW_SF_WORLDMAP_ON` | 世界空间颜色变化（每棵树色调差异） | ✅ 48 个材质都设了贴图 |
| `_PW_SF_BILLBOARD_ON` | 面片按视角翻转（仅 LOD 末级） | ✅ 35 个 billboard 材质 |
| `_PW_SF_COVER_ON` | 覆盖层（苔藓/积雪） | ❌ **实测完全空转** |

**为什么 COVER 是空转**：69 个启用它的材质，`_PW_Cover=0` 且
`_PW_CoverLayer0Progress=0`。`CoverSurface` 里
`amount = progress × min(worldY²+wrap,1) × alpha`，progress=0 → amount=0 →
`lerp(i_albedo, cover, 0)` 等于原色，**画面完全不变**。

**为什么不能强行统一其它 keyword**：`_PW_ShaderMode` 是显式的材质角色分层，
keyword 与它严格对应——

| ShaderMode | 角色 | keyword | 材质数 |
|---|---|---|---|
| 0 | 树干/枯木/树桩（不透明实体） | `WIND` 或空 | 32 |
| 1 | 树叶/针叶（镂空 + 透光） | `ALPHATEST+SSS+WIND+WORLDMAP` | 26 |
| 2 | 远景 billboard | 模式 1 再加 `BILLBOARD` | 35 |

树干开 `ALPHATEST` 会变透明，树叶关 `ALPHATEST` 会丢掉镂空轮廓——
所以「统一 keyword」在物理上不成立，能统一的只有那个无效的 `COVER`。

**COVER 已清除（2026-11-17 复核）**：`TreeKeywordCleanup.Run`，备份在
`Tools/tree-keyword-backup/`。仓库内 `_PW_SF_COVER_ON` 出现次数为 **0**；
地形实际在用的 10 个树材质，组合数 **4 种 → 2 种**：

| 改后组合 | 材质数 | 角色 |
|---|---|---|
| `WIND` | 6 | 树干 |
| `ALPHATEST + SSS + WIND + WORLDMAP` | 4 | 针叶 |

验证：清除前后同机位渲染，平均像素差 0.097/255，指标逐位相同（`Logs/tree-render-verify.txt`）。

**⚠️ 清除 keyword 必须直接改 .mat 文本**：`Material.DisableKeyword()` 对 ShaderGraph
材质**不持久**——内存里改了，但 `m_ValidKeywords` 在 ShaderGraph 重编译时会写回原值。
第一版 `TreeKeywordCleanup` 用 `DisableKeyword` 并报告"已清零"，实际复查发现
4 个在用针叶材质仍带 `COVER`，是假 PASS。现在工具改为直接删 `m_ValidKeywords` 的那一行，
并直接读 `.mat` 文本复核，不再用 `shaderKeywords` 自检。

**Frame Debugger 实测（编辑器截图确认）**：
- 批次拆分原因显示 `SRP: Node use different shader keywords`；
- 连续三个 SRP Batch 里，mesh 是**跨 LOD 级混合**的（LOD0/LOD1/LOD2 出现在同一批），
  说明 **keyword 一致时 LOD 级不会拆批**——LOD 不是批次来源；
- 真正的拆分维度只有 keyword，而 keyword 对应的是「树干 vs 树叶」两种材质角色。

### 6.2 「树被拆成很多批次」的真实根因

**不是 keyword 差异，也不是材质槽数。是格式错误的材质引用导致 Unity 放弃 instancing。**

运行时日志（`Logs/tree-fd4.log`）报了 216 条：

```
The tree PW_Tree_Spruce_02 couldn't be instanced because one of the materials is missing.
The tree PW_Tree_Spruce_04 couldn't be instanced because one of the materials is missing.
The tree PW_Tree_Spruce_07_Hero couldn't be instanced because one of the materials is missing.
```

guid `3eae9beb201a57d449c20d45949ed190`（材质 `PW_Tree_Ending`）被 Spruce_01/02/04/07
与 Pine 系的 FBX `externalObjects` 和 prefab 引用，但**工程里不存在拥有该 guid 的 .mat**。

**只要有一个材质槽缺失，Unity 就整批放弃 GPU instancing，退回逐实例提交。**
所以「批次多」的第一杠杆是**补齐缺失材质**，不是合并材质槽或改 shader。

已在 2026-11-12 补建 `PW_Tree_Ending.mat`（复用原 guid，贴图用
`Terrain Details/Textures/PW_Tree_Ending_D/N/M.png`），警告数 216 → **0**。

降批次的后续手段（按收益排序）：
1. ~~补齐缺失材质~~（已做，恢复 instancing）
2. 合并材质槽（需做贴图集，改动大）
3. 减 LOD 级数 / 中远距转 impostor（`Amplify Impostors`）

### 6.3 旧版被推翻的结论

| 旧结论 | 实际 | 推翻依据 |
|---|---|---|
| 「材质 keyword 全部为空」 | 6 种 keyword、5 种组合 | 字段名是 `m_ValidKeywords`；`m_ShaderKeywords` 已废弃 |
| 「变体组合数 = 1」 | 5 | 同上 |
| 「批次数来自 原型×LOD×槽×chunk」 | 主因是缺材质导致 instancing 被拒 | `couldn't be instanced` 警告 216 条 |
| 「45 个树原型」 | 地形上只用 7 个 | `terrainData.treePrototypes` 运行时读取 |

### 6.4 视觉问题：树发淡粉红（2026-11-12 已修）

4 个 Spruce 原型共 24 个材质槽的 `_MainTex` 是空引用（`fileID: 0`，入库即如此）。
ShaderGraph 里 albedo = `SAMPLE(_MainTex)`，空贴图取到白色，
白 × `_Color` × 暖主光 = 淡粉。

贴图一直在 `Content Resources/Terrain Details/Textures/`
（`PW_Tree_Spruce_D/N/M.png`、`PW_Tree_Spruce_Needles_02_D/N/M.png`），
FBX 内部记录的原始贴图名就是这两个。

修复：`TreeMaterialFix.Run` 只填 GUID 引用不复制文件；备份在 `Tools/tree-material-backup/`，
`RestoreAll` 可回滚。验证：`Logs/tree-render-verify.txt` 偏粉 0.00%、PASS。

### 6.5 同屏开销：降密度（2026-11-12）

**第一步：渲染参数压缩**（`TerrainChunkTuning.ApplyTreeLight`，可逆）：

| 参数 | 原值 | 新值 | 效果 |
|---|---|---|---|
| `TreeDistance` | 600 | **450** | 可见树 54,762 → 31,100（**−43%**） |
| `TreeBillboardDistance` | 60 | **35** | 完整 mesh 可见范围收窄 42% |
| `TreeMaximumFullLODCount` | 32 | **16** | 完整 LOD 树 32 → 16（−50%） |
| `TreeCrossFadeLength` | 50 | **20** | LOD 重叠过渡带收窄 60% |

**`TreeDistance=600` 原本是设置失误**：chunk 边长只有 512m，
600m 比块还大，等于**一块之内的树无论多远都要画**，距离裁剪完全没生效。

**为什么取 450 而不是更激进**：实测对比过 260m（−81%）和 320m（−72%），
但都被目视验收抓出问题——高空/远眺机位能直接看到 `treeDistance` 处
「树突然没有」的秃边界（`Logs/tree-distance-check.png`）。450m 贴近地形视距边缘，
地面平视与 120m 高空俯拍都看不到断带，是收益与观感的平衡点。

注意 `ApplyTreeLight` 里必须显式写 `DetailObjectDistance=0`：
若沿用 `Mobile` 档的 70，Terrain 会把 detail 草重新打开，
导致同一片地表画两遍草（Terrain detail + BRG）。这个坑已踩过一次。

**第二步：实例数降到 1/10**（2026-11-12，`TreeDensityReduce.Apply10`）：

渲染参数压缩后玩家平视观感仍太密（用户判断），于是**真正改数据**——
Unity 的 Terrain 树没有运行时密度参数（草有 `detailObjectDensity`，树没有），
减数量只能改写 `TerrainData.treeInstances` 或改 Gaia 撒点重跑世界生成。

| 指标 | 原值 | 1/10 后 |
|---|---|---|
| 全图树实例 | 464,843 | **46,472** |
| 密度 | 469 棵/公顷 | 47 棵/公顷 |
| 450m 内可见 | 31,100 | **3,082** |

实现要点（`Assets/Editor/TreeDensityReduce.cs`）：
- 按地形名做**稳定种子**（`string.GetHashCode` 跨进程不一致，不能直接用）的
  均匀抽样——取"前 N 个"会留下半边空缺的森林；
- 原始实例逐字段备份到 `Tools/tree-instance-backup/`，`RestoreAll` 可还原；
- **重跑 Gaia 世界生成会覆盖**，届时需重跑本工具。

密度参考：改后 47 棵/公顷已低于成熟针叶林下缘（300~800 棵/公顷），
接近疏林观感。若后续觉得过稀，可用 `Apply3`（1/3）或 `Apply5`（1/5）从备份重新抽样。

**验收相机统一为玩家视角**（2026-11-12）：人眼高度 1.7m、平视上扬 3°、FOV 60°。
高空俯瞰看不到玩家真实观感，所有树的目视验收不再使用俯瞰机位。

### 6.6 Impostor 接入（2026-11-12，已落地）

**结论：Terrain 树可以直接吃上 impostor** —— Terrain 渲染树时会沿用
prefab 里 LODGroup 的各级 renderer。之前 §7 判断「挂不上去」是因为当时
AmplifyImpostor 的替换目标是"场景里的 LODGroup 对象"，而实际验证发现
**改 prefab 本身即可**：Terrain 每次实例化树都从 prefab 取 LOD 结构。

**已做**（`TreeImpostorAllBake.Run`，prefab 备份在 `Tools/tree-prefab-backup/`）：

1. 导入 Amplify Impostors 1.1.0（URP14 pass 齐备，自带 `LifecycleManagement`
   兼容 shim，2022.3 可编译）；
2. 给 4 个在用云杉原型（Spruce_02/06/04/07_Hero）烘 octahedron impostor
   （2048² Albedo/Normals/Occlusion/Specular，`Assets/*_Impostor.asset`）；
3. LOD 结构 4 级压成 **2 级**（`ReplaceAllExceptFirst`）：

| 级 | 改前（每级 1~3 槽） | 改后 |
|---|---|---|
| LOD0 h=0.55 | Bark+Ending+Needles（近景） | **不变**，保留原 mesh |
| LOD1~3 | Bark+Needles / Billboard | 合并成 1 级 impostor（h=0.016） |

中远景从每棵 2~4 次提交 → **1 次**，且 impostor 材质带
`multi_compile_instancing`，同 chunk 同原型可 GPU instancing。

**烘焙注意**：
- `IsReady` 要求 prefab 上**先有** `AmplifyImpostor` 组件（autoCreate 只放宽 Data 为空）；
- `m_lodReplacement` 只在烘焙时被读取应用——对"已烘过但要改结构"的 prefab，
  需手动改 LODGroup（脚本第 3 步的语义与 `AmplifyImpostor.cs:1457` 一致）；
- 烘焙会临时加载 prefab 内容，产出的 `AmplifyImpostorAsset` 引用 shader
  `Hidden/Amplify Impostors/Octahedron Impostor URP`（URP14 分支自动选择）。

**验证**：近景 PASS（`Logs/tree-render-verify.txt`，均值与改前逐位一致）；
远景森林连续无断带，impostor 轮廓与针叶细节正常（`Logs/tree-distance-check.png`）。

**已知误报**：`TreeMaterialAudit` 会报 4 个 impostor 材质「_MainTex 为空」——
impostor shader 用的是 `_Albedo` 属性，该审计按 `_MainTex` 检查对它不适用，
属误报（远景渲染已证明正常）。

### 为什么树没跟着草一起改 BRG

`drawTreesAndFoliage` 同时管树和草，所以关草只能用 `detailObjectDistance=0` 单点关闭。
树的 BRG 化需要自己的烘焙管线：`TreeInstance` 的序列化字段只有
`position / widthScale / heightScale / rotation / color / lightmapColor / index`
（TerrainData 实测），**没有法线**，草那套「贴合坡面」的做法不能直接复用；
且原型 × 3~4 LOD 的 mesh 组合远多于草的 7×3。当前未排期。

## 7. 待验证项

- draw call 实测基线：batchmode 下 Profiler 计数器（`Batches Count` /
  `Draw Calls Count`）恒为 0，Frame Debugger 内部 API（嵌套命名空间
  `UnityEditorInternal.FrameDebuggerInternal.FrameDebuggerUtility`）未走通。
  impostor 化之后的批次数变化必须在编辑器 Frame Debugger 里人工确认。
- Amplify Impostors 在移动端的 overdraw 表现、`_PW_SF_WIND_ON` 树摆动
  与 impostor 的兼容性（impostor 材质没有风摆分支）
- Shader Control 对 ShaderGraph 的裁剪效果
- 树的材质槽能否合并（19 个 3 槽 prefab 是降批次的最大杠杆）
