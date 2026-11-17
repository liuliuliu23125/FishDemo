using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using WaveHarmonic.Crest;

public static class LakeCrestSetup
{
    private const string Tag = "[LakeCrest]";
    private const string WorldScenePath = "Assets/Scenes/World.unity";
    private const string OutDir = "Logs/Lake";
    private const string ResultPath = OutDir + "/lake-crest.txt";

    private const string WaterMatPath = "Packages/com.waveharmonic.crest/Runtime/Materials/Water.mat";
    private const string VolumeMatPath = "Packages/com.waveharmonic.crest/Runtime/Materials/Water Volume.mat";

    private const string NodeName = "Crest Lake";

    private const float ViewpointHeightAboveWater = 26f;

    private const float ClipHalfExtent = 320f;

    public static void Apply() { Run("apply"); }
    public static void Remove() { Run("remove"); }
    public static void Report() { Run("report"); }

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
                if (old != null)
                {
                    for (var i = old.transform.childCount - 1; i >= 0; i--)
                    {
                        UnityEngine.Object.DestroyImmediate(old.transform.GetChild(i).gameObject);
                    }

                    var oldWater = old.GetComponent<WaterRenderer>();
                    if (oldWater != null) UnityEngine.Object.DestroyImmediate(oldWater);

                    if (old.GetComponents<Component>().Length <= 1)
                    {
                        UnityEngine.Object.DestroyImmediate(old);
                    }
                }

                EditorSceneManager.MarkSceneDirty(scene);
                EditorSceneManager.SaveScene(scene);
                AssetDatabase.SaveAssets();
                W($"已移除 {NodeName} 的水面数据（保留可能存在的 LakeCameraRig）");
                W("==== 结论：PASS ====");
                Flush(sb, 0);
                return;
            }

            var root = GameObject.Find(NodeName);
            if (root == null)
            {
                root = new GameObject(NodeName);
            }
            else
            {
                for (var i = root.transform.childCount - 1; i >= 0; i--)
                {
                    UnityEngine.Object.DestroyImmediate(root.transform.GetChild(i).gameObject);
                }

                var staleWater = root.GetComponent<WaterRenderer>();
                if (staleWater != null) UnityEngine.Object.DestroyImmediate(staleWater);
            }

            var waterMat = AssetDatabase.LoadAssetAtPath<Material>(WaterMatPath);
            var volumeMat = AssetDatabase.LoadAssetAtPath<Material>(VolumeMatPath);
            if (waterMat == null) { W($"FAIL：找不到水面材质 {WaterMatPath}"); Flush(sb, 2); return; }
            W($"水面材质 = {waterMat.name}（shader={waterMat.shader?.name}）");

            root.transform.position = new Vector3(LakeCarve.Center.x, LakeCarve.WaterLevel, LakeCarve.Center.y);
            W($"水面节点位置 = {root.transform.position}");

            var viewpointGo = new GameObject("Lake Viewpoint");
            viewpointGo.transform.SetParent(root.transform, false);
            viewpointGo.transform.localPosition = new Vector3(0f, ViewpointHeightAboveWater, 0f);

            var water = root.AddComponent<WaterRenderer>();
            water.Surface.Material = waterMat;
            water.Surface.VolumeMaterial = volumeMat;
            water.Viewpoint = viewpointGo.transform;
            W($"LOD 视点 = {viewpointGo.name} @ {viewpointGo.transform.position}（固定；Viewer 仍跟相机）");
            CrestPerformanceMode.SetMeshType(water, 1, W);

            ApplyWaterColor(waterMat, volumeMat, W);

            water.Viewer = Camera.main;
            water.PrimaryLight = UnityEngine.Object.FindObjectsOfType<Light>()
                .FirstOrDefault(l => l.type == LightType.Directional);

            water.LodLevels = 5;
            water.LodResolution = 128;
            water.ScaleRange = new Vector2(1f, 16f);

            water.AnimatedWavesLod.OverrideResolution = false;
            water.LevelLod.OverrideResolution = false;
            water.DepthLod.OverrideResolution = true;
            water.DepthLod.Resolution = 128;
            water.FoamLod.OverrideResolution = true;
            water.FoamLod.Resolution = 128;
            water.ClipLod.OverrideResolution = true;
            water.ClipLod.Resolution = 128;

            water.DepthLod.EnableSignedDistanceFields = false;

            W($"LodLevels={water.LodLevels}, LodResolution={water.LodResolution}, " +
              $"Depth={water.DepthLod.Resolution}, Foam={water.FoamLod.Resolution}, " +
              $"Clip={water.ClipLod.Resolution}, ScaleRange={water.ScaleRange}");

            water.AbsorptionLod.Enabled = false;
            water.ScatteringLod.Enabled = false;
            water.AlbedoLod.Enabled = false;

            water.DynamicWavesLod.Enabled = false;
            water.FlowLod.Enabled = false;
            water.ShadowLod.Enabled = false;
            water.ClipLod.Enabled = true;
            water.FoamLod.Enabled = true;
            water.FoamLod.SimulationFrequency = 30;
            water.DepthLod.Enabled = true;
            water.AnimatedWavesLod.Enabled = true;
            water.LevelLod.Enabled = true;
            W("模块：AnimatedWaves/Foam/Depth/Level/Clip = ON；" +
              "Absorption/Scattering/Albedo/DynamicWaves/Flow/Shadow = OFF（水色用材质恒定值）");

            water.Reflections.Enabled = false;
            W("平面反射 = OFF");

            water.Underwater.Enabled = true;

            water.ClipLod.DefaultClippingState = DefaultClippingState.EverythingClipped;
            W("ClipLod.DefaultClippingState = EverythingClipped");

            var clipGo = new GameObject("Lake Clip Area");
            clipGo.transform.SetParent(root.transform, false);
            clipGo.transform.localPosition = Vector3.zero;
            clipGo.transform.localScale = new Vector3(ClipHalfExtent * 2f,
                                                      DepthForClip(water), ClipHalfExtent * 2f);
            var clip = clipGo.AddComponent<ClipLodInput>();
            clip.Primitive = LodInputPrimitive.Cube;
            clip.Inverted = true;
            W($"ClipLodInput: Cube, Inverted=true（盒内保留水）, 尺寸 {clipGo.transform.localScale}");

            var bodyGo = new GameObject("Lake Body");
            bodyGo.transform.SetParent(root.transform, false);
            bodyGo.transform.localPosition = Vector3.zero;
            float ext = LakeCarve.WaterHalfExtent;
            bodyGo.transform.localScale = new Vector3(ext * 2f, 1f, ext * 2f);

            var body = bodyGo.AddComponent<WaterBody>();
            body.Exclusion = WaterBodyExclusion.Include;
            body.Precise = true;
            body.Affects = WaterBodyAffects.Volume | WaterBodyAffects.Physics;
            W($"WaterBody: AABB 半边长 = {ext}m，Exclusion=Include，Affects=Volume|Physics");

            var shapeGo = new GameObject("Lake Waves");
            shapeGo.transform.SetParent(root.transform, false);
            var shape = shapeGo.AddComponent<ShapeGerstner>();
            shape.Resolution = 96;
            var spectrum = AssetDatabase.LoadAssetAtPath<WaveSpectrum>(
                "Packages/com.waveharmonic.crest/Runtime/Data/WaveSpectra/WavesModerate.asset");
            if (spectrum != null)
            {
                shape.Spectrum = spectrum;
                W($"波浪频谱 = {spectrum.name}, Resolution={shape.Resolution}");
            }
            water.WindSpeed = 20f;
            W($"风速 = {water.WindSpeed} km/h，ShapeGerstner 已挂");

            if (CrestPerformanceMode.IsSimpleTransparencyEnabled())
            {
                if (CrestPerformanceMode.HasOtherDepthOrOpaqueConsumers(W))
                {
                    W("⚠ 检测到除 Crest 外还有 shader 依赖 Depth/Opaque，保持 URP 资产不变");
                }
                else
                {
                    CrestPerformanceMode.SetUrpTextures(requireDepth: false, requireOpaque: false, W);
                }
                CrestPerformanceMode.SetTransparencyMinAlpha(
                    CrestPerformanceMode.SimpleTransparencyMinAlpha, W);
                W("SimpleTransparency=ON：URP Depth/Opaque=0，水面 MeshType=Quad");
            }
            else
            {
                EnableUrpDepthAndOpaque(W);
            }

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

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

        if (volumeMat != null && !Mathf.Approximately(volumeMat.GetFloat("_Crest_ExtinctionMultiplier"), 1f))
        {
            volumeMat.SetFloat("_Crest_ExtinctionMultiplier", 1f);
            EditorUtility.SetDirty(volumeMat);
        }

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

    private static float DepthForClip(WaterRenderer water) => 200f;

    private static bool Verify(Action<string> W)
    {
        bool ok = true;

        var root = GameObject.Find(NodeName);
        if (root == null) { W("  FAIL：找不到 Crest 水面节点"); return false; }

        var water = root.GetComponent<WaterRenderer>();
        if (water == null) { W("  FAIL：节点上没有 WaterRenderer"); return false; }
        float y = root.transform.position.y;
        W($"  WaterRenderer OK, transform.y={y:F1}（期望 {LakeCarve.WaterLevel:F1}）");
        if (Mathf.Abs(y - LakeCarve.WaterLevel) > 0.01f) ok = false;

        if (water.Surface.Material == null) { W("  FAIL：Surface.Material 为空"); ok = false; }
        else W($"  Surface.Material = {water.Surface.Material.name}");

        var viewpoint = water.Viewpoint;
        W($"  Viewpoint = {(viewpoint != null ? viewpoint.name : "null")}（期望 Lake Viewpoint；Viewer 仍跟相机）");
        if (viewpoint == null) ok = false;

        int meshType = CrestPerformanceMode.GetMeshType(water);
        W($"  Surface.MeshType = {meshType}（期望 1=Quad）");
        if (meshType != 1) ok = false;

        W($"  LodLevels={water.LodLevels}（期望 5）, LodResolution={water.LodResolution}（期望 128）, " +
          $"Depth={water.DepthLod.Resolution}, Foam={water.FoamLod.Resolution}, Clip={water.ClipLod.Resolution}（期望 128）");
        if (water.LodLevels != 5 || water.LodResolution != 128) ok = false;
        if (water.DepthLod.Resolution != 128 ||
            water.FoamLod.Resolution != 128 ||
            water.ClipLod.Resolution != 128) ok = false;

        W($"  DepthLod.EnableSignedDistanceFields = {water.DepthLod.EnableSignedDistanceFields}（期望 false）");
        if (water.DepthLod.EnableSignedDistanceFields) ok = false;

        var body = root.GetComponentInChildren<WaterBody>();
        if (body == null) { W("  FAIL：没有 WaterBody"); ok = false; }
        else
        {
            W($"  WaterBody: Exclusion={body.Exclusion} Affects={body.Affects}");
            if (body.Exclusion != WaterBodyExclusion.Include) ok = false;
        }

        bool clipState = water.ClipLod.DefaultClippingState == DefaultClippingState.EverythingClipped;
        W($"  AbsorptionLod.Enabled(get)={water.AbsorptionLod.Enabled}, " +
          $"ScatteringLod.Enabled(get)={water.ScatteringLod.Enabled}, " +
          $"AlbedoLod.Enabled(get)={water.AlbedoLod.Enabled}（编辑期 _Valid 未就绪，仅供参考）");

        W($"  ClipLod.DefaultClippingState = {water.ClipLod.DefaultClippingState}（期望 EverythingClipped）");
        if (!clipState) ok = false;
        var clipInputs = root.GetComponentsInChildren<ClipLodInput>(true);
        W($"  ClipLodInput 数量 = {clipInputs.Length}（期望 >= 1）");
        if (clipInputs.Length == 0) ok = false;
        foreach (var c in clipInputs)
        {
            W($"    {c.name}: Primitive={c.Primitive} Inverted={c.Inverted} scale={c.transform.lossyScale}");
            if (Mathf.Abs(c.transform.lossyScale.x - ClipHalfExtent * 2f) > 0.1f ||
                Mathf.Abs(c.transform.lossyScale.z - ClipHalfExtent * 2f) > 0.1f)
            {
                W($"    FAIL：Clip 盒体 XZ 期望 {ClipHalfExtent * 2f}，实际 {c.transform.lossyScale}");
                ok = false;
            }
        }

        var gaia = GameObject.Find("Gaia Water");
        W($"  旧 Gaia Water 激活状态 = {(gaia != null ? gaia.activeSelf : false)}（期望 false）");
        if (gaia != null && gaia.activeSelf) ok = false;

        var urp = File.ReadAllText("Assets/Settings/UniversalRP.asset");
        bool depth = System.Text.RegularExpressions.Regex.IsMatch(urp, @"m_RequireDepthTexture:\s*1");
        bool opaque = System.Text.RegularExpressions.Regex.IsMatch(urp, @"m_RequireOpaqueTexture:\s*1");
        bool simpleTransparency = CrestPerformanceMode.IsSimpleTransparencyEnabled();
        W($"  URP DepthTexture={depth}, OpaqueTexture={opaque}（SimpleTransparency={simpleTransparency}，" +
          $"期望 {(simpleTransparency ? "0/0" : "1/1")}）");
        if (simpleTransparency)
        {
            if ((depth || opaque) && !CrestPerformanceMode.HasOtherDepthOrOpaqueConsumers(W))
                ok = false;
        }
        else if (!depth || !opaque)
        {
            ok = false;
        }

        return ok;
    }

    private static void Flush(StringBuilder sb, int code)
    {
        Directory.CreateDirectory(OutDir);
        File.WriteAllText(ResultPath, sb.ToString());
        EditorApplication.Exit(code);
    }
}
