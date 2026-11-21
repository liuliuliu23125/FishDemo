using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using FishDemo.Gameplay;

public static class UnderwaterEcologySetup
{
    private const string Tag = "[UnderwaterEcology]";
    private const string WorldScenePath = "Assets/Scenes/World.unity";
    private const string RootName = "Underwater Ecology";
    private const string GenRoot = "Assets/UnderwaterEcology";
    private const string OutDir = "Logs/Underwater";
    private const string ResultPath = OutDir + "/underwater-ecology-setup.txt";

    private const string UwRoot = "Assets/Underwater world - Coral Reef & Marine Life";
    private const string OcRoot = "Assets/Davis3D/OceanEnvironmentPack";

    private const string CoralPrefabRoot = UwRoot + "/Coral Reef/Prefabs";
    private const string FishPrefabRoot = UwRoot + "/Marine Life/Prefabs";
    private const string OcPrefabRoot = OcRoot + "/Prefabs";
    private const string OcTextureRoot = OcRoot + "/Textures";

    private static readonly Vector2 ReefCenter2D = new Vector2(192f, 246f);
    private const float ReefRadius = 30f;
    private const float ReefPatchCenterDepth = 11.3f;
    private const float SandLift = 0.45f;

    private static readonly string[] CoralPrefabs =
    {
        "Coral_1", "Coral_2", "Coral_6", "Coral_7", "Coral_8", "Coral_9",
    };

    private static readonly string[] OceanPlantPrefabs =
    {
        "Prefab_Kelp_A", "Prefab_Kelp_B", "Prefab_SeaWeed_Multi", "Prefab_SeaWeed_Single",
        "Prefab_Anemone", "Prefab_Coral_A", "Prefab_Coral_B",
        "SeaSponge/Prefab_SeaSponge_Group_Large_A",
        "SeaSponge/Prefab_SeaSponge_Group_Large_B",
        "SeaSponge/Prefab_SeaSponge_Group_Small",
    };

    private static readonly List<Terrain> Terrains = new List<Terrain>();
    private static System.Random Rng = new System.Random(20261121);

    public static void Apply()
    {
        Run("apply");
    }

    public static void Remove()
    {
        Run("remove");
    }

    public static void Report()
    {
        Run("report");
    }

    private static void Run(string mode)
    {
        var sb = new StringBuilder();
        void W(string s) { Debug.Log($"{Tag} {s}"); sb.AppendLine(s); }

        try
        {
            Rng = new System.Random(20261121);
            Directory.CreateDirectory(OutDir);
            var scene = EditorSceneManager.OpenScene(WorldScenePath, OpenSceneMode.Single);
            ChunkLoadScope.LoadRelevant(ChunkLoadScope.Area.FromLake(20f), W);
            EditorSceneManager.SetActiveScene(EditorSceneManager.GetSceneByPath(WorldScenePath));
            CacheTerrains();
            W($"场景 = {WorldScenePath}，Terrain = {Terrains.Count}");

            if (mode == "report")
            {
                var existing = GameObject.Find(RootName);
                if (existing == null)
                {
                    W($"FAIL：场景里找不到 {RootName}");
                    Flush(sb, 2, W);
                    return;
                }
                W("");
                W("=== 校验 ===");
                bool reportOk = Validate(existing, W);
                W($"==== 结论：{(reportOk ? "PASS" : "FAIL")} ====");
                Flush(sb, reportOk ? 0 : 2, W);
                return;
            }

            var stale = GameObject.Find(RootName);
            if (stale != null) UnityEngine.Object.DestroyImmediate(stale);

            if (mode == "remove")
            {
                foreach (var sub in new[] { "Materials", "Meshes", "Prefabs" })
                {
                    string path = GenRoot + "/" + sub;
                    if (AssetDatabase.IsValidFolder(path)) AssetDatabase.DeleteAsset(path);
                }
                DisableCrestCaustics();
                EditorSceneManager.MarkSceneDirty(scene);
                EditorSceneManager.SaveScene(scene);
                AssetDatabase.SaveAssets();
                W("已删除 Underwater Ecology 与生成材质/网格/prefab，保留 shader");
                Flush(sb, 0, W);
                return;
            }

            EnsureGenFolders();
            ConfigureTextureImporters(W);
            ConfigureModelImporters(W);
            ConfigureMaterials(W);
            var custom = BuildCustomMaterials(W);

            var root = new GameObject(RootName);
            var benthic = new GameObject("Benthic").transform;
            benthic.SetParent(root.transform, false);
            var fishRoot = new GameObject("Fish").transform;
            fishRoot.SetParent(root.transform, false);

            var seabed = BuildSeabed(root.transform, W);
            var used = new List<Vector3>();
            BuildBenthic(benthic, used, W);
            BuildBubbles(root.transform, W);
            var godRays = BuildGodRays(root.transform, W);
            var ambience = BuildAmbience(root.transform, W);

            var meshBluetang = BuildVatMesh(OcRoot + "/Meshes/Creatures/SM_Bluetang.FBX", "Bluetang", W);
            var meshMedaka = BuildVatMesh(OcRoot + "/Meshes/Creatures/SM_Medaka.FBX", "Medaka", W);
            var meshBluetangFar = LoadMesh(OcRoot + "/Meshes/Creatures/SM_Bluetang.FBX", "LOD1", W);
            var meshMedakaFar = LoadMesh(OcRoot + "/Meshes/Creatures/SM_Medaka.FBX", "LOD1", W);
            var matBluetang = BuildVatMaterial("Bluetang", "", meshBluetang, W);
            var matMedaka = BuildVatMaterial("Medaka", "", meshMedaka, W);
            var matBluetangFar = BuildVatMaterial("Bluetang", "LOD1", meshBluetangFar, W);
            var matMedakaFar = BuildVatMaterial("Medaka", "LOD1", meshMedakaFar, W);

            var jellyfishPrefab = BuildJellyfishVariant(W);
            BuildFishSchools(fishRoot, meshBluetang, meshBluetangFar, matBluetang, matBluetangFar,
                meshMedaka, meshMedakaFar, matMedaka, matMedakaFar, jellyfishPrefab, W);
            ApplyCustomMaterials(root.transform, custom);
            ConfigureCrestCaustics(W);

            var controller = root.AddComponent<UnderwaterEcologyController>();
            SetRef(controller, "_benthicRoot", benthic);
            SetRef(controller, "_fishRoot", fishRoot);
            SetRef(controller, "_godRayRoot", godRays);
            SetRef(controller, "_bubbleRoot", root.transform.Find("Bubbles")?.gameObject);
            SetRef(controller, "_ambience", ambience);
            SetFloat(controller, "_waterLevel", LakeCarve.WaterLevel);
            SetVector(controller, "_lakeCenter", new Vector3(LakeCarve.Center.x, LakeCarve.WaterLevel, LakeCarve.Center.y));
            SetFloat(controller, "_lakeRadius", LakeCarve.Radius);
            SetFloat(controller, "_lakeDepth", LakeCarve.Depth);
            SetVector(controller, "_reefCenter", new Vector3(ReefCenter2D.x, LakeCarve.WaterLevel - ReefPatchCenterDepth, ReefCenter2D.y));

            var controls = root.AddComponent<UnderwaterDemoControls>();
            SetVector(controls, "_anchor", new Vector3(ReefCenter2D.x, LakeCarve.WaterLevel - 6f, ReefCenter2D.y - 10f));
            SetVector(controls, "_lookAt", new Vector3(ReefCenter2D.x, LakeCarve.WaterLevel - 10f, ReefCenter2D.y));

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            W("");
            W("=== 校验 ===");
            bool ok = Validate(root, W);
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

    private static void EnsureGenFolders()
    {
        EnsureFolder("Assets", "UnderwaterEcology");
        EnsureFolder("Assets/UnderwaterEcology", "Materials");
        EnsureFolder("Assets/UnderwaterEcology", "Meshes");
        EnsureFolder("Assets/UnderwaterEcology", "Prefabs");
        EnsureFolder("Assets/UnderwaterEcology", "Shaders");
    }

    private static void EnsureFolder(string parent, string name)
    {
        if (!AssetDatabase.IsValidFolder(parent + "/" + name))
            AssetDatabase.CreateFolder(parent, name);
    }

    private static void CacheTerrains()
    {
        Terrains.Clear();
        Terrains.AddRange(UnityEngine.Object.FindObjectsOfType<Terrain>());
        Terrains.Sort((a, b) =>
        {
            bool ga = HasGts(a);
            bool gb = HasGts(b);
            if (ga != gb) return ga ? -1 : 1;
            float aa = a.terrainData != null ? a.terrainData.size.x * a.terrainData.size.z : float.MaxValue;
            float bb = b.terrainData != null ? b.terrainData.size.x * b.terrainData.size.z : float.MaxValue;
            return aa.CompareTo(bb);
        });
    }

    private static bool HasGts(Terrain t)
    {
        return t != null && t.transform.Find("GTS_Mesh") != null;
    }

    private static float SampleHeight(float x, float z)
    {
        foreach (var t in Terrains)
        {
            if (t == null || t.terrainData == null) continue;
            var p = t.transform.position;
            var s = t.terrainData.size;
            if (x < p.x || x > p.x + s.x || z < p.z || z > p.z + s.z) continue;
            return p.y + t.SampleHeight(new Vector3(x, 0f, z));
        }

        float dx = x - LakeCarve.Center.x;
        float dz = z - LakeCarve.Center.y;
        float r = Mathf.Sqrt(dx * dx + dz * dz);
        float k = 1f - (r / LakeCarve.Radius) * (r / LakeCarve.Radius);
        if (k < 0f) k = 0f;
        return LakeCarve.WaterLevel - LakeCarve.Depth * k;
    }

    private static Vector3 SampleNormal(float x, float z)
    {
        foreach (var t in Terrains)
        {
            if (t == null || t.terrainData == null) continue;
            var p = t.transform.position;
            var s = t.terrainData.size;
            if (x < p.x || x > p.x + s.x || z < p.z || z > p.z + s.z) continue;
            float u = (x - p.x) / s.x;
            float v = (z - p.z) / s.z;
            return t.terrainData.GetInterpolatedNormal(u, v);
        }
        return Vector3.up;
    }

    private static void ConfigureTextureImporters(Action<string> W)
    {
        int changed = 0;
        foreach (var root in new[] { UwRoot, OcRoot })
        {
            foreach (var guid in AssetDatabase.FindAssets("t:Texture2D", new[] { root }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (!(path.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                      || path.EndsWith(".tga", StringComparison.OrdinalIgnoreCase)
                      || path.EndsWith(".hdr", StringComparison.OrdinalIgnoreCase)))
                    continue;

                var importer = AssetImporter.GetAtPath(path) as TextureImporter;
                if (importer == null) continue;

                string name = Path.GetFileName(path);
                bool vat = name.IndexOf("VertAnim", StringComparison.OrdinalIgnoreCase) >= 0
                           || name.IndexOf("_VA.", StringComparison.OrdinalIgnoreCase) >= 0;
                bool normal = name.IndexOf("_Normal", StringComparison.OrdinalIgnoreCase) >= 0
                              || name.IndexOf("_Norm", StringComparison.OrdinalIgnoreCase) >= 0;

                var targetType = normal ? TextureImporterType.NormalMap : TextureImporterType.Default;
                bool targetSrgb = !normal && !vat;
                bool targetMip = !vat;
                var targetWrap = vat ? TextureWrapMode.Clamp : TextureWrapMode.Repeat;
                var targetFilter = vat ? FilterMode.Bilinear : FilterMode.Trilinear;
                int targetAniso = vat ? 0 : 2;
                var targetNpot = vat ? TextureImporterNPOTScale.None : TextureImporterNPOTScale.ToNearest;
                var targetCompression = vat ? TextureImporterCompression.Uncompressed : TextureImporterCompression.Compressed;
                var targetFormat = vat ? TextureImporterFormat.RGBAHalf : TextureImporterFormat.Automatic;

                var def = importer.GetPlatformTextureSettings("DefaultTexturePlatform");
                var android = importer.GetPlatformTextureSettings("Android");

                bool same = importer.textureType == targetType
                            && importer.sRGBTexture == targetSrgb
                            && importer.mipmapEnabled == targetMip
                            && importer.wrapMode == targetWrap
                            && importer.filterMode == targetFilter
                            && importer.anisoLevel == targetAniso
                            && importer.npotScale == targetNpot
                            && importer.maxTextureSize == 1024
                            && importer.textureCompression == targetCompression
                            && def.overridden
                            && def.maxTextureSize == 1024
                            && def.format == targetFormat
                            && def.textureCompression == targetCompression
                            && android.overridden
                            && android.maxTextureSize == 1024
                            && android.format == (vat ? TextureImporterFormat.RGBAHalf : TextureImporterFormat.ASTC_8x8)
                            && android.textureCompression == targetCompression;

                if (same) continue;

                importer.textureType = targetType;
                importer.sRGBTexture = targetSrgb;
                importer.mipmapEnabled = targetMip;
                importer.wrapMode = targetWrap;
                importer.filterMode = targetFilter;
                importer.anisoLevel = targetAniso;
                importer.npotScale = targetNpot;
                importer.maxTextureSize = 1024;
                importer.textureCompression = targetCompression;

                def.overridden = true;
                def.maxTextureSize = 1024;
                def.resizeAlgorithm = TextureResizeAlgorithm.Mitchell;
                def.format = targetFormat;
                def.textureCompression = targetCompression;
                importer.SetPlatformTextureSettings(def);

                android.overridden = true;
                android.maxTextureSize = 1024;
                android.resizeAlgorithm = TextureResizeAlgorithm.Mitchell;
                if (vat)
                {
                    android.format = TextureImporterFormat.RGBAHalf;
                    android.textureCompression = TextureImporterCompression.Uncompressed;
                    android.crunchedCompression = false;
                }
                else
                {
                    android.format = TextureImporterFormat.ASTC_8x8;
                    android.textureCompression = TextureImporterCompression.Compressed;
                }
                importer.SetPlatformTextureSettings(android);

                var standalone = importer.GetPlatformTextureSettings("Standalone");
                standalone.overridden = false;
                importer.SetPlatformTextureSettings(standalone);

                importer.SaveAndReimport();
                changed++;
            }
        }
        W($"纹理导入设置已处理 {changed} 张");
    }

    private static void ConfigureModelImporters(Action<string> W)
    {
        foreach (var path in new[]
                 {
                     OcRoot + "/Meshes/Creatures/SM_Bluetang.FBX",
                     OcRoot + "/Meshes/Creatures/SM_Medaka.FBX",
                 })
        {
            var importer = AssetImporter.GetAtPath(path) as ModelImporter;
            if (importer == null) continue;
            if (!importer.isReadable)
            {
                importer.isReadable = true;
                importer.SaveAndReimport();
                W($"已打开可读：{path}");
            }
        }
    }

    private static void ConfigureMaterials(Action<string> W)
    {
        int count = 0;
        foreach (var path in AssetDatabase.FindAssets("t:Material", new[] { UwRoot, OcRoot }))
        {
            string p = AssetDatabase.GUIDToAssetPath(path);
            var mat = AssetDatabase.LoadAssetAtPath<Material>(p);
            if (mat == null) continue;
            if (!mat.enableInstancing)
            {
                mat.enableInstancing = true;
                EditorUtility.SetDirty(mat);
            }
            count++;
        }
        var godRay = AssetDatabase.LoadAssetAtPath<Material>(OcRoot + "/Materials/Effects/M_GodRay.mat");
        if (godRay != null)
        {
            var beam = AssetDatabase.LoadAssetAtPath<Texture2D>(OcTextureRoot + "/TilableTextures/T_Gradient_Vertical_2wayB.png");
            var vertical = AssetDatabase.LoadAssetAtPath<Texture2D>(OcTextureRoot + "/TilableTextures/Gradient_Vertical_2way2.png");
            if (beam != null)
            {
                godRay.SetTexture("_BeamGradientA", beam);
                godRay.SetTexture("_BeamGradientB", beam);
                godRay.SetTexture("_T_Gradient_Vertical_2wayB", beam);
                godRay.SetTexture("_Gradient_Vertical_way3", beam);
            }
            if (vertical != null) godRay.SetTexture("_Gradient_Vertical_2way2", vertical);
            EditorUtility.SetDirty(godRay);
        }

        var coralB = AssetDatabase.LoadAssetAtPath<Material>(OcRoot + "/Materials/Foliage/M_Coral_B.mat");
        if (coralB != null)
        {
            var mask = AssetDatabase.LoadAssetAtPath<Texture2D>(OcTextureRoot + "/PlantTextures/T_Coral_Mask.png");
            if (mask != null) coralB.SetTexture("Material_Texture2D_9", mask);
            EditorUtility.SetDirty(coralB);
        }

        W($"材质 instancing 已处理 {count} 个");
    }

    private static Texture2D LoadTexture(string path)
    {
        return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
    }

    private static Dictionary<Material, Material> BuildCustomMaterials(Action<string> W)
    {
        var map = new Dictionary<Material, Material>();
        var plantShader = Shader.Find("FishDemo/Underwater/Plant");
        var godShader = Shader.Find("FishDemo/Underwater/GodRay");
        var bubbleShader = Shader.Find("FishDemo/Underwater/Bubble");
        if (plantShader == null || godShader == null || bubbleShader == null)
        {
            W("FAIL：自研水下 shader 未导入");
            return map;
        }

        EnsureFolder(GenRoot + "/Materials", "Custom");
        var gradient = LoadTexture(OcTextureRoot + "/TilableTextures/T_Gradient_Vertical_2wayB.png");
        int plants = 0, effects = 0;

        foreach (var guid in AssetDatabase.FindAssets("t:Material", new[] { OcRoot + "/Materials" }))
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            var src = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (src == null || src.shader == null) continue;

            string shaderName = src.shader.name;
            Material created = null;
            if (shaderName.Contains("Shader_Seaweed") || shaderName.Contains("Shader_SolidPlant"))
            {
                created = new Material(plantShader) { name = src.name + "_Custom" };
                var diffuse = src.GetTexture("_Diffuse") ?? src.GetTexture("_MainTex");
                var normal = src.GetTexture("_Normal");
                var glow = src.GetTexture("_Glow");
                var color = src.GetColor("_Color");
                if (diffuse == null)
                {
                    var multiply = src.GetColor("_DiffuseMultiply");
                    color = new Color(color.r * multiply.r, color.g * multiply.g,
                        color.b * multiply.b, color.a);
                }
                created.SetTexture("_BaseMap", diffuse);
                created.SetTexture("_BumpMap", normal);
                created.SetTexture("_EmissionMap", glow);
                created.SetColor("_BaseColor", color);
                created.SetColor("_EmissionColor", src.GetColor("_GlowColor"));
                created.SetFloat("_Cutoff", Mathf.Clamp01(src.GetFloat("_AlphaCutoff")));
                created.SetFloat("_EmissionStrength", glow != null ? 0.2f : 0f);
                created.SetFloat("_WindSpeed", 0.8f);
                created.SetFloat("_WindStrength", src.name.Contains("Kelp") ? 0.18f
                    : src.name.Contains("SeaWeed") ? 0.14f
                    : src.name.Contains("Anemone") ? 0.06f : 0.02f);
                created.SetFloat("_AmbientBoost", 0.85f);
                created.SetFloat("_BumpScale", 1f);
                if (normal != null) created.EnableKeyword("_NORMALMAP");
                created.enableInstancing = true;
                if (diffuse == null) W($"无 diffuse，使用 _DiffuseMultiply：{src.name}");
                plants++;
            }
            else if (shaderName.Contains("Shader_Godrays"))
            {
                created = new Material(godShader) { name = src.name + "_Custom" };
                created.SetTexture("_MainTex", gradient);
                created.SetColor("_Color", new Color(0.55f, 0.8f, 1f, 1f));
                created.SetFloat("_Intensity", 0.55f);
                created.SetFloat("_EdgeSoftness", 0.4f);
                created.SetFloat("_VerticalFade", 0.75f);
                effects++;
            }
            else if (shaderName.Contains("ParticlesBubbles"))
            {
                created = new Material(bubbleShader) { name = src.name + "_Custom" };
                created.SetColor("_Color", new Color(0.7f, 0.88f, 1f, 1f));
                created.SetFloat("_Intensity", 1.1f);
                created.SetFloat("_Alpha", 0.8f);
                created.SetFloat("_RimPower", 2f);
                effects++;
            }

            if (created == null) continue;
            string outPath = GenRoot + "/Materials/Custom/" + created.name + ".mat";
            AssetDatabase.DeleteAsset(outPath);
            AssetDatabase.CreateAsset(created, outPath);
            map[src] = created;
        }

        W($"自研水下材质：植物 {plants}，效果 {effects}");
        return map;
    }

    private static void ApplyCustomMaterials(Transform root, Dictionary<Material, Material> map)
    {
        if (map.Count == 0) return;
        foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
        {
            var mats = renderer.sharedMaterials;
            bool changed = false;
            for (int i = 0; i < mats.Length; i++)
            {
                if (mats[i] != null && map.TryGetValue(mats[i], out var custom))
                {
                    mats[i] = custom;
                    changed = true;
                }
            }
            if (changed) renderer.sharedMaterials = mats;
        }
    }

    private static GameObject BuildJellyfishVariant(Action<string> W)
    {
        var source = AssetDatabase.LoadAssetAtPath<GameObject>(OcPrefabRoot + "/Creatures/SM_Jellyfish.prefab");
        if (source == null) { W("FAIL：缺失 SM_Jellyfish.prefab"); return null; }

        var shader = Shader.Find("FishDemo/Underwater/JellyfishVAT");
        if (shader == null) { W("FAIL：找不到 JellyfishVAT"); return null; }

        var materials = new Dictionary<string, Material>();
        foreach (var tendrils in new[] { false, true })
        {
            foreach (var lod in new[] { "LOD0", "LOD1" })
            {
                string type = tendrils ? "Tendrils" : "Top";
                var mat = new Material(shader) { name = $"M_Jellyfish_{type}_{lod}_Custom" };
                string basePath = tendrils
                    ? OcTextureRoot + "/Creatures/Jellyfish/T_JellyfishTendrils_Textures.TGA"
                    : OcTextureRoot + "/Creatures/Jellyfish/T_JellyfishTop_Textures.TGA";
                string opacityPath = tendrils
                    ? OcTextureRoot + "/Creatures/Jellyfish/T_Jellyfish_Tendrils_Opacity.TGA"
                    : OcTextureRoot + "/Creatures/Jellyfish/T_JellyfishTop_Opacity.TGA";
                string glowPath = tendrils
                    ? OcTextureRoot + "/Creatures/Jellyfish/T_JellyfishTendrils_Glow.TGA"
                    : OcTextureRoot + "/Creatures/Jellyfish/T_JellyfishTop_Glow.TGA";
                string vatPath = OcTextureRoot + "/Creatures/Jellyfish/T_Jellyfish_" + lod + "_VertAnim.HDR";
                mat.SetTexture("_BaseMap", LoadTexture(basePath));
                mat.SetTexture("_OpacityMap", LoadTexture(opacityPath));
                mat.SetTexture("_EmissionMap", LoadTexture(glowPath));
                mat.SetTexture("_VAT", LoadTexture(vatPath));
                mat.SetColor("_Color", Color.white);
                mat.SetColor("_EmissionColor", new Color(0.25f, 0.55f, 1f, 1f));
                mat.SetFloat("_Alpha", tendrils ? 0.32f : 0.45f);
                mat.SetFloat("_EmissionStrength", 1.6f);
                mat.SetFloat("_VATFrames", 51f);
                mat.SetFloat("_VATSpeed", 0.8f);
                mat.SetFloat("_VATAmount", 0.1f);
                mat.SetFloat("_OffsetScale", 0.1f);
                mat.enableInstancing = true;

                string outPath = GenRoot + "/Materials/Custom/" + mat.name + ".mat";
                AssetDatabase.DeleteAsset(outPath);
                AssetDatabase.CreateAsset(mat, outPath);
                materials[type + "_" + lod] = mat;
            }
        }

        var instance = (GameObject)PrefabUtility.InstantiatePrefab(source);
        foreach (var renderer in instance.GetComponentsInChildren<Renderer>(true))
        {
            var mats = renderer.sharedMaterials;
            for (int i = 0; i < mats.Length; i++)
            {
                if (mats[i] == null) continue;
                bool tendrils = mats[i].name.Contains("Tendrils");
                string lod = mats[i].name.Contains("LOD1") ? "LOD1" : "LOD0";
                string key = (tendrils ? "Tendrils" : "Top") + "_" + lod;
                if (materials.TryGetValue(key, out var custom)) mats[i] = custom;
            }
            renderer.sharedMaterials = mats;
        }

        string path = GenRoot + "/Prefabs/Jellyfish_Compat.prefab";
        AssetDatabase.DeleteAsset(path);
        var saved = PrefabUtility.SaveAsPrefabAsset(instance, path);
        UnityEngine.Object.DestroyImmediate(instance);
        W($"水母自研材质 prefab：{path}");
        return saved;
    }

    private static GameObject BuildSeabed(Transform parent, Action<string> W)
    {
        const int rings = 96;
        const int segments = 256;
        float outer = LakeCarve.Radius - 2f;
        float cx = LakeCarve.Center.x;
        float cz = LakeCarve.Center.y;

        var verts = new List<Vector3>(1 + rings * segments);
        var uvs = new List<Vector2>(1 + rings * segments);
        var tris = new List<int>(rings * segments * 6);

        float centerY = SampleHeight(cx, cz) + SandLift;
        verts.Add(new Vector3(cx, centerY, cz));
        uvs.Add(new Vector2(cx / 8f, cz / 8f));

        for (int r = 1; r <= rings; r++)
        {
            float rr = outer * r / rings;
            for (int s = 0; s < segments; s++)
            {
                float a = s * Mathf.PI * 2f / segments;
                float x = cx + Mathf.Cos(a) * rr;
                float z = cz + Mathf.Sin(a) * rr;
                verts.Add(new Vector3(x, SampleHeight(x, z) + SandLift, z));
                uvs.Add(new Vector2(x / 8f, z / 8f));
            }
        }

        for (int s = 0; s < segments; s++)
        {
            int a = 1 + s;
            int b = 1 + (s + 1) % segments;
            tris.Add(0); tris.Add(b); tris.Add(a);
        }

        for (int r = 1; r < rings; r++)
        {
            int ring0 = 1 + (r - 1) * segments;
            int ring1 = 1 + r * segments;
            for (int s = 0; s < segments; s++)
            {
                int s1 = (s + 1) % segments;
                int a = ring0 + s;
                int b = ring0 + s1;
                int c = ring1 + s;
                int d = ring1 + s1;
                tris.Add(a); tris.Add(c); tris.Add(b);
                tris.Add(b); tris.Add(c); tris.Add(d);
            }
        }

        var mesh = new Mesh { name = "SeabedSand" };
        mesh.SetVertices(verts);
        mesh.SetUVs(0, uvs);
        mesh.SetTriangles(tris, 0);
        mesh.RecalculateNormals();
        var normals = mesh.normals;
        if (normals.Length > 0 && normals[0].y < 0f)
        {
            for (int i = 0; i < tris.Count; i += 3)
            {
                int tmp = tris[i + 1];
                tris[i + 1] = tris[i + 2];
                tris[i + 2] = tmp;
            }
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateNormals();
        }
        mesh.RecalculateTangents();
        mesh.RecalculateBounds();

        string meshPath = GenRoot + "/Meshes/SeabedSand.asset";
        AssetDatabase.DeleteAsset(meshPath);
        AssetDatabase.CreateAsset(mesh, meshPath);

        var material = BuildSandMaterial(W);
        var go = new GameObject("Seabed Sand");
        go.transform.SetParent(parent, false);
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        var mr = go.AddComponent<MeshRenderer>();
        mr.sharedMaterial = material;
        mr.shadowCastingMode = ShadowCastingMode.Off;
        mr.receiveShadows = false;
        mr.lightProbeUsage = LightProbeUsage.Off;
        mr.reflectionProbeUsage = ReflectionProbeUsage.Off;

        var ns = mesh.normals;
        int nearest = 0;
        float best = float.MaxValue;
        for (int i = 0; i < verts.Count; i++)
        {
            float dx = verts[i].x - ReefCenter2D.x;
            float dz = verts[i].z - ReefCenter2D.y;
            float d = dx * dx + dz * dz;
            if (d < best) { best = d; nearest = i; }
        }
        W($"海床网格：{verts.Count} 顶点 / {tris.Count / 3} 三角形，normal0.y={(ns.Length > 0 ? ns[0].y : 0f):F2}，" +
          $"reef 最近顶点 y={verts[nearest].y:F2}（terrain={SampleHeight(ReefCenter2D.x, ReefCenter2D.y):F2}）");
        return go;
    }

    private static Material BuildSandMaterial(Action<string> W)
    {
        string path = GenRoot + "/Materials/M_SeabedSand.mat";
        AssetDatabase.DeleteAsset(path);
        var shader = Shader.Find("FishDemo/Underwater/SeabedSand");
        if (shader == null) { W("FAIL：找不到 SeabedSand shader"); return null; }
        var mat = new Material(shader) { name = "M_SeabedSand" };
        var baseMap = AssetDatabase.LoadAssetAtPath<Texture2D>(OcTextureRoot + "/T_Sand_Dif.png");
        if (baseMap != null) mat.SetTexture("_BaseMap", baseMap);
        mat.SetColor("_BaseColor", new Color(0.95f, 0.88f, 0.72f, 1f));
        mat.SetColor("_AmbientColor", new Color(0.5f, 0.64f, 0.72f, 1f));
        mat.SetFloat("_AmbientStrength", 1.25f);
        mat.SetFloat("_LightStrength", 0.9f);
        mat.enableInstancing = true;
        AssetDatabase.CreateAsset(mat, path);
        W("海床材质：自研 SeabedSand + T_Sand_Dif");
        return mat;
    }

    private static void BuildBenthic(Transform root, List<Vector3> used, Action<string> W)
    {
        var coralCounts = new[] { 4, 4, 4, 4, 4, 4 };
        for (int i = 0; i < CoralPrefabs.Length; i++)
        {
            PlacePrefab(root, $"{CoralPrefabRoot}/{CoralPrefabs[i]}.prefab", coralCounts[i], 0.8f, 1.8f,
                1.4f, 0.03f, true, used, W);
        }

        PlacePrefab(root, $"{CoralPrefabRoot}/Rock_2.prefab", 10, 1.2f, 3.2f, 2.2f, 0.15f, true, used, W);
        PlacePrefab(root, $"{CoralPrefabRoot}/Ship.prefab", 1, 1.4f, 1.4f, 0f, -0.3f, false, used, W,
            new Vector3(ReefCenter2D.x + 13f, 0f, ReefCenter2D.y + 6f), 70f);

        var counts = new[] { 6, 6, 8, 8, 4, 4, 4, 1, 2, 4 };
        for (int i = 0; i < OceanPlantPrefabs.Length; i++)
        {
            float min = 0.7f, max = 1.6f;
            if (OceanPlantPrefabs[i].Contains("SeaSponge")) { min = 0.9f; max = 1.7f; }
            if (OceanPlantPrefabs[i].Contains("Kelp")) { min = 0.8f; max = 1.7f; }
            PlacePrefab(root, $"{OcPrefabRoot}/{OceanPlantPrefabs[i]}.prefab", counts[i], min, max,
                1.5f, 0.02f, false, used, W);
        }

        PlacePrefab(root, $"{OcPrefabRoot}/Prefab_Shell.prefab", 2, 0.5f, 1.2f, 0.8f, 0.02f, true, used, W);
        W($"底栖实例：{root.childCount} 个");
    }

    private static void PlacePrefab(Transform root, string path, int count, float minScale, float maxScale,
        float minDistance, float sink, bool alignNormal, List<Vector3> used, Action<string> W,
        Vector3? fixedPosition = null, float fixedYaw = 0f)
    {
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
        if (prefab == null)
        {
            W($"FAIL：缺失 prefab {path}");
            return;
        }

        for (int i = 0; i < count; i++)
        {
            float x, z;
            if (fixedPosition.HasValue)
            {
                x = fixedPosition.Value.x;
                z = fixedPosition.Value.z;
            }
            else
            {
                bool found = false;
                x = z = 0f;
                for (int attempt = 0; attempt < 24; attempt++)
                {
                    float a = (float)Rng.NextDouble() * Mathf.PI * 2f;
                    float r = Mathf.Sqrt((float)Rng.NextDouble()) * ReefRadius;
                    float tx = ReefCenter2D.x + Mathf.Cos(a) * r;
                    float tz = ReefCenter2D.y + Mathf.Sin(a) * r;
                    bool ok = true;
                    for (int u = 0; u < used.Count; u++)
                    {
                        float dx = used[u].x - tx;
                        float dz = used[u].z - tz;
                        if (dx * dx + dz * dz < minDistance * minDistance) { ok = false; break; }
                    }
                    if (!ok) continue;
                    x = tx; z = tz; found = true; break;
                }
                if (!found) continue;
            }

            float y = SampleHeight(x, z) + SandLift;
            if (y > LakeCarve.WaterLevel - 0.4f) continue;

            var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab, root);
            go.name = prefab.name + "_" + i;
            var normal = alignNormal ? SampleNormal(x, z) : Vector3.up;
            var yaw = fixedPosition.HasValue ? fixedYaw : (float)Rng.NextDouble() * 360f;
            go.transform.position = new Vector3(x, y - sink, z);
            go.transform.rotation = Quaternion.FromToRotation(Vector3.up, normal)
                                    * Quaternion.Euler(0f, yaw, 0f)
                                    * prefab.transform.rotation;
            float scale = Mathf.Lerp(minScale, maxScale, (float)Rng.NextDouble());
            go.transform.localScale = prefab.transform.localScale * scale;

            foreach (var renderer in go.GetComponentsInChildren<Renderer>(true))
            {
                renderer.shadowCastingMode = ShadowCastingMode.Off;
                renderer.receiveShadows = false;
            }

            used.Add(new Vector3(x, y, z));
        }
    }

    private static GameObject BuildBubbles(Transform parent, Action<string> W)
    {
        var root = new GameObject("Bubbles");
        root.transform.SetParent(parent, false);

        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(OcPrefabRoot + "/P_Bubbles.prefab");
        if (prefab == null) { W("FAIL：缺失 P_Bubbles.prefab"); return root; }

        var positions = new[]
        {
            new Vector3(ReefCenter2D.x - 10f, 0f, ReefCenter2D.y + 4f),
            new Vector3(ReefCenter2D.x + 6f, 0f, ReefCenter2D.y - 8f),
            new Vector3(ReefCenter2D.x + 14f, 0f, ReefCenter2D.y + 9f),
            new Vector3(ReefCenter2D.x - 4f, 0f, ReefCenter2D.y - 14f),
        };

        for (int i = 0; i < positions.Length; i++)
        {
            var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab, root.transform);
            go.name = "Bubbles_" + i;
            float y = SampleHeight(positions[i].x, positions[i].z);
            go.transform.position = new Vector3(positions[i].x, y + 0.3f, positions[i].z);
            go.transform.localScale = Vector3.one * 1.5f;

            var ps = go.GetComponentInChildren<ParticleSystem>();
            if (ps != null)
            {
                var main = ps.main;
                main.maxParticles = 80;
                main.startLifetime = 6f;
                main.startSpeed = 0.7f;
                var emission = ps.emission;
                emission.rateOverTime = 6f;
            }
        }

        W($"气泡发射器：{positions.Length} 个");
        return root;
    }

    private static GameObject BuildGodRays(Transform parent, Action<string> W)
    {
        var root = new GameObject("God Rays");
        root.transform.SetParent(parent, false);

        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(OcPrefabRoot + "/Prefab_GodRay.prefab");
        if (prefab == null) { W("FAIL：缺失 Prefab_GodRay.prefab"); return root; }

        var sun = RenderSettings.sun;
        float sunYaw = sun != null ? sun.transform.eulerAngles.y : 35f;

        for (int i = 0; i < 8; i++)
        {
            float a = i * Mathf.PI * 2f / 8f + 0.31f;
            float r = 6f + (float)Rng.NextDouble() * 20f;
            float x = ReefCenter2D.x + Mathf.Cos(a) * r;
            float z = ReefCenter2D.y + Mathf.Sin(a) * r;

            var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab, root.transform);
            go.name = "GodRay_" + i;
            var mf = go.GetComponentInChildren<MeshFilter>();
            var b = mf != null && mf.sharedMesh != null ? mf.sharedMesh.bounds : new Bounds(Vector3.zero, Vector3.one);
            float height = Mathf.Max(1f, b.size.y);
            float width = Mathf.Max(1f, Mathf.Max(b.size.x, b.size.z));
            float scaleY = (LakeCarve.WaterLevel - SampleHeight(x, z)) / height;
            float scaleXZ = 7f / width;
            go.transform.localScale = new Vector3(scaleXZ, scaleY, scaleXZ);
            go.transform.position = new Vector3(x, LakeCarve.WaterLevel - b.max.y * scaleY, z);
            go.transform.rotation = Quaternion.Euler(0f, sunYaw, 0f);

            foreach (var renderer in go.GetComponentsInChildren<Renderer>(true))
            {
                renderer.shadowCastingMode = ShadowCastingMode.Off;
                renderer.receiveShadows = false;
                foreach (var mat in renderer.sharedMaterials)
                {
                    if (mat != null) mat.SetFloat("_Glow", 0.22f);
                }
            }
        }

        root.SetActive(false);
        W($"God rays：8 个（朝向太阳方位角 {sunYaw:F0}°，默认关闭）");
        return root;
    }

    private static AudioSource BuildAmbience(Transform parent, Action<string> W)
    {
        var go = new GameObject("Underwater Ambience");
        go.transform.SetParent(parent, false);
        var source = go.AddComponent<AudioSource>();
        source.clip = AssetDatabase.LoadAssetAtPath<AudioClip>(OcRoot + "/Sounds/S_UnderwaterAmbience_A.WAV");
        source.loop = true;
        source.playOnAwake = false;
        source.spatialBlend = 0f;
        source.volume = 0f;
        if (source.clip == null) W("FAIL：缺失水下环境音");
        return source;
    }

    private static Mesh BuildVatMesh(string fbxPath, string label, Action<string> W)
    {
        var meshes = AssetDatabase.LoadAllAssetsAtPath(fbxPath).OfType<Mesh>().ToArray();
        var source = meshes.FirstOrDefault(m => m.name.IndexOf("LOD0", StringComparison.OrdinalIgnoreCase) >= 0)
                     ?? meshes.FirstOrDefault();
        if (source == null)
        {
            W($"FAIL：{fbxPath} 没有 Mesh");
            return null;
        }

        string path = GenRoot + "/Meshes/" + label + "_VAT.asset";
        AssetDatabase.DeleteAsset(path);

        if (source.uv2.Length == source.vertexCount)
        {
            W($"VAT mesh：{label} 使用源 mesh {source.vertexCount} 顶点（UV1 {source.uv2.Length}）");
            return source;
        }

        var copy = UnityEngine.Object.Instantiate(source);
        copy.name = label + "_VAT";
        int n = copy.vertexCount;
        var uv1 = new Vector2[n];
        for (int i = 0; i < n; i++) uv1[i] = new Vector2(n > 1 ? i / (float)(n - 1) : 0f, 0f);
        copy.SetUVs(1, uv1);
        copy.UploadMeshData(false);
        AssetDatabase.CreateAsset(copy, path);
        W($"VAT mesh：{label} 生成 UV1，{n} 顶点（源 {source.name}，源 UV1 {source.uv2.Length}）");
        return copy;
    }

    private static Mesh LoadMesh(string fbxPath, string token, Action<string> W)
    {
        var meshes = AssetDatabase.LoadAllAssetsAtPath(fbxPath).OfType<Mesh>().ToArray();
        var mesh = meshes.FirstOrDefault(m => m.name.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0);
        if (mesh == null)
        {
            W($"FAIL：{fbxPath} 没有 {token} mesh");
            return null;
        }
        return mesh;
    }

    private static Material BuildVatMaterial(string label, string lodSuffix, Mesh mesh, Action<string> W)
    {
        var shader = Shader.Find("FishDemo/Underwater/FishVAT");
        if (shader == null) { W("FAIL：找不到 FishDemo/Underwater/FishVAT"); return null; }

        string suffix = string.IsNullOrEmpty(lodSuffix) ? "" : "_" + lodSuffix;
        string path = GenRoot + "/Materials/M_" + label + "_VAT" + suffix + ".mat";
        AssetDatabase.DeleteAsset(path);
        var mat = new Material(shader) { name = "M_" + label + "_VAT" + suffix };

        string basePath = label == "Bluetang" ? OcTextureRoot + "/Creatures/Bluetang/T_Bluetang_Dif.TGA" : OcTextureRoot + "/Creatures/Medaka/T_Medaka_Dif.TGA";
        string normalPath = label == "Bluetang" ? OcTextureRoot + "/Creatures/Bluetang/T_Bluetang_Norm.TGA" : OcTextureRoot + "/Creatures/Medaka/T_Medaka_Norm.TGA";
        string vatPath;
        if (label == "Bluetang")
            vatPath = OcTextureRoot + "/Creatures/Bluetang/T_Bluetang_VertAnim_" + (string.IsNullOrEmpty(lodSuffix) ? "LOD0" : lodSuffix) + ".HDR";
        else
            vatPath = OcTextureRoot + "/Creatures/Medaka/T_Medaka_" + (string.IsNullOrEmpty(lodSuffix) ? "LOD0" : lodSuffix) + "_VA.HDR";

        mat.SetTexture("_BaseMap", AssetDatabase.LoadAssetAtPath<Texture2D>(basePath));
        mat.SetTexture("_BumpMap", AssetDatabase.LoadAssetAtPath<Texture2D>(normalPath));
        mat.SetTexture("_VAT", AssetDatabase.LoadAssetAtPath<Texture2D>(vatPath));
        mat.SetColor("_BaseColor", Color.white);
        mat.SetFloat("_VATFrames", 51f);
        mat.SetFloat("_VATSpeed", 1f);
        mat.SetFloat("_VATAmount", 0.1f);
        mat.SetFloat("_OffsetScale", 0.1f);
        mat.SetFloat("_Cutoff", 0.5f);
        mat.SetFloat("_BumpScale", 1f);
        mat.enableInstancing = true;
        AssetDatabase.CreateAsset(mat, path);

        W($"VAT 材质：{mat.name}（mesh={(mesh != null ? mesh.vertexCount.ToString() : "null")}）");
        return mat;
    }

    private static void BuildFishSchools(Transform root, Mesh meshBluetang, Mesh meshBluetangFar,
        Material matBluetang, Material matBluetangFar, Mesh meshMedaka, Mesh meshMedakaFar,
        Material matMedaka, Material matMedakaFar, GameObject jellyfishPrefab, Action<string> W)
    {
        float cx = ReefCenter2D.x;
        float cz = ReefCenter2D.y;
        float surface = LakeCarve.WaterLevel;

        CreateMeshSchool(root, "Bluetang School", meshBluetang, meshBluetangFar, matBluetang, matBluetangFar,
            120, 0.34f, new Vector3(cx + 2f, surface - 9f, cz - 4f), new Vector3(10f, 2.2f, 10f),
            0.22f, 0.7f, 5f, false, 0f, 11);
        CreateMeshSchool(root, "Medaka School", meshMedaka, meshMedakaFar, matMedaka, matMedakaFar,
            100, 0.18f, new Vector3(cx - 6f, surface - 9.8f, cz - 6f), new Vector3(12f, 1.8f, 12f),
            0.28f, 0.9f, 7f, false, 0f, 29);

        CreatePrefabSchool(root, "Coral Fish 1", FishPrefabRoot + "/Coral_Fish_1.prefab", 10, 0.5f,
            new Vector3(cx - 3f, surface - 8f, cz - 1f), new Vector3(7f, 1.5f, 7f),
            0.16f, 0.5f, 3f, false, 0f, 41);
        CreatePrefabSchool(root, "Coral Fish 2", FishPrefabRoot + "/Coral_Fish_2.prefab", 10, 0.45f,
            new Vector3(cx + 5f, surface - 7f, cz - 5f), new Vector3(8f, 1.5f, 8f),
            0.17f, 0.5f, 3f, false, 0f, 43);
        CreatePrefabSchool(root, "Coral Fish 4", FishPrefabRoot + "/Coral_Fish_4.prefab", 10, 0.42f,
            new Vector3(cx, surface - 6.5f, cz - 9f), new Vector3(9f, 2f, 9f),
            0.15f, 0.55f, 4f, false, 0f, 47);

        CreatePrefabSchool(root, "Turtle", FishPrefabRoot + "/Leatherback_turtle.prefab", 1, 1.3f,
            new Vector3(cx + 10f, surface - 6f, cz), new Vector3(16f, 2f, 16f),
            0.05f, 0.2f, 8f, false, 0f, 53);
        CreatePrefabSchool(root, "Stingray", FishPrefabRoot + "/Beige_stingray.prefab", 1, 1.1f,
            new Vector3(cx - 10f, surface - 8f, cz + 5f), new Vector3(18f, 1f, 18f),
            0.06f, 0.25f, 6f, false, 0f, 59);

        if (jellyfishPrefab != null)
        {
            CreatePrefabSchool(root, "Jellyfish", AssetDatabase.GetAssetPath(jellyfishPrefab), 8, 0.7f,
                new Vector3(cx, surface - 5f, cz - 2f), new Vector3(7f, 3f, 7f),
                0.04f, 0.15f, 4f, true, 0f, 61);
        }

        W($"鱼群学校：{root.childCount} 组");
    }

    private static void CreateMeshSchool(Transform root, string name, Mesh mesh, Mesh meshFar,
        Material material, Material materialFar, int count, float length, Vector3 center, Vector3 radius,
        float orbitSpeed, float verticalSpeed, float drift, bool upright, float modelYaw, int seed)
    {
        var go = new GameObject(name);
        go.transform.SetParent(root, false);
        var school = go.AddComponent<FishSchool>();
        SetRef(school, "_mesh", mesh);
        SetRef(school, "_meshFar", meshFar);
        SetRef(school, "_material", material);
        SetRef(school, "_materialFar", materialFar);
        SetInt(school, "_count", count);
        SetFloat(school, "_fishLength", length);
        SetVector(school, "_center", center);
        SetVector(school, "_radius", radius);
        SetFloat(school, "_orbitSpeed", orbitSpeed);
        SetFloat(school, "_verticalSpeed", verticalSpeed);
        SetFloat(school, "_schoolDriftRadius", drift);
        SetBool(school, "_upright", upright);
        SetFloat(school, "_modelYaw", modelYaw);
        SetInt(school, "_seed", seed);
    }

    private static void CreatePrefabSchool(Transform root, string name, string prefabPath,
        int count, float length, Vector3 center, Vector3 radius, float orbitSpeed, float verticalSpeed,
        float drift, bool upright, float modelYaw, int seed)
    {
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
        if (prefab == null)
        {
            Debug.LogError($"{Tag} 缺失 prefab {prefabPath}");
            return;
        }

        var go = new GameObject(name);
        go.transform.SetParent(root, false);
        var school = go.AddComponent<FishSchool>();
        SetRef(school, "_prefab", prefab);
        SetInt(school, "_count", count);
        SetFloat(school, "_fishLength", length);
        SetVector(school, "_center", center);
        SetVector(school, "_radius", radius);
        SetFloat(school, "_orbitSpeed", orbitSpeed);
        SetFloat(school, "_verticalSpeed", verticalSpeed);
        SetFloat(school, "_schoolDriftRadius", drift);
        SetBool(school, "_upright", upright);
        SetFloat(school, "_modelYaw", modelYaw);
        SetInt(school, "_seed", seed);
    }

    private static void ConfigureCrestCaustics(Action<string> W)
    {
        var water = AssetDatabase.LoadAssetAtPath<Material>("Packages/com.waveharmonic.crest/Runtime/Materials/Water.mat");
        var volume = AssetDatabase.LoadAssetAtPath<Material>("Packages/com.waveharmonic.crest/Runtime/Materials/Water Volume.mat");
        var caustics = AssetDatabase.LoadAssetAtPath<Texture2D>("Packages/com.waveharmonic.crest/Runtime/Textures/Caustics/Caustics.png");
        if (water == null || caustics == null) { W("FAIL：Crest 水材质或焦散贴图缺失"); return; }

        foreach (var mat in new[] { water, volume })
        {
            if (mat == null) continue;
            mat.SetTexture("_Crest_CausticsTexture", caustics);
            mat.SetTexture("_Crest_CausticsDistortionTexture", null);
            mat.SetFloat("_Crest_CausticsEnabled", 1f);
            mat.SetFloat("_Crest_CausticsTextureScale", 30f);
            mat.SetFloat("_Crest_CausticsScrollSpeed", 0.8f);
            mat.SetFloat("_Crest_CausticsStrength", 1.8f);
            mat.SetFloat("_Crest_CausticsTextureAverage", 0.07f);
            mat.SetFloat("_Crest_CausticsFocalDepth", 3f);
            mat.SetFloat("_Crest_CausticsDepthOfField", 10f);
            mat.SetFloat("_Crest_CausticsMotionBlur", 0f);
            mat.SetFloat("_Crest_CausticsDistortionStrength", 0f);
            mat.SetFloat("_Crest_CausticsForceDistortion", 0f);
            EditorUtility.SetDirty(mat);
        }

        if (water != null) water.EnableKeyword("_CREST_CUSTOM_MESH");
        if (volume != null)
        {
            volume.EnableKeyword("d_Crest_NoMaskColor");
            volume.EnableKeyword("d_Crest_NoMaskDepth");
        }
        W("Crest 水下焦散已启用（无新 pass，2 次已有水下 pass 内采样）");
    }

    private static void DisableCrestCaustics()
    {
        foreach (var path in new[]
                 {
                     "Packages/com.waveharmonic.crest/Runtime/Materials/Water.mat",
                     "Packages/com.waveharmonic.crest/Runtime/Materials/Water Volume.mat",
                 })
        {
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null) continue;
            mat.SetFloat("_Crest_CausticsEnabled", 0f);
            mat.SetTexture("_Crest_CausticsTexture", null);
            mat.SetTexture("_Crest_CausticsDistortionTexture", null);
            EditorUtility.SetDirty(mat);
        }
    }

    private static bool Validate(GameObject root, Action<string> W)
    {
        bool ok = true;
        var benthic = root.transform.Find("Benthic");
        var fish = root.transform.Find("Fish");
        var seabed = root.transform.Find("Seabed Sand");
        var godRays = root.transform.Find("God Rays");
        var bubbles = root.transform.Find("Bubbles");
        var audio = root.transform.Find("Underwater Ambience");
        var controller = root.GetComponent<UnderwaterEcologyController>();
        var controls = root.GetComponent<UnderwaterDemoControls>();

        W($"  root={root.name}，benthic={(benthic ? benthic.childCount : 0)}，fish={(fish ? fish.childCount : 0)}，" +
          $"seabed={(seabed != null)}，godRays={(godRays ? godRays.childCount : 0)}，bubbles={(bubbles ? bubbles.childCount : 0)}，audio={(audio != null)}");
        if (benthic == null || benthic.childCount < 70) ok = false;
        if (fish == null || fish.childCount < 8) ok = false;
        if (seabed == null || seabed.GetComponent<MeshRenderer>() == null) ok = false;
        if (godRays == null || godRays.childCount < 4) ok = false;
        if (bubbles == null || bubbles.childCount < 2) ok = false;
        if (controller == null || controls == null) ok = false;

        int nullMaterials = 0;
        int renderers = 0;
        long triangles = 0;
        var materials = new HashSet<Material>();
        var lod0 = new HashSet<Renderer>();
        foreach (var lg in root.GetComponentsInChildren<LODGroup>(true))
        {
            var lods = lg.GetLODs();
            if (lods.Length == 0) continue;
            foreach (var r in lods[0].renderers) lod0.Add(r);
        }

        foreach (var r in root.GetComponentsInChildren<Renderer>(true))
        {
            renderers++;
            bool count = !(r.GetComponentInParent<LODGroup>() != null) || lod0.Contains(r);
            if (count)
            {
                Mesh mesh = null;
                if (r is SkinnedMeshRenderer smr) mesh = smr.sharedMesh;
                else if (r.TryGetComponent<MeshFilter>(out var filter)) mesh = filter.sharedMesh;
                if (mesh != null) triangles += (long)mesh.triangles.Length / 3;
            }
            foreach (var mat in r.sharedMaterials)
            {
                if (mat == null) nullMaterials++;
                else materials.Add(mat);
            }
        }

        W($"  渲染器={renderers}，唯一材质≈{materials.Count}，LOD0 三角形≈{triangles}，空材质={nullMaterials}");
        if (nullMaterials > 0) ok = false;

        var water = AssetDatabase.LoadAssetAtPath<Material>("Packages/com.waveharmonic.crest/Runtime/Materials/Water.mat");
        bool caustics = water != null && water.GetTexture("_Crest_CausticsTexture") != null
                        && water.GetFloat("_Crest_CausticsEnabled") > 0.5f;
        W($"  Crest 焦散贴图/开关 = {caustics}");
        if (!caustics) ok = false;

        foreach (var school in root.GetComponentsInChildren<FishSchool>(true))
        {
            int count = SerializedInt(school, "_count");
            if (count <= 0) { W($"  FAIL：{school.name} 没有实例配置"); ok = false; }
        }

        return ok;
    }

    private static int SerializedInt(UnityEngine.Object target, string name)
    {
        var so = new SerializedObject(target);
        var prop = so.FindProperty(name);
        return prop != null ? prop.intValue : 0;
    }

    private static void SetRef(UnityEngine.Object target, string name, UnityEngine.Object value)
    {
        var so = new SerializedObject(target);
        var prop = so.FindProperty(name);
        if (prop != null) { prop.objectReferenceValue = value; so.ApplyModifiedPropertiesWithoutUndo(); }
    }

    private static void SetFloat(UnityEngine.Object target, string name, float value)
    {
        var so = new SerializedObject(target);
        var prop = so.FindProperty(name);
        if (prop != null) { prop.floatValue = value; so.ApplyModifiedPropertiesWithoutUndo(); }
    }

    private static void SetInt(UnityEngine.Object target, string name, int value)
    {
        var so = new SerializedObject(target);
        var prop = so.FindProperty(name);
        if (prop != null) { prop.intValue = value; so.ApplyModifiedPropertiesWithoutUndo(); }
    }

    private static void SetBool(UnityEngine.Object target, string name, bool value)
    {
        var so = new SerializedObject(target);
        var prop = so.FindProperty(name);
        if (prop != null) { prop.boolValue = value; so.ApplyModifiedPropertiesWithoutUndo(); }
    }

    private static void SetVector(UnityEngine.Object target, string name, Vector3 value)
    {
        var so = new SerializedObject(target);
        var prop = so.FindProperty(name);
        if (prop != null) { prop.vector3Value = value; so.ApplyModifiedPropertiesWithoutUndo(); }
    }

    [UnityEditor.MenuItem("FishDemo/Underwater/重建水下生态")]
    private static void MenuApply()
    {
        Apply();
    }

    [UnityEditor.MenuItem("FishDemo/Underwater/校验水下生态")]
    private static void MenuReport()
    {
        Report();
    }

    [UnityEditor.MenuItem("FishDemo/Underwater/移除水下生态")]
    private static void MenuRemove()
    {
        Remove();
    }

    private static void Flush(StringBuilder sb, int code, Action<string> W)
    {
        Directory.CreateDirectory(OutDir);
        File.WriteAllText(ResultPath, sb.ToString());
        if (Application.isBatchMode) EditorApplication.Exit(code);
        else Debug.Log($"{Tag} {(code == 0 ? "PASS" : "FAIL")}（编辑器模式不退出，结果见 {ResultPath}）");
    }
}
