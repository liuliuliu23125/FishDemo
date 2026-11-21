# 水下生态（中心湖礁区）

> 在现有中心湖里做「海」的水下表现验证：礁石/珊瑚/海草/鱼群/水母/气泡，复用 Crest 水下与焦散。
> 所有参数与数量以 `UnderwaterEcologySetup.Apply` 为准；改布局先改它再重跑。

## 1. 资产来源与导入

| 包 | 导入路径 | 用途 |
|---|---|---|
| Underwater world - Coral Reef & Marine Life 1.0 | `Assets/Underwater world - Coral Reef & Marine Life/` | 珊瑚 1/2/6/7/8/9、Rock_2、Ship、Coral_Fish 1/2/4、海龟、魟鱼、动画 |
| Ocean Environment Pack 2.0 | `Assets/Davis3D/OceanEnvironmentPack/` | URP Patch 的植物/水母/气泡/GodRay prefab 与贴图、蓝吊/青鳉 VAT |

- 原始 `.unitypackage` 留在 `Reference/`（`.gitignore` 已忽略），仓库只提交按需导入的子集。
- 贴图统一压到 **1024 / ASTC 8x8**；VAT 贴图 `RGBAHalf`、无 mip、`NPOT=None`。原因：源图 4K，移动端显存与导入时间不可接受。
- 新增二进制目录已写入 `.gitattributes`（`.png/.TGA/.FBX/.HDR/.WAV` 与 `Assets/UnderwaterEcology/Meshes/*.asset`）；YAML 材质/prefab 仍走 git。
- Ocean 包的 URP Patch 生成自 Unity 6/URP 17，原 shader 在 2022.3/URP 14 下缺 `ProbeVolumeVariants.hlsl` 等文件。**不直接使用**这些 shader，改为自研 `Plant/GodRay/Bubble/JellyfishVAT`；见 §4。

## 2. 场景结构

`Assets/Scenes/World.unity` 的根节点 `Underwater Ecology`：

| 子节点 | 内容 |
|---|---|
| `Benthic` | 84 个底栖实例：6 种珊瑚（各 4）+ Rock_2（10）+ Ship（1）+ 海草/海带/海葵/海绵/贝壳 |
| `Fish` | 8 个 `FishSchool`：蓝吊 120、青鳉 100、Coral_Fish 1/2/4 各 10、海龟 1、魟鱼 1、水母 8 |
| `Seabed Sand` | 全湖床沙色网格（24577 顶点 / 48896 三角形），沿 `Terrain.SampleHeight + 0.45m` 生成 |
| `God Rays` | 8 个 GodRay 实例；**默认关闭**，运行时 `G` 切换（WIP） |
| `Bubbles` | 4 个气泡发射器；仅水下启用 |
| `Underwater Ambience` | 2D 循环环境音；相机入水后淡入 |

礁区中心 `(192, 246)`（距湖心 150m，水深约 11.3m），底栖分布在半径 30m 内。相机默认机位 `(192, 258, 236)` 看向 `(192, 254, 246)`。

## 3. 运行时

| 脚本 | 职责 |
|---|---|
| `Gameplay/UnderwaterEcologyController` | 用 `Camera.main` 判定入水；`J` 生态总开关；`G` GodRay 开关（默认关）；距礁 >120m 且在水上时禁用 `Benthic`；环境音淡入淡出 |
| `Gameplay/FishSchool` | 参数化鱼群（不做 O(n²) boids）：绕移动中心椭圆轨道 + 垂直摆动；支持 prefab（skinned/水母）与 mesh（VAT）；mesh 模式在 28m 处切 LOD0/LOD1 网格与材质 |
| `Gameplay/UnderwaterDemoControls` | `U` 切水下机位（暂停 `LakeOrbitCamera`），再按 `U` 回环湖轨道；`F` 仍为自由飞行 |

按键：`U` 水下机位、`J` 生态开/关、`G` GodRay（默认关）、`C` 相机、`F` 飞行。

## 4. 自研 shader

| shader | 用途 | 要点 |
|---|---|---|
| `FishVAT` | 蓝吊/青鳉 | 读 UV1 的 VAT 变形；`GetMainLight + SampleSH`；开启 instancing；一个材质 = 一个物种的 1 个 draw call |
| `Plant` | 海草/海带/珊瑚/海绵 | UV.y 高度加权的顶点摆动；alpha clip；`Cull Off`；无 diffuse 的材质用 `_DiffuseMultiply` 作基色 |
| `SeabedSand` | 海床 | 简化光照（主光 + 固定水下环境色），不采样 light probe/shadow，保证湖底稳定可见 |
| `JellyfishVAT` | 水母 | VAT + 透明加法混合 + 自发光；LOD0/LOD1 各一套材质 |
| `UnderwaterBubble` | 气泡 | 程序化圆形 rim/中心 alpha + 粒子顶点 alpha；**不采样** refraction 贴图（该贴图没有可用 alpha，直接采样会把 billboard 画成不透明） |
| `UnderwaterGodRay` | GodRay | 加法渐变；**当前网格/缩放下会形成大面积色块，默认关闭，WIP** |

全部自研 shader 走 URP 14 的 `Core.hlsl` / `Lighting.hlsl`，无 URP 17 专属 include；SRP Batcher 兼容，支持 instancing。自研 shader 内不写注释。

## 5. 焦散

- 复用 Crest 已有水下 pass：给 `Packages/com.waveharmonic.crest/Runtime/Materials/Water.mat` 写 `_Crest_CausticsTexture`（Crest `Caustics.png`）与 `_Crest_Caustics*` 参数；`Water Volume.mat` 同步一份。
- **0 个新 pass、0 个新全屏 RT、0 次 blit**；只在相机入水时已有的 underwater pass 内增加 2 次焦散纹理采样。
- `UnderwaterEcologySetup.Remove` 会关闭焦散并清空贴图。

## 6. 批处理与性能账

| 项 | 值 |
|---|---|
| 新 pass / 全屏 RT / blit | 0 / 0 / 0 |
| Crest 焦散 | 复用 underwater pass，+2 次纹理采样 |
| 底栖 | 84 实例；唯一材质约 29；LOD0 合计 ≈983k 三角形（Ocean prefab 带 LODGroup，运行时按屏占比降级） |
| 海床 | 1 个 Mesh，48896 三角形，1 次 draw |
| 鱼群 | 2 个 VAT 学校：各 1 个 instanced draw（260 条鱼 = 2 draw）；30 条 skinned + 海龟/魟鱼各 1；VAT 鱼近景 ≈305k、远景 LOD1 ≈131k |
| 透明效果 | 4 个气泡粒子系统；GodRay 默认关闭 |
| 开关 | `J` 关整片生态；`G` 关 GodRay；水上且距礁 >120m 时 `Benthic` 全关；VAT 鱼 28m 切 LOD1 |
| 真机 | **未验**；Editor 近景 LOD0 生态合计约 1.4M，叠世界后可能超 1.5M/frame 预算，需真机与降级 |

## 7. 验收

| 入口 | 判什么 | 条件 |
|---|---|---|
| `UnderwaterEcologySetup.Apply` | 幂等重建根节点、材质、海床、鱼群、焦散；末页校验 PASS | 可 `-nographics` |
| `UnderwaterEcologySetup.Report` | 只读校验现有根节点 | 可 `-nographics` |
| `UnderwaterEcologySetup.Remove` | 删根节点与生成材质/网格/prefab（保留 shader），关闭焦散 | 可 `-nographics` |
| `UnderwaterEcologyVerify.Run` | 结构：实例数、空材质、skinned/VAT、焦散、鱼群配置 | 可 `-nographics` |
| `UnderwaterEcologyPreview.Run` | **需图形设备**：水下水上截图 + 生态开/关像素差 | 1280×720；`Logs/Underwater/underwater-preview-*.png` |
| `UnderwaterEcologyProbe.Run` | 只读：LOD0 三角面、VAT 网格、GTS 与 Terrain 高度差 | 可 `-nographics` |

2026-11-21 Editor 实测：`Setup.Apply` PASS；`Verify` PASS（224 renderer / 29 材质 / 0 空材质 / 8 学校 / 260 鱼 / 焦散 True）；`Preview` PASS（开/关像素差 45.1%，`onCaptureUnderwater=True`）。

## 8. 已知限制

- GodRay 默认关闭，当前网格/缩放会产生大面积色块；`G` 仅用于继续调试。
- 水母自研材质是近似（加法混合 + VAT + 自发光），与包内 HDRP/URP17 原效果不同。
- 水面在 WebGL/GLES 不可用（Crest 全局约束），本工程仅 Vulkan。
- 生态数量是 demo 上限，真机 30fps 未验；低画质档优先 `J` 关生态、减少鱼数、把 VAT LOD 距离降到 15m、关闭气泡。
