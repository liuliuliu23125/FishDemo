# Crest · Editor/Scripts 规约

> 第三方编辑器代码，不改。项目里真正操作这些设置的是
> [Assets/Editor/Water/CrestPerformanceMode.cs](../../../../Assets/Editor/Water/CrestPerformanceMode.cs)。

## 1. 设置资产与生成链

设置是 `ScriptableSingleton<ProjectSettings>`，落盘在：

```
ProjectSettings/Packages/com.waveharmonic.crest/Settings.asset
```

**不在 `Assets/` 下**，`AssetDatabase.LoadAllAssetsAtPath` 读不到；应通过 `ProjectSettings.Instance` 或直接读文本。

平台设置按 `NamedBuildTarget` 映射：`_PlatformSettingsDesktop` / `Server` / `Android` / `IOS` / `TVOS` / `VisionOS` / `Web`，另有一个 `_PlatformSettings` 作为默认值（`_Default=true`）。每个字段都有 `_OverrideXxx` 开关，未覆盖时回落到默认。

## 2. 从设置到三处产物

`UpdateSymbols()` 做两件事：

1. `UpdateScriptingSymbols()` → PlayerSettings 宏：
   - `d_Crest_LegacyUnderwater`
   - **`d_Crest_SimpleTransparency`**
   - `d_Crest_DisablePlanarReflectionApplySmoothness`
2. `ShaderSettingsGenerator.Generate()` → `ShaderGeneratorUtility.GenerateAll()` → 生成
   `Runtime/Shaders/Library/Settings/Settings.Crest.*.hlsl`。

触发方式：

- `[InitializeOnLoadMethod]` 在加载/重编译时重写宏（防止被移除）；
- `FileSystemWatcher` 监听 `Settings.asset`：变化后 destroy 单例、反射清 `s_Instance`、再 `UpdateSymbols()`。

## 3. 批处理/无域重载的坑（项目实测）

切 SimpleTransparency 时，以下做法**无效或半有效**：

- 只改 `Settings.Crest.*.hlsl`：脚本重载后生成器会覆盖；
- `AssetDatabase.LoadAllAssetsAtPath`：资产在 `ProjectSettings/`，读不到；
- `SerializedObject` + `ScriptableSingleton.Save()`：内存回读正确，落盘仍是旧值；
- `UpdateSymbols()`：只请求重编译，batchmode 不触发域重载。

有效路径（项目工具 `CrestPerformanceMode` 的实现）：

1. 直接改 `Settings.asset` 文本 → 重载单例（照 `ProjectSettings.cs` 的 `OnChanged` 逻辑）；
2. 调 `UpdateSymbols()`；
3. 反射调 `ShaderGeneratorUtility.GenerateAll()` 并等完成。

改完后必须同时确认三层：设置资产值、PlayerSettings 宏、生成的 HLSL。

## 4. Validators

`Validators.cs` 在 Inspector 里检查：

- `WaterRenderer` 不能有旋转/缩放；层与相机 cullingMask；
- `LodInput` 的 Renderer/Geometry 层、纹理输入；
- 材质的 simulation 开关与模块开关是否一致（缺 Feature 会报错）；
- `WaterBody` 的材质父级、`Precise` 与 ClipLod 的依赖；
- `UnderwaterRenderer` / `WaterReflections` / `Meniscus` 的配置。

Inspector 报红通常是「模块开了但材质没开对应 Feature」或「Reflection/Underwater 层没对上」，按提示改，不要绕过。

## 5. 其他编辑器资产

- `Requirements/Requirements.cs`：`#error` 拦 Unity < 2022.3 与不匹配的可选包（CPU Queries/Paint/Portals/Splines 等）。
- `Integrations/Gaia/GRC_Crest.cs`：给 Gaia 用的 `GaiaRuntimeComponent`，工程未使用；不要靠它自动加 Crest。
- `MaterialUpgrader.cs` / `Migration`：材质/版本迁移逻辑；升级包后按 Inspector 提示跑迁移。
- `Generated/ShaderSettings.Generated.cs`：生成物，不要手改。

## 6. 相关

| 文档 | 何时读 |
|---|---|
| [crest_internals](../../../../Docs/crest_internals.md) §8 | 三层配置与生成链的原理 |
| [water §6.1](../../../../Docs/water.md) | 本工程切 SimpleTransparency 的实测踩坑 |
| [Runtime/Shaders/AGENTS.md](../../Runtime/Shaders/AGENTS.md) | 生成物与 keyword |
| [../../../../Assets/Editor/Water/AGENTS.md](../../../../Assets/Editor/Water/AGENTS.md) | 项目侧工具与验收 |
