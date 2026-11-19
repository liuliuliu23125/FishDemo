# 渲染与批处理规约

> 写任何渲染代码（pass / RendererFeature / shader / 后处理）前必读。预算与验收见 [performance_budget](performance_budget.md)。

## 1. 管线铁律（2026-11-17）

### 1.1 天空盒只有 Enviro 一个来源

- 场景 `m_SkyboxMaterial` 是内联的 `Enviro/Skybox`；`m_AmbientMode=0`（Skybox）、`Main Camera.m_ClearFlags=1`。运行时 `EnviroSkyModule.SetupSkybox()` 还会写 `RenderSettings.skybox`（`EnviroSkyModule.cs:137-148`）。
- **默认管线天空盒已经不在用，无需再删**。不要再加第二个天空盒/天空球/天空 pass；不要把 `m_SkyboxMaterial` 置空，否则环境光一起黑。

### 1.2 CopyColor / CopyDepth 不是默认项

| 纹理 | 触发 | 当前 | 消费者 |
|---|---|---|---|
| `_CameraOpaqueTexture` | `m_RequireOpaqueTexture=1` 或 `ConfigureInput(Opaque)` | **0** | 无 |
| `_CameraDepthTexture` | `m_RequireDepthTexture=1` 或 `ConfigureInput(Depth)` | **1** | **Enviro** 体积雾/云（`EnviroURPRenderPass.cs:83`） |

Crest 的 `CopyTargetsRenderPass` 受 `[Conditional(k_Refraction)]` 与 `WriteToColor/DepthTexture` 控制；SimpleTransparency 下 Crest 自身是 0/0。当前管线里的 1 次 CopyDepth 是 Enviro 的成本。

**「两套深度」不是重复计算**：

| 数据 | 生产者 | 坐标 / 用途 | 消费者 |
|---|---|---|---|
| `_CameraDepthTexture`（屏幕视深度） | URP `CopyDepthPass`，每相机每帧 1 次全屏 copy | 屏幕 uv → 相机视深度；回答「沿这条射线走多远终止」 | Enviro 雾 / 体积雾 / 体积云 |
| Crest `DepthLod`（世界高度场） | `DepthTexture.compute` 读 Terrain heightmap；128²×5、R16F，约 160 KiB；不是屏幕深度 | 世界 XZ → 湖床高度，水深 = 水位 − 床高 | Crest 水面 / 水下 / 水深查询 |

- 两者不可互推：屏幕深度只在可见画面内，含所有不透明物且依赖相机；高度场覆盖全湖、相机无关，但不含遮挡与视距。
- `d_Crest_SimpleTransparency` 下 Crest 调用 `ConfigureInput(None)`，既不产生也不读取 `_CameraDepthTexture`；主画面全屏深度只有 Enviro 的 1 次，Crest 的 compute 不是第二份拷贝。
- 时序上 CopyDepth 固定在 `AfterRenderingTransparents - 1`（499），水面（transparent，450）画完才可用，不能改成水读屏幕深度；也不要反过来用高度场替代 Enviro 深度。

**平面反射相机**：Crest 每帧最多 1 次 512×256 渲染（Above only），`cullingMask=Default`。工程给反射相机单独用 `Assets/Settings/ReflectionRenderer.asset`（renderer index 1）：**无 Renderer Feature + `IntermediateTextureMode=Auto`**，因此直接渲染到目标 RT，**没有 intermediate color，也没有 FinalBlit**。`CaptureDepth=false`，反射相机不请求 `_CameraDepthTexture`，**没有反射相机的 CopyDepth**；`Reflection.hlsl` 的漏光抑制不生效（斜裁剪已挡住水下几何）。

> **URP 必须打补丁**：`UniversalRenderPipeline.RenderSingleCameraInternal` 默认只对 `CameraType.Game` 读取 `UniversalAdditionalCameraData`；反射相机是 `CameraType.Reflection`，不打补丁时 RendererIndex / requiresDepth / renderShadows / post 全部被忽略，退回默认 renderer，必然出现 CopyDepth + FinalBlit。补丁见 [URP AGENTS](../Packages/com.unity.render-pipelines.universal/AGENTS.md) §3。

打完补丁后的反射 pass 队列（2026-11-19 实测）：

```text
MainLightShadowCasterPass DrawObjectsPass DrawSkyboxPass DrawObjectsPass InvokeOnRenderObjectCallbackPass
```

- `MainLightShadowCasterPass` 是 URP 的空 shadowmap 绑定（`stripShadowsOffVariants=true`），没有 shadow caster 绘制；保留它避免 shader 采样到主相机的错误阴影。
- 没有 `CopyDepthPass`，没有 `FinalBlitPass`；`createColor=false`、`createDepth=false`、`activeColor=CameraTarget`。

`PlanarReflectionController` 用视锥 ∩ `WaterBody` AABB 控制开关；水面看不到时 0 成本。反射相机走无 Feature 的 `ReflectionRenderer`（index 1），Enviro 不参与；不要用 `CanRenderOnCamera` 排除 `CameraType.Reflection`（Enviro 全局反射探针也是该类型）。草在 layer 6，`PlanarReflectionController` 把反射相机登记到 `GrassBRGManager.ReflectionCamera`，BRG 剔除回调按相机位置（1cm 容差）比对后跳过（不要用 begin/end 计数：Crest 走 `RenderSingleCamera`，事件不配对）。

**CopyDepth 能否优化（结论：当前保留 1 次）**。URP 14 的路径是 `requiresDepthCopyPass = !requiresDepthPrepass && requiresDepthTexture`（`UniversalRenderer.cs:860-861`），然后入队一次 `CopyDepthPass`（`:1151-1154`）；该 pass 用全屏 `Blitter.BlitTexture` 写深度（`CopyDepthPass.cs:184`）。本工程 MSAA=1、Vulkan 支持 depth copy，所以 `CanCopyDepth` 为真（`:1622-1635`），不会自动走 prepass。

> **`CrestRenderProbe` 的 0/0 不能当反证**：它在 `beginCameraRendering` 抓队列，早于 URP `renderer.Setup()`；URP 内部的 CopyDepthPass 是 Setup 里入队的，工具看不到。要确认 URP 的 copy，用 Frame Debugger。

可选方案及取舍：

| 方案 | 效果 | 结论 |
|---|---|---|
| `m_DepthPrimingMode=Forced` / `CopyDepthMode=ForcePrepass` | 去掉 CopyDepth，改为**重绘一遍不透明几何**的 depth prepass | **不要**。本工程地形/树等不透明几何重绘比一次深度拷贝贵；只有高 overdraw 场景才可能回本 |
| 去掉 `m_RequireDepthTexture`，只靠 Enviro 的 `ConfigureInput(Depth)` | 只给 Enviro 相机做 copy，其他相机不付 | 运行时只有一台相机启用（`LakeCameraRig` 组件级切换），**收益≈0**；还牵动 `AtmosphereSetup`/`CrestPerformanceMode` 的断言，暂不做 |
| 降分辨率深度 / 自建深度 RT | 省带宽 | 要改 Enviro shader/pass，升级即丢，**demo 不做** |
| `CopyDepthMode` 改 AfterOpaques | **无效**：Enviro 的 `ConfigureInput` 会把 copy 固定到 `AfterRenderingTransparents - 1`（`UniversalRenderer.cs:715-720`），与 `m_CopyDepthMode` 无关 | 不改 |

执行顺序：grep `_CameraOpaqueTexture|_CameraDepthTexture|SampleSceneDepth|SampleSceneColor` → 看 `ConfigureInput` → 能共用就共用 → 能直接读 attachment 就不 copy → 改完重跑 `CrestRenderProbe` / Frame Debugger。**禁止“先开着以后可能用”**。

> 带宽量级：1080p 深度拷贝约 8MB 读 + 8MB 写，30fps 约 0.5GB/s。可接受，但不要再叠第二份。

### 1.3 完成功能必须同时给性能账

新增内容必须说清：pass 数、全屏 RT 数、blit 数、批处理路径、依赖哪张 depth/opaque、低画质档能否关闭。说不出来就是没做完。

### 1.4 管线里尽量不 Blit

优先级：调 pass 顺序 → 直接读写相机 color/depth → 复用上下文纹理 → 单次全屏 pass → 降分辨率 RT → 最后才是 Blit。

- 禁止全分辨率 ping-pong（A→B→A）和无理由的 `GetTemporary(cameraTargetDescriptor)`。
- 第三方固有的 blit 要计数：Enviro 雾每帧 **1 次全屏 color copy + 1 次合成**（`EnviroURPRenderPass.cs:220-232`、`:298-302`），体积雾本体 1/4 分辨率；Crest Quad 水面 1 次 DrawMesh。
- 改第三方 blit 必须打 `[FishDemo]` 标记、备份、升级时重打，并写进模块文档。

### 1.5 后处理 / 调色 / ACES

- Tonemapping + Color Grading + LUT 在 URP 内置**同一个 Uber pass**；ACES 只是 Tonemapping 模式，**不另起 pass**。LUT 保持 32（`m_ColorGradingLutSize=32`）。
- **当前已启用全局后处理**：`Post Processing` Volume + `Assets/Settings/PostProcess/FishDemo Global Volume.asset`；`m_ColorGradingMode=1`（HDR grading），ACES + Bloom（Half/Quarter）+ Vignette + Grain；**不做 DOF**。
- 只保留一套后处理；不叠 Beautify 等第二套。Bloom 保持 half-res/quarter-res 降采样链，不升全分辨率；H blur 用 5-tap 双线性等价（embedded URP 的 `[FishDemo]` 补丁）。
- 低画质档先 `B` 循环到 Bloom Half/3 或 Quarter/3（3 级），再关 Bloom。夜景曝光由 `AtmosphereController` 在克隆 profile 上做（+1.8EV），不是第二个 Volume。
- 后处理只登记 pass / 全屏 RT / blit 数；ms 分项预算等角色接入后按真机基线分配。当前账见 [postprocess](postprocess.md) §0.4。

## 2. 当前 pass 顺序（代码事实）

```
Depth prepass / CopyDepth（m_RequireDepthTexture=1，供 Enviro）
  → Opaque（GTS 地表 / 树 / 常规物体，SRP Batcher）
  → Crest WaterSurfaceRenderPass（BeforeRenderingTransparents，Quad 1 DrawMesh）
  → Transparent
  → Enviro 雾/体积雾（AfterRenderingTransparents）
  → URP 后处理（全局 Volume `Post Processing`：Uber pass + Bloom half-res；无 DOF）
```

Enviro 的 pass 位置补丁在 `EnviroURPRenderPass.cs` 构造函数，升级会丢，见 [atmosphere](atmosphere.md) §0.4。

## 3. 批处理

**SRP Batcher（常规物体）→ GPU Instancing（树）→ BRG（草）**。静态合批已关闭（Android/Standalone 0/0），动态合批保持关闭。

| 内容 | 路径 |
|---|---|
| 地表（GTS mesh）/ 树 / 常规物体 | SRP Batcher |
| 草（烘焙 435 万，场景 429 万） | BRG，不经 SRP Batcher |
| NPC / 人群（GPU ECS Animation Baker） | Entities Graphics（BRG + DOTS instancing），不经 SRP Batcher |
| Crest 水面（Quad） | 1 次 `DrawMesh` |
| 天空 / 雾 / 体积 | Enviro RenderFeature |

SRP Batcher 三条实战结论：合批单位是 CBUFFER 布局而不是材质；贴图不参与 CBUFFER；`MaterialPropertyBlock` 和逐物体 `[PerRendererData]` 会拆批。详见 [gts_srp_batcher](gts_srp_batcher.md)。

**任何新增渲染内容必须说明走哪条批处理路径。**

## 4. 光照

- 唯一启用的主光是 Enviro 的 Sun/Moon Directional Light；旧 `Directional Light` 的 Light 组件已禁用。不要再加第二盏方向光。
- **环湖火把**：`Lakeside Torches` 共 12 支，同时点亮 **≤ 6** 盏（`TorchRingController.HardLightLimit`），默认 1 盏，`L` 循环 0/1/2/4/6；按到 `Camera.main` 的距离选最近 N 盏（12% 滞回）。点光 `intensity=120 / range=48m / shadows=None`，其余靠 emissive + Bloom。
- **不做昼夜门控**：火把按手动数量点亮，白天/阴天也可以开；只有夜景曝光跟 `Enviro.isNight`。
- 当前 Forward，per-object light limit = 4；6 盏是预算上限，单物体仍最多叠加 4 盏。URP 14.0.12 已实现 Forward+，但工程未启用（切换前必须回归，见 [gpu_ecs_animation_baker](gpu_ecs_animation_baker.md) §1.2）。
- 点光不投影；Enviro 体积雾只采样主光。Crest 平面反射已开（Default 层），火把/树会进入水面倒影，但点光没有地面阴影或体积光柱。

## 5. 禁止

- ❌ 假设 RenderGraph / 内置水体 / 内置体积雾存在。Forward+ 在 URP 14.0.12 已有，但工程未启用；不要在未回归前依赖它。
- ❌ 加第二个天空盒；无消费者时打开 `m_RequireOpaqueTexture` / `m_RequireDepthTexture`；新增全屏 Blit 链或 ping-pong。
- ❌ 每帧无条件开水面反射相机（必须由 `PlanarReflectionController` 门控）；叠第二套后处理。
- ❌ 用 Terrain detail 做大规模草；用 `drawTreesAndFoliage=false` 关草（原因与替代见 [Grass/AGENTS.md](../Assets/Editor/Grass/AGENTS.md) §2）。
- ❌ 在 `Update` 里逐对象设材质属性；用 MPB 传 per-object 数据；Gameplay 层直接写 `CommandBuffer`；用 `Resources.Load` 做流式加载。

## 6. 注释要求

渲染代码注明：URP 版本行为、依赖的 depth/opaque 及 copy 是给谁的、pass/RT/blit 数、能否降级、非显而易见的原因。
