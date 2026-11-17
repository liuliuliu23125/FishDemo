using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace FishDemo.EditorTools
{
    public static class GrassBudgetProbe
    {
        private const string Tag = "[GrassBudget]";
        private const string ResultPath = "Logs/Grass/grass-budget.txt";
        private const string WorldScenePath = "Assets/Scenes/World.unity";

        private const long TriangleBudgetPerFrame = 1_500_000;

        [MenuItem("FishDemo/Rendering/④ 统计草几何预算", false, 103)]
        public static void Run()
        {
            var sb = new StringBuilder();
            void W(string s) { Debug.Log($"{Tag} {s}"); sb.AppendLine(s); }

            EditorSceneManager.OpenScene(WorldScenePath, OpenSceneMode.Single);

            string chunkPath = null;
            foreach (var s in EditorBuildSettings.scenes)
            {
                if (s.enabled && !s.path.EndsWith("World.unity"))
                {
                    chunkPath = s.path;
                    break;
                }
            }
            if (chunkPath == null)
            {
                W("找不到 chunk 场景");
                Flush(sb);
                return;
            }
            EditorSceneManager.OpenScene(chunkPath, OpenSceneMode.Additive);
            W($"样本 chunk = {Path.GetFileNameWithoutExtension(chunkPath)}");

            var terrain = Object.FindObjectOfType<Terrain>();
            if (terrain == null || terrain.terrainData == null)
            {
                W("样本 chunk 内没有 Terrain");
                Flush(sb);
                return;
            }

            var td = terrain.terrainData;
            var protos = td.detailPrototypes;
            W($"detailPrototypes = {protos.Length}，detail 分辨率 = {td.detailWidth}x{td.detailHeight}");
            W("");

            long totalInstances = 0;
            long totalTriangles = 0;
            W($"{"Layer",5} {"Proto",-36} {"Instances",12} {"Verts",6} {"Tris",6} {"TrisTotal",14}");
            for (int l = 0; l < protos.Length; l++)
            {
                int[,] layer;
                try
                {
                    layer = td.GetDetailLayer(0, 0, td.detailWidth, td.detailHeight, l);
                }
                catch (System.Exception e)
                {
                    W($"{l,5} (层不存在，跳过): {e.GetType().Name}");
                    continue;
                }

                long count = 0;
                foreach (int v in layer) if (v > 0) count += v;

                var proto = protos[l];
                Mesh mesh = null;
                if (proto.usePrototypeMesh && proto.prototype != null)
                {
                    var mf = proto.prototype.GetComponent<MeshFilter>();
                    if (mf != null) mesh = mf.sharedMesh;
                }

                int verts = mesh != null ? mesh.vertexCount : 0;
                int tris = mesh != null ? mesh.triangles.Length / 3 : 0;
                long triTotal = count * tris;

                totalInstances += count;
                totalTriangles += triTotal;

                string name = mesh != null ? mesh.name
                    : (proto.prototypeTexture != null ? proto.prototypeTexture.name + "(texture)" : "(null)");
                W($"{l,5} {name.Truncate(36),-36} {count,12:N0} {verts,6} {tris,6} {triTotal,14:N0}");
            }

            W("");
            if (totalInstances > 0)
            {
                double avgTris = totalTriangles / (double)totalInstances;
                W($"单 chunk 实例总数（100% 密度全距离） = {totalInstances:N0}");
                W($"单 chunk 三角形总数                 = {totalTriangles:N0}");
                W($"**每实例加权平均三角形数**         = {avgTris:F1}");
                W($"→ 每实例平均顶点数约               = {avgTris * 1.5:F0}（按三角:顶点≈1:1.5 估）");

                W("");
                W("── 按 36 chunk 外推 ──");
                W($"100% 全量实例 = {totalInstances * 36:N0}");
                W($"100% 全量三角形 = {totalTriangles * 36:N0}");
                W($"10% 采样实例   = {totalInstances * 36 * 0.10:N0}");
                W($"10% 采样三角形 = {totalTriangles * 36 * 0.10:N0}");

                W("");
                W("── 按 1.5M 三角形/帧预算倒推 ──");
                double maxInstancesPerFrame = TriangleBudgetPerFrame / avgTris;
                W($"预算可容纳实例数 = 1,500,000 / {avgTris:F1} = {maxInstancesPerFrame:N0}");
                W($"（这是「全用当前 clump mesh」的上限；用 billboard 可放大 ~{avgTris / 2:F0} 倍）");

                W("");
                W("── 当前 Terrain 档（density=0.35, dist=70m）实际提交量 ──");
                double areaRatio = Mathf.Clamp01((terrain.detailObjectDistance * terrain.detailObjectDistance) / (512f * 512f));
                double visibleNow = totalInstances * terrain.detailObjectDensity * areaRatio;
                W($"单 chunk 可见实例 ≈ {visibleNow:N0}");
                W($"单 chunk 可见三角形 ≈ {visibleNow * avgTris:N0}");
                W($"36 chunk 若同时可见（SECTR 只加载近处，实际远小于此）≈ {visibleNow * avgTris * 36:N0}");
            }

            Flush(sb);
        }

        private static string Truncate(this string s, int len) =>
            s.Length <= len ? s : s.Substring(0, len - 1) + "…";

        private static void Flush(StringBuilder sb)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ResultPath));
            File.WriteAllText(ResultPath, sb.ToString());
            if (Application.isBatchMode)
                EditorApplication.Exit(0);
        }
    }
}
