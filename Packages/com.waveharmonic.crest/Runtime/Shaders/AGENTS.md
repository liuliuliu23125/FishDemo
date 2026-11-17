# Crest · Runtime/Shaders 规约

> 第三方 shader，不改。改水面材质/透明前先读 [crest_internals](../../../../Docs/crest_internals.md) §6；
> 本工程参数见 [water](../../../../Docs/water.md) §4、§6.1。

## 1. 生成式设置：不要手改

- `Settings.Crest.*.hlsl` 文件头写明「automatically generated, don't edit by hand」；手改会在脚本重载时被覆盖。
- 唯一入口是设置资产 → 生成器；完整链路与 batchmode 做法见 [Editor/Scripts §1–§3](../../Editor/Scripts/AGENTS.md)。

## 2. 两层 keyword

| 层 | 例子 | 来源 |
|---|---|---|
| 工程 shader 设置（生成） | `CREST_SIMPLE_TRANSPARENCY`、`CREST_PLANAR_REFLECTIONS`、`CREST_FOAM_SAMPLING_*` | `Settings.Crest.*.hlsl` |
| 脚本宏（PlayerSettings） | `d_Crest_SimpleTransparency`、`d_Crest_LegacyUnderwater`、`d_Crest_DisablePlanarReflectionApplySmoothness` | `ProjectSettings.asset` / 生成器 |
| 材质 local keyword | `_CREST_CUSTOM_MESH`、`_SURFACE_TYPE_TRANSPARENT` | 运行时/材质 |
| 运行时写入 | `d_Crest_NoMaskColor`、`d_Crest_NoMaskDepth` | `UnderwaterRenderer` |

`Constants.Symbols.k_Refraction` 在 `d_Crest_SimpleTransparency` 下变成 `CREST_NOOP`，使 `[Conditional]` 的 CopyColor/CopyDepth 调用被编译掉——这是「只改 C# 不改这里没效果 / 只改 hlsl 不改 C# 也没效果」的根源。

## 3. 透明公式（`Surface/Fragment.hlsl`）

```hlsl
#if d_Crest_SimpleTransparency
o_Alpha = i_Underwater
    ? 1.0 - transmitted
    : max(max(length(volumeOpacity), _Crest_TransparencyMinimumAlpha), max(foam, albedo.a));
#endif
```

- 非 SimpleTransparency 时 `o_Alpha` 恒为 1，透明来自折射合成 + 体积消光（`Refraction.hlsl` / `VolumeLighting.hlsl`）。
- `_Crest_TransparencyMinimumAlpha` 是 Simple 模式的兜底下限；工程值见 [water §4](../../../../Docs/water.md)，不调低会「水面突然变实」。
- 调透明要同时看 Absorption/Scattering 与 `volumeOpacity`，不是只改一个 scalar。

## 4. 文件分工

| 路径 | 内容 |
|---|---|
| `Surface/Water.shadergraph` | 水面主 ShaderGraph（材质引用它） |
| `Surface/Fragment.hlsl` | 着色主逻辑：透明、泡沫、反照率、散射 |
| `Surface/Alpha.hlsl` / `Refraction.hlsl` / `Reflection.hlsl` | 透明/折射/反射分支 |
| `Surface/Keywords.hlsl` | `d_Crest_SimpleTransparency`、`d_Crest_CustomMesh` 等映射 |
| `Data/*.compute` | LOD 模拟/输入（Depth/Clip/Foam/DynamicWaves/Shadow/Query） |
| `Volume/*.shader` | 水下效果与 mask |
| `Library/Settings.*` | **生成物**，见 §1 |

## 5. 硬约束

1. 不手改生成物；不手改 `CREST_SIMPLE_TRANSPARENCY` 的 HLSL 而不同步设置/宏。
2. 不支持 OpenGL/GLES；shader 里也有 `#pragma exclude_renderers glcore gles3`。
3. 水面材质是 ShaderGraph；`_CREST_CUSTOM_MESH` 的写入规则见 [Surface §2](../Scripts/Surface/AGENTS.md)。
4. 新增 keyword 前先确认它不是工程级设置（应该走生成器）；材质级用 local keyword。
5. 改 shader 后按 [rendering_rules](../../../../Docs/rendering_rules.md) 交 pass/draw/variant 账，并重跑水面验收。

## 6. 相关

| 文档 | 何时读 |
|---|---|
| [crest_internals](../../../../Docs/crest_internals.md) §6 | 透明与 copy pass 的触发条件 |
| [water §6.1](../../../../Docs/water.md) | SimpleTransparency 三层配置与踩坑 |
| [Editor/Scripts/AGENTS.md](../../Editor/Scripts/AGENTS.md) | 设置生成与批处理 |
| [../Scripts/Surface/AGENTS.md](../Scripts/Surface/AGENTS.md) | 材质/网格/pass |
