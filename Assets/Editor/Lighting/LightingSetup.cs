using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using FishDemo.Gameplay;
using UnityEditor;
using UnityEditor.Rendering;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

public static class LightingSetup
{
    private const string Tag = "[LightingSetup]";
    private const string WorldScenePath = "Assets/Scenes/World.unity";
    private const string SettingsDir = "Assets/Settings";
    private const string LightingDir = "Assets/Settings/Lighting";
    private const string PostProcessDir = "Assets/Settings/PostProcess";
    private const string ProfilePath = "Assets/Settings/PostProcess/FishDemo Global Volume.asset";
    private const string OutDir = "Logs/Lighting";
    private const string ResultPath = OutDir + "/lighting-setup.txt";

    private const string TorchRootName = "Lakeside Torches";
    private const string VolumeRootName = "Post Processing";
    private const int TorchCount = 12;
    private const float TorchScale = 4f;
    private const float BaseIntensity = 120f;
    private const float BaseRange = 12f;
    private const float FlameRate = 26f;
    private const float EmberRate = 5f;

    private static readonly Color CoreEmission = new Color(9f, 3.4f, 0.8f, 1f);

    [MenuItem("FishDemo/World/④ 布置湖边火把与后处理")]
    public static void ApplyMenu() { Apply(); }

    public static void Apply() { Run("apply"); }
    public static void Remove() { Run("remove"); }
    public static void Report() { Run("report"); }

    private static void Run(string mode)
    {
        var sb = new StringBuilder();
        void W(string s) { Debug.Log($"{Tag} {s}"); sb.AppendLine(s); }

        try
        {
            Directory.CreateDirectory(OutDir);
            var scene = EditorSceneManager.OpenScene(WorldScenePath, OpenSceneMode.Single);
            W($"模式 = {mode}  场景 = {WorldScenePath}");

            if (mode == "remove")
            {
                RemoveRoots(scene, W);
                SaveWorld(scene);
                W("==== 结论：PASS ====");
                Flush(sb, 0);
                return;
            }

            if (mode == "report")
            {
                ReportState(scene, W);
                W("==== 结论：PASS ====");
                Flush(sb, 0);
                return;
            }

            var chunkPaths = new List<string>();
            foreach (var s in EditorBuildSettings.scenes)
            {
                if (!s.enabled) continue;
                if (s.path.EndsWith("World.unity")) continue;
                if (!File.Exists(s.path)) continue;
                chunkPaths.Add(s.path);
            }

            var (loaded, skipped) = ChunkLoadScope.LoadRelevant(ChunkLoadScope.Area.FromLake(), W);
            W($"chunk 场景：加载 {loaded} / 跳过 {skipped}");
            var terrains = UnityEngine.Object.FindObjectsOfType<Terrain>();
            W($"可用 Terrain = {terrains.Length}");

            var positions = SampleTorchPositions(terrains, W);
            CloseChunks(chunkPaths);
            EditorSceneManager.SetActiveScene(scene);

            EnsureFolder(LightingDir);
            EnsureFolder(PostProcessDir);

            var wood = EnsureMaterial(LightingDir + "/Torch Wood.mat", "Universal Render Pipeline/Lit", m =>
            {
                m.SetColor("_BaseColor", new Color(0.22f, 0.115f, 0.055f, 1f));
                m.SetFloat("_Metallic", 0f);
                m.SetFloat("_Smoothness", 0.12f);
            });
            var metal = EnsureMaterial(LightingDir + "/Torch Metal.mat", "Universal Render Pipeline/Lit", m =>
            {
                m.SetColor("_BaseColor", new Color(0.055f, 0.05f, 0.045f, 1f));
                m.SetFloat("_Metallic", 0.75f);
                m.SetFloat("_Smoothness", 0.45f);
            });
            var core = EnsureMaterial(LightingDir + "/Torch Flame Core.mat", "Universal Render Pipeline/Lit", m =>
            {
                m.SetColor("_BaseColor", new Color(0.02f, 0.012f, 0.006f, 1f));
                m.SetFloat("_Metallic", 0f);
                m.SetFloat("_Smoothness", 0.2f);
                m.SetColor("_EmissionColor", CoreEmission);
                m.EnableKeyword("_EMISSION");
                m.globalIlluminationFlags = MaterialGlobalIlluminationFlags.None;
            });
            var particles = EnsureParticleMaterial(LightingDir + "/Torch Particles.mat");

            var root = BuildTorches(scene, positions, wood, metal, core, particles, W);
            var controller = root.GetComponent<TorchRingController>();
            var compiled = root.GetComponentsInChildren<TorchLight>(true);
            controller.Bind(compiled, 1);
            EditorUtility.SetDirty(controller);
            W($"火把根节点 = {root.name}，火把数 = {compiled.Length}，默认开启 = 1（L 循环 0/1/2/4/6）");

            var profile = BuildProfile(W);
            var volume = BuildVolume(scene, profile, W);
            BindPostVolume(scene, volume, W);
            ConfigureCameras(scene, W);
            ConfigureColorGradingMode(W);

            SaveWorld(scene);
            AssetDatabase.SaveAssets();

            W($"Profile = {ProfilePath}");
            W("后处理：ACES Tonemapping / S 曲线 Color Grading / Bloom / Vignette / Film Grain（无 DOF）");
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

    private static List<Vector3> SampleTorchPositions(Terrain[] terrains, Action<string> W)
    {
        var result = new List<Vector3>(TorchCount);
        float cx = LakeCarve.Center.x;
        float cz = LakeCarve.Center.y;
        for (int i = 0; i < TorchCount; i++)
        {
            float angle = 360f * i / TorchCount;
            float rad = angle * Mathf.Deg2Rad;
            float dx = Mathf.Sin(rad);
            float dz = Mathf.Cos(rad);
            float bestRadius = LakeCarve.Radius + 1f;
            float bestHeight = float.MaxValue;
            for (float r = LakeCarve.Radius + 1f; r <= LakeCarve.Radius + 15f; r += 0.5f)
            {
                float h = SampleHeight(terrains, cx + dx * r, cz + dz * r);
                if (float.IsNaN(h)) continue;
                if (h < LakeCarve.WaterLevel + 0.2f) continue;
                if (h < bestHeight)
                {
                    bestHeight = h;
                    bestRadius = r;
                }
            }
            if (bestHeight == float.MaxValue)
            {
                bestRadius = LakeCarve.Radius + 1f;
                bestHeight = LakeCarve.WaterLevel + 0.5f;
            }

            var p = new Vector3(cx + dx * bestRadius, bestHeight, cz + dz * bestRadius);
            result.Add(p);
            W($"火把[{i:00}] angle={angle:F0} r={bestRadius:F1} " +
              $"pos=({p.x:F1},{p.y:F2},{p.z:F1}) 水位+{p.y - LakeCarve.WaterLevel:F2}m");
        }
        return result;
    }

    private static float SampleHeight(Terrain[] terrains, float x, float z)
    {
        for (int i = 0; i < terrains.Length; i++)
        {
            var t = terrains[i];
            if (t == null || t.terrainData == null) continue;
            var p = t.transform.position;
            var s = t.terrainData.size;
            if (x < p.x || x > p.x + s.x || z < p.z || z > p.z + s.z) continue;
            return p.y + t.SampleHeight(new Vector3(x, 0f, z));
        }
        return float.NaN;
    }

    private static void CloseChunks(List<string> chunkPaths)
    {
        foreach (var path in chunkPaths)
        {
            var s = SceneManager.GetSceneByPath(path);
            if (s.isLoaded) EditorSceneManager.CloseScene(s, true);
        }
    }

    private static GameObject BuildTorches(Scene scene, List<Vector3> positions, Material wood,
        Material metal, Material core, Material particles, Action<string> W)
    {
        var root = scene.GetRootGameObjects().FirstOrDefault(g => g.name == TorchRootName);
        if (root == null)
        {
            root = new GameObject(TorchRootName);
            SceneManager.MoveGameObjectToScene(root, scene);
        }
        for (int i = root.transform.childCount - 1; i >= 0; i--)
            UnityEngine.Object.DestroyImmediate(root.transform.GetChild(i).gameObject);

        var controller = root.GetComponent<TorchRingController>();
        if (controller == null) controller = root.AddComponent<TorchRingController>();

        var torches = new TorchLight[positions.Count];
        for (int i = 0; i < positions.Count; i++)
            torches[i] = BuildTorch(root.transform, i, positions[i], wood, metal, core, particles, W);

        return root;
    }

    private static TorchLight BuildTorch(Transform parent, int index, Vector3 pos, Material wood,
        Material metal, Material coreMat, Material particleMat, Action<string> W)
    {
        var go = new GameObject($"Torch {index:00}");
        go.transform.SetParent(parent, false);
        go.transform.position = pos;

        var pole = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        UnityEngine.Object.DestroyImmediate(pole.GetComponent<Collider>());
        pole.name = "Pole";
        pole.transform.SetParent(go.transform, false);
        pole.transform.localPosition = new Vector3(0f, 0.85f * TorchScale, 0f);
        pole.transform.localScale = new Vector3(0.06f * TorchScale, 0.85f * TorchScale, 0.06f * TorchScale);
        SetRenderer(pole.GetComponent<MeshRenderer>(), wood);

        var bowl = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        UnityEngine.Object.DestroyImmediate(bowl.GetComponent<Collider>());
        bowl.name = "Bowl";
        bowl.transform.SetParent(go.transform, false);
        bowl.transform.localPosition = new Vector3(0f, 1.62f * TorchScale, 0f);
        bowl.transform.localScale = new Vector3(0.2f * TorchScale, 0.06f * TorchScale, 0.2f * TorchScale);
        SetRenderer(bowl.GetComponent<MeshRenderer>(), metal);

        var core = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        UnityEngine.Object.DestroyImmediate(core.GetComponent<Collider>());
        core.name = "Flame Core";
        core.transform.SetParent(go.transform, false);
        core.transform.localPosition = new Vector3(0f, 1.78f * TorchScale, 0f);
        core.transform.localScale = new Vector3(0.22f * TorchScale, 0.34f * TorchScale, 0.22f * TorchScale);
        var coreRenderer = core.GetComponent<MeshRenderer>();
        SetRenderer(coreRenderer, coreMat);
        coreRenderer.enabled = false;

        var lightGo = new GameObject("Light");
        lightGo.transform.SetParent(go.transform, false);
        lightGo.transform.localPosition = new Vector3(0f, 1.85f * TorchScale, 0f);
        var light = lightGo.AddComponent<Light>();
        light.type = LightType.Point;
        light.color = new Color(1f, 0.55f, 0.22f, 1f);
        light.intensity = BaseIntensity;
        light.range = BaseRange * TorchScale;
        light.shadows = LightShadows.None;
        light.renderMode = LightRenderMode.Auto;
        light.enabled = false;

        var flame = BuildFlameParticles(go.transform, particleMat, true);
        var embers = BuildFlameParticles(go.transform, particleMat, false);

        var torch = go.AddComponent<TorchLight>();
        torch.Bind(light, flame, embers, coreRenderer, BaseIntensity, CoreEmission, FlameRate, EmberRate);
        return torch;
    }

    private static ParticleSystem BuildFlameParticles(Transform parent, Material material, bool flame)
    {
        var go = new GameObject(flame ? "Flame FX" : "Embers");
        go.transform.SetParent(parent, false);
        go.transform.localPosition = new Vector3(0f, (flame ? 1.86f : 1.9f) * TorchScale, 0f);

        var ps = go.AddComponent<ParticleSystem>();
        var main = ps.main;
        main.duration = 1f;
        main.loop = true;
        main.playOnAwake = false;
        main.simulationSpace = ParticleSystemSimulationSpace.World;
        main.scalingMode = ParticleSystemScalingMode.Hierarchy;
        main.maxParticles = flame ? 48 : 24;
        main.startLifetime = flame
            ? new ParticleSystem.MinMaxCurve(0.35f, 0.55f)
            : new ParticleSystem.MinMaxCurve(1.2f, 2.4f);
        main.startSpeed = flame
            ? new ParticleSystem.MinMaxCurve(0.6f * TorchScale, 1.1f * TorchScale)
            : new ParticleSystem.MinMaxCurve(1.2f * TorchScale, 2.2f * TorchScale);
        main.startSize = flame
            ? new ParticleSystem.MinMaxCurve(0.32f * TorchScale, 0.55f * TorchScale)
            : new ParticleSystem.MinMaxCurve(0.025f * TorchScale, 0.06f * TorchScale);
        main.startRotation = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);
        main.gravityModifier = flame ? -0.03f : -0.005f;
        main.startColor = new ParticleSystem.MinMaxGradient(Color.white);

        var emission = ps.emission;
        emission.rateOverTimeMultiplier = 0f;

        var shape = ps.shape;
        shape.enabled = true;
        shape.shapeType = ParticleSystemShapeType.Cone;
        shape.angle = flame ? 14f : 26f;
        shape.radius = (flame ? 0.16f : 0.1f) * TorchScale;
        shape.rotation = new Vector3(-90f, 0f, 0f);
        shape.randomDirectionAmount = 0.3f;

        var color = ps.colorOverLifetime;
        color.enabled = true;
        color.color = new ParticleSystem.MinMaxGradient(BuildColorGradient(flame));

        var size = ps.sizeOverLifetime;
        size.enabled = true;
        size.size = new ParticleSystem.MinMaxCurve(1f, flame
            ? new AnimationCurve(
                new Keyframe(0f, 0.55f), new Keyframe(0.25f, 1f),
                new Keyframe(0.7f, 0.85f), new Keyframe(1f, 0.15f))
            : new AnimationCurve(
                new Keyframe(0f, 1f), new Keyframe(1f, 0.55f)));

        var rotation = ps.rotationOverLifetime;
        rotation.enabled = true;
        rotation.z = new ParticleSystem.MinMaxCurve(-0.5f, 0.5f);

        var renderer = ps.GetComponent<ParticleSystemRenderer>();
        renderer.sharedMaterial = material;
        renderer.renderMode = ParticleSystemRenderMode.Billboard;
        renderer.alignment = ParticleSystemRenderSpace.View;
        renderer.shadowCastingMode = ShadowCastingMode.Off;
        renderer.receiveShadows = false;

        ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
        return ps;
    }

    private static Gradient BuildColorGradient(bool flame)
    {
        var g = new Gradient();
        if (flame)
        {
            g.SetKeys(
                new[]
                {
                    new GradientColorKey(new Color(1f, 0.92f, 0.55f), 0f),
                    new GradientColorKey(new Color(1f, 0.55f, 0.12f), 0.35f),
                    new GradientColorKey(new Color(0.55f, 0.08f, 0.02f), 1f),
                },
                new[]
                {
                    new GradientAlphaKey(0f, 0f),
                    new GradientAlphaKey(1f, 0.12f),
                    new GradientAlphaKey(0.65f, 0.6f),
                    new GradientAlphaKey(0f, 1f),
                });
        }
        else
        {
            g.SetKeys(
                new[]
                {
                    new GradientColorKey(new Color(1f, 0.55f, 0.12f), 0f),
                    new GradientColorKey(new Color(1f, 0.3f, 0.05f), 1f),
                },
                new[]
                {
                    new GradientAlphaKey(0f, 0f),
                    new GradientAlphaKey(1f, 0.1f),
                    new GradientAlphaKey(0f, 1f),
                });
        }
        return g;
    }

    private static void SetRenderer(MeshRenderer r, Material m)
    {
        r.sharedMaterial = m;
        r.shadowCastingMode = ShadowCastingMode.Off;
        r.receiveShadows = false;
    }

    private static VolumeProfile BuildProfile(Action<string> W)
    {
        var profile = AssetDatabase.LoadAssetAtPath<VolumeProfile>(ProfilePath);
        if (profile == null)
        {
            profile = VolumeProfileFactory.CreateVolumeProfileAtPath(ProfilePath);
            W($"已创建 Volume Profile：{ProfilePath}");
        }
        else
        {
            W($"复用 Volume Profile：{ProfilePath}");
        }

        var tonemap = GetOrAdd<Tonemapping>(profile);
        tonemap.mode.overrideState = true;
        tonemap.mode.value = TonemappingMode.ACES;

        var color = GetOrAdd<ColorAdjustments>(profile);
        color.postExposure.overrideState = true;
        color.postExposure.value = 0.35f;
        color.contrast.overrideState = true;
        color.contrast.value = 10f;
        color.saturation.overrideState = true;
        color.saturation.value = -8f;
        color.colorFilter.overrideState = true;
        color.colorFilter.value = new Color(1f, 0.99f, 0.96f, 1f);

        var curves = GetOrAdd<ColorCurves>(profile);
        curves.master.overrideState = true;
        curves.master.value = new TextureCurve(
            new[]
            {
                new Keyframe(0f, 0f),
                new Keyframe(0.25f, 0.24f),
                new Keyframe(0.5f, 0.54f),
                new Keyframe(0.75f, 0.8f),
                new Keyframe(1f, 1f),
            }, 0f, false, new Vector2(0f, 1f));

        curves.hueVsHue.overrideState = true;
        curves.hueVsHue.value = new TextureCurve(
            new[]
            {
                new Keyframe(0f, 0.5f),
                new Keyframe(0.03f, 0.492f),
                new Keyframe(0.06f, 0.478f),
                new Keyframe(0.09f, 0.472f),
                new Keyframe(0.12f, 0.482f),
                new Keyframe(0.16f, 0.5f),
                new Keyframe(0.33f, 0.5f),
                new Keyframe(0.55f, 0.5f),
                new Keyframe(0.7f, 0.5f),
                new Keyframe(0.88f, 0.5f),
                new Keyframe(1f, 0.5f),
            }, 0.5f, true, new Vector2(0f, 1f));

        curves.hueVsSat.overrideState = true;
        curves.hueVsSat.value = new TextureCurve(
            new[]
            {
                new Keyframe(0f, 0.52f),
                new Keyframe(0.04f, 0.545f),
                new Keyframe(0.08f, 0.56f),
                new Keyframe(0.12f, 0.54f),
                new Keyframe(0.16f, 0.5f),
                new Keyframe(0.33f, 0.5f),
                new Keyframe(0.55f, 0.5f),
                new Keyframe(0.7f, 0.5f),
                new Keyframe(0.88f, 0.5f),
                new Keyframe(1f, 0.52f),
            }, 0.5f, true, new Vector2(0f, 1f));

        var wb = GetOrAdd<WhiteBalance>(profile);
        wb.temperature.overrideState = true;
        wb.temperature.value = 10f;
        wb.tint.overrideState = true;
        wb.tint.value = -3f;

        var smh = GetOrAdd<ShadowsMidtonesHighlights>(profile);
        smh.shadows.overrideState = true;
        smh.shadows.value = new Vector4(0.85f, 0.95f, 1.12f, 0f);
        smh.midtones.overrideState = true;
        smh.midtones.value = new Vector4(1f, 1f, 1f, 0f);
        smh.highlights.overrideState = true;
        smh.highlights.value = new Vector4(1.12f, 1f, 0.85f, 0f);

        var bloom = GetOrAdd<Bloom>(profile);
        bloom.threshold.overrideState = true;
        bloom.threshold.value = 0.85f;
        bloom.intensity.overrideState = true;
        bloom.intensity.value = 0.4f;
        bloom.scatter.overrideState = true;
        bloom.scatter.value = 0.65f;
        bloom.tint.overrideState = true;
        bloom.tint.value = new Color(1f, 0.9f, 0.78f, 1f);
        bloom.highQualityFiltering.overrideState = true;
        bloom.highQualityFiltering.value = false;
        bloom.downscale.overrideState = true;
        bloom.downscale.value = BloomDownscaleMode.Half;
        bloom.maxIterations.overrideState = true;
        bloom.maxIterations.value = 6;

        if (profile.TryGet<DepthOfField>(out var dof))
        {
            profile.Remove<DepthOfField>();
            if (AssetDatabase.IsSubAsset(dof))
                AssetDatabase.RemoveObjectFromAsset(dof);
            UnityEngine.Object.DestroyImmediate(dof, true);
            EditorUtility.SetDirty(profile);
            W("Depth of Field 已移除（按需求不做 DOF）");
        }

        var vignette = GetOrAdd<Vignette>(profile);
        vignette.intensity.overrideState = true;
        vignette.intensity.value = 0.22f;
        vignette.smoothness.overrideState = true;
        vignette.smoothness.value = 0.45f;

        var grain = GetOrAdd<FilmGrain>(profile);
        grain.type.overrideState = true;
        grain.type.value = FilmGrainLookup.Medium1;
        grain.intensity.overrideState = true;
        grain.intensity.value = 0.12f;
        grain.response.overrideState = true;
        grain.response.value = 0.8f;

        EditorUtility.SetDirty(profile);
        W("Volume Profile：ACES / S 曲线 / 橙红 Hue vs Hue+Sat 近似 GOT / 对比+15 / 饱和-10 / 暖高光冷暗部 / Bloom 0.4 / Vignette 0.22 / Grain 0.12 / 无 DOF");
        return profile;
    }

    private static T GetOrAdd<T>(VolumeProfile profile) where T : VolumeComponent
    {
        if (profile.TryGet<T>(out var existing)) return existing;
        return VolumeProfileFactory.CreateVolumeComponent<T>(profile, true, false);
    }

    private static Volume BuildVolume(Scene scene, VolumeProfile profile, Action<string> W)
    {
        var go = scene.GetRootGameObjects().FirstOrDefault(g => g.name == VolumeRootName);
        if (go == null)
        {
            go = new GameObject(VolumeRootName);
            SceneManager.MoveGameObjectToScene(go, scene);
        }
        var volume = go.GetComponent<Volume>();
        if (volume == null) volume = go.AddComponent<Volume>();
        volume.isGlobal = true;
        volume.priority = 0f;
        volume.weight = 1f;
        volume.sharedProfile = profile;
        EditorUtility.SetDirty(volume);
        W($"全局 Volume = {VolumeRootName}（profile={profile.name}）");
        return volume;
    }

    private static void BindPostVolume(Scene scene, Volume volume, Action<string> W)
    {
        var root = scene.GetRootGameObjects().FirstOrDefault(g => g.name == "Atmosphere");
        var ctrl = root != null ? root.GetComponent<AtmosphereController>() : null;
        if (ctrl == null)
            throw new InvalidOperationException("场景里没有 AtmosphereController；先跑 AtmosphereSetup.Apply");
        var so = new SerializedObject(ctrl);
        so.FindProperty("_postProcessVolume").objectReferenceValue = volume;
        so.FindProperty("_nightExposureEv").floatValue = 1.8f;
        so.FindProperty("_nightContrast").floatValue = 8f;
        so.FindProperty("_nightSaturation").floatValue = -18f;
        so.FindProperty("_nightColorFilter").colorValue = new Color(0.78f, 0.88f, 1.08f, 1f);
        so.ApplyModifiedPropertiesWithoutUndo();
        EditorUtility.SetDirty(ctrl);
        W("AtmosphereController 已绑定全局后处理 Volume（夜景曝光 +1.8EV，冷色/降饱和）");
    }

    private static void ConfigureCameras(Scene scene, Action<string> W)
    {
        int count = 0;
        foreach (var go in scene.GetRootGameObjects())
        {
            foreach (var cam in go.GetComponentsInChildren<Camera>(true))
            {
                var data = cam.GetComponent<UniversalAdditionalCameraData>();
                if (data == null) data = cam.gameObject.AddComponent<UniversalAdditionalCameraData>();
                data.renderPostProcessing = true;
                data.dithering = true;
                data.renderShadows = true;
                data.volumeLayerMask = 1;
                EditorUtility.SetDirty(data);
                count++;
                W($"相机 {cam.name}：postProcessing=on，dithering=on，volumeLayerMask=Default");
            }
        }
        W($"已配置相机数 = {count}");
    }

    private static void ConfigureColorGradingMode(Action<string> W)
    {
        const string urpPath = "Assets/Settings/UniversalRP.asset";
        var urp = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(urpPath);
        if (urp == null) throw new InvalidOperationException($"找不到 URP 资产 {urpPath}");
        var so = new SerializedObject(urp);
        var prop = so.FindProperty("m_ColorGradingMode");
        int before = prop.intValue;
        prop.intValue = 1;
        so.ApplyModifiedPropertiesWithoutUndo();
        EditorUtility.SetDirty(urp);
        AssetDatabase.SaveAssets();
        W($"Color Grading 模式：{before} → 1（HDR / LogC LUT；ACES 保留暗部）");
    }

    private static void RemoveRoots(Scene scene, Action<string> W)
    {
        foreach (var name in new[] { TorchRootName, VolumeRootName })
        {
            var go = scene.GetRootGameObjects().FirstOrDefault(g => g.name == name);
            if (go == null) continue;
            UnityEngine.Object.DestroyImmediate(go);
            W($"已删除「{name}」");
        }

        foreach (var go in scene.GetRootGameObjects())
        {
            foreach (var cam in go.GetComponentsInChildren<Camera>(true))
            {
                var data = cam.GetComponent<UniversalAdditionalCameraData>();
                if (data != null)
                {
                    UnityEngine.Object.DestroyImmediate(data);
                    W($"已移除 {cam.name} 的 UniversalAdditionalCameraData");
                }
            }
        }
    }

    private static void ReportState(Scene scene, Action<string> W)
    {
        var torchRoot = scene.GetRootGameObjects().FirstOrDefault(g => g.name == TorchRootName);
        var torches = torchRoot != null ? torchRoot.GetComponentsInChildren<TorchLight>(true) : new TorchLight[0];
        W($"火把根节点 = {(torchRoot != null ? torchRoot.name : "缺失")}，火把数 = {torches.Length}");
        var controller = torchRoot != null ? torchRoot.GetComponent<TorchRingController>() : null;
        W($"TorchRingController = {(controller != null ? "存在" : "缺失")}" +
          (controller != null ? $"，默认开启 = {controller.MaxActiveLights}，上限 = {TorchRingController.HardLightLimit}" : ""));

        var volume = scene.GetRootGameObjects().FirstOrDefault(g => g.name == VolumeRootName);
        var v = volume != null ? volume.GetComponent<Volume>() : null;
        W($"全局 Volume = {(v != null ? "存在" : "缺失")}，profile = {(v != null && v.sharedProfile != null ? v.sharedProfile.name : "null")}");
        var profile = v != null ? v.sharedProfile : AssetDatabase.LoadAssetAtPath<VolumeProfile>(ProfilePath);
        if (profile != null)
        {
            if (profile.TryGet<Tonemapping>(out var t))
                W($"Tonemapping = {t.mode.value}");
            if (profile.TryGet<Bloom>(out var b))
                W($"Bloom = threshold {b.threshold.value} / intensity {b.intensity.value}");
            if (profile.TryGet<DepthOfField>(out var d))
                W($"DOF = {d.mode.value} / {d.gaussianStart.value}-{d.gaussianEnd.value}m");
            if (profile.TryGet<ColorAdjustments>(out var c))
                W($"ColorAdjustments = exposure {c.postExposure.value} / contrast {c.contrast.value} / saturation {c.saturation.value}");
        }
        else
        {
            W("FAIL：Volume Profile 不存在");
        }

        foreach (var go in scene.GetRootGameObjects())
        {
            foreach (var cam in go.GetComponentsInChildren<Camera>(true))
            {
                var data = cam.GetComponent<UniversalAdditionalCameraData>();
                W($"相机 {cam.name}：UAD={(data != null ? "有" : "无")}，" +
                  $"postProcessing={(data != null && data.renderPostProcessing)}，" +
                  $"dithering={(data != null && data.dithering)}");
            }
        }

        var atmo = scene.GetRootGameObjects().FirstOrDefault(g => g.name == "Atmosphere");
        var ac = atmo != null ? atmo.GetComponent<AtmosphereController>() : null;
        if (ac != null)
        {
            var so = new SerializedObject(ac);
            var prop = so.FindProperty("_postProcessVolume");
            W($"AtmosphereController 夜景曝光 Volume = " +
              $"{(prop != null && prop.objectReferenceValue != null ? prop.objectReferenceValue.name : "未绑定")}");
        }
        else
        {
            W("AtmosphereController = 缺失");
        }
    }

    private static Material EnsureMaterial(string path, string shaderName, Action<Material> configure)
    {
        var shader = Shader.Find(shaderName);
        if (shader == null) throw new InvalidOperationException($"找不到 shader {shaderName}");
        var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (mat == null)
        {
            mat = new Material(shader);
            AssetDatabase.CreateAsset(mat, path);
        }
        else if (mat.shader != shader)
        {
            mat.shader = shader;
        }
        configure(mat);
        EditorUtility.SetDirty(mat);
        return mat;
    }

    private static Material EnsureParticleMaterial(string path)
    {
        var shader = Shader.Find("Universal Render Pipeline/Particles/Unlit");
        if (shader == null) throw new InvalidOperationException("找不到 URP Particles/Unlit shader");
        var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (mat == null)
        {
            mat = new Material(shader);
            AssetDatabase.CreateAsset(mat, path);
        }
        else if (mat.shader != shader)
        {
            mat.shader = shader;
        }

        var tex = AssetDatabase.GetBuiltinExtraResource<Texture2D>("Default-Particle.psd");
        mat.SetTexture("_BaseMap", tex);
        mat.SetColor("_BaseColor", Color.white);
        mat.SetFloat("_Surface", 1f);
        mat.SetFloat("_Blend", 2f);
        mat.SetFloat("_BlendOp", 0f);
        mat.SetFloat("_SrcBlend", (float)BlendMode.One);
        mat.SetFloat("_DstBlend", (float)BlendMode.One);
        mat.SetFloat("_SrcBlendAlpha", (float)BlendMode.One);
        mat.SetFloat("_DstBlendAlpha", (float)BlendMode.Zero);
        mat.SetFloat("_ZWrite", 0f);
        mat.SetFloat("_Cull", (float)CullMode.Off);
        mat.SetFloat("_AlphaClip", 0f);
        mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
        mat.SetOverrideTag("RenderType", "Transparent");
        mat.renderQueue = (int)RenderQueue.Transparent;
        EditorUtility.SetDirty(mat);
        return mat;
    }

    private static void EnsureFolder(string path)
    {
        if (AssetDatabase.IsValidFolder(path)) return;
        var parent = Path.GetDirectoryName(path)?.Replace('\\', '/');
        var name = Path.GetFileName(path);
        if (string.IsNullOrEmpty(parent) || !AssetDatabase.IsValidFolder(parent))
            throw new InvalidOperationException($"无法创建目录 {path}");
        AssetDatabase.CreateFolder(parent, name);
    }

    private static void SaveWorld(Scene scene)
    {
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        AssetDatabase.SaveAssets();
    }

    private static void Flush(StringBuilder sb, int code)
    {
        Directory.CreateDirectory(OutDir);
        File.WriteAllText(ResultPath, sb.ToString());
        if (Application.isBatchMode) EditorApplication.Exit(code);
    }
}
