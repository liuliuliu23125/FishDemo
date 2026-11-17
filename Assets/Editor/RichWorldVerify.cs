using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class RichWorldVerify
{
    private const string Tag = "[RichVerify]";
    private const string WorldScene = "Assets/Scenes/World.unity";

    public static void Run()
    {
        var sb = new StringBuilder();
        void W(string s) { Debug.Log($"{Tag} {s}"); sb.AppendLine(s); }

        var scene = EditorSceneManager.OpenScene(WorldScene, OpenSceneMode.Single);
        var chunks = Object.FindObjectsOfType<SECTR_Chunk>();
        var sectors = Object.FindObjectsOfType<SECTR_Sector>();
        var loader = Object.FindObjectOfType<SECTR_RegionLoader>();
        W($"主场景：Chunk={chunks.Length}, Sector={sectors.Length}, RegionLoader={(loader!=null)}, Terrain={Object.FindObjectsOfType<Terrain>().Length}");

        var build = new System.Collections.Generic.List<EditorBuildSettingsScene> { new EditorBuildSettingsScene(WorldScene, true) };
        int miss = 0;
        foreach (var c in chunks) { if (System.IO.File.Exists(c.NodeName)) build.Add(new EditorBuildSettingsScene(c.NodeName, true)); else miss++; }
        EditorBuildSettings.scenes = build.ToArray();
        W($"Build Settings = {build.Count} 个场景，缺失 {miss}");

        long totalTrees = 0; int sampleChecked = 0;
        int minLayers = int.MaxValue; float gMin = 1, gMax = 0;
        foreach (var c in chunks.OrderBy(x => x.ScenePath).Take(4))
        {
            var cs = EditorSceneManager.OpenScene(c.NodeName, OpenSceneMode.Additive);
            var ts = Object.FindObjectsOfType<Terrain>().Where(t => t.gameObject.scene == cs).ToList();
            foreach (var t in ts)
            {
                var td = t.terrainData;
                int res = td.heightmapResolution, step = Mathf.Max(1, res / 32);
                var h = td.GetHeights(0, 0, res, res);
                float mn = 1, mx = 0;
                for (int x = 0; x < res; x += step) for (int y = 0; y < res; y += step) { if (h[x,y]<mn) mn=h[x,y]; if (h[x,y]>mx) mx=h[x,y]; }
                gMin = Mathf.Min(gMin, mn); gMax = Mathf.Max(gMax, mx);
                int layers = td.terrainLayers?.Length ?? 0;
                minLayers = Mathf.Min(minLayers, layers);
                long detail = 0;
                for (int l = 0; l < (td.detailPrototypes?.Length ?? 0); l++)
                {
                    var layer = td.GetDetailLayer(0, 0, td.detailWidth, td.detailHeight, l);
                    foreach (int v in layer) if (v > 0) detail += v;
                }
                W($"  {System.IO.Path.GetFileNameWithoutExtension(c.NodeName)}: 起伏={mx-mn:F3}, 贴图层={layers}, 树={td.treeInstanceCount}, 草密度={detail}");
                totalTrees += td.treeInstanceCount;
                sampleChecked++;
            }
            EditorSceneManager.CloseScene(cs, true);
        }

        W($"抽样 {sampleChecked} 块：合计树={totalTrees}, 最小贴图层={minLayers}, 高度范围=[{gMin:F3},{gMax:F3}] 跨 {(gMax-gMin)*700:F1}m");

        var loadSize = loader.LoadSize;
        var ever = new System.Collections.Generic.HashSet<SECTR_Sector>();
        int maxAt = 0;
        for (int i=0;i<7;i++) for (int j=0;j<7;j++)
        {
            var pos = new Vector3(Mathf.Lerp(-1536,1536,i/6f), 350, Mathf.Lerp(-1536,1536,j/6f));
            var list = new System.Collections.Generic.List<SECTR_Sector>();
            SECTR_Sector.GetContaining(ref list, new Bounds(pos, loadSize));
            list = list.Where(s=>s.Frozen).ToList();
            maxAt = Mathf.Max(maxAt, list.Count);
            foreach (var s in list) ever.Add(s);
        }
        W($"流式：单点最多 {maxAt} 块，全覆盖 {ever.Count}/{sectors.Length}");

        bool pass = chunks.Length==36 && sectors.Length==36 && loader!=null && miss==0
                    && sampleChecked>0 && totalTrees>0 && minLayers>0
                    && gMax-gMin > 0.05f && maxAt < sectors.Length && ever.Count==sectors.Length;
        W($"==== 结论：{(pass?"PASS":"FAIL")} ====");

        EditorSceneManager.SaveScene(scene);
        AssetDatabase.SaveAssets();
        System.IO.Directory.CreateDirectory("Logs/Gaia");
        System.IO.File.WriteAllText("Logs/Gaia/rich-verify.txt", sb.ToString());
        EditorApplication.Exit(pass?0:2);
    }
}
