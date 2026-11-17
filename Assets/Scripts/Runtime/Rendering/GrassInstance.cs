using System;
using System.IO;
using System.Runtime.InteropServices;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using UnityEngine;

namespace FishDemo.Rendering
{
    [StructLayout(LayoutKind.Sequential)]
    public struct GrassInstance
    {
        public float3 Position;
        public uint Packed;
        public float3 Normal;
        public uint Color;

        public int Type
        {
            get => (int)(Packed & 0xFF);
            set => Packed = (Packed & 0xFFFFFF00u) | ((uint)(value & 0xFF));
        }

        public byte ScaleQ
        {
            get => (byte)((Packed >> 8) & 0xFF);
            set => Packed = (Packed & 0xFFFF00FFu) | ((uint)value << 8);
        }

        public ushort YawQ
        {
            get => (ushort)((Packed >> 16) & 0xFFFF);
            set => Packed = (Packed & 0x0000FFFFu) | ((uint)value << 16);
        }

        public float Scale => 0.5f + ScaleQ * (1.0f / 255f);

        public float YawRadians => YawQ * (math.PI * 2f / 65536f);


        public const uint Magic = 0x53535247;

        public const uint Version = 1;

        public const int HeaderSize = 36;

        public const int Stride = 32;

        public static int FileSizeFor(int count) => HeaderSize + count * Stride;
    }

    public sealed class GrassChunkData
    {
        public GrassInstance[] Instances;
        public Bounds Bounds;
        public string ChunkName;

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

    [StructLayout(LayoutKind.Sequential)]
    public struct GrassBlock
    {
        public int Start;
        public int Count;

        public float3 Center;
        public float3 HalfExtent;

        public int TypeIndex;

        public int BatchIndex;

        public int BatchStart;

        public int ChunkBase;
    }

    public static class GrassChunkFile
    {
        public const int TypeCount = 7;

        private static byte[] s_readBuffer;

        public static void Read(string path, out NativeArray<GrassInstance> instances, out Bounds bounds)
        {
            instances = default;
            bounds = default;

            if (UnsafeUtility.SizeOf<GrassInstance>() != GrassInstance.Stride)
                throw new InvalidOperationException(
                    $"[GrassChunkFile] GrassInstance 布局 {UnsafeUtility.SizeOf<GrassInstance>()}B ≠ 磁盘 {GrassInstance.Stride}B");

            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16))
            {
                long len64 = fs.Length;
                if (len64 < GrassInstance.HeaderSize || len64 > int.MaxValue)
                    throw new InvalidDataException($"[GrassChunkFile] 文件大小异常: {path} ({len64}B)");

                int len = (int)len64;
                var raw = new NativeArray<byte>(len, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
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
