# 资源加载、缓存与对象池策略

> 目标环境：Unity 2022.3.62f2 + URP 14.0.12，Android 旗舰机，30fps

## 结论速览

| 层 | 方案 | 说明 |
|---|---|---|
| Asset 层（chunk 资源） | **Addressables + 延迟卸载 + LRU** | 核心策略 |
| GPU 资源层（Mesh / Buffer） | **对象池复用，不 Destroy** | 比 GameObject 池更关键 |
| GameObject 层（小物件） | `UnityEngine.Pool`（暂不引入 PoolManager） | 按需 |
| 植被 | GraphicsBuffer 池，无 GameObject | BRG/instancing |

**核心原则：反复加载用「缓存」解决，不用「池」解决。**

## 1. 为什么区分"频繁创建销毁"与"反复加载"

这两件事成本来源完全不同，用错方案会白费力气。

| 场景 | 成本来源 | 正确方案 |
|---|---|---|
| **频繁创建销毁**（鱼、水花、浮标、特效） | `Instantiate` CPU 开销 + `Destroy` 的 GC 压力 | **对象池** |
| **反复加载**（玩家在 chunk 边界来回走） | Bundle IO + 反序列化 + 显存分配 | **缓存 + 延迟卸载** |

对象池解决不了反复加载，因为 `LoadAssetAsync` 的开销在 IO 和解序列化，无法预先"泡"在池里。

## 2. 三层资源模型

"反复加载"在 Unity 里实际分三层，每层解法不同：

| 层 | 反复加载的真实成本 | 解法 |
|---|---|---|
| **Asset 层**（Addressables） | Bundle IO + 反序列化 | 引用计数 + LRU + 延迟卸载 |
| **GPU 资源层**（Mesh / GraphicsBuffer） | 显存分配 + 顶点数据上传 | 池化复用 buffer，只改数据 |
| **GameObject 层** | Instantiate / Destroy | GameObject 池 |

## 3. Asset 层：延迟卸载 + LRU（核心）

### 3.1 延迟卸载

玩家离开 chunk 后**不立即释放**，等 N 秒或走出更大范围才释放。

SECTR 的 `RegionLoader` 就是这个设计（`SECTR_RegionLoader.cs:21-22`）：

```csharp
public Vector3 LoadSize = new Vector3(20f, 10f, 20f);
// 卸载距离 = LoadSize 的百分比，用于防抖
```

有独立的卸载滞后阈值，避免玩家在边界反复横跳导致频繁加载卸载。

**本项目参数建议**（初始，需实测校准）：

| 参数 | 建议值 | 说明 |
|---|---|---|
| 加载范围 | 相机前方 + 两侧，半径 2.5 个 chunk | |
| 卸载滞后 | 超出加载范围 1.5 个 chunk 才卸载 | 防边界抖动 |
| 延迟时间 | 离开后 2~3 秒 | 应对短暂回头 |

### 3.2 LRU 缓存

保留最近 K 个 chunk 的资源引用，命中就不重新加载。

```
缓存容量 K = max(当前可见 chunk 数 × 1.5, 最小值)
超容量时淘汰最久未使用的
```

**注意**：LRU 淘汰的是 **Asset 引用**（`Addressables.Release`），不是 GameObject。两者生命周期分开管理。

### 3.3 预加载

按移动方向预加载前方 chunk，消除"走到才加载"的卡顿。

```
if (velocity.magnitude > threshold)
    preload(neighborChunksInDirection(velocity));
```

### 3.4 分帧与预算

- 加载必须在预算内（≤ 3ms/帧，30fps 下可放宽到 5ms）。
- 使用分帧 + 异步上传。
- 每帧最多激活 N 个 chunk（参考 SECTR 的 `activationQueue` 节流思路）。

## 4. GPU 资源层：Mesh / Buffer 池

**这一层最容易被忽略，但往往比 GameObject 池更贵。**

### 4.1 Mesh

`new Mesh()` + `SetVertices` 会触发显存分配和上传，**比 Instantiate 贵得多**。

**规则**：chunk 卸载时**不要 Destroy Mesh**，应池化复用：

```csharp
// 卸载
meshPool.Return(chunk.mesh);   // 不是 Destroy(chunk.mesh)

// 加载
var mesh = meshPool.Rent(vertexCount);
mesh.SetVertices(...);          // 只更新数据，不重新分配
```

**要求**：池按顶点数分桶（如 1k / 4k / 16k），减少重新分配。

### 4.2 GraphicsBuffer（植被 BRG）

- 按**最大容量**预分配，chunk 切换时只改内容和 count。
- 不要为每个 chunk 新建 buffer。

## 5. GameObject 层

### 5.1 地形 chunk root

chunk 的 GameObject 可以池化复用（结构一致，只换 mesh 和碰撞体）。

### 5.2 小物件：暂不引入 PoolManager

**结论：暂缓引入 PoolManager。**

理由：
1. Unity 2021.2+ 已内置 `UnityEngine.Pool.ObjectPool<T>`，够用。
2. PoolManager 的 `Dictionary<string, PrefabPool>` 按字符串索引，有额外开销。
3. 目前没有明确的"高频 Spawn/Destroy"场景清单。

**何时引入**：当确认鱼群、水花、浮标等场景的内置池不够用（如需要 Inspector 配置、prewarm、自动 culling）时。

**明确不适用**：
- ❌ 地形 chunk（需要 Addressables 管 Asset 生命周期，池只管 GameObject）
- ❌ 植被（走 GPU instancing/BRG，没有 GameObject）

## 6. 本项目的完整分工

```
鱼群 / 水花 / 浮标 / 钓具      → UnityEngine.Pool（GameObject 池）
地形 chunk 的 Mesh            → 自建 Mesh 池（复用，不 Destroy）
地形 chunk 的 GameObject      → 对象池（chunk root 复用）
Chunk 的 Asset 加载           → Addressables + LRU + 延迟卸载
植被（BRG/instancing）        → GraphicsBuffer 池，无 GameObject
```

## 7. 与 SECTR 的关系

SECTR 已经实现了「区域判定 + 异步加载 + 激活队列节流 + 卸载滞后」。

**建议**：
- 如果地形 chunk 用 SECTR 管理，直接复用它的 `RegionLoader` 滞后逻辑。
- 但 SECTR 是 **scene 粒度**，且跨 scene 不合批。**地形 mesh 分块建议自己做 Addressables**，SECTR 只用于区域级内容切换。

## 待验证项

- chunk 加载的实际耗时分布（IO vs 反序列化 vs 显存上传）
- Mesh 池分桶的粒度
- LRU 容量 K 的最优值（内存 vs 命中率）
- 延迟卸载时长对内存峰值的影响
