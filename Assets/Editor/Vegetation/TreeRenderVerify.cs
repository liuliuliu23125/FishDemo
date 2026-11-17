using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class TreeRenderVerify
{
    private const string Tag = "[TreeRender]";
    private const string WorldScenePath = "Assets/Scenes/World.unity";
    private const string ResultPath = "Logs/tree-render-verify.txt";
    private const string PngPath = "Logs/tree-render-verify.png";

    public static void Run()
    {
        var sb = new StringBuilder();
        void W(string s) { Debug.Log($"{Tag} {s}"); sb.AppendLine(s); }

        EditorSceneManager.OpenScene(WorldScenePath, OpenSceneMode.Single);

        int opened = 0;
        foreach (var s in EditorBuildSettings.scenes)
        {
            if (!s.enabled || s.path.EndsWith("World.unity") || !File.Exists(s.path)) continue;
            EditorSceneManager.OpenScene(s.path, OpenSceneMode.Additive);
            opened++;
        }
        W($"附加加载 chunk = {opened}");

        Vector3 target = Vector3.zero;
        bool found = false;
        foreach (var t in Object.FindObjectsOfType<Terrain>())
        {
            var td = t.terrainData;
            if (td == null || td.treeInstanceCount == 0) continue;
            var insts = td.treeInstances;
            for (int i = 0; i < insts.Length && i < 200; i++)
            {
                var p = insts[i].position;
                if (p.x < 0.05f || p.x > 0.95f || p.z < 0.05f || p.z > 0.95f) continue;
                target = t.transform.position + Vector3.Scale(p, td.size);
                found = true;
                break;
            }
            if (found) break;
        }
        if (!found)
        {
            var ts = Object.FindObjectsOfType<Terrain>();
            if (ts.Length > 0)
                target = ts.Aggregate((a, b) => a.transform.position.sqrMagnitude < b.transform.position.sqrMagnitude ? a : b).transform.position;
            W("未找到树实例，退回地形位置取景");
        }
        W($"取景目标 = {target}");

        var camGo = new GameObject("~TreeVerifyCam");
        var cam = camGo.AddComponent<Camera>();
        cam.fieldOfView = 55f;
        cam.nearClipPlane = 0.3f;
        cam.farClipPlane = 3000f;
        cam.clearFlags = CameraClearFlags.Skybox;

        Vector3 eye = new Vector3(target.x + 13f, target.y + 1.7f, target.z + 13f);
        cam.transform.position = eye;
        cam.transform.LookAt(new Vector3(target.x, target.y + 6f, target.z));

        const int size = 512;
        var rt = new RenderTexture(size, size, 24, RenderTextureFormat.ARGB32);
        rt.Create();

        var prev = RenderTexture.active;
        cam.targetTexture = rt;
        cam.Render();
        RenderTexture.active = rt;

        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        tex.ReadPixels(new Rect(0, 0, size, size), 0, 0);
        tex.Apply();
        RenderTexture.active = prev;
        cam.targetTexture = null;

        var px = tex.GetPixels32();
        File.WriteAllBytes(PngPath, tex.EncodeToPNG());

        long n = 0, pinkish = 0;
        double sumR = 0, sumG = 0, sumB = 0;
        int sampledTrees = 0;

        foreach (var t in Object.FindObjectsOfType<Terrain>())
        {
            var td = t.terrainData;
            if (td == null) continue;
            var protos = td.treePrototypes;
            var insts = td.treeInstances;

            for (int i = 0; i < insts.Length; i++)
            {
                var ti = insts[i];
                if (ti.prototypeIndex < 0 || ti.prototypeIndex >= protos.Length) continue;

                Vector3 world = t.transform.position + Vector3.Scale(ti.position, td.size);
                var prefab = protos[ti.prototypeIndex].prefab;
                float protoH = 8f;
                if (prefab != null)
                {
                    var lg = prefab.GetComponent<LODGroup>();
                    if (lg != null && lg.size > 0f) protoH = lg.size;
                }
                Vector3 canopy = world + Vector3.up * (protoH * ti.heightScale * 0.7f);

                Vector3 sp = cam.WorldToScreenPoint(canopy);
                if (sp.z <= 0) continue;
                if (sp.x < 8 || sp.x > size - 8 || sp.y < 8 || sp.y > size - 8) continue;

                if (Vector3.Distance(cam.transform.position, canopy) > 150f) continue;

                int cx = Mathf.RoundToInt(sp.x), cy = Mathf.RoundToInt(sp.y);
                bool any = false;
                for (int dy = -4; dy <= 4; dy++)
                {
                    for (int dx = -4; dx <= 4; dx++)
                    {
                        int x = cx + dx, y = cy + dy;
                        if (x < 0 || x >= size || y < 0 || y >= size) continue;
                        var c = px[y * size + x];
                        float lum = 0.299f * c.r + 0.587f * c.g + 0.114f * c.b;
                        if (lum < 12f) continue;

                        if (c.b >= c.g - 8) continue;

                        n++;
                        sumR += c.r; sumG += c.g; sumB += c.b;
                        if (c.r > c.g + 10) pinkish++;
                        any = true;
                    }
                }
                if (any) sampledTrees++;
            }
        }

        double ar = n > 0 ? sumR / n : 0, ag = n > 0 ? sumG / n : 0, ab = n > 0 ? sumB / n : 0;
        double pinkPct = n > 0 ? pinkish * 100.0 / n : 0;

        W($"采样树 = {sampledTrees}，植被像素采样点 = {n}");
        W($"植被均值 R={ar:F1} G={ag:F1} B={ab:F1}");
        W($"明显偏粉(R>G+10)占比 = {pinkPct:F2}%");

        bool isOliveGreen = ab < ag - 8;
        bool notPink = pinkPct < 5.0;
        bool pass = n > 500 && isOliveGreen && notPink;

        W($"B 明显低于 G（植被绿）= {isOliveGreen}");
        W($"无明显偏粉 = {notPink}");
        W($"==== 结论：{(pass ? "PASS" : "FAIL")} ====");

        Object.DestroyImmediate(camGo);
        rt.Release();
        Object.DestroyImmediate(rt);
        File.WriteAllText(ResultPath, sb.ToString());
        if (Application.isBatchMode) EditorApplication.Exit(pass ? 0 : 2);
    }
}
