# Assets/Procedural Worlds 规约（第三方 Gaia / GeNa / SECTR / GTS）

> 本目录是**第三方插件资产**，不是自研代码。
> 改动或排查第三方问题时建议一次读完。

## 1. 目录结构：Cache 与 Install 的区别

```
Packages - Cache/     安装包缓存（.unitypackage 原文件），代码不参与编译
Packages - Install/   已解包安装的内容，真正参与编译
```

**已安装**（`Packages - Install/` 下实际存在）：

| 内容 | 路径 | 说明 |
|---|---|---|
| Gaia 核心 | `Packages - Install/Gaia/` | `GaiaCore` / `GaiaEditor` asmdef |
| Gaia Water (URP) | `Packages - Install/Gaia Water/` | **已停用（2026-11-13）**：水面改由 Crest 承担。当前 `World.unity` 里已无 `Gaia Water` 节点；`LakeCrestSetup.Apply` 只在能找到时禁用它。见 [water](../../Docs/water.md) §1 |
| Unity URP Water | `Packages - Install/Unity URP Water/` | 水下效果（`GaiaURPUnderwaterEffects`） |
| Gaia Pro Assets and Biomes | `Packages - Install/Gaia Pro Assets and Biomes/` | Biome 预设与美术材质 |
| Stamps | `Packages - Install/Stamps/` | 地貌印章 exr |
| **GTS** | `Packages - Install/GTS/` | **Terrain→Mesh 转换，地表渲染的实际承担者** |
| GeNa | `GeNa/` | 与 Gaia 同级目录，非 Install 下 |
| SECTR | `SECTR/` | 与 Gaia 同级目录，非 Install 下 |
| **Pegasus** | `Pegasus/` | 与 Gaia 同级目录，非 Install 下。相机巡游/飞行路径插件 |

**已安装但不在本目录**（不要因为不在 `Assets/Procedural Worlds/` 就当成没装）：

| 内容 | 路径 | 说明 |
|---|---|---|
| **Crest Water 5** | `Packages/com.waveharmonic.crest`（embedded） | 中心湖水面的实际承担者，见 [water](../../Docs/water.md) §1 |
| **Enviro 3** | `Assets/Enviro 3 - Sky and Weather/` | 昼夜/天气/体积雾光，见 [atmosphere](../../Docs/atmosphere.md) §0 |
| **Amplify Impostors** | `Assets/AmplifyImpostors/` | 4 个云杉原型已烘 impostor，见 [vegetation](../../Docs/vegetation.md) §6.6 |

**未安装**（只在仓库根 `Reference/*.unitypackage`，不要假设可用）：
Animancer Pro、Shader Control、Fog Mist 2、Light Beam 2、Lights 2、Beautify 3、Radiant GI、PoolManager、DOTween Pro。

> 根 `AGENTS.md` §2 的选型表分「现状 / 规划」两列，规划列不代表已安装。

## 2. 程序集归属

| 包 | asmdef | 备注 |
|---|---|---|
| Gaia | `GaiaCore`（全平台）、`GaiaEditor`（Editor） | 可被 `FishDemo.Editor` 引用 |
| SECTR | **无 asmdef** | 编进 `Assembly-CSharp-Editor` / `Assembly-CSharp`，asmdef 程序集引用不到 |
| GTS | **无 asmdef**（仅 MeshSimplifier 有） | 编进 `Assembly-CSharp`；调用它的脚本必须放 `Assets/Editor/` |
| GeNa | `Gena.Scripts.Utils` | |
| **Pegasus** | **无 asmdef** | 编进 `Assembly-CSharp`；调用它的脚本必须放 `Assets/Editor/` 或无 asmdef 的运行时目录 |

调用 SECTR / GTS / Pegasus 编辑器 API 的脚本必须放 `Assets/Editor/`，详见 [../Editor/AGENTS.md](../Editor/AGENTS.md)。

## 3. 改动原则

1. **不改第三方源码**。要改行为，优先用配置、包装层或 `-executeMethod` 外部脚本。
2. **例外一：美术资产可被脚本改写**。`BiomeSpawner` 会改
   `Gaia Pro Assets and Biomes/Biomes/Alpine Meadow/Alpine Meadow-Trees.asset` 的 HeightMask
   （`m_heightMaskType: 0`、`[62, 665]`）。重装 Gaia 会丢。
3. **例外二：GTS shader 被改过（2026-11-09）**。为让地形走 SRP Batcher，改了 3 个文件：
   - `GTS/Shaders/GTS_TerrainInput.hlsl` — 17 个材质属性包进 `UnityPerMaterial`
   - `GTS/Shaders/URP/TerrainLitInput.hlsl` — `_NumLayersCount` 移出 `_Terrain` CBUFFER
   - `GTS/Shaders/URP/GTS_URP_Compiled.shader` — 移除 `[PerRendererData]` 标记
   - `GTS/Scripts/Core/MonoBehaviours/GTSMesh.cs` — `worldSize` 改为可配置

   **原始文件备份在 `Tools/gts-shader-backup/`，重装/升级 GTS 会覆盖，需重新应用。**
   原理与验证方式见 [gts_srp_batcher](../../Docs/gts_srp_batcher.md)。
4. **GTS Profile 的纹理数组已改 ASTC**：`compressed=true`、`ASTC_8x8`、2048×2048×8 层。
   单数组 341MB → 21.3MB。**不要改成 `ASTC_6x6`**——GTS 枚举值 50 实际映射到
   `ASTC_RGB_6x6`（无 alpha），而 albedo 的 alpha 是高度混合权重，会破坏材质过渡。
5. **材质迁移有方向性**：`GaiaUtils.ProcessMaterialLibrary` 按 `GaiaSettings.m_currentRenderer` 决定迁移方向。若不是 `Universal`，它会把 URP 材质改回 BuiltIn。改之前先确认该字段。
6. **SECTR 的 README 只写到 2019.0.3**，但代码里有 `#if UNITY_6000_0_OR_NEWER` 分支，说明被维护到新版本。判断兼容性看源码宏和 asmdef，不要看 README 版本号。
7. **例外三：Pegasus 被改过两处（2026-11-11）**，都是原版 bug，不修跑不起来：

   | 文件 | 位置 | 问题 | 修法 |
   |---|---|---|---|
   | `Pegasus/Scripts/PegasusManager.cs` | 约 `:1249` | 用 `TimeSpan.Milliseconds`（毫秒的 0..999 部分）算帧时长；>1000fps 时恒为 0，相机几乎不动（实测 25 秒走 133m 应为 750m） | 改 `Time.unscaledDeltaTime` |
   | `Pegasus/Scripts/PegasusPoi.cs` | 约 `:746` | `firstSegment` 未夹取，每帧前进 ≥1 个 poiStep 时 `m_poiSteps[firstSegment+1]` 越界，30fps 下每秒数百次异常 | 先 `Clamp01(percent)` 再夹 `firstSegment` |

   **这两处的原始副本在 `Tools/pegasus-patches/`**（不是备份工具，是改后文件），重装 Pegasus 会覆盖，需重新应用。
   注意：第二处越界**是被第一处长期掩盖的**——修好帧时长后立刻暴露。只修一处会更糟。

8. **树的批次结论（2026-11-12 更新，详见 [vegetation](../../Docs/vegetation.md) §6）**：
   地形实际只用 **7 个原型**，其中 4 个云杉占 99.99%；实例已降到 **46,472**。
   4 个云杉已烘 Amplify Impostor，LOD 从 3~4 级压成 **2 级**。
   在用的 10 个材质只有 **2 种 keyword 组合**（树干 `WIND` / 针叶
   `ALPHATEST+SSS+WIND+WORLDMAP`）。**批次暴增先查
   `couldn't be instanced because one of the materials is missing`**——
   一个材质槽缺失会让整批树放弃 GPU instancing，不是 keyword 问题。

9. **例外四：SECTR 流式被打过补丁（2026-11-17）**。原版有三个问题：
   ① `activationQueue` 是死代码（从未把 `allowSceneActivation` 设为 false），
   9 个 chunk 同帧激活；② 加载/卸载一帧全发、无预算；③ 卸载时同步
   `Resources.UnloadUnusedAssets()`，editor 明显卡顿。

   改了两个文件：
   - `SECTR/Scripts/Stream/SECTR_Chunk.cs`：跟踪 `UnloadSceneAsync`；资产扫描改走
     运行时宿主。**没有**关 `allowSceneActivation`——2022.3 批量 additive 下会卡在
     progress=0.90、isDone 永不为真。
   - `SECTR/Scripts/Stream/SECTR_RegionLoader.cs`：每帧最多 2 个加载引用 /
     4 个释放引用；加载按 50ms wall-clock 间隔发起；释放交给 `SectrChunkCache`。

   新增运行时（无 asmdef，编进 `Assembly-CSharp`）：
   `Assets/Scripts/Runtime/Gameplay/SectrChunkCache.cs`、
   `SectrStreamingRuntime.cs`。缓存默认 editor 6 块 / 真机 4 块，TTL 15s / 10s。

   **原始副本在 `Tools/sectr-patches/*.orig`；升级/重装 SECTR 会覆盖补丁，必须重打。**
   参数与验证见 [terrain_streaming](../../Docs/terrain_streaming.md) §12。

   > 内存在 SECTR 之外的归属：草数据由 `Gameplay/SectrGrassStreamer.cs` 跟随 chunk 进出
   > （`NativeArray` + `GraphicsBuffer` 自管，不走 `Resources.UnloadUnusedAssets`）。
   > `gcIncremental: 1` 只作兜底，**不接管 GCMode**。见
   > [terrain_streaming](../../Docs/terrain_streaming.md) §12.5。别把内存问题和 SECTR 补丁混为一谈。

## 4. 排查入口

| 症状 | 去哪 |
|---|---|
| 地形是平的 | `GaiaSessionManager.CreateOrUpdateWorld` 只建网格，必须显式 `Stamp` |
| 一棵树都没有 | Biome 预设的 HeightMask 按旧世界标定；见 [world_implementation §2.3](../../Docs/world_implementation.md) |
| **树的 draw call 特别多** | 先 grep `couldn't be instanced`（材质槽缺失会让整批树退化成逐实例提交）；已排除 keyword 差异，见 §3.8 |
| **草在编辑器可见、打包后消失** | Android 回落 GLES，或 `GrassData` 走 `File.ReadAllBytes` 打包后不存在；见 [grass_brg_progress](../../Docs/grass_brg_progress.md) §5 |
| 材质是粉的 | `m_currentRenderer` 不是 Universal；跑 `GaiaSetupTools.UpgradeMaterialsToURP` |
| chunk 加载不出来 | `SECTR_Sector.TotalBounds` 为零 → `RegionLoader` 求交永远不命中，且不报错 |
| 加载主场景就爆内存 | 原始地形残留没删；见 [../Editor/AGENTS.md](../Editor/AGENTS.md) §3.3 |
| **GTS 转换时 MissingReferenceException** | 一进程跑了多块；必须一进程一块，见 [gts_srp_batcher §8](../../Docs/gts_srp_batcher.md) |
| **mesh 地形贴图/分层错乱** | 缺 `GTSMesh` 组件或 `worldSize` 用了默认 1024；见 [gts_srp_batcher](../../Docs/gts_srp_batcher.md) §8 |
| **真机上地形材质全黑/错乱** | `GtsRuntimeDataBootstrap` 没挂或缺 `Refresh()`；同上 |
| **地形合批不生效** | GTS shader 缺 `UnityPerMaterial` CBUFFER；见 [gts_srp_batcher](../../Docs/gts_srp_batcher.md) |

## 5. 相关文档

| 文档 | 何时读 |
|---|---|
| [gts_srp_batcher](../../Docs/gts_srp_batcher.md) | 改 GTS shader、Terrain→Mesh 转换、排查地形合批/视觉差异 |
| [grass_brg_progress](../../Docs/grass_brg_progress.md) | 排查草渲染（BRG 不经过 Gaia/SECTR 的渲染路径） |
| [world_implementation](../../Docs/world_implementation.md) | 理解这些包在本工程里实际怎么用的 |
| [terrain_streaming](../../Docs/terrain_streaming.md) | Gaia / GeNa / SECTR / GTS 的选型分析 |
| [water](../../Docs/water.md) | 水体：Crest 落地配置、湖盆、性能模式 |
| [atmosphere](../../Docs/atmosphere.md) | 大气：Enviro 落地配置与管线位置 |
