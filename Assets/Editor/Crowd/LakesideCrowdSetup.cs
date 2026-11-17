using System.Collections.Generic;
using System.IO;
using System.Text;
using FishDemo.Gameplay;
using Unity.Scenes;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

public static class LakesideCrowdSetup
{
    private const string Tag = "[LakesideCrowd]";
    private const string WorldScenePath = "Assets/Scenes/World.unity";
    private const string AuthoringScenePath = "Assets/Scenes/LakesideCrowd.unity";
    private static readonly string[] PrefabPaths =
    {
        "Assets/GPUECSAnimationBaker/Samples/Characters/Ch19/BakedAssets_Ch19/Ch19_GpuEcsAnimator.prefab",
        "Assets/GPUECSAnimationBaker/Samples/Characters/Ch14/BakedAssets_Ch14/Ch14_GpuEcsAnimator.prefab",
        "Assets/GPUECSAnimationBaker/Samples/Characters/Ch09/BakedAssets_Ch09/Ch09_GpuEcsAnimator.prefab",
        "Assets/GPUECSAnimationBaker/Samples/Characters/Ch46/BakedAssets_Ch46/Ch46_GpuEcsAnimator.prefab"
    };
    private const float CrowdScale = 4f;
    private const string RootName = "Lakeside Crowd";
    private const string SubSceneName = "Lakeside Crowd (SubScene)";
    private const string OutDir = "Logs/Crowd";
    private const string ResultPath = OutDir + "/lakeside-setup.txt";

    [MenuItem("FishDemo/World/③ 布置湖边人群 (1人)")]
    public static void ApplyOne() { Run(1); }

    [MenuItem("FishDemo/World/③ 布置湖边人群 (30人)")]
    public static void ApplyThirty() { Run(30); }

    [MenuItem("FishDemo/World/③ 布置湖边人群 (200人)")]
    public static void ApplyCrowd() { Run(200); }

    private static void Run(int count)
    {
        var sb = new StringBuilder();
        void W(string s) { Debug.Log($"{Tag} {s}"); sb.AppendLine(s); }

        try
        {
            Directory.CreateDirectory(OutDir);
            if (count <= 0) throw new System.ArgumentOutOfRangeException(nameof(count));

            Terrain[] terrains = LoadTerrains(out List<string> chunkPaths, W);
            var positions = new List<Vector3>(count);
            var yaws = new List<float>(count);
            for (int i = 0; i < count; i++)
            {
                Vector3 position = count == 1
                    ? SampleAt(terrains, 190f, 191f, W)
                    : SampleShoreAuto(terrains, 360f * i / count, W);
                positions.Add(position);
                yaws.Add(Mathf.Atan2(LakeCarve.Center.x - position.x,
                                     LakeCarve.Center.y - position.z) * Mathf.Rad2Deg);
            }
            CloseChunks(chunkPaths);

            var prefabObjects = new List<GameObject>();
            for (int i = 0; i < PrefabPaths.Length; i++)
            {
                GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPaths[i]);
                if (prefab == null)
                {
                    W($"警告：prefab 不存在 {PrefabPaths[i]}");
                    continue;
                }
                prefabObjects.Add(prefab);
            }
            if (prefabObjects.Count == 0) throw new FileNotFoundException("没有可用 prefab");

            Scene authoring = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var root = new GameObject(RootName);
            var behaviour = root.AddComponent<LakesideCrowdBehaviour>();
            behaviour.prefabs = prefabObjects.ToArray();
            behaviour.positions = positions.ToArray();
            behaviour.yaws = yaws.ToArray();
            behaviour.count = count;
            behaviour.scale = CrowdScale;

            Directory.CreateDirectory(Path.GetDirectoryName(AuthoringScenePath));
            EditorSceneManager.SaveScene(authoring, AuthoringScenePath);
            AssetDatabase.Refresh();
            AssetDatabase.SaveAssets();

            Scene world = EditorSceneManager.OpenScene(WorldScenePath, OpenSceneMode.Single);
            GameObject subSceneObject = GameObject.Find(SubSceneName);
            if (subSceneObject == null) subSceneObject = new GameObject(SubSceneName);
            SubScene subScene = subSceneObject.GetComponent<SubScene>();
            if (subScene == null) subScene = subSceneObject.AddComponent<SubScene>();
            subScene.SceneAsset = AssetDatabase.LoadAssetAtPath<SceneAsset>(AuthoringScenePath);
            subScene.AutoLoadScene = true;
            EditorSceneManager.MarkSceneDirty(world);
            EditorSceneManager.SaveScene(world);

            for (int i = 0; i < prefabObjects.Count; i++)
                W($"Prefab[{i}] = {PrefabPaths[i]}");
            W($"Count = {count}");
            W($"Scale = {CrowdScale}");
            W($"Authoring = {AuthoringScenePath}");
            W($"SubScene = {SubSceneName} / {WorldScenePath}");
            for (int i = 0; i < positions.Count; i++)
                W($"  [{i}] ({positions[i].x:F2}, {positions[i].y:F2}, {positions[i].z:F2}) yaw={yaws[i]:F1}");
            W("==== 结论：PASS ====");
            Flush(sb, 0);
        }
        catch (System.Exception ex)
        {
            W($"异常：{ex}");
            Flush(sb, 1);
        }
    }

    private static Terrain[] LoadTerrains(out List<string> chunkPaths, System.Action<string> W)
    {
        EditorSceneManager.OpenScene(WorldScenePath, OpenSceneMode.Single);
        chunkPaths = new List<string>();
        foreach (EditorBuildSettingsScene s in EditorBuildSettings.scenes)
        {
            if (!s.enabled) continue;
            if (s.path.EndsWith("World.unity")) continue;
            if (File.Exists(s.path)) chunkPaths.Add(s.path);
        }
        foreach (string path in chunkPaths)
            EditorSceneManager.OpenScene(path, OpenSceneMode.Additive);
        W($"加载 chunk = {chunkPaths.Count}");
        Terrain[] terrains = UnityEngine.Object.FindObjectsOfType<Terrain>();
        W($"Terrain = {terrains.Length}");
        return terrains;
    }

    private static Vector3 SampleAt(Terrain[] terrains, float angleDegrees, float radius,
        System.Action<string> W)
    {
        float cx = LakeCarve.Center.x;
        float cz = LakeCarve.Center.y;
        float rad = angleDegrees * Mathf.Deg2Rad;
        float px = cx + Mathf.Sin(rad) * radius;
        float pz = cz + Mathf.Cos(rad) * radius;
        float py = SampleWorldHeight(terrains, px, pz);
        if (float.IsNaN(py)) py = LakeCarve.WaterLevel + 0.5f;
        W($"岸点 angle={angleDegrees:F1} r={radius:F1} height={py:F2}（水位 {LakeCarve.WaterLevel:F0}）");
        return new Vector3(px, py, pz);
    }

    private static Vector3 SampleShoreAuto(Terrain[] terrains, float angleDegrees,
        System.Action<string> W)
    {
        float cx = LakeCarve.Center.x;
        float cz = LakeCarve.Center.y;
        float rad = angleDegrees * Mathf.Deg2Rad;
        float dx = Mathf.Sin(rad);
        float dz = Mathf.Cos(rad);
        float bestRadius = LakeCarve.Radius + 1f;
        float bestHeight = float.MaxValue;
        for (float r = LakeCarve.Radius + 1f; r <= LakeCarve.Radius + 15f; r += 0.5f)
        {
            float h = SampleWorldHeight(terrains, cx + dx * r, cz + dz * r);
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
        if (bestHeight > LakeCarve.WaterLevel + 10f)
            W($"警告：angle={angleDegrees:F1} 最低干燥点仍高于水位 +{bestHeight - LakeCarve.WaterLevel:F2}m，角色会站在陡坎上");
        W($"岸点 angle={angleDegrees:F1} r={bestRadius:F1} height={bestHeight:F2}（水位 {LakeCarve.WaterLevel:F0}）");
        return new Vector3(cx + dx * bestRadius, bestHeight, cz + dz * bestRadius);
    }

    private static float SampleWorldHeight(Terrain[] terrains, float x, float z)
    {
        for (int i = 0; i < terrains.Length; i++)
        {
            Terrain t = terrains[i];
            if (t == null || t.terrainData == null) continue;
            Vector3 p = t.transform.position;
            Vector3 size = t.terrainData.size;
            if (x < p.x || x > p.x + size.x || z < p.z || z > p.z + size.z) continue;
            return p.y + t.SampleHeight(new Vector3(x, 0f, z));
        }
        return float.NaN;
    }

    private static void CloseChunks(List<string> chunkPaths)
    {
        for (int i = 0; i < chunkPaths.Count; i++)
        {
            Scene scene = SceneManager.GetSceneByPath(chunkPaths[i]);
            if (scene.isLoaded) EditorSceneManager.CloseScene(scene, true);
        }
    }

    private static void Flush(StringBuilder sb, int code)
    {
        Directory.CreateDirectory(OutDir);
        File.WriteAllText(ResultPath, sb.ToString());
        if (Application.isBatchMode) EditorApplication.Exit(code);
    }
}
