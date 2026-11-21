# Assets/UnderwaterEcology 规约（自研水下资源）

> 自研 shader + `UnderwaterEcologySetup` 生成的材质/网格/prefab。细节见 [underwater_ecology](../../Docs/underwater_ecology.md)。

## 1. 目录

| 路径 | 内容 |
|---|---|
| `Shaders/` | `FishVAT`、`Plant`、`SeabedSand`、`JellyfishVAT`、`UnderwaterBubble`、`UnderwaterGodRay`（WIP） |
| `Materials/` | VAT 鱼材质（LOD0/LOD1）、`M_SeabedSand` |
| `Materials/Custom/` | 由 Ocean 源材质生成的植物/气泡/GodRay/水母材质 |
| `Meshes/SeabedSand.asset` | 全湖海床沙网格（24577 顶点 / 48896 三角形），**LFS** |
| `Prefabs/Jellyfish_Compat.prefab` | 换成自研水母材质的 prefab 变体 |

## 2. 硬约束

- shader 零注释；不用 URP 17 专属 include（`ProbeVolumeVariants.hlsl`、`DebugMipmapStreamingMacros.hlsl`、`MotionVectorsCommon.hlsl`）。2022.3/URP 14 是唯一目标。
- `FishVAT` / `JellyfishVAT` 的 VAT 采样必须用 mesh UV1 与 `51` 帧映射；不要覆盖源 mesh 的 UV1（蓝吊 919 顶点映射到 861 列 VAT，青鳉 751 映射到 665 列）。
- `SeabedSand` 不采样 light probe/shadow，环境色是固定水下色；改亮度改 `_AmbientColor/_AmbientStrength/_BaseColor`。
- `UnderwaterBubble` 的 alpha 来自程序化圆形 rim/中心 + 粒子顶点 alpha；不要改回采样 `T_ParticleBubbles_Refraction` 的 alpha（该贴图没有透明边缘，会让 billboard 变不透明）。
- 新增/修改材质必须保持 `enableInstancing=true`；VAT 鱼一个物种一个共享材质，走 GPU instancing。
- `UnderwaterGodRay` 当前会产生大面积色块，默认关闭；修好网格 UV/缩放前不要默认开。

## 3. 生成与清理

- 重新生成：`UnderwaterEcologySetup.Apply`（会覆盖 `Materials/`、`Meshes/`、`Prefabs/`，**不会**动 `Shaders/`）。
- 清理：`UnderwaterEcologySetup.Remove`（保留 `Shaders/`，删除生成材质/网格/prefab，并关闭 Crest 焦散）。
- 不要手工改生成材质/prefab 后不重跑 setup；它们会被覆盖。
