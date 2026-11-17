using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class LakeCarve
{
    private const string Tag = "[LakeCarve]";
    private const string WorldScenePath = "Assets/Scenes/World.unity";
    private const string OutDir = "Logs/Lake";
    private const string ResultPath = OutDir + "/lake-carve.txt";
    private const string BackupDir = "Tools/lake-backup";

    public static readonly Vector2 Center = new Vector2(192f, 96f);
    public const float Radius = 190f;
    public const float WaterLevel = 264f;
    public const float Depth = 30f;
    public const float Blend = 90f;

    public static void Apply() { Run("apply"); }
    public static void Restore() { Run("restore"); }

    public static void Report() { Run("report"); }

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

                    var tMin = new Vector2(p.x, p.z);
                    var tMax = new Vector2(p.x + size.x, p.z + size.z);
                    if (tMax.x < min.x || tMin.x > max.x || tMax.y < min.y || tMin.y > max.y) continue;

                    var hm = td.heightmapResolution;
                    float cellX = size.x / (hm - 1);
                    float cellZ = size.z / (hm - 1);

                    int x0 = Mathf.Max(0, Mathf.FloorToInt((min.x - p.x) / cellX) - 1);
                    int z0 = Mathf.Max(0, Mathf.FloorToInt((min.y - p.z) / cellZ) - 1);
                    int x1 = Mathf.Min(hm - 1, Mathf.CeilToInt((max.x - p.x) / cellX) + 1);
                    int z1 = Mathf.Min(hm - 1, Mathf.CeilToInt((max.y - p.z) / cellZ) + 1);
                    int w = x1 - x0 + 1, h = z1 - z0 + 1;
                    if (w <= 0 || h <= 0) continue;

                    var heights = td.GetHeights(x0, z0, w, h);
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

                    int changed = 0;
                    float minH = float.MaxValue, maxH = float.MinValue;
                    for (int iz = 0; iz < h; iz++)
                    {
                        float wz = p.z + (z0 + iz) * cellZ;
                        for (int ix = 0; ix < w; ix++)
                        {
                            float wx = p.x + (x0 + ix) * cellX;
                            float cur = heights[iz, ix] * size.y;
                            float target = TargetHeight(wx, wz, cur);
                            if (target >= cur) continue;

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

    private static float TargetHeight(float wx, float wz, float current)
    {
        float dx = wx - Center.x, dz = wz - Center.y;
        float r = Mathf.Sqrt(dx * dx + dz * dz);
        if (r > Radius) return current;
        float k = 1f - (r / Radius) * (r / Radius);
        float target = WaterLevel - Depth * k;
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

    private static bool VerifyLake(Action<string> W)
    {
        var chunkPaths = LoadActiveChunkPaths();
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

        Check("湖心", Center.x, Center.y, WaterLevel - Depth, 2.5f);
        Check("半径 1/2", Center.x + Radius * 0.5f, Center.y, WaterLevel - Depth * 0.75f, 4f);
        Check("半径 R", Center.x + Radius, Center.y, WaterLevel, 4f);

        float ext = WaterHalfExtent;
        int below = 0;
        float worst = float.MaxValue;
        Vector3 worstAt = Vector3.zero;
        for (float x = Center.x - ext; x <= Center.x + ext; x += 8f)
        {
            for (float z = Center.y - ext; z <= Center.y + ext; z += 8f)
            {
                float dx = x - Center.x, dz = z - Center.y;
                if (dx * dx + dz * dz <= Radius * Radius) continue;
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


    private static void WriteBackup(string path, int x0, int z0, int w, int h, float[,] heights)
    {
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var bw = new BinaryWriter(fs);
        bw.Write(0x4B414C42u);
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
