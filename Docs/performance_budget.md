# 性能预算与验证标准

> 目标 iQOO 16 / 30fps（33.3ms）。**当前不分配分项 ms 预算**，原因见 §1。

## 1. 帧时间预算（未分配）

玩家角色、玩法逻辑还没接入；NPC 已接 **200 人整圈**（4 种角色 × 4× 大小 / Forward / Entities Graphics，Editor 验证）。现在按系统拆 ms 是虚构的数字：把 9ms 给地形、4ms 给角色，
等于在角色不存在时先替它占了预算，后续任何新增都只能"超预算"。

分项预算的前置条件：

1. 玩家角色、玩法逻辑接入（NPC 200 人已接入）；
2. iQOO 16 上跑完整场景，用 Profiler 取 CPU Main / GPU 基线；
3. 把实测基线回写到本文件，再按系统分配余量。

在此之前，渲染改动只登记**可数的结构性开销**（pass / 全屏 RT / blit / draw call），见 §2、§3。

## 2. 管线 pass / 带宽预算

| 项 | 当前值 | 约束 |
|---|---|---|
| CopyColor | 0 | 新增消费者前必须证明必要 |
| CopyDepth | **1（Enviro）** | URP 14 在“透明后采样深度”下不可避免；不要用 ForcePrepass 换取取消（会重绘不透明几何），详见 [rendering_rules](rendering_rules.md) §1.2 |
| 平面反射 | 1 次 512×256 相机渲染，无 intermediate/final blit、无 CopyDepth | Above only；Default 层；草（layer 6）/水面（4）/UI 不反射；相机看不到湖时 0 |
| 反射管线 | `ReflectionRenderer.asset`（renderer index 1，无 Feature，IntermediateTextureMode=Auto） | 反射直接写目标 RT；主相机仍用 `UniversalRenderer.asset`（index 0） |
| 反射贴图 | 512×256×2 slice，约 1–2 MiB | 只在反射相机启用时分配；格式由 `SystemInfo.GetGraphicsFormat(HDR)` 决定 |
| 全屏 blit | Enviro 雾 1 copy + 1 合成 | 不允许再叠全屏 ping-pong |
| 水面 | 1 次 DrawMesh | 不开第二个水面 pass |
| 体积雾中间 RT | 1/4 分辨率 | 不改全分辨率 |
| 后处理 Uber | 1 次全屏 pass | ACES / HDR LUT / 调色 / 曲线 / SMH / Vignette / Grain 共用，不另起 pass |
| Bloom Half/6（默认） | 1 预滤波 + 2×5 降采样 + 5 升采样 = 16 blit | half-res 金字塔 6 级，12 张 half-res RT，无 full-res RT；H 模糊 5 tap |
| Bloom Half/3（`B`） | 1 预滤波 + 2×2 降采样 + 2 升采样 = 7 blit | half-res 金字塔 3 级，6 张 half-res RT；H 模糊 5 tap |
| Bloom Quarter/3（`B`） | 1 预滤波 + 2×2 降采样 + 2 升采样 = 7 blit | quarter-res 金字塔 3 级，6 张 RT；H 模糊 5 tap |
| 火把点光 | ≤ 6 盏，无附加光阴影 | per-object 4；`range=48m`，范围外物体不付光照循环 |

**带宽原则**：每个全屏 copy/blit 都是一次 tile load+store。1080p 深度拷贝约 8MB 读 + 8MB 写，30fps ≈ 0.5GB/s；512×256 反射相机只有颜色写回约 0.5MB/帧量级，无中间 RT、无 final blit、无深度拷贝。新增 pass 前先算这一笔，并说明能否用 pass 顺序或已有 RT 替代。

**后处理降级顺序**：当前无 DOF；低画质档先 `B` 循环到 Bloom Half/3 或 Quarter/3（3 级/7 blit），再关 Bloom。不要为省成本把 HDR grading 改回 LDR（ACES 下午夜会被压黑）。夜景曝光过渡期间 LUT 会重建约 2.5s，除过渡外稳定。

## 3. 规模上限

- Draw Call：< 300（SRP Batcher 阶段）/ < 100（BRG 阶段）
- 三角形：< 1.5M / 帧
- 动态光源：12 支火把，同时点亮 ≤ 6 盏点光（无阴影、per-object 4）；默认 1 盏，`L` 循环 0/1/2/4/6。不按昼夜门控
- 平面反射：1 台反射相机、512×256、Above only；独立 ReflectionRenderer（无 Feature/Auto）直接写 RT；草 BRG 不参与反射（layer 6 + 反射相机期间跳过剔除）
- 火把可见物：24 个不透明小网格（杆+碗）常驻；≤6 个发光核心 + ≤12 个半透明粒子系统随点亮数量增加
- 同屏角色：≤ 200（200 人 VAT 已验，Editor/Forward；真机未测）
- 水下生态（中心湖礁区）：84 底栖实例（LOD0 合计 ≈983k，Ocean prefab 带 LODGroup，水上且距礁 >120m 时整组关闭）；海床 1 个 Mesh 48896 tris；鱼群 260（VAT 220 条、2 个学校共享材质开 instancing；30 条 skinned；海龟/魟鱼/水母 10）；GodRay 默认关闭。生态近景 LOD0 合计约 1.27M，**未计世界**，真机未测；降级看 [underwater_ecology](underwater_ecology.md) §6/§8
- 水面浮萍（中心湖岸线）：193 株睡莲，4 个共享网格 + 1 共享材质，材质开 instancing（实际批处理路径以 Frame Debugger 为准）；合计 ≈372k tris，逐实例剔除；固定水位，无 LOD/阴影。真机未测；低画质直接移除或减半，见 [duckweed](duckweed.md) §4/§6

## 4. 验证要求

渲染改动必须提供证据之一，否则不算完成：

1. **Frame Debugger**：draw call、合批、CopyColor/CopyDepth 次数、全屏 blit 次数符合预期。
2. **Profiler**：记录 CPU Main / GPU 耗时，作为后续分项预算的基线。
3. **iQOO 16 实机**：30fps 稳定。
4. **降级验证**：最低画质档不崩、无 shader 报错。

禁止“应该没问题”式结论。新增 pass / 全屏 RT / blit 超出 §2 的口径时，必须给出取舍：关掉了什么、哪个画质档关闭、或明确的优化计划。
