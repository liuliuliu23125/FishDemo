using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using WaveHarmonic.Crest;

/// <summary>
/// 把 Crest 水体接进 World.unity，替换掉原来的 Gaia Water。
///
/// ── 第一性原理：Crest 怎么"只在凹陷里出水" ──
/// Crest 默认生成**无限大的水面**（SeaLevel 处一张平面），不是"往坑里灌水"。
/// 要让它只出现在湖盆内，靠的是两个东西配合（代码事实，见 SurfaceRenderer.cs:774-915）：
///
///   1. `WaterRenderer.DefaultExcludes = Surface` → 全局默认无水；
///   2. `WaterBody` 组件 + `Exclusion = Include` → 只在这个 AABB 内加水。
///      ApplyWaterBodyCulling 对每个水面 chunk 与 WaterBody AABB 求交，
///      与水体矩形不相交的 chunk 直接 `forceRenderingOff = true`。
///      这是**逐 chunk 的渲染剔除**，不是遮罩，所以湖外一点成本都不付。
///
///   ⚠ 第一版实测踩坑：用了 `Exclusion = Exclude`（语义是"这块不是水"，用来挖洞），
///   效果恰好相反——湖里没水、湖外全是海。Include/Exclude 语义不要凭直觉猜。
///
///   3. `WaterRenderer` 的 `LodLevels` 与 `ScaleRange` 压小
///      → 水面网格只铺到湖盆附近，不做无限远。湖很小（半径 190m），
///        默认 9 级 LOD 是给大海准备的，必须压到 4 级。
///
/// ── 为什么还要 WaterBody，而不是直接缩小水面 ──
/// 因为水面是以**视点为中心**的 clipmap（`SurfaceRenderer` 的 chunk 网格跟着相机走）。
/// 只缩 LOD 只能减小"覆盖半径"，水面依然是个以相机为中心的圆盘，
/// 站在湖边会看到水面铺到湖外的地形上。WaterBody 的 AABB 剔除才是把水面**裁成矩形湖面**的那一步。
///
/// ── 性能取舍（对应 AGENTS.md 的 30fps / iQOO 16 目标）──
/// 关掉对本 demo 无收益的模块：
///   - DynamicWaves：船/鱼交互波，每帧多一次仿真 pass。钓鱼玩法将来要，先关。
///   - Flow：大范围流向，湖里不需要。
///   - Shadow：水面接收阴影，成本高，且 demo 主光只有一个方向光。
///   - Clip：不用 clip 贴图（我们用 WaterBody AABB 剔除）。
/// 保留 AnimatedWaves（波浪）+ Foam（岸边泡沫）+ Depth/Absorption（水下颜色）+ Level（水位）。
/// 反射默认关闭（`Reflections.Enabled = false`）——这是最贵的一项，
/// 且 Crest 没有"水体不在视锥内就跳过反射"的判断（见 Docs/water.md §3），
/// demo 阶段先不开，等有需要再按需补。
///
/// ── 与旧 Gaia Water 的关系 ──
/// 旧 `Gaia Water` 预制体实例会被**禁用并移出主场景**（不是删除），
/// 其 `PWS_WaterSystem` 与 Gaia session 的海平面耦合（见 world_implementation §6.1），
/// 留着会干扰。禁用后仍可从场景历史找回。
///
/// 用法：
///   -executeMethod LakeCrestSetup.Apply
///   -executeMethod LakeCrestSetup.Remove
///   -executeMethod LakeCrestSetup.Report
/// 输出 Logs/Lake/lake-crest.txt
/// </summary>
public static class LakeCrestSetup
{
    private const string Tag = "[LakeCrest]";
    private const string WorldScenePath = "Assets/Scenes/World.unity";
    private const string OutDir = "Logs/Lake";
    private const string ResultPath = OutDir + "/lake-crest.txt";

    private const string WaterMatPath = "Packages/com.waveharmonic.crest/Runtime/Materials/Water.mat";
    private const string VolumeMatPath = "Packages/com.waveharmonic.crest/Runtime/Materials/Water Volume.mat";

    /// <summary>水面节点名。重复执行时按这个名字找已有对象，保证幂等。</summary>
    private const string NodeName = "Crest Lake";

    public static void Apply() { Run("apply"); }
    public static void Remove() { Run("remove"); }
    public static void Report() { Run("report"); }

    /// <summary>
    /// 只重写水面材质的光学参数，不动场景节点。调水色时用它，比 Apply 快得多
    /// （Apply 会销毁重建 Crest 节点，还会重跑 URP 前置检查与全量校验）。
    /// </summary>
    public static void Retune()
    {
        var sb = new StringBuilder();
        void W(string s) { Debug.Log($"{Tag} {s}"); sb.AppendLine(s); }
        try
        {
            var waterMat = AssetDatabase.LoadAssetAtPath<Material>(WaterMatPath);
            var volumeMat = AssetDatabase.LoadAssetAtPath<Material>(VolumeMatPath);
            if (waterMat == null || volumeMat == null)
            {
                W($"FAIL：找不到材质（water={waterMat}, volume={volumeMat}）");
                Flush(sb, 2);
                return;
            }

            ApplyWaterColor(waterMat, volumeMat, W);
            W(DescribeOptics(waterMat));
            W("==== 结论：PASS ====");
            Flush(sb, 0);
        }
        catch (Exception ex)
        {
            W($"异常：{ex}");
            W("==== 结论：FAIL ====");
            Flush(sb, 1);
        }
    }

    private static void Run(string mode)
    {
        var sb = new StringBuilder();
        void W(string s) { Debug.Log($"{Tag} {s}"); sb.AppendLine(s); }

        try
        {
            Directory.CreateDirectory(OutDir);
            var scene = EditorSceneManager.OpenScene(WorldScenePath, OpenSceneMode.Single);
            W($"模式 = {mode}  场景 = {WorldScenePath}");

            // ── 旧的 Gaia Water：apply 时禁用，remove 时恢复 ──
            var gaiaWater = GameObject.Find("Gaia Water");
            if (mode == "apply" && gaiaWater != null && gaiaWater.activeSelf)
            {
                gaiaWater.SetActive(false);
                W("已禁用旧 Gaia Water（保留在场景里，可随时恢复）");
            }
            else if (mode == "remove" && gaiaWater != null && !gaiaWater.activeSelf)
            {
                gaiaWater.SetActive(true);
                W("已恢复旧 Gaia Water");
            }

            if (mode == "report")
            {
                var existing = GameObject.Find(NodeName);
                W($"Crest 水面节点存在 = {existing != null}");
                if (existing != null)
                {
                    var wr = existing.GetComponent<WaterRenderer>();
                    W($"  WaterRenderer = {wr != null}, transform.y = {existing.transform.position.y:F1}");
                }
                W("==== 结论：PASS ====");
                Flush(sb, 0);
                return;
            }

            if (mode == "remove")
            {
                var old = GameObject.Find(NodeName);
                if (old != null) UnityEngine.Object.DestroyImmediate(old);
                // 同时清掉 WaterBody 子节点（它是水面的子物体，随父一起删）
                EditorSceneManager.MarkSceneDirty(scene);
                EditorSceneManager.SaveScene(scene);
                AssetDatabase.SaveAssets();
                W($"已删除 {NodeName}");
                W("==== 结论：PASS ====");
                Flush(sb, 0);
                return;
            }

            // ── apply ──
            // 幂等：先删旧的
            var stale = GameObject.Find(NodeName);
            if (stale != null) UnityEngine.Object.DestroyImmediate(stale);

            var waterMat = AssetDatabase.LoadAssetAtPath<Material>(WaterMatPath);
            var volumeMat = AssetDatabase.LoadAssetAtPath<Material>(VolumeMatPath);
            if (waterMat == null) { W($"FAIL：找不到水面材质 {WaterMatPath}"); Flush(sb, 2); return; }
            W($"水面材质 = {waterMat.name}（shader={waterMat.shader?.name}）");

            var root = new GameObject(NodeName);
            root.transform.position = new Vector3(LakeCarve.Center.x, LakeCarve.WaterLevel, LakeCarve.Center.y);
            W($"水面节点位置 = {root.transform.position}");

            var water = root.AddComponent<WaterRenderer>();
            // AddComponent 会调 Reset()，Reset 里已把 Water.mat / Chunk.prefab 填好。
            // 但 Reset 只在编辑器里、且通过 Inspector 添加时才走；这里显式兜底一次。
            water.Surface.Material = waterMat;
            water.Surface.VolumeMaterial = volumeMat;

            // ── 写入水的颜色参数（否则水面渲染成纯黑）──
            // 机制（代码事实）：Crest 的 Absorption/Scattering 仿真把
            // `_Crest_Absorption` / `_Crest_Scattering` 两个**全局**向量注入 shader，
            // 而这两个值来自水面材质上的 `_Crest_AbsorptionColor` 等属性，
            // 由 `WaterRenderer.UpdateAbsorptionFromColor(Material)` 在
            // **材质 Inspector 改动时**才计算写入（OnChange 回调）。
            // 工程里的 Water.mat 只序列化了 blend 参数，没有这两个属性 →
            // 运行时取到 (0,0,0,0) → 水面全黑（实测：湖盆形状正确、水面铺满，像素 0,0,0）。
            // 这里用 Crest 的 public API `CalculateAbsorptionValueFromColor` 按默认色算好写入。
            ApplyWaterColor(waterMat, volumeMat, W);

            // ── 视点与光 ──
            water.Viewer = Camera.main;
            water.PrimaryLight = UnityEngine.Object.FindObjectsOfType<Light>()
                .FirstOrDefault(l => l.type == LightType.Directional);

            // ── LOD：湖很小，压到最低档 ──
            // 默认 9 级是给"无限海洋"的，每级翻倍覆盖范围，9 级能铺到几十公里。
            // 湖盆半径 190m，4 级（覆盖约 8 倍视距）足够，且每级都有仿真开销。
            water.LodLevels = 4;
            // 默认 384 对 190m 的湖太浪费；160 在湖边看水面起伏已足够。
            water.LodResolution = 160;
            // ScaleRange 下限压到 1：水面网格跟着相机高度缩放，湖面不需要放大。
            water.ScaleRange = new Vector2(1f, 16f);
            W($"LodLevels={water.LodLevels}, LodResolution={water.LodResolution}, ScaleRange={water.ScaleRange}");

            // ── 模块裁剪（移动端 30fps）──
            // Clip 必须开：Crest 是海洋系统，水面是以视点为中心的 clipmap、铺到无限远。
            // 湖泊要靠 Clip 把湖盆以外的水面精确裁掉（见下方 Clip 配置）。
            //
            // ── Absorption / Scattering 仿真：关闭，改用材质恒定色 ──
            // shader 逻辑（Fragment.hlsl:274-306）：`absorption = 0` 后，只有
            // `g_Crest_SampleAbsorptionSimulation == true` 时才从仿真纹理采样，
            // 否则保留材质上的 `_Crest_Absorption` / `_Crest_Scattering`。
            // 因此关掉仿真 = 水面用材质恒定色（青蓝），且省掉两个每帧仿真 pass。
            // 对一个小湖（半径 190m）来说，空间变化的吸收色没有视觉收益：
            // 深浅变化主要靠 Depth 仿真 + 岸边泡沫体现。
            //
            // 实测背景：开启仿真时水面渲染成**纯黑**（湖盆形状与水面几何完全正确，
            // 像素却是 0,0,0）—— batchmode 的 Camera.Render() 不驱动 Crest 的
            // 仿真 compute pass，仿真纹理是空白，采样出来就是 0。
            // 即使不是 batchmode 环境，这个选择对移动端也更划算。
            water.AbsorptionLod.Enabled = false;
            water.ScatteringLod.Enabled = false;
            water.AlbedoLod.Enabled = false;

            water.DynamicWavesLod.Enabled = false;
            water.FlowLod.Enabled = false;
            water.ShadowLod.Enabled = false;
            water.ClipLod.Enabled = true;
            water.FoamLod.Enabled = true;
            water.DepthLod.Enabled = true;
            water.AnimatedWavesLod.Enabled = true;
            water.LevelLod.Enabled = true;
            W("模块：AnimatedWaves/Foam/Depth/Level/Clip = ON；" +
              "Absorption/Scattering/Albedo/DynamicWaves/Flow/Shadow = OFF（水色用材质恒定值）");

            // ── 反射：默认关（最贵的一项，且 Crest 无视锥剔除）──
            water.Reflections.Enabled = false;
            W("平面反射 = OFF");

            // ── 水下效果：保留但降配 ──
            water.Underwater.Enabled = true;

            // ── Clip：把无限水面裁成湖盆矩形（关键）──
            // 机制（代码事实）：
            //   Crest 的水面网格是以**视点**为中心的 clipmap，`LateUpdatePosition` 把
            //   Position 设为 (视点.x, 水面y, 视点.z)，各 chunk 相对它按 LOD 翻倍铺开，
            //   最外圈能到 ±数十公里。所以 WaterRenderer 的 transform XZ 位置**不影响水面**，
            //   而 WaterBody 的 chunk 级 AABB 剔除也挡不住这种巨型外圈 chunk
            //   （实测：49 个 chunk 里只有 8 个被剔除，水面仍铺满全世界）。
            // 因此用 Surface Clipping 做精确裁剪：
            //   1. ClipLod.DefaultClippingState = EverythingClipped → 默认全裁；
            //   2. ClipLodInput（Primitive/Cube，变换定义范围）→ 在湖盆矩形内把水加回来。
            //   对应手册 §14.1（"EverythingClipped + inputs add water"）。
            //   材质已带 _ALPHATEST_ON / _BUILTIN_AlphaClip（Water.mat），数据通路就绪。
            water.ClipLod.DefaultClippingState = DefaultClippingState.EverythingClipped;
            W("ClipLod.DefaultClippingState = EverythingClipped");

            // 湖盆加水区：用 ClipLodInput 的 Primitive(Cube) 模式。
            // Cube 的 SDF 对该 transform 的盒体内部写"有水"。
            var clipGo = new GameObject("Lake Clip Area");
            clipGo.transform.SetParent(root.transform, false);
            clipGo.transform.localPosition = Vector3.zero;
            clipGo.transform.localScale = new Vector3(LakeCarve.WaterHalfExtent * 2f,
                                                      DepthForClip(water), LakeCarve.WaterHalfExtent * 2f);
            var clip = clipGo.AddComponent<ClipLodInput>();
            clip.Primitive = LodInputPrimitive.Cube;
            // ── Inverted 的语义（读 ClipPrimitive.compute:99-104 精确推导）──
            //   初始数据：EverythingClipped → 全部写 1（>0.5 即裁掉）
            //   Inverted=true : target = min(target, sdf)
            //     盒内 sdf<0 → target<0 → 不裁（保留水）；盒外远处 sdf 大 → min(1,sdf)=1 → 裁。
            //   Inverted=false: target = max(target, 1-sdf) ≥ 1 → 全裁，一滴水都不剩。
            // 所以「默认全裁 + 只在盒内出水」必须用 Inverted=true。
            // 注意：2026-11-13 曾误改为 false 导致整湖消失（compute 语义推导见上）。
            clip.Inverted = true;
            W($"ClipLodInput: Cube, Inverted=true（盒内保留水）, 尺寸 {clipGo.transform.localScale}");

            // ── WaterBody：给水下体积/物理限定范围 ──
            // 注意：WaterBody 的 AABB 剔除是**整 chunk 粒度**，对"水面只在湖里"这个需求
            // 不够精确（巨型外圈 chunk 一定与 AABB 相交，全被保留）。
            // 真正裁水面靠上面的 Clip；WaterBody 这里只负责 Volume（水下效果）与物理的限域。
            var bodyGo = new GameObject("Lake Body");
            bodyGo.transform.SetParent(root.transform, false);
            bodyGo.transform.localPosition = Vector3.zero;
            float ext = LakeCarve.WaterHalfExtent;      // R + Blend = 280m
            bodyGo.transform.localScale = new Vector3(ext * 2f, 1f, ext * 2f);

            var body = bodyGo.AddComponent<WaterBody>();
            body.Exclusion = WaterBodyExclusion.Include;
            body.Precise = true;
            body.Affects = WaterBodyAffects.Volume | WaterBodyAffects.Physics;
            W($"WaterBody: AABB 半边长 = {ext}m，Exclusion=Include，Affects=Volume|Physics");

            // ── 波浪输入 ──
            // Crest 的波浪来自 **Input 组件**，不是 WaterRenderer 上的参数：
            // 只开 AnimatedWavesLod 而没有 Shape* 输入时，水面是绝对平坦的
            // （实测：水面渲染成一块纯色平板，完全没有起伏）。
            // 这里挂 ShapeGerstner 并指定 Crest 自带的 WavesModerate 频谱。
            var shapeGo = new GameObject("Lake Waves");
            shapeGo.transform.SetParent(root.transform, false);
            var shape = shapeGo.AddComponent<ShapeGerstner>();
            var spectrum = AssetDatabase.LoadAssetAtPath<WaveSpectrum>(
                "Packages/com.waveharmonic.crest/Runtime/Data/WaveSpectra/WavesModerate.asset");
            if (spectrum != null)
            {
                shape.Spectrum = spectrum;
                W($"波浪频谱 = {spectrum.name}");
            }
            // 湖面不需要大风浪：风程短，20 km/h 左右起小波（默认 10 太静）。
            water.WindSpeed = 20f;
            W($"风速 = {water.WindSpeed} km/h，ShapeGerstner 已挂");

            // ── URP 前置：Crest 的深度/不透明图依赖 ──
            EnableUrpDepthAndOpaque(W);

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            // ── 校验 ──
            W("");
            W("=== 校验 ===");
            bool ok = Verify(W);
            W($"==== 结论：{(ok ? "PASS" : "FAIL")} ====");
            Flush(sb, ok ? 0 : 2);
        }
        catch (Exception ex)
        {
            W($"异常：{ex}");
            W("==== 结论：FAIL ====");
            Flush(sb, 1);
        }
    }

    /// <summary>
    /// 打开 URP 资产上的 Depth Texture / Opaque Texture。
    ///
    /// 为什么必须开：Crest 的
    ///   - 水下吸收/散射（UnderwaterRenderer 读 _CameraDepthTexture）
    ///   - 水面与地形的深度比较（判断哪部分在水下、岸边泡沫）
    ///   - 折射（需要 _CameraOpaqueTexture）
    /// 都依赖这两张图。工程当前都是 0（`UniversalRP.asset:22-23`），
    /// 不开会导致水下效果失效、岸边泡沫不出现，且**不报错**——静默不生效，最难排查。
    /// </summary>
    private static void EnableUrpDepthAndOpaque(Action<string> W)
    {
        const string path = "Assets/Settings/UniversalRP.asset";
        var text = File.ReadAllText(path);
        bool changed = false;

        string[] keys = { "m_RequireDepthTexture", "m_RequireOpaqueTexture" };
        foreach (var key in keys)
        {
            if (System.Text.RegularExpressions.Regex.IsMatch(text, key + @":\s*1")) continue;
            text = System.Text.RegularExpressions.Regex.Replace(text, key + @":\s*0", key + ": 1");
            changed = true;
            W($"  已开启 {key} = 1");
        }

        if (changed)
        {
            File.WriteAllText(path, text);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            W("  UniversalRP.asset 已重新导入");
        }
        else
        {
            W("  Depth/Opaque 已开启，无需修改");
        }
    }

    /// <summary>
    /// 把 Crest 的 Absorption / Scattering 消光系数写进材质，决定水体的透明程度。
    ///
    /// ── 为什么"透明度"由这两个系数决定（代码事实）──
    /// `Fragment.hlsl:625-670`：
    ///     o_Emission *= 1 - volumeOpacity;          // 场景色（折射采样）被衰减
    ///     o_Emission += volumeLight * volumeOpacity; // 补上水体自身的散射光
    /// 其中 `VolumeLighting.hlsl:22-25`：
    ///     volumeOpacity = 1 - exp(-extinction * 视线在水中的长度)
    ///     extinction    = absorption + scattering
    /// 所以 **extinction 越大越不透明**，与材质的 alpha 通道无关
    /// （`CREST_SIMPLE_TRANSPARENCY (0)`，见 `Settings.Crest.Default.hlsl:23`，
    ///  非 SimpleTransparency 路径下 `o_Alpha` 恒为 1，走的是折射合成而非 alpha 混合）。
    ///
    /// ── 2026-11-13 的参数为什么必须改 ──
    /// 上一版 absorption=(0.194,0.064,0.029) + scattering=(0.10,0.42,0.50)
    /// → extinction≈(0.29,0.48,0.53)/m → 5m 处只剩 exp(-2.4)≈9%，
    /// 等于下水就看不见底，观感就是"一块不透明的蓝板"。
    /// 现按"岸边看得见水底、湖心 30m 渐变成深青"反推：
    ///   目标 extinction ≈(0.061,0.041,0.045)/m → 1/e 深度 16/24/22m
    ///   → 3m 处保留 83% 场景色（能看见底），30m 处 16%（深水区自然是深色）。
    ///
    /// ── 颜色怎么反推成系数 ──
    /// `WaterRenderer.UpdateAbsorptionFromColor`（WaterRenderer.cs:519-527）：
    ///     absorption = -color.a * 32/5 * ln(color.rgb)
    /// 即 color.a 是"密度"，color.rgb 是"透过水看到的颜色"。
    /// 所以 alpha 越小越清澈；rgb 越接近白，各通道衰减越均衡（越不偏色）。
    ///
    /// ── scattering 为什么 R 要压得很低 ──
    /// 体积光的颜色 = `scatteringAmount * 入射光`，而
    /// `scatteringAmount = scattering / extinction`（VolumeLighting.hlsl:78）。
    /// 上一版 scattering=(0.034,0.046,0.050) 让三通道比值接近 1，
    /// 入射光一乘就趋近白 —— 实测水下画面均值 (118,131,139)，雾是白的。
    /// 现取 (0.012,0.026,0.036) → scatteringAmount≈(0.20,0.63,0.80)，
    /// 雾色明显偏青蓝，这才是湖水该有的水下观感。
    /// </summary>
    private static void ApplyWaterColor(Material waterMat, Material volumeMat, Action<string> W)
    {
        var absorptionColor = new Color(0.62f, 0.86f, 0.92f, 0.016f);
        var scatteringColor = new Color(0.012f, 0.026f, 0.036f, 1f);

        var absorption = WaterRenderer.CalculateAbsorptionValueFromColor(absorptionColor);
        var scattering = new Vector4(scatteringColor.r, scatteringColor.g, scatteringColor.b, scatteringColor.a);

        void Set(Material m, string label)
        {
            if (m == null) return;
            m.SetColor("_Crest_AbsorptionColor", absorptionColor);
            m.SetVector("_Crest_Absorption", absorption);
            m.SetVector("_Crest_Scattering", scattering);
            EditorUtility.SetDirty(m);
        }

        Set(waterMat, "Water.mat");
        Set(volumeMat, "Water Volume.mat");

        // 水下雾的密度倍率。`Underwater.hlsl:244-245` 把 absorption/scattering 乘上它，
        // 所以它只影响**水下**观感，不改水面透明度。
        // shader 里声明为 Range(0,1)（Underwater.shader:8），**只能调淡不能调浓**，
        // 故取 1（沿用材质原值），靠 absorption 本身的青蓝偏色形成水下色偏。
        if (volumeMat != null && !Mathf.Approximately(volumeMat.GetFloat("_Crest_ExtinctionMultiplier"), 1f))
        {
            volumeMat.SetFloat("_Crest_ExtinctionMultiplier", 1f);
            EditorUtility.SetDirty(volumeMat);
        }

        // ── 水下体积光的入射光权重 ──
        // `UnderwaterShared.hlsl:288-303` 把这两个值传给 VolumeLighting 的
        // ambientTerm / directTerm，缩放"环境光 / 阳光注入散射光"的量。
        // 默认 1.0 → 水下雾亮度等于水面受光，观感是"白雾"（实测均值 R106 G126 B137）。
        // 湖水只有上部透光，压到 0.35/0.4 才有"越深越暗"的观感。
        //
        // ⚠ **必须写在水面材质 Water.mat 上，不能写 Water Volume.mat**。
        // `UnderwaterRenderer.Effect.cs:247` 每帧调
        // `_VolumeMaterial.CopyMatchingPropertiesFromMaterial(_SurfaceMaterial)`，
        // 把水面材质里的**同名属性**整体覆盖到水下材质上
        // （由 `_CopyWaterMaterialParametersEachFrame: 1` 每帧执行，见场景 serialized 数据）。
        // 所以只在 Volume 材质上设值会在下一帧被水面材质覆盖回 1.0，静默失效。
        //
        // 同时它不影响水面本身：水面走 `Fragment.hlsl` 自己的 VolumeLighting 调用，
        // 那里的入射光项已被 `o_Emission *= 1-volumeOpacity` 主导，权重变化肉眼不可见。
        void SetScatteringWeights(Material m)
        {
            if (m == null) return;
            m.SetFloat("_Crest_AmbientTerm", 0.35f);
            m.SetFloat("_Crest_DirectTerm", 0.40f);
            EditorUtility.SetDirty(m);
        }

        SetScatteringWeights(waterMat);
        SetScatteringWeights(volumeMat);
        W("  体积光入射权重：_Crest_AmbientTerm=0.35, _Crest_DirectTerm=0.40（写在两个材质上，Volume 会被水面覆盖）");

        AssetDatabase.SaveAssets();
        W($"  写水色：_Crest_AbsorptionColor={absorptionColor}, density(a)={absorptionColor.a}");
    }

    /// <summary>
    /// 打印由当前材质参数推出的消光系数与 1/e 深度，作为"水有多透明"的可核查证据。
    /// </summary>
    private static string DescribeOptics(Material waterMat)
    {
        var a = waterMat.GetVector("_Crest_Absorption");
        var s = waterMat.GetVector("_Crest_Scattering");
        float ex = a.x + s.x, ey = a.y + s.y, ez = a.z + s.z;
        float d(double e) => e > 1e-5 ? (float)(1.0 / e) : 9999f;

        var lines = new StringBuilder();
        lines.AppendLine("  === 水面光学参数 ===");
        lines.AppendLine($"  _Crest_Absorption   = ({a.x:F4},{a.y:F4},{a.z:F4})  /m");
        lines.AppendLine($"  _Crest_Scattering   = ({s.x:F4},{s.y:F4},{s.z:F4})  /m");
        lines.AppendLine($"  extinction(=A+S)    = ({ex:F4},{ey:F4},{ez:F4})  /m");
        lines.AppendLine($"  1/e 深度            = R {d(ex):F1}m / G {d(ey):F1}m / B {d(ez):F1}m");
        lines.AppendLine($"  3m 处保留场景色     = {100f * Mathf.Exp(-ex * 3f):F0}% (R 通道，最差)");
        lines.AppendLine($"  30m 处保留场景色    = {100f * Mathf.Exp(-ex * 30f):F1}%");
        return lines.ToString().TrimEnd();
    }

    /// <summary>Clip 盒体的 Y 尺寸：只要覆盖水面上下即可（SDF 只判 XZ 有效范围由 transform 决定）。</summary>
    private static float DepthForClip(WaterRenderer water) => 200f;

    private static bool Verify(Action<string> W)
    {
        bool ok = true;

        var root = GameObject.Find(NodeName);
        if (root == null) { W("  FAIL：找不到 Crest 水面节点"); return false; }

        var water = root.GetComponent<WaterRenderer>();
        if (water == null) { W("  FAIL：节点上没有 WaterRenderer"); return false; }
        // 注意：不能用 water.SeaLevel 校验。它读的是 internal Position，
        // 而 Position 只在运行时 RunUpdate 里被赋值（WaterRenderer.cs:256），
        // 编辑器批处理下恒为 0。水位要看 transform.position.y。
        float y = root.transform.position.y;
        W($"  WaterRenderer OK, transform.y={y:F1}（期望 {LakeCarve.WaterLevel:F1}）");
        if (Mathf.Abs(y - LakeCarve.WaterLevel) > 0.01f) ok = false;

        if (water.Surface.Material == null) { W("  FAIL：Surface.Material 为空"); ok = false; }
        else W($"  Surface.Material = {water.Surface.Material.name}");

        var body = root.GetComponentInChildren<WaterBody>();
        if (body == null) { W("  FAIL：没有 WaterBody"); ok = false; }
        else
        {
            W($"  WaterBody: Exclusion={body.Exclusion} Affects={body.Affects}");
            if (body.Exclusion != WaterBodyExclusion.Include) ok = false;
        }

        // Clip 必须配置成「默认全裁 + 输入加水」。
        // 注意：不能校验 ClipLod.Enabled —— 它读的是 `_Enabled && _Valid`，
        // 而 _Valid 只在运行时 Initialize()（需要 GPU）后为 true，
        // 编辑期批处理访问必然 false（Lod.cs:720）。校验要落到可持久化的字段上。
        bool clipState = water.ClipLod.DefaultClippingState == DefaultClippingState.EverythingClipped;
        // 水的颜色来源（Absorption/Scattering）必须在配置里显式开。
        // 注意导出/写入 _Enabled 后，编辑期读 Enabled 仍会是 false（_Valid 未就绪），
        // 所以这里只能打印提示，不能当判据。
        W($"  AbsorptionLod.Enabled(get)={water.AbsorptionLod.Enabled}, " +
          $"ScatteringLod.Enabled(get)={water.ScatteringLod.Enabled}, " +
          $"AlbedoLod.Enabled(get)={water.AlbedoLod.Enabled}（编辑期 _Valid 未就绪，仅供参考）");

        W($"  ClipLod.DefaultClippingState = {water.ClipLod.DefaultClippingState}（期望 EverythingClipped）");
        if (!clipState) ok = false;
        var clipInputs = root.GetComponentsInChildren<ClipLodInput>(true);
        W($"  ClipLodInput 数量 = {clipInputs.Length}（期望 >= 1）");
        if (clipInputs.Length == 0) ok = false;
        foreach (var c in clipInputs)
            W($"    {c.name}: Primitive={c.Primitive} Inverted={c.Inverted} scale={c.transform.lossyScale}");

        // 旧水必须已禁用
        var gaia = GameObject.Find("Gaia Water");
        W($"  旧 Gaia Water 激活状态 = {(gaia != null ? gaia.activeSelf : false)}（期望 false）");
        if (gaia != null && gaia.activeSelf) ok = false;

        // URP 深度/不透明图
        var urp = File.ReadAllText("Assets/Settings/UniversalRP.asset");
        bool depth = System.Text.RegularExpressions.Regex.IsMatch(urp, @"m_RequireDepthTexture:\s*1");
        bool opaque = System.Text.RegularExpressions.Regex.IsMatch(urp, @"m_RequireOpaqueTexture:\s*1");
        W($"  URP DepthTexture={depth}, OpaqueTexture={opaque}");
        if (!depth || !opaque) ok = false;

        return ok;
    }

    private static void Flush(StringBuilder sb, int code)
    {
        Directory.CreateDirectory(OutDir);
        File.WriteAllText(ResultPath, sb.ToString());
        EditorApplication.Exit(code);
    }
}
