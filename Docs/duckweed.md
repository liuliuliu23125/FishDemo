# 水面睡莲/浮萍（中心湖）

> 从 Nature Package - Swamp,Forest Environment v1.0 取出的水面植物，沿中心湖岸线点缀一圈，补足水面细节。
> 包内按资源命名未找到浮萍（Lemna/duckweed），可用的是 `swamp_lily_*`（睡莲/浮水植物）；本模块以它作为水面浮水植被。
> 布置参数唯一来源：`Assets/Editor/Water/LakeDuckweedSetup.cs`。改布局先改它再重跑。

## 1. 资产提取与导入

| 项 | 值 |
|---|---|
| 源包 | `Reference/Nature Package - Swamp,Forest Environment v1.0.unitypackage`（1.7GB，仅取子集） |
| 源目录 | `Assets/ALP8310_Assets/SwampForest_environment_/ExtraMeshes/` |
| 模型 | `swamp_lily_Pink01/Pink02/White03/White04.fbx` → `Assets/WaterPlants/Source/SwampForest/Models/` |
| 贴图 | `SwampLily01.tga`（diffuse+alpha）、`SwampLily01_N.png`（normal）→ `.../Textures/` |
| 占位材质 | 包内原 `SwampLily01.mat`（Standard，未渲染）→ `.../Models/Materials/`；保留是为了让 FBX 的 external material 引用可解析，避免每次导入自动生成新材质 |
| 导入 | 贴图 1024 / ASTC_8x8 / Trilinear / Clamp；法线 `NormalMap`；diffuse 保留 alpha |
| 源尺寸 | diffuse 4096、normal 2048，均降到 **1024**（TGA 4.0MB、法线 1.1MB），避免大图进入仓库与显存 |
| 模型数据 | Pink01 1664v/1957t，Pink02 1539v/1867t，White03 1607v/1895t，White04 1762v/1985t |
| 源包围盒 | XZ footprint 0.28~0.36m；按目标 3.2m 放大（baseScale 5.55~7.17） |

- 4 个 FBX 的 `.meta` 原样保留，GUID 不变；目标路径改到 `Assets/WaterPlants/`，不影响旧引用（包内没有其他资产引用它们）。
- 包内另有 SpeedTree 版水莲 prefab（`models/Swamp_lily_*Detail*.prefab`）；**未使用**：它们依赖内置 SpeedTree shader，不能在 URP 14 下直接工作。包内 Standard 材质只作 FBX 占位、不参与渲染。

## 2. 场景结构

`Assets/Scenes/World.unity` 根节点 `Lake Duckweed`：

| 子节点 | 内容 |
|---|---|
| `Lily Pink01` / `Pink02` / `White03` / `White04` | 每个变体一个分组，下面是共享该变体网格的实例 |

- 共 **193 个实例**（52 / 48 / 46 / 47），18 个簇；每簇 8~13 株，簇内散开半径 9m。
- 簇心沿湖岸线分布：方位 120°→420°（避开正北礁区 90°±30°），半径 `0.88R~0.96R`；单株最远 `0.975R`，避免上岸。
- 单株缩放 0.75~1.35 × baseScale，随机朝向；y = `LakeCarve.WaterLevel + 0.06 + 0~0.02`。
- 使用固定种子 `20261121`，重复 `Apply` 位置一致；改种子会整体重排。

## 3. 渲染

| 项 | 实现 |
|---|---|
| Shader | `Assets/WaterPlants/Shaders/WaterSurfacePlant.shader`（`FishDemo/Water/SurfacePlant`） |
| 光照 | 主光 + `SampleSH` 环境项；不采样阴影/反射探针 |
| 透明 | BaseMap alpha + `_Cutoff=0.5` alpha clip；`Cull Off`；`Queue=AlphaTest(2450)`、`ZWrite On` |
| 动画 | 每株用**自身枢轴**算相位，整株 bob（`_WaveStrength=0.07`、`_WaveSpeed=0.85`）+ 水平漂移（`_DriftStrength=0.06`）+ 倾斜（`_TiltStrength=3°`）+ 偏航（`_YawStrength=2°`）；无 CPU 更新、无逐实例 MPB |
| 合批 | 4 个共享网格 + 1 个共享材质 + `enableInstancing=true`；shader 同时是 SRP Batcher 兼容，实际走哪条路径以 Frame Debugger 为准 |
| 阴影 | 关闭 Cast Shadows；shader 没有 ShadowCaster pass |
| 反射 | 在 Default 层，符合 Crest 平面反射的层筛选；未单独做像素验证 |

## 4. 性能账

| 项 | 值 |
|---|---|
| 新 pass / RT / blit | 0 / 0 / 0 |
| 实例 | 193 个 MeshRenderer，4 个网格，1 个材质 |
| 三角形 | 193 个实例合计 ≈371,845（单株约 1.9k） |
| draw call | 4 个唯一网格 / 1 个材质；instancing 已开，但 SRP Batcher 可能优先，未用 Frame Debugger 实测 |
| Draw 开销 | 每实例独立剔除；不会因整圈网格一次提交全部三角形 |
| 真机 | **未验**；低画质档可直接 `Remove` 或减半簇数/实例数 |

## 5. 生成与验收

```
LakeDuckweedSetup.Apply
  → LakeDuckweedSetup.Report
  → LakeDuckweedPreview.Run        (真实图形设备)
```

- 菜单：`FishDemo/Water/重建水面浮萍`、`校验水面浮萍`、`移除水面浮萍`。
- 批处理：`-executeMethod LakeDuckweedSetup.Apply` / `.Report`；预览必须去掉 `-nographics`。
- 重跑 `WorldPipeline` / `SectrPipeline` / `LakeCarve` 后湖会重建，必须重跑 `Apply`。
- `Apply` 会覆盖 `Assets/WaterPlants/{Materials,Meshes}`；`Shaders/` 与 `Source/` 保留。
- `Remove` 删除根节点与生成材质/网格，保留源资产与 shader。

2026-11-21 Editor 实测：`Apply` PASS（193 renderer / 4 唯一网格 / 371845 tris / 0 共享材质异常）；`Report` PASS；`Preview` PASS（开/关像素差 **9.1%**；浮萍遮罩内 30 帧 A/B 平均差异 **66.5/通道**，含水面与光照随时间的变化，未做静态对照）。截图见 `Logs/Water/duckweed-preview-*.png`（wide/on/shore/anim-a/anim-b/off）。

## 6. 已知限制

- 整株 bob/漂移/倾斜是程序化近似，**不跟随 Crest 浪高**；浪峰时部分叶片可能被水面盖住，低机位可见。
- 使用的是睡莲而非小型浮萍；单株带花与水下茎，近景会看到细茎。
- 单株约 1.9k 三角形、未做 LOD；193 株合计约 372k，远景仍按完整网格提交。
- 没有阴影投射与接触阴影；水面反射中是正确可见的。
- 只在中心湖（`LakeCarve`）生效；其他水体或分块礁区需要重新做分布。
