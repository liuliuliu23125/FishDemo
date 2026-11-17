# GPU ECS Animation Baker 1.3.6 落地手册

> 包位置：`Assets/GPUECSAnimationBaker`（从 `Reference/GPU ECS Animation Baker [1.3.6].unitypackage` 导入，非 UPM 包）。
> 本文件记录**当前代码状态 + 使用方式 + 待验证项**；硬约束与索引入口见
> [Assets/GPUECSAnimationBaker/AGENTS.md](../Assets/GPUECSAnimationBaker/AGENTS.md)。
> 何时读：接 NPC / 人群、改 VAT 材质或 shader、评估性能、排查实体不显示。

## 0. 现状速览

| 项 | 状态 | 依据 |
|---|---|---|
| 导入 | 已导入 `Assets/GPUECSAnimationBaker`（Engine + Samples） | 目录清单 |
| 版本 | unitypackage 文件名 `[1.3.6]`；源码无版本常量；Manual 只写 `v.1` | Manual「Requirements」原文 |
| 程序集 | `GPUECSAnimationBakerEngine` / `GPUECSAnimationBakerSamples` 已生成 DLL | `Library/ScriptAssemblies/GPUECSAnimationBaker*.dll` |
| 依赖 | Entities 1.4.8 / Entities Graphics 1.4.21 / Newtonsoft 3.2.2 / DOTween | `packages-lock.json`、asmdef |
| 工程内调用方 | **有**：`LakesideCrowd` **200 人整圈**（Ch19/Ch14/Ch09/Ch46 四种角色，4× 大小，r=191–205） | `Assets/Scenes/LakesideCrowd.unity`、`Assets/Scripts/Runtime/Gameplay/LakesideCrowdBehaviour.cs` |
| 200 人整圈验证 | **PASS**：实体 200/200、半径 191–205、Y 267.06–301.84、动画 30 帧内变化；近景 draw=9/rendered=10、俯瞰 draw=4/rendered=95、弧段 draw=8/rendered=37 | `Logs/Crowd/lakeside-verify.txt`、`lakeside-30-overview.png`、`lakeside-30-crowd.png` |
| 样例运行 | 样例场景未跑；自研 200 人已在 Forward 下验证渲染 | `Logs/Crowd/lakeside-verify.txt` |
| 真机 | 未验证 | — |

## 1. 依赖与兼容

### 1.1 锁定版本

| 包 | 版本 | 备注 |
|---|---|---|
| Unity | 2022.3.62f2 | 满足 Manual「Unity 2022.2.20f1 或更新」 |
| Entities | 1.4.8 | 满足 Manual「ECS 1.0.8 或更新」 |
| Entities Graphics | 1.4.21 | `package.json`：`"unity": "2022.3"`, `"unityRelease": "20f1"` |
| Burst / Collections / Mathematics | 1.8.29 / 2.6.8 / 1.3.2 | Engine asmdef 的 GUID 引用均可解析 |
| Newtonsoft.Json | 3.2.2 | asmdef `precompiledReferences` |
| DOTween | `Assets/Plugins/Demigiant/DOTween/DOTween.dll`、`.../Editor/DOTweenEditor.dll` | asmdef `precompiledReferences` |

【代码事实】`GPUECSAnimationBakerEngine.asmdef` 引用 Burst / Mathematics / Entities.Graphics / Entities / Transforms / Collections / Entities.Hybrid；
Samples asmdef 额外引用 Engine 与 TextMeshPro。`Library/ScriptAssemblies` 中两个 DLL 已生成。

### 1.2 渲染路径冲突（接入前必读）

| 结论 | 类型 | 依据 |
|---|---|---|
| Entities Graphics 1.4 文档：URP **只支持 Forward+** | 文档声称 | `Library/PackageCache/com.unity.entities.graphics@1.4.21/Documentation~/requirements-and-compatibility.md:15`；功能矩阵 `entities-graphics-versions.md:33` 同样写 `Rendering Path: Only Forward+ is supported` |
| 引擎在非 Forward+ 时只打 warning，不阻止渲染 | 代码事实 | `.../Unity.Entities.Graphics/EntitiesGraphicsSystem.cs:886-906`，文案 `Entities.Graphics should be used with URP Forward+.` |
| URP 14.0.12 **已有** Forward+（`RenderingMode.ForwardPlus = 2`） | 代码事实 | `Library/PackageCache/com.unity.render-pipelines.universal@14.0.12/Runtime/UniversalRenderer.cs:11-19`；渲染实现见同文件 `:220`, `:247`, `:1357` |
| URP 14 官方文档包含 Forward+ 章节与启用方式 | 文档事实 | `.../Documentation~/rendering/forward-plus-rendering-path.md`；`Documentation~/TableOfContents.md:23` |
| 工程当前是 **Forward**，per-object light limit = 4 | 代码事实 | `Assets/Settings/UniversalRenderer.asset:57` `m_RenderingMode: 0`；`Assets/Settings/UniversalRP.asset:40` `m_AdditionalLightsPerObjectLimit: 4` |
| URP 14 + Forward 实测：200 个 VAT 实体可渲染 | 运行证据 | 俯瞰 `draw=4 / rendered=95`；弧段 `draw=8 / rendered=37`；截图 `Logs/Crowd/lakeside-30-overview.png`、`lakeside-30-crowd.png`；控制台仍有 Forward+ warning |

【推断】旧项目文档“2022.3 没有 Forward+”来自 Unity 6 / URP 17 的旧认知，对 URP 14.0.12 不成立。
但**当前渲染路径仍是 Forward**，所以 per-object limit 4 的约束现在仍然有效。

**当前决策：不切 Forward+**（保持工程现有 Forward）。200 人混合角色已在 Forward 下渲染 PASS（俯瞰 rendered=95、弧段 rendered=37）；不再以 Forward+ 作为接入前提。切换仍会改变主光/附加光/探针路径，除非 Forward 下出现 200 人渲染异常，否则不动。

### 1.3 Android

- Entities Graphics 文档：Android 的 URP **仅支持 Vulkan 与 GLES 3.1+**（`requirements-and-compatibility.md`「Unity Player system requirements」）。
- 工程已是 **仅 Vulkan**（`m_APIs=15000000`，`m_Automatic=0`），满足文档要求。
- 真机兼容性、IL2CPP/ARM64 构建、Entities Graphics shader 变体剥离均未验证。

## 2. 原理链路（第一性原理）

### 2.1 烘焙

```text
源 prefab：Animator + SkinnedMeshRenderer(+LODGroup)
  ├─ 顶点骨骼权重 → UV1 / UV2 / UV3
  ├─ 每帧骨骼矩阵 → AnimationMatricesTexture（RGBAHalf，宽 = 骨骼数×3，高 = 总帧数）
  ├─ 材质复制 → 绑定 _AnimatedBoneMatrices
  └─ Mesh 复制 → MeshRenderer + GpuEcsAnimatedMeshBehaviour
```

- 【文档】Manual「Overview」：权重烘进 uv1/uv2/uv3，骨骼变换逐帧烘进动画纹理，Custom vertex shader 负责采样，ECS 每帧更新 shader 值。
- 【文档】Manual「Limitations」：**只能用 UV0**；材质必须是 ShaderGraph；经典 Animator 运行时完全不运行；IK 永不支持。
- 【代码事实】采样实现：`Engine/Shader/GpuEcsAnimator.cginc:10` `loadBoneMatrixTexture`；`:54` `calculateFrameValues`；`:83` `AnimateBlend_half`。
  纹理寻址为 `(boneIndex * 3 + i, frameIndex)`，每骨骼 3 个 RGBA 行；顶点最多 6 组权重；最多 4 个 blend slot（当前/过渡/双 clip）。
- 【文档 vs 代码】Manual「QuickStart」写动画纹理“每骨骼 4 列”，但 `GpuEcsAnimator.cginc` 实际按 `boneIndex * 3 + i`（i=0..2）取 3 列，
  第 4 行由着色器补 `(0,0,0,1)`；Ch19 纹理宽度 171 = 57 骨骼 × 3，与代码一致。以代码为准。

### 2.2 运行时

```text
GpuEcsAnimatorSystem   → 计算 frameIndex / blendFactor → 写 GpuEcsAnimatorShaderDataComponent
GpuEcsAnimatedMeshSystem → 复制到 GpuEcsMaterialAnimationState（[MaterialProperty("_AnimationState")]）
Entities Graphics      → DOTS instancing 批量提交，per-instance 覆盖 _AnimationState / _EnableAnimation
顶点着色器             → 采样 _AnimatedBoneMatrices，输出变形后的位置/法线/切线
```

| 符号 | 作用 | 代码位置 |
|---|---|---|
| `GpuEcsAnimatorSystem` | 推进动画状态、处理过渡/事件/锚点 | `Engine/AnimatorSystem/GpuEcsAnimatorSystem.cs:11,14,33` |
| `GpuEcsAnimatedMeshSystem` | 把动画状态写给每个 mesh 实体 | `Engine/AnimatorSystem/GpuEcsAnimatedMeshSystem.cs:7,18,32` |
| `GpuEcsMaterialAnimationState` | `_AnimationState` 的 per-instance 值 | `Engine/AnimatorSystem/GpuEcsAnimatedMeshComponents.cs:12` |
| `GpuEcsMaterialEnableAnimation` | `_EnableAnimation` 的 per-instance 值 | 同上 `:18` |
| `GpuEcsAnimatorShaderDataComponent` | animator 解算结果（float4x4） | `Engine/AnimatorSystem/GpuEcsAnimatorComponents.cs:28` |
| `GpuEcsAnimatorControlComponent` | 用户唯一需要写的控制组件 | `Engine/AnimatorSystem/GpuEcsAnimatorControlComponents.cs:5` |
| `GpuEcsAnimatorAspect` | `RunAnimation / StartAnimation / StopAnimation` | `Engine/AnimatorSystem/GpuEcsAnimatorAspect.cs:9,26,33` |

### 2.3 为什么能承载大量角色

- 变形在顶点着色器完成，**没有骨骼层级变换、没有蒙皮 Job、没有 Animator**。
- 动画纹理按**角色类型**共享；N 个同类型实例只占一份纹理。
- ECS 每帧只更新每个实例的 `float4x4` 动画状态；CPU 成本与实例数线性但常数很小（官方样例展示 200×200 人群）。
- 代价：顶点着色器采样纹理的带宽、UV 通道被占用、材质必须 ShaderGraph、无运行时 IK。

## 3. 资产结构与角色清单

### 3.1 目录

```text
Assets/GPUECSAnimationBaker/
├── Engine/
│   ├── AnimatorSystem/     运行时组件、系统、Aspect
│   ├── Baker/              烘焙器、编辑器菜单、批量烘焙 API
│   ├── Data/               动画数据、枚举、绘制器
│   └── Shader/             GpuEcsAnimator.cginc + Subgraph
├── Samples/
│   ├── Characters/         12 个已烘焙 prefab（角色 + 附件）；BakedAssets_* 为运行时产物
│   ├── SampleScenes/       0_Basics … 6_Events、Festival、Marathon
│   └── Shaders/            URP/HDRP 的 Simple / Color ShaderGraph
└── GPU ECS Animation Baker Manual.pdf
```

### 3.2 一个烘焙产物的组成（以 Ch19 为例）

`Samples/Characters/Ch19/BakedAssets_Ch19/`：

| 文件 | 事实 |
|---|---|
| `Ch19_GpuEcsAnimator.prefab` | 根：`GpuEcsAnimatorBehaviour`；4 个子物体：`GpuEcsAnimatedMeshBehaviour` + MeshFilter + MeshRenderer；LODGroup 4 级 |
| `Ch19_Mesh_Ch19*.mesh` | 4 份 LOD mesh（UV1/2/3 存骨骼权重） |
| `Ch19_Material_Ch19.mat` | 1 份材质，4 个 LOD 共用；Shader = `GPUECSAnimator_URP_SimpleShader.shadergraph`（guid `de8b1051…`） |
| `Ch19_AnimationMatricesTexture_Ch19.asset` | `m_Width: 171`、`m_Height: 1254`、`m_TextureFormat: 17`（RGBAHalf）、`m_CompleteImageSize: 1715472` ≈ **1.64 MiB** |
| `Ch19_AnimationIDs.cs` | 5 个动画枚举；生成的 initializer **未挂到 prefab 根**，默认动画 ID = 0 |

【代码事实】`m_Width: 171`、`m_Height: 1254`；按 cginc 的 3 列/骨骼推算骨骼数 = 57；1254 = prefab 的 `totalNbrOfFrames`。

### 3.3 已烘焙角色

数据来自各 `*_GpuEcsAnimator.prefab` 的 YAML：

| 角色 | 总帧 | 动画数 | 全部循环 | LOD 级数 | Renderer / Mesh / Material |
|---|---:|---:|---|---:|---|
| Ch09 | 1121 | 4 | 是 | 4 | 4 / 4 / 1 |
| Ch14 | 1617 | 4 | 是 | 4 | 4 / 4 / 1 |
| Ch19 | 1254 | 5 | 是 | 4 | 4 / 4 / 1 |
| Ch36 | 128 | 4 | 是 | 无 LODGroup | 1 / 1 / 1 |
| Ch46 | 1678 | 5 | 是 | 4 | 4 / 4 / 1 |
| ClownZombie | 122 | 1 | 是 | 无 LODGroup | 1 / 1 / 1 |
| Maria | 695 | 4 | 否（仅第 1 段循环） | 无 LODGroup | 2 / 2 / 2 |
| Maria_Attachments | 320 | 1 | 是 | 无 LODGroup | 1 / 1 / 1 |
| Mutant | 85 | 1 | 是 | 3 | 3 / 3 / 1 |
| xBot_RunnerMarathon | 430 | 2 | 是 | 4 | 8 / 8 / 2 |
| yBot_RunnerMarathon | 430 | 2 | 是 | 4 | 8 / 8 / 2 |
| Zombie | 1485 | 3 | 是 | 4 | 8 / 8 / 2 |

有动画枚举文件的角色：

- **Ch19**：`ChickenDance / HokeyPokey / NorthernSoulSpin / SwingDancing / WaveHipHopDance`，5 段全循环。
- **Ch36**：`WalkNormal / WalkSomewhatGraceful / WalkMoreGraceful / WalkVeryGraceful`。
- **Maria**：`StrafeWalk`（循环）/ `OutwardSlash` / `InwardSlash` / `Fight`（后 3 段一次性）。
- **xBot_RunnerMarathon**：`WalkToRun / RunToSprint`；yBot 同结构。

【推断】湖边“站满人并同时做动作”优先用 **Ch19**：5 段循环舞蹈、1 份材质、4 级 LOD，单角色类型即可让 30+ 实例只占 1 份动画纹理。
其他角色没有 `_AnimationIDs.cs` 的，动画名称需查源 prefab 或 Manual 的烘焙配置，不要猜。

## 4. 烘焙流程

### 4.1 手工流程（官方 Manual）

1. **改材质**：在源角色使用的 ShaderGraph 中加入 `GpuEcsAnimator` Subgraph；暴露 3 个属性：
   - `EnableAnimation`：bool / Vector1，`Override Property Declaration` + `Hybrid Per Instance`；
   - `AnimatedBoneMatrices`：Texture2D，普通暴露；
   - `AnimationState`：Matrix4x4，`Override Property Declaration` + `Hybrid Per Instance`。
   最省事的做法是直接复制 `Samples/Shaders/` 里的样例 shader。
2. **挂烘焙器**：选中带 Animator + SkinnedMeshRenderer 的对象，通过包的菜单挂 `GpuEcsAnimationBakerBehaviour`。
3. **配置**：
   - Animations：Animation ID、Animator State Name、Single Clip / Dual Clip Blend、Loop、额外参数与层；
   - Generate AnimationIds Enum：可选，生成枚举 + initializer；
   - Events / Attachment Anchors：可选；
   - Bone Usage：每顶点默认骨骼数，可按 LOD 覆盖（越多越准，GPU 越贵）；
   - Transform Usage Flags Parent & Children。
4. **生成**：产物写入 `BakedAssets_<Name>/`，包括枚举/initializer、动画纹理、材质副本、带 UV 权重的 mesh 副本、`*_GpuEcsAnimator.prefab`。

【文档】动画采样由 **Animator State** 决定；Animation Clip 只用来确定采样长度。只改 clip 不改 state，烘焙结果不会变。

### 4.2 批量烘焙 API

`Engine/Baker/GpuEcsAnimationBakeServices.cs:104` `GenerateAnimationObject(assetPath, bakerData, …)`；
`:129` `GenerateAnimationObjectFromModel(refModel, sourceModel, …)`。
可以复用同一份 `GpuEcsAnimationBakerData` 批量生成多个角色，并自选输出目录。

### 4.3 本工程的约定

- 源角色资产可以放 `Assets/` 下自有目录；**生成物留在源资产附近的 `BakedAssets_*`**，不要搬进 `Assets/Editor`。
- 自研烘焙脚本放 `Assets/Editor/`（默认程序集）或 `Assets/Scripts/Editor/`（`FishDemo.Editor`）；后者若引用 GPU Baker，先确认 asmdef 可引用 Engine 程序集。
- 不要手改生成物；改源 prefab / 材质 / 动画配置后重跑烘焙。

## 5. 运行时使用

### 5.1 最小控制面

只写 `GpuEcsAnimatorControlComponent`：

```csharp
entityManager.SetComponentData(entity, new GpuEcsAnimatorControlComponent
{
    animatorInfo = new AnimatorInfo
    {
        animationID = animationId,
        blendFactor = 0f,
        speedFactor = 1f
    },
    startNormalizedTime = start01,
    transitionSpeed = 0.2f
});
```

需要停止/重启时再写 `GpuEcsAnimatorControlStateComponent { state = Start/Stop/KeepCurrentState }`。
`GpuEcsAnimatorAspect` 是同一件事的封装。

### 5.2 生成方式

官方 `CrowdSpawnerBehaviour` 的 Baker 把预制体数组转成实体 buffer；`CrowdSpawnerSystem`：

- `CrowdSpawnerSystem.cs:98` `CreateNewAnimator`
- `:106` `ecb.Instantiate(prefab)`
- `:110` 写 `LocalTransform`（位置/旋转/缩放）
- `:126` 写 `GpuEcsAnimatorControlComponent`（随机动画 ID + 随机 `startNormalizedTime`）

【推断】30+ 湖边人群可复用同一模式：位置列表由离线地形采样得到，动画 ID 在 prefab 的 `GpuEcsAnimationDataBufferElement` 长度内随机，
`startNormalizedTime` 随机化以避免整齐同步。

### 5.3 固定初始动画

如果不想写运行时控制代码，可把生成的 `*_AnimationInitializerBehaviour` 挂到烘焙 prefab 根，在 Inspector 选初始动画。
当前 Ch19 / Maria 的 prefab **都未挂** initializer。

## 6. 性能账（估算，真机未测）

### 6.1 渲染

| 项 | 结论 | 类型 |
|---|---|---|
| 批处理 | Entities Graphics 用 BRG + DOTS instancing；不是 SRP Batcher | 文档 + 代码 |
| Draw call（Ch19） | 1 份材质 + 4 级 LOD；同 LOD 的实例合批，理想 1 条 draw command/LOD | 推断（依据 prefab 结构） |
| Draw call（Maria） | 2 份材质（身体 + 剑）→ 2 条 draw command | 推断 |
| 阴影 | 烘焙 prefab 的 MeshRenderer `m_CastShadows: 1`、`m_ReceiveShadows: 1`；30+ 实例会进 shadow pass | 代码事实 |
| LOD | 由烘焙的 LODGroup 决定；没有运行时 LOD 之外的自适应密度 | 代码事实 |
| SRP Batcher | 对实体无效；普通物件仍靠 SRP Batcher，且 URP Asset 必须保持开启（Entities Graphics 文档「creating-a-new-entities-graphics-project」要求） | 文档 |

### 6.2 顶点纹理采样

【代码事实】每次 `calculateFrameValues` 对每个非零权重骨骼加载 3 个 texel；帧间插值取 frame、frame+1；`AnimateBlend_half` 最多 4 个 blend slot。
【推断】稳定状态下 1 个 blend slot、每顶点 4 个有效骨骼 → 2 帧 × 4 骨骼 × 3 texel = **24 texel / 顶点**；
过渡期 2 个 slot 翻倍；最坏（6 骨骼 × 4 slot）144 texel / 顶点。实际顶点数与有效骨骼数需在 Unity 里实测。

### 6.3 内存

| 项 | 值 |
|---|---|
| Ch19 动画纹理 | 171 × 1254 × 8B = 1,715,472 B ≈ 1.64 MiB |
| 同类型 30 个实例 | 纹理不复制，仍 1.64 MiB |
| 不同角色类型 | 每种角色各自一份动画纹理；按 `totalNbrOfFrames` 与骨骼数线性增长 |

### 6.4 与项目上限的关系

项目硬约束已从「同屏角色上限 20」逐步改为 **200**；当前 200 人整圈落地（Editor/Forward，4 种角色 × 4× 大小）。真机 30fps 尚未验证，200 是当前设计上限而不是实测达标值。

- 30 是设计上限，不等于已达标；真机数据出来前不能把“30 人 30fps”写成验收结论。
- 需要测：draw call、GPU 时间、顶点纹理带宽、阴影开销、LOD 分布、内存峰值。
- 若实测不够，优先关阴影 / 减少同时可见数 / 分档；不要用宣传语替代实测。

## 7. 验证

### 7.1 当前能做到的

- 编译：`LakesideCrowd` 接入后已重新编译通过；1 人验证 PASS。
- 200 人整圈：`LakesideCrowdVerify.Run` **PASS**；结果 `Logs/Crowd/lakeside-verify.txt`，截图 `Logs/Crowd/lakeside-verify.png`、`lakeside-30-overview.png`、`lakeside-30-crowd.png`。
  抓图前等 10s，因为地形 chunk/GTS 初始化慢，不是没加载。
- 样例场景：不在 Build Settings。要在编辑器里直接打开 `Samples/SampleScenes/0_Basics/0_Basics.unity` 或 `FestivalEntities.unity`；
  要通过菜单/`SceneManager.LoadSceneAsync` 跑，必须先把对应场景加入 Build Settings。
- 批处理入口：`bash Tools/run-unity.sh <executeMethod>`（会先杀残留 Unity 进程、清 `Temp/UnityLockfile`）。

### 7.2 接入湖边人群前的最小验证顺序

1. 打开 `0_BasicsEntities.unity`，确认 Entities 世界、Entities Graphics、VAT 动画都能跑（控制台不应出现 Forward+ warning 以外的错误）。
2. 打开 `FestivalEntities.unity`，确认多人同屏；记录 DrawCommandCount / BatchCount / RenderedInstanceCount（`EntitiesGraphicsSystem` 统计）与 Frame Debugger。
3. 在 `World.unity` 里放 1 个 Ch19 烘焙 prefab（临时 Subscene 或 spawner），确认：
   - 实体生成数量正确；
   - `GpuEcsAnimatorShaderDataComponent` 的 frameIndex 随时间变化；
   - 角色贴地、朝向正确、相机可见；
   - 截图留档。
   已通过 `LakesideCrowdSetup.ApplyOne` + `LakesideCrowdVerify.Run` 完成，见 §9.6。
4. 再扩到 30+，逐步打开阴影、加入湖岸位置、测真机；扩量前先解决 §9.2 的湖岸陡坎布点。

### 7.3 已实现的自动验证

`Assets/Editor/Crowd/LakesideCrowdVerify.cs`（`LakesideCrowdVerify.Run`）已覆盖 200 人：

- 预加载湖区 9 个 chunk，进入 Play Mode；
- 从 spawner 读取期望人数，断言实体数 = 200（不含 prefab）；
- 断言全部实例半径 150–220m、Y ≥ 水位 +0.05m；当前 200/200 通过；
- 断言首位实体的 `GpuEcsAnimatorShaderDataComponent.shaderData.c2.x`（frameIndex）在 30 帧内变化；
- 离屏 RenderTexture（2560×1440）输出近景、俯瞰、弧段 3 张图；抓图前等 10s，避开地形加载慢的窗口；
- 输出 `Logs/Crowd/lakeside-verify.txt`、`lakeside-verify.png`、`lakeside-30-overview.png`、`lakeside-30-crowd.png`。

待补：真机 draw call / GPU / 显存、阴影开销、200 人同屏 30fps。

## 8. 风险与待验证

| 风险 | 等级 | 说明 / 应对 |
|---|---|---|
| Forward 与 Entities Graphics 兼容性 | 🟢 低 | 200 人已在 Forward 下渲染 PASS；决定不切 Forward+ |
| 切换 Forward+ 的回归范围 | 🟡 中 | 当前不切；除非 Forward 下 200 人异常，否则不动 Crest/Enviro/GTS/草 的回归 |
| 湖岸硬边陡坎 | 🔴 高 | `LakeCarve` 在 r>190 直接恢复原始地形；121/200 个点位高于水位 10m 以上，最南侧约 +37.8m。贴水站立需低岸弧段或改湖盆造岸坡 |
| 样例角色 shader 不是项目 PBR | 🟡 中 | 样例用 `GPUECSAnimator_URP_SimpleShader`；要视觉一致需把 `GpuEcsAnimator` Subgraph 接进项目 PBR ShaderGraph |
| 200 人上限无余量 | 🟡 中 | 当前设计上限 = 200；真机测完再决定关阴影 / 分档 / 降人数 |
| 200 人阴影成本 | 🟡 中 | prefab 默认 cast/receive shadows；200 个 4× 角色进 shadow pass，真机未测 |
| 实体生命周期 | 🟡 中 | 实体不在 SECTR chunk 内；需自研生成/销毁与驻留上限 |
| 动画纹理内存 | 🟡 中 | 4 种角色各一份 VAT 纹理，合计约 7.9 MiB（Ch19 1.64 + Ch14 2.11 + Ch09 1.67 + Ch46 2.50 MiB）；实例数不复制纹理 |
| 版本溯源 | 🟢 低 | 源码无 1.3.6 常量；以 unitypackage 文件名 + Manual 为准 |
| 包已下架 | 🟢 低 | 已 vendor 进仓库，不依赖 Asset Store 再下载 |

## 9. 湖边 30+ 人方案分析（未实施）

目标：**沿中心湖岸边至少 30 个角色，同时播放动作**。按第一性原理拆成 6 个独立问题。

### 9.1 用哪种角色

- 混合 **4 种角色**：Ch19 / Ch14（VAT 57 骨骼）+ Ch09 / Ch46（VAT 65 骨骼）；四者的动画都循环。
- 每个 prefab 使用自己的烘焙动画纹理；同类实例共享一份纹理。跨角色复用需要 **骨骼数 + 骨骼顺序 + 动画帧布局** 都一致；当前四种角色各自烘焙，没有共享动画贴图。
- 200 个实例 × 4 个 prefab；`LakesideCrowdBehaviour.scale = 4` 统一放大 4 倍（原始模型的 4×）。
- 角色 prefab 的原点在脚底，缩放后脚仍贴地；正面朝向由 yaw 控制。

### 9.2 站在哪里

湖参数唯一来源是 `Assets/Editor/Water/LakeCarve.cs`：`Center=(192,96)`、`Radius=190`、`WaterLevel=264`、`Depth=30`、`Blend=90`。

- **实测地形形状（`LakesideShoreProbe`）**：`LakeCarve.TargetHeight` 在 `r > Radius` 直接返回原始地形，没有过渡；
  r=190 处是水位 264m，r=191 处南岸 283.79m、北岸 185–190° 约 267–269m。
  **湖岸多数是 280–310m 的陡坎，不是平缓沙滩。**
- 1 人先放在 angle=190°、r=191m、Y=268.44m 验证；随后扩到 **200 人整圈**（1.8° 间隔，r=191m 处约 6m 一个人）。
- 200 人实测：半径 191–205m，Y 267.06–301.84；**121/200 个点位高于水位 10m 以上**，最南侧约 +37.8m，整圈仍有大量角色站在陡坎上。
- 视觉上若要全部贴水站立：
  - **只布低岸弧段**（约 170–210°）并加密；
  - 或改 `LakeCarve` 增加岸坡（需要重挖湖盆 → 重转受影响 GTS → 重跑 Crest/验证，成本高）。
  当前是 200 人整圈方案：人数、密度与混合角色满足，但陡坎问题未解决。
- 朝向：`LookRotation((192 - x, 0, 96 - z))`，即面向湖心。
- 采样需要加载湖区 chunk（`ChunkLoadScope.LoadRelevant(Area.FromLake())`）；**把结果烘成位置数组**，运行时不再依赖 Terrain 是否加载。
- 当前工具：`LakesideCrowdSetup.ApplyOne`（1 人）、`ApplyThirty`（30 人）、`ApplyCrowd`（200 人，已跑）；都按角度自动找最低可站立点。

### 9.3 怎么生成

- 自研 `LakesideCrowdBehaviour`（`Assets/Scripts/Runtime/Gameplay/`，无 asmdef）放在 authoring scene
  `Assets/Scenes/LakesideCrowd.unity`；`World.unity` 里由 `Lakeside Crowd (SubScene)` 引用它（`AutoLoadScene=1`）。
  - 字段：Ch19 prefab、位置数组、yaw 数组、count、seed；
  - Baker：把 prefab 转成实体 prefab，把位置/yaw 写进 DynamicBuffer；
  - System：首次运行 `EntityManager.Instantiate`，写 `LocalTransform` 与 `GpuEcsAnimatorControlComponent`。
- 编辑器工具 `LakesideCrowdSetup.ApplyOne` 负责采样、创建 authoring scene、写 SubScene；`ApplyThirty` 是 30 人版本，`ApplyCrowd` 是当前 200 人版本。
- 动画 ID 在 prefab 的动画 buffer 长度内随机（Ch19 为 0–4），`startNormalizedTime` 随机 0–1，避免整齐同步。
- 不要在 SECTR chunk 里各放一份：实体会重复。人群是常驻实体，生命周期由这个 manager 统一管理。

### 9.4 为什么不需要每帧采样地形

- 位置在世界生成后是静态的；离线采样一次即可。
- 运行时采样会依赖“湖区 chunk 已加载”这一时序，SECTR 是异步加载，容易在场景启动时拿到 NaN。
- 世界重新生成（`WorldPipeline` / `LakeCarve`）后，位置数组需要重跑编辑器工具更新，和湖的其他手动步骤一样。

### 9.5 性能与可见性

- 200 个混合角色：4 个 prefab/材质组，按 LOD 合批；实测视角 draw command：近景 9、俯瞰 4、弧段 8。
- 人数从 30 扩到 200 后，实体数 ×6.7，但 draw command 仍是每材质/LOD 的小常数；顶点处理量与阴影开销线性增长，真机 200 人 30fps 未验证。
- 远岸角色在 300m 轨道相机下可能被雾/LOD 弱化；200 人整圈 6m 间距，画面已形成连续人群。
- 上限已改为 200；真机测完再决定是否分档或降阴影。

### 9.6 验收状态

**200 人已通过**（`LakesideCrowdVerify.Run`）：

1. 实体数 = 200/200，全部带 `GpuEcsAnimatorShaderDataComponent`；4 种 prefab，4× 缩放。
2. 半径 191.00–205.00m（要求 150–220），Y 267.06–301.84，越界 0/200。
3. 首位实体 30 帧内 frameIndex 161→166，证明在动。
4. 抓图：近景 `lakeside-verify.png`（draw=9/rendered=10）、俯瞰 `lakeside-30-overview.png`（draw=4/rendered=95，完整一圈）、弧段 `lakeside-30-crowd.png`（draw=8/rendered=37，密集人群）；抓图前等 10s 等地形加载。

**视觉状态**：旋转俯瞰能看出完整一圈人；弧段图能看到多种颜色角色密集排列；**121/200 仍站在高于水位 10m 以上的陡坎**，贴水站立需低岸弧段或改湖盆。

**真机待验收**：draw command / GPU / 显存 / 阴影 / 200 人 30fps。

## 10. 相关文档

| 文档 | 何时读 |
|---|---|
| [Assets/GPUECSAnimationBaker/AGENTS.md](../Assets/GPUECSAnimationBaker/AGENTS.md) | 写接入代码前的硬约束 |
| [character_animation](character_animation.md) | Animancer vs GPU Baker 的选型与分工 |
| [rendering_rules](rendering_rules.md) | 新增渲染内容前的管线铁律 |
| [performance_budget](performance_budget.md) | 做性能验收与 ms 预算 |
| [terrain_streaming](terrain_streaming.md) | 湖岸位置、SECTR chunk 与实体生命周期 |
| `GPU ECS Animation Baker Manual.pdf` | 官方 Requirements / Limitations / Bake Services 原文 |
