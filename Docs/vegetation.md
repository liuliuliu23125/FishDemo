# 植被与 LOD 优化选型分析

> 分析对象：Amplify Impostors v1.1.0 / Shader Control v8.2.3
> 目标环境：Unity 2022.3.62f2 + URP 14.0.12，Android 旗舰机，30fps

## 结论速览

| 包 | 是否可用 | 定位 | 结论 |
|---|---|---|---|
| Amplify Impostors | ✅ 可用 | 中远景 LOD | **采用**，必须用自带 shader |
| Shader Control | ✅ 可用 | Shader 变体裁剪 | 采用 |
| ~~Vibrant Grass Shader~~ | ❌ **移除** | 近景草 | 与流式加载架构冲突，见 §1 |
| ~~TVE Amplify Impostors Module~~ | ❌ **移除** | TVE 集成 | 只有 Unity 6 的 URP 包 |

## 1. 为什么移除 Vibrant Grass Shader

### 技术方案回顾

它是"编辑器期把所有草烘焙进一个 mesh，运行时单个 MeshRenderer"（`GrassMeshMaster.cs:20`）。理论上 draw call 极低。

### 两个致命的架构冲突

**冲突 1：每个草场实例创建一个独立材质**

`GrassMeshMaster.cs:134`：

```csharp
Material MainMatToAdd = new Material(MatApplied);
MainMatInstanced = MainMatToAdd;
MainMatInstanced.name = MainMatToUse.name + "_Instanced";
```

**每块草场 `new Material(...)`** → 每块一个材质实例 → **破坏 SRP Batcher**。

对分块流式加载是灾难：每个 chunk 加载都新增一个材质实例，chunk 卸载时若没正确销毁（它要求手动调 `DestroyInstancedMaterials_Event`），材质会持续累积。

**冲突 2：设计单位是 300m 见方的"草场"，与 chunk 粒度不匹配**

`_Setup.pdf` 原文：

```
-Keep each VibrantGrassShader instance under 400 grass fields (300x300meters),
but you can stack them next to each other.
```

它的设计单位是**一片 300×300 米**的区域，且明确建议"实例数控制在 400 个草场以内"。而本项目的地形 chunk 是 64~128 米。两者粒度差 2~5 倍，且它的 mesh 无法按 chunk 局部剔除。

### 结论

**移除。** 不是它不好，而是它的设计前提（有限区域、单 mesh、每实例独立材质）与本项目的大世界分块流式 + SRP Batcher 要求根本冲突。

**替代方案**：草走 GPU instancing / BRG，实例位置来自 chunk 的 `ComputeBuffer`。这本来就在 AGENTS.md 的批处理总则里。

## 2. 植被渲染链路（移除 Vibrant Grass 后）

```
近景（0-30m）
  └─ 草：GPU instancing / BRG（位置来自 chunk ComputeBuffer）
  └─ 树：LOD0 mesh

中景（30-80m）
  └─ LOD1 简化 mesh

远景（80-200m）
  └─ Amplify Impostor（Octahedron）

极远（200m+）
  └─ GTS 地形 splat 贴图
```

## 3. Amplify Impostors

### 版本兼容

`Readme.txt` 原文：

```
Requirements
  * Unity 2022.3 or newer
  * URP or HDRP 14 or newer, and Shader Graph
```

**完美匹配** Unity 2022.3 + URP 14。

### 核心能力

把 3D 模型烘焙成 **Octahedron（八面体）** 或 **Spherical（球面）** impostor —— 用贴图替代整个 mesh，保留视角变化。

### ⚠️ 关键性能警告

`Readme.txt` 原文：

```
The Shader Graph versions write per-pixel depth without conservative depth, which disables
early depth testing (early-z) and makes heavy overdraw noticeably more expensive on the GPU.
The included Amplify Impostors shaders and the Amplify Shader Editor shaders use conservative
depth and keep early-z.
```

**必须用自带的 Amplify Impostors shader，不要用 ShaderGraph 版。**

对植被这种大量重叠、overdraw 严重的场景，early-z 的有无是数量级的差别。

### 烘焙注意

- 编辑器期烘焙，需纳入资产管线。
- Octahedron 通常比 Spherical 更省。
- 移动端贴图建议 512~1024。

## 4. Shader Control

### 功能

扫描工程里所有 shader，找出 `shader_feature` / `multi_compile` 产生的变体，标记未被任何材质使用的 keyword，生成裁剪配置。

### 为什么本项目需要

引入的第三方包（Enviro、Crest、Amplify Impostors、Volumetric 系列、GTS）**每个都带来大量 shader 变体**：

1. **变体爆炸** → 构建时间极长
2. **包体膨胀**
3. **运行时内存**

### 使用建议

- 在**所有包导入完成、材质配置定型后**跑一次。
- 重点裁剪：Enviro 的天气类型变体、Crest 的 LOD 模块变体、Volumetric 的质量档变体、GTS 的 splat 层数变体。
- 配合 `GraphicsSettings.asset` 的 `m_InstancingStripping`、`m_BrgStripping`。

### 风险

- 修改 shader 源码的 keyword 声明，**升级包后需重跑**。
- 裁剪过度会导致 keyword 缺失 → 材质变粉红。**必须真机验证**。

## 待验证项

- Amplify Impostors 在移动端的 overdraw 表现
- Octahedron vs Spherical 的选择
- Shader Control 对 ShaderGraph 的裁剪效果
- 自研草 GPU instancing 的实例数量上限
