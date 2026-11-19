using System;
using System.IO;
using System.Text;
using FishDemo.Gameplay;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering.Universal;
using WaveHarmonic.Crest;

public static class PlanarReflectionSetup
{
    private const string Tag = "[PlanarReflection]";
    private const string WorldScenePath = "Assets/Scenes/World.unity";
    private const string OutDir = "Logs/Lake";
    private const string ResultPath = OutDir + "/planar-reflection.txt";
    private const string WaterMatPath = "Packages/com.waveharmonic.crest/Runtime/Materials/Water.mat";
    private const string ReflectionRendererPath = "Assets/Settings/ReflectionRenderer.asset";
    private const string GrassLayerName = "Grass";
    private const int Width = 512;
    private const int Height = 256;

    public static void Apply() { Run(true); }
    public static void Report() { Run(false); }

    public static void Configure(WaterRenderer water, Action<string> W)
    {
        var reflections = water.Reflections;
        reflections.Enabled = true;
        reflections.ReflectionSide = WaterReflectionSide.Above;
        reflections.Layers = 1;
        reflections.Resolution = Width;
        reflections.ResolutionY = Height;
        reflections.Overscan = 1.25f;
        reflections.Sky = true;
        reflections.DisablePixelLights = true;
        reflections.DisableShadows = true;
        reflections.UseObliqueMatrix = true;
        reflections.NonObliqueNearSurface = false;
        reflections.ClipPlaneOffset = 0f;
        reflections.DisableOcclusionCulling = true;
        reflections.RenderOnlySingleCamera = true;
        reflections.HDR = true;
        reflections.Stencil = false;
        reflections.RendererIndex = 1;
        reflections.CaptureDepth = false;

        var material = water.Surface.Material;
        if (material == null)
            material = AssetDatabase.LoadAssetAtPath<Material>(WaterMatPath);

        if (material != null)
        {
            material.SetFloat("_Crest_PlanarReflectionsEnabled", 1f);
            material.SetFloat("_Crest_PlanarReflectionsIntensity", 1f);
            material.SetFloat("_Crest_PlanarReflectionsRoughness", 1f);
            material.SetFloat("_Crest_PlanarReflectionsDistortion", 1f);
            material.SetFloat("_Crest_PlanarReflectionsApplySmoothness", 0f);
            EditorUtility.SetDirty(material);
        }

        var controller = water.GetComponent<PlanarReflectionController>();
        if (controller == null)
            controller = water.gameObject.AddComponent<PlanarReflectionController>();

        controller.Bind(water);
        EditorUtility.SetDirty(controller);

        W($"平面反射 = ON：Above / {Width}x{Height} / Default 层 / Overscan={reflections.Overscan} / " +
          $"disableShadows={reflections.DisableShadows} disablePixelLights={reflections.DisablePixelLights}");
    }

    private static void Run(bool apply)
    {
        var sb = new StringBuilder();
        void W(string s) { Debug.Log($"{Tag} {s}"); sb.AppendLine(s); }

        try
        {
            Directory.CreateDirectory(OutDir);
            var scene = EditorSceneManager.OpenScene(WorldScenePath, OpenSceneMode.Single);
            W($"模式 = {(apply ? "apply" : "report")}  场景 = {WorldScenePath}");

            var water = UnityEngine.Object.FindObjectOfType<WaterRenderer>();
            if (water == null)
            {
                W("FAIL：找不到 WaterRenderer");
                Flush(sb, 2);
                return;
            }

            if (apply)
            {
                Configure(water, W);
                EditorSceneManager.MarkSceneDirty(scene);
                EditorSceneManager.SaveScene(scene);
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
            }

            bool ok = Verify(water, W);
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

    private static bool Verify(WaterRenderer water, Action<string> W)
    {
        bool ok = true;
        var r = water.Reflections;

        W($"Enabled={r.Enabled}（期望 True）");
        if (!r.Enabled) ok = false;

        W($"Side={r.ReflectionSide}（期望 Above）");
        if (r.ReflectionSide != WaterReflectionSide.Above) ok = false;

        W($"Layers={r.Layers.value}（期望 1 = Default）");
        if (r.Layers.value != 1) ok = false;

        W($"Resolution={r.Resolution}x{r.ResolutionY}（期望 {Width}x{Height}）");
        if (r.Resolution != Width || r.ResolutionY != Height) ok = false;

        W($"Overscan={r.Overscan:F2}（期望 1.25）, Sky={r.Sky}, " +
          $"DisableShadows={r.DisableShadows}, DisablePixelLights={r.DisablePixelLights}, " +
          $"Oblique={r.UseObliqueMatrix}, SingleCamera={r.RenderOnlySingleCamera}, " +
          $"RendererIndex={r.RendererIndex}（期望 1）, CaptureDepth={r.CaptureDepth}（期望 False）");
        if (!Mathf.Approximately(r.Overscan, 1.25f) ||
            !r.Sky ||
            !r.DisableShadows ||
            !r.DisablePixelLights ||
            !r.UseObliqueMatrix ||
            !r.RenderOnlySingleCamera ||
            r.RendererIndex != 1 ||
            r.CaptureDepth) ok = false;

        var reflectionRenderer = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(ReflectionRendererPath);
        W($"ReflectionRenderer: {(reflectionRenderer != null)}, " +
          $"features={reflectionRenderer?.rendererFeatures.Count}, " +
          $"intermediate={reflectionRenderer?.intermediateTextureMode}");
        if (reflectionRenderer == null ||
            reflectionRenderer.rendererFeatures.Count != 0 ||
            reflectionRenderer.intermediateTextureMode != IntermediateTextureMode.Auto) ok = false;

        int grassLayer = LayerMask.NameToLayer(GrassLayerName);
        W($"{GrassLayerName} layer = {grassLayer}（期望 6）");
        if (grassLayer < 0 || (r.Layers.value & (1 << grassLayer)) != 0) ok = false;

        var controller = water.GetComponent<PlanarReflectionController>();
        W($"PlanarReflectionController = {(controller != null)}");
        if (controller == null)
        {
            ok = false;
        }
        else
        {
            controller.RebuildBounds();
            var bounds = controller.WaterBounds;
            W($"水体可见性 Bounds center={bounds.center} size={bounds.size}");

            var expectedCenter = new Vector3(LakeCarve.Center.x, LakeCarve.WaterLevel, LakeCarve.Center.y);
            if (Vector3.Distance(bounds.center, expectedCenter) > 1f) ok = false;
            if (bounds.size.x < LakeCarve.WaterHalfExtent * 2f - 1f ||
                bounds.size.z < LakeCarve.WaterHalfExtent * 2f - 1f) ok = false;

            ok &= VerifyVisibility(controller, expectedCenter, W);
        }

        var material = water.Surface.Material;
        if (material == null)
        {
            W("FAIL：Surface.Material 为空");
            ok = false;
        }
        else
        {
            bool hasProperty = material.HasProperty("_Crest_PlanarReflectionsEnabled");
            float enabled = hasProperty ? material.GetFloat("_Crest_PlanarReflectionsEnabled") : -1f;
            W($"Water.mat _Crest_PlanarReflectionsEnabled={enabled}（期望 1）, hasProperty={hasProperty}");
            if (!hasProperty || enabled < 0.5f) ok = false;
        }

        return ok;
    }

    private static bool VerifyVisibility(PlanarReflectionController controller, Vector3 center, Action<string> W)
    {
        bool ok = true;
        var go = new GameObject("PlanarReflectionVerifyCamera");
        try
        {
            var camera = go.AddComponent<Camera>();
            camera.fieldOfView = 60f;
            camera.aspect = 16f / 9f;
            camera.nearClipPlane = 0.3f;
            camera.farClipPlane = 1000f;

            go.transform.position = center + new Vector3(0f, 100f, -400f);
            go.transform.LookAt(center);
            bool facing = controller.IsVisible(camera);

            go.transform.rotation = Quaternion.Euler(0f, 180f, 0f);
            bool away = controller.IsVisible(camera);

            go.transform.position = center + new Vector3(0f, -10f, -400f);
            go.transform.LookAt(center);
            bool underwater = controller.IsVisible(camera);

            W($"可见性：朝向水面={facing}（期望 True），背对={away}（期望 False），水下={underwater}（期望 False）");
            if (!facing || away || underwater) ok = false;
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(go);
        }

        return ok;
    }

    private static void Flush(StringBuilder sb, int exitCode)
    {
        File.WriteAllText(ResultPath, sb.ToString());
        if (Application.isBatchMode)
            EditorApplication.Exit(exitCode);
    }
}
