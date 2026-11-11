# Assets/Procedural Worlds 规约（第三方 Gaia / GeNa / SECTR / GTS）

> 本目录是**第三方插件资产**，不是自研代码。
> 本文件约 105 行，改动或排查第三方问题时建议一次读完。

## 1. 目录结构：Cache 与 Install 的区别

```
Packages - Cache/     安装包缓存（.unitypackage 原文件），代码不参与编译
Packages - Install/   已解包安装的内容，真正参与编译
```

**已安装**（`Packages - Install/` 下实际存在）：

| 内容 | 路径 | 说明 |
|---|---|---|
| Gaia 核心 | `Packages - Install/Gaia/` | `GaiaCore` / `GaiaEditor` asmdef |
| Gaia Water (URP) | `Packages - Install/Gaia Water/` | 实际使用的水面 |
| Unity URP Water | `Packages - Install/Unity URP Water/` | 水下效果（`GaiaURPUnderwaterEffects`） |
| Gaia Pro Assets and Biomes | `Packages - Install/Gaia Pro Assets and Biomes/` | Biome 预设与美术材质 |
| Stamps | `Packages - Install/Stamps/` | 地貌印章 exr |
| **GTS** | `Packages - Install/GTS/` | **Terrain→Mesh 转换，地表渲染的实际承担者** |
| GeNa | `GeNa/` | 与 Gaia 同级目录，非 Install 下 |
| SECTR | `SECTR/` | 与 Gaia 同级目录，非 Install 下 |
| **Pegasus** | `Pegasus/` | 与 Gaia 同级目录，非 Install 下。相机巡游/飞行路径插件 |

**未安装**（只在仓库根 `Reference/*.unitypackage`，不要假设可用）：
Crest Water 5、Enviro 3、Amplify Impostors、Animancer Pro、Shader Control、Fog Mist 2、Light Beam 2、Lights 2、Beautify 3、Radiant GI、PoolManager、DOTween Pro。

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

8. **树的批次调优结论（2026-11-11 已查明，暂不改）**：45 个树 prefab 全部只用
   `PW_General_ShaderGraph_URP` 一个 shader，且材质的 keyword 集合完全一致
   （`Logs/tree-variants.txt` 汇总：不同 shader = 1，不同 shader+keyword 组合 = 1）。
   即**树不存在「keyword 不同导致拆批」的问题**，批次数只来自
   「每 prefab 3~4 级 LODGroup × 各 LOD 多个 renderer（树干/树叶/根部）× 36 个 chunk 各自独立提交」。
   要减少树批次只能改资产结构（合材质 / 减 LOD / 转 impostor），不是配置问题。

## 4. 排查入口

| 症状 | 去哪 |
|---|---|
| 地形是平的 | `GaiaSessionManager.CreateOrUpdateWorld` 只建网格，必须显式 `Stamp` |
| 一棵树都没有 | Biome 预设的 HeightMask 按旧世界标定；见 [world_implementation §2.3](../../Docs/world_implementation.md) |
| **树的 draw call 特别多** | 不是 keyword 问题（已排除，见 §3.8）；是 LOD 级数 × renderer 数 × chunk 数，要减只能改资产 |
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
| [water](../../Docs/water.md) | 水体选型（注意：该文档写的是 Crest，与当前实现不同） |
| [collaboration_rules](../../Docs/collaboration_rules.md) | 历史踩坑 |
