using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using FishDemo.Rendering;

namespace FishDemo.EditorTools
{
    public static class GrassLodVerify
    {
        private const string Tag = "[GrassLod]";
        private const string ResultPath = "Logs/Grass/lod-verify.txt";
        private const string ShaderPath = "Assets/Grasses/GrassBRG.shader";
        private const string PngDir = "Logs/Grass";

        private const long TriangleBudget = 1_500_000;

        [MenuItem("FishDemo/Rendering/⑦ 验证距离 LOD", false, 107)]
        public static void Run()
        {
            var sb = new StringBuilder();
            void W(string s) { Debug.Log($"{Tag} {s}"); sb.AppendLine(s); }

            var shader = AssetDatabase.LoadAssetAtPath<Shader>(ShaderPath);
            if (shader == null) { W($"找不到 shader: {ShaderPath}"); Flush(sb); return; }

            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
            {
                W("NullGfxDevice：LOD 需要真实渲染，跳过（SKIP，不是失败）。");
                Flush(sb);
                return;
            }

            const int size = 1080;
            const float fov = 60f;

            var root = new GameObject("GrassLOD_Verify");
            RenderTexture rt = null;
            Camera cam = null;
            bool allPass = true;

            try
            {
                var mat = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };

                var mgr = root.AddComponent<GrassBRGManager>();
                mgr.ConfigureForValidation(1000, 1.5f, 1.0f);
                mgr.SetMaterialForValidation(mat);
                root.transform.position = Vector3.zero;
                mgr.ConfigureLodForValidation(true, lod0Px: 100f, lod1Px: 25f, cullPx: 8f);
                mgr.Initialize();

                if (!mgr.IsReady) { W("初始化失败"); allPass = false; Flush(sb); return; }

                var tris = mgr.GetLodTriangleCounts();
                W($"LOD 三角形数 = [LOD0 {tris[0]}, LOD1 {tris[1]}, LOD2 {tris[2]}]");
                W($"阈值（像素高度）= LOD0≥100, LOD1≥25, 剔除<8");
                W("");

                rt = new RenderTexture(size, size, 24, RenderTextureFormat.ARGB32);
                rt.Create();

                var camGo = new GameObject("GrassLOD_VerifyCam");
                cam = camGo.AddComponent<Camera>();
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = Color.black;
                cam.fieldOfView = fov;
                cam.nearClipPlane = 0.3f;
                cam.farClipPlane = 2000f;
                cam.cullingMask = ~0;

                W($"{"距离",8} {"LOD0",8} {"LOD1",8} {"LOD2",8} {"可见",8} {"三角形",12} {"预算占用",10}");
                long baseTriangles = -1;

                foreach (float dist in new[] { 6f, 20f, 60f, 100f })
                {
                    cam.transform.position = new Vector3(0f, dist * 0.6f, -dist);
                    cam.transform.LookAt(Vector3.zero);

                    var request = new RenderPipeline.StandardRequest { destination = rt };
                    if (!RenderPipeline.SupportsRenderRequest(cam, request))
                    {
                        W("不支持 StandardRequest"); allPass = false; break;
                    }
                    RenderPipeline.SubmitRenderRequest(cam, request);

                    int l0 = mgr.GetLastLodCount(0);
                    int l1 = mgr.GetLastLodCount(1);
                    int l2 = mgr.GetLastLodCount(2);
                    int vis = mgr.LastVisibleCount;
                    long trisTotal = mgr.LastSubmittedTriangles;
                    if (dist == 20f) baseTriangles = trisTotal;

                    float budgetUse = trisTotal / (float)TriangleBudget;
                    W($"{dist,8:F0} {l0,8} {l1,8} {l2,8} {vis,8} {trisTotal,12:N0} {budgetUse,9:P1}");

                    float halfExtent = 24f;
                    float requiredHalfAngle = Mathf.Atan2(halfExtent, dist) * Mathf.Rad2Deg;
                    if (requiredHalfAngle < fov * 0.5f - 2f)
                        SaveRender(rt, size, $"{PngDir}/lod-dist-{dist:F0}.png");
                }

                W("");

                cam.transform.position = new Vector3(0f, 4f, -6f);
                cam.transform.LookAt(Vector3.zero);
                SubmitAndSettle(cam, rt, mgr);
                int near0 = mgr.GetLastLodCount(0);
                int near1 = mgr.GetLastLodCount(1);
                int nearTotal = mgr.LastVisibleCount;
                bool nearOk = nearTotal > 0 && near0 > 0 && near1 > 0;
                W($"判定1 近处(6m)同时出现 LOD0 与 LOD1：LOD0={near0}, LOD1={near1}/{nearTotal} → {(nearOk ? "PASS" : "FAIL")}");
                allPass &= nearOk;

                cam.transform.position = new Vector3(0f, 50f, -80f);
                cam.transform.LookAt(Vector3.zero);
                SubmitAndSettle(cam, rt, mgr);
                int far0 = mgr.GetLastLodCount(0);
                int far2 = mgr.GetLastLodCount(2);
                int farTotal = mgr.LastVisibleCount;
                bool farOk = farTotal > 0 && far2 > 0 && far0 == 0;
                W($"判定2 远处(80m)切到 LOD2 且无 LOD0：LOD0={far0}, LOD2={far2}/{farTotal} → {(farOk ? "PASS" : "FAIL")}");
                allPass &= farOk;

                cam.transform.position = new Vector3(0f, 4f, -6f);
                cam.transform.LookAt(Vector3.zero);
                SubmitAndSettle(cam, rt, mgr);
                long nearTris = mgr.LastSubmittedTriangles;
                int nearVis = mgr.LastVisibleCount;
                double nearTriPerInst = nearVis > 0 ? nearTris / (double)nearVis : 0;

                cam.transform.position = new Vector3(0f, 50f, -80f);
                cam.transform.LookAt(Vector3.zero);
                SubmitAndSettle(cam, rt, mgr);
                long farTris = mgr.LastSubmittedTriangles;
                int farVis = mgr.LastVisibleCount;
                double farTriPerInst = farVis > 0 ? farTris / (double)farVis : 0;

                bool triOk = nearTriPerInst > farTriPerInst && farTriPerInst > 0;
                W($"判定3 每实例三角形 近处({nearTriPerInst:F1}) > 远处({farTriPerInst:F1}) → {(triOk ? "PASS" : "FAIL")}");
                allPass &= triOk;

                long maxTri = System.Math.Max(nearTris, farTris);
                bool budgetOk = maxTri <= TriangleBudget;
                W($"判定4 最大提交三角形 {maxTri:N0} ≤ 预算 {TriangleBudget:N0} → {(budgetOk ? "PASS" : "FAIL")}");
                allPass &= budgetOk;

                W("");
                W($"==== 结论：{(allPass ? "PASS" : "FAIL")} ====");

                W("");
                W("── LOD 相对收益（同视角 A/B）──");
                var abPositions = new (string label, Vector3 pos, Vector3 look)[]
                {
                    ("近 6m",  new Vector3(0f, 4f, -6f),   Vector3.zero),
                    ("中 20m", new Vector3(0f, 12f, -20f), Vector3.zero),
                    ("远 60m", new Vector3(0f, 36f, -60f), Vector3.zero),
                };

                foreach (var (label, pos, look) in abPositions)
                {
                    cam.transform.position = pos;
                    cam.transform.LookAt(look);
                    mgr.ConfigureLodForValidation(true, 100f, 25f, 8f);
                    mgr.Initialize();
                    SubmitAndSettle(cam, rt, mgr);
                    int visible = mgr.LastVisibleCount;
                    long withLod = mgr.LastSubmittedTriangles;

                    mgr.ConfigureLodForValidation(false, 0f, 0f, 0f);
                    mgr.Initialize();
                    SubmitAndSettle(cam, rt, mgr);
                    long withoutLod = mgr.LastSubmittedTriangles;

                    int visibleNoLod = mgr.LastVisibleCount;
                    double triPerWith = visible > 0 ? withLod / (double)visible : 0;
                    double triPerWithout = visibleNoLod > 0 ? withoutLod / (double)visibleNoLod : 0;
                    double saving = triPerWithout > 0 ? 1.0 - triPerWith / triPerWithout : 0;

                    W($"{label}: 开 LOD 每实例 {triPerWith:F1} tri / 关 LOD 每实例 {triPerWithout:F1} tri " +
                      $"→ 省 {saving:P1}");
                }

                mgr.ConfigureLodForValidation(true, 100f, 25f, 8f);
                mgr.Initialize();
            }
            finally
            {
                if (rt != null) { rt.Release(); Object.DestroyImmediate(rt); }
                if (cam != null) Object.DestroyImmediate(cam.gameObject);
                Object.DestroyImmediate(root);
                Flush(sb);
            }

            if (Application.isBatchMode)
                EditorApplication.Exit(allPass ? 0 : 2);
        }

        private static void SubmitAndSettle(Camera cam, RenderTexture rt, GrassBRGManager mgr)
        {
            var request = new RenderPipeline.StandardRequest { destination = rt };
            if (RenderPipeline.SupportsRenderRequest(cam, request))
                RenderPipeline.SubmitRenderRequest(cam, request);
        }

        private static void SaveRender(RenderTexture rt, int size, string path)
        {
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false, true);
            tex.ReadPixels(new Rect(0, 0, size, size), 0, 0);
            tex.Apply();
            var png = tex.EncodeToPNG();
            Object.DestroyImmediate(tex);
            RenderTexture.active = prev;

            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllBytes(path, png);
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
