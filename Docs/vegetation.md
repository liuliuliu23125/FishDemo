# 植被与 LOD 优化选型分析

> 分析对象：Amplify Impostors v1.1.0 / Shader Control v8.2.3
> 目标环境：Unity 2022.3.62f2 + URP 14.0.12，Android 旗舰机，30fps
>
> **2026-11-09 更新**：草已确定改 **BRG（BatchRendererGroup）**，实施方案见
> [grass_brg_plan](grass_brg_plan.md)。本文的选型结论（Amplify Impostors / Shader Control）
> 仍然有效，草的具体实现以 grass_brg_plan 为准。
>
> **2026-11-11 更新**：草 BRG **已落地**（36 chunk / 1265 万实例 / Terrain detail 已关），
> 状态与实测见 [grass_brg_progress](grass_brg_progress.md)。
> **树仍未改动**，仍走 Terrain；树的分析结论见本文 §5。

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
| 实例 | 36 chunk / **12,652,005** | `Logs/final-detail.log` |
| 烘焙产物 | 386 MB，32B/实例 | `GrassData/*.bytes` |
| 烘焙采样率 | `0.035`（≈0.82 株/m²） | `GrassBaker.cs:58` |
| LOD | 3 级，阈值 100/25/8 px | `GrassBRGManager.cs` |
| 剔除 | 32m 空间分块 + 块级距离剔除，21.9ms/帧 | `Logs/Grass/perf-probe.txt` |
| Terrain detail | **已关**（`DetailObjectDistance=0`） | 36 个 chunk 场景 |
| 内存 | ≈917 MB（全量常驻） | 同上 |

**尚未做**：SECTR 流式、真机验证、阴影/透光/贴图、花的迁移。

详见 [grass_brg_progress](grass_brg_progress.md)。

## 6. 树：现状与分析（2026-11-11，未改动）

**树仍走 Terrain 自带渲染**（`drawTreesAndFoliage=1`），由 TerrainRenderer 提交。
这与草不同，不要混谈。

| 项 | 值 | 依据 |
|---|---|---|
| 树原型 | 45 个 | `Logs/tree-variants.txt` |
| 实例总数 | **464,843**（36 chunk 累加） | `Logs/Gaia/chunk-render-stats-mobile.txt` |
| shader | **只有 1 个**：`PW_General_ShaderGraph_URP.shadergraph` | 同上，208 次引用 |
| 材质 keyword | **全部为空**（每个材质 `m_ShaderKeywords` 都是 0 项） | 同上 |
| 变体组合数 | **1**（单 shader + 无 keyword） | 同上 |
| LOD | 每 prefab 1 个 LODGroup，3~4 级 | prefab YAML |

### 关于「树被拆成很多批次」

已查明**不是 keyword 差异导致的**：45 个原型、208 个材质引用，shader 与 keyword
组合只有 1 种。批次数来自结构，是三处乘算的结果（实测 prefab 结构）：

1. 每个 prefab **只有 1 个 LODGroup**，3 或 4 级（45 个里 15 个是 3 级、30 个是 4 级）；
2. 每级 **1 个 MeshRenderer**，但该 renderer 的 mesh 有 **1~3 个材质槽**（树干/树叶/根部），
   每槽一次 draw call（实测材质槽数分布：1 槽 62 个、2 槽 49 个、3 槽 19 个）；
3. 36 个 chunk 各自独立提交。

即 `Σ(原型 × LOD 级数 × 材质槽数) × 视野内 chunk 数`。

**降批次只能改资产结构**（合并材质槽 / 减 LOD 级数 / 中远距离转 impostor），
调 shader 或 keyword 无效。这也正是 §3 Amplify Impostors 的适用场景。

### 为什么树没跟着草一起改 BRG

`drawTreesAndFoliage` 同时管树和草，所以关草只能用 `detailObjectDistance=0` 单点关闭。
树的 BRG 化需要自己的烘焙管线：`TreeInstance` 的序列化字段只有
`position / widthScale / heightScale / rotation / color / lightmapColor / index`
（TerrainData 实测），**没有法线**，草那套「贴合坡面」的做法不能直接复用；
且 45 原型 × 3~4 LOD 的 mesh 组合远多于草的 7×3。当前未排期。

## 7. 待验证项

- Amplify Impostors 在移动端的 overdraw 表现
- Octahedron vs Spherical 的选择
- Shader Control 对 ShaderGraph 的裁剪效果
- 树的材质槽能否合并（19 个 3 槽 prefab 是降批次的最大杠杆）
