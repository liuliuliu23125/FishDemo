# 工程待办与待决策

> 记录当前工程的实际差距与未决事项。完成后从本文件移除。

## 1. 工程配置差距

| # | 问题 | 出处 | 处理 |
|---|---|---|---|
| 1 | Android 最低 SDK 22 | `AndroidMinSdkVersion: 22` | 建议 26+ |
| 2 | ~~增量 GC 开关~~ | **已关闭（2026-11-17）**：`gcIncremental: 0` | 按决策关闭增量 GC；**不接管 GCMode**。非增量触发是一次完整停顿，因此必须继续压缩托管堆（草已移出并流式），见 [terrain_streaming](terrain_streaming.md) §12.5 |
| 3 | Android 默认画质 Medium | `m_PerPlatformDefaultQuality.Android: 2` | 改 High/Ultra |
| 4 | 无 Input System / Cinemachine | `manifest.json` | 钓鱼玩法需要 |
| 5 | GPU Baker 未 vendor | 包已下架 | 立即 vendor 进仓库 |

**已关闭/已过时**（不要重复处理）：
- URP 已使用 `UniversalRenderer`（`UniversalRP.asset` 的 renderer guid 指向 `Assets/Settings/UniversalRenderer.asset`）；
- `.gitattributes` + LFS 已配置；Entities 1.4.8 / Entities Graphics 1.4.21、Newtonsoft 3.2.2 已在 manifest；
- Android 图形 API 已仅 Vulkan（`m_APIs=15000000`, `m_Automatic=0`）；
- 2D feature 包不在 manifest；
- **静态合批已关闭**：`BatchingSetup.FixAll` 写入 Android/Standalone 0/0，`GtsApplyMeshToChunks` 不再给 subTile 标 static；
- **深度图已改为 1（2026-11-15，Enviro 体积雾/云的硬依赖）**；不透明图仍为 0。
  这不再只是“接入前要改”的提醒，而是当前配置。若将来移除大气系统，可改回 0 省一次 depth prepass。
- **大气系统已落地**：Enviro 3.3.2b（昼夜 / 天气 / 体积雾 / 体积光），见
  [atmosphere](atmosphere.md) §0。VFM2 / VLB2 / VL2 未安装，等有局部雾或灯对象再接。

**已完成**：YooAsset 3.0.7 嵌入式接入（编译通过，`"source": "embedded"`）。

## 2. 功能缺口（代码层面的待办）

| 项 | 说明 |
|---|---|
| Crest 源码补丁 | `DepthLod.TerrainDepthInput` 已加“只烘湖区 Include WaterBody AABB 内 Terrain”的过滤；`WaterReflections` 已加非方形反射贴图（512×256，`ResolutionY`）与 `CaptureDepth`（关闭反射相机深度拷贝）。升级 Crest 时必须重打，否则深度会多 dispatch、反射会退回方形并重新请求 CopyDepth |
| URP 反射相机补丁 | `UniversalRenderPipeline.RenderSingleCameraInternal` 已改为对 `CameraType.Reflection` 也读取 `UniversalAdditionalCameraData`。升级 URP 必须重打，否则反射相机忽略 RendererIndex/CaptureDepth/renderShadows/post，退回 CopyDepth + FinalBlit |
| SECTR 流式补丁 | `SECTR_Chunk.cs` / `SECTR_RegionLoader.cs` 已改（激活节流、加载/卸载限流、缓存接管）。升级/重装 SECTR 会覆盖，原始副本在 `Tools/sectr-patches/`；见 [terrain_streaming](terrain_streaming.md) §12 |
| YooAsset 启动入口 | `YooAssetInitializer.InitializeAsync()` 目前**无任何调用方**，资源层尚未接线 |
| 模拟构建产物 | 菜单 `YooAsset/FishDemo/执行模拟构建并注入路径` 未实际执行过 |
| NPC 动画与湖边人群 | GPU ECS Animation Baker 已接 **200 人整圈**（Ch19/Ch14/Ch09/Ch46 四种角色，4× 大小；r=191–205，Y=267.06–301.84；Forward 渲染 PASS）；121/200 在高于水位 10m 以上的陡坎。下一步决定“低岸弧段加密”或“改 `LakeCarve` 造岸坡”；真机 30fps 待测；见 [gpu_ecs_animation_baker](gpu_ecs_animation_baker.md) §9 |
| Enviro 雾的固有全屏 pass | 每帧 1 次全屏 color copy + 1 次合成；体积云开启后还会加全分辨率 temp。要优化必须改 Enviro 并打 `[FishDemo]` 标记，见 [atmosphere](atmosphere.md) §0.7 |

## 3. 待决策

### 3.1 是否长期锁定 Unity 2022.3

| 选择 | 收益 | 代价 |
|---|---|---|
| 锁定 2022.3 | 现有第三方包全部验证过 | 需自研 BRG、体积雾、水体 |
| 升级 Unity 6 | 白拿 RenderGraph / GPUResidentDrawer / 内置水体（Forward+ 在 URP 14.0.12 已有，工程当前未启用） | 全部第三方包需重新验证，含已下架的 GPU Baker |

**当前建议**：先按 2022.3 推进，渲染抽象层留迁移口。demo 阶段换引擎收益低于把内容做出来。

**Forward+ 决策点**：**当前决定不切**，保持 Forward。200 人 VAT 已在 Forward 下渲染 PASS（俯瞰 draw=4/rendered=95、弧段 draw=8/rendered=34；控制台仍有 Forward+ warning）；真机性能待验。Forward+ 不再作为接入前提，见 [gpu_ecs_animation_baker](gpu_ecs_animation_baker.md) §1.2。

### 3.2 `InstallCleanupMode`

默认 `None`，覆盖安装后旧缓存残留。可选 `ClearAllCacheFiles` / `ClearAllBundleFiles` / `ClearAllManifestFiles`。需要权衡启动时间与磁盘占用。

### 3.3 iQOO 16 实机数据

性能预算表目前全是估值，需实机 Profile 后校准。
