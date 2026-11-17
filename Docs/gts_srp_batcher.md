# GTS Shader SRP Batcher 改造记录

> **本文件 212 行，改 GTS shader 或排查地形合批/视觉差异前读。**
> 改动对象是第三方资产，重装 GTS 会覆盖，需要重做。

## 1. 为什么改

GTS 的 URP shader 原版**不兼容 SRP Batcher**，导致地形 mesh 完全不走合批。

判定依据（SRP Batcher 的硬性要求）：所有 per-material 属性必须声明在名为
`UnityPerMaterial` 的 CBUFFER 内。

| 文件 | 原状 | 问题 |
|---|---|---|
| `GTS/Shaders/GTS_TerrainInput.hlsl` | 17 个材质属性裸声明在全局作用域 | 没有 `UnityPerMaterial`，整 shader 判定不兼容 |
| `GTS/Shaders/URP/TerrainLitInput.hlsl` | `_NumLayersCount` 在 `_Terrain` CBUFFER 内 | 该变量在 Properties 标了 `[PerRendererData]`，per-object 数据破坏批次一致性 |
| `GTS/Shaders/URP/GTS_URP_Compiled.shader` | `_NumLayersCount` 带 `[PerRendererData]` | 同上 |

## 2. 改了什么

### 2.1 `GTS_TerrainInput.hlsl`

把 **17 个材质属性**包进 `CBUFFER_START(UnityPerMaterial)`：

```
_TerrainPosSize, _BlendFactor, _Resolution, _DetailNearFarData,
_GeoNearData, _GeoFarData, _GeoLayerData0, _GeoLayerData1, _MacroVariationData,
_TessellationMultiplier, _TerrainHeightmapRecipSize, _GlobalBlendData,
_WorldAlignedUVs, _ObjectSpaceDataA, _ColormapNearFarData, _ColormapDataA
```

**保持全局（不进 CBUFFER）的 9 个**：

| 变量 | 原因 |
|---|---|
| `_DetailBlendData` | 未在 Properties 声明；全工程无任何 C# 设置它 |
| `_LayerST0..7` | 未在 Properties 声明；仅在 `_MOBILE_VR_ON` 分支使用；由 `SetGlobalVector` 下发 |

**贴图不进 CBUFFER**（CBUFFER 只放数值）。贴图差异不影响 SRP Batcher——
它按 CBUFFER 布局归并，不按材质对象归并。**这是本次改造能生效的关键认知**：
36 个材质只要布局一致，即使贴图各不相同，也能合进同一批。

### 2.2 `URP/TerrainLitInput.hlsl`

把 `_NumLayersCount` 移出 `_Terrain` CBUFFER，改为普通全局声明。
全目录核实该变量只有声明、无读取（`grep _NumLayersCount` 仅 2 处声明）。

### 2.3 `URP/GTS_URP_Compiled.shader`

`_NumLayersCount` 的 `[PerRendererData]` 标记移除，保留属性声明以免材质数据丢失引用。

## 3. 影响面

- 7 个 pass（ForwardLit / ShadowCaster / GBuffer / DepthOnly / DepthNormals /
  SceneSelectionPass / Meta）全部 include 同一套 hlsl，**改一处即全部生效**。
- 贴图声明、采样逻辑、splat 混合算法**完全未动**。
- 材质资产无需重建（按 GUID 引用 shader，源码改动自动生效）。

## 4. 回滚

原始文件备份在 `Tools/gts-shader-backup/`：

```
GTS_URP_Compiled.shader
GTS_TerrainInput.hlsl
TerrainLitInput_URP.hlsl   （原始 TerrainLitInput.hlsl）
TerrainLitPasses.hlsl
GTS_Functions.hlsl
```

回滚方式：把对应文件复制回 `Assets/Procedural Worlds/Packages - Install/GTS/Shaders/` 下原位置。

**注意**：重装或升级 GTS 会覆盖本次改动，需要重新应用。

## 5. 怎么验证生效

SRP Batcher 没有公开的运行时查询 API（`ShaderUtil.GetSRPBatcherCompatibilityCode` 是 internal，
反射调用会让 batchmode 崩溃，已验证）。**唯一可靠验证是 Frame Debugger**：

1. 进 Play Mode，打开 Frame Debugger
2. 选中一个地形 subTile 的 draw call
3. 看右侧面板是否出现 `SRP Batch` 分组，以及 draw call 是否被归入该分组
4. 对比改造前：地形是独立的 `Draw Mesh` 条目，无 SRP Batch 归组

若仍不合批，按以下顺序排查：
- shader 是否真的重编译（改完需触发 reimport）
- 是否有别的材质属性漏在 CBUFFER 外
- 是否被静态合批抢先（静态合批会合并成大 mesh，反而破坏 SRP Batcher 归并）

## 6. 与静态合批的关系

2026-11-09 时 subTile 标了 `isStatic`，会触发**静态合批**（构建期把多个 mesh 合成大 VBO）。
这与 SRP Batcher 是两套机制，且会互相干扰：

| 机制 | 合批依据 | 现状 |
|---|---|---|
| 静态合批 | 同材质 + 同 static 标记，构建期合并 | **已取消**：PlayerSettings Android/Standalone 0/0 + `go.isStatic=false` |
| SRP Batcher | 同 shader variant + 同 CBUFFER 布局 | 已生效 |

**两者不建议同时依赖。** 确认 SRP Batcher 生效后已取消 subTile 的 static 标记，
避免静态合批把 mesh 合并后破坏 SRP Batcher 的按材质归并粒度。

## 7. 待办

- [x] Frame Debugger 确认 SRP Batch 分组出现（项目负责人已确认生效）
- [x] 静态合批已关闭（见 §8.6）
- [x] 纹理数组改 ASTC（见 §8.5）
- [x] 草改 BRG（435 万实例，Terrain detail 已关）

## 8. 视觉与 Terrain 不一致的修复（2026-11-09）

切到 mesh 后画面与 Terrain 差异明显，根因是**运行时数据注入缺失**，不是 shader 算法问题。

### 8.1 根因一：GTSMesh 组件缺失（主因）

GTS 的 splat 混合依赖一组**全局** Vector 数组，由 `GTSComponent.UpdateMaterial()`
通过 `Shader.SetGlobalVectorArray` 注入：

```
_HeightData / _UVData / _ColorData / _LayerDataA / _TriPlanarData /
_MaskMapRemapData / _DisplacementData / _MaskMapRemapMin / _MaskMapRemapMax
```

最初的 `GtsApplyMeshToChunks` 只创建了裸 `MeshFilter + MeshRenderer + 材质`，
**没挂 GTSMesh**，于是无人注入这些数组 → shader 读到空值 → splat 权重、UV、层颜色全错。

修复：每个 subTile 挂 `GTSMesh`（设 profile / material），并调 `Refresh()`。

### 8.2 根因二：worldSize 硬编码 1024

`GTSMesh.worldSize` 原实现返回固定 `(1024, 0, 1024)`，用于计算 tiling：
```
tiling = worldSize / layer.tileSize
```
本工程 subTile 实际是 **256m**，用 1024 计算会让贴图密度**错 4 倍**（明显过密）。

修复：改 `GTSMesh.worldSize` 为可配置——优先读 `m_worldSizeOverride`，
否则回退到 mesh 包围盒的 XZ 尺寸。构建脚本用 `SetWorldSizeOverride()` 写入实际值
（实测写入 `(256, 0, 256)`）。

### 8.3 根因三：真机上不会自动注入

`GTSMesh` 标了 `[ExecuteAlways]`，编辑器里能靠 `Update/OnValidate` 触发；
但真机上：
- `GTSComponent.Update()` 只在 `m_isDirty` 时 Refresh
- `m_isDirty` 由 `OnValidate` 置位，而 `OnValidate` 只在编辑器生效

→ 打包后全局数组不会被设置。

修复：新增 `Assets/GTSRuntime/GtsRuntimeDataBootstrap.cs`（挂每个 subTile），
`Start()` 时显式调一次 `gtsMesh.Refresh()`，每帧开销 0。

**目录选择**：GTS 无 asmdef，编进 `Assembly-CSharp`；asmdef 程序集引用不到它，
`Assets/Editor` 又会被编进 `Assembly-CSharp-Editor`（打包后不存在）。
所以放在 `Assets/GTSRuntime/` 这个无 asmdef 的独立目录。

### 8.4 相关工具

| 脚本 | 用途 |
|---|---|
| `Assets/Editor/GtsFixMeshRendering.cs` | 批量给 36 chunk 的 144 个 subTile 挂 GTSMesh + 写 worldSize |
| `Assets/GTSRuntime/GtsRuntimeDataBootstrap.cs` | 运行时注入全局数组 |
| `Assets/Procedural Worlds/Packages - Install/GTS/Scripts/Core/MonoBehaviours/GTSMesh.cs` | 已改 `worldSize` 实现（第三方源码，重装会覆盖） |

### 8.5 纹理数组已改 ASTC 压缩（2026-11-09）

**改前**：`maxTextureSize=2048`、`compressed=false`（RGBA32），单数组 **341MB**，合计 **682MB**。

**改后**：`compressed=true`、`compressionFormat=ASTC_8x8`，单数组 **21.3MB**，合计 **42.6MB**，省 **94%**。

**为什么必须用带 alpha 的 ASTC**（关键，别踩）：

albedo 数组的 **alpha 通道被当作高度混合权重**使用：
```hlsl
GTS_Functions.hlsl:591  float masks[] = { albedoA.a, albedoB.a, albedoC.a, albedoD.a };
GTS_Functions.hlsl:592  float4 splatHeight = float4(masks[0..3]) * splatControl.rgba;
```
用 `ASTC_RGB_*`（无 alpha）会让高度混合失效，材质过渡变成硬边。

**格式陷阱（实测确认）**：`GTSCompressionFormat` 的枚举值与 Unity `TextureFormat` 的映射不一致：

| GTS 枚举 | 值 | 实际 TextureFormat | alpha |
|---|---|---|---|
| `ASTC_4x4` | 48 | `ASTC_4x4` | ✅ |
| **`ASTC_6x6`** | **50** | **`ASTC_RGB_6x6`** | ❌ **无 alpha** |
| `ASTC_8x8` | 51 | `ASTC_8x8` | ✅ |

→ **避开 6x6**，用 `ASTC_8x8`。

**体积对照**（2048×2048×8 层）：

| 格式 | 单数组 | 双数组合计 |
|---|---|---|
| RGBA32（原） | 341 MB | 682 MB |
| **ASTC_8x8（采用）** | **21.3 MB** | **42.6 MB** |

**工具**：`Assets/Editor/GtsAstcCompress.cs`（`-executeMethod GtsAstcCompress.Run`）。
它先打开一个 chunk 场景填充 `gtsLayers`，再改设置并重建数组。
数组 GUID 不变（`CreateOrReplaceAsset` 复用原资产），材质绑定自动保持。

**注意**：PC 编辑器 `SystemInfo.SupportsTextureFormat(ASTC_8x8)` 返回 **False**，
但 `EditorUtility.CompressTexture` 仍能成功压缩并写入资产——这是编辑器侧无法采样，
不影响 Android 真机。代价是**编辑器里预览地表可能异常**，需以真机为准。

### 8.6 静态合批已关闭

地形 subTile 原先标了 `isStatic`，会触发构建期静态合批。
它把多个 mesh 合并成大 VBO，**破坏 SRP Batcher 的按材质归并粒度**，二者互相干扰。

已由 `Assets/Editor/BatchingSetup.cs` 的 `FixAll` 通过 PlayerSettings 的 internal
`SetBatchingForPlatform` 写入平台覆写：

```
m_BuildTargetBatching:
  Android:           m_StaticBatching: 0, m_DynamicBatching: 0
  Standalone:        m_StaticBatching: 0, m_DynamicBatching: 0
```

同时 `GtsApplyMeshToChunks.cs` 已改为 `go.isStatic = false`，重跑 GTS 流水线
不会再把 subTile 标回 static。存量 chunk 里的 static 标记仍保留，但全局静态合批已关，
不再参与合批。

SRP Batcher 成为地形唯一的合批路径。
