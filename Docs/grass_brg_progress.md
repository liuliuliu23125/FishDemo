# 草 BRG 化 · 实施进度

> 最后更新：2026-11-11
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
| M3 真机 + 阴影/透光/贴图 + 流式 | ⬜ 未开始 | — |

**当前状态**：36 个 chunk 全部烘焙并可渲染，1265 万实例，单帧剔除 21.9ms（编辑器 D3D11），
M1/LOD/M2/多 chunk 四项验证全 PASS，0 运行时异常。Terrain detail 已关，
同一处不再被画两遍草。

**尚未完成的关键项**：
- ❌ 真机（Vulkan / iQOO 16）未验证
- ❌ 阴影/透光/贴图（当前是 Lambert + 常量环境光 + 顶点风摆，无贴图无投影）
- ❌ SECTR 流式加载未接（当前全量常驻 917MB）
- ❌ 移动端性能未测
- ❌ 花（CloverFlower / Daisy / Dandelions）随 detail 关闭一起消失，未迁到 BRG

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
- **Lambert 主光 + 常量半球环境光 + 顶点风摆**（2026-11-11 加）
- 仍缺：阴影、透光、贴图、LOD crossfade（M3）

### 2.3 编辑器工具（`Assets/Scripts/Editor/`，程序集 `FishDemo.Editor`）

| 工具 | 菜单 | 用途 |
|---|---|---|
| `GrassBaker` | ⑧ | 烘焙 36 chunk → `.bytes` |
| `GrassM1Verify` | ① | BRG 链路端到端（离屏渲染 + 像素回读 + 负向剔除） |
| `GrassLodVerify` | ⑦ | LOD 分层正确性 + A/B 收益 |
| `GrassM2Verify` | ⑩ | 烘焙数据 → 渲染全链路 |
| `GrassMultiChunkVerify` | ⑬ | 36 个 chunk 逐个验证有草 |
| `GrassVertexBudget` | ⑫ | 真实规格相机下的顶点预算 |
| `GrassPerfProbe` | ⑭ | 剔除耗时/内存实测 |
| `GrassSceneSetup` / `GrassSceneSetupM2` | ⑤ / ⑪ | 把草摆进 World 场景 |
| `GrassBudgetProbe` / `GrassMeshStats` / `GrassBakeDiagnose` | ④ / ② / ⑨ | 规模与地形数据诊断 |
| `WorldProbe` | ③ | 地表高度探测 |

### 2.4 烘焙产物

`Assets/Scenes/World/GrassData/<chunk>.bytes` × 36

| 项 | 值 |
|---|---|
| 单 chunk 实例 | ≈ 351,000 |
| 单 chunk 文件 | ≈ 10.7 MB |
| 36 chunk 实例总数 | **12,652,005** |
| 磁盘总量 | **386 MB** |
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
| LOD1 | ≥ 25 px | 共享程序化草簇 6 叶（所有类型同一个） | 12 | ≤ 37 m |
| LOD2 | ≥ 8 px | 共享十字面片 | 4 | ≤ 117 m |
| 剔除 | < 8 px | — | — | > 117 m |

> ⚠️ **§4.2 的顶点预算是按旧 LOD0（24 tri 兜底草簇）测的，已偏乐观约 5 倍。**
> 真实原型 mesh 是 128 tri（主草），需重测（M3 待办）。
> 判断「是否已注入真实 mesh」看日志 `LOD0 三角形 = 128/12/4`；
> 若是 `24/12/4`，说明只注入了部分或没跑 `DetailBrgSetup`。

阈值用**屏幕像素高度**而非固定米数，换分辨率/机型视觉密度不漂移。
换算：`pixelH = nominalHeight × scale × screenH / (2·tan(fov/2)·dist)`。

### 3.4 多 batch（16MB 上限）

BRG 单 batch 的 raw buffer 上限 16MB（`EntitiesGraphicsSystem.cs:1156`）→ **524,288 实例/批**。
超限的实测表现：shader 读到垃圾 → NaN → **全黑且无任何报错**。

1265 万实例 → **25 个 batch**，每个独立 `GraphicsBuffer` + 独立 `BatchID`。
命令矩阵 = batch × LOD（最多 75 条）。

### 3.5 空间分块剔除（性能关键）

1. 运行时在**合并后的全局数组**上做 32m 网格计数排序（O(n)），生成块表
2. 剔除时先测块：**距离剔除优先**（AABB 最近点 > 117m 直接跳过），再做 6 平面视锥测试
3. 只对可见块内实例做逐实例视锥 + LOD 判定

## 4. 实测数据

### 4.1 规模与密度

| 项 | 值 |
|---|---|
| 单 chunk 实例 | 351,664 |
| 密度 | **1.34 株/m²** |
| 36 chunk 总实例 | 12,652,005 |
| 磁盘 | 386 MB |
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

### 4.4 剔除性能演进（1265 万实例，`Logs/Grass/perf-probe.txt`）

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
| CPU `GrassInstance[]` | 386 MB |
| CPU `_positions`（float3 副本） | 145 MB |
| GPU 实例 buffer | 386 MB |
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
| 12 | 与 Terrain detail 重复绘制 | 同一处画两遍草，且 detail 无法与 BRG 合批 | `DetailObjectDistance = 0`；**不要用 `drawTreesAndFoliage=false`**（会关掉 46.5 万棵树） |
| 13 | 打包后草消失 | `Assets/` 不存在，`File.ReadAllBytes` 失败 → 静默回退合成方阵 | 正式版改走 YooAsset（M3） |

## 6. 下一步（M3，未开始）

按依赖顺序：

1. **SECTR 流式加载草数据**——按相机位置加载/卸载 chunk，解决 917MB 常驻（**当前第一优先级**）
2. **shader 加阴影/透光/贴图**（当前只有 Lambert 主光 + 常量环境光）
3. **花迁到 BRG**——CloverFlower / Daisy / Dandelions 随 detail 关闭一起消失，
   要把它们做成 BRG 实例并入同一套数据管线
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
| 烘焙数据 | `Assets/Scenes/World/GrassData/*.bytes`（36 个，387MB） |
| 烘焙 / 原型注入 | `GrassBaker.cs`（⑧）、`DetailBrgSetup.cs`（⑮） |
| 烘焙报告 | `Logs/Grass/bake-report.txt` |
| 各项验证 | `Logs/Grass/{m1,lod,m2,multi-chunk}-verify.txt` |
| 性能实测 | `Logs/Grass/perf-probe.txt` |
| 顶点预算（**旧几何，待重测**） | `Logs/Grass/vertex-budget.txt` |
| 渲染截图 | `Logs/grass-closeup.png`、`Logs/Grass/m2-render.png`、`lod-dist-*.png` |
