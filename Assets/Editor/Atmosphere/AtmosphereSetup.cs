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

            var enManager = enRoot.GetComponent<EnviroManager>();
            enManager.LoadConfiguration();
            EditorUtility.SetDirty(enManager);
            W("已从 configuration 资产加载 Enviro 模块（Time/Fog/Weather/...）");

            var quality = CreateOrUpdateMobileQuality(W);

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

        if (q.skyOverride == null) q.skyOverride = new EnviroSkyQualitySettings();
        if (q.volumetricCloudsOverride == null) q.volumetricCloudsOverride = new EnviroVolumetricCloudsQualitySettings();
        if (q.fogOverride == null) q.fogOverride = new EnviroFogQualitySettings();
        if (q.flatCloudsOverride == null) q.flatCloudsOverride = new EnviroFlatCloudsQualitySettings();
        if (q.auroraOverride == null) q.auroraOverride = new EnviroAuroraQualitySettings();
        if (q.effectsOverride == null) q.effectsOverride = new EnviroEffectsQualitySettings();

        q.skyOverride.skyMode = EnviroSky.SkyMode.Simple;

        q.volumetricCloudsOverride.volumetricClouds = false;
        q.volumetricCloudsOverride.downsampling = 4;
        q.volumetricCloudsOverride.stepsLayer1 = 32;

        q.flatCloudsOverride.cirrusClouds = true;
        q.flatCloudsOverride.flatClouds = true;
        q.flatCloudsOverride.flatCloudsShadowSteps = 4;

        q.fogOverride.fog = true;
        q.fogOverride.fogQualityMode = EnviroFogSettings.FogQualityMode.Simple;
        q.fogOverride.volumetrics = true;
        q.fogOverride.unityFog = false;
        q.fogOverride.quality = EnviroFogSettings.Quality.Low;
        q.fogOverride.steps = 32;

        q.auroraOverride.aurora = false;
        q.auroraOverride.steps = 16;

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
