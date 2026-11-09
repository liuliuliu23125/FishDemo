# Assets/Procedural Worlds 规约（第三方 Gaia / GeNa / SECTR / GTS）

> 本目录是**第三方插件资产**，不是自研代码。
> 本文件约 81 行，改动或排查第三方问题时建议一次读完。

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

调用 SECTR / GTS 编辑器 API 的脚本必须放 `Assets/Editor/`，详见 [../Editor/AGENTS.md](../Editor/AGENTS.md)。

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
3. **材质迁移有方向性**：`GaiaUtils.ProcessMaterialLibrary` 按 `GaiaSettings.m_currentRenderer` 决定迁移方向。若不是 `Universal`，它会把 URP 材质改回 BuiltIn。改之前先确认该字段。
4. **SECTR 的 README 只写到 2019.0.3**，但代码里有 `#if UNITY_6000_0_OR_NEWER` 分支，说明被维护到新版本。判断兼容性看源码宏和 asmdef，不要看 README 版本号。

## 4. 排查入口

| 症状 | 去哪 |
|---|---|
| 地形是平的 | `GaiaSessionManager.CreateOrUpdateWorld` 只建网格，必须显式 `Stamp` |
| 一棵树都没有 | Biome 预设的 HeightMask 按旧世界标定；见 [world_implementation §2.3](../../Docs/world_implementation.md) |
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
| [world_implementation](../../Docs/world_implementation.md) | 理解这些包在本工程里实际怎么用的 |
| [terrain_streaming](../../Docs/terrain_streaming.md) | Gaia / GeNa / SECTR / GTS 的选型分析 |
| [water](../../Docs/water.md) | 水体选型（注意：该文档写的是 Crest，与当前实现不同） |
| [collaboration_rules](../../Docs/collaboration_rules.md) | 历史踩坑 |
