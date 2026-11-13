using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// 只读探针：核对"挖湖"是否真的落到了**渲染用的几何**上。
///
/// ── 为什么要单独验这一层 ──
/// 地表渲染走 GTS mesh（Terrain 的 drawHeightmap=false），所以：
///   - TerrainData 高度图被挖了 ≠ 玩家看到凹陷；
///   - 必须 GTS mesh 也重烘过，碗才看得见。
/// 本探针同时取样两处，把"数据改了但没重烘"这种静默失败暴露出来：
///   1. Terrain.SampleHeight（碰撞/逻辑高度）；
///   2. GTS mesh 顶点（真正画出来的高度）。
///
/// 用法：-executeMethod LakeGeometryProbe.Run
/// 输出 Logs/Lake/lake-geometry.txt
/// </summary>
public static class LakeGeometryProbe
{
    private const string Tag = "[LakeGeom]";
    private const string WorldScenePath = "Assets/Scenes/World.unity";
    private const string OutDir = "Logs/Lake";
    private const string ResultPath = OutDir + "/lake-geometry.txt";
    private const string GtsRoot = "Assets/GTS User Data/Scenes";

    public static void Run()
    {
        var sb = new StringBuilder();
        void W(string s) { Debug.Log($"{Tag} {s}"); sb.AppendLine(s); }

        try
        {
            Directory.CreateDirectory(OutDir);
            EditorSceneManager.OpenScene(WorldScenePath, OpenSceneMode.Single);

            // 加载所有 chunk（Additive，避免引用失效）
            var chunkPaths = new List<string>();
            foreach (var s in EditorBuildSettings.scenes)
            {
                if (!s.enabled) continue;
                if (s.path.EndsWith("World.unity")) continue;
                chunkPaths.Add(s.path);
            }
            foreach (var p in chunkPaths) if (File.Exists(p)) EditorSceneManager.OpenScene(p, OpenSceneMode.Additive);
            W($"加载 chunk = {chunkPaths.Count}");

            var terrains = UnityEngine.Object.FindObjectsOfType<Terrain>();
            W($"Terrain = {terrains.Length}");

            // ── 1. Terrain 高度（碰撞/逻辑）──
            W("");
            W("=== Terrain.SampleHeight ===");
            float SampleWorld(float x, float z)
            {
                foreach (var t in terrains)
                {
                    var p = t.transform.position; var sz = t.terrainData.size;
                    if (x < p.x || x > p.x + sz.x || z < p.z || z > p.z + sz.z) continue;
                    return p.y + t.SampleHeight(new Vector3(x, 0f, z));
                }
                return float.NaN;
            }
            float cx = LakeCarve.Center.x, cz = LakeCarve.Center.y;
            float tCenter = SampleWorld(cx, cz);
            float tHalf = SampleWorld(cx + LakeCarve.Radius * 0.5f, cz);
            float tRim = SampleWorld(cx + LakeCarve.Radius, cz);
            W($"  湖心   ({cx:F0},{cz:F0}) = {tCenter:F1}（期望碗底 {LakeCarve.WaterLevel - LakeCarve.Depth:F1}）");
            W($"  半径1/2              = {tHalf:F1}（期望 {LakeCarve.WaterLevel - LakeCarve.Depth * 0.75f:F1}）");
            W($"  半径R                = {tRim:F1}（期望 {LakeCarve.WaterLevel:F1}）");
            bool terrainOk = !float.IsNaN(tCenter) && Mathf.Abs(tCenter - (LakeCarve.WaterLevel - LakeCarve.Depth)) < 3f;
            W($"  → Terrain 层：{(terrainOk ? "PASS（凹陷已写入高度图）" : "FAIL")}");

            // ── 2. GTS mesh 顶点（真正渲染的几何）──
            W("");
            W("=== GTS mesh 顶点（渲染几何）===");
            // 仅检查 x 方向：湖心所在的 subTile 名可从 Terrain 位置推，但更稳的是扫全部 mesh
            float ext = LakeCarve.Radius;
            float meshMinInBowl = float.MaxValue;      // 湖盆内（r<=R）最低顶点
            float meshMinSample = float.NaN;
            int meshCount = 0, vertTotal = 0;
            var perMesh = new List<string>();

            foreach (var t in terrains)
            {
                var p = t.transform.position; var sz = t.terrainData.size;
                // 只扫覆盖湖盆的地形
                if (p.x + sz.x < cx - ext || p.x > cx + ext || p.z + sz.z < cz - ext || p.z > cz + ext) continue;

                // 找它下面的 GTS mesh
                var meshRoot = t.transform.Find("GTS_Mesh");
                if (meshRoot == null) { perMesh.Add($"{t.name}: 无 GTS_Mesh 节点"); continue; }
                var mfs = meshRoot.GetComponentsInChildren<MeshFilter>(true);
                foreach (var mf in mfs)
                {
                    var m = mf.sharedMesh;
                    if (m == null) continue;
                    meshCount++; vertTotal += m.vertexCount;
                    // mesh 顶点是相对 Terrain 的局部坐标（GTS BuildUnityMesh 从 0 起）
                    var verts = m.vertices;
                    float localMin = float.MaxValue, localMax = float.MinValue;
                    int inBowl = 0;
                    foreach (var v in verts)
                    {
                        float wx = p.x + v.x, wz = p.z + v.z;
                        float dx = wx - cx, dz = wz - cz;
                        if (dx * dx + dz * dz > ext * ext) continue;
                        float wy = p.y + v.y;
                        inBowl++;
                        if (wy < localMin) localMin = wy;
                        if (wy > localMax) localMax = wy;
                    }
                    if (inBowl > 0)
                    {
                        perMesh.Add($"{mf.name}: 湖盆内顶点 {inBowl}, Y {localMin:F1}~{localMax:F1}");
                        if (localMin < meshMinInBowl) meshMinInBowl = localMin;
                    }
                }
            }

            foreach (var s in perMesh) W("  " + s);
            W($"  mesh 数 = {meshCount}, 顶点合计 = {vertTotal:N0}");
            W($"  湖盆内最低顶点 Y = {(meshMinInBowl == float.MaxValue ? "无" : meshMinInBowl.ToString("F1"))}" +
              $"（期望约 {LakeCarve.WaterLevel - LakeCarve.Depth:F1}）");

            bool meshOk = meshMinInBowl != float.MaxValue && meshMinInBowl < LakeCarve.WaterLevel - LakeCarve.Depth * 0.5f;
            W($"  → GTS mesh 层：{(meshOk ? "PASS（渲染几何已重烘）" : "FAIL（渲染几何仍是旧地形）")}");

            // ── 3. 水面与湖盆的相对关系 ──
            W("");
            W("=== 水面 vs 湖盆 ===");
            W($"  水位 = {LakeCarve.WaterLevel:F1}，碗底 = {tCenter:F1}，水深 = {LakeCarve.WaterLevel - tCenter:F1}m");
            W($"  湖面半径 R = {LakeCarve.Radius}，水面 AABB 半边长 = {LakeCarve.WaterHalfExtent}");

            bool all = terrainOk && meshOk;
            W("");
            W($"==== 结论：{(all ? "PASS" : "FAIL")} ====");
            Flush(sb, all ? 0 : 2);
        }
        catch (Exception ex)
        {
            W($"异常：{ex}");
            W("==== 结论：FAIL ====");
            Flush(sb, 1);
        }
    }

    private static void Flush(StringBuilder sb, int code)
    {
        Directory.CreateDirectory(OutDir);
        File.WriteAllText(ResultPath, sb.ToString());
        EditorApplication.Exit(code);
    }
}
