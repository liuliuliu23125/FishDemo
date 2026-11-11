using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// 诊断 Terrain Detail（细节物体）的实际配置。
///
/// 背景：用户观察到 PW_LawnGrass_CloverFlower 被单独绘制两次、用独立 shader，
/// 怀疑它没被合批。需要确认：
///   1. 每个 chunk 的 TerrainData 里到底登记了哪些 detail 原型；
///   2. 每个原型是 Mesh 还是 Texture、用的什么 shader；
///   3. detail 渲染是否开着（DetailObjectDistance > 0）；
///   4. 与 BRG 草是否重叠（同一片地表被画两遍 = 双份几何）。
///
/// 用法：-executeMethod TerrainDetailProbe.Run
/// 结果写 Logs/terrain-detail-probe.txt
/// </summary>
public static class TerrainDetailProbe
{
    private const string Tag = "[DetailProbe]";
    private const string WorldScenePath = "Assets/Scenes/World.unity";
    private const string ResultPath = "Logs/terrain-detail-probe.txt";

    public static void Run()
    {
        var sb = new StringBuilder();
        void W(string s) { Debug.Log($"{Tag} {s}"); sb.AppendLine(s); }

        var scene = EditorSceneManager.OpenScene(WorldScenePath, OpenSceneMode.Single);
        var chunks = Object.FindObjectsOfType<SECTR_Chunk>().OrderBy(c => c.ScenePath).ToList();
        W($"主场景 chunk 数 = {chunks.Count}");

        // 只查前 2 个 chunk 就够：同一个 Gaia 预设生成的 TerrainData，原型配置一致。
        int inspected = 0;
        foreach (var c in chunks)
        {
            if (inspected >= 2) break;
            if (string.IsNullOrEmpty(c.NodeName) || !File.Exists(c.NodeName)) continue;

            var cs = EditorSceneManager.OpenScene(c.NodeName, OpenSceneMode.Additive);
            var terrains = Object.FindObjectsOfType<Terrain>()
                .Where(t => t.gameObject.scene == cs).ToArray();

            W("");
            W($"===== chunk: {Path.GetFileName(c.NodeName)} =====");
            W($"Terrain 组件数 = {terrains.Length}");

            foreach (var t in terrains)
            {
                var td = t.terrainData;
                if (td == null) continue;

                W($"-- {t.name} --");
                W($"  DetailObjectDistance = {t.detailObjectDistance}");
                W($"  DetailObjectDensity  = {t.detailObjectDensity}");
                W($"  DrawInstanced        = {t.drawInstanced}");
                W($"  DrawHeightmap        = {t.drawHeightmap}");
                W($"  detailResolution     = {td.detailResolution}");
                W($"  detailPatchCount     = {(td.detailPatchCount)}");

                var protos = td.detailPrototypes;
                W($"  detailPrototypes 数 = {protos.Length}");
                for (int i = 0; i < protos.Length; i++)
                {
                    var p = protos[i];
                    string meshName = p.prototype != null ? p.prototype.name : "(null)";
                    string texName = p.prototypeTexture != null ? p.prototypeTexture.name : "(null)";
                    string shader = "(无材质)";
                    if (p.prototype != null)
                    {
                        // prototype 是 GameObject，材质挂在它的 MeshRenderer 上。
                        var mr = p.prototype.GetComponent<MeshRenderer>();
                        if (mr != null && mr.sharedMaterial != null)
                        {
                            var m = mr.sharedMaterial;
                            shader = m.shader != null ? m.shader.name : "(shader null)";
                        }
                    }
                    W($"    [{i}] usePrototypeMesh={p.usePrototypeMesh} mesh={meshName} tex={texName}");
                    W($"         shader={shader}  minW={p.minWidth:F2} maxW={p.maxWidth:F2} " +
                       $"minH={p.minHeight:F2} maxH={p.maxHeight:F2} healthy={p.healthyColor} dry={p.dryColor}");
                }

                // 各层 detail 实例数（Layer 0 = 第一层）
                for (int layer = 0; layer < td.detailPrototypes.Length; layer++)
                {
                    try
                    {
                        var map = td.GetDetailLayer(0, 0, td.detailWidth, td.detailHeight, layer);
                        long sum = 0; int max = 0;
                        foreach (var v in map) { sum += v; if (v > max) max = v; }
                        W($"    detailLayer[{layer}] 实例总数={sum:N0} 单格最大={max}");
                    }
                    catch (System.Exception e)
                    {
                        W($"    detailLayer[{layer}] 读取失败: {e.Message}");
                    }
                }
            }

            EditorSceneManager.CloseScene(cs, true);
            inspected++;
        }

        // 主场景里的 Terrain（应无，地形都在 chunk 里）
        var worldTerrains = Object.FindObjectsOfType<Terrain>()
            .Where(t => t.gameObject.scene == scene).ToArray();
        W("");
        W($"主场景 Terrain 数 = {worldTerrains.Length}（应为 0）");

        Flush(sb);
        EditorApplication.Exit(0);
    }

    private static void Flush(StringBuilder sb)
    {
        Directory.CreateDirectory("Logs");
        File.WriteAllText(ResultPath, sb.ToString());
    }
}
