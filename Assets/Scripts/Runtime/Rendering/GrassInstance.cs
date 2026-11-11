using System;
using System.IO;
using System.Runtime.InteropServices;
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
}
