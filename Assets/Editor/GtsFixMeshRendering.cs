using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using ProceduralWorlds.GTS;

public static class GtsFixMeshRendering
{
    private const string Tag = "[GTS-Fix]";
    private const string ResultPath = "Logs/Gaia/gts-fix-rendering.txt";
    private const string ChunkDir = "Assets/Scenes/World/Chunks";
    private const string WorldScenePath = "Assets/Scenes/World.unity";
    private const string ProfilePath = "Assets/Gaia User Data/GTS World Profile.asset";

    public static void RunAll()
    {
        var sb = new StringBuilder();
        void W(string s) { Debug.Log($"{Tag} {s}"); sb.AppendLine(s); }

        var profile = AssetDatabase.LoadAssetAtPath<GTSProfile>(ProfilePath);
        if (profile == null) { W("找不到 Profile"); Flush(sb, 1); return; }
        W($"Profile = {profile.name}, gtsLayers = {profile.gtsLayers.Count}");

        W("");
        W("=== 检查 gtsLayers 的 tileSize ===");
        foreach (var l in profile.gtsLayers)
            W($"  {l.name}: tileSize=({l.tileSize.x},{l.tileSize.y})");

        var activeChunks = LoadActiveChunkPaths();
        W($"活跃 chunk = {activeChunks.Count}");

        int ok = 0, fail = 0, fixedTiles = 0;
        foreach (string chunk in activeChunks.OrderBy(x => x))
        {
            try
            {
                string scenePath = $"{ChunkDir}/{chunk}.unity";
                if (!File.Exists(scenePath)) { fail++; continue; }

                var scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);
                var terrain = UnityEngine.Object.FindObjectsOfType<Terrain>().FirstOrDefault();
                if (terrain == null) { fail++; continue; }

                var meshRoot = terrain.transform.Find("GTS_Mesh");
                if (meshRoot == null) { W($"  {chunk}: 无 GTS_Mesh 节点"); fail++; continue; }

                var rends = meshRoot.GetComponentsInChildren<MeshRenderer>(true);
                if (rends.Length == 0) { W($"  {chunk}: 无 MeshRenderer"); fail++; continue; }

                Vector3 worldSize = terrain.terrainData.size;

                int n = 0;
                foreach (var mr in rends)
                {
                    var go = mr.gameObject;

                    var old = go.GetComponent<GTSMesh>();
                    if (old != null) UnityEngine.Object.DestroyImmediate(old);

                    var gtsMesh = go.AddComponent<GTSMesh>();
                    gtsMesh.profile = profile;
                    gtsMesh.material = mr.sharedMaterial;
                    gtsMesh.colormap = null;

                    Vector3 subSize = worldSize;
                    var mf = go.GetComponent<MeshFilter>();
                    if (mf != null && mf.sharedMesh != null)
                    {
                        var b = mf.sharedMesh.bounds.size;
                        if (b.x > 0.001f && b.z > 0.001f) subSize = b;
                    }
                    gtsMesh.SetWorldSizeOverride(subSize);

                    gtsMesh.Refresh();

                    if (go.GetComponent<GtsRuntimeDataBootstrap>() == null)
                        go.AddComponent<GtsRuntimeDataBootstrap>().profile = profile;
                    n++;
                }

                EditorSceneManager.MarkSceneDirty(scene);
                EditorSceneManager.SaveScene(scene);
                W($"  {chunk}: GTSMesh x{n}，worldSize={worldSize}");
                fixedTiles += n;
                ok++;
            }
            catch (Exception ex)
            {
                W($"  {chunk}: 异常 {ex.GetType().Name}: {ex.Message}");
                fail++;
            }
        }

        W("");
        W($"===== 汇总：成功 chunk={ok}，失败={fail}，修复 subTile={fixedTiles} =====");
        bool pass = fail == 0 && ok > 0;
        W($"==== 结论：{(pass ? "PASS" : "FAIL")} ====");
        Flush(sb, pass ? 0 : 2);
    }

    private static HashSet<string> LoadActiveChunkPaths()
    {
        var r = new HashSet<string>();
        EditorSceneManager.OpenScene(WorldScenePath, OpenSceneMode.Single);
        foreach (var c in UnityEngine.Object.FindObjectsOfType<SECTR_Chunk>())
            if (!string.IsNullOrEmpty(c.NodeName))
                r.Add(Path.GetFileNameWithoutExtension(c.NodeName));
        return r;
    }

    private static void Flush(StringBuilder sb, int code)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ResultPath));
        File.WriteAllText(ResultPath, sb.ToString());
        EditorApplication.Exit(code);
    }
}
