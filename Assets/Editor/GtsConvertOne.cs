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

/// <summary>
/// GTS 单 chunk 转换（供外部批处理逐块调用，一个 Unity 进程处理一块）。
///
/// ── 为什么必须一进程一块 ──
/// GTS 的 CreateTextureArrays() → StartProfile() → GetGTSComponents() 会遍历
/// 当前进程内**所有**场景的 GTSComponent。批处理连续 OpenScene(Single) 时，
/// 上一轮场景卸载后组件虽已销毁，但 GTS 内部缓存（gtsLayers / 组件列表）仍持有引用，
/// 触发 MissingReferenceException。实测在单进程内无论怎么刷新引用都躲不开。
/// 用独立进程隔离是最稳的做法：每块转换完进程即退出，不留任何跨块状态。
///
/// ── 前置条件 ──
/// Profile 与纹理数组由第一块（IsFirst=true）生成并落盘，后续块直接复用。
/// 所以必须先跑 index=0 的块，再跑其余（或按顺序跑）。
///
/// 用法：
///   Unity.exe -batchmode -projectPath <proj> -executeMethod GtsConvertOne.Run \
///       -gtsChunk "World_SECTR Terrain_0_0-... Sector" -gtsFirst 1
///   环境变量方式不可靠（Unity 会过滤），改用 -gtsChunk / -gtsFirst 自定义参数。
/// </summary>
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
            // 本进程只打开这一个场景，不跨块，故不会有跨场景引用问题
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

            // 幂等：清本 chunk 旧产物
            foreach (var root in scene.GetRootGameObjects().ToList())
                if (root.name.StartsWith("GTS_MeshTerrains"))
                    UnityEngine.Object.DestroyImmediate(root);

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

            // ── 非首块必须显式回绑全局纹理数组 ──
            // 每块都会生成一个新材质，但 CreateTextureArrays 只在首块跑一次（数组是所有 chunk 共享的）。
            // 新材质不会自动继承 Profile 上的 albedo/normal array，必须手动 SetTexture，
            // 否则 shader 采到空数组 → 走 FallbackError 或整片黑。
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

    /// <summary>从命令行读自定义参数。Unity 会忽略未知的 -xxx，但 args 里仍能取到。</summary>
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
