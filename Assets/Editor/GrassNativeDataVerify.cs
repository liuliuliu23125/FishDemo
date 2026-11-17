using System;
using System.IO;
using System.Text;
using FishDemo.Rendering;
using Unity.Collections;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;

/// <summary>
/// 验证草的原生数据通路与旧的托管读取逐实例一致，且分块排序不丢不乱。
///
/// 能验（无需图形设备）：
///   1. `GrassChunkFile.Read` 的 count / bounds 与 `GrassChunkData.Read` 相同；
///   2. 32B 顺序布局 MemCpy 后，每个实例的位置/类型/缩放/朝向/法线一致；
///   3. `SortIntoBlocks` 前后实例多重集不变（位置和 × 数量 的校验和一致）；
///   4. 每个块内实例类型单一，块区间覆盖全部实例。
///
/// 不能验：BRG 的 AddBatch / 剔除（需要真实图形设备，见 GrassCloseupVerify）。
///
/// 用法：Unity.exe -batchmode -nographics -projectPath E:\FishDemo \
///   -executeMethod GrassNativeDataVerify.Run -logFile Logs/Grass/native-data.log
/// </summary>
public static class GrassNativeDataVerify
{
    private const string Tag = "[GrassNative]";
    private const string ResultPath = "Logs/Grass/native-data.txt";
    private const string GrassDataDir = "Assets/Scenes/World/GrassData";

    public static void Run()
    {
        var sb = new StringBuilder();
        void W(string s) { Debug.Log($"{Tag} {s}"); sb.AppendLine(s); }

        bool all = true;
        try
        {
            string[] files = Directory.GetFiles(GrassDataDir, "*.bytes");
            if (files.Length == 0)
                throw new InvalidOperationException("没有草烘焙数据");

            // 抽第一块与最后一块：覆盖湖区清草前后两种分布。
            int[] picks = { 0, files.Length - 1 };
            foreach (int idx in picks)
            {
                string f = files[idx];
                string name = Path.GetFileNameWithoutExtension(f);
                all &= VerifyOne(f, name, W);
            }
        }
        catch (Exception e)
        {
            W($"异常：{e}");
            all = false;
        }

        W("");
        W(all ? "==== 结论：PASS ====" : "==== 结论：FAIL ====");

        Directory.CreateDirectory(Path.GetDirectoryName(ResultPath));
        File.WriteAllText(ResultPath, sb.ToString());
        if (Application.isBatchMode)
            EditorApplication.Exit(all ? 0 : 2);
    }

    private static bool VerifyOne(string path, string name, Action<string> W)
    {
        bool ok = true;

        GrassChunkData managed = GrassChunkData.Read(File.ReadAllBytes(path), name);
        GrassChunkFile.Read(path, out NativeArray<GrassInstance> native, out Bounds nativeBounds);

        try
        {
            ok &= Assert(managed.Instances.Length == native.Length,
                $"{name}: 数量 托管 {managed.Instances.Length:N0} / 原生 {native.Length:N0}", W);

            bool sameBounds = (managed.Bounds.min - nativeBounds.min).sqrMagnitude < 1e-6f &&
                              (managed.Bounds.max - nativeBounds.max).sqrMagnitude < 1e-6f;
            ok &= Assert(sameBounds, $"{name}: bounds 一致", W);

            // 逐实例比对所有字段（32B MemCpy 的正确性）。
            int mismatch = 0;
            for (int i = 0; i < native.Length; i++)
            {
                var a = managed.Instances[i];
                var b = native[i];
                if (!a.Position.Equals(b.Position) || a.Packed != b.Packed ||
                    !a.Normal.Equals(b.Normal) || a.Color != b.Color)
                {
                    if (mismatch < 3)
                        W($"  首个不一致 @{i}: 托管 ({a.Position},{a.Packed:X8}) 原生 ({b.Position},{b.Packed:X8})");
                    mismatch++;
                }
            }
            ok &= Assert(mismatch == 0, $"{name}: 实例字段一致（不一致 {mismatch}）", W);

            // 排序前保存校验和（逐个 float 求和，位置排序不变性）。
            double sx = 0, sy = 0, sz = 0;
            long typeHist = 0;
            for (int i = 0; i < native.Length; i++)
            {
                var p = native[i].Position;
                sx += p.x; sy += p.y; sz += p.z;
                typeHist += native[i].Type + 1;
            }

            var blocks = GrassChunkFile.SortIntoBlocks(native, 32f, 1f, 0.3f);
            try
            {
                double ax = 0, ay = 0, az = 0;
                long typeHist2 = 0;
                for (int i = 0; i < native.Length; i++)
                {
                    var p = native[i].Position;
                    ax += p.x; ay += p.y; az += p.z;
                    typeHist2 += native[i].Type + 1;
                }

                double eps = Math.Max(1.0, native.Length * 1e-3);
                bool sumOk = Math.Abs(sx - ax) < eps && Math.Abs(sy - ay) < eps && Math.Abs(sz - az) < eps;
                ok &= Assert(sumOk && typeHist == typeHist2,
                    $"{name}: 排序前后实例多重集一致（位置和/类型直方图）", W);

                long covered = 0;
                bool typePure = true;
                int cursor = 0;
                for (int b = 0; b < blocks.Length; b++)
                {
                    var blk = blocks[b];
                    if (blk.Start != cursor) typePure = false;
                    cursor = blk.Start + blk.Count;
                    covered += blk.Count;
                    int t0 = native[blk.Start].Type;
                    for (int i = blk.Start + 1; i < blk.Start + blk.Count; i++)
                        if (native[i].Type != t0) { typePure = false; break; }
                    if (blk.TypeIndex != t0) typePure = false;
                }

                ok &= Assert(covered == native.Length && typePure,
                    $"{name}: 块表覆盖 {covered:N0}/{native.Length:N0}，块内类型单一/连续", W);

                // 类型段必须连续：ComputeTypeRanges 依赖这个前提。
                bool typeRuns = true;
                for (int i = 1; i < native.Length; i++)
                {
                    int a = native[i - 1].Type, b = native[i].Type;
                    if (b < a) { typeRuns = false; break; }
                }
                ok &= Assert(typeRuns, $"{name}: 类型单调（排序键高位生效）", W);
            }
            finally
            {
                if (blocks.IsCreated) blocks.Dispose();
            }
        }
        finally
        {
            if (native.IsCreated) native.Dispose();
        }

        return ok;
    }

    private static bool Assert(bool cond, string msg, Action<string> W)
    {
        W($"{msg} : {(cond ? "PASS" : "FAIL")}");
        return cond;
    }
}
