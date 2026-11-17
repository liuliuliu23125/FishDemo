# YooAsset · Editor/BundleBuilder 规约

> 第三方编辑器代码，不改。管线差异、模拟构建原理见 [yooasset_3.0.7 §8.3–§8.4](../../../../Docs/yooasset_3.0.7.md)。

## 1. 六种构建管线（`EBuildPipeline`）

| 管线 | 产物 | 用法 |
|---|---|---|
| `EditorSimulateBuildPipeline` | 虚拟 AssetBundle + 清单 | **编辑器模拟运行**，不产生真实 bundle |
| `LegacyBuildPipeline` | AssetBundle（旧管线） | 兼容旧工程；新工程不选 |
| `ScriptableBuildPipeline` | AssetBundle（SBP） | **正式 AssetBundle 包** |
| `RawFileBuildPipeline` | 原生文件（不打包） | 视频/大文件等不想进 bundle 的资源 |
| `InstantAssetBuildPipeline` | 团结引擎 InstantAsset | 非本项目平台，忽略 |
| `ArchiveFileBuildPipeline` | 归档文件包 | 特殊分发格式 |

`EBundleType` 决定运行时用哪种 `IBundleHandle`（`AssetBundle` / `RawBundle` / `ArchiveBundle` / `InstantBundle` / `Virtual*`）。

## 2. 任务链

SBP（`ScriptableBuildPipeline.cs`）：

```
TaskPrepare → TaskGetBuildMap → TaskBuilding → TaskVerifyBuildResult
→ TaskEncryption → TaskUpdateBundleInfo → TaskCreateManifest
→ TaskCreateReport → TaskCreatePackage → TaskCopyBundledFiles → TaskCreateCatalog
```

ESBP（模拟构建）：

```
TaskPrepare → TaskGetBuildMap → TaskUpdateBundleInfo → TaskCreateManifest
```

RFBP 与 SBP 类似，但 `TaskBuilding` 换成原生文件拷贝。

`BundleBuilder.Run(parameters, pipeline, enableLog)` 是执行入口；失败会返回 `BuildResult.FailedTask` 与 `ErrorInfo`。

## 3. 参数

`BuildParameters` 关键项：`BuildOutputRoot`、`BundledFileRoot`、`BuildPipeline`、`BuildBundleType`、`BuildTarget`、`PackageName`、`PackageVersion`、`PackageNote`、`FileNameStyle`、`ClearBuildCacheFiles`、`UseAssetDependencyDB`、`EnableSharePackRule`、`SingleReferencedPackAlone`、`VerifyBuildingResult`、`BundleEncryptor` / `ManifestEncryptor` / `ManifestDecryptor`。

SBP 额外项：`CompressOption`、`StripUnityVersion`、`DisableWriteTypeTree`、`ReplaceAssetPathWithAddress`、`TrackSpriteAtlasDependencies`、`WriteLinkXML`、CacheServer。

## 4. 项目入口

`Assets/Scripts/Editor/YooAssetEditorBootstrap.cs`：

- 菜单 `YooAsset/FishDemo/执行模拟构建并注入路径`；
- 参数 `PackageBuildParameters { BuildPipelineName = EditorSimulateBuildPipeline, BuildBundleType = VirtualAssetBundle }`；
- 调 `BundleSimulateBuilder.SimulateBuild(...)`，把 `PackageRootDirectory` 注入 `YooAssetInitializer.EditorSimulatePackageRoot`；
- `[InitializeOnLoad]` 在编辑器启动时自动补一次（避免每次手动点）。

## 5. 硬约束

- **模拟构建只验证流程**，上线前必须跑一次真实 SBP/RFBP 构建（见包 [AGENTS.md §5](../../AGENTS.md) 规则 8）。
- **不要修改包内任务类**：需要额外步骤时用 `IBuildTask` 扩展/自定义管线，不要往第三方任务链里插代码。
- 构建输出目录与 `StreamingAssets` 的关系由 `BundledFileRoot` 决定；改路径要同时改部署脚本与 `RemoteService`。
- `PackageVersion` 是更新判定依据；同版本内容变更不会被客户端识别，必须换版本号。
- 构建后的包用 `BundleReporter` 检查依赖图/重复资源，不要只看「构建成功」。

## 6. 相关

| 文档 | 何时读 |
|---|---|
| [yooasset_3.0.7 §8.3–§8.4](../../../../Docs/yooasset_3.0.7.md) | 管线差异、模拟构建原理 |
| [yooasset_3.0.7 §8.5](../../../../Docs/yooasset_3.0.7.md) | 四个编辑器窗口 |
| [../BundleCollector/AGENTS.md](../BundleCollector/AGENTS.md) | 构建输入（收集配置） |
| [yooasset_practices §4](../../../../Docs/yooasset_practices.md) | 交叉引用与模拟模式的局限 |
