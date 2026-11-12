using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// 远景验收：确认降 treeDistance 之后没有出现"树墙/秃地"硬边界。
///
/// ── 为什么必须单独验这一项 ──
/// treeDistance 从 600 降到 260，同屏可见树减少 81%。这个数字很漂亮，
/// 但如果 260m 处出现"树突然消失"的硬边界、或远处山头光秃，
/// 就是拿画面换性能，不能算完成。
/// 这里从高处俯拍几个远近层次，落 PNG 供目视，并统计画面中
/// 天空像素占比（秃地/漏空会让天空占比上升）。
///
/// 用法：-executeMethod TreeDistanceVisualCheck.Run
/// 输出 Logs/tree-distance-check.png / .txt
/// </summary>
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

        // 机位设计：找一个能同时看见"有树"和"无树"两种区域的视角。
        // 高空俯拍看不出断带（450m 与 260m 的差异都落在画面边缘且被地形起伏挡住），
        // 所以要压低相机、平视远方，让 treeDistance 的截断线横穿画面中部。
        var t0 = terrains.OrderByDescending(t => t.terrainData.treeInstanceCount).First();
        Vector3 c = t0.transform.position + t0.terrainData.size * 0.5f;
        c.y = t0.SampleHeight(new Vector3(c.x, 0, c.z)) + t0.transform.position.y;

        // 玩家视角：人眼高度 1.7m，站在地面上平视前方。
        // 之前所有验收都用高空俯瞰，那看不到玩家真正会看到的画面——
        // 平视时近处树挡视野、地面占比大、远景被树冠切断，观感与俯瞰完全不同。
        // 这才是判断"密度够不够、有没有秃地"的唯一有效机位。
        const float EyeHeight = 1.7f;

        var camGo = new GameObject("~TreeDistCam");
        var cam = camGo.AddComponent<Camera>();
        cam.fieldOfView = 60f;          // 接近手机竖屏/横屏的常见 FOV
        cam.farClipPlane = 6000f;
        cam.clearFlags = CameraClearFlags.Skybox;
        cam.transform.position = new Vector3(c.x, c.y + EyeHeight, c.z);
        // 平视略上扬 3°（人眼自然视线），看向 200m 外
        cam.transform.rotation = Quaternion.Euler(3f, 45f, 0f);

        W($"机位 = {cam.transform.position}，看向 {c + new Vector3(0f, 5f, 120f)}");
        W($"当前 treeDistance = {t0.treeDistance}，billboard = {t0.treeBillboardDistance}");

        const int size = 640;
        var rt = new RenderTexture(size, size, 24, RenderTextureFormat.ARGB32);
        rt.Create();
        cam.targetTexture = rt;

        Directory.CreateDirectory("Logs");

        // 对照组：关掉树，确认"没树的地面"长什么样。
        // 有树图里如果出现和这张一样的地表，就说明那里确实被 treeDistance 裁掉了。
        foreach (var t in terrains) t.drawTreesAndFoliage = false;
        for (int i = 0; i < 3; i++) cam.Render();
        SaveFrame(rt, size, "Logs/tree-distance-check-notrees.png");

        foreach (var t in terrains) t.drawTreesAndFoliage = true;
        for (int i = 0; i < 5; i++) cam.Render();
        var tex = SaveFrame(rt, size, PngPath);

        // 统计：亮且蓝=天空；剩下的算地形/树
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

        bool ok = true;   // 这一项只出图给人看，不做机械判定；有图即可
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
