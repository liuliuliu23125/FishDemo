# 性能预算与验证标准

> 目标 iQOO 16 / 30fps（33.3ms）。下表是**初始预算**，需实机 Profile 后回写。

## 1. GPU 预算

| 项目 | 目标 |
|---|---|
| 不透明几何（地形+植被+建筑） | ≤ 9ms |
| 角色（含 20 个同屏） | ≤ 4ms |
| 水面（含反射） | ≤ 5ms |
| 阴影 | ≤ 3ms |
| 体积雾 + 体积光 | ≤ 3ms |
| 天空/大气 | ≤ 1.5ms |
| 后处理 | ≤ 2.5ms |
| 水下后处理 | ≤ 1.5ms |
| UI | ≤ 2ms |
| 余量 | ≤ 1.8ms |

各项合计 31.5ms + 余量 1.8ms = 33.3ms。当前：水面 SimpleTransparency + Quad（平面反射关闭，1 次 DrawMesh）；Enviro 体积雾 Low（1/4 res、steps 32）、体积云关。均为估值，待实机校准。

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
2. **Profiler**：单项 GPU 耗时在预算内。
3. **iQOO 16 实机**：30fps 稳定。
4. **降级验证**：最低画质档不崩、无 shader 报错。

禁止“应该没问题”式结论。单项超预算 10% 必须给出取舍：关掉了什么、哪个画质档关闭、或明确的优化计划。
