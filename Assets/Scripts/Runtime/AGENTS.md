# Assets/Scripts/Runtime 规约

> 三个子目录，**两套程序集**：`Resources/`（`FishDemo.Resources`）、
> `Rendering/`（`FishDemo.Rendering`）、`Gameplay/`（**无 asmdef**，编进 `Assembly-CSharp`）。
> 建议一次读完。

## 1. 目录与程序集

| 目录 | 程序集 | 命名空间 | 说明 |
|---|---|---|---|
| `Resources/` | `FishDemo.Resources` | `FishDemo.AssetManagement` | YooAsset 初始化 + 句柄封装 |
| `Rendering/` | `FishDemo.Rendering` | `FishDemo.Rendering` | 草 BRG 运行时，`allowUnsafeCode=true` |
| `Gameplay/` | **无** → `Assembly-CSharp` | `FishDemo.Gameplay` | Pegasus 巡游 + 环湖轨道相机与切换 rig + 大气控制器 |

**`Gameplay/` 不能加 asmdef**：`PegasusTourController.cs` 直接 `using Pegasus`，
`LakeCameraRig.cs` 同时 `using Pegasus` 与 `using WaveHarmonic.Crest`，同理；
Pegasus 没有 asmdef、编在 `Assembly-CSharp`。asmdef 程序集引用不到默认程序集。
同 `Assets/GTSRuntime/` 的处理方式。

`Streaming/` 仍**不存在**。

## 2. Resources（YooAsset）：已实现，仍未接线

| 文件 | 状态 |
|---|---|
| `Resources/YooAssetInitializer.cs` | 初始化三模式（编辑器模拟 / 离线 / 联机）、清单加载、下载参数 |
| `Resources/AssetLoader.cs` | 句柄获取/释放，返回 `AssetLease<T>`（资源 + 句柄成对） |

**全工程无调用方**：`InitializeAsync()` 没有被任何脚本调用；实际资源加载走 SECTR 的 `SceneManager.LoadSceneAsync`。

接线的前置条件（缺一不可）：

1. 菜单 `YooAsset/FishDemo/初始化资源收集配置` —— 生成 `Assets/BundleCollectorSetting.asset`。
2. 菜单 `YooAsset/FishDemo/执行模拟构建并注入路径` —— 否则 `EditorSimulatePackageRoot` 为空，初始化会抛 `InvalidOperationException`。
3. 调用方必须在进入依赖资源的场景**之前** `await YooAssetInitializer.InitializeAsync()`。

**地址规则必须是 `AddressByFolderAndFileName`**（`YooAssetSetup.cs:96`）。
`Assets/Scenes` 下场景与烘焙数据同名（`World/Chunks/*Sector.unity` 与 `World/GrassData/*Sector.bytes`），
纯文件名规则会让两者撞 Address，收集阶段直接抛 `Address already exists`（`BundleCollector.cs:226`）。

## 2.5 Gameplay：相机与巡游（无 asmdef）

| 文件 | 职责 |
|---|---|
| `Gameplay/PegasusTourController.cs` | 原 F 键巡游/手动飞行切换。**已被 `LakeCameraRig` 接管 F 键后禁用**（保留不删，可随时改回） |
| `Gameplay/LakeOrbitCamera.cs` | 环湖轨道运动：半径 300m / **高度跟随地形**（地表+18m，且不低于水位+26m，指数平滑）/ 120s 一圈 / 俯仰 12°±3°；`Paused` 让出 transform |
| `Gameplay/LakeCameraRig.cs` | **C** 切相机（巡游 ⇄ 轨道）+ **F** 自由飞行；同步 Crest `Viewer`、SECTR loader、MainCamera tag、**Enviro `ChangeCamera`** |
| `Gameplay/AtmosphereController.cs` | 大气运行时配置：质量档、时间流速、天气轮换、草风桥接、水下雾开关；**左上角 HUD（自上而下：键盘操作提示标题 → 昼夜/天气/流速 → 按键说明）**；热键 `1-5` 天气 / `T` 暂停时间 / `[` `]` ±1 小时 / `\` 切流速。宽度由 `ComputeHudWidth` 夹取，逐行测量（`MeasureBlock`） |
| `Gameplay/SectrChunkCache.cs` | SECTR chunk 的 TTL + LRU 保留；接管 RegionLoader 的引用，切相机/边界回走时复用，淘汰才真正卸载 |
| `Gameplay/SectrStreamingRuntime.cs` | 流式运行时宿主：驱动缓存 Tick；等 `UnloadSceneAsync` 完成后按冷却做异步 `UnloadUnusedAssets`；低内存清草驻留 + buffer 池 + chunk 缓存 |
| `Gameplay/SectrGrassStreamer.cs` | 草数据跟随 SECTR chunk：`IsLoaded` 才读该 chunk 的 `.bytes`，卸载即释放；每次最多加/卸一个，驻留数硬上限 24。`BeforeSceneLoad` 里调 `GrassBRGManager.RequestStreaming()` |

三条容易踩的：
1. **切相机必须同步四样东西**：`WaterRenderer.Viewer`（水面 clipmap 以它为中心）、
   当前相机的 `SECTR_RegionLoader`（按自身 transform 加载 chunk）、`Camera.main` tag、
   以及 `EnviroManager.Camera`（Enviro 的 RenderFeature 只对 manager.Camera/Cameras 生效）。
   新增"跟着相机走的状态"必须加进 `LakeCameraRig.Apply()`。
2. **F 键飞行前必须暂停轨道脚本**（`LakeOrbitCamera.Paused`）：它在 `LateUpdate` 写 transform，
   比飞行的 `Update` 更晚，不暂停会把飞行位移整帧覆盖。
3. **组件级开关，不要 `SetActive(false)`**：每台相机自带 `AudioListener`，
   禁 GameObject 会触发 "no audio listeners"。

验收：`LakeOrbitVerify.Run`（可 `-nographics`，9 项断言）。

## 3. Rendering（草 BRG）

| 文件 | 职责 |
|---|---|
| `Rendering/GrassBRGManager.cs` | 实例构建、32m 空间分块、多 batch（16MB/批）、Burst 剔除 Job、3 级距离 LOD、按类型分配材质 |
| `Rendering/GrassInstance.cs` | 32B 磁盘格式 + `GrassChunkData.Read`（魔数/版本/大小校验） |

**它属于 Render Data 层**，可以碰 `BatchRendererGroup` / `GraphicsBuffer`；
Gameplay 层不得直接操作 `CommandBuffer` / `Graphics.*`（[rendering_rules](../../../Docs/rendering_rules.md) §1）。

**即使在这里也不得新增每帧全屏 Blit / ping-pong。** 新增 pass 先按
[rendering_rules](../../../Docs/rendering_rules.md) §1 交性能账：pass 数、全屏 RT 数、
blit 数、依赖哪张 depth/opaque、能否复用已有上下文纹理。

三条最容易踩的实现约束（细节见 [grass_brg_progress](../../../Docs/grass_brg_progress.md) §5）：

1. `AddBatch` 只接受 **raw buffer**（`Target.Raw` + stride 4 + count = 字节数/4），`Structured` 直接失败。
2. 单个 batch 的 raw buffer 上限 16MB → **32B/实例 = 524,288 实例/批**。超限表现是「全黑且无任何报错」。
3. `OnPerformCulling` 的输出数组必须 `UnsafeUtility.Malloc(..., Allocator.TempJob)`，由引擎释放。
   Job 内**不能** `Dispose()` native container，字段要加 `[DeallocateOnJobCompletion]`。

### 3.1 材质按「类型 × LOD」分配（花显色的落点）

`BatchDrawCommand.materialID` 可以逐条命令指定，所以：

| LOD | 几何 | 材质 |
|---|---|---|
| LOD0 | 各类型原型 mesh | **每类型一个**（`_typeMaterials[t]`） |
| LOD1 | Gaia `PW_DetailCrossPlanes`（每类型一份定制副本） | 同上，**三层共用** |
| LOD2 | Gaia `PW_DetailQuad`（同上） | 同上 |

三层共用同一份类型材质（2026-11-12 起）。**曾经只有 LOD0 用带图材质、LOD1/2 走纯色，
导致 99.8% 的草没有花色**——改这里时务必确认三层都绑了 `_USE_ALBEDO_TEX`。

Gaia billboard 的 UV 是「0..1 图集平铺」语义，所以 `BuildScaledBillboard` 会把
UV 重写到该类型的精灵格、并把顶点缩放到原型尺寸（材质只有一份 `_TypeScaleBase`）。

- **tint / 类型尺寸走材质属性而不是 per-instance**：batch 已按类型分段，每类型一个材质
  即可表达「不同 Type 不同 tint/尺寸」，GPU buffer 保持 32B/实例、不增加上传带宽。
- 磁盘 `GrassInstance.Color`（RGBA8）**当前恒为白色且未上传 GPU**（`UploadInstanceData`
  只传 2 段）。要逐实例染色才需升级为第三段。
- `shader_feature` 的开关是 **keyword**：`new Material` 后必须 `EnableKeyword("_USE_ALBEDO_TEX")`，
  只 `SetFloat` 不会切换变体（表现为花仍是绿的、无 alpha 裁剪，静默失效）。
- 竖直面片**不能用地表法线做 Lambert**（实例法线朝上，低太阳角下会全黑），
  要朝相机混合 + 包裹光照，见 `GrassBRG.shader` 的 Frag。
- 不要开 `AlphaToMask`：工程 MSAA=1，alpha-to-coverage 会退化并把草切成碎屑。

**数据加载是临时通路**：`TryAutoLoadChunkData()` 用 `System.IO.File.ReadAllBytes` 直读
`Assets/Scenes/World/GrassData/*.bytes`（编辑器/开发期）。打包后 `Assets/` 不存在，
会回退到 M1 合成方阵。正式版必须改成经 YooAsset。

**Play Mode 走流式、不进托管堆**：`SectrGrassStreamer` 在 `BeforeSceneLoad` 置
`GrassBRGManager.RequestStreaming()`，`Initialize` 因此跳过全量读盘，等 chunk 增量加载。
流式路径用 `GrassChunkFile.Read/SortIntoBlocks`（NativeArray + `MemCpy`），
只保留 `positions`（12B/实例），GPU 侧每 chunk 一组 raw buffer。
**不要再往托管 `GrassInstance[]` 里加数据**：编辑器工具仍走 `GrassChunkData`（托管），
那是给 `GrassBaker/GrassMultiChunkVerify` 等一次性脚本用的，不要接进 Play Mode。

## 4. 必须遵守的约束

详细依据见 [YooAsset 模块规约](../../../Packages/com.tuyoogame.yooasset/AGENTS.md)，这里只列最容易犯的：

- **禁止 `Resources.Load`**，也禁止业务层直接调 `Package.LoadAssetAsync`，必须经 `AssetLoader`。
- **句柄成对释放**：`AssetLoader.LoadAsync` 返回 `AssetLease<T>`，用完 `Release(location, handle)`。禁止复用同一句柄、禁止重复 `Release`。
- **清单必须先取版本号**：`RequestPackageVersionAsync()` 之后才能 `LoadPackageManifestAsync`，离线也一样。
- **`Shutdown()` 前必须 `ReleaseAll()`**：`Destroy()` 走 `AssetBundle.Unload(true)`，不检查引用计数。

## 5. 相关文档

| 文档 | 何时读 |
|---|---|
| [YooAsset 模块规约](../../../Packages/com.tuyoogame.yooasset/AGENTS.md) | 改资源层代码前（必读） |
| [grass_brg_progress](../../../Docs/grass_brg_progress.md) | 改草 BRG 运行时、排查「草消失」 |
| [grass_brg_plan](../../../Docs/grass_brg_plan.md) | 要理解 BRG 数据/file 格式的设计取舍 |
| [yooasset_practices](../../../Docs/yooasset_practices.md) | 遇到初始化失败、句柄失效、卸载不掉、要查参数依据 |
| [yooasset_3.0.7](../../../Docs/yooasset_3.0.7.md) | 要理解内部链路；改包源码 |
| [pooling_lod](../../../Docs/pooling_lod.md) | 设计缓存、池、chunk 加载策略 |
