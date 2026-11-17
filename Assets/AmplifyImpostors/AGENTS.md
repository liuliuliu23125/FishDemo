# Assets/AmplifyImpostors 规约（第三方插件）

> **第三方源码，不要改动**。版本 **1.1.0**（`ChangeLog.txt`）。
> 本目录只记录项目怎么用它；选型与实测见 [vegetation](../../Docs/vegetation.md) §3、§6.6。

## 1. 目录与程序集

| 路径 | 内容 |
|---|---|
| `Plugins/Scripts/` | 运行时：`AmplifyImpostor`、`AmplifyImpostorAsset`、烘焙 API。程序集 `AmplifyImpostors.Runtime` |
| `Plugins/Editor/` | 编辑器：bake preset、batch set。程序集 `AmplifyImpostors.Editor`（Editor only） |
| `Plugins/EditorResources/Shaders/` | impostor shader；URP 用 `OctahedronImpostorURP.shader` |
| `Plugins/ShaderGraph/URP/` | URP ShaderGraph fallback |
| `Examples/` | 官方示例，项目未使用 |

## 2. 项目内的落地

产物在 `Assets/PW_Tree_Spruce_*_Impostor.asset`。烘焙范围、入口、2 级 LOD 与 Terrain 接入见 [Vegetation §4](../Editor/Vegetation/AGENTS.md) 与 [vegetation §6.6](../../Docs/vegetation.md)。

## 3. 烘焙与改结构的坑

1. `IsReady` 要求 prefab 上**先有** `AmplifyImpostor` 组件；`autoCreate` 只放宽 Data 为空，不负责加组件。
2. `m_lodReplacement` **只在烘焙时读取应用**。已烘过但要改 LOD 结构的 prefab，必须手动改 LODGroup。
3. 烘焙会临时加载 prefab 内容；批量烘用 `AmplifyImpostorBakeAPI.BakeImpostorsAtPaths`。
4. impostor 材质用 `_Albedo` 属性，不是 `_MainTex`。`TreeMaterialAudit` 报「`_MainTex` 为空」是**误报**。
5. 升级插件后必须重烘；旧产物不保证兼容新 shader。

## 4. 相关文档

| 文档 | 何时读 |
|---|---|
| [vegetation](../../Docs/vegetation.md) §3 | 版本兼容、性能警告、烘焙注意 |
| [vegetation](../../Docs/vegetation.md) §6.6 | 本项目 impostor 接入的实测结论 |
| [../Editor/Vegetation/AGENTS.md](../Editor/Vegetation/AGENTS.md) | 烘焙脚本、树材质与 LOD |
