using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

public static class LakeDuckweedSetup
{
    private const string Tag = "[LakeDuckweedSetup]";
    private const string WorldScenePath = "Assets/Scenes/World.unity";
    private const string RootName = "Lake Duckweed";
    private const string SourceRoot = "Assets/WaterPlants/Source/SwampForest";
    private const string ShaderPath = "Assets/WaterPlants/Shaders/WaterSurfacePlant.shader";
    private const string MaterialPath = "Assets/WaterPlants/Materials/M_SwampLily.mat";
    private const string MeshDir = "Assets/WaterPlants/Meshes";
    private const string OutDir = "Logs/Water";
    private const string ResultPath = OutDir + "/duckweed-setup.txt";

    private const int Seed = 20261121;
    private const int ClusterCount = 18;
    private const float TargetFootprint = 3.2f;
    private const float ClusterSpread = 9f;
    private const float ShoreRadiusMin = 0.88f;
    private const float ShoreRadiusMax = 0.96f;
    private const float MaxWaterRadius = 0.975f;
    private const float FloatHeight = 0.06f;

    private static readonly string[] Variants = { "Pink01", "Pink02", "White03", "White04" };

    private static readonly string[] ModelPaths =
    {
        SourceRoot + "/Models/swamp_lily_Pink01.fbx",
        SourceRoot + "/Models/swamp_lily_Pink02.fbx",
        SourceRoot + "/Models/swamp_lily_White03.fbx",
        SourceRoot + "/Models/swamp_lily_White04.fbx",
    };

    private static readonly string[] TexturePaths =
    {
        SourceRoot + "/Textures/SwampLily01.tga",
        SourceRoot + "/Textures/SwampLily01_N.png",
    };

    public static void Apply() { Run("apply"); }
    public static void Remove() { Run("remove"); }
    public static void Report() { Run("report"); }

    private static void Run(string mode)
    {
        var sb = new StringBuilder();
        void W(string s)
        {
            Debug.Log($"{Tag} {s}");
            sb.AppendLine(s);
        }

        var ok = true;
        try
        {
            Directory.CreateDirectory(OutDir);
            W($"模式 = {mode}  场景 = {WorldScenePath}");

            if (mode != "report")
            {
                ConfigureImporters(W);
                AssetDatabase.Refresh();
            }

            var scene = EditorSceneManager.OpenScene(WorldScenePath, OpenSceneMode.Single);
            var root = GameObject.Find(RootName);

            if (mode == "remove")
            {
                if (root != null) UnityEngine.Object.DestroyImmediate(root);
                DeleteGeneratedAssets();
                EditorSceneManager.MarkSceneDirty(scene);
                EditorSceneManager.SaveScene(scene);
                AssetDatabase.SaveAssets();
                W("已移除 Lake Duckweed 与生成材质/网格");
                W("==== 结论：PASS ====");
                Flush(sb, 0, W);
                return;
            }

            if (mode == "report")
            {
                ok = Validate(root, W);
                W($"==== 结论：{(ok ? "PASS" : "FAIL")} ====");
                Flush(sb, ok ? 0 : 2, W);
                return;
            }

            if (root != null) UnityEngine.Object.DestroyImmediate(root);
            DeleteGeneratedAssets();
            EnsureFolder("Assets/WaterPlants", "Materials");
            EnsureFolder("Assets/WaterPlants", "Meshes");

            var material = BuildMaterial(W);
            if (material == null)
            {
                W("FAIL：材质生成失败");
                Flush(sb, 2, W);
                return;
            }

            var sources = new Mesh[Variants.Length];
            var baseScales = new float[Variants.Length];
            for (int i = 0; i < Variants.Length; i++)
            {
                sources[i] = LoadSourceMesh(ModelPaths[i], W);
                if (sources[i] == null)
                {
                    W($"FAIL：无法合并 {ModelPaths[i]}");
                    Flush(sb, 2, W);
                    return;
                }

                var size = sources[i].bounds.size;
                float footprint = Mathf.Max(size.x, size.z);
                baseScales[i] = footprint > 0.001f ? TargetFootprint / footprint : 1f;
                W($"源 {Variants[i]}：verts={sources[i].vertexCount} tris={sources[i].triangles.Length / 3} " +
                  $"size={size} baseScale={baseScales[i]:F3}");
            }

            var placements = BuildPlacements(baseScales);
            root = new GameObject(RootName);
            int totalInstances = 0;
            long totalTris = 0;
            for (int i = 0; i < Variants.Length; i++)
            {
                var path = $"{MeshDir}/Lily_{Variants[i]}.asset";
                AssetDatabase.CreateAsset(sources[i], path);

                var variantRoot = new GameObject($"Lily {Variants[i]}");
                variantRoot.transform.SetParent(root.transform, false);
                long variantTris = sources[i].triangles.Length / 3L;

                foreach (var p in placements[i])
                {
                    var go = new GameObject($"Lily {Variants[i]} {totalInstances + 1}");
                    go.transform.SetParent(variantRoot.transform, false);
                    go.transform.localPosition = p.Position;
                    go.transform.localRotation = Quaternion.Euler(0f, p.Yaw, 0f);
                    go.transform.localScale = Vector3.one * p.Scale;

                    go.AddComponent<MeshFilter>().sharedMesh = sources[i];
                    var renderer = go.AddComponent<MeshRenderer>();
                    renderer.sharedMaterial = material;
                    renderer.shadowCastingMode = ShadowCastingMode.Off;
                    renderer.receiveShadows = false;
                    renderer.lightProbeUsage = LightProbeUsage.BlendProbes;
                    renderer.reflectionProbeUsage = ReflectionProbeUsage.BlendProbes;
                    renderer.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;
                    totalInstances++;
                }

                totalTris += variantTris * placements[i].Count;
                W($"变体 {Variants[i]}：实例={placements[i].Count} verts={sources[i].vertexCount} " +
                  $"tris={variantTris}");
            }
            W($"合计实例={totalInstances}，三角形={totalTris}");

            EditorUtility.SetDirty(root);
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            AssetDatabase.SaveAssets();

            ok = Validate(GameObject.Find(RootName), W);
            W($"==== 结论：{(ok ? "PASS" : "FAIL")} ====");
            Flush(sb, ok ? 0 : 2, W);
        }
        catch (Exception ex)
        {
            W($"异常：{ex}");
            W("==== 结论：FAIL ====");
            Flush(sb, 1, W);
        }
    }

    private static void ConfigureImporters(Action<string> W)
    {
        foreach (var path in TexturePaths)
        {
            var importer = AssetImporter.GetAtPath(path) as TextureImporter;
            if (importer == null)
            {
                W($"WARN：找不到贴图导入器 {path}");
                continue;
            }

            bool normal = path.EndsWith("_N.png", StringComparison.OrdinalIgnoreCase);
            var targetType = normal ? TextureImporterType.NormalMap : TextureImporterType.Default;
            bool targetSrgb = !normal;
            var targetFormat = normal ? TextureImporterFormat.Automatic : TextureImporterFormat.Automatic;

            var def = importer.GetPlatformTextureSettings("DefaultTexturePlatform");
            var android = importer.GetPlatformTextureSettings("Android");

            bool same = importer.textureType == targetType
                        && importer.sRGBTexture == targetSrgb
                        && importer.mipmapEnabled
                        && importer.wrapMode == TextureWrapMode.Clamp
                        && importer.filterMode == FilterMode.Trilinear
                        && importer.anisoLevel == 2
                        && importer.maxTextureSize == 1024
                        && importer.textureCompression == TextureImporterCompression.Compressed
                        && (!normal || importer.alphaSource == TextureImporterAlphaSource.None)
                        && (normal || importer.alphaIsTransparency)
                        && def.overridden
                        && def.maxTextureSize == 1024
                        && def.format == targetFormat
                        && android.overridden
                        && android.maxTextureSize == 1024
                        && android.format == TextureImporterFormat.ASTC_8x8;

            if (same) continue;

            importer.textureType = targetType;
            importer.sRGBTexture = targetSrgb;
            importer.alphaSource = normal ? TextureImporterAlphaSource.None : TextureImporterAlphaSource.FromInput;
            importer.alphaIsTransparency = !normal;
            importer.mipmapEnabled = true;
            importer.wrapMode = TextureWrapMode.Clamp;
            importer.filterMode = FilterMode.Trilinear;
            importer.anisoLevel = 2;
            importer.npotScale = TextureImporterNPOTScale.None;
            importer.maxTextureSize = 1024;
            importer.textureCompression = TextureImporterCompression.Compressed;

            def.overridden = true;
            def.maxTextureSize = 1024;
            def.resizeAlgorithm = TextureResizeAlgorithm.Mitchell;
            def.format = targetFormat;
            def.textureCompression = TextureImporterCompression.Compressed;
            importer.SetPlatformTextureSettings(def);

            android.overridden = true;
            android.maxTextureSize = 1024;
            android.resizeAlgorithm = TextureResizeAlgorithm.Mitchell;
            android.format = TextureImporterFormat.ASTC_8x8;
            android.textureCompression = TextureImporterCompression.Compressed;
            importer.SetPlatformTextureSettings(android);

            var standalone = importer.GetPlatformTextureSettings("Standalone");
            standalone.overridden = false;
            importer.SetPlatformTextureSettings(standalone);

            importer.SaveAndReimport();
            W($"贴图导入设置：{path}");
        }

        foreach (var path in ModelPaths)
        {
            var importer = AssetImporter.GetAtPath(path) as ModelImporter;
            if (importer == null)
            {
                W($"WARN：找不到模型导入器 {path}");
                continue;
            }

            if (!importer.isReadable)
            {
                importer.isReadable = true;
                importer.SaveAndReimport();
                W($"已打开可读：{path}");
            }
        }
    }

    private static Mesh LoadSourceMesh(string path, Action<string> W)
    {
        var model = AssetDatabase.LoadAssetAtPath<GameObject>(path);
        if (model == null)
        {
            W($"WARN：模型不存在 {path}");
            return null;
        }

        var filters = model.GetComponentsInChildren<MeshFilter>(true);
        var combines = new List<CombineInstance>();
        var rootInv = model.transform.worldToLocalMatrix;
        foreach (var filter in filters)
        {
            var mesh = filter.sharedMesh;
            if (mesh == null) continue;
            if (!mesh.isReadable)
            {
                W($"WARN：网格不可读 {path}");
                return null;
            }

            combines.Add(new CombineInstance
            {
                mesh = mesh,
                transform = rootInv * filter.transform.localToWorldMatrix,
            });
        }

        if (combines.Count == 0) return null;

        var merged = new Mesh { name = Path.GetFileNameWithoutExtension(path) };
        merged.indexFormat = IndexFormat.UInt32;
        merged.CombineMeshes(combines.ToArray(), true, true);
        merged.RecalculateBounds();
        return merged;
    }

    private static Material BuildMaterial(Action<string> W)
    {
        var shader = AssetDatabase.LoadAssetAtPath<Shader>(ShaderPath);
        if (shader == null) shader = Shader.Find("FishDemo/Water/SurfacePlant");
        if (shader == null)
        {
            W("FAIL：找不到 FishDemo/Water/SurfacePlant");
            return null;
        }

        var baseMap = AssetDatabase.LoadAssetAtPath<Texture2D>(TexturePaths[0]);
        var normalMap = AssetDatabase.LoadAssetAtPath<Texture2D>(TexturePaths[1]);
        if (baseMap == null || normalMap == null)
        {
            W("FAIL：贴图缺失");
            return null;
        }

        var material = AssetDatabase.LoadAssetAtPath<Material>(MaterialPath);
        if (material == null)
        {
            material = new Material(shader);
            AssetDatabase.CreateAsset(material, MaterialPath);
        }
        else
        {
            material.shader = shader;
        }

        material.SetTexture("_BaseMap", baseMap);
        material.SetTexture("_BumpMap", normalMap);
        material.SetColor("_BaseColor", Color.white);
        material.SetFloat("_BumpScale", 1f);
        material.SetFloat("_Cutoff", 0.5f);
        material.SetFloat("_WaveStrength", 0.07f);
        material.SetFloat("_WaveSpeed", 0.85f);
        material.SetFloat("_DriftStrength", 0.06f);
        material.SetFloat("_TiltStrength", 3f);
        material.SetFloat("_YawStrength", 2f);
        material.SetFloat("_AmbientBoost", 0.8f);
        material.EnableKeyword("_NORMALMAP");
        material.enableInstancing = true;
        material.renderQueue = (int)RenderQueue.AlphaTest;
        EditorUtility.SetDirty(material);
        return material;
    }

    private static List<Placement>[] BuildPlacements(float[] baseScales)
    {
        var rng = new System.Random(Seed);
        var result = new List<Placement>[Variants.Length];
        for (int i = 0; i < result.Length; i++) result[i] = new List<Placement>();

        float cx = LakeCarve.Center.x;
        float cz = LakeCarve.Center.y;
        float maxR = LakeCarve.Radius * MaxWaterRadius;

        for (int cluster = 0; cluster < ClusterCount; cluster++)
        {
            float t = ClusterCount > 1 ? cluster / (float)(ClusterCount - 1) : 0f;
            float angle = Mathf.Lerp(120f, 420f, t) * Mathf.Deg2Rad;
            float radius = LakeCarve.Radius * Mathf.Lerp(ShoreRadiusMin, ShoreRadiusMax, (float)rng.NextDouble());
            float ccx = cx + Mathf.Cos(angle) * radius;
            float ccz = cz + Mathf.Sin(angle) * radius;
            int count = 8 + rng.Next(6);

            for (int j = 0; j < count; j++)
            {
                float spreadAngle = (float)rng.NextDouble() * Mathf.PI * 2f;
                float dist = Mathf.Sqrt((float)rng.NextDouble()) * ClusterSpread;
                float x = ccx + Mathf.Cos(spreadAngle) * dist;
                float z = ccz + Mathf.Sin(spreadAngle) * dist;

                float dx = x - cx;
                float dz = z - cz;
                float rr = Mathf.Sqrt(dx * dx + dz * dz);
                if (rr > maxR)
                {
                    float k = maxR / rr;
                    x = cx + dx * k;
                    z = cz + dz * k;
                }

                int variant = (cluster * 3 + j) % Variants.Length;
                float scale = baseScales[variant] * (0.75f + (float)rng.NextDouble() * 0.6f);
                float yaw = (float)rng.NextDouble() * 360f;
                float y = LakeCarve.WaterLevel + FloatHeight + (float)rng.NextDouble() * 0.02f;
                result[variant].Add(new Placement(new Vector3(x, y, z), yaw, scale));
            }
        }

        return result;
    }

    private static bool Validate(GameObject root, Action<string> W)
    {
        bool ok = true;
        if (root == null)
        {
            W("FAIL：场景里找不到 Lake Duckweed");
            return false;
        }

        var material = AssetDatabase.LoadAssetAtPath<Material>(MaterialPath);
        var filters = root.GetComponentsInChildren<MeshFilter>(true);
        var renderers = root.GetComponentsInChildren<MeshRenderer>(true);
        var meshes = new HashSet<Mesh>();
        long verts = 0;
        long tris = 0;
        int bad = 0;

        foreach (var filter in filters)
        {
            var mesh = filter.sharedMesh;
            if (mesh == null)
            {
                bad++;
                continue;
            }
            meshes.Add(mesh);
            verts += mesh.vertexCount;
            tris += mesh.triangles.Length / 3;
        }

        foreach (var renderer in renderers)
        {
            if (renderer.sharedMaterial == null || renderer.sharedMaterial != material) bad++;
        }

        if (renderers.Length == 0) ok = false;
        if (meshes.Count != Variants.Length) ok = false;
        if (bad != 0) ok = false;
        if (material == null) ok = false;
        if (material != null && material.shader == null) ok = false;
        if (material != null && !material.enableInstancing) ok = false;

        float wave = material != null ? material.GetFloat("_WaveStrength") : 0f;
        float drift = material != null ? material.GetFloat("_DriftStrength") : 0f;
        float tilt = material != null ? material.GetFloat("_TiltStrength") : 0f;
        float yaw = material != null ? material.GetFloat("_YawStrength") : 0f;
        if (wave <= 0f || drift <= 0f || tilt <= 0f || yaw <= 0f) ok = false;

        W($"根节点 = {root.name}");
        W($"实例渲染器 = {renderers.Length}，唯一网格 = {meshes.Count}（期望 {Variants.Length}）");
        W($"顶点 = {verts}，三角形 = {tris}");
        W($"材质 = {(material != null ? MaterialPath : "null")}，共享材质异常 = {bad}");
        W($"材质 shader = {(material != null && material.shader != null ? material.shader.name : "null")}，" +
          $"instancing = {(material != null && material.enableInstancing)}");
        W($"动画参数 = wave {wave:F2} / drift {drift:F2} / tilt {tilt:F1}° / yaw {yaw:F1}°");
        return ok;
    }

    private static void DeleteGeneratedAssets()
    {
        AssetDatabase.DeleteAsset(MaterialPath);
        AssetDatabase.DeleteAsset(MeshDir);
    }

    private static void EnsureFolder(string parent, string name)
    {
        if (!AssetDatabase.IsValidFolder(parent + "/" + name))
        {
            AssetDatabase.CreateFolder(parent, name);
        }
    }

    private static void Flush(StringBuilder sb, int code, Action<string> W)
    {
        Directory.CreateDirectory(OutDir);
        File.WriteAllText(ResultPath, sb.ToString());
        if (Application.isBatchMode) EditorApplication.Exit(code);
        else Debug.Log($"{Tag} {(code == 0 ? "PASS" : "FAIL")}（编辑器模式不退出，结果见 {ResultPath}）");
    }

    [MenuItem("FishDemo/Water/重建水面浮萍")]
    private static void MenuApply()
    {
        Apply();
    }

    [MenuItem("FishDemo/Water/校验水面浮萍")]
    private static void MenuReport()
    {
        Report();
    }

    [MenuItem("FishDemo/Water/移除水面浮萍")]
    private static void MenuRemove()
    {
        Remove();
    }

    private readonly struct Placement
    {
        public readonly Vector3 Position;
        public readonly float Yaw;
        public readonly float Scale;

        public Placement(Vector3 position, float yaw, float scale)
        {
            Position = position;
            Yaw = yaw;
            Scale = scale;
        }
    }
}
