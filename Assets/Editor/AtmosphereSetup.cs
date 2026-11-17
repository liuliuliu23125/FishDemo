using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Enviro;
using FishDemo.Gameplay;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

/// <summary>
/// 把 Enviro 3 接进 World.unity 与 URP（昼夜 / 天气 / 体积雾 / 体积光）。
///
/// ── 第一性原理：为什么是 Enviro 一个包，而不是 4 个包一起上 ──
/// 1. 昼夜/天气/全局体积雾/方向光体积光 Enviro 全部覆盖：
///    - 夜间主光由它自己的 Sun/Moon Directional Light 驱动；
///    - 体积雾 shader 采样 `_MainLightShadowmapTexture`（EnviroVolumetricsURP.shader:154-168），
///      树影会形成光柱，不需要另装 Volumetric Lights 2；
///    - 体积雾 Low 档是 1/4 分辨率 raymarch + 双边模糊上采样（EnviroFogModule.cs:571-600），
///      符合移动端预算。
/// 2. Volumetric Fog Mist 2 的价值是"局部雾体积"（山谷/海面），当前场景没有这种需求；
///    Volumetric Light Beam / Lights 2 需要灯笼/火堆等发光体，场景里一个都没有。
///    先不引入，避免 4 套 fullscreen pass 的排序与性能风险；等有具体对象再接。
///
/// ── 必须改的三处工程配置 ──
/// 1. `ENVIRO_URP` 宏：Enviro 的 `EnviroDefineSymbol` 只加 `ENVIRO_3`。没有这个宏，
///    `EnviroURPRenderFeature` 整个类型被 #if 掉，雾/云/天空全部静默失效。
/// 2. URP `m_RequireDepthTexture = 1`：体积雾/云都读 `_CameraDepthTexture`。
///    Crest 当前是 SimpleTransparency，depth 本来是 0；开这一项会多一次 depth prepass，
///    这是大气系统的硬成本，必须计入预算。
/// 3. Renderer 挂 `EnviroURPRenderFeature`：它负责在 BeforeRenderingTransparents-1
///    执行天空/雾/云 pass。
///
/// ── 为什么旧 Directional Light 只禁用组件、不删 ──
/// Enviro 预制体自带 Sun/Moon Directional Light。两盏 main light 会让 URP 只取最亮的那盏，
/// 另一盏变成 additional light（per-object limit 4），阴影/颜色都会错。
/// 保留旧对象只关 Light 组件，便于 Remove 回滚，也不会破坏场景其他引用。
///
/// 用法：
///   -executeMethod AtmosphereSetup.EnsureDefines   （先跑，改宏后需要重新编译）
///   -executeMethod AtmosphereSetup.Apply
///   -executeMethod AtmosphereSetup.Remove
///   -executeMethod AtmosphereSetup.Report
/// 输出 Logs/Atmosphere/atmosphere-setup.txt
/// </summary>
public static class AtmosphereSetup
{
    private const string Tag = "[Atmosphere]";
    private const string WorldScenePath = "Assets/Scenes/World.unity";
    private const string EnviroPrefabPath = "Assets/Enviro 3 - Sky and Weather/Enviro 3.prefab";
    private const string RendererPath = "Assets/Settings/UniversalRenderer.asset";
    private const string UrpAssetPath = "Assets/Settings/UniversalRP.asset";
    private const string QualityPath = "Assets/Settings/Enviro Mobile Quality.asset";
    private const string OutDir = "Logs/Atmosphere";
    private const string ResultPath = OutDir + "/atmosphere-setup.txt";

    private const string EnviroNodeName = "Enviro 3";
    private const string ControllerNodeName = "Atmosphere";
    private const string LegacyLightName = "Directional Light";

    public static void EnsureDefines()
    {
        var sb = new StringBuilder();
        void W(string s) { Debug.Log($"{Tag} {s}"); sb.AppendLine(s); }

        try
        {
            // 编辑器用当前 active build target group 的宏；Android 必须单独写，
            // 否则编辑器里能跑、打包后全部 Enviro URP 代码消失。
            AddDefines(W, BuildTargetGroup.Standalone, "ENVIRO_3", "ENVIRO_URP");
            AddDefines(W, BuildTargetGroup.Android, "ENVIRO_3", "ENVIRO_URP");
            AssetDatabase.SaveAssets();
            W("==== 结论：PASS（下一进程重新编译后生效）====");
            Flush(sb, 0);
        }
        catch (Exception ex)
        {
            W($"异常：{ex}");
            W("==== 结论：FAIL ====");
            Flush(sb, 1);
        }
    }

    public static void Apply()
    {
        var sb = new StringBuilder();
        void W(string s) { Debug.Log($"{Tag} {s}"); sb.AppendLine(s); }

        try
        {
            var scene = EditorSceneManager.OpenScene(WorldScenePath, OpenSceneMode.Single);

            // ── 1. 旧主光：只关 Light 组件 ──
            GameObject legacy = scene.GetRootGameObjects().FirstOrDefault(g => g.name == LegacyLightName);
            if (legacy != null)
            {
                var light = legacy.GetComponent<Light>();
                if (light != null && light.type == LightType.Directional)
                {
                    light.enabled = false;
                    W($"已禁用旧主光：{LegacyLightName}（Enviro 自带 Sun/Moon Directional Light）");
                }
            }
            else
            {
                W($"未找到旧主光「{LegacyLightName}」，跳过（可能已改过名）");
            }

            // ── 2. Enviro 预制体实例（幂等）──
            var enRoot = scene.GetRootGameObjects().FirstOrDefault(g => g.name == EnviroNodeName);
            if (enRoot == null)
            {
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(EnviroPrefabPath);
                if (prefab == null)
                {
                    W($"FAIL：找不到 Enviro 预制体 {EnviroPrefabPath}");
                    Flush(sb, 2);
                    return;
                }
                enRoot = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
                enRoot.name = EnviroNodeName;
                SceneManager.MoveGameObjectToScene(enRoot, scene);
                W($"已实例化 Enviro 预制体并命名为「{EnviroNodeName}」");
            }
            else
            {
                W($"复用已存在的「{EnviroNodeName}」");
            }

            if (enRoot.GetComponent<EnviroManager>() == null)
            {
                W("FAIL：Enviro 根节点上没有 EnviroManager");
                Flush(sb, 2);
                return;
            }

            // ── 2.5 把 configuration 资产里的模块挂到 manager 上 ──
            // 这是本次接入最关键的一步。Enviro 的模块（Time/Fog/Weather/...）不是预制体
            // 的孩子，而是 Edit Mode 下 EnviroManager.Update() 调用 LoadConfiguration()
            // 才从 configuration 资产赋值的。Apply 在同一帧里实例化并保存场景，
            // Update 还没跑 → 保存下来的模块字段全是 null →
            // 进 Play Mode 后 StartModules() 因为 null 直接跳过 → 整个大气系统静默失效
            // （实测日志：Time=False Lighting=False ... 全部 False）。
            // 所以必须在保存前显式加载并标记 manager dirty。
            var enManager = enRoot.GetComponent<EnviroManager>();
            enManager.LoadConfiguration();
            EditorUtility.SetDirty(enManager);
            W("已从 configuration 资产加载 Enviro 模块（Time/Fog/Weather/...）");

            // ── 3. 移动端质量档（自定义 EnviroQuality 资产）──
            var quality = CreateOrUpdateMobileQuality(W);

            // ── 4. 运行时控制器 ──
            var ctrlRoot = scene.GetRootGameObjects().FirstOrDefault(g => g.name == ControllerNodeName);
            if (ctrlRoot == null)
            {
                ctrlRoot = new GameObject(ControllerNodeName);
                SceneManager.MoveGameObjectToScene(ctrlRoot, scene);
                W($"已创建控制节点「{ControllerNodeName}」");
            }
            var ctrl = ctrlRoot.GetComponent<AtmosphereController>();
            if (ctrl == null) ctrl = ctrlRoot.AddComponent<AtmosphereController>();
            var ctrlSo = new SerializedObject(ctrl);
            ctrlSo.FindProperty("_mobileQuality").objectReferenceValue = quality;
            ctrlSo.ApplyModifiedPropertiesWithoutUndo();
            W("已绑定 AtmosphereController 与移动端质量档");

            // ── 5. URP depth texture ──
            var urp = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(UrpAssetPath);
            if (urp == null)
            {
                W($"FAIL：找不到 URP 资产 {UrpAssetPath}");
                Flush(sb, 2);
                return;
            }
            var urpSo = new SerializedObject(urp);
            var depthProp = urpSo.FindProperty("m_RequireDepthTexture");
            bool beforeDepth = depthProp.intValue == 1;
            depthProp.intValue = 1;
            urpSo.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(urp);
            W($"URP depth texture：{(beforeDepth ? "已是 1" : "0 → 1")}");

            // ── 6. Renderer feature ──
#if ENVIRO_URP
            var rendererData = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(RendererPath);
            if (rendererData == null)
            {
                W($"FAIL：找不到 Renderer 资产 {RendererPath}");
                Flush(sb, 2);
                return;
            }
            bool hasFeature = rendererData.rendererFeatures.Any(f => f is EnviroURPRenderFeature);
            if (!hasFeature)
            {
                var feature = ScriptableObject.CreateInstance<EnviroURPRenderFeature>();
                feature.name = "Enviro Render Feature";
                AssetDatabase.AddObjectToAsset(feature, rendererData);
                rendererData.rendererFeatures.Add(feature);
                EditorUtility.SetDirty(rendererData);
                rendererData.SetDirty();
                AssetDatabase.SaveAssets();
                AssetDatabase.ImportAsset(RendererPath, ImportAssetOptions.ForceUpdate);
                W("已把 EnviroURPRenderFeature 挂到 UniversalRenderer");
            }
            else
            {
                W("EnviroURPRenderFeature 已存在");
            }
#else
            W("FAIL：当前程序集没有 ENVIRO_URP 宏。先跑 AtmosphereSetup.EnsureDefines，" +
              "等 Unity 重新编译后再跑 Apply");
            Flush(sb, 2);
            return;
#endif

            // descriptor 由 OnValidate 在 ImportAsset 时重建；这里再存一次确保落盘。
            AssetDatabase.SaveAssets();
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            W($"场景已保存：{WorldScenePath}");
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

    public static void Remove()
    {
        var sb = new StringBuilder();
        void W(string s) { Debug.Log($"{Tag} {s}"); sb.AppendLine(s); }

        try
        {
            var scene = EditorSceneManager.OpenScene(WorldScenePath, OpenSceneMode.Single);

            foreach (var name in new[] { ControllerNodeName, EnviroNodeName })
            {
                var go = scene.GetRootGameObjects().FirstOrDefault(g => g.name == name);
                if (go != null)
                {
                    UnityEngine.Object.DestroyImmediate(go);
                    W($"已删除「{name}」");
                }
            }

            var legacy = scene.GetRootGameObjects().FirstOrDefault(g => g.name == LegacyLightName);
            if (legacy != null)
            {
                var light = legacy.GetComponent<Light>();
                if (light != null)
                {
                    light.enabled = true;
                    W($"已恢复旧主光：{LegacyLightName}");
                }
            }

#if ENVIRO_URP
            var rendererData = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(RendererPath);
            if (rendererData != null)
            {
                var remove = rendererData.rendererFeatures.Where(f => f is EnviroURPRenderFeature).ToList();
                foreach (var f in remove)
                {
                    rendererData.rendererFeatures.Remove(f);
                    AssetDatabase.RemoveObjectFromAsset(f);
                    UnityEngine.Object.DestroyImmediate(f, true);
                    W("已移除 EnviroURPRenderFeature");
                }
                if (remove.Count > 0)
                {
                    EditorUtility.SetDirty(rendererData);
                    rendererData.SetDirty();
                    AssetDatabase.SaveAssets();
                }
            }
#endif

            var urp = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(UrpAssetPath);
            if (urp != null)
            {
                var so = new SerializedObject(urp);
                so.FindProperty("m_RequireDepthTexture").intValue = 0;
                so.ApplyModifiedPropertiesWithoutUndo();
                EditorUtility.SetDirty(urp);
                W("URP depth texture → 0");
            }

            if (AssetDatabase.LoadAssetAtPath<EnviroQuality>(QualityPath) != null)
            {
                AssetDatabase.DeleteAsset(QualityPath);
                W($"已删除 {QualityPath}");
            }

            // Enviro 会把自己的天空盒材质写进 RenderSettings；移除后恢复 Unity 默认天空盒
            // 与 Skybox 环境光模式，避免场景留下一个指向已删除材质的引用。
            var defaultSky = AssetDatabase.GetBuiltinExtraResource<Material>("Default-Skybox.mat");
            if (defaultSky != null) RenderSettings.skybox = defaultSky;
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Skybox;
            W("已恢复默认天空盒 / Skybox 环境光模式");

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            AssetDatabase.SaveAssets();
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

    public static void Report()
    {
        var sb = new StringBuilder();
        void W(string s) { Debug.Log($"{Tag} {s}"); sb.AppendLine(s); }

        try
        {
            var scene = EditorSceneManager.OpenScene(WorldScenePath, OpenSceneMode.Single);
            var roots = scene.GetRootGameObjects();

            W($"Enviro 预制体：{EnviroPrefabPath} = {(AssetDatabase.LoadAssetAtPath<GameObject>(EnviroPrefabPath) != null ? "存在" : "缺失")}");
            W($"场景 Enviro 根：{(roots.Any(g => g.name == EnviroNodeName) ? "存在" : "缺失")}");
            W($"场景 Atmosphere 根：{(roots.Any(g => g.name == ControllerNodeName) ? "存在" : "缺失")}");

            var legacy = roots.FirstOrDefault(g => g.name == LegacyLightName);
            var light = legacy != null ? legacy.GetComponent<Light>() : null;
            W($"旧主光：{(light != null ? (light.enabled ? "仍启用（FAIL）" : "已禁用") : "不存在")}");

            int activeDirectional = 0;
            var allLights = UnityEngine.Object.FindObjectsOfType<Light>(true);
            foreach (var l in allLights)
                if (l.type == LightType.Directional && l.enabled && l.gameObject.activeInHierarchy) activeDirectional++;
            W($"启用的方向光数量：{activeDirectional}（期望 1，Enviro 的 Sun/Moon）");

            var urp = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(UrpAssetPath);
            var urpSo = new SerializedObject(urp);
            W($"URP depth texture = {urpSo.FindProperty("m_RequireDepthTexture").intValue}（期望 1）");

            var rendererData = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(RendererPath);
            var features = rendererData != null ? rendererData.rendererFeatures : new List<ScriptableRendererFeature>();
            W($"Renderer features（{features.Count}）：{string.Join(", ", features.Select(f => f != null ? f.GetType().Name : "null"))}");
            bool hasEnviro = features.Any(f => f != null && f.GetType().Name == "EnviroURPRenderFeature");
            W($"EnviroURPRenderFeature：{(hasEnviro ? "已挂" : "缺失（FAIL）")}");

            var q = AssetDatabase.LoadAssetAtPath<EnviroQuality>(QualityPath);
            if (q != null)
            {
                W($"质量档：{q.name} / 天空={q.skyOverride?.skyMode} / 体积云={q.volumetricCloudsOverride?.volumetricClouds} " +
                  $"/ 平云={q.flatCloudsOverride?.flatClouds} / 雾体积={q.fogOverride?.volumetrics} " +
                  $"/ 雾质量={q.fogOverride?.quality} / steps={q.fogOverride?.steps} / 极光={q.auroraOverride?.aurora}");
            }
            else
            {
                W($"质量档：{QualityPath} 缺失（FAIL）");
            }

            bool pass = activeDirectional == 1 && hasEnviro &&
                        urpSo.FindProperty("m_RequireDepthTexture").intValue == 1 && q != null;
            W($"==== 结论：{(pass ? "PASS" : "FAIL")} ====");
            Flush(sb, pass ? 0 : 2);
        }
        catch (Exception ex)
        {
            W($"异常：{ex}");
            W("==== 结论：FAIL ====");
            Flush(sb, 1);
        }
    }

    private static EnviroQuality CreateOrUpdateMobileQuality(Action<string> W)
    {
        var q = AssetDatabase.LoadAssetAtPath<EnviroQuality>(QualityPath);
        bool created = false;
        if (q == null)
        {
            q = ScriptableObject.CreateInstance<EnviroQuality>();
            q.name = "Enviro Mobile Quality";
            AssetDatabase.CreateAsset(q, QualityPath);
            created = true;
        }

        // 新建的 ScriptableObject 里，[Serializable] 引用类型字段可能是 null。
        if (q.skyOverride == null) q.skyOverride = new EnviroSkyQualitySettings();
        if (q.volumetricCloudsOverride == null) q.volumetricCloudsOverride = new EnviroVolumetricCloudsQualitySettings();
        if (q.fogOverride == null) q.fogOverride = new EnviroFogQualitySettings();
        if (q.flatCloudsOverride == null) q.flatCloudsOverride = new EnviroFlatCloudsQualitySettings();
        if (q.auroraOverride == null) q.auroraOverride = new EnviroAuroraQualitySettings();
        if (q.effectsOverride == null) q.effectsOverride = new EnviroEffectsQualitySettings();

        // 天空：Simple 跳过 front/back 多层颜色计算，移动端先保帧率。
        q.skyOverride.skyMode = EnviroSky.SkyMode.Simple;

        // 体积云：关闭。128 步 raymarch 与 30fps 预算冲突；用 flat clouds 表达云量。
        q.volumetricCloudsOverride.volumetricClouds = false;
        q.volumetricCloudsOverride.downsampling = 4;
        q.volumetricCloudsOverride.stepsLayer1 = 32;

        // 平云：开着，阴影步数压到 4（只影响云影投射到地面的解析步数）。
        q.flatCloudsOverride.cirrusClouds = true;
        q.flatCloudsOverride.flatClouds = true;
        q.flatCloudsOverride.flatCloudsShadowSteps = 4;

        // 体积雾：Low 档 = 1/4 分辨率 raymarch + 双边模糊上采样（EnviroFogModule.cs:571-600）。
        // 注意 Low 预设里 volumetrics=0，这里必须显式改回 true，否则没有体积光。
        q.fogOverride.fog = true;
        q.fogOverride.fogQualityMode = EnviroFogSettings.FogQualityMode.Simple; // height fog 单次 blit
        q.fogOverride.volumetrics = true;
        q.fogOverride.unityFog = false;
        q.fogOverride.quality = EnviroFogSettings.Quality.Low;
        q.fogOverride.steps = 32;

        // 极光：这个纬度/场景不需要。
        q.auroraOverride.aurora = false;
        q.auroraOverride.steps = 16;

        // 雨雪粒子：移动端发射率减半。
        q.effectsOverride.particeEmissionRateModifier = 0.5f;

        EditorUtility.SetDirty(q);
        AssetDatabase.SaveAssets();
        W($"{(created ? "已创建" : "已更新")}移动端质量档：{QualityPath}");
        return q;
    }

    private static void AddDefines(Action<string> W, BuildTargetGroup group, params string[] defines)
    {
        string current = PlayerSettings.GetScriptingDefineSymbolsForGroup(group);
        var list = current.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries).ToList();
        bool changed = false;
        foreach (var d in defines)
        {
            if (!list.Contains(d))
            {
                list.Add(d);
                changed = true;
            }
        }
        if (changed)
        {
            PlayerSettings.SetScriptingDefineSymbolsForGroup(group, string.Join(";", list));
            W($"{group} 宏：{current} → {string.Join(";", list)}");
        }
        else
        {
            W($"{group} 宏已包含 {string.Join(",", defines)}");
        }
    }

    private static void Flush(StringBuilder sb, int exitCode)
    {
        Directory.CreateDirectory(OutDir);
        File.WriteAllText(ResultPath, sb.ToString());
        EditorApplication.Exit(exitCode);
    }
}
