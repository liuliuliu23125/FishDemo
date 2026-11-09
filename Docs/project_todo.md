# 工程待办与待决策

> 记录当前工程的实际差距与未决事项。完成后从本文件移除。

## 1. 工程配置差距

| # | 问题 | 出处 | 处理 |
|---|---|---|---|
| 1 | 只有 2D Renderer，无 3D Renderer | `UniversalRP.asset: m_RendererDataList` → `Renderer2D.asset` | 新建 `UniversalRenderer` |
| 2 | 深度图未开 | `m_RequireDepthTexture: 0` | 改 1（Crest/雾/焦散依赖） |
| 3 | 无 Git LFS | 无 `.gitattributes` | 添加并迁移大资源 |
| 4 | 无 Entities | `manifest.json` | 装 Entities + Entities Graphics（GPU Baker 需要） |
| 5 | 缺 Newtonsoft.Json | GPU Baker asmdef | 用 `com.unity.nuget.newtonsoft-json`（DOTween 已提供） |
| 6 | Android 最低 SDK 22 | `AndroidMinSdkVersion: 22` | 建议 26+ |
| 7 | Graphics API 为 Automatic | `m_BuildTargetGraphicsAPIs` | 显式配 **Vulkan** |
| 8 | 增量 GC 关闭 | `gcIncremental: 0` | 开启，降低卡顿 |
| 9 | Android 默认画质 Medium | `m_PerPlatformDefaultQuality.Android: 2` | 改 High/Ultra |
| 10 | 2D feature 包冗余 | `com.unity.feature.2d` | 转 3D 后移除 |
| 11 | 无 Input System / Cinemachine | `manifest.json` | 钓鱼玩法需要 |
| 12 | GPU Baker 未 vendor | 包已下架 | 立即 vendor 进仓库 |

**已完成**：YooAsset 3.0.7 嵌入式接入（编译通过，`"source": "embedded"`）。

## 2. 功能缺口（代码层面的待办）

| 项 | 说明 |
|---|---|
| Crest 反射可见性跳过 | Crest 只有基于相机高度的 `SkipAbove`/`SkipBelow`（`WaterReflections.cs:240`），**没有视锥判断**。需自研补 `GeometryUtility.TestPlanesAABB` |
| YooAsset 启动入口 | `YooAssetInitializer.InitializeAsync()` 目前**无任何调用方**，资源层尚未接线 |
| 模拟构建产物 | 菜单 `YooAsset/FishDemo/执行模拟构建并注入路径` 未实际执行过 |

## 3. 待决策

### 3.1 是否长期锁定 Unity 2022.3

| 选择 | 收益 | 代价 |
|---|---|---|
| 锁定 2022.3 | 现有第三方包全部验证过 | 需自研 BRG、体积雾、水体 |
| 升级 Unity 6 | 白拿 Forward+ / RenderGraph / GPUResidentDrawer / 内置水体 | 全部第三方包需重新验证，含已下架的 GPU Baker |

**当前建议**：先按 2022.3 推进，渲染抽象层留迁移口。demo 阶段换引擎收益低于把内容做出来。

### 3.2 `InstallCleanupMode`

默认 `None`，覆盖安装后旧缓存残留。可选 `ClearAllCacheFiles` / `ClearAllBundleFiles` / `ClearAllManifestFiles`。需要权衡启动时间与磁盘占用。

### 3.3 iQOO 16 实机数据

性能预算表目前全是估值，需实机 Profile 后校准。
