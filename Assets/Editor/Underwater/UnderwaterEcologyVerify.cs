using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using FishDemo.Gameplay;

public static class UnderwaterEcologyVerify
{
    private const string Tag = "[UnderwaterEcologyVerify]";
    private const string WorldScenePath = "Assets/Scenes/World.unity";
    private const string RootName = "Underwater Ecology";
    private const string OutDir = "Logs/Underwater";
    private const string ResultPath = OutDir + "/underwater-ecology.txt";

    public static void Run()
    {
        var sb = new StringBuilder();
        void W(string s) { Debug.Log($"{Tag} {s}"); sb.AppendLine(s); }

        try
        {
            Directory.CreateDirectory(OutDir);
            var scene = EditorSceneManager.OpenScene(WorldScenePath, OpenSceneMode.Single);
            ChunkLoadScope.LoadRelevant(ChunkLoadScope.Area.FromLake(20f), W);
            EditorSceneManager.SetActiveScene(EditorSceneManager.GetSceneByPath(WorldScenePath));

            var root = GameObject.Find(RootName);
            if (root == null)
            {
                W($"FAIL：场景里找不到 {RootName}");
                Flush(sb, 2);
                return;
            }

            bool ok = true;
            var controller = root.GetComponent<UnderwaterEcologyController>();
            var controls = root.GetComponent<UnderwaterDemoControls>();
            var benthic = root.transform.Find("Benthic");
            var fish = root.transform.Find("Fish");
            var seabed = root.transform.Find("Seabed Sand");
            var godRays = root.transform.Find("God Rays");
            var bubbles = root.transform.Find("Bubbles");
            var ambience = root.transform.Find("Underwater Ambience");

            W($"root={root.name}");
            W($"controller={(controller != null)} controls={(controls != null)}");
            W($"benthic={(benthic != null ? benthic.childCount : -1)} fish={(fish != null ? fish.childCount : -1)} " +
              $"godRays={(godRays != null ? godRays.childCount : -1)} bubbles={(bubbles != null ? bubbles.childCount : -1)} " +
              $"seabed={(seabed != null)} ambience={(ambience != null)}");

            if (controller == null || controls == null) ok = false;
            if (benthic == null || benthic.childCount < 70) ok = false;
            if (fish == null || fish.childCount < 8) ok = false;
            if (seabed == null) ok = false;
            if (godRays == null || godRays.childCount < 4) ok = false;
            if (bubbles == null || bubbles.childCount < 2) ok = false;
            if (ambience == null || ambience.GetComponent<AudioSource>() == null) ok = false;

            int renderers = 0;
            int nullMaterialSlots = 0;
            int unsupportedShaders = 0;
            int missingScripts = 0;
            long triangles = 0;
            var materials = new HashSet<Material>();
            var shaders = new HashSet<Shader>();

            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
            {
                renderers++;
                Mesh mesh = null;
                if (r is SkinnedMeshRenderer smr) mesh = smr.sharedMesh;
                else if (r.TryGetComponent<MeshFilter>(out var filter)) mesh = filter.sharedMesh;
                if (mesh != null) triangles += (long)mesh.triangles.Length / 3;
                foreach (var mat in r.sharedMaterials)
                {
                    if (mat == null) { nullMaterialSlots++; continue; }
                    materials.Add(mat);
                    if (mat.shader == null)
                    {
                        unsupportedShaders++;
                        continue;
                    }
                    shaders.Add(mat.shader);
                    if (SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Null
                        && !mat.shader.isSupported) unsupportedShaders++;
                }
            }

            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            {
                if (t.GetComponents<MonoBehaviour>().Any(c => c == null)) missingScripts++;
            }

            W($"renderers={renderers} materials={materials.Count} shaders={shaders.Count} " +
              $"triangles≈{triangles} nullMaterialSlots={nullMaterialSlots} unsupportedShaders={unsupportedShaders} missingScripts={missingScripts}");
            if (nullMaterialSlots > 0 || unsupportedShaders > 0 || missingScripts > 0) ok = false;

            int schools = 0;
            int expectedFish = 0;
            var vatShaders = new HashSet<Shader>();
            foreach (var school in root.GetComponentsInChildren<FishSchool>(true))
            {
                schools++;
                int count = SerializedInt(school, "_count");
                expectedFish += count;
                var prefab = SerializedRef(school, "_prefab");
                var mesh = SerializedRef(school, "_mesh");
                if (prefab == null && mesh == null)
                {
                    W($"FAIL：{school.name} 既没有 prefab 也没有 mesh");
                    ok = false;
                }

                foreach (var name in new[] { "_material", "_materialFar" })
                {
                    if (SerializedRef(school, name) is Material mat && mat.shader != null)
                        vatShaders.Add(mat.shader);
                }
            }
            W($"schools={schools} expectedFish={expectedFish}");
            if (schools < 8 || expectedFish < 200) ok = false;

            var water = AssetDatabase.LoadAssetAtPath<Material>("Packages/com.waveharmonic.crest/Runtime/Materials/Water.mat");
            bool caustics = water != null
                            && water.GetTexture("_Crest_CausticsTexture") != null
                            && water.GetFloat("_Crest_CausticsEnabled") > 0.5f;
            W($"crestCaustics={caustics} causticsTexture={(water != null ? water.GetTexture("_Crest_CausticsTexture")?.name : "null")}");
            if (!caustics) ok = false;

            W($"vatShaders={(vatShaders.Count > 0 ? string.Join(",", vatShaders.Select(s => s.name)) : "none")}");
            if (vatShaders.Count == 0) ok = false;

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

    private static int SerializedInt(UnityEngine.Object target, string name)
    {
        var so = new SerializedObject(target);
        var p = so.FindProperty(name);
        return p != null ? p.intValue : 0;
    }

    private static UnityEngine.Object SerializedRef(UnityEngine.Object target, string name)
    {
        var so = new SerializedObject(target);
        var p = so.FindProperty(name);
        return p != null ? p.objectReferenceValue : null;
    }

    private static void Flush(StringBuilder sb, int code)
    {
        Directory.CreateDirectory(OutDir);
        File.WriteAllText(ResultPath, sb.ToString());
        EditorApplication.Exit(code);
    }
}
