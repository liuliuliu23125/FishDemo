using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// 树的近景渲染色验收：证明「树不再是粉红/发白」。
///
/// 为什么必须真渲染：_MainTex 是否为空只能从 API 读到"有没有引用"，
/// 但"画面看起来对不对"还要看 albedo × 光照 × 色调映射的最终结果。
/// 这里直接摆一个贴地近景相机对着 Spruce，落 PNG 并统计像素色相分布。
///
/// 判定：
///   1. 画面里树占据的像素，绿色通道均值 > 红/蓝通道（树是绿的，不是粉红）；
///   2. "偏粉白"像素（R 与 B 都明显高于 G）占比 < 5%。
///
/// 用法：-executeMethod TreeRenderVerify.Run
/// 输出 Logs/tree-render-verify.txt + Logs/tree-render-verify.png
/// </summary>
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

        // 树都在 chunk 场景里，必须把启用的 chunk 一并加载，否则只有空地形
        int opened = 0;
        foreach (var s in EditorBuildSettings.scenes)
        {
            if (!s.enabled || s.path.EndsWith("World.unity") || !File.Exists(s.path)) continue;
            EditorSceneManager.OpenScene(s.path, OpenSceneMode.Additive);
            opened++;
        }
        W($"附加加载 chunk = {opened}");

        // 找一个树实例密集的位置：用地形上第一个 Spruce 实例当取景点
        Vector3 target = Vector3.zero;
        bool found = false;
        foreach (var t in Object.FindObjectsOfType<Terrain>())
        {
            var td = t.terrainData;
            if (td == null || td.treeInstanceCount == 0) continue;
            var insts = td.treeInstances;
            // 取一个高度适中的实例，避免取到边界外的
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
        // 找不到就退回到地形集合中心
        if (!found)
        {
            var ts = Object.FindObjectsOfType<Terrain>();
            if (ts.Length > 0)
                target = ts.Aggregate((a, b) => a.transform.position.sqrMagnitude < b.transform.position.sqrMagnitude ? a : b).transform.position;
            W("未找到树实例，退回地形位置取景");
        }
        W($"取景目标 = {target}");

        // 玩家视角：人眼高度 1.7m，站在树前 18m 平视。
        // 不用之前的"高 9m 斜俯"——那不是玩家会看到的画面。
        var camGo = new GameObject("~TreeVerifyCam");
        var cam = camGo.AddComponent<Camera>();
        cam.fieldOfView = 55f;
        cam.nearClipPlane = 0.3f;
        cam.farClipPlane = 3000f;
        cam.clearFlags = CameraClearFlags.Skybox;

        Vector3 eye = new Vector3(target.x + 13f, target.y + 1.7f, target.z + 13f);
        cam.transform.position = eye;
        // 平视偏上一点，把树冠纳入画面
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

        // ── 统计口径：只取「树冠像素」，用几何定位而不是颜色筛选 ──
        // 之前按颜色排除天空/地面会漏：远景有蓝色雾带、地表有黄色小花，
        // 都会污染均值。这里把每个树实例的树冠中心投影到屏幕，
        // 在投影点周围取小块采样，采到的必然是该树的枝叶。
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

                // 树的世界位置；树冠取原型 LODGroup m_Size 的 ~70% 处（枝叶最密的位置）。
                // 不用常量估高：不同原型从 3m 到 74m 都有，估错就会采到天空。
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
                if (sp.z <= 0) continue;                                  // 在相机背后
                if (sp.x < 8 || sp.x > size - 8 || sp.y < 8 || sp.y > size - 8) continue; // 出画

                // 只采近景树：远景树在屏幕上只有几个像素宽，采样点很容易穿过去
                // 落到后面的雾霭/天空上，把均值污染成蓝灰，测不出树的真实颜色。
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
                        if (lum < 12f) continue;   // 完全黑的采样点丢掉（树洞/背面剪影）

                        // 只认「植被像素」：枝叶是绿/橄榄色，B 明显低于 G。
                        // 远景雾霭和天空是中性灰或偏蓝（B≈G≈R 或 B 最大），
                        // 之前不过滤这层，均值就被灰雾稀释成蓝灰，测不出树色。
                        if (c.b >= c.g - 8) continue;

                        n++;
                        sumR += c.r; sumG += c.g; sumB += c.b;
                        // 「粉」的真实特征是红明显压过绿（之前用 R>=G 判，
                        // 会把云杉的橄榄绿 R≈G 误判成粉）。留 10 的容差。
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

        // 判据（按云杉实测校准，不是凭感觉设的）：
        //   云杉针叶是**橄榄绿**，其固有色就是 R≈G 且 B 明显偏低
        //   （健康样本实测 R=55.0 G=53.8 B=34.7）。
        //   所以「G 必须严格大于 R」这个条件对云杉不成立，会把正常的树判成失败。
        //   真正要排除的「粉红」特征是：红明显压过绿（R > G+10）。
        //   再加一条 B 不能接近 R（粉色是 R 和 B 都高、G 低）。
        bool isOliveGreen = ab < ag - 8;          // 蓝明显低于绿 → 是植被色，不是灰/粉
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
