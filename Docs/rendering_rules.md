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
- 只保留一套后处理；不叠 Beautify 等第二套。Bloom 保持默认降采样链，不改全分辨率。
- 后处理只登记 pass / 全屏 RT 数；ms 分项预算等角色接入后按真机基线分配。

## 2. 当前 pass 顺序（代码事实）

```
Depth prepass / CopyDepth（m_RequireDepthTexture=1，供 Enviro）
  → Opaque（GTS 地表 / 树 / 常规物体，SRP Batcher）
  → Crest WaterSurfaceRenderPass（BeforeRenderingTransparents，Quad 1 DrawMesh）
  → Transparent
  → Enviro 雾/体积雾（AfterRenderingTransparents）
  → URP 后处理（内置 Uber pass；主场景当前无 Volume）
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
- 夜晚 additional lights ≤ 6，其余靠 emissive + Bloom。当前 Forward，per-object light limit = 4；URP 14.0.12 已实现 Forward+，但工程未启用（切换前必须回归，见 [gpu_ecs_animation_baker](gpu_ecs_animation_baker.md) §1.2）。

## 5. 禁止

- ❌ 假设 RenderGraph / 内置水体 / 内置体积雾存在。Forward+ 在 URP 14.0.12 已有，但工程未启用；不要在未回归前依赖它。
- ❌ 加第二个天空盒；无消费者时打开 `m_RequireOpaqueTexture` / `m_RequireDepthTexture`；新增全屏 Blit 链或 ping-pong。
- ❌ 每帧无条件开水面反射相机；叠第二套后处理。
- ❌ 用 Terrain detail 做大规模草；用 `drawTreesAndFoliage=false` 关草（会连树一起关，当前 46,472 棵）。
- ❌ 在 `Update` 里逐对象设材质属性；用 MPB 传 per-object 数据；Gameplay 层直接写 `CommandBuffer`；用 `Resources.Load` 做流式加载。

## 6. 注释要求

渲染代码注明：URP 版本行为、依赖的 depth/opaque 及 copy 是给谁的、pass/RT/blit 数、能否降级、非显而易见的原因。
