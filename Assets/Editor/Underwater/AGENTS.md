# Assets/Editor/Underwater 规约（水下生态）

> 默认程序集 `Assembly-CSharp-Editor`，无 asmdef。可引用 `FishDemo.Gameplay`、`ChunkLoadScope`、`LakeCarve`。
> 细节与性能账见 [underwater_ecology](../../../Docs/underwater_ecology.md)。

## 1. 脚本

| 脚本 | 职责 | 入口 |
|---|---|---|
| `UnderwaterEcologySetup.cs` | 幂等布置 `Underwater Ecology` 根节点；生成海床、自定义材质、VAT 材质、水母 prefab；配置贴图导入、Crest 焦散 | `Apply` / `Remove` / `Report` |
| `UnderwaterEcologyVerify.cs` | 结构验收：实例数、空材质、鱼群配置、VAT、焦散开关 | `Run`（可 `-nographics`） |
| `UnderwaterEcologyPreview.cs` | **需图形设备**：水下/水面/海床截图 + 生态开/关像素差 | `Run`（**不要**加 `-nographics`） |
| `UnderwaterEcologyProbe.cs` | 只读：LOD0 三角面、VAT 网格 UV1、GTS 与 Terrain 高度差 | `Run`（可 `-nographics`） |

## 2. 重建与验收

```
UnderwaterEcologySetup.Apply
  → UnderwaterEcologyVerify.Run
  → UnderwaterEcologyPreview.Run   (图形设备)
```

- 编辑器菜单：`FishDemo/Underwater/重建水下生态`、`校验水下生态`、`移除水下生态`。菜单路径不退出编辑器；批处理路径仍用 `EditorApplication.Exit`。
- 重跑 `WorldPipeline` / `SectrPipeline` 后湖与 GTS 会丢，先按 [water §2](../../../Docs/water.md) 重建湖，再跑 `Apply`。
- `Apply` 会重建根节点和 `Assets/UnderwaterEcology/{Materials,Meshes,Prefabs}`；**shader 目录保留**。
- `Remove` 会关闭 `Water Volume.mat` 的焦散并清空贴图；`Water.mat` 同步写 0（surface 未采样焦散）。`Apply` 会重新打开。
- `Apply` 的贴图导入有幂等判断；已配置的贴图不会重复 `SaveAndReimport`。

## 3. 布局硬约束

- 湖参数唯一来源是 `LakeCarve`；礁区中心/半径/海床高度都从这里推导。
- 海床采样必须优先取**带 `GTS_Mesh` 的 Terrain**，并按面积从小到大排序；否则会采到重叠的大 Terrain，沙面会沉到 GTS 下。
- 海床 `SandLift` 与网格分辨率共同决定是否盖住 GTS；当前 `0.45m / 96×256`，日志会打印礁区最近顶点高度与 Terrain 高度。
- 底栖对象的位置也用海床高度采样，避免珊瑚/海带悬空或埋入沙里。
- 生成材质必须 `enableInstancing=true`；LODGroup 由 Ocean prefab 自带，不要拆。
- GodRay 默认关闭；`G` 只是调试入口，当前效果不合格。

## 4. 贴图/模型导入

- 贴图：`1024 / ASTC_8x8`；法线用 NormalMap；VAT `.HDR` 用 `RGBAHalf`、无 mip、`NPOT=None`。
- `SM_Bluetang.FBX` / `SM_Medaka.FBX` 需要 `isReadable` 才能生成 VAT 备用 mesh；源 mesh 已有 UV1 时直接用源 mesh，不覆盖（覆盖会打乱 861/665 列的 VAT 映射）。
- 第三方 URP Patch 的 shader 不兼容 URP 14；不要重新启用 `Assets/Davis3D/.../Shaders/Shader_*.shader`，用 `Assets/UnderwaterEcology/Shaders` 的自研版本。

## 5. 预览注意事项

- batchmode 下 Game View 不渲染，`WaterRenderer.SeaLevel` 可能是 0；预览脚本先做一次 `Camera.Render()` priming，再等 `SeaLevel>1`。
- 相机/Enviro/Crest Viewer 必须同步：预览脚本显式设置 `Camera.main` tag、`WaterRenderer.Viewer`、`EnviroManager.ChangeCamera`。
- 预览会禁用 `GrassBRGManager`、`LakeOrbitCamera`、`LakeCameraRig`、`PegasusTourController`，避免与验收机位抢 transform。
