# YooAsset · Editor/BundleCollector 规约

> 第三方编辑器代码，不改。数据结构与规则行为见 [yooasset_3.0.7 §8.1–§8.2](../../../../Docs/yooasset_3.0.7.md)。

## 1. 数据模型（四层）

```
BundleCollectorSetting            （Assets/BundleCollectorSetting.asset）
  └─ BundleCollectorPackage       （一个 YooAsset 包）
       └─ BundleCollectorGroup   （标签/启用规则）
            └─ BundleCollector   （收集路径/地址/打包/过滤规则）
```

- Setting：`UniqueBundleName`（多包时建议开）、`Packages`。
- Package：`EnableAddressable`、`SupportExtensionless`、`LocationToLower`、`IncludeAssetGUID`、`AutoCollectShaders`、`IgnoreRuleName`。
- Group：`AssetTags`、`ActiveRuleName`（`EnableGroup` / `DisableGroup`）。
- Collector：`CollectPath` / `CollectorGUID` / `CollectorType` / `AddressRuleName` / `PackRuleName` / `FilterRuleName` / `AssetTags` / `UserData`。

## 2. 规则接口

| 接口 | 默认实现 | 作用 |
|---|---|---|
| `IAddressRule` | `AddressByFileName` / `AddressByFolderAndFileName` / `AddressByGroupAndFileName` / `AddressDisable` | 资源定位地址 |
| `IBundlePackRule` | `PackDirectory` / `PackTopDirectory` / `PackCollector` / `PackGroup` / `PackSeparately` / `PackRawFile` / `PackVideoFile` / `PackShader` / `PackShaderVariants` | bundle 粒度 |
| `IAssetFilterRule` | `CollectAll` / `CollectScene` / `CollectPrefab` / `CollectSprite` / `CollectShader` / `CollectShaderVariants` | 收集哪些资源 |
| `IAssetIgnoreRule` | `NormalIgnoreRule` / `RawFileIgnoreRule` | 忽略规则 |
| `IGroupActiveRule` | `EnableGroup` / `DisableGroup` | 分组是否参与构建 |

**打包粒度是泄漏与 draw 成本的源头**：`PackDirectory` 按目录成包；chunk 交叉引用的全局禁令见包 [AGENTS.md §5](../../AGENTS.md) 规则 3。

## 3. 项目现状

- 配置资产：`Assets/BundleCollectorSetting.asset`；当前只有 `DefaultPackage` 的 `Scene` 组（`PackDirectory` + `AddressByFolderAndFileName` + `CollectAll`）。
- 生成入口：菜单 `YooAsset/FishDemo/初始化资源收集配置`（`YooAssetSetup.SetupCollector`）；它只对**已存在**的目录建组，缺失目录会 warning 跳过。
- 包标志：`EnableAddressable=1`、`SupportExtensionless=1`、`LocationToLower=1`、`IncludeAssetGUID=0`、`AutoCollectShaders=1`。

## 4. 硬约束

- **不要手改 YAML 里的 Collector 列表**：用 `BundleCollectorWindow` 编辑，GUID/规则名容易写错。
- 改收集路径后必须重新构建；旧 bundle 不会自动消失。
- 多包同名 bundle 必须开 `UniqueBundleName`，否则运行时按 bundle 名解析会串包。
- `CollectorType=MainAssetCollector` 与 `DependAssetCollector` 的语义不同；依赖收集器不应被当作主资源加载。

## 5. 相关

| 文档 | 何时读 |
|---|---|
| [yooasset_3.0.7 §8.1–§8.2](../../../../Docs/yooasset_3.0.7.md) | 数据结构、规则实际行为 |
| [yooasset_3.0.7 §8.5](../../../../Docs/yooasset_3.0.7.md) | 四个编辑器窗口 |
| [../BundleBuilder/AGENTS.md](../BundleBuilder/AGENTS.md) | 构建管线如何使用这些配置 |
