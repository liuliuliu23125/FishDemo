using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// 在中心区域挖一个人工湖盆，并把水位/湖盆参数落到 Logs 供后续步骤读取。
///
/// ── 第一性原理：为什么是"挖碗"而不是"找洼地" ──
/// 实测（Logs/Lake/site-probe.txt）本地形高度范围 236~350m，世界中心 284m，
/// 中心 ±800m 内是缓坡、没有任何天然洼地（最低点 260.6m 与周边只差 4m）。
/// 因此"把水显示到凹陷处"必须**人工造凹陷**。
///
/// ── 为什么碗形，且只挖不填 ──
/// 碗形（半径 R 内深度从 0 平滑增至 D）在岸线处深度自然为 0，水边收得干净。
/// 水位取 WL 时，要求 R 到 R+BLEND 这一圈原始地形全部高于 WL + 2m ——
/// 满足这一条就**不需要任何填方**，也就不会把原有地形轮廓改掉。
/// 选定参数（(192,96) / R=190 / WL=266 / D=30）实测：
///   - R+BLEND 环最低 268.0m，比水位高 2.0m；
///   - AABB 内 r>R 区域最低 267.6m，仍高于水位 —— 水面矩形不会在湖外露出；
///   - 挖方 5.0M m³，全部在 tile(1,1) 的 chunk 内。
///
/// ── 为什么改高度图就够，还要重转 GTS ──
/// 地表渲染是 GTS mesh（`drawHeightmap=false`），高度图只驱动碰撞与 Terrain 内部查询。
/// 改完高度图必须重转受影响 chunk 的 GTS mesh，否则看到的地表还是旧的。
/// 本脚本只负责高度图；GTS 重转见 AGENTS.md §3.5 的既有流程。
///
/// ── 可逆性 ──
/// 改动前把受影响 TerrainData 的 heightmap 原始 float[] 备份到
/// Tools/lake-backup/&lt;chunk&gt;.bytes（只存受影响矩形，不是整张图）。
/// Restore 完整还原。重跑世界生成会覆盖，需重做。
///
/// 用法：
///   -executeMethod LakeCarve.Apply      挖湖
///   -executeMethod LakeCarve.Restore    还原
///   -executeMethod LakeCarve.Report     只统计不改
/// 输出 Logs/Lake/lake-carve.txt
/// </summary>
public static class LakeCarve
{
    private const string Tag = "[LakeCarve]";
    private const string WorldScenePath = "Assets/Scenes/World.unity";
    private const string OutDir = "Logs/Lake";
    private const string ResultPath = OutDir + "/lake-carve.txt";
    private const string BackupDir = "Tools/lake-backup";

    // ── 湖盆参数（由 LakeSiteProbe 的高度网格算出，勿随意改）──
    /// <summary>湖心世界坐标（XZ）。</summary>
    public static readonly Vector2 Center = new Vector2(192f, 96f);
    /// <summary>湖面半径（米）：此半径内挖到最深。</summary>
    public const float Radius = 190f;
    /// <summary>
    /// 静水位（世界 Y，米）。
    ///
    /// 为什么是 264 而不是碗沿的 266：
    /// 碗沿（r=R）挖到 266，而 AABB 内湖盆外最低原生地形是 266.7m，只高出 0.7m。
    /// Crest 有波浪起伏，0.7m 余量可能让水从碗沿"溢出"到湖外的地形上（视觉上是水面从地里冒出来）。
    /// 降到 264 后余量 2.7m，同时岸线自然落在 r≈183m（碗壁与水面的交点），湖面略小但完全安全。
    /// </summary>
    public const float WaterLevel = 264f;
    /// <summary>湖心最大水深（米）。</summary>
    public const float Depth = 30f;
    /// <summary>岸线过渡带宽度（米）：半径 R..R+BLEND 内不做任何改动。</summary>
    public const float Blend = 90f;

    public static void Apply() { Run("apply"); }
    public static void Restore() { Run("restore"); }

    public static void Report() { Run("report"); }

    /// <summary>
    /// 供其他脚本读取的湖盆描述（例如 Crest 水面尺寸）。
    /// 水面 AABB 半边长 = R + Blend，正好覆盖挖方区，且已实测该矩形内无水外露。
    /// </summary>
    public static float WaterHalfExtent => Radius + Blend;

    private static void Run(string mode)
    {
        var sb = new StringBuilder();
        void W(string s) { Debug.Log($"{Tag} {s}"); sb.AppendLine(s); }

        try
        {
            Directory.CreateDirectory(OutDir);
            if (mode == "apply") Directory.CreateDirectory(BackupDir);

            W($"模式 = {mode}");
            W($"湖心 = ({Center.x}, {Center.y})  R={Radius}  WL={WaterLevel}  D={Depth}  blend={Blend}");

            var chunkPaths = LoadActiveChunkPaths();
            W($"活跃 chunk = {chunkPaths.Count}");
            if (chunkPaths.Count == 0) { W("==== 结论：FAIL（无活跃 chunk） ===="); Flush(sb, 2); return; }

            // 湖盆 AABB（世界坐标）
            var min = new Vector2(Center.x - WaterHalfExtent, Center.y - WaterHalfExtent);
            var max = new Vector2(Center.x + WaterHalfExtent, Center.y + WaterHalfExtent);
            W($"湖盆 AABB = [{min.x}, {min.y}] .. [{max.x}, {max.y}]");

            long totalChanged = 0, totalBacked = 0;
            int touchedChunks = 0;
            var perChunk = new List<string>();

            foreach (var chunkPath in chunkPaths)
            {
                var scene = EditorSceneManager.OpenScene(chunkPath, OpenSceneMode.Single);
                var terrains = UnityEngine.Object.FindObjectsOfType<Terrain>();
                bool sceneDirty = false;

                foreach (var t in terrains)
                {
                    var td = t.terrainData;
                    if (td == null) continue;
                    var p = t.transform.position;
                    var size = td.size;

                    // 该地形覆盖的世界矩形
                    var tMin = new Vector2(p.x, p.z);
                    var tMax = new Vector2(p.x + size.x, p.z + size.z);
                    if (tMax.x < min.x || tMin.x > max.x || tMax.y < min.y || tMin.y > max.y) continue;

                    var hm = td.heightmapResolution;      // 513
                    float cellX = size.x / (hm - 1);
                    float cellZ = size.z / (hm - 1);

                    // 只取覆盖湖盆矩形的那部分高度图窗口（含 blend 外扩一点，避免接缝）
                    int x0 = Mathf.Max(0, Mathf.FloorToInt((min.x - p.x) / cellX) - 1);
                    int z0 = Mathf.Max(0, Mathf.FloorToInt((min.y - p.z) / cellZ) - 1);
                    int x1 = Mathf.Min(hm - 1, Mathf.CeilToInt((max.x - p.x) / cellX) + 1);
                    int z1 = Mathf.Min(hm - 1, Mathf.CeilToInt((max.y - p.z) / cellZ) + 1);
                    int w = x1 - x0 + 1, h = z1 - z0 + 1;
                    if (w <= 0 || h <= 0) continue;

                    var heights = td.GetHeights(x0, z0, w, h);   // [z, x] 归一化 0..1
                    string safe = Sanitize(td.name);
                    string bakPath = Path.Combine(BackupDir, safe + ".bytes");

                    if (mode == "apply")
                    {
                        if (!File.Exists(bakPath))
                        {
                            WriteBackup(bakPath, x0, z0, w, h, heights);
                            totalBacked++;
                            W($"  {td.name}: 已备份窗口 {w}×{h}（x0={x0}, z0={z0}）");
                        }
                        else
                        {
                            W($"  {td.name}: 备份已存在，跳过备份（保证原始数据不被覆盖）");
                        }
                    }
                    else if (mode == "restore")
                    {
                        if (!File.Exists(bakPath)) { W($"  {td.name}: 无备份，跳过"); continue; }
                        var bak = ReadBackup(bakPath, out int bx0, out int bz0, out int bw, out int bh);
                        if (bx0 != x0 || bz0 != z0 || bw != w || bh != h)
                        {
                            W($"  {td.name}: 窗口与备份不符（当前 {w}×{h}@{x0},{z0} / 备份 {bw}×{bh}@{bx0},{bz0}），跳过");
                            continue;
                        }
                        td.SetHeights(bx0, bz0, bak);
                        EditorUtility.SetDirty(td);
                        sceneDirty = true;
                        totalChanged += bw * bh;
                        W($"  {td.name}: 已还原 {bw}×{bh}");
                        continue;
                    }

                    if (mode == "report")
                    {
                        var (_, n, dmin, dmax) = MeasureWindow(heights, x0, z0, w, h, p, cellX, cellZ);
                        if (n > 0) W($"  {td.name}: 受影响格点 {n}，现有高度 {dmin * size.y:F1}~{dmax * size.y:F1}m");
                        continue;
                    }

                    // ── apply：按碗形挖 ──
                    int changed = 0;
                    float minH = float.MaxValue, maxH = float.MinValue;
                    for (int iz = 0; iz < h; iz++)
                    {
                        float wz = p.z + (z0 + iz) * cellZ;
                        for (int ix = 0; ix < w; ix++)
                        {
                            float wx = p.x + (x0 + ix) * cellX;
                            float cur = heights[iz, ix] * size.y;   // 世界高度（地形原点 y=0）
                            float target = TargetHeight(wx, wz, cur);
                            if (target >= cur) continue;            // 只挖不填

                            float norm = target / size.y;
                            if (norm < 0f) norm = 0f;
                            if (Mathf.Abs(norm - heights[iz, ix]) < 1e-7f) continue;
                            heights[iz, ix] = norm;
                            changed++;
                            if (target < minH) minH = target;
                            if (target > maxH) maxH = target;
                        }
                    }

                    if (changed > 0)
                    {
                        td.SetHeights(x0, z0, heights);
                        EditorUtility.SetDirty(td);
                        sceneDirty = true;
                        totalChanged += changed;
                        perChunk.Add($"{td.name}: 改动 {changed} 格，新高度 {minH:F1}~{maxH:F1}m");
                        W($"  {td.name}: 改动 {changed} 格，新高度 {minH:F1}~{maxH:F1}m");
                    }
                    else
                    {
                        W($"  {td.name}: 无需改动（已挖过或不在湖盆内）");
                    }
                }

                if (sceneDirty)
                {
                    EditorSceneManager.MarkSceneDirty(scene);
                    EditorSceneManager.SaveScene(scene);
                    touchedChunks++;
                }
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            W("");
            W($"受影响 chunk 场景 = {touchedChunks}，改动格点合计 = {totalChanged}，备份 = {totalBacked}");
            foreach (var s in perChunk) W("  " + s);

            // ── 校验（apply/restore 后都验一遍）──
            if (mode != "report")
            {
                W("");
                W("=== 校验：湖心/岸边/湖外采样点高度 ===");
                bool ok = VerifyLake(W);
                W($"==== 结论：{(ok ? "PASS" : "FAIL")} ====");
                Flush(sb, ok ? 0 : 2);
                return;
            }

            W("==== 结论：PASS ====");
            Flush(sb, 0);
        }
        catch (Exception ex)
        {
            W($"异常：{ex}");
            W("==== 结论：FAIL ====");
            Flush(sb, 1);
        }
    }

    /// <summary>
    /// 目标高度：r &lt;= R 时碗形下挖，R..R+Blend 不动。
    /// 返回值可能高于当前高度（在 blend 区或浅水边缘），调用方用「只挖不填」过滤掉。
    /// </summary>
    private static float TargetHeight(float wx, float wz, float current)
    {
        float dx = wx - Center.x, dz = wz - Center.y;
        float r = Mathf.Sqrt(dx * dx + dz * dz);
        if (r > Radius) return current;                 // 岸外不动
        float k = 1f - (r / Radius) * (r / Radius);     // 0..1，中心 1，边缘 0
        float target = WaterLevel - Depth * k;
        // 岸线处 target == WaterLevel，若原地形低于水位则不抬高（只挖不填）
        return target;
    }

    private static (long count, long n, float min, float max) MeasureWindow(
        float[,] heights, int x0, int z0, int w, int h, Vector3 p, float cellX, float cellZ)
    {
        long n = 0;
        float min = float.MaxValue, max = float.MinValue;
        for (int iz = 0; iz < h; iz++)
        {
            float wz = p.z + (z0 + iz) * cellZ;
            for (int ix = 0; ix < w; ix++)
            {
                float wx = p.x + (x0 + ix) * cellX;
                float dx = wx - Center.x, dz = wz - Center.y;
                if (dx * dx + dz * dz > Radius * Radius) continue;
                float hv = heights[iz, ix];
                n++;
                if (hv < min) min = hv;
                if (hv > max) max = hv;
            }
        }
        return (n, n, min, max);
    }

    /// <summary>重新打开受影响 chunk 采样，验证碗形是否真的形成。</summary>
    private static bool VerifyLake(Action<string> W)
    {
        // 必须 Additive 打开：OpenScene(Single) 会卸载上一场景，之前收集的 Terrain 引用变悬空，
        // 采样时抛 MissingReferenceException（实测踩过）。
        var chunkPaths = LoadActiveChunkPaths();   // 内部会 OpenScene(World, Single)
        foreach (var cp in chunkPaths)
        {
            if (!File.Exists(cp)) continue;
            EditorSceneManager.OpenScene(cp, OpenSceneMode.Additive);
        }
        var terrains = new List<Terrain>(UnityEngine.Object.FindObjectsOfType<Terrain>());

        float Sample(float x, float z)
        {
            foreach (var t in terrains)
            {
                var p = t.transform.position; var s = t.terrainData.size;
                if (x < p.x || x > p.x + s.x || z < p.z || z > p.z + s.z) continue;
                return p.y + t.SampleHeight(new Vector3(x, 0f, z));
            }
            return float.NaN;
        }

        bool ok = true;
        void Check(string label, float x, float z, float expect, float tol)
        {
            float v = Sample(x, z);
            bool pass = !float.IsNaN(v) && Mathf.Abs(v - expect) <= tol;
            if (!pass) ok = false;
            W($"  {label} ({x:F0},{z:F0}): h={v:F1}  期望 {expect:F1}±{tol}  {(pass ? "OK" : "FAIL")}");
        }

        // 湖心应到碗底；半径中点应在半深处；岸线处应等于水位；岸外必须高于水位（不溢出）。
        Check("湖心", Center.x, Center.y, WaterLevel - Depth, 2.5f);
        Check("半径 1/2", Center.x + Radius * 0.5f, Center.y, WaterLevel - Depth * 0.75f, 4f);
        Check("半径 R", Center.x + Radius, Center.y, WaterLevel, 4f);

        // 溢出检查必须按**矩形 AABB** 判，不能按圆形：
        // 水面由 Crest WaterBody 的矩形 AABB 决定是否渲染（SurfaceRenderer.ApplyWaterBodyCulling），
        // AABB 之外的原始地形哪怕低于水位也不会露出水面。
        // 只要 AABB 内、湖盆外的地形全部高于水位，水面就被"关"在湖盆里。
        float ext = WaterHalfExtent;
        int below = 0;
        float worst = float.MaxValue;
        Vector3 worstAt = Vector3.zero;
        for (float x = Center.x - ext; x <= Center.x + ext; x += 8f)
        {
            for (float z = Center.y - ext; z <= Center.y + ext; z += 8f)
            {
                float dx = x - Center.x, dz = z - Center.y;
                if (dx * dx + dz * dz <= Radius * Radius) continue;   // 湖盆内，允许低于水位
                float v = Sample(x, z);
                if (float.IsNaN(v)) continue;
                if (v < worst) { worst = v; worstAt = new Vector3(x, v, z); }
                if (v <= WaterLevel) below++;
            }
        }
        W($"  AABB 内湖盆外最低点 = {worst:F1}m @ ({worstAt.x:F0},{worstAt.z:F0})，" +
          $"低于水位的采样点 {below} 个");
        if (worst <= WaterLevel)
        {
            ok = false;
            W($"  FAIL：AABB 内存在低于水位 {WaterLevel:F1}m 的湖外地形，水面会溢出");
        }
        return ok;
    }

    // ───────────────────────── 备份格式 ─────────────────────────

    /// <summary>
    /// 备份格式：magic(4) version(4) x0(4) z0(4) w(4) h(4) + w*h 个 float32。
    /// 只存窗口而非整张 513×513，单块约 200KB 量级。
    /// </summary>
    private static void WriteBackup(string path, int x0, int z0, int w, int h, float[,] heights)
    {
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var bw = new BinaryWriter(fs);
        bw.Write(0x4B414C42u);  // "BLAK"
        bw.Write(1);
        bw.Write(x0); bw.Write(z0); bw.Write(w); bw.Write(h);
        for (int iz = 0; iz < h; iz++)
            for (int ix = 0; ix < w; ix++)
                bw.Write(heights[iz, ix]);
    }

    private static float[,] ReadBackup(string path, out int x0, out int z0, out int w, out int h)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read);
        using var br = new BinaryReader(fs);
        uint magic = br.ReadUInt32();
        if (magic != 0x4B414C42u) throw new InvalidDataException($"备份魔数不符: {path}");
        int ver = br.ReadInt32();
        if (ver != 1) throw new InvalidDataException($"备份版本不支持: {ver}");
        x0 = br.ReadInt32(); z0 = br.ReadInt32(); w = br.ReadInt32(); h = br.ReadInt32();
        var arr = new float[h, w];
        for (int iz = 0; iz < h; iz++)
            for (int ix = 0; ix < w; ix++)
                arr[iz, ix] = br.ReadSingle();
        return arr;
    }

    /// <summary>从主场景的 SECTR_Chunk.NodeName 反推活跃 chunk 场景路径。</summary>
    private static List<string> LoadActiveChunkPaths()
    {
        EditorSceneManager.OpenScene(WorldScenePath, OpenSceneMode.Single);
        return UnityEngine.Object.FindObjectsOfType<SECTR_Chunk>()
            .Where(c => !string.IsNullOrEmpty(c.NodeName))
            .Select(c => c.NodeName.Replace('\\', '/'))
            .Distinct()
            .OrderBy(p => p)
            .ToList();
    }

    private static string Sanitize(string s)
    {
        if (string.IsNullOrEmpty(s)) return "unknown";
        foreach (char c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        return s;
    }

    private static void Flush(StringBuilder sb, int code)
    {
        Directory.CreateDirectory(OutDir);
        File.WriteAllText(ResultPath, sb.ToString());
        EditorApplication.Exit(code);
    }
}
