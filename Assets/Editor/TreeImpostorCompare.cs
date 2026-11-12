using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// 渲染验证：把烘好的云杉 impostor 与原始 LOD0 mesh 并排摆出来，
/// 同一机位、同一光照下各出一张图，肉眼与像素级对比。
///
/// ── 为什么要并排对比，而不是只看 impostor 一张图 ──
/// Impostor 的价值是"远距离看起来和真树差不多"。
/// 单独渲一张 impostor 很好看没有意义——必须有真树作为参照，
/// 才能判断切换距离该设多远、以及这个包在本工程的光照/色调下会不会明显失真。
///
/// 用法：-executeMethod TreeImpostorCompare.Run
/// 输出 Logs/impostor-compare.png + Logs/impostor-compare.txt
/// </summary>
public static class TreeImpostorCompare
{
    private const string Tag = "[ImpostorCmp]";
    private const string ResultPath = "Logs/impostor-compare.txt";
    private const string PngPath = "Logs/impostor-compare.png";
    private const string PrefabPath =
        "Assets/Procedural Worlds/Packages - Install/Gaia Pro Assets and Biomes/Prefabs/Trees/PW_Tree_Spruce_06.prefab";

    public static void Run()
    {
        var sb = new StringBuilder();
        void W(string s) { Debug.Log($"{Tag} {s}"); sb.AppendLine(s); }

        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

        // 一盏方向光，参数照抄 World.unity 的主光（白、1.2、50° 仰角）
        var lightGo = new GameObject("Sun");
        var light = lightGo.AddComponent<Light>();
        light.type = LightType.Directional;
        light.color = Color.white;
        light.intensity = 1.2f;
        lightGo.transform.rotation = Quaternion.Euler(50f, 30f, 0f);

        RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Trilight;
        RenderSettings.ambientSkyColor = new Color(0.212f, 0.227f, 0.259f);
        RenderSettings.ambientEquatorColor = new Color(0.114f, 0.125f, 0.133f);
        RenderSettings.ambientGroundColor = new Color(0.047f, 0.043f, 0.035f);

        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
        if (prefab == null) { W("FAIL：prefab 载入失败"); Flush(sb, 2); return; }

        // 左：真实 LOD0；右：impostor
        // 用 prefab 实例化后强制各自只显示一个 LOD 级
        for (int i = 0; i < 2; i++)
        {
            var inst = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            inst.transform.position = new Vector3(i * 8f - 4f, 0f, 0f);
            var lg = inst.GetComponentInChildren<LODGroup>();
            if (lg != null)
            {
                var lods = lg.GetLODs();
                W($"实例{i}: LOD 级数={lods.Length}, 各级 renderer={string.Join(",", lods.Select(l => l.renderers.Length))}");

                // 直接禁用 LODGroup 才能让 renderer.enabled 生效：
                // LODGroup 每帧会按屏幕占比重算并覆盖各级 renderer 的 enabled，
                // 手动关掉某几级之后马上又被它打开，表现就是"什么都不显示"。
                lg.enabled = false;

                if (i == 0)
                {
                    // 左：只看 LOD0（真树最高精度）
                    for (int k = 1; k < lods.Length; k++)
                        foreach (var r in lods[k].renderers) if (r != null) r.enabled = false;
                    W($"  左：只保留 LOD0（{lods[0].renderers.Length} 个 renderer）");
                }
                else
                {
                    // 右：只看最末级（ReplaceLast 后应是 impostor）
                    for (int k = 0; k < lods.Length - 1; k++)
                        foreach (var r in lods[k].renderers) if (r != null) r.enabled = false;
                    var last = lods[lods.Length - 1].renderers.FirstOrDefault();
                    W($"  右：只保留 LOD{lods.Length - 1}，renderer={(last != null ? last.name : "null")}，" +
                      $"材质={(last != null && last.sharedMaterial != null ? last.sharedMaterial.name : "null")}");
                }
            }
        }

        var camGo = new GameObject("Cam");
        var cam = camGo.AddComponent<Camera>();
        cam.fieldOfView = 40f;
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = new Color(0.15f, 0.17f, 0.2f);
        cam.transform.position = new Vector3(0f, 6f, -22f);
        cam.transform.LookAt(new Vector3(0f, 6f, 0f));

        W($"相机 = {cam.transform.position}，看向 (0,6,0)");

        const int size = 768;
        var rt = new RenderTexture(size, size, 24, RenderTextureFormat.ARGB32);
        rt.Create();
        cam.targetTexture = rt;
        // 多渲几帧，确保 shader 变体编译完成、贴图 mip 就绪
        for (int i = 0; i < 5; i++) cam.Render();

        var prev = RenderTexture.active;
        RenderTexture.active = rt;
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        tex.ReadPixels(new Rect(0, 0, size, size), 0, 0);
        tex.Apply();
        RenderTexture.active = prev;

        Directory.CreateDirectory("Logs");
        File.WriteAllBytes(PngPath, tex.EncodeToPNG());
        W($"已写出 {PngPath}");

        // 左右两半各自统计，确认两边都画出了东西（而不是某一侧全黑）
        var px = tex.GetPixels32();
        int leftN = 0, rightN = 0;
        double lr = 0, lg_ = 0, lb = 0, rr = 0, rg = 0, rb = 0;
        Color32 bg = new Color32((byte)(0.15f * 255), (byte)(0.17f * 255), (byte)(0.2f * 255), 255);
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                var c = px[y * size + x];
                // 与背景色差异明显就算"有物体"
                if (Mathf.Abs(c.r - bg.r) + Mathf.Abs(c.g - bg.g) + Mathf.Abs(c.b - bg.b) < 30) continue;
                if (x < size / 2) { leftN++; lr += c.r; lg_ += c.g; lb += c.b; }
                else { rightN++; rr += c.r; rg += c.g; rb += c.b; }
            }
        }
        W($"左半（真树 LOD0）像素={leftN}, 均值=({(leftN > 0 ? lr / leftN : 0):F0},{(leftN > 0 ? lg_ / leftN : 0):F0},{(leftN > 0 ? lb / leftN : 0):F0})");
        W($"右半（Impostor）像素={rightN}, 均值=({(rightN > 0 ? rr / rightN : 0):F0},{(rightN > 0 ? rg / rightN : 0):F0},{(rightN > 0 ? rb / rightN : 0):F0})");

        bool ok = leftN > 2000 && rightN > 2000;
        W($"两侧都有内容 = {ok}");
        W($"==== 结论：{(ok ? "PASS" : "FAIL")} ====");

        cam.targetTexture = null;
        rt.Release();
        Object.DestroyImmediate(rt);
        Flush(sb, ok ? 0 : 2);
    }

    private static void Flush(StringBuilder sb, int code)
    {
        File.WriteAllText(ResultPath, sb.ToString());
        if (Application.isBatchMode) EditorApplication.Exit(code);
    }
}
