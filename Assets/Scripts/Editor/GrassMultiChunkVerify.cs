using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using FishDemo.Rendering;

namespace FishDemo.EditorTools
{
    /// <summary>
    /// 多 chunk 覆盖验证：确认「每个 chunk 都有草」且「从不同相机位置都能看到草」。
    ///
    /// 用户反馈的两个问题：
    ///   1. 不是每个 chunk 都有草 —— 早先只加载了 files[0]。
    ///   2. 只看见一部分 —— 单 chunk 只有 511m，且 LOD 在远处剔除。
    ///
    /// 本脚本对每个 chunk 的包围盒中心各渲一帧，统计洋红像素。
    /// 每个 chunk 都必须 > 0，否则说明该 chunk 数据没进 BRG。
    ///
    /// 用法：-executeMethod FishDemo.EditorTools.GrassMultiChunkVerify.Run
    /// </summary>
    public static class GrassMultiChunkVerify
    {
        private const string Tag = "[GrassMulti]";
        private const string ResultPath = "Logs/Grass/multi-chunk-verify.txt";
        private const string ShaderPath = "Assets/Grasses/GrassBRG.shader";
        private const string GrassDataDir = "Assets/Scenes/World/GrassData";

        [MenuItem("FishDemo/Rendering/⑬ 验证多 chunk 覆盖", false, 115)]
        public static void Run()
        {
            var sb = new StringBuilder();
            void W(string s) { Debug.Log($"{Tag} {s}"); sb.AppendLine(s); }

            string[] files = Directory.GetFiles(GrassDataDir, "*.bytes");
            if (files.Length == 0) { W("无烘焙数据"); Flush(sb); return; }

            var chunks = new List<GrassChunkData>(files.Length);
            foreach (var f in files)
                chunks.Add(GrassChunkData.Read(File.ReadAllBytes(f), Path.GetFileNameWithoutExtension(f)));

            var shader = AssetDatabase.LoadAssetAtPath<Shader>(ShaderPath);
            if (shader == null) { W("找不到 shader"); Flush(sb); return; }

            foreach (var m in Object.FindObjectsOfType<GrassBRGManager>())
                m.enabled = false;

            var root = new GameObject("GrassMulti_Verify");
            Camera cam = null;
            RenderTexture rt = null;
            var prevActive = RenderTexture.active;

            try
            {
                var mat = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
                mat.SetColor("_BaseColor", new Color(1f, 0f, 1f, 1f)); // 洋红区分地形草

                var mgr = root.AddComponent<GrassBRGManager>();
                mgr.SetMaterialForValidation(mat);
                mgr.ConfigureLodForValidation(true, 100f, 25f, 8f);
                mgr.SetChunkDataList(chunks);
                if (!mgr.IsReady) { W("初始化失败"); Flush(sb); return; }

                W($"已加载 {chunks.Count} 个 chunk，总实例 {mgr.InstanceCount:N0}");
                W("");

                const int size = 256;
                rt = new RenderTexture(size, size, 24, RenderTextureFormat.ARGB32);
                rt.Create();

                var camGo = new GameObject("GrassMulti_Cam");
                cam = camGo.AddComponent<Camera>();
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = Color.black;
                cam.fieldOfView = 60f;
                cam.nearClipPlane = 0.3f;
                cam.farClipPlane = 3000f;
                cam.cullingMask = ~0;

                W($"{"#",3} {"chunk",-42} {"实例",10} {"可见",8} {"洋红",8}");
                int empty = 0;
                int checkedCount = 0;

                for (int i = 0; i < chunks.Count; i++)
                {
                    var c = chunks[i];
                    if (c.Instances.Length == 0) { W($"{i,3} {Short(c.ChunkName),-42} 空数据"); empty++; continue; }

                    // 站到该 chunk 数据包围盒中心最近实例的地表，人眼高度，略俯视
                    Vector3 center = c.Bounds.center;
                    Vector3 ground = center;
                    float best = float.MaxValue;
                    foreach (var inst in c.Instances)
                    {
                        float dx = inst.Position.x - center.x, dz = inst.Position.z - center.z;
                        float d2 = dx * dx + dz * dz;
                        if (d2 < best)
                        {
                            best = d2;
                            ground = new Vector3(inst.Position.x, inst.Position.y, inst.Position.z);
                        }
                    }

                    cam.transform.position = new Vector3(ground.x, ground.y + 1.7f, ground.z);
                    cam.transform.rotation = Quaternion.Euler(12f, 0f, 0f);

                    var request = new RenderPipeline.StandardRequest { destination = rt };
                    if (!RenderPipeline.SupportsRenderRequest(cam, request))
                    { W("不支持 StandardRequest"); break; }
                    // 渲两次：首次剔除 Job 可能未完成（M2 踩过的坑）
                    RenderPipeline.SubmitRenderRequest(cam, request);
                    RenderPipeline.SubmitRenderRequest(cam, request);

                    RenderTexture.active = rt;
                    var tex = new Texture2D(size, size, TextureFormat.RGBA32, false, true);
                    tex.ReadPixels(new Rect(0, 0, size, size), 0, 0);
                    tex.Apply();
                    var px = tex.GetPixels32();
                    Object.DestroyImmediate(tex);
                    RenderTexture.active = prevActive;

                    int magenta = 0;
                    for (int k = 0; k < px.Length; k++)
                    {
                        var p = px[k];
                        if (p.r > 100 && p.b > 100 && p.g < 60) magenta++;
                    }

                    int vis = mgr.LastVisibleCount;
                    checkedCount++;
                    if (magenta == 0) empty++;
                    W($"{i,3} {Short(c.ChunkName),-42} {c.Instances.Length,10:N0} {vis,8:N0} {magenta,8:N0}");
                }

                W("");
                bool pass = empty == 0 && checkedCount == chunks.Count;
                W($"检查 {checkedCount} 个 chunk，无草的 {empty} 个");
                W($"==== 结论：{(pass ? "PASS" : "FAIL")} ====");
                Flush(sb);
                if (Application.isBatchMode) EditorApplication.Exit(pass ? 0 : 2);
            }
            finally
            {
                RenderTexture.active = prevActive;
                if (rt != null) { rt.Release(); Object.DestroyImmediate(rt); }
                if (cam != null) Object.DestroyImmediate(cam.gameObject);
                Object.DestroyImmediate(root);
                foreach (var m in Object.FindObjectsOfType<GrassBRGManager>()) m.enabled = true;
            }
        }

        private static string Short(string name)
        {
            // "World_SECTR Terrain_0_0-20261109 - 162007 0-0-0 Sector" -> "T0_0 0-0-0"
            if (string.IsNullOrEmpty(name)) return "(null)";
            var parts = name.Split(' ');
            if (parts.Length >= 4)
                return $"{parts[1].Replace("Terrain_", "T")} {parts[parts.Length - 2]}";
            return name.Length > 40 ? name.Substring(0, 40) : name;
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
