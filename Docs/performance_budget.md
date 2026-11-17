# 性能预算与验证标准

> 目标 iQOO 16 / 30fps（33.3ms）。**当前不分配分项 ms 预算**，原因见 §1。

## 1. 帧时间预算（未分配）

角色、NPC、玩法都还没接入，现在按系统拆 ms 是虚构的数字：把 9ms 给地形、4ms 给角色，
等于在角色不存在时先替它占了预算，后续任何新增都只能"超预算"。

分项预算的前置条件：

1. 角色与 NPC 渲染、动画、玩法逻辑接入；
2. iQOO 16 上跑完整场景，用 Profiler 取 CPU Main / GPU 基线；
3. 把实测基线回写到本文件，再按系统分配余量。

在此之前，渲染改动只登记**可数的结构性开销**（pass / 全屏 RT / blit / draw call），见 §2、§3。

## 2. 管线 pass / 带宽预算

| 项 | 当前值 | 约束 |
|---|---|---|
| CopyColor | 0 | 新增消费者前必须证明必要 |
| CopyDepth | **1（Enviro）** | URP 14 在“透明后采样深度”下不可避免；不要用 ForcePrepass 换取取消（会重绘不透明几何），详见 [rendering_rules](rendering_rules.md) §1.2 |
| 全屏 blit | Enviro 雾 1 copy + 1 合成 | 不允许再叠全屏 ping-pong |
| 水面 | 1 次 DrawMesh | 不开第二个水面 pass |
| 体积雾中间 RT | 1/4 分辨率 | 不改全分辨率 |
| 后处理 | URP 内置 Uber pass 1 次 | ACES/LUT/调色共用，不另起 pass |

**带宽原则**：每个全屏 copy/blit 都是一次 tile load+store。1080p 深度拷贝约 8MB 读 + 8MB 写，30fps ≈ 0.5GB/s。新增 pass 前先算这一笔，并说明能否用 pass 顺序或已有 RT 替代。

## 3. 规模上限

- Draw Call：< 300（SRP Batcher 阶段）/ < 100（BRG 阶段）
- 三角形：< 1.5M / 帧
- 夜晚动态光源：≤ 6（其余靠 emissive + Bloom）
- 同屏角色：≤ 20

## 4. 验证要求

渲染改动必须提供证据之一，否则不算完成：

1. **Frame Debugger**：draw call、合批、CopyColor/CopyDepth 次数、全屏 blit 次数符合预期。
2. **Profiler**：记录 CPU Main / GPU 耗时，作为后续分项预算的基线。
3. **iQOO 16 实机**：30fps 稳定。
4. **降级验证**：最低画质档不崩、无 shader 报错。

禁止“应该没问题”式结论。新增 pass / 全屏 RT / blit 超出 §2 的口径时，必须给出取舍：关掉了什么、哪个画质档关闭、或明确的优化计划。
