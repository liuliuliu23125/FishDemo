# 草 BRG 化 · 实施进度

> 最后更新：2026-11-12
> 对应方案：[grass_brg_plan.md](grass_brg_plan.md)
> 本文只记录**已落地且有实测证据**的内容。未验证项一律标注「未开始/待验证」。

## 1. 总览

把草从 Terrain detail（CPU 提交）迁到 BatchRendererGroup（BRG）。

| 里程碑 | 状态 | 验收依据 |
|---|---|---|
| M1 最小 BRG 链路 | ✅ 完成 | `Logs/Grass/m1-verify.txt` |
| M1.5 距离 LOD | ✅ 完成 | `Logs/Grass/lod-verify.txt` |
| M2 数据管线（烘焙+加载） | ✅ 完成 | `Logs/Grass/m2-verify.txt` |
| M2.1 密度返工 | ✅ 完成 | `Logs/Grass/vertex-budget.txt` |
| M2.2 多 chunk + 空间分块剔除 | ✅ 完成 | `Logs/Grass/multi-chunk-verify.txt` + `perf-probe.txt` |
| 关闭 Terrain 自带草 | ✅ 完成（2026-11-11） | 36 个 chunk 场景 `m_DetailObjectDistance: 0` |
| 真实 detail 原型 mesh | ✅ 完成（2026-11-11） | `Logs/Grass/detail-brg-setup.txt`，`LOD0=128 tri` |
| shader 主光 + 环境光 + 风摆 | ✅ 完成（2026-11-11） | `Assets/Grasses/GrassBRG.shader:1-9` |
| 花显色（albedo 图集 + tint） | ✅ 完成（2026-11-11） | 仅 LOD0 生效，见下条 |
| **全 LOD 精灵图集化 + 密度减半** | ✅ 完成并验证（2026-11-12） | `Logs/grass-flower.txt` PASS |
| M3 真机 + 阴影/透光/流式 | ⬜ 未开始 | — |

**当前状态**：36 个 chunk 全部烘焙并可渲染，**435 万实例（原 1265 万，降 65.6%）**，
磁盘 133MB（原 386MB）。三层 LOD 全部采样各类型 albedo 图集的真实精灵格，
花在中远距离也有颜色。`GrassFlowerVerify` 三个判据全 PASS。

**尚未完成的关键项**：
- ❌ 真机（Vulkan / iQOO 16）未验证
- ❌ 阴影/透光（当前是包裹式 Lambert + 常量环境光 + 顶点风摆）
- ❌ SECTR 流式加载未接（当前全量常驻约 320MB）
- ❌ 移动端性能未测

## 2. 交付物

### 2.1 运行时（`Assets/Scripts/Runtime/Rendering/`，程序集 `FishDemo.Rendering`）

| 文件 | 职责 |
|---|---|
| `GrassBRGManager.cs` | BRG 管理器：实例构建、空间分块、多 batch、剔除 Job、LOD 分层 |
| `GrassInstance.cs` | 32B 磁盘格式 + `GrassChunkData.Read`（魔数/版本/大小校验） |
| `FishDemo.Rendering.asmdef` | 独立程序集，`allowUnsafeCode=true`（工程全局为 false，BRG 输出是裸指针） |

### 2.2 Shader（`Assets/Grasses/GrassBRG.shader`）

- `FishDemo/Grass/BRGMinimal`，URP 兼容 + `DOTS_INSTANCING_ON`
- `#pragma target 4.5` + `#pragma exclude_renderers gles`
- 读 32B 语义数据（`_GrassPos` / `_GrassNormalScale`），shader 内重建变换
- **包裹式叶片光照 + 常量环境光 + 顶点风摆**（2026-11-12 改）：见 §2.2.2
- **albedo 图集采样 + alpha 裁剪 + per-type tint / 尺寸**：
  - `_USE_ALBEDO_TEX`（shader_feature）：**三层 LOD 的每类型材质全部启用**（2026-11-12 改，
    之前只有 LOD0 启用，导致 99.8% 的草纯色）
  - `_TypeTint`：per-类型染色，乘在 albedo 上。**不是 per-instance**——
    batch 按类型分段，每类型一个材质即可，GPU buffer 保持 32B 不变
  - `_TypeScaleBase`：per-类型 `(xz 倍率, y 倍率, 网格高度)`。类型尺寸差一个量级
    （枯草块 6.5×、雏菊 2.5~3×、主草 1.5×），塞不进 8 位 per-instance 缩放
  - RenderType 改 `TransparentCutout`、Queue 改 `AlphaTest`
  - **不开 `AlphaToMask`**：工程 MSAA=1，alpha-to-coverage 会退化并把叶片切成碎屑（踩坑 #16）
- 仍缺：阴影、透光、LOD crossfade（M3）

### 2.2.1 花为什么必须用图集而不是 tint（实测结论，别再走弯路）

花的颜色（雏菊白瓣黄芯、三叶草粉、蒲公英黄）**在图集像素里，不在 tint 里**：

| 事实 | 证据 |
|---|---|
| 7 个原型只用 2 张 RGBA 图集（Wild 4096²、Lawn 4096²，实测 `AssetDatabase` 加载结果）：lawn 系（枯草/三叶草/雏菊/蒲公英）共用 `PW_LawnGrass_00_D`，wild 系（主草/林下叶/枯枝）共用 `PW_WildGrass_00_D` | 解析 7 个 FBX 内嵌贴图引用 + `PW_Detail_Lawn/WildGrass.mat` 的 `_BaseMap` guid |
| FBX **无顶点色**（`LayerElementColor` 0 处） | FBX 二进制解析 |
| 图集透明背景占比高（Lawn 69% / Wild 84% 的像素 alpha==0），不裁剪会画出实心色块 | PNG 解码采样 |
| 原 `PW_Details_Foliage_URP` shadergraph 的贴图节点 `useTilingAndOffset: false`，即**直接用网格 UV 采样、不缩放** | shadergraph JSON |
| 磁盘 32B 格式的 `Color` 字段恒写 `0xFFFFFFFF` 且从未上传 GPU | `GrassBaker.cs:267`、`UploadInstanceData` 只有 2 段 |

所以「给不同 Type 传不同 tint」单独做是**显不出花色的**——必须同时给 shader
加图集采样与 alpha 裁剪。tint 只做类型间的轻微区分（如枯草块偏黄），
不承担造色职责。磁盘 `Color` 字段保留，将来做逐实例 healthy/dry 时再启用第三段上传。

**为什么 tint 走材质而不是 per-instance**：batch 已按「类型 × 16MB」分段，
一个 draw command 只能绑一个 materialID → 同 batch 必然同类型。
每类型一个材质即可表达「不同 Type 不同 tint/尺寸」，GPU buffer 保持 32B/实例、
上传带宽不变。材质最多 8 个（7 个带图 + 1 个共享纯色兜底），同 shader。

### 2.2.2 「花看不见」的真根因：不是贴图，是 LOD 覆盖（2026-11-12）

2026-11-11 加完图集采样后 `Logs/grass-closeup.txt` 报 PASS，但画面上几乎看不到花。
逐层排查后的实测结论：

| 事实 | 证据 |
|---|---|
| 同一 shader 渲染雏菊→白、三叶草→粉、蒲公英→黄，**贴图链路完全正常** | `Tools/atlas-probe/_flower_zoom.png`（Gaia 材质 vs 本 shader 并排） |
| 但可见 14,029 实例里 **LOD0 只有 28 个（0.2%）** | `Logs/grass-closeup.txt` |
| 旧 LOD1/LOD2 是**所有类型共用的程序化草簇/十字片**，UV 铺满 0..1 | `BuildClumpMesh` / `BuildCrossMesh`（已删） |
| 于是 99.8% 的草走「无贴图纯色渐变」分支，花只在 9.4m 内存在 | 上两条的组合 |

**修法**：LOD1/LOD2 改用 **Gaia 自带的真实 billboard**，三层共用该类型的带图材质：

| LOD | 几何 | 三角形 | 来源 |
|---|---|---|---|
| LOD0 | 各类型原型 mesh | 128（主草） | `PW_*` 原型 FBX |
| LOD1 | `PW_DetailCrossPlanes` | 120 | Gaia 通用 billboard |
| LOD2 | `PW_DetailQuad` | 8 | Gaia 通用 billboard |

这两个 billboard 的 UV 是「0..1 图集平铺」语义（每个面片占图集一个子块），
所以在**构建期**把 UV 重写到该类型的精灵格、并把顶点缩放到原型尺寸
（原型只有 0.03~0.30m 高，billboard 是 1×1m；材质三层共用，只有一份缩放）。
详见 `GrassBRGManager.BuildScaledBillboard`。

代价：每类型 2 份 mesh 副本（共 14 个，90/10 顶点），且 LOD1 从 6 tri 涨到 120 tri
—— 视觉换真实资产是值得的（实测画面里草的轮廓与花形明显更像 Gaia 原版）。

**精灵格位不能靠猜**：图集里平铺着几十个互不相关的图案，还混有纯色测试条。
两个实测坑：

1. **雏菊/蒲公英原型 mesh 的 UV 包围盒横跨图集顶部一条纯色绿渐变条**
   （`u[0.290,0.740] v[0.920,1.000]`），占其采样面积 **94% / 77%**。
   所以「原型 mesh 能看出花」不代表它的 bbox 能当精灵格用。
   正确格位靠 alpha 连通域分析 + mesh UV 重心光栅化命中率交叉验证得出。
2. **精灵片的法线不能用平面法线做 Lambert**。草/花是竖直面片（十字交叉），
   而实例携带的法线是**地表法线（朝上）**，`dot(up, lightDir)` 在低太阳角下接近 0
   —— 实测俯角 25° 场景里草只剩 20% 亮度，花有颜色但暗得看不见。
   改为「朝相机方向混合 0.6 + 上偏 0.5 抬半球 + 包裹光照 `ndotl*0.75+0.25`」。

### 2.2.3 尺寸来自 Gaia 的 detailPrototype，不是写死常量

烘焙只存了 8 位 per-instance 缩放（[0.5,1.5]），把 Gaia 的 `minWidth/maxWidth` 全丢了：
7 类统一 0.5~1.5 倍，而 Gaia 原配置是枯草块 **5~8**、雏菊 **1~4**、主草 **1~2**。

现在的分工：

- **类型基准倍率** → 材质 `_TypeScaleBase`（一类型一份，零带宽）
- **个体抖动** → GPU buffer 的 8 位 ScaleQ（±50%）

`DetailBrgSetup` 从 chunk 的 `TerrainData.detailPrototypes` 取 `(min+max)/2` 作为基准。
另存网格真实高度到 `_TypeScaleBase.z`：雏菊网格只有 0.15m 高，
shader 里 `bladeT` 按 1m 归一化的话根部渐变等于失效。

**副作用（已修）**：`_nominalHeight` 原本硬编码 1m，接入真实尺寸后 LOD 换算会高估 2~3 倍，
导致「远处还在画原型 mesh」。改为 `EffectiveNominalHeight` 自动取
各类型 `高度 × 竖直倍率` 的最大值。

### 2.3 编辑器工具（`Assets/Scripts/Editor/`，程序集 `FishDemo.Editor`）

| 工具 | 菜单 | 用途 |
|---|---|---|
| `GrassBaker` | ⑧ | 烘焙 36 chunk → `.bytes` |
| `DetailBrgSetup` | ⑮ | 注入 7 个原型 mesh **+ 2 张 albedo 图集 + 精灵格位 + 类型尺寸 + tint** |
| `GrassM1Verify` | ① | BRG 链路端到端（离屏渲染 + 像素回读 + 负向剔除） |
| `GrassLodVerify` | ⑦ | LOD 分层正确性 + A/B 收益 |
| `GrassM2Verify` | ⑩ | 烘焙数据 → 渲染全链路 |
| `GrassMultiChunkVerify` | ⑬ | 36 个 chunk 逐个验证有草 |
| `GrassVertexBudget` | ⑫ | 真实规格相机下的顶点预算 |
| `GrassPerfProbe` | ⑭ | 剔除耗时/内存实测 |
| `GrassSceneSetup` / `GrassSceneSetupM2` | ⑤ / ⑪ | 把草摆进 World 场景 |
| `GrassFlowerVerify` | — | **花色验收**：中远距 LOD 是否有花色（`Logs/grass-flower.txt`） |
| `GrassDensityProbe` | — | 密度校准探针（多采样率对比实例数，只统计不写盘） |
| `GrassBudgetProbe` / `GrassMeshStats` / `GrassBakeDiagnose` | ④ / ② / ⑨ | 规模与地形数据诊断 |
| `WorldProbe` | ③ | 地表高度探测 |

### 2.4 烘焙产物

`Assets/Scenes/World/GrassData/<chunk>.bytes` × 36

| 项 | 值 |
|---|---|
| 单 chunk 实例 | ≈ 120,839 |
| 单 chunk 文件 | ≈ 3.69 MB |
| 36 chunk 实例总数 | **4,350,225**（2026-11-12 减半后） |
| 磁盘总量 | **132.8 MB** |
| 每实例字节 | 32 |

## 3. 技术方案要点

### 3.1 数据结构（32B / 实例）

```
0..11   position  float3  世界坐标（含地形高度）
12..15  packed    uint    [0-7]type [8-15]scaleQ [16-31]yawQ
16..27  normal    float3  地表法线（贴合坡面）
28..31  color     uint    RGBA8 tint
```

磁盘、CPU、GPU 统一 32B。**不存 48B 矩阵**——烘焙产物的语义数据直接上传，
shader 按需重建变换，省 33% 带宽且保留运行时调缩放/LOD 的能力。

### 3.2 采样与过滤（`GrassBaker`）

```
对每层每格：n = GetDetailLayer[x,y]
  n' = round(n × 0.035)
  if n>0 && n'==0 → 30% 概率保底 1 个（避免空洞）
  格内均匀随机抖动（消除网格感）
  过滤：坡度 tan > 1.0（45°）、高度 < 海平面+2m
```

- `SamplingRate = 0.035`（`GrassBaker.cs:58`）
- 坡度用高度图中心差分自算（米/米），**不用语义不明的 `TerrainData.GetSteepness`**
- 确定性种子（按 chunk 名哈希），重烘结果一致

### 3.3 距离 LOD（3 层，单 batch 内切 meshID）

| 层 | 屏幕像素阈值 | 几何 | 三角形 | 1080p/60° 对应距离 |
|---|---|---|---|---|
| LOD0 | ≥ 100 px | **各类型的真实 detail 原型 mesh**（`DetailBrgSetup` 注入） | **128**（主草；其余类型 24~322） | ≤ 9.4 m |
| LOD1 | ≥ 25 px | **Gaia `PW_DetailCrossPlanes`**（每类型一份 UV/尺寸定制副本） | 120 | ≤ 37 m |
| LOD2 | ≥ 8 px | **Gaia `PW_DetailQuad`**（同上） | 8 | ≤ 117 m |
| 剔除 | < 8 px | — | — | > 117 m |

> 判断「是否已注入真实资源」看日志 `LOD0 三角形 = 128/120/8`。
> 若是 `6/6/4`（程序化兜底）或 `24/12/4`（更早的兜底草簇），
> 说明没跑 `DetailBrgSetup` 或 billboard 注入失败。
>
> ⚠️ **§4.2 的顶点预算表是 2026-11-11 的旧几何测的，已不适用**
> （当时 LOD1/2 是 12/4 tri，现在是 120/8 tri）。重测属 M3 待办。

阈值用**屏幕像素高度**而非固定米数，换分辨率/机型视觉密度不漂移。
换算：`pixelH = nominalHeight × scale × screenH / (2·tan(fov/2)·dist)`。

### 3.4 多 batch（16MB 上限）

BRG 单 batch 的 raw buffer 上限 16MB（`EntitiesGraphicsSystem.cs:1156`）→ **524,288 实例/批**。
超限的实测表现：shader 读到垃圾 → NaN → **全黑且无任何报错**。

435 万实例 → **13 个 batch**（实测 `Logs/flowerverify4.log`），每个独立 `GraphicsBuffer` + 独立 `BatchID`。
命令矩阵 = batch × LOD（最多 75 条）。

### 3.5 空间分块剔除（性能关键）

1. 运行时在**合并后的全局数组**上做 32m 网格计数排序（O(n)），生成块表
2. 剔除时先测块：**距离剔除优先**（AABB 最近点 > 117m 直接跳过），再做 6 平面视锥测试
3. 只对可见块内实例做逐实例视锥 + LOD 判定

## 4. 实测数据

### 4.1 规模与密度

| 项 | 值 |
|---|---|
| 单 chunk 实例 | 120,839 |
| 密度 | **0.46 株/m²** |
| 36 chunk 总实例 | 4,350,225 |
| 磁盘 | 132.8 MB |
| 最近邻距离 CV | 0.565（非规则网格，地形疏密生效） |

### 4.2 顶点/三角形预算（`Logs/Grass/vertex-budget.txt`）

1080p / 60° FOV / 站姿 1.7m，单 chunk 数据，6 个方向：

| 视角 | LOD0 | LOD1 | LOD2 | 可见 | 三角形 | 顶点 |
|---|---|---|---|---|---|---|
| 平视 0° | 128 | 1,451 | 13,511 | 15,090 | 74,528 | 149,056 |
| 俯视 15° | 137 | 1,434 | 13,213 | 14,784 | 73,348 | 146,696 |
| 俯视 30° | 146 | 1,352 | 5,968 | 7,466 | 43,600 | 87,200 |
| 环视 90° | 157 | 1,465 | 13,458 | 15,080 | 75,180 | 150,360 |
| 环视 180° | 191 | 1,501 | 14,002 | 15,694 | 78,604 | 157,208 |
| 环视 270° | 146 | 1,378 | 13,178 | 14,702 | 72,752 | 145,504 |

**单帧最大 15.7 万顶点 / 7.9 万三角形**（占 1.5M 三角形预算的 5.2%）。

### 4.3 LOD 收益（`Logs/Grass/lod-verify.txt`）

| 视角 | 开 LOD | 关 LOD | 节省 |
|---|---|---|---|
| 近 6 m | 36.2 tri/实例 | 144.0 | 74.9% |
| 中 20 m | 17.0 tri/实例 | 144.0 | 88.2% |
| 远 60 m | 4.0 tri/实例 | 144.0 | 97.2% |

### 4.4 剔除性能演进（**旧数据：1265 万实例**，`Logs/Grass/perf-probe.txt`）

> 下表是 2026-11-11 在 1265 万实例上测的。现在是 435 万，扫描量与其成正比下降，
> 但**未重测**——不要直接引用为当前值。

| 版本 | 扫描量 | 单帧耗时 |
|---|---|---|
| 4 遍全量扫描 | 1265 万 × 4 | 2466 ms |
| 2 遍 + 前缀和 | 1265 万 × 2 | 1244 ms |
| + 32m 空间分块 | 193 万（15.3%） | 527 ms |
| **+ 块级距离剔除** | **1.1 万（0.1%）** | **21.9 ms** |

**降 112 倍**，可见实例数（2244）全程不变，正确性未破。

### 4.5 内存占用

| 项 | 大小 |
|---|---|
| CPU `GrassInstance[]` | 133 MB |
| CPU `_positions`（float3 副本） | 145 MB |
| GPU 实例 buffer | 133 MB |
| **合计** | **≈ 917 MB** |

桌面可接受，**移动端必须靠流式加载**。

## 5. 踩坑记录（都会导致「草消失且无报错」）

| # | 坑 | 表现 | 正确做法 |
|---|---|---|---|
| 1 | `AddBatch` 只接受 raw buffer | `Target.Structured` 失败 | `Target.Raw` + stride 4 + count=字节数/4 |
| 2 | DOTS 块名必须是枚举 | 自定义块名不报错但 metadata 全失效，草全叠原点 | 自定义属性用 `UserPropertyMetadata` |
| 3 | 访问宏需 #ifdef 分路径 | 非 DOTS 变体编译失败 | 照 URP 模式：DOTS 读缓存+`#define` 重定向，否则 static 常量 |
| 4 | `splitVisibilityMask=0` | 整批静默丢弃 | 相机单 split 写 `0x01` |
| 5 | Job 内 `Dispose()` native container | `InvalidOperationException`（222 条，画面正常只在日志刷） | 字段加 `[DeallocateOnJobCompletion]` |
| 6 | 单 batch > 16MB | shader 读垃圾 → 全黑无报错 | 按 52.4 万实例切分多 batch |
| 7 | `SubmitRenderRequest` 不等首次 Job | 首帧全黑，第二帧正常 | 验证脚本渲两次（或等 JobHandle） |
| 8 | 块级只做视锥测试 | 1384/9216 块「可见」，扫 15% 实例 | **必须先做距离剔除**（far clip 3000m 让视锥覆盖全世界） |
| 9 | `[ExecuteAlways]` + `AddComponent` | 验证脚本改参数不生效 | `Configure*ForValidation` 内部先 `Shutdown()` |
| 10 | `_scaleMultiplier=8` 残留 | 密度看似正常但顶点爆炸（LOD 距离被放大 8 倍） | 阶段切换必须清理上一阶段的权宜参数 |
| 11 | 运行时按名字找 detail 原型 mesh | `Resources.FindObjectsOfTypeAll<Mesh>()` 实测 0 命中，7 种植物长一样，`LOD0 三角形 = 24` | 编辑器期用 `AssetDatabase` 拿引用、序列化进组件（`DetailBrgSetup`）；且 FBX 内 mesh 名带 `_E` 后缀 |
| 12 | 与 Terrain detail 重复绘制 | 同一处画两遍草，且 detail 无法与 BRG 合批 | `DetailObjectDistance = 0`；**不要用 `drawTreesAndFoliage=false`**（会关掉 4.65 万棵树） |
| 13 | 打包后草消失 | `Assets/` 不存在，`File.ReadAllBytes` 失败 → 静默回退合成方阵 | 正式版改走 YooAsset（M3） |
| 14 | `[Toggle]` 属性设了但没 EnableKeyword | `shader_feature` 变体不切换：LOD0 花仍是绿色、无 alpha 裁剪，**静默失效** | 代码里必须显式 `mat.EnableKeyword("_USE_ALBEDO_TEX")`，`SetFloat` 只影响属性值不影响变体 |
| 15 | 只传 tint 不给贴图 | 花仍是纯绿（tint 乘在绿色渐变上，乘什么都是绿） | 花色在图集里，必须采样 + `clip(a - _Cutoff)` |
| 16 | **MSAA=1 时开 `AlphaToMask On`** | alpha-to-coverage 退化，近景草被切成亮绿碎屑、前景出现大块纯色（实测截图） | 工程 `Assets/Settings/UniversalRP.asset:28` 的 `m_MSAA: 1`，**不要开 A2M**；靠 `clip` 硬边即可 |
| 17 | 编辑器脚本里连续调两个 setter | 各重建一次 BRG，每次上千万实例分块排序 → 整脚本超 60 秒被截断，连保存都走不到 | 用 `ApplyDetailPrototypeSetup(..., rebuild:false)` 一次写入；编辑器期只写字段，下次打开场景自然重建 |
| 18 | **只给 LOD0 配图集，LOD1/2 用程序化几何** | 图集链路正常（单独渲染雏菊出白、三叶草出粉）但画面上几乎没花：可见 14,029 实例里 LOD0 只占 28 个（0.2%），其余 99.8% 走无贴图纯色分支 | LOD1/LOD2 改成「每类型采样自己图集格的精灵交叉片」，三层共用带图材质。**排查时先看 `GetLastLodCount` 的分布，别急着怀疑贴图** |
| 19 | 用原型 mesh 的 UV **包围盒**当精灵格 | 雏菊/蒲公英的 bbox 横跨图集顶部一条纯色绿渐变条（占采样面积 94%/77%），照样采不出花 | 精灵格要靠 alpha 连通域 + mesh UV 重心光栅化命中率交叉验证，剔除 `fill>0.97` 的实心矩形 |
| 20 | **竖直面片用平面法线做 Lambert** | 实例法线是地表法线（朝上），低太阳角下 `dot(up,light)`≈0：俯角 25° 场景草只剩 20% 亮度，花有颜色但暗到看不见 | 法线朝相机混合 0.6 + 上偏 0.5 抬半球 + 包裹光照 `ndotl*0.75+0.25` |
| 21 | 加了真实尺寸却仍用 `_nominalHeight=1` | LOD/剔除距离按 1m 换算，实际主草只有 0.30m → 高估 2~3 倍，「远处还在画原型 mesh」 | `EffectiveNominalHeight` 取各类型 `高度×竖直倍率` 的最大值 |
| 22 | 用 `GetPixels32()` 统计画面区域时忘了行序 | 按「自上而下」写 `y*width`，实际 Color32 第 0 行是画面**底部**，统计到的是天空与树冠（19 万误命中，真实地面只有几千） | 行序自下而上：「画面下半部」= 数组**前** 55% |
| 23 | 低采样率时按比例直觉估总量 | 0.035→0.0175 以为减半，实际 1265 万→435 万（降 65.6%）。因为稀疏格保底（30% 概率补 1 个）与采样率无关，占比在低 rate 下变大 | 数量以 `Logs/Grass/bake-report.txt` 实测为准；要精确落某个目标值必须按真实分布反算，不能等比缩放 |

## 6. 下一步（M3，未开始）

按依赖顺序：

1. **SECTR 流式加载草数据**——按相机位置加载/卸载 chunk，解决约 320MB 常驻（**当前第一优先级**）
2. **shader 加阴影/透光**（当前有包裹式叶片光照 + 常量环境光 + albedo 图集裁剪）
3. ~~花迁到 BRG~~ ✅ 已完成（2026-11-12），见 §2.2.2
4. **剔除并行化**（`IJobParallelFor`）——21.9ms 仍占 33ms 预算的 2/3
5. **剔除 Job 移出主线程依赖**（当前 `SubmitRenderRequest` 需等 Job 完成）
6. **真机验证**（Vulkan / iQOO 16 / 30fps）
7. 分块排序移入烘焙期（省 1~2 秒加载开销）
8. `_positions` 与 `_instances` 合并（省 145MB）
9. 草数据改走 YooAsset（当前 `File.ReadAllBytes`，打包后失效）

## 7. 复现步骤

```bash
# 1. 烘焙（36 chunk，约 10 分钟）
Unity.exe -batchmode -nographics -projectPath E:\FishDemo \
  -executeMethod FishDemo.EditorTools.GrassBaker.RunAll

# 2. 接入场景
Unity.exe -batchmode -nographics -projectPath E:\FishDemo \
  -executeMethod FishDemo.EditorTools.GrassSceneSetupM2.Run

# 3. 验证（需真实图形设备，不要加 -nographics）
Unity.exe -batchmode -projectPath E:\FishDemo \
  -executeMethod FishDemo.EditorTools.GrassMultiChunkVerify.Run
Unity.exe -batchmode -projectPath E:\FishDemo \
  -executeMethod FishDemo.EditorTools.GrassPerfProbe.Run
```

**注意**：批处理前必须关闭 Unity 编辑器，否则报
`HandleProjectAlreadyOpenInAnotherInstance` 并崩溃。

## 8. 关键文件索引

| 内容 | 位置 |
|---|---|
| 实施方案 | [grass_brg_plan.md](grass_brg_plan.md) |
| 渲染规约 | [rendering_rules.md](rendering_rules.md) |
| 性能预算 | [performance_budget.md](performance_budget.md) |
| 运行时 | `Assets/Scripts/Runtime/Rendering/{GrassBRGManager,GrassInstance}.cs` |
| Shader | `Assets/Grasses/GrassBRG.shader` |
| 烘焙数据 | `Assets/Scenes/World/GrassData/*.bytes`（36 个，133MB） |
| 烘焙 / 原型注入 | `GrassBaker.cs`（⑧）、`DetailBrgSetup.cs`（⑮） |
| 烘焙报告 | `Logs/Grass/bake-report.txt` |
| 各项验证 | `Logs/Grass/{m1,lod,m2,multi-chunk}-verify.txt` |
| 性能实测 | `Logs/Grass/perf-probe.txt` |
| 顶点预算（**旧几何，待重测**） | `Logs/Grass/vertex-budget.txt` |
| 渲染截图 | `Logs/grass-closeup.png`、`Logs/Grass/m2-render.png`、`lod-dist-*.png` |
