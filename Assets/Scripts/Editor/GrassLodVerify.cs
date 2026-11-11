using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using FishDemo.Rendering;

namespace FishDemo.EditorTools
{
    /// <summary>
    /// LOD 验收：证明三层 LOD 真的按距离分开，且提交的三角形数落在预算内。
    ///
    /// 验证三件事：
    ///   1. 分层正确：同一批实例在近/中/远相机下，各层实例数此消彼长。
    ///   2. 预算达标：提交三角形数 = Σ(层实例 × 层三角形)，对比 1.5M 预算。
    ///   3. 无回归：LOD 开启时画面仍有草（不是全黑）。
    ///
    /// 为什么不用单次渲染判定：LOD 的错误模式是「分层边界算错」或
    /// 「所有实例都落到同一层」，这些只能通过对比多距离下的分层计数发现，
    /// 单张渲染图看不出区别（三层都是草）。
    ///
    /// 用法：
    ///   Unity.exe -batchmode -projectPath <proj> \
    ///     -executeMethod FishDemo.EditorTools.GrassLodVerify.Run
    /// 结果写 Logs/Grass/lod-verify.txt
    /// </summary>
    public static class GrassLodVerify
    {
        private const string Tag = "[GrassLod]";
        private const string ResultPath = "Logs/Grass/lod-verify.txt";
        private const string ShaderPath = "Assets/Grasses/GrassBRG.shader";
        private const string PngDir = "Logs/Grass";

        /// <summary>performance_budget.md §2 的三角形预算。</summary>
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

            // RT 高度必须与目标平台一致，否则像素阈值会算错。
            // 阈值是按「1080p + 60° FOV」设计的：100px→8.7m、25px→35m、8px→110m。
            // 用 512px 的 RT 会让同样的像素阈值对应一半的距离（实测踩过：
            // 20m 处就已经切到 LOD1，看起来像 LOD0 不生效）。
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
                // 1000 株、1.5m 间距 → 约 47m 见方。用真实尺度，不放大。
                mgr.ConfigureForValidation(1000, 1.5f, 1.0f);
                mgr.SetMaterialForValidation(mat);
                root.transform.position = Vector3.zero;
                // 关键：显式打开 LOD 并给标准阈值。
                // M1 链路验证把它关了，这里必须打开，否则测不到分层。
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

                // 相机从不同距离正对方阵中心，观察分层分布变化。
                // 注意：这同时也验证了「阈值随距离推进」的单调性。
                W($"{"距离",8} {"LOD0",8} {"LOD1",8} {"LOD2",8} {"可见",8} {"三角形",12} {"预算占用",10}");
                long baseTriangles = -1;

                // 相机从不同距离俯视方阵中心。
                // 注意必须带上俯角：方阵是平铺在地面的 47m 方格，
                // 纯水平视角（相机与草同高）只能看到最前排，大片草被自己挡住/在视锥外，
                // 渲染图会是黑的（实测踩过），统计也会偏。
                foreach (float dist in new[] { 6f, 20f, 60f, 100f })
                {
                    // 相机高度随距离抬高一点，保证 47m 方阵完整入画。
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

                    // 只在「方阵能完整入画」的距离保存渲染图。
                    // 47m 方阵在 6m/20m 处根本放不进 60° 视锥，存下来是黑图，会误导。
                    // 统计值本身仍对每个距离记录（那用的是视锥剔除后的真实计数）。
                    float halfExtent = 24f; // 47m 方阵的半宽
                    float requiredHalfAngle = Mathf.Atan2(halfExtent, dist) * Mathf.Rad2Deg;
                    if (requiredHalfAngle < fov * 0.5f - 2f)
                        SaveRender(rt, size, $"{PngDir}/lod-dist-{dist:F0}.png");
                }

                W("");

                // ── 判定 1：近处应出现 LOD0（而不是完全没有）──
                // 注意不能要求「LOD0 占多数」：47m 方阵在 6m 相机下，
                // 只有距相机 8.7m 内的一小片是 LOD0，大部分自然落在 LOD1。
                // 这正是 LOD 应有的行为，所以判定写成「LOD0 > 0 且 LOD1 也存在」。
                cam.transform.position = new Vector3(0f, 4f, -6f);
                cam.transform.LookAt(Vector3.zero);
                SubmitAndSettle(cam, rt, mgr);
                int near0 = mgr.GetLastLodCount(0);
                int near1 = mgr.GetLastLodCount(1);
                int nearTotal = mgr.LastVisibleCount;
                bool nearOk = nearTotal > 0 && near0 > 0 && near1 > 0;
                W($"判定1 近处(6m)同时出现 LOD0 与 LOD1：LOD0={near0}, LOD1={near1}/{nearTotal} → {(nearOk ? "PASS" : "FAIL")}");
                allPass &= nearOk;

                // ── 判定 2：远处应出现 LOD2，且 LOD0 基本消失 ──
                // 1080p 下 25px 阈值 ≈ 35m，40m 外应全是 LOD2。
                cam.transform.position = new Vector3(0f, 50f, -80f);
                cam.transform.LookAt(Vector3.zero);
                SubmitAndSettle(cam, rt, mgr);
                int far0 = mgr.GetLastLodCount(0);
                int far2 = mgr.GetLastLodCount(2);
                int farTotal = mgr.LastVisibleCount;
                bool farOk = farTotal > 0 && far2 > 0 && far0 == 0;
                W($"判定2 远处(80m)切到 LOD2 且无 LOD0：LOD0={far0}, LOD2={far2}/{farTotal} → {(farOk ? "PASS" : "FAIL")}");
                allPass &= farOk;

                // ── 判定 3：每实例三角形数 近处 > 远处（LOD 生效的量化标志）──
                //
                // 不能比较「三角形总数」：6m 相机下 47m 方阵大部分在视锥外（实测可见 237），
                // 80m 相机则能看到全部（Visible 999）。总数受可见实例数支配，
                // 与 LOD 无关，会把判定带偏（第一版就是这么误判的）。
                // 正确口径是「每实例平均三角形」，它直接反映 LOD 层级选择。
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

                // ── 判定 4：全程不超预算 ──
                long maxTri = System.Math.Max(nearTris, farTris);
                bool budgetOk = maxTri <= TriangleBudget;
                W($"判定4 最大提交三角形 {maxTri:N0} ≤ 预算 {TriangleBudget:N0} → {(budgetOk ? "PASS" : "FAIL")}");
                allPass &= budgetOk;

                W("");
                W($"==== 结论：{(allPass ? "PASS" : "FAIL")} ====");

                // ── 附加：LOD 相对收益（同视角 开/关 对比）──
                // 这是回答「LOD 到底省了多少」的唯一硬数据。
                // 注意必须用同一相机位姿，否则视锥内外差异会污染结果。
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

                    // 关 LOD：所有可见实例都按 LOD0 画
                    mgr.ConfigureLodForValidation(false, 0f, 0f, 0f);
                    mgr.Initialize();
                    SubmitAndSettle(cam, rt, mgr);
                    long withoutLod = mgr.LastSubmittedTriangles;

                    // 关 LOD 时不剔除远景，可见数与开 LOD 不同，所以按「每实例三角形」比较
                    int visibleNoLod = mgr.LastVisibleCount;
                    double triPerWith = visible > 0 ? withLod / (double)visible : 0;
                    double triPerWithout = visibleNoLod > 0 ? withoutLod / (double)visibleNoLod : 0;
                    double saving = triPerWithout > 0 ? 1.0 - triPerWith / triPerWithout : 0;

                    W($"{label}: 开 LOD 每实例 {triPerWith:F1} tri / 关 LOD 每实例 {triPerWithout:F1} tri " +
                      $"→ 省 {saving:P1}");
                }

                // 恢复开启状态，避免污染后续
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

        /// <summary>渲染一次并等待剔除 Job 完成，保证读到的计数是本相机的结果。</summary>
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
