using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using ProceduralWorlds.GTS;

public static class GtsConvertOne
{
    private const string Tag = "[GTS-One]";
    private const string ChunkDir = "Assets/Scenes/World/Chunks";
    private const string ProfilePath = "Assets/Gaia User Data/GTS World Profile.asset";
    private const string GtsUserDataRoot = "Assets/GTS User Data";
    private const string LogDir = "Logs/Gaia/gts-per-chunk";

    private const int SubTiles = 1;
    private const SaveResolution Resolution = SaveResolution.Half;
    private const int LodCount = 1;

    public static void Run()
    {
        string chunkName = GetArg("-gtsChunk");
        bool isFirst = GetArg("-gtsFirst") == "1";

        var sb = new StringBuilder();
        void W(string s) { Debug.Log($"{Tag} {s}"); sb.AppendLine(s); }
        void Flush(int code)
        {
            Directory.CreateDirectory(LogDir);
            string safe = string.IsNullOrEmpty(chunkName) ? "unknown" : Sanitize(chunkName);
            File.WriteAllText(Path.Combine(LogDir, safe + ".txt"), sb.ToString());
            EditorApplication.Exit(code);
        }

        if (string.IsNullOrEmpty(chunkName))
        {
            W("缺少 -gtsChunk 参数");
            Flush(1);
            return;
        }

        string scenePath = $"{ChunkDir}/{chunkName}.unity";
        W($"chunk = {chunkName}");
        W($"isFirst = {isFirst}");

        if (!File.Exists(scenePath)) { W($"场景不存在：{scenePath}"); Flush(1); return; }

        try
        {
            var scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);
            var terrains = UnityEngine.Object.FindObjectsOfType<Terrain>();
            if (terrains.Length == 0) { W("无 Terrain"); Flush(1); return; }

            var terrain = terrains[0];
            W($"terrain={terrain.name} layers={terrain.terrainData.terrainLayers?.Length ?? 0} " +
              $"trees={terrain.terrainData.treeInstanceCount}");

            var profile = AssetDatabase.LoadAssetAtPath<GTSProfile>(ProfilePath);
            if (profile == null)
            {
                if (!isFirst) { W("Profile 不存在，但本块不是首块；请先跑首块"); Flush(1); return; }
                Directory.CreateDirectory(Path.GetDirectoryName(ProfilePath));
                var tmp = ScriptableObject.CreateInstance<GTSProfile>();
                AssetDatabase.CreateAsset(tmp, ProfilePath);
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
                profile = AssetDatabase.LoadAssetAtPath<GTSProfile>(ProfilePath);
                W($"已创建 Profile：{ProfilePath}");
            }

            profile.meshSettings.saveResolution = Resolution;
            profile.meshSettings.subTiles = SubTiles;
            profile.meshSettings.lodCount = LodCount;
            EditorUtility.SetDirty(profile);
            AssetDatabase.SaveAssets();

            foreach (var root in scene.GetRootGameObjects().ToList())
                if (root.name.StartsWith("GTS_MeshTerrains"))
                    UnityEngine.Object.DestroyImmediate(root);
            foreach (var t in terrains)
            {
                var oldMeshNode = t.transform.Find("GTS_Mesh");
                if (oldMeshNode != null) UnityEngine.Object.DestroyImmediate(oldMeshNode.gameObject);
            }

            var gtsTerrain = terrain.GetComponent<GTSTerrain>();
            if (gtsTerrain == null) gtsTerrain = terrain.gameObject.AddComponent<GTSTerrain>();
            gtsTerrain.profile = profile;

            W("ApplyProfile…");
            gtsTerrain.ApplyProfile();
            AssetDatabase.SaveAssets();

            W("UpdateAllTextures / SaveAllTextures…");
            gtsTerrain.UpdateAllTextures();
            gtsTerrain.SaveAllTextures();
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            W($"纹理绑定期望：_Control0={Has(terrain, "_Control0")}, _AlbedoArray={Has(terrain, "_AlbedoArray")}");

            if (isFirst && profile.TextureArraysEmpty)
            {
                W($"生成全局纹理数组（gtsLayers={profile.gtsLayers.Count}）…");
                profile.CreateTextureArrays();
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
                W($"纹理数组就绪 = {!profile.TextureArraysEmpty}");
            }

            if (!profile.TextureArraysEmpty && terrain.materialTemplate != null)
            {
                var mat = terrain.materialTemplate;
                if (mat.GetTexture("_AlbedoArray") == null && profile.textureArraySettings.albedoArray != null)
                {
                    mat.SetTexture("_AlbedoArray", profile.textureArraySettings.albedoArray);
                    EditorUtility.SetDirty(mat);
                    W("  已回绑 _AlbedoArray");
                }
                if (mat.GetTexture("_NormalArray") == null && profile.textureArraySettings.normalArray != null)
                {
                    mat.SetTexture("_NormalArray", profile.textureArraySettings.normalArray);
                    EditorUtility.SetDirty(mat);
                    W("  已回绑 _NormalArray");
                }
                AssetDatabase.SaveAssets();
            }

            W("ConvertToMesh…");
            profile.ConvertToMesh();
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            string sceneDir = $"{GtsUserDataRoot}/Scenes/{chunkName}";
            var meshes = Directory.Exists(sceneDir)
                ? Directory.GetFiles(sceneDir, "*.mesh", SearchOption.AllDirectories)
                : Array.Empty<string>();
            long tris = 0, verts = 0;
            foreach (var m in meshes)
            {
                var mesh = AssetDatabase.LoadAssetAtPath<Mesh>(m.Replace('\\', '/'));
                if (mesh == null) continue;
                tris += mesh.triangles.Length / 3;
                verts += mesh.vertexCount;
            }
            W($"mesh={meshes.Length}, verts={verts}, tris={tris}");
            W($"材质贴图：_Control0={Has(terrain, "_Control0")}, _AlbedoArray={Has(terrain, "_AlbedoArray")}, " +
              $"_SplatmapIndex={Has(terrain, "_SplatmapIndex")}");

            bool ok = meshes.Length > 0;
            W($"==== 结论：{(ok ? "PASS" : "FAIL")} ====");
            Flush(ok ? 0 : 2);
        }
        catch (Exception ex)
        {
            W($"异常：{ex}");
            W("==== 结论：FAIL ====");
            Flush(3);
        }
    }

    private static bool Has(Terrain t, string prop)
    {
        var m = t.materialTemplate;
        return m != null && m.GetTexture(prop) != null;
    }

    private static string GetArg(string name)
    {
        var args = Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == name) return args[i + 1];
        return null;
    }

    private static string Sanitize(string s)
    {
        foreach (char c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        return s;
    }
}
