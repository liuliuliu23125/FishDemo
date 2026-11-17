using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using FishDemo.Rendering;

namespace FishDemo.EditorTools
{
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
                int magentaMain = RenderAndCount(cam, rt, size, out Color32[] pxMain, out int nonBlackMain);
                SavePng(pxMain, size, "Logs/Grass/scene-camera-view.png");
                W($"[主相机] 非背景 {nonBlackMain} ({nonBlackMain / (float)(size * size):P2})，洋红像素 {magentaMain}");

                var diagCam = CreateDiagCamera(mgr.transform.position);
                int magentaDiag = RenderAndCount(diagCam, rt, size, out Color32[] pxDiag, out int nonBlackDiag);
                SavePng(pxDiag, size, "Logs/Grass/scene-camera-diag.png");
                W($"[诊断俯视] 非背景 {nonBlackDiag}，洋红像素 {magentaDiag}");
                Object.DestroyImmediate(diagCam.gameObject);

                W($"BRG 可见实例（最近一次剔除）= {mgr.LastVisibleCount}");

                pass = magentaMain > 0 || magentaDiag > 0;
                if (magentaMain == 0 && magentaDiag > 0)
                    W("注意：主相机视角看不到 BRG 草（诊断俯视能看到），布点需调整。");

                W($"==== 结论：{(pass ? "PASS" : "FAIL")} ====");
            }
            finally
            {
                mat.SetColor("_BaseColor", originalColor);
                rt.Release();
                Object.DestroyImmediate(rt);
                Flush(sb);
            }

            if (Application.isBatchMode)
                EditorApplication.Exit(pass ? 0 : 2);
        }

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
