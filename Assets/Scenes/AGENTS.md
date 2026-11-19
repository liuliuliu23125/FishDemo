# Assets/Scenes 规约

> 改场景或重跑世界生成前建议一次读完。

## 1. World.unity 是产物，不是手搭场景

`Assets/Scenes/World.unity` 及其 `World/` 子目录全部由批处理脚本生成：

```
Assets/Scenes/World.unity                    主场景（23 个根对象）
Assets/Scenes/World/Chunks/*.unity           36 个 chunk 场景（按批次命名）
Assets/Scenes/World/TerrainSplits/*.asset    36 份 chunk TerrainData
Assets/Scenes/World/GrassData/*.bytes        36 份 BRG 草烘焙数据（132.8 MiB）
```

**手改会在下次重跑时被覆盖。** 要改结果，改脚本参数后重跑，或改完立即提交并接受"重跑会丢"的代价。

**重跑 WorldPipeline + SectrPipeline 后，这些手动步骤必须重做**（否则渲染结果全错）：

| 步骤 | 工具 | 漏了会怎样 |
|---|---|---|
| 移动档参数 | `TerrainChunkTuning.ApplyMobile` | 退回 PC 档，草可见距离 150m、密 0.5 |
| 关 Terrain 草 | `TerrainChunkTuning.DisableDetail` | 与 BRG 草重复绘制（同一处画两遍） |
| GTS mesh | `GtsConvertOne` → `GtsApplyMeshToChunks` → `GtsFixMeshRendering` | 地表贴图/分层错乱 |
| BRG 草接入 | `GrassBaker.RunAll` → `GrassSceneSetupM2` → `DetailBrgSetup` | 场景里没有 `GrassBRG (M1)` 或 LOD0 退回兜底 mesh |
| Pegasus 巡游 | 菜单 `FishDemo/World/② 布置 Pegasus 环绕巡游` | 没有巡游路线 / POI 悬空 |
| **中心湖** | `LakeCarve.Apply` → `LakeVegetationClear.Apply` → 重转 GTS（受影响 2 chunk） → `LakeCrestSetup.Apply` | 湖没水 / 水边长树 / 地形没凹陷 |
| **大气** | `AtmosphereSetup.Apply`（首次/干净导入先 `EnsureDefines`） | 没有天空/雾/天气；旧 `Directional Light` 与 Enviro 的光形成双主光 |
| **火把 / 后处理** | `LightingSetup.Apply`（必须在 `AtmosphereSetup.Apply` **之后**） | 没有 `Lakeside Torches` / `Post Processing`；无 ACES/Bloom；相机不跑后处理 |
| **湖边人群** | `LakesideCrowdSetup.ApplyOne`（1 人）/ `ApplyThirty`（30 人）/ `ApplyCrowd`（200 人，当前） | World 里没有 SubScene / 没有角色 |

**湖边人群**：`Assets/Scenes/LakesideCrowd.unity` 是 authoring scene，`World.unity` 里的
`Lakeside Crowd (SubScene)` 引用它（`AutoLoadScene=1`）。当前是 **200 人整圈**（1.8° 间隔，4 种角色，
4× 大小，r=191–205，Y=267.06–301.84）。`LakeCarve` 在 r>190 直接恢复原始地形：南岸约 283.8m；
**121/200 个点位高于水位 10m 以上**，最南侧约 +37.8m。要全部贴水站立需改低岸弧段密集布点或改湖盆造岸坡。详见
[gpu_ecs_animation_baker](../../Docs/gpu_ecs_animation_baker.md) §9。

生成链路与参数见 [world_implementation](../../Docs/world_implementation.md)。

## 2. 批次命名规则

chunk / TerrainData 文件名形如：

```
World_SECTR Terrain_0_0-20261109 - 162007 0-1-0 Sector.unity
              └ tile(2D) ┘ └─ 批次 token ─┘ └sector(3D)┘
```

- **批次 token**（`YYYYMMDD - HHMMSS`）来自 Gaia session，每次重跑都变。
- 只有主场景与 Build Settings 引用的那一批是"活跃批次"。其余是孤儿。
- 当前：磁盘 72 个 chunk（162007 与 140021 各 36），**只有 162007 在用**。
- 清理用 `-executeMethod CleanupOrphanAssets.Run`，它按活跃 chunk 的 `NodeName` 反推批次，不会盲删。

## 3. 主场景构成

| 根对象 | 说明 |
|---|---|
| `Main Camera` | 挂 `SECTR_RegionLoader`（加载中心）；Pegasus 巡游也用它作 target；`m_ClearFlags=1`（Skybox） |
| `Lake Orbit Camera` | 环湖轨道相机 + `LakeCameraRig`（C 切相机 / F 飞行），见 [water](../../Docs/water.md) §5 |
| ~~`Gaia Water`~~ | **当前场景已无此节点**（YAML 里没有任何名字含 `Water` 的 GameObject）；水面由 `Crest Lake` 提供 |
| **`Crest Lake`** | Crest 水面 + `WaterBody` + `ClipLodInput`（湖盆裁剪）。位置 `(192,264,96)` |
| `Terrain_x_x` ×9 | 只有 Sector×4 + Portal×4，**无 Terrain 组件**（地形在 chunk 里） |
| `Gaia Terrains` / `Gaia Runtime` / `Gaia Tools` | Gaia 运行时容器 |
| `Alpine Meadow-*` ×10 | 撒点器，挂着空转的 Gaia `TerrainLoader` |
| **`GrassBRG (M1)`** | 挂 `GrassBRGManager`（`FishDemo.Rendering`）。锚点 `(0, 329.6, -1151.8)`，`_grassDataDir=Assets/Scenes/World/GrassData`，`_instanceCount=4292694`（烘焙总数 4,350,225，湖区清草后减少） |
| **`Pegasus Tour`** | 挂 `PegasusManager`（16 个 POI 是它的子对象，不入根列表）+ `PegasusTourController` |
| **`Enviro 3`** | Enviro 3.3.2b 运行时根：`EnviroManager` + Sun/Moon 方向光 + Effects/Audio/粒子层级。**预制体实例化后由 Enviro 在编辑器 OnEnable 里自行 Unpack**，场景里是普通层级，不要再按 prefab override 处理 |
| **`Atmosphere`** | 挂 `AtmosphereController`（`FishDemo.Gameplay`）：质量档、时间流速、天气轮换、草风桥接、HUD、夜景曝光 |
| **`Lakeside Torches`** | 12 支环湖火把 + `TorchRingController`（`FishDemo.Gameplay`）：默认亮 1 支，`L` 循环 0/1/2/4/6，硬上限 6 |
| **`Post Processing`** | 全局 Volume，引用 `Assets/Settings/PostProcess/FishDemo Global Volume.asset`（ACES / HDR grading / Bloom / 调色；无 DOF） |
| ~~`Directional Light`~~ | **Light 组件 `m_Enabled=0`**（2026-11-15）：Enviro 自带 Sun/Moon 方向光。保留对象便于 `AtmosphereSetup.Remove` 回滚；不要再启用 |

**天空盒**：`RenderSettings.m_SkyboxMaterial` 是 Enviro/Skybox，`m_AmbientMode=0`（Skybox）。
Enviro 运行时也会写 `RenderSettings.skybox`。**不要加第二个天空盒或换成默认天空盒**；
清空会让环境光变黑。详见 [rendering_rules](../../Docs/rendering_rules.md) §1.1。

**`GrassBRG (M1)` 名字里的 M1 是历史遗留**，它现在跑的是全量烘焙数据
（36 chunk；烘焙 435 万、场景当前 429 万），不是 M1 的 1000 个方阵实例。改名要连同
`GrassSceneSetupM2` / `DetailBrgSetup` 里的 `NodeName` 常量一起改。

**不要**往主场景加地形或静态大物件 —— 它们不会被 SECTR 分块，会常驻内存。可流式的内容应放进 chunk 或走资源层。

**草已改为随 SECTR 流式**（`Gameplay/SectrGrassStreamer`，2026-11-17）：
chunk 加载才读对应 `.bytes`，卸载即释放，驻留上限 24 块。
编辑器的 `GrassSceneSetupM2` 等工具仍会全量读盘（约 320MB），那是工具行为，不是运行时
（[grass_brg_progress](../../Docs/grass_brg_progress.md) §4.5 / §6）。

## 4. Build Settings

- 必须包含 `World.unity` + 36 个活跃 chunk 场景（共 37 个）。
- 由 `FinalIntegrity.Run` 整体重建，不要手工维护。
- SECTR 通过 `SceneManager.LoadSceneAsync(ScenePath)` 加载，**场景必须登记在 Build Settings**，否则运行时静默失败。

## 5. 其他场景

| 场景 | 说明 |
|---|---|
| `SampleScene.unity` | 3D 模板示例，由 `UrpPipelineSetup` 重建。不参与世界生成 |

## 6. 相关文档

| 文档 | 何时读 |
|---|---|
| [world_implementation](../../Docs/world_implementation.md) | 重跑生成、排查 chunk、查已知缺陷 |
| [grass_brg_progress](../../Docs/grass_brg_progress.md) | 改草数据/场景接入、排查草消失 |
| [../Editor/AGENTS.md](../Editor/AGENTS.md) | 切块/收尾/验证脚本的约定 |
| [rendering_rules](../../Docs/rendering_rules.md) | 写任何渲染代码前（管线铁律 / Copy / Blit） |
| [pooling_lod](../../Docs/pooling_lod.md) | chunk 加载与缓存策略 |
