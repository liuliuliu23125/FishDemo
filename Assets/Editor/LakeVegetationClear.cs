using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using FishDemo.Rendering;

/// <summary>
/// 清掉湖区（湖盆 + 岸线过渡带）内的树与草，让水面干净。
///
/// ── 为什么树和草要分开处理 ──
/// 二者存在完全不同的地方，改法也不同（见 AGENTS.md §3）：
///   - 树：TerrainData.treeInstances（归一化 XZ + 世界 Y），由 Terrain 渲染。
///   - 草：已全部迁到 BRG，磁盘上是 Assets/Scenes/World/GrassData/*.bytes
///     （32B/实例，位置是世界坐标）。Terrain 的 detail 渲染已关闭（DetailObjectDistance=0），
///     所以**只改 .bytes 就够，不需要动 TerrainData.detailLayer**。
///
/// ── 为什么按「水位 + 岸线缓冲」而不是按湖盆半径删 ──
/// 要删的是"会被水淹掉"和"贴着水面会穿帮"的植被，判据应该是**高度**：
///   - 高度 &lt; 水位 + 1m  → 一定在水里或水线上，删；
///   - 湖盆半径内（r &lt;= R + 8m）→ 即使地形高于水位也在碗壁上，删；
/// 两条取并集。这样即使个别树在碗壁上悬空，也不会露出来。
///
/// ── 可逆性 ──
/// 树：备份受影响 chunk 的 treeInstances 到 Tools/lake-veg-backup/&lt;chunk&gt;.trees
/// 草：备份受影响 chunk 的 .bytes 原文件到 Tools/lake-veg-backup/&lt;chunk&gt;.bytes
/// Restore 完整还原。重跑世界生成/草烘焙会覆盖，需重做。
///
/// 用法：
///   -executeMethod LakeVegetationClear.Apply
///   -executeMethod LakeVegetationClear.Restore
///   -executeMethod LakeVegetationClear.Report
/// 输出 Logs/Lake/lake-vegetation.txt
/// </summary>
public static class LakeVegetationClear
{
    private const string Tag = "[LakeVeg]";
    private const string WorldScenePath = "Assets/Scenes/World.unity";
    private const string GrassDir = "Assets/Scenes/World/GrassData";
    private const string OutDir = "Logs/Lake";
    private const string ResultPath = OutDir + "/lake-vegetation.txt";
    private const string BackupDir = "Tools/lake-veg-backup";

    /// <summary>岸线额外缓冲（米）：r &lt;= R + 此值时也删，避免碗壁上的植被悬在水面。</summary>
    private const float ShoreBuffer = 8f;


    public static void Apply() { Run("apply"); }
    public static void Restore() { Run("restore"); }
    public static void Report() { Run("report"); }

    private static float CullRadius => LakeCarve.Radius + ShoreBuffer;

    private static void Run(string mode)
    {
        var sb = new StringBuilder();
        void W(string s) { Debug.Log($"{Tag} {s}"); sb.AppendLine(s); }

        try
        {
            Directory.CreateDirectory(OutDir);
            if (mode == "apply") Directory.CreateDirectory(BackupDir);

            W($"模式 = {mode}");
            W($"湖心=({LakeCarve.Center.x},{LakeCarve.Center.y}) 删除半径={CullRadius}m（R+{ShoreBuffer}）");

            long treesRemoved = 0, grassRemoved = 0;
            int chunksTouched = 0;

            // ── 树 ──
            W("");
            W("=== 树（TerrainData.treeInstances）===");
            foreach (var chunkPath in LoadActiveChunkPaths())
            {
                var scene = EditorSceneManager.OpenScene(chunkPath, OpenSceneMode.Single);
                bool dirty = false;
                foreach (var t in UnityEngine.Object.FindObjectsOfType<Terrain>())
                {
                    var td = t.terrainData;
                    if (td == null || td.treeInstanceCount == 0) continue;
                    if (!ChunkTouchesLake(t)) continue;

                    string safe = Sanitize(td.name);
                    string bak = Path.Combine(BackupDir, safe + ".trees");
                    var p0 = t.transform.position;
                    var size = td.size;

                    if (mode == "restore")
                    {
                        if (!File.Exists(bak)) { W($"  {td.name}: 无备份，跳过"); continue; }
                        var restored = ReadTreeBackup(bak);
                        td.treeInstances = restored;
                        EditorUtility.SetDirty(td);
                        dirty = true;
                        W($"  {td.name}: 已还原 {restored.Length} 棵树");
                        continue;
                    }

                    var insts = td.treeInstances;
                    var keep = new List<TreeInstance>(insts.Length);

                    if (mode == "apply" && !File.Exists(bak))
                        WriteTreeBackup(bak, insts);

                    foreach (var ti in insts)
                    {
                        float wx = p0.x + ti.position.x * size.x;
                        float wz = p0.z + ti.position.z * size.z;
                        float wy = p0.y + ti.position.y * size.y;
                        if (ShouldCull(wx, wz, wy))
                            continue;
                        keep.Add(ti);
                    }

                    int removed = insts.Length - keep.Count;
                    if (mode == "report")
                    {
                        W($"  {td.name}: {insts.Length} 棵，湖区判据命中 {removed} 棵");
                        continue;
                    }

                    if (removed > 0)
                    {
                        td.treeInstances = keep.ToArray();
                        EditorUtility.SetDirty(td);
                        dirty = true;
                        treesRemoved += removed;
                        W($"  {td.name}: 删除 {removed} / {insts.Length} 棵");
                    }
                    else
                    {
                        W($"  {td.name}: 无需删除（{insts.Length} 棵均不在湖区）");
                    }
                }

                if (dirty)
                {
                    EditorSceneManager.MarkSceneDirty(scene);
                    EditorSceneManager.SaveScene(scene);
                    chunksTouched++;
                }
            }
            AssetDatabase.SaveAssets();

            // ── 草（.bytes）──
            W("");
            W("=== 草（GrassData/*.bytes，世界坐标）===");
            var files = Directory.GetFiles(GrassDir, "*.bytes").OrderBy(f => f).ToList();
            W($"草数据文件 = {files.Count}");
            foreach (var f in files)
            {
                string name = Path.GetFileNameWithoutExtension(f);
                string safe = Sanitize(name);
                string bak = Path.Combine(BackupDir, safe + ".bytes");

                if (mode == "restore")
                {
                    if (!File.Exists(bak)) continue;
                    File.Copy(bak, f, overwrite: true);
                    W($"  {name}: 已还原");
                    continue;
                }

                var data = GrassChunkData.Read(File.ReadAllBytes(f), name);
                if (data.Instances == null || data.Instances.Length == 0) continue;

                var keep = new List<GrassInstance>(data.Instances.Length);
                int removed = 0;
                foreach (var gi in data.Instances)
                {
                    if (ShouldCull(gi.Position.x, gi.Position.z, gi.Position.y)) removed++;
                    else keep.Add(gi);
                }

                if (mode == "report")
                {
                    if (removed > 0) W($"  {name}: {data.Instances.Length:N0} 株，命中 {removed:N0} 株");
                    continue;
                }

                if (removed == 0) continue;
                if (!File.Exists(bak)) File.Copy(f, bak);
                WriteGrassFile(f, keep, data.Bounds);
                grassRemoved += removed;
                W($"  {name}: 删除 {removed:N0} / {data.Instances.Length:N0} 株");
            }

            AssetDatabase.Refresh();

            W("");
            W($"删除树 = {treesRemoved}，删除草 = {grassRemoved}，改写 chunk 场景 = {chunksTouched}");
            W($"备份目录 = {BackupDir}");

            // ── 校验：重新读一遍，确认湖区内已无植被 ──
            if (mode != "report")
            {
                W("");
                W("=== 校验：湖区内残留植被 ===");
                bool ok = Verify(W);
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
    /// 是否应被删除。
    ///
    /// 判据只用「到湖心的水平距离」，**不加全局高度判据**。
    /// 原因（实测踩过）：本地形整体高度 236~350m，世界边缘大片区域低于任何合理水位；
    /// 若加 `h &lt; 水位` 这一条，会把世界另一头（离湖 1km 外）的草也删掉
    /// ——实测第一次统计就命中 25 万株，几乎全是无关区域。
    /// 挖方只在 r&lt;=R 内，所以半径判据已经完整覆盖"会被淹"的区域；
    /// 再加 ShoreBuffer 覆盖碗壁上可能悬空的植被即可。
    /// </summary>
    private static bool ShouldCull(float wx, float wz, float wy)
    {
        float dx = wx - LakeCarve.Center.x, dz = wz - LakeCarve.Center.y;
        return dx * dx + dz * dz <= CullRadius * CullRadius;
    }

    private static bool ChunkTouchesLake(Terrain t)
    {
        var p = t.transform.position;
        var s = t.terrainData.size;
        float ext = CullRadius;
        return !(p.x + s.x < LakeCarve.Center.x - ext || p.x > LakeCarve.Center.x + ext ||
                 p.z + s.z < LakeCarve.Center.y - ext || p.z > LakeCarve.Center.y + ext);
    }

    private static bool Verify(Action<string> W)
    {
        bool ok = true;

        // 草
        int grassLeft = 0;
        foreach (var f in Directory.GetFiles(GrassDir, "*.bytes"))
        {
            var d = GrassChunkData.Read(File.ReadAllBytes(f), Path.GetFileNameWithoutExtension(f));
            if (d.Instances == null) continue;
            foreach (var gi in d.Instances)
                if (ShouldCull(gi.Position.x, gi.Position.z, gi.Position.y)) grassLeft++;
        }
        W($"  湖区残留草 = {grassLeft}");
        if (grassLeft > 0) ok = false;

        // 树
        int treesLeft = 0;
        var chunkPaths = LoadActiveChunkPaths();
        foreach (var cp in chunkPaths) if (File.Exists(cp)) EditorSceneManager.OpenScene(cp, OpenSceneMode.Additive);
        foreach (var t in UnityEngine.Object.FindObjectsOfType<Terrain>())
        {
            var td = t.terrainData;
            if (td == null) continue;
            var p0 = t.transform.position; var size = td.size;
            foreach (var ti in td.treeInstances)
            {
                float wx = p0.x + ti.position.x * size.x;
                float wz = p0.z + ti.position.z * size.z;
                float wy = p0.y + ti.position.y * size.y;
                if (ShouldCull(wx, wz, wy)) treesLeft++;
            }
        }
        W($"  湖区残留树 = {treesLeft}");
        if (treesLeft > 0) ok = false;

        return ok;
    }

    // ───────────────────────── 文件读写 ─────────────────────────

    /// <summary>树的备份：magic(4) count(4) + count × TreeInstance(归一化 position float3 + widthScale + heightScale + color rgba + prototypeIndex)。</summary>
    private static void WriteTreeBackup(string path, TreeInstance[] insts)
    {
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var bw = new BinaryWriter(fs);
        bw.Write(0x4552544Cu);   // "LTRE"
        bw.Write(insts.Length);
        foreach (var t in insts)
        {
            bw.Write(t.position.x); bw.Write(t.position.y); bw.Write(t.position.z);
            bw.Write(t.widthScale); bw.Write(t.heightScale);
            bw.Write(t.color.r); bw.Write(t.color.g); bw.Write(t.color.b); bw.Write(t.color.a);
            bw.Write(t.lightmapColor.r); bw.Write(t.lightmapColor.g); bw.Write(t.lightmapColor.b); bw.Write(t.lightmapColor.a);
            bw.Write(t.prototypeIndex);
        }
    }

    private static TreeInstance[] ReadTreeBackup(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read);
        using var br = new BinaryReader(fs);
        if (br.ReadUInt32() != 0x4552544Cu) throw new InvalidDataException($"树备份魔数不符: {path}");
        int n = br.ReadInt32();
        var arr = new TreeInstance[n];
        for (int i = 0; i < n; i++)
        {
            arr[i] = new TreeInstance
            {
                position = new Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle()),
                widthScale = br.ReadSingle(),
                heightScale = br.ReadSingle(),
                color = new Color(br.ReadSingle(), br.ReadSingle(), br.ReadSingle(), br.ReadSingle()),
                lightmapColor = new Color(br.ReadSingle(), br.ReadSingle(), br.ReadSingle(), br.ReadSingle()),
                prototypeIndex = br.ReadInt32(),
            };
        }
        return arr;
    }

    /// <summary>按 GrassInstance 的磁盘格式重写草文件（头部 + 32B/实例）。</summary>
    private static void WriteGrassFile(string path, List<GrassInstance> insts, Bounds bounds)
    {
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var bw = new BinaryWriter(fs);
        bw.Write(GrassInstance.Magic);
        bw.Write(GrassInstance.Version);
        bw.Write(insts.Count);
        bw.Write(bounds.min.x); bw.Write(bounds.min.y); bw.Write(bounds.min.z);
        bw.Write(bounds.max.x); bw.Write(bounds.max.y); bw.Write(bounds.max.z);
        foreach (var g in insts)
        {
            bw.Write(g.Position.x); bw.Write(g.Position.y); bw.Write(g.Position.z);
            bw.Write(g.Packed);
            bw.Write(g.Normal.x); bw.Write(g.Normal.y); bw.Write(g.Normal.z);
            bw.Write(g.Color);
        }
    }

    private static List<string> LoadActiveChunkPaths()
    {
        EditorSceneManager.OpenScene(WorldScenePath, OpenSceneMode.Single);
        return UnityEngine.Object.FindObjectsOfType<SECTR_Chunk>()
            .Where(c => !string.IsNullOrEmpty(c.NodeName))
            .Select(c => c.NodeName.Replace('\\', '/'))
            .Distinct().OrderBy(p => p).ToList();
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
