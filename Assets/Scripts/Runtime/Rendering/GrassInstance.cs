using System;
using System.IO;
using System.Runtime.InteropServices;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using UnityEngine;

namespace FishDemo.Rendering
{
    /// <summary>
    /// 单个草实例的磁盘/内存表示（32 字节）。
    ///
    /// 为什么是 32B 而不是 GPU 侧的 48B 矩阵：
    /// 磁盘与 CPU 侧只需要「位置 + 朝向 + 缩放 + 种类 + 法线」这些**语义数据**，
    /// 变换矩阵是运行时按需展开的派生量。存 48B 矩阵会让烘焙文件大 50%，
    /// 且无法在烘焙后调整缩放/LOD 策略。所以磁盘用压缩表示，
    /// GPU 上传时再展开成 48B 的 float3x4（见 GrassBRGManager.PackMatrix3x4）。
    ///
    /// 布局（顺序即内存顺序，不可重排——直接按字节读写文件）：
    ///   0..11   position  float3  世界坐标（已含地形高度）
    ///   12..15  packed    uint    [0-7] type [8-15] scaleQ [16-31] yawQ
    ///   16..27  normal    float3  地表法线（用于贴合坡面）
    ///   28..31  color     uint    RGBA8 tint
    /// 合计 32B，且 32 是 16 的倍数，便于 GPU 上传对齐。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct GrassInstance
    {
        public float3 Position;
        public uint Packed;
        public float3 Normal;
        public uint Color;

        /// <summary>实例类型（0..7），对应 detail 层索引。</summary>
        public int Type
        {
            get => (int)(Packed & 0xFF);
            set => Packed = (Packed & 0xFFFFFF00u) | ((uint)(value & 0xFF));
        }

        /// <summary>缩放量化值（0..255），映射到 [0.5, 1.5] 倍。</summary>
        public byte ScaleQ
        {
            get => (byte)((Packed >> 8) & 0xFF);
            set => Packed = (Packed & 0xFFFF00FFu) | ((uint)value << 8);
        }

        /// <summary>Yaw 量化值（0..65535），映射到 [0, 2π)。</summary>
        public ushort YawQ
        {
            get => (ushort)((Packed >> 16) & 0xFFFF);
            set => Packed = (Packed & 0x0000FFFFu) | ((uint)value << 16);
        }

        /// <summary>量化缩放 → 实际倍数。0.5~1.5，跨度足够表达自然感又不易察觉。</summary>
        public float Scale => 0.5f + ScaleQ * (1.0f / 255f);

        /// <summary>量化 Yaw → 弧度。</summary>
        public float YawRadians => YawQ * (math.PI * 2f / 65536f);

        // ───────────────────────── 文件格式 ─────────────────────────

        /// <summary>文件魔数 "GRSS"（little-endian）。</summary>
        public const uint Magic = 0x53535247;

        /// <summary>格式版本。改布局必须递增，避免旧文件被误读。</summary>
        public const uint Version = 1;

        /// <summary>头部字节数：magic(4) + version(4) + count(4) + boundsMin(12) + boundsMax(12)。</summary>
        public const int HeaderSize = 36;

        /// <summary>每个实例的字节数。</summary>
        public const int Stride = 32;

        /// <summary>整个文件的预期字节数。</summary>
        public static int FileSizeFor(int count) => HeaderSize + count * Stride;
    }

    /// <summary>
    /// 一个 chunk 的烘焙产物（解压后的运行时表示）。
    /// 不直接持有 TerrainData 引用——运行时 chunk 可能已卸载。
    /// </summary>
    public sealed class GrassChunkData
    {
        public GrassInstance[] Instances;
        public Bounds Bounds;
        public string ChunkName;

        /// <summary>
        /// 从二进制流读取。格式不符时抛异常而不是返回空——
        /// 静默返回空会表现为「草突然不见了」，极难排查。
        /// </summary>
        public static GrassChunkData Read(byte[] bytes, string chunkName)
        {
            if (bytes == null || bytes.Length < GrassInstance.HeaderSize)
                throw new InvalidDataException($"[GrassChunkData] 文件过短: {chunkName}");

            int offset = 0;
            uint magic = ReadU32(bytes, ref offset);
            if (magic != GrassInstance.Magic)
                throw new InvalidDataException(
                    $"[GrassChunkData] 魔数不匹配（期望 0x{GrassInstance.Magic:X8}，实际 0x{magic:X8}）: {chunkName}");

            uint version = ReadU32(bytes, ref offset);
            if (version != GrassInstance.Version)
                throw new InvalidDataException(
                    $"[GrassChunkData] 版本不支持（期望 {GrassInstance.Version}，实际 {version}）: {chunkName}。请重新烘焙。");

            int count = (int)ReadU32(bytes, ref offset);
            if (count < 0 || GrassInstance.FileSizeFor(count) != bytes.Length)
                throw new InvalidDataException(
                    $"[GrassChunkData] 数量与文件大小不符（count={count}, 期望 {GrassInstance.FileSizeFor(count)}B，实际 {bytes.Length}B）: {chunkName}");

            Vector3 boundsMin = ReadVec3(bytes, ref offset);
            Vector3 boundsMax = ReadVec3(bytes, ref offset);

            var instances = new GrassInstance[count];
            // 直接按 32B 步长逐字段拷贝。不用 MemoryMarshal.Cast 是因为
            // GrassInstance 含 float3（Vectors 的布局），逐字段读最稳、不依赖封送细节。
            for (int i = 0; i < count; i++)
            {
                instances[i] = new GrassInstance
                {
                    Position = new float3(
                        ReadF32(bytes, ref offset),
                        ReadF32(bytes, ref offset),
                        ReadF32(bytes, ref offset)),
                    Packed = ReadU32(bytes, ref offset),
                    Normal = new float3(
                        ReadF32(bytes, ref offset),
                        ReadF32(bytes, ref offset),
                        ReadF32(bytes, ref offset)),
                    Color = ReadU32(bytes, ref offset),
                };
            }

            var b = new Bounds();
            b.SetMinMax(boundsMin, boundsMax);

            return new GrassChunkData
            {
                Instances = instances,
                Bounds = b,
                ChunkName = chunkName,
            };
        }

        private static uint ReadU32(byte[] b, ref int o)
        {
            uint v = (uint)(b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24));
            o += 4;
            return v;
        }

        private static float ReadF32(byte[] b, ref int o)
        {
            float v = BitConverter.ToSingle(b, o);
            o += 4;
            return v;
        }

        private static Vector3 ReadVec3(byte[] b, ref int o)
        {
            float x = ReadF32(b, ref o);
            float y = ReadF32(b, ref o);
            float z = ReadF32(b, ref o);
            return new Vector3(x, y, z);
        }
    }

    /// <summary>
    /// 一个剔除块：排序后同类型 + 同 32m 空间格的一段实例。
    ///
    /// BatchIndex / BatchStart / ChunkBase 是**流式加载需要**的运行时字段：
    /// 每个 chunk 有自己的实例数组，剔除输出必须换算成「该 batch 的 buffer 内相对索引」。
    /// 非流式（整表一次加载）时 ChunkBase=0，语义与旧版一致。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct GrassBlock
    {
        /// <summary>实例区间起点。流式模式下是「全局位置数组」的下标。</summary>
        public int Start;
        public int Count;

        /// <summary>剔除用 AABB（世界坐标）。</summary>
        public float3 Center;
        public float3 HalfExtent;

        /// <summary>官方 detail 原型类型（0..6）。</summary>
        public int TypeIndex;

        /// <summary>该块所属 batch 在 <c>_batches</c> 里的下标。</summary>
        public int BatchIndex;

        /// <summary>该 batch 在所属 chunk 实例数组里的起点。</summary>
        public int BatchStart;

        /// <summary>该块所属 chunk 在全局位置数组里的起点。</summary>
        public int ChunkBase;
    }

    /// <summary>
    /// 运行时草数据的**零托管分配**读取与排序。
    ///
    /// 为什么必须走原生内存：
    /// 435 万实例 × 32B = 133MB。放在托管数组里会同时产生三份
    /// （逐 chunk 读入 / 合并 / 排序副本），托管堆常驻 260MB+、峰值 500MB+。
    /// 托管 GC 的标记阶段要遍历整个存活堆，堆越大单次停顿越贵——
    /// 这不是"调 GC 参数"能解决的，只能让这批数据根本不进托管堆。
    ///
    /// 磁盘布局与 <see cref="GrassInstance"/> 的 Sequential 布局逐字节一致，
    /// 所以文件体可以整段 MemCpy，不需要逐字段解析。
    /// （工程目标平台 x86_64 / ARM64 都是小端，与烘焙端一致。）
    /// </summary>
    public static class GrassChunkFile
    {
        // 运行时只需要 32B/实例这一种格式；旧版 GrassChunkData 的托管读取保留给编辑器工具。
        public const int TypeCount = 7;

        /// <summary>读盘中转缓冲（64KB）。静态复用，避免每次 chunk 加载产生托管 byte[3.7MB]。</summary>
        private static byte[] s_readBuffer;

        /// <summary>
        /// 从烘焙文件读入原生实例数组（Allocator.Persistent，调用方负责 Dispose）。
        /// 失败抛异常：静默返回空会表现为「整片草消失」，极难排查。
        /// </summary>
        public static void Read(string path, out NativeArray<GrassInstance> instances, out Bounds bounds)
        {
            instances = default;
            bounds = default;

            // 自检一次：磁盘格式假设 32B 顺序布局。若有人给 GrassInstance 加了字段，
            // 这里必须当场失败，不能等到 GPU 上读出乱码。
            if (UnsafeUtility.SizeOf<GrassInstance>() != GrassInstance.Stride)
                throw new InvalidOperationException(
                    $"[GrassChunkFile] GrassInstance 布局 {UnsafeUtility.SizeOf<GrassInstance>()}B ≠ 磁盘 {GrassInstance.Stride}B");

            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16))
            {
                long len64 = fs.Length;
                if (len64 < GrassInstance.HeaderSize || len64 > int.MaxValue)
                    throw new InvalidDataException($"[GrassChunkFile] 文件大小异常: {path} ({len64}B)");

                int len = (int)len64;
                // Temp 分配不是托管堆：不产生 GC 垃圾，读完立即 Dispose。
                var raw = new NativeArray<byte>(len, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
                // 64KB 中转缓冲，静态复用；每次只 MemCpy 一段，避免整文件托管 byte[]。
                if (s_readBuffer == null)
                    s_readBuffer = new byte[1 << 16];
                try
                {
                    unsafe
                    {
                        byte* dst = (byte*)raw.GetUnsafePtr();
                        int read = 0;
                        while (read < len)
                        {
                            int want = Math.Min(s_readBuffer.Length, len - read);
                            int n = fs.Read(s_readBuffer, 0, want);
                            if (n <= 0)
                                throw new EndOfStreamException($"[GrassChunkFile] 读取中断 {read}/{len}: {path}");
                            fixed (byte* src = s_readBuffer)
                                UnsafeUtility.MemCpy(dst + read, src, n);
                            read += n;
                        }

                        byte* p = dst;
                        uint magic = *(uint*)p;
                        if (magic != GrassInstance.Magic)
                            throw new InvalidDataException($"[GrassChunkFile] 魔数不匹配: {path}");

                        uint version = *(uint*)(p + 4);
                        if (version != GrassInstance.Version)
                            throw new InvalidDataException($"[GrassChunkFile] 版本不支持 {version}: {path}");

                        int count = (int)*(uint*)(p + 8);
                        if (count < 0 || GrassInstance.FileSizeFor(count) != len)
                            throw new InvalidDataException(
                                $"[GrassChunkFile] 数量与文件大小不符（count={count}, {len}B）: {path}");

                        Vector3 min = new Vector3(*(float*)(p + 12), *(float*)(p + 16), *(float*)(p + 20));
                        Vector3 max = new Vector3(*(float*)(p + 24), *(float*)(p + 28), *(float*)(p + 32));
                        bounds = new Bounds();
                        bounds.SetMinMax(min, max);

                        instances = new NativeArray<GrassInstance>(count, Allocator.Persistent,
                            NativeArrayOptions.UninitializedMemory);
                        if (count > 0)
                        {
                            UnsafeUtility.MemCpy(instances.GetUnsafePtr(), p + GrassInstance.HeaderSize,
                                (long)count * GrassInstance.Stride);
                        }
                    }
                }
                catch
                {
                    if (instances.IsCreated) instances.Dispose();
                    throw;
                }
                finally
                {
                    raw.Dispose();
                }
            }
        }

        /// <summary>
        /// 就地把实例按 (类型, 32m 空间格) 重排，并产出块表。
        ///
        /// 与旧的托管版 <c>SortInstancesIntoBlocks</c> 语义完全一致：
        /// 类型作为高位键，保证同类型实例连续 → 一个 batch 内可共用一条 draw command。
        /// 原地排序需要一份临时副本；TempJob 是原生内存，不进托管堆。
        /// </summary>
        public static NativeArray<GrassBlock> SortIntoBlocks(
            NativeArray<GrassInstance> instances, float blockSize, float cullRadius, float grassHeight)
        {
            int n = instances.Length;
            if (n == 0)
                return default;

            float minX = float.MaxValue, minZ = float.MaxValue;
            float maxX = float.MinValue, maxZ = float.MinValue;
            for (int i = 0; i < n; i++)
            {
                var p = instances[i].Position;
                if (p.x < minX) minX = p.x;
                if (p.x > maxX) maxX = p.x;
                if (p.z < minZ) minZ = p.z;
                if (p.z > maxZ) maxZ = p.z;
            }

            int gridW = Mathf.Max(1, Mathf.CeilToInt((maxX - minX) / blockSize));
            int gridH = Mathf.Max(1, Mathf.CeilToInt((maxZ - minZ) / blockSize));
            int blockCount = gridW * gridH;
            int keySpace = TypeCount * blockCount;

            var counts = new NativeArray<int>(keySpace + 1, Allocator.Temp, NativeArrayOptions.ClearMemory);
            var keyOf = new NativeArray<int>(n, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            try
            {
                for (int i = 0; i < n; i++)
                {
                    var p = instances[i].Position;
                    int bx = Mathf.Clamp((int)((p.x - minX) / blockSize), 0, gridW - 1);
                    int bz = Mathf.Clamp((int)((p.z - minZ) / blockSize), 0, gridH - 1);
                    int key = Mathf.Clamp(instances[i].Type, 0, TypeCount - 1) * blockCount + bz * gridW + bx;
                    keyOf[i] = key;
                    counts[key + 1]++;
                }

                for (int k = 0; k < keySpace; k++)
                    counts[k + 1] += counts[k];

                var write = new NativeArray<int>(keySpace + 1, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
                var scratch = new NativeArray<GrassInstance>(n, Allocator.TempJob,
                    NativeArrayOptions.UninitializedMemory);
                try
                {
                    for (int k = 0; k <= keySpace; k++)
                        write[k] = counts[k];

                    for (int i = 0; i < n; i++)
                        scratch[write[keyOf[i]]++] = instances[i];

                    // 拷回调用方持有的 Persistent 数组，scratch 由本方法释放。
                    unsafe
                    {
                        UnsafeUtility.MemCpy(instances.GetUnsafePtr(), scratch.GetUnsafePtr(),
                            (long)n * GrassInstance.Stride);
                    }
                }
                finally
                {
                    write.Dispose();
                    scratch.Dispose();
                }

                // ── 生成块表（非空键）──
                int nonEmpty = 0;
                for (int k = 0; k < keySpace; k++)
                    if (counts[k + 1] > counts[k]) nonEmpty++;

                var blocks = new NativeArray<GrassBlock>(nonEmpty, Allocator.Persistent,
                    NativeArrayOptions.UninitializedMemory);
                int bi = 0;
                for (int k = 0; k < keySpace; k++)
                {
                    int start = counts[k];
                    int count = counts[k + 1] - start;
                    if (count == 0) continue;

                    float byMin = float.MaxValue, byMax = float.MinValue;
                    float bxMin = float.MaxValue, bxMax = float.MinValue;
                    float bzMin = float.MaxValue, bzMax = float.MinValue;
                    for (int i = start; i < start + count; i++)
                    {
                        var p = instances[i].Position;
                        if (p.y < byMin) byMin = p.y;
                        if (p.y > byMax) byMax = p.y;
                        if (p.x < bxMin) bxMin = p.x;
                        if (p.x > bxMax) bxMax = p.x;
                        if (p.z < bzMin) bzMin = p.z;
                        if (p.z > bzMax) bzMax = p.z;
                    }

                    blocks[bi++] = new GrassBlock
                    {
                        Start = start,
                        Count = count,
                        Center = new float3((bxMin + bxMax) * 0.5f, (byMin + byMax) * 0.5f + grassHeight * 0.5f,
                            (bzMin + bzMax) * 0.5f),
                        HalfExtent = new float3((bxMax - bxMin) * 0.5f + cullRadius,
                            (byMax - byMin) * 0.5f + grassHeight * 0.5f + cullRadius,
                            (bzMax - bzMin) * 0.5f + cullRadius),
                        TypeIndex = k / blockCount,
                    };
                }
                return blocks;
            }
            finally
            {
                counts.Dispose();
                keyOf.Dispose();
            }
        }
    }
}
