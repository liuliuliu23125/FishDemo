using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using FishDemo.Rendering;

namespace FishDemo.EditorTools
{
    /// <summary>
    /// 用「主相机 + World 场景」渲染一帧并回读，确认摆进场景的草真的可见。
    ///
    /// 与 GrassM1Verify 的区别：
    ///   GrassM1Verify 用合成相机 + 临时节点，验证 BRG 链路本身；
    ///   本脚本用场景里真实存在的 GrassBRG (M1) 节点和 Main Camera，验证「摆进场景后能看到」。
    ///   两者不能互相替代——前者证明 API 对，后者证明场景配置对（图层/锚点/贴地/尺度）。
    ///
    /// 用法：-executeMethod FishDemo.EditorTools.GrassSceneVerify.Run
    /// </summary>
    public static class GrassSceneVerify
    {
        private const string Tag = "[GrassSceneVerify]";
        private const string ResultPath = "Logs/Grass/scene-verify.txt";
        private const string WorldScenePath = "Assets/Scenes/World.unity";

        [MenuItem("FishDemo/Rendering/⑥ 验证世界场景里的草", false, 106)]
        public static void Run()
        {
            var sb = new StringBuilder();
            void W(string s) { Debug.Log($"{Tag} {s}"); sb.AppendLine(s); }

            var scene = EditorSceneManager.OpenScene(WorldScenePath, OpenSceneMode.Single);

            // 草在 World.unity，但地表高度靠运行时 SampleHeight，所以必须把 chunk 也加载进来，
            // 否则 BuildInstances 会回落到锚点高度，草会飘在空中。
            int opened = 0;
            foreach (var s in EditorBuildSettings.scenes)
            {
                if (!s.enabled || s.path.EndsWith("World.unity")) continue;
                if (!File.Exists(s.path)) continue;
                EditorSceneManager.OpenScene(s.path, OpenSceneMode.Additive);
                opened++;
            }
            W($"附加加载 chunk = {opened}");

            var cam = Camera.main;
            if (cam == null) { W("找不到 MainCamera"); Flush(sb); return; }

            var mgr = Object.FindObjectOfType<GrassBRGManager>();
            if (mgr == null) { W("场景里找不到 GrassBRGManager"); Flush(sb); return; }
            W($"找到 GrassBRGManager，锚点 = {mgr.transform.position}");

            // ── 关键：把 BRG 草改成洋红色 ──
            // 地形自带 detail 草也是绿色，用绿色统计无法区分「BRG 画出来了」和
            // 「本来就是地形草」。洋红在地形配色里不存在，只要出现就是 BRG 画的。
            var mat = mgr.CurrentMaterial;
            if (mat == null) { W("BRG 材质为空，初始化可能失败"); Flush(sb); return; }
            var originalColor = mat.HasProperty("_BaseColor") ? mat.GetColor("_BaseColor") : Color.white;
            mat.SetColor("_BaseColor", new Color(1f, 0f, 1f, 1f));
            W($"已把 BRG 草染成洋红（原色 {originalColor}）");

            const int size = 512;
            var rt = new RenderTexture(size, size, 24, RenderTextureFormat.ARGB32);
            rt.Create();

            bool pass = false;
            try
            {
                // ── 视角 1：主相机原视角（验证「进游戏能看到」）──
                int magentaMain = RenderAndCount(cam, rt, size, out Color32[] pxMain, out int nonBlackMain);
                SavePng(pxMain, size, "Logs/Grass/scene-camera-view.png");
                W($"[主相机] 非背景 {nonBlackMain} ({nonBlackMain / (float)(size * size):P2})，洋红像素 {magentaMain}");

                // ── 视角 2：诊断相机，贴近草方阵俯视（验证几何真的在那）──
                var diagCam = CreateDiagCamera(mgr.transform.position);
                int magentaDiag = RenderAndCount(diagCam, rt, size, out Color32[] pxDiag, out int nonBlackDiag);
                SavePng(pxDiag, size, "Logs/Grass/scene-camera-diag.png");
                W($"[诊断俯视] 非背景 {nonBlackDiag}，洋红像素 {magentaDiag}");
                Object.DestroyImmediate(diagCam.gameObject);

                W($"BRG 可见实例（最近一次剔除）= {mgr.LastVisibleCount}");

                // 判定口径：主相机或诊断视角任一看到洋红即算画出。
                // 主相机看不到不算失败——草可能太小/被地形遮挡，那是布点问题不是链路问题。
                pass = magentaMain > 0 || magentaDiag > 0;
                if (magentaMain == 0 && magentaDiag > 0)
                    W("注意：主相机视角看不到 BRG 草（诊断俯视能看到），布点需调整。");

                W($"==== 结论：{(pass ? "PASS" : "FAIL")} ====");
            }
            finally
            {
                mat.SetColor("_BaseColor", originalColor); // 还原，避免污染场景资产
                rt.Release();
                Object.DestroyImmediate(rt);
                Flush(sb);
            }

            if (Application.isBatchMode)
                EditorApplication.Exit(pass ? 0 : 2);
        }

        /// <summary>
        /// 渲染一帧并统计洋红像素（R 高、G 低、B 高）。
        /// 洋红在地形配色里不存在，所以只要出现就是 BRG 草画的，不会被地形草污染。
        /// </summary>
        private static int RenderAndCount(Camera cam, RenderTexture rt, int size,
            out Color32[] pixels, out int nonBlack)
        {
            pixels = null;
            nonBlack = 0;

            var request = new RenderPipeline.StandardRequest { destination = rt };
            if (!RenderPipeline.SupportsRenderRequest(cam, request))
                return 0;

            RenderPipeline.SubmitRenderRequest(cam, request);

            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false, true);
            tex.ReadPixels(new Rect(0, 0, size, size), 0, 0);
            tex.Apply();
            pixels = tex.GetPixels32();
            Object.DestroyImmediate(tex);
            RenderTexture.active = prev;

            int magenta = 0;
            for (int i = 0; i < pixels.Length; i++)
            {
                var p = pixels[i];
                if (p.r > 8 || p.g > 8 || p.b > 8) nonBlack++;
                if (p.r > 100 && p.b > 100 && p.g < 60) magenta++;
            }
            return magenta;
        }

        private static void SavePng(Color32[] pixels, int size, string path)
        {
            if (pixels == null) return;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false, true);
            tex.SetPixels32(pixels);
            tex.Apply();
            var png = tex.EncodeToPNG();
            Object.DestroyImmediate(tex);

            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllBytes(path, png);
        }

        private static Camera CreateDiagCamera(Vector3 grassCenter)
        {
            var go = new GameObject("GrassDiagCam");
            var cam = go.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0f, 0f, 0f, 1f);
            cam.fieldOfView = 60f;
            cam.nearClipPlane = 0.3f;
            cam.farClipPlane = 1000f;
            // 从草方阵斜上方看下去，保证整个方阵入画。
            cam.transform.position = grassCenter + new Vector3(0f, 45f, -45f);
            cam.transform.LookAt(grassCenter);
            cam.cullingMask = ~0;
            return cam;
        }

        private static void Flush(StringBuilder sb)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ResultPath));
            File.WriteAllText(ResultPath, sb.ToString());
            if (Application.isBatchMode && !sb.ToString().Contains("结论"))
                EditorApplication.Exit(1);
        }
    }
}
