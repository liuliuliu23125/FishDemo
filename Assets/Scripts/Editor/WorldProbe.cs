using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace FishDemo.EditorTools
{
    /// <summary>
    /// 只读探针：找出主相机附近可站立的地面高度与位置。
    ///
    /// 为什么需要：M1 的草是硬编码布点的，要放进 World.unity 让人用眼睛看到，
    /// 必须知道「相机看的方向、脚下地表多高」，否则草会埋在地下或悬空。
    /// 地形已 mesh 化但 Terrain 组件仍保留，所以用 Terrain.SampleHeight 即可。
    ///
    /// 用法：-executeMethod FishDemo.EditorTools.WorldProbe.Run
    /// 结果写 Logs/Grass/world-probe.txt
    /// </summary>
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

            // World.unity 编辑期不含 Terrain（地形全在 chunk 场景里，由 SECTR 运行时加载），
            // 所以必须把 chunk 场景 additive 打开才能取到真实高度。
            //
            // 注意：这里不能用 SECTR_Chunk 来枚举 chunk——它在默认程序集 Assembly-CSharp 里，
            // 而本文件属于 FishDemo.Editor（asmdef），引用方向反了编不过。
            // 改用 Build Settings 的场景列表（FinalIntegrity 维护的正是这份），绕开该依赖。
            var chunkPaths = new System.Collections.Generic.List<string>();
            foreach (var s in EditorBuildSettings.scenes)
            {
                if (!s.enabled) continue;
                if (s.path.EndsWith("World.unity")) continue; // 主场景已单独打开
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

            // 视线与 y=0 平面的交点，以及沿视线若干距离上的采样点。
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

            // 相机正下方
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
