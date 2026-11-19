# com.unity.render-pipelines.core（embedded 14.0.12）

> 本目录是 SRP Core 14.0.12 的本地副本（`source: embedded`），与 embedded URP 配套。
> 改 ACES、颜色空间、Volume 核心逻辑前先读 [postprocess](../../Docs/postprocess.md) §4。

## 1. 裁剪

- 已删除 `Documentation~`、`ApiUpdater~`、`Tests`、`.npmignore`。
- `Runtime`、`Editor`、`ShaderLibrary` 保持 14.0.12 原样；`.meta` / GUID 不要动。

## 2. 当前定制

| 文件 | 定制 |
|---|---|
| — | **无** |

**GOT ACES 的落点**（尚未修改）：

- `ShaderLibrary/Color.hlsl`：`AcesTonemap`、`unity_to_ACES`、ACES↔ACEScg 矩阵
- `ShaderLibrary/ACES.hlsl`：AP0/AP1 矩阵与 ACES 函数

修改时必须同时检查 URP 的 `LutBuilderHdr/Ldr` 调用链；所有定制行加 `[FishDemo]`，登记在
[URP AGENTS](../com.unity.render-pipelines.universal/AGENTS.md) §3。

## 3. 维护规则

1. 升级：从 `Library/PackageCache/com.unity.render-pipelines.core@14.0.12` 重新复制，重打补丁。
2. 改 `Color.hlsl` 会影响所有 URP tonemapping，必须跑 `LightingVerify` + `AtmosphereVerify`，
   并额外对比日落 / 午夜截图。
3. 不要改 Volume / 后处理的公共 API 行为去迎合单个项目；项目侧用 Volume 参数解决。
4. 这个包没有 `Documentation~` / `Tests`，不要假设它们存在。

## 4. 相关文档

| 文档 | 何时读 |
|---|---|
| [URP AGENTS](../com.unity.render-pipelines.universal/AGENTS.md) | URP 定制、升级、验证 |
| [postprocess](../../Docs/postprocess.md) | GOT ACES 调研与当前调色参数 |
