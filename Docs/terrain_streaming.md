# 地形与流式加载选型分析

> 分析对象：Gaia Pro VS 4.2.6 / GeNa Pro 3.5.14 / SECTR World Streaming
> 目标环境：Unity 2022.3.62f2 + URP 14.0.12，Android 旗舰机，30fps

## 结论速览

| 包 | 是否可用 | 角色 | 关键风险 |
|---|---|---|---|
| Gaia Pro VS | ✅ 可用 | 地形**生成**（编辑器期） | 内容在嵌套包里；植被走 Terrain 原生，移动端不可直接用 |
| GeNa Pro | ✅ 可用 | 地形**细节雕琢**（道路/河流/村落） | 与 Gaia 互补，不替代 |
| SECTR | ✅ 可用 | 运行时**场景流式**加载 | 以 scene 为单位，chunk 粒度受 scene 数量限制 |
| GTS（Gaia 内含） | ✅ 可用 | **Terrain → Mesh** 转换 | 这是"运行时 mesh 分块"的关键工具 |

## 1. 版本兼容性核实

### Gaia Pro VS
外层包只是安装器 + 框架，真正内容在嵌套包。证据：

```
Assets/Procedural Worlds/Packages - Cache/Gaia Pro/Gaia Pro Assets and Biomes.unitypackage (3.7GB)
Assets/Procedural Worlds/Packages - Cache/Gaia Pro/GTS.unitypackage (173MB)
Assets/Procedural Worlds/Packages - Cache/Gaia/Gaia.unitypackage (5.3MB)
```

Unity 版本要求的直接证据（`Assets/Procedural Worlds/Frameworks/Setup1/Scripts/Editor/SetupEditorWindow.cs:279`）：

```csharp
#if !UNITY_2022_3_OR_NEWER
    EditorGUILayout.HelpBox($"Gaia 2023 requires Unity Version 2022.3 or later. ...");
    GUI.enabled = false;
```

**结论：2022.3 是它的最低支持版本，正好匹配当前工程。** 且代码里用 `#if UNITY_6000_6_OR_NEWER` 做前向兼容分支，说明它是"2022.3 为基线 + 兼容 Unity 6"的结构，而不是"只能 Unity 6"。

### SECTR
`SECTR_CharacterMotor.cs:20` 等处使用 `#if UNITY_6000_0_OR_NEWER` 切换 `PhysicsMaterial` 和 `linearVelocity`（Unity 6 才有的 API），说明**主路径是旧版本 API，Unity 6 只是额外分支**。`SECTR_AudioWindow.cs` 用 `#if UNITY_2022_2_OR_NEWER`。

**结论：可用。** 但注意 README 只写到 2019.0.3，代码里还有 `SECTR_Modules.HasPro()` 这种很老的判断，属于"长期未大改但被维护到新版本"的状态。

### GeNa Pro
与 Gaia 同一套 Procedural Worlds 框架（Common7/CleanUp7），版本策略一致。包名带 "for Unity 6" 指的是"支持到 Unity 6"，不是"仅限 Unity 6"。

## 2. Gaia Pro VS 分析

**定位**：地形**生成**工具。用 stamp（高度图印章）驱动 Terrain 生成，配合 biome（生物群系）规则撒植被/石头/建筑。

**关键发现 1：Gaia 本身不做运行时渲染。** 它是编辑器工具，产出标准 Unity Terrain + 标准 Terrain 植被。运行时开销完全取决于 Unity Terrain 自己的渲染路径。

**关键发现 2：Gaia 包里没有 grass/instancing 相关代码**（`find out/gaiavs "Grass|Detail|Instanc"` 返回 0 匹配）。这意味着它依赖 Terrain 的 `drawInstanced` 来渲染草——即 `Terrain.drawInstanced = true`。

**这是移动端的主要隐患**：Unity Terrain 的 detail/grass 渲染即使开 instanced，仍是 CPU 端按 patch 提交，大世界下会成为瓶颈。`GTSTerrain.cs:972` 里能看到它临时开关 `terrain.drawInstanced` 来抓 normalmap，说明这条路径确实被依赖。

## 3. GTS：Terrain → Mesh（关键）

GTS = GeNa Terrain Suite，随 Gaia Pro 一起提供。这是解决"运行时 mesh 分块"需求的**现成工具**。

证据 1 —— mesh 分块能力（`GTSMeshSplitter.cs:74`）：

```csharp
public List<GameObject> SplitAndCreateGameObjects(
    int numberOfXSplits, int numberOfZSplits,
    TranslatePivotType translatePivot, bool addMeshCollider = true)
```

证据 2 —— 多 splat 混合靠全局数组传递（`GTSRuntime.cs`）：

```csharp
public Vector4[] HeightDataArray;
public Vector4[] UVDataArray;
public Vector4[] LayerDataAArray;
public Vector4[] TriPlanarDataArray;
```

这是标准做法：Terrain 转 mesh 后，原本的 splat 混合信息（最多 N 层）通过全局 shader 数组传给材质，顶点/像素着色器重建混合。**代价是每个材质要绑定这些数组，会占用 shader 常量缓冲。**

证据 3 —— URP 支持（`GTSDefaults.cs:38-40`）：

```csharp
[SerializeField] public Material urpMaterial;
[SerializeField] public Object urpPackage;
```

**结论：GTS 可以做 Terrain→Mesh，且支持 URP。** 但要注意：
- 它是**编辑器期转换**，产出的 mesh 需要你自己接流式加载。
- 分块后每块 mesh 的材质如果共享同一个 shader + 相同 keyword，可被 SRP Batcher 合批；如果每块用不同 splat 层数，会破坏合批。
- 碰撞：`addMeshCollider = true` 会给每块加 MeshCollider，大世界要改成"近处 MeshCollider + 远处高度图采样"。

## 4. GeNa Pro 分析

**定位**：场景**细节摆放**工具。道路、河流、村落、桥梁的自动生成与地形适配。

与 Gaia 的关系是**互补**：
- Gaia = 大尺度地形塑形 + 生态撒点
- GeNa = 中尺度人工痕迹（路、河、建筑）的贴合

**对本项目的价值**：钓鱼游戏需要海岸线、栈桥、小屋、路径，GeNa 能省大量手工摆放时间。但它**不解决渲染性能问题**，产出的是普通 GameObject。

**风险**：GeNa 生成物通常是独立 GameObject，数量多时会打散合批。必须配合"生成后合并为静态 mesh"或"转成 GPU instancing"的流程。

## 5. SECTR 流式加载分析

**机制**：Sector（逻辑分区）→ Chunk（可流式单元）→ 导出为**独立 scene 文件**。

证据（`SECTR_Chunk.cs:238`）：

```csharp
asyncLoadOp = SceneManager.LoadSceneAsync(ScenePath, LoadSceneMode.Additive);
activationQueue.AddLast(this);
```

卸载（`SECTR_Chunk.cs:285`）：

```csharp
SceneManager.UnloadSceneAsync(ScenePath);
```

**关键特性 1：异步 + 激活队列节流。** `activationQueue` 保证每帧只激活有限数量的 chunk，避免单帧卡顿。这是它比"自己写 YooAsset 加载"省事的地方。

**关键特性 2：加载器基于体积判定**（`SECTR_RegionLoader.cs:21-22`）：

```csharp
public Vector3 LoadSize = new Vector3(20f, 10f, 20f);
// 卸载距离 = LoadSize 的百分比，用于防抖
```

有"卸载滞后阈值"设计，避免玩家在边界反复横跳导致频繁加载卸载。

**关键限制：粒度受 scene 数量约束。** 每个 chunk 是一个 scene。如果地形切成 10×10=100 块，就是 100 个 scene 文件。这带来两个问题：
1. Scene 加载有固定开销（即使异步），比直接实例化 prefab 重。
2. 每块 scene 里的对象无法跨 scene 合批。

**结论**：SECTR 适合"大区域切换"（如不同海湾），不适合"每 64m 一块"的高密度网格流式。**地形 mesh 分块建议自己做（YooAsset + 对象池），SECTR 用于区域级的内容流式。**

## 6. 与 BRG / SRP Batcher 的配合

| 方案 | 合批影响 |
|---|---|
| GTS 产出的地形 mesh | ✅ 同 shader + 同 keyword 时可 SRP Batcher |
| Gaia 撒的植被（Terrain detail） | ❌ CPU 提交，无法 BRG |
| Gaia 撒的树（Terrain tree） | ⚠️ 可 instanced，但仍走 Terrain 系统 |
| GeNa 生成物 | ⚠️ 独立 GO，需后处理合并 |
| SECTR 加载的对象 | ⚠️ 跨 scene 不合批，同 scene 内可合批 |

**关键结论**：如果追求 BRG，**不能直接用 Gaia/Terrain 的植被系统**，必须把植被位置导出成自己的实例数据，走 GPU instancing / BRG。

## 7. 移动端性能风险清单

1. **Terrain 原生渲染**：`Terrain.drawInstanced` 在移动端仍是 CPU 提交，patch 数量多时 CPU 爆。
2. **GTS 分块后的材质数**：每块独立材质会破坏 SRP Batcher；必须共享材质 + 用全局数组区分。
3. **SECTR 的 scene 切换**：频繁 LoadSceneAsync 有固定开销，且触发 GC。
4. **GeNa 生成物数量**：独立 GO 太多，draw call 爆炸。
5. **MeshCollider**：大世界每块都加，物理开销大。
6. **植被密度**：Gaia 默认密度是按 PC 设计的，移动端要大幅下调。

## 8. 建议技术路线

```
编辑器期：
  Gaia Pro VS  → 生成 Terrain（大尺度地形 + 生态分布）
  GeNa Pro     → 加道路/河流/村落/栈桥（中尺度细节）
  GTS          → Terrain 转 Mesh，按 64~128m 分块，导出 LOD 金字塔

运行时：
  地形 chunk  → 自研 YooAsset 流式 + 对象池 + BRG/SRP Batcher
  区域内容    → SECTR（可选，用于"整个海湾"级别的加载）
  植被        → 从 Gaia 导出位置数据 → 自研 GPU instancing / BRG
  碰撞        → 近处 MeshCollider，远处高度图采样
```

**优先级建议**：
1. 先验证 GTS 能否在 2022.3 + URP 14 正确转换并渲染（这是整条链路的地基）。
2. 再验证分块 mesh 的 SRP Batcher 合批是否生效。
3. SECTR 作为可选项，等确认地形流式方案跑通后再评估是否引入。

## 待验证项（需要导入工程后才能确认）

- GTS 转换后 URP 材质是否正确（splat 层数上限是多少）
- 分块 mesh 的顶点数/块大小，与移动端带宽的平衡
- Gaia 植被导出为实例数据的可行性（是否有现成 API）

## 9. 「多 scene 打断合批」这条说法要拆开看

> 2026-11-09 补：针对"chunk 能不能改 prefab 管理"做的专项核实。

结论：**多 scene 不打断 SRP Batcher，只打断静态合批；而本工程的 chunk 主体是 Terrain，两条都用不上。所以为了合批去做 prefab 化，收益极小。**

按合批机制分别判定：

| 机制 | 是否跨 scene | 依据 | 本工程是否吃到 |
|---|---|---|---|
| SRP Batcher | **是**（按 shader + CBUFFER 布局归并，不看 scene 边界） | `UniversalRP.asset:63` `m_UseSRPBatcher: 1` | 仅 267 个 MeshRenderer 可吃 |
| 静态合批 | **否**（构建期按 scene 合并 VBO） | Unity 构建行为 | 地形不参与，等于用不上 |
| 动态合批 | 否 | `UniversalRP.asset:64` `m_SupportsDynamicBatching: 0`（且规约要求关闭） | 不用 |

**决定性事实**：每个 chunk 的视觉主体是 1 个 `Terrain` 组件（36 个 chunk / 36 个 Terrain）。Terrain 由引擎原生 TerrainRenderer 绘制，**不参与 SRP Batcher，也不参与静态合批**。chunk 内真正的 GameObject 几何总共只有 267 个 `MeshRenderer`（Gaia 撒的树桩/音效区/风暴），常驻 4 块时约 30 个。

所以"chunk 之间能否合批"这个问题，在当前实现里**没有可挽救的对象**——不是 scene 挡住的，是 Terrain 本身不进合批管线。

> **2026-11-09 更新（Mesh 化之后）**：地表已从 Terrain 换成 GTS mesh，
> **现在能吃 SRP Batcher 了**。关键动作是给 GTS shader 补 `UnityPerMaterial` CBUFFER，
> 详见 [gts_srp_batcher](gts_srp_batcher.md)。
>
> 也因此，本节开头"为了合批去做 prefab 化收益极小"的结论要限定范围：
> - **合批不需要 prefab 化**——SRP Batcher 本来就跨 scene 生效，改 shader 即可
> - prefab 化的真正价值是**走 YooAsset bundle**，不是合批
>
> 另一个纠正：静态合批与 SRP Batcher 会互相干扰（静态合批先合并成大 mesh，
> 反而破坏 SRP Batcher 的按材质归并粒度）。subTile 目前标了 `isStatic`，
> 是否移除以 Frame Debugger 实测为准。

## 10. chunk 为什么不能改成 prefab

**SECTR 这一版只支持 scene 加载**（穷举 `SECTR_Chunk.cs`）：

- `_Load()`（`:227-252`）只有 `SceneManager.LoadScene` / `LoadSceneAsync` 两条路径；
- `_DestroyChunk()`（`:282-320`）只有 `UnloadSceneAsync`；
- 全目录 `grep AssetBundle` = 0 命中；
- `ProxyMesh`（`:60`）只是卸载后的占位 proxy，不是 prefab 加载。

**更硬的障碍：Terrain 不能装进 prefab**。全工程 `grep "^--- !u!218 " --include="*.prefab"` = 0 命中，Unity 不支持把 Terrain 组件序列化进 prefab 资产。

因此：

| 诉求 | 可行性 |
|---|---|
| 整个 chunk 改 prefab | ❌ Terrain 装不进 prefab，SECTR 也不支持 |
| chunk 内撒点物用 prefab 实例 | ✅ 现状已如此（267 个 MeshRenderer 都是 prefab 实例） |
| Terrain→Mesh 后的 chunk 用 prefab + YooAsset | ✅ 推荐，但前提是先做 Mesh 化 |

**所以 prefab 化是 Mesh 化的下游动作，不是替代方案。**

> **2026-11-09 更新**：Mesh 化已完成（36 chunk × 4 = 144 个 subTile，地表改由
> `PW/GTS_URP_Compiled` 绘制，Terrain 只留碰撞与植被）。但 chunk **仍是 scene**：
> SECTR 的加载器只认 scene，且 chunk 里还留着 Terrain 组件（碰撞用）。
> 下一步若要 prefab 化，需先解决碰撞载体（TerrainCollider → MeshCollider）与 SECTR 替换。
> 实现细节见 [gts_srp_batcher](gts_srp_batcher.md) 与 [world_implementation](world_implementation.md) §4.1。

## 11. 当前 chunk 的真实性能构成

> **2026-11-09 更新**：下表是 **mesh 化之前**（Terrain 绘制）的数据，保留作对照。
> 现状已变：地表不再走 TerrainRenderer 的 2 pass，改由 1 个 GTS mesh draw
> （8 层 splat 靠 Texture2DArray + `_SplatmapIndex` 在单 pass 内完成）。
> 草/树仍走 Terrain，数值不变。

| 项 | 数值（mesh 化前） | 来源 |
|---|---|---|
| 每块 splat 层数 | 8 | `chunk-render-stats.txt` |
| 每块地形 pass 数 | **2**（4 层/pass，超出走 `TerrainLitAdd`） | `TerrainLitAdd.shader:89` |
| 常驻地形 pass | ≈8 | 常驻 4 块 × 2 pass |
| 每块 detail 实例（全密度） | ≈570 万 | `chunk-render-stats.txt` |
| 36 块 detail 总量 | **2.07 亿** | 同上 |
| 每块树实例 | ≈1.3 万 | 同上 |
| 36 块树总量 | 46.5 万 | 同上 |
| 每块 TerrainData | ≈4.68MB | 文件大小 |

**mesh 化后的地形几何**：

| 项 | 数值 |
|---|---|
| subTile 数 | 144（36 chunk × 4） |
| 每 subTile 顶点 / 三角 | 16641 / 32768 |
| 三角总量 | 4,718,592（原 1887 万的 25%） |
| 常驻 4 块三角 | ≈52.4 万 |
| 地形 draw（每 chunk） | 1 个 GTS mesh（8 层 splat 单 pass） |

→ 瓶颈排序（mesh 化后）：**草 > 树 > 地形 mesh**。草是下一步重点。
