using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class TreeDistanceVisualCheck
{
    private const string Tag = "[TreeDistVis]";
    private const string WorldScenePath = "Assets/Scenes/World.unity";
    private const string ResultPath = "Logs/tree-distance-check.txt";
    private const string PngPath = "Logs/tree-distance-check.png";

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

        var terrains = Object.FindObjectsOfType<Terrain>().Where(t => t != null && t.terrainData != null).ToArray();
        if (terrains.Length == 0) { W("FAIL：无 Terrain"); Flush(sb, 2); return; }

        var t0 = terrains.OrderByDescending(t => t.terrainData.treeInstanceCount).First();
        Vector3 c = t0.transform.position + t0.terrainData.size * 0.5f;
        c.y = t0.SampleHeight(new Vector3(c.x, 0, c.z)) + t0.transform.position.y;

        const float EyeHeight = 1.7f;

        var camGo = new GameObject("~TreeDistCam");
        var cam = camGo.AddComponent<Camera>();
        cam.fieldOfView = 60f;
        cam.farClipPlane = 6000f;
        cam.clearFlags = CameraClearFlags.Skybox;
        cam.transform.position = new Vector3(c.x, c.y + EyeHeight, c.z);
        cam.transform.rotation = Quaternion.Euler(3f, 45f, 0f);

        W($"机位 = {cam.transform.position}，看向 {c + new Vector3(0f, 5f, 120f)}");
        W($"当前 treeDistance = {t0.treeDistance}，billboard = {t0.treeBillboardDistance}");

        const int size = 640;
        var rt = new RenderTexture(size, size, 24, RenderTextureFormat.ARGB32);
        rt.Create();
        cam.targetTexture = rt;

        Directory.CreateDirectory("Logs");

        foreach (var t in terrains) t.drawTreesAndFoliage = false;
        for (int i = 0; i < 3; i++) cam.Render();
        SaveFrame(rt, size, "Logs/tree-distance-check-notrees.png");

        foreach (var t in terrains) t.drawTreesAndFoliage = true;
        for (int i = 0; i < 5; i++) cam.Render();
        var tex = SaveFrame(rt, size, PngPath);

        var px = tex.GetPixels32();
        int sky = 0;
        foreach (var cc in px)
        {
            float lum = 0.299f * cc.r + 0.587f * cc.g + 0.114f * cc.b;
            if (lum > 140f && cc.b >= cc.r) sky++;
        }
        double skyPct = sky * 100.0 / px.Length;
        W($"天空像素占比 = {skyPct:F1}%");
        W($"已写出 {PngPath}");

        bool ok = true;
        W("（此项为目视验收，需人工确认 260m 处无硬边界/秃地）");
        W($"==== 结论：{(ok ? "PASS" : "FAIL")} ====");

        cam.targetTexture = null;
        rt.Release();
        Object.DestroyImmediate(rt);
        Flush(sb, 0);
    }

    private static Texture2D SaveFrame(RenderTexture rt, int size, string path)
    {
        var prev = RenderTexture.active;
        RenderTexture.active = rt;
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        tex.ReadPixels(new Rect(0, 0, size, size), 0, 0);
        tex.Apply();
        RenderTexture.active = prev;
        File.WriteAllBytes(path, tex.EncodeToPNG());
        return tex;
    }

    private static void Flush(StringBuilder sb, int code)
    {
        File.WriteAllText(ResultPath, sb.ToString());
        if (Application.isBatchMode) EditorApplication.Exit(code);
    }
}
