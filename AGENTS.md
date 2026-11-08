# FishDemo 项目规约

> 通用工作流（Spec、HARD-GATE、Verification 等）见全局 `~/.zcode/AGENTS.md`。本文件只记录项目特有约束。

## 0. 协作红线

详见 [Docs/collaboration_rules.md](Docs/collaboration_rules.md)。

1. **只陈述有据可查的事实**：文件路径 + 行号、工具输出、或文档原文。不确定就说"不知道"，禁止补全历史、禁止虚构文件/数据/结论。
2. **结论分三类标注，不可混写**：代码事实 / 文档声称（未验证）/ 我的推断（注明依据）。
3. **版本兼容必须查证**：看 `package.json` 的 `unity` 字段、asmdef、源码版本宏、官方文档。**包名和宣传语不可作依据。**

## 1. 项目定位

Unity 2022.3 + URP 的 **PBR 写实钓鱼游戏 Demo**。

- 视觉对标《对马岛之魂》：强方向光、大气透视、风驱动植被、低饱和高对比、青橙调。
- 大世界，地形/植被/建筑**分块动态加载**。
- 核心场景：水面（含水下）、昼夜循环、夜晚有限多光源。
- 目标机型：**iQOO 16**（旗舰），**30fps**。每个效果必须有降级路径。
- **联网**：将来需要，**当前不实现网络层**；只保证客户端表现能承载多人（玩家自身 + 同屏最多 20 个角色）。

## 2. 硬约束

| 项 | 值 | 出处 |
|---|---|---|
| Unity | 2022.3.62f2 | `ProjectSettings/ProjectVersion.txt` |
| URP | 14.0.12 | `Packages/manifest.json` |
| 色彩空间 | Linear | `ProjectSettings.asset` |
| 脚本后端 | IL2CPP / ARM64 | 同上 |
| 纹理压缩 | ASTC | 同上 |
| 帧率目标 | 30fps | 项目决策 |
| 目标机型 | iQOO 16 | 项目决策 |
| 同屏角色上限 | 20 | 项目决策 |

**2022.3 / URP 14 的能力边界**（决定哪些必须绕开）：

| 能力 | 2022.3 现状 | 应对 |
|---|---|---|
| GPUResidentDrawer | ❌ 无（Unity 6 才有） | 手写 BRG |
| Forward+ | ❌ 无（URP 17 才有） | 少量光源 + emissive |
| 内置体积雾/光 | ❌ 无（HDRP 才有） | 第三方包 |
| 内置水体 | ❌ 无（URP 17 才有） | Crest |
| RenderGraph | ❌ 无（Unity 6 才有） | `ScriptableRendererFeature` + `CommandBuffer` |

## 3. 性能预算

30fps = 33.3ms/帧。以下为**初始目标，需在 iQOO 16 实机 Profile 后回写**。

| 项目 | 目标 |
|---|---|
| 不透明几何（地形+植被+建筑） | ≤ 9ms |
| 角色（含 20 个同屏） | ≤ 4ms |
| 水面（含反射） | ≤ 5ms |
| 阴影 | ≤ 3ms |
| 体积雾 + 体积光 | ≤ 3ms |
| 天空/大气 | ≤ 1.5ms |
| 后处理 | ≤ 2.5ms |
| 水下后处理 | ≤ 1.5ms |
| UI + 余量 | ≤ 4ms |

**规模上限**：Draw Call < 300（SRP Batcher 阶段）/ < 100（BRG 阶段）；三角形 < 1.5M；夜晚动态光源 ≤ 6。

## 4. 渲染架构分层

```
Gameplay → World Streaming（chunk + 流式）
         → Render Data（BRG / 实例 buffer）
         → URP（Renderer + RendererFeature）
              ├ Opaque（地形/植被/建筑/角色）
              ├ Water（反射 / 水下）
              ├ Atmosphere（天空/雾/体积）
              ├ Transparent
              └ Post（Bloom/Tonemap/LUT/AA/水下）
```

**约定**：Gameplay 层不得直接操作 `CommandBuffer` 或 `Graphics.*`。

## 5. 模块与选型

| 模块 | 方案 | 关键约束 | 详见 |
|---|---|---|---|
| 地形生成 | Gaia Pro VS | 仅编辑器期用；运行时不用其植被系统 | [terrain_streaming](Docs/terrain_streaming.md) |
| 地形细节（路/河/村落） | GeNa Pro | 生成物需合并或转实例，避免打散合批 | [terrain_streaming](Docs/terrain_streaming.md) |
| Terrain → Mesh | **GTS**（Gaia 内含） | 64~128m 分块 + LOD 金字塔 | [terrain_streaming](Docs/terrain_streaming.md) |
| 区域流式 | SECTR（可选） | 仅区域级切换，不用于高密度网格 | [terrain_streaming](Docs/terrain_streaming.md) |
| **水体** | **Crest Water 5** | 关 DynamicWaves/Flow/Shadow；补反射可见性跳过 | [water](Docs/water.md) |
| 昼夜/天气/大气雾 | **Enviro 3** | 雾 steps 32→16/8；需半分辨率改造 | [atmosphere](Docs/atmosphere.md) |
| 体积雾/光 | Fog Mist 2 / Light Beam 2 / Lights 2 | Lights 2 限 3 个；光柱用 Light Beam | [atmosphere](Docs/atmosphere.md) |
| 后处理/调色 | **URP 内置** | 不引入第三方；LUT 预烘焙 | [postprocess](Docs/postprocess.md) |
| 草 | 自研 GPU instancing / BRG | 位置来自 chunk `ComputeBuffer` | [vegetation](Docs/vegetation.md) |
| 植被/物体 LOD | **Amplify Impostors** | 必须用自带 shader（保 early-z） | [vegetation](Docs/vegetation.md) |
| Shader 变体裁剪 | Shader Control | 包定型后跑；裁剪后必须真机验证 | [vegetation](Docs/vegetation.md) |
| 玩家动画 | **Animancer Pro** | CPU 动画；骨骼 ≤ 60 | [character_animation](Docs/character_animation.md) |
| NPC / 远程玩家动画 | GPU ECS Animation Baker | 必须 vendor；同屏 ≤ 20 | [character_animation](Docs/character_animation.md) |
| 资源缓存 | Addressables + 延迟卸载 + LRU | 卸载不 Destroy Mesh | [pooling_lod](Docs/pooling_lod.md) |

## 6. 各模块要点

**地形与流式**：Gaia 生成 Terrain → GTS 转 mesh 分块 → 自研 Addressables 流式。禁止依赖 Terrain 原生 grass/detail 渲染（CPU 提交）。碰撞近处 `MeshCollider`、远处高度图采样。详见 [terrain_streaming](Docs/terrain_streaming.md)。

**植被**：近景草自研 instancing/BRG；中景 LOD1；远景 Amplify Impostor；极远地形 splat。Impostor 禁用 ShaderGraph 版（破坏 early-z）。详见 [vegetation](Docs/vegetation.md)。

**水体**：Crest 模块化，移动端只留 AnimatedWaves + Foam + Absorption。**必须自研"反射可见性跳过"**——Crest 只有基于相机高度的 `SkipAbove`/`SkipBelow`，没有视锥判断。详见 [water](Docs/water.md)。

**大气**：Enviro 3 管全局；局部雾/光柱/点光源体积光各司其职。所有 pass 都读 depth、都做 fullscreen blit，**顺序必须协调**。详见 [atmosphere](Docs/atmosphere.md)。

**光照**：主光单 directional 按时间驱动；夜晚 additional lights ≤ 6，其余靠 emissive + Bloom；per-object light limit 为 4。

**角色**：玩家 Animancer + `SkinnedMeshRenderer`（CPU）；NPC 与远程玩家用 GPU Baker（VAT）。两者共用同一 PBR shader（keyword 区分 VAT）保证 SRP Batcher 一致。详见 [character_animation](Docs/character_animation.md)。

**资源缓存**：核心原则是**反复加载用缓存解决，不用池解决**。chunk 卸载不 Destroy Mesh，池化复用。详见 [pooling_lod](Docs/pooling_lod.md)。

## 7. 批处理总则

优先级：**SRP Batcher（必须）→ GPU Instancing（必须）→ BRG（目标）→ 静态合批（谨慎）**。

- 动态合批保持关闭。
- 所有材质共享 shader + 相同 keyword 组合。
- **任何新增渲染内容必须说明走哪条批处理路径**，说不出来就是设计缺陷。

## 8. 工程约定

- **Git LFS**：大世界资源必须配。
- **Addressables**：流式加载唯一入口，禁止 `Resources.Load`。
- **目录**：`Assets/Scripts/Runtime/{Rendering,Streaming,Gameplay}`、`Assets/ThirdParty/`。
- **注释**：关键函数写中文注释，说明意图、约束、非显而易见的原因。渲染代码须注明依赖的 URP 版本行为、是否依赖深度图、能否降级。

## 9. 禁止事项

- ❌ 假设 URP 14 有 Forward+ / RenderGraph / 内置水体 / 内置体积雾。
- ❌ 每帧无条件开水面反射相机。
- ❌ 依赖 Terrain 自带 grass/detail 做大规模植被。
- ❌ 在 `Update` 里逐对象设材质属性（破坏 SRP Batcher）。
- ❌ 运行时逐像素算调色/大气散射。
- ❌ 用 `Resources.Load` 做流式加载。
- ❌ 引入无法从 registry 恢复的包却不 vendor 进仓库。
- ❌ 在 Gameplay 层直接写 `CommandBuffer`。

## 10. 验证要求

渲染改动必须提供证据之一，否则不算完成：

1. Frame Debugger 截图（draw call / 合批情况）。
2. Profiler 数据（单项 GPU 耗时在预算内）。
3. **iQOO 16 实机**运行日志（30fps 稳定）。
4. 降级验证（最低画质不崩、无 shader 报错）。

禁止"应该没问题"式结论。预算表（§3）首次实机测试后回写真实数据。

## 11. 当前工程差距（待办）

| # | 问题 | 出处 | 处理 |
|---|---|---|---|
| 1 | 只有 2D Renderer | `UniversalRP.asset: m_RendererDataList` → `Renderer2D.asset` | 新建 `UniversalRenderer` |
| 2 | 深度图未开 | `m_RequireDepthTexture: 0` | 改 1（Crest/雾依赖） |
| 3 | 无 Git LFS | 无 `.gitattributes` | 添加 |
| 4 | 无 Addressables | `manifest.json` | 安装 |
| 5 | 无 Entities | 同上 | 装 Entities + Entities Graphics（GPU Baker 需要） |
| 6 | 缺 Newtonsoft.Json | GPU Baker asmdef | 用 `com.unity.nuget.newtonsoft-json`（DOTween 已提供） |
| 7 | Android 最低 SDK 22 | `AndroidMinSdkVersion: 22` | 建议 26+ |
| 8 | Graphics API 为 Automatic | `m_BuildTargetGraphicsAPIs` | 显式配 **Vulkan** |
| 9 | 增量 GC 关闭 | `gcIncremental: 0` | 开启 |
| 10 | Android 默认画质 Medium | `m_PerPlatformDefaultQuality.Android: 2` | 改 High/Ultra |
| 11 | 2D feature 包冗余 | `com.unity.feature.2d` | 转 3D 后移除 |
| 12 | 无 Input System / Cinemachine | `manifest.json` | 钓鱼玩法需要 |
| 13 | Crest 缺反射可见性判断 | `WaterReflections.cs:240` | 自研补 `TestPlanesAABB` |
| 14 | GPU Baker 未 vendor | 包已下架 | 立即 vendor 进仓库 |

## 12. 待决策

1. **是否长期锁定 Unity 2022.3**：锁定需自研 BRG；升级 Unity 6 可白拿 Forward+/RenderGraph/内置水体，但要重新验证全部第三方包。当前建议：先按 2022.3 推进，渲染抽象层留迁移口。
2. **iQOO 16 实机数据**：预算表目前是估值，需实机校准。
