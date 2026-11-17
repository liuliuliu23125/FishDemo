using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using FishDemo.Rendering;

namespace FishDemo.EditorTools
{
    public static class GrassSceneSetup
    {
        private const string Tag = "[GrassScene]";
        private const string ResultPath = "Logs/Grass/scene-setup.txt";
        private const string WorldScenePath = "Assets/Scenes/World.unity";
        private const string RootName = "GrassBRG (M1)";

        [MenuItem("FishDemo/Rendering/⑤ 把 M1 草放进 World 场景", false, 105)]
        public static void Run()
        {
            var sb = new StringBuilder();
            void W(string s) { Debug.Log($"{Tag} {s}"); sb.AppendLine(s); }

            var scene = EditorSceneManager.OpenScene(WorldScenePath, OpenSceneMode.Single);

            foreach (var existing in scene.GetRootGameObjects())
            {
                if (existing.name == RootName)
                {
                    Object.DestroyImmediate(existing);
                    W($"已移除旧节点 {RootName}");
                }
            }

            var cam = Camera.main;
            if (cam == null)
            {
                W("找不到 MainCamera，中止");
                Flush(sb);
                return;
            }

            Vector3 fwd = cam.transform.forward;

            const float lookDownAngleDeg = 50f;
            float horizontal = cam.transform.position.y / Mathf.Tan(lookDownAngleDeg * Mathf.Deg2Rad);
            Vector3 flatFwd = new Vector3(fwd.x, 0f, fwd.z).normalized;
            Vector3 probeXZ = cam.transform.position + flatFwd * horizontal;

            float groundY = SampleGround(probeXZ.x, probeXZ.z, cam.transform.position.y - 93f);
            float actualDrop = cam.transform.position.y - groundY;
            horizontal = actualDrop / Mathf.Tan(lookDownAngleDeg * Mathf.Deg2Rad);
            probeXZ = cam.transform.position + flatFwd * horizontal;
            groundY = SampleGround(probeXZ.x, probeXZ.z, groundY);

            Vector3 anchorPos = new Vector3(probeXZ.x, groundY, probeXZ.z);

            var root = new GameObject(RootName);
            root.transform.position = anchorPos;

            var mgr = root.AddComponent<GrassBRGManager>();
            mgr.ConfigureForValidation(1000, 0.6f, 0.6f);
            mgr.SetAnchor(root.transform, true);

            var so = new SerializedObject(mgr);
            so.FindProperty("_scaleMultiplier").floatValue = 8f;
            so.ApplyModifiedPropertiesWithoutUndo();

            W($"相机 = {cam.transform.position}, forward = {fwd}");
            W($"草锚点（视锥下边缘内侧）= {anchorPos}，地面高 {groundY:F1}");
            W($"锚点距相机 = {Vector3.Distance(cam.transform.position, anchorPos):F1} m");
            W("已挂 GrassBRGManager：1000 实例 / 间距 0.6m / 缩放 ×8 / 贴地");

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            AssetDatabase.SaveAssets();

            W($"已保存 {WorldScenePath}");
            Flush(sb);
        }

        private static float SampleGround(float x, float z, float fallbackY)
        {
            foreach (var s in EditorBuildSettings.scenes)
            {
                if (!s.enabled || s.path.EndsWith("World.unity")) continue;
                if (!File.Exists(s.path)) continue;
                EditorSceneManager.OpenScene(s.path, OpenSceneMode.Additive);
            }

            var terrains = Object.FindObjectsOfType<Terrain>();
            foreach (var t in terrains)
            {
                if (t == null || t.terrainData == null) continue;
                Vector3 tp = t.transform.position;
                Vector3 ts = t.terrainData.size;
                if (x < tp.x || x > tp.x + ts.x || z < tp.z || z > tp.z + ts.z)
                    continue;
                return t.SampleHeight(new Vector3(x, 0f, z)) + tp.y;
            }

            return fallbackY;
        }

        private static void Flush(StringBuilder sb)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ResultPath));
            File.WriteAllText(ResultPath, sb.ToString());
            if (Application.isBatchMode)
                EditorApplication.Exit(0);
        }
    }
}
