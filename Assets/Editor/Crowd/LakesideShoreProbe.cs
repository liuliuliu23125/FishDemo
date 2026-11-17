using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class LakesideShoreProbe
{
    private const string Tag = "[ShoreProbe]";
    private const string WorldScenePath = "Assets/Scenes/World.unity";
    private const string OutDir = "Logs/Crowd";
    private const string ResultPath = OutDir + "/shore-probe.txt";

    [MenuItem("FishDemo/World/③ 湖边岸线探针")]
    public static void Run()
    {
        var sb = new StringBuilder();
        void W(string s) { Debug.Log($"{Tag} {s}"); sb.AppendLine(s); }

        try
        {
            Directory.CreateDirectory(OutDir);
            EditorSceneManager.OpenScene(WorldScenePath, OpenSceneMode.Single);
            var chunkPaths = new List<string>();
            foreach (EditorBuildSettingsScene s in EditorBuildSettings.scenes)
            {
                if (!s.enabled) continue;
                if (s.path.EndsWith("World.unity")) continue;
                if (File.Exists(s.path)) chunkPaths.Add(s.path);
            }
            foreach (string path in chunkPaths)
                EditorSceneManager.OpenScene(path, OpenSceneMode.Additive);
            Terrain[] terrains = UnityEngine.Object.FindObjectsOfType<Terrain>();
            W($"chunk={chunkPaths.Count} terrain={terrains.Length}");

            float SampleWorld(float x, float z)
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

            float cx = LakeCarve.Center.x;
            float cz = LakeCarve.Center.y;
            float[] radii = { 188f, 189.5f, 190f, 190.5f, 191f, 195f, 200f, 210f, 220f, 240f };
            W($"水位={LakeCarve.WaterLevel:F1} 半径={LakeCarve.Radius:F1}");
            W("angle," + string.Join(",", System.Array.ConvertAll(radii, r => $"r{r:F1}")));
            float best = float.MaxValue;
            float bestAngle = 0f;
            for (int deg = 0; deg < 360; deg += 5)
            {
                float rad = deg * Mathf.Deg2Rad;
                float dx = Mathf.Sin(rad);
                float dz = Mathf.Cos(rad);
                var values = new List<string>();
                float outside = float.NaN;
                for (int i = 0; i < radii.Length; i++)
                {
                    float h = SampleWorld(cx + dx * radii[i], cz + dz * radii[i]);
                    values.Add(float.IsNaN(h) ? "NaN" : h.ToString("F2"));
                    if (Mathf.Approximately(radii[i], 191f)) outside = h;
                }
                W($"{deg}," + string.Join(",", values));
                if (!float.IsNaN(outside) && outside < best)
                {
                    best = outside;
                    bestAngle = deg;
                }
            }

            W($"最低 r=191 高度 = {best:F2} at angle={bestAngle:F1}");
            W($"建议岸点角度 = {bestAngle:F1}，向外 1m 处高度 = " +
              $"{SampleWorld(cx + Mathf.Sin(bestAngle * Mathf.Deg2Rad) * 192f, cz + Mathf.Cos(bestAngle * Mathf.Deg2Rad) * 192f):F2}");
            W("==== 结论：PASS ====");
            Flush(sb, 0);
        }
        catch (System.Exception ex)
        {
            W($"异常：{ex}");
            Flush(sb, 1);
        }
    }

    private static void Flush(StringBuilder sb, int code)
    {
        Directory.CreateDirectory(OutDir);
        File.WriteAllText(ResultPath, sb.ToString());
        if (Application.isBatchMode) EditorApplication.Exit(code);
    }
}
