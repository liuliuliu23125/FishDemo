# FishDemo 项目规约

Unity 2022.3 + URP 的 **PBR 写实钓鱼游戏 Demo**，视觉对标《对马岛之魂》；大世界分块流式，含水下与昼夜。性能数据为估值。

## 0. 协作红线

1. **只陈述有据可查的事实**：文件路径 + 符号、工具输出、或文档原文。不确定就说"不知道"，禁止补全历史、禁止虚构。
2. **结论分三类标注**：代码事实 / 文档声称（未验证）/ 推断（注明依据），不可混写。
3. **版本兼容必须查证**：看 `package.json` 的 `unity` 字段、asmdef、源码宏、官方文档；包名与宣传语不可作依据。
4. **自研代码的引用不写行号**：用「文件 + 符号 + grep」；第三方源码引用保留行号，升级包后必须复核。

## 1. 全局硬约束

| 项 | 值 |
|---|---|
| Unity / URP | 2022.3.62f2 / 14.0.12 |
| 色彩空间 / 后端 / 压缩 | Linear / IL2CPP+ARM64 / ASTC 8x8 |
| 帧率 / 机型 | 30fps / iQOO 16（未真机验证） |
| 同屏角色上限 | 200 |
| 联网 | 将来需要，当前不实现；客户端表现需可承载多人 |
| 合批 | **SRP Batcher 唯一路径；静态与动态合批均关闭** |
| Android 图形 API | **仅 Vulkan，排除 GLES**（`m_APIs=15000000`、`m_Automatic=0`） |

- Android 仅 Vulkan 的 shader 约束见 [Assets/Grasses/AGENTS.md](Assets/Grasses/AGENTS.md) §2 与 [grass_brg_plan](Docs/grass_brg_plan.md) §1.1；切换工具见 [Assets/Editor/World/AGENTS.md](Assets/Editor/World/AGENTS.md) §1。
- 工程保持 **Forward**，不切 Forward+；理由与验收见 [Assets/Settings/AGENTS.md](Assets/Settings/AGENTS.md)。
- 地形纹理数组必须 `ASTC_8x8`；原因见 [Assets/Editor/Gts/AGENTS.md](Assets/Editor/Gts/AGENTS.md) §2.3。
- 写渲染代码前先读 [rendering_rules](Docs/rendering_rules.md) §1；性能账规则见 §1.3。

## 2. 目录入口

> 进入目录写代码前读该目录的 `AGENTS.md`；模块现状见 [Docs/AGENTS.md](Docs/AGENTS.md) §1。

| 目录 | 何时读 |
|---|---|
| [Assets/Scripts](Assets/Scripts/AGENTS.md) | 写任何自研代码前（程序集边界、命名空间） |
| [Assets/Scripts/Editor](Assets/Scripts/Editor/AGENTS.md) | 改 Gaia 工具链、草烘焙与 BRG 验证 |
| [Assets/Scripts/Runtime](Assets/Scripts/Runtime/AGENTS.md) | 改资源层、BRG 草运行时、相机 rig |
| [Assets/Editor](Assets/Editor/AGENTS.md) | 改 SECTR/GTS/水/大气/火把后处理/验证脚本；已按模块拆子目录 |
| [Assets/Procedural Worlds](Assets/Procedural%20Worlds/AGENTS.md) | 用第三方插件、排查 Gaia/SECTR/GTS/Pegasus |
| [Assets/AmplifyImpostors](Assets/AmplifyImpostors/AGENTS.md) | 改 impostor 烘焙/LOD 前 |
| [Assets/Enviro 3 - Sky and Weather](Assets/Enviro%203%20-%20Sky%20and%20Weather/AGENTS.md) | 改大气、升级 Enviro、重打雾 pass 补丁前 |
| [Assets/Grasses](Assets/Grasses/AGENTS.md) | 改草 shader、材质 keyword、Vulkan 约束前 |
| [Assets/GTSRuntime](Assets/GTSRuntime/AGENTS.md) | 改 chunk 的 `GTSMesh` 运行时初始化 |
| [Assets/Settings](Assets/Settings/AGENTS.md) | 改 URP Asset / Renderer / Volume Profile / Enviro 质量档前 |
| [Assets/Gaia User Data](Assets/Gaia%20User%20Data/AGENTS.md) | 查/改 GTS Profile、纹理数组、Gaia session |
| [Assets/GTS User Data](Assets/GTS%20User%20Data/AGENTS.md) | 查 GTS 转换中间产物、重跑转换 |
| [Assets/Scenes](Assets/Scenes/AGENTS.md) | 改场景、重跑世界生成、重接草/Pegasus |
| [Assets/GPUECSAnimationBaker](Assets/GPUECSAnimationBaker/AGENTS.md) | 接 NPC/人群、改 VAT 材质或 shader |
| [Packages/com.tuyoogame.yooasset](Packages/com.tuyoogame.yooasset/AGENTS.md) | 改资源层代码前 |
| [Packages/com.waveharmonic.crest](Packages/com.waveharmonic.crest/AGENTS.md) | 改 Crest 运行时/材质/水下；升级前必读补丁与生成链 |
| [Packages/com.unity.render-pipelines.universal](Packages/com.unity.render-pipelines.universal/AGENTS.md) | 改 URP tonemap / 后处理 / shader 前（embedded 14.0.12） |
| [Packages/com.unity.render-pipelines.core](Packages/com.unity.render-pipelines.core/AGENTS.md) | 改 ACES / 核心 shader 前（embedded 14.0.12） |

## 3. 事实源与约定

- **模块现状、文档索引、工程约定**（LFS、程序集目录、注释规则、全局配置镜像）全部在 [Docs/AGENTS.md](Docs/AGENTS.md)。
- **常读**：[rendering_rules](Docs/rendering_rules.md)（渲染/批处理）、[performance_budget](Docs/performance_budget.md)（性能验收）。
