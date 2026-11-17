# Assets/Editor/Common 规约

> 默认程序集 `Assembly-CSharp-Editor`，无 asmdef。跨模块复用的验证工具。

## 1. `ChunkLoadScope`

验证脚本共用的 chunk 区域筛选。`LoadRelevant(Area)` 只 `Additive` 打开与区域相交的 chunk 场景，避免每次加载全部 36 块。

- `Area.FromLake(margin = 150)`：以 `LakeCarve.Center` / `Radius` / `Blend` 构造湖 AABB；湖区通常筛出 **8~9 个** chunk。
- chunk 世界原点：读场景 YAML 里**所有非零 `m_LocalPosition` 取 x/z 最小值**。每个 chunk 场景里有正负对称两组位置，最小值恒为地形。
- **不要用文件名网格坐标**：`Terrain_X_Y` 是 1024m 大区块、`a-b-0` 是 512m 子块，直接取末尾数字只有 4 种重复值。
- chunk 尺寸从场景依赖的 `TerrainData.size.x` 读取；读不到时按相交处理（宁可多加载）。

## 2. 使用方式

调用 `ChunkLoadScope.LoadRelevant(ChunkLoadScope.Area.FromLake())`。

调用方：`Water/LakeRenderVerify`、`LakeWaterVerify`、`LakeOrbitVerify`、`CrestRenderProbe`、`Atmosphere/AtmosphereVerify`、`Crowd/LakesideCrowdVerify`。

## 3. 注意

`Area.FromLake` 依赖 `Water/LakeCarve` 的常量。改湖址/半径后无需改这里，但引用方筛出的 chunk 集合会随之变化。
