# Assets/WaterPlants 规约（水面睡莲/浮萍）

> 从 `Reference/Nature Package - Swamp,Forest Environment v1.0.unitypackage` 提取的水面植物子集 + 自研 URP 14 shader。
> 布置、性能账与已知限制见 [duckweed](../../Docs/duckweed.md)。

## 1. 目录

| 路径 | 内容 |
|---|---|
| `Source/SwampForest/Models/*.fbx` | 4 个睡莲变体（Pink01/Pink02/White03/White04）；每个 1.5k~1.8k 顶点 |
| `Source/SwampForest/Models/Materials/SwampLily01.mat` | 包内原 Standard 材质；保留以解析 FBX 的外部材质引用，**未被场景渲染** |
| `Source/SwampForest/Textures/` | `SwampLily01.tga`（diffuse+alpha）、`SwampLily01_N.png`（法线），均已降采样到 1024 |
| `Shaders/WaterSurfacePlant.shader` | 水面植物前向 shader：alpha clip、Cull Off、主光 + SH；每株按自身枢轴做 bob/漂移/倾斜/偏航 |
| `Materials/M_SwampLily.mat` | **生成物**；唯一共享材质，`enableInstancing=true` |
| `Meshes/Lily_*.asset` | **生成物**；4 个变体合并网格（各 1.5k~1.8k 顶点），LFS |

## 2. 硬约束

- 只提交提取出的 4 个 FBX、2 张贴图与包内原 Standard 占位材质；**不要**把 1.7GB 的 Swamp/Forest 包整体导入。
- 源贴图 diffuse 4096、normal 2048，均已降到 **1024**；导入设置固定 `1024 / ASTC_8x8`，法线走 `NormalMap`、`Clamp`，diffuse 保留 alpha 并开 `alphaIsTransparency`。
- `Materials/` 与 `Meshes/` 由 `LakeDuckweedSetup.Apply` 覆盖重建；不要手改后不重跑 setup。
- 所有实例共享 **1 个材质 + 4 个变体网格**，材质开 `enableInstancing`。给单个实例换材质/换网格会破坏合批；实际走 instancing 还是 SRP Batcher 以 Frame Debugger 为准。
- 位置唯一来源是 `LakeCarve`（湖心/半径/水位）；当前 y = `WaterLevel + 0.06`，**不跟随 Crest 浪高**。改水位或重跑世界后必须重新 `Apply`。
- 渲染器关闭阴影投射（shader 无 ShadowCaster pass）；不要打开 Cast Shadows。
- shader 零注释；只用 URP 14 前向 include，不新增 pass / RT / blit，不改 Crest。

## 3. 生成与验收

```
LakeDuckweedSetup.Apply
  → LakeDuckweedSetup.Report
  → LakeDuckweedPreview.Run        (真实图形设备)
```

- 菜单：`FishDemo/Water/重建水面浮萍`、`校验水面浮萍`、`移除水面浮萍`。
- 批处理：`-executeMethod LakeDuckweedSetup.Apply` / `.Report`；预览必须去掉 `-nographics`。
- `Remove` 会删除 `Lake Duckweed` 根节点、生成材质与网格；`Source/` 与 `Shaders/` 保留。
- `LilyModelProbe.Run` 只读输出 4 个源 FBX 的顶点/三角形/包围盒，改源模型后先跑它。
- 预览输出：`Logs/Water/duckweed-preview-wide.png`、`-on.png`、`-shore.png`、`-anim-a.png`、`-anim-b.png`、`-off.png`；当前 on/off 像素差 9.1%，浮萍遮罩内 A/B 平均差异 66.5（未与关闭动画的静态对照比较）。
- 动画参数：`_WaveStrength=0.07`、`_WaveSpeed=0.85`、`_DriftStrength=0.06`、`_TiltStrength=3°`、`_YawStrength=2°`；相位取每株世界枢轴，不用 MPB。
