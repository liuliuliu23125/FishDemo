# com.unity.render-pipelines.universal（embedded 14.0.12）

> 本目录是 **URP 14.0.12 的本地副本**（`source: embedded`），由项目自己维护。
> 改任何文件前先确认是 `[FishDemo]` 定制，还是 Unity 原版代码。

## 1. 为什么 embedded

- 需要改 tonemapping / ACES 时不能再依赖 `Library/PackageCache`（会被包管理器覆盖）。
- 同时 embedded `com.unity.render-pipelines.core`（ACES 函数在 core 的 `ShaderLibrary/Color.hlsl`）。
- 版本必须保持 **14.0.12**，与 `Packages/manifest.json` / `packages-lock.json` 一致。

## 2. 裁剪

- 已删除 `Documentation~`、`Samples~`、`Tests`、`.npmignore`，并从 `package.json` 删除 `samples`。
- `Runtime`、`Editor`、`Shaders`、`ShaderLibrary`、`Textures` 保持 14.0.12 原样。
- 不要删除或重命名 `.meta`：GUID 变了，场景/材质/URP 资产的引用会断。

## 3. 当前定制

| 文件 | 定制 | 说明 |
|---|---|---|
| `Runtime/Overrides/Bloom.cs` | `maxIterations` 默认 3、范围 2..6（原 6 / 2..8） | `[FishDemo]`；High 档由 `AtmosphereController` 覆盖为 6，Quarter 档 3 |
| `Shaders/PostProcessing/Bloom.shader` | `FragBlurH` 9 tap → **5 tap 双线性等价** | `[FishDemo]`；保持原 9-tap 高斯半径/形状，减少每级采样 |
| `Runtime/UniversalRenderPipeline.cs` | `RenderSingleCameraInternal` 对 `CameraType.Reflection` 也读取 `UniversalAdditionalCameraData` | `[FishDemo]`；不补则 Crest 反射相机的 RendererIndex / requiresDepth / renderShadows / post 全部被忽略，退回默认 renderer（CopyDepth + FinalBlit） |
| — | ACES 仍未改 | GOT 的改法与落点见 [postprocess](../../Docs/postprocess.md) §4 |

**真改 GOT ACES 的落点**（尚未修改）：

- `../com.unity.render-pipelines.core/ShaderLibrary/Color.hlsl`：`AcesTonemap`
- `../com.unity.render-pipelines.core/ShaderLibrary/ACES.hlsl`
- `Shaders/PostProcessing/LutBuilderHdr.shader` / `LutBuilderLdr.shader`
- 相关 `ShaderLibrary` include

## 4. 维护规则

1. 所有定制行加 `[FishDemo]` 标记，并在本文件登记；不要悄悄改。
2. 升级：从 `Library/PackageCache/com.unity.render-pipelines.universal@14.0.12` 重新复制 14.0.12，
   重打所有 `[FishDemo]` 补丁（含 `RenderSingleCameraInternal` 的反射相机 UACD 补丁），跑 `LightingVerify` + `AtmosphereVerify` + `PlanarReflectionVerify`。
3. 改 shader 后必须重编 shader variant；验证需真实图形设备，不能用 `-nographics`。
4. 低画质档的开关放在 Volume / 项目侧，不要改 URP 默认行为。
5. 这个包是 **Runtime/Editor 全量源码**，编译时间比 PackageCache 版本长，属正常。

## 5. 验证

```
Unity.exe -batchmode -projectPath E:\FishDemo -executeMethod LightingVerify.Run
Unity.exe -batchmode -projectPath E:\FishDemo -executeMethod AtmosphereVerify.Run
```

当前基线（2026-11-18）：LightingVerify PASS（无 DOF、ACES、Bloom、HDR grading）；
AtmosphereVerify PASS（正午顶 0.585、午夜顶 0.005、午夜约 120 色 / 阈值 100）。

## 6. 相关文档

| 文档 | 何时读 |
|---|---|
| [postprocess](../../Docs/postprocess.md) | 后处理参数、GOT ACES 调研、pass 账 |
| [rendering_rules](../../Docs/rendering_rules.md) | 管线铁律 |
| [core AGENTS](../com.unity.render-pipelines.core/AGENTS.md) | 改 ACES / 核心 shader |
| [Settings AGENTS](../../Assets/Settings/AGENTS.md) | URP Asset / Renderer / Volume |
