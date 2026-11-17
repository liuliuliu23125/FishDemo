using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace FishDemo.EditorTools
{
    public static class WorldProbe
    {
        private const string Tag = "[WorldProbe]";
        private const string ResultPath = "Logs/Grass/world-probe.txt";
        private const string WorldScenePath = "Assets/Scenes/World.unity";

        [MenuItem("FishDemo/Rendering/③ 探测主相机附近地表", false, 102)]
        public static void Run()
        {
            var sb = new StringBuilder();
            void W(string s) { Debug.Log($"{Tag} {s}"); sb.AppendLine(s); }

            EditorSceneManager.OpenScene(WorldScenePath, OpenSceneMode.Single);

            var chunkPaths = new System.Collections.Generic.List<string>();
            foreach (var s in EditorBuildSettings.scenes)
            {
                if (!s.enabled) continue;
                if (s.path.EndsWith("World.unity")) continue;
                chunkPaths.Add(s.path);
            }
            W($"Build Settings 中 chunk 场景数 = {chunkPaths.Count}");

            int opened = 0;
            foreach (var path in chunkPaths)
            {
                if (!File.Exists(path)) continue;
                EditorSceneManager.OpenScene(path, OpenSceneMode.Additive);
                opened++;
            }
            W($"已加载 chunk 场景 = {opened}");

            var cam = Camera.main;
            if (cam == null)
            {
                W("找不到 MainCamera");
                Flush(sb);
                return;
            }

            Vector3 cp = cam.transform.position;
            Vector3 fwd = cam.transform.forward;
            W($"相机位置 = {cp}");
            W($"相机朝向 = {fwd}");
            W($"相机 FOV = {cam.fieldOfView}, far = {cam.farClipPlane}");

            var terrains = Object.FindObjectsOfType<Terrain>();
            W($"场景内 Terrain 数 = {terrains.Length}");
            foreach (var t in terrains)
            {
                if (t.terrainData == null) continue;
                W($"  {t.name}: pos={t.transform.position} size={t.terrainData.size}");
            }

            W("");
            W("沿视线采样（从相机向前，每 100m 一个点）：");
            foreach (float d in new[] { 100f, 200f, 300f, 400f, 500f, 600f, 800f, 1000f })
            {
                Vector3 p = cp + fwd * d;
                string hit = "无地形";
                float h = float.NaN;
                foreach (var t in terrains)
                {
                    if (t.terrainData == null) continue;
                    Vector3 tp = t.transform.position;
                    Vector3 ts = t.terrainData.size;
                    if (p.x >= tp.x && p.x <= tp.x + ts.x && p.z >= tp.z && p.z <= tp.z + ts.z)
                    {
                        h = t.SampleHeight(new Vector3(p.x, 0f, p.z)) + tp.y;
                        hit = t.name;
                        break;
                    }
                }
                W($"  d={d,6:F0}  交点=({p.x,8:F1},{p.y,8:F1},{p.z,8:F1})  地面高={(float.IsNaN(h) ? "N/A" : h.ToString("F1")),8}  地形={hit}");
            }

            float belowH = float.NaN;
            foreach (var t in terrains)
            {
                if (t.terrainData == null) continue;
                Vector3 tp = t.transform.position;
                Vector3 ts = t.terrainData.size;
                if (cp.x >= tp.x && cp.x <= tp.x + ts.x && cp.z >= tp.z && cp.z <= tp.z + ts.z)
                {
                    belowH = t.SampleHeight(new Vector3(cp.x, 0f, cp.z)) + tp.y;
                    break;
                }
            }
            W("");
            W($"相机正下方地面高 = {(float.IsNaN(belowH) ? "N/A" : belowH.ToString("F1"))}");

            Flush(sb);
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
