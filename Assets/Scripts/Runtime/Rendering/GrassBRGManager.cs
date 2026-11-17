using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

namespace FishDemo.Rendering
{
    [ExecuteAlways]
    [DisallowMultipleComponent]
    public sealed class GrassBRGManager : MonoBehaviour
    {
        [Header("M1 测试数据（硬编码，不读地形）")]
        [Tooltip("实例总数。M1 固定 1000，M2 起由 chunk 数据决定。")]
        [SerializeField] private int _instanceCount = 1000;

        [Tooltip("网格间距（米）。仅 M1 硬编码布点用。")]
        [SerializeField] private float _spacing = 1.5f;

        [Tooltip("剔除用包围球半径（米）。草片约 1m 高，取 1.0 是保守值。")]
        [SerializeField] private float _cullRadius = 1.0f;

        [Tooltip("草材质。为空时用 Shader.Find 兜底（仅编辑器/开发期可靠）。")]
        [SerializeField] private Material _material;

        [Tooltip("草地锚点。为空时用本物体 transform 的位置作为方阵中心。")]
        [SerializeField] private Transform _anchor;

        [Tooltip("把每个实例吸附到地表高度（用已加载 Terrain 的 SampleHeight，失败再回落射线）。" +
                 "M1 没有烘焙数据，硬编码平铺会悬空/埋地，必须开这个才能看到效果。")]
        [SerializeField] private bool _snapToGround = true;

        [Tooltip("实例整体缩放倍数。M1 用来让 1m 草在远景下也看得见，M2 烘焙后应保持 1。")]
        [SerializeField] private float _scaleMultiplier = 1f;

        [Tooltip("Shader 名。兜底创建材质时使用。")]
        [SerializeField] private string _shaderName = "FishDemo/Grass/BRGMinimal";

        [Header("距离 LOD（屏幕像素阈值）")]
        [Tooltip("LOD0→LOD1 的切换阈值，单位：草在屏幕上的高度（像素）。" +
                 "用像素而非米，换分辨率/机型时视觉密度不漂移。")]
        [SerializeField] private float _lod0PixelHeight = 100f;

        [Tooltip("LOD1→LOD2 的切换阈值（像素）。")]
        [SerializeField] private float _lod1PixelHeight = 25f;

        [Tooltip("低于该像素高度直接剔除（LOD2 之外）。")]
        [SerializeField] private float _cullPixelHeight = 8f;

        [Tooltip("草在世界里的标称高度（米）。用于把屏幕像素反算成距离。" +
                 "有类型尺寸数据时会被自动推导值覆盖（见 EffectiveNominalHeight）。")]
        [SerializeField] private float _nominalHeight = 1f;

        [Tooltip("关掉 LOD 时只用 LOD0，用于 A/B 对比验证。")]
        [SerializeField] private bool _enableLod = true;

        private float EffectiveNominalHeight
        {
            get
            {
                float h = 0f;
                if (_detailPrototypeScales != null)
                {
                    for (int i = 0; i < _detailPrototypeScales.Length && i < kDetailTypeCount; i++)
                    {
                        var s = _detailPrototypeScales[i];
                        if (s.y > 1e-4f && s.z > 1e-4f)
                            h = Mathf.Max(h, s.y * s.z);
                    }
                }
                return h > 1e-4f ? h : _nominalHeight;
            }
        }

        [SerializeField] private int _debugCullShots = 0;

        private const int kBytesPerInstance = 32;

        private const int kMaxBatchBytes = 16 * 1024 * 1024;

        private struct BatchSlice
        {
            public GraphicsBuffer Buffer;
            public BatchID BatchID;
            public int Start;
            public int Count;
        }

        private List<BatchSlice> _batches;

        private List<int> _batchType;

        private List<(int type, int start, int count)> _slices;


        private static bool s_streamingRequested;
        private static bool s_streamingSuppressed;

        public static void RequestStreaming()
        {
            if (!s_streamingSuppressed)
                s_streamingRequested = true;
        }

        public static void RequestFullLoad()
        {
            s_streamingSuppressed = true;
            s_streamingRequested = false;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void ResetStreamingSuppression()
        {
            s_streamingSuppressed = false;
        }

        public bool IsStreaming => _streaming;

        private bool _streaming;

        private sealed class StreamChunk
        {
            public string Name;
            public NativeArray<float3> Positions;
            public NativeArray<GrassBlock> LocalBlocks;
            public StreamSlice[] Slices;
            public Bounds Bounds;
            public int PosOffset;
            public int GlobalBatchBase;
        }

        private struct StreamSlice
        {
            public int Type;
            public int Start;
            public int Count;
            public GraphicsBuffer Buffer;
            public BatchID BatchID;
        }

        private readonly List<StreamChunk> _streamChunks = new List<StreamChunk>();

        public int StreamingChunkCount => _streamChunks.Count;

        public long StreamingBufferBytes
        {
            get
            {
                long bytes = 0;
                for (int i = 0; i < _streamChunks.Count; i++)
                {
                    var slices = _streamChunks[i].Slices;
                    if (slices == null) continue;
                    for (int j = 0; j < slices.Length; j++)
                    {
                        var buffer = slices[j].Buffer;
                        if (buffer != null) bytes += (long)buffer.count * 4;
                    }
                }
                return bytes + s_pooledBufferBytes;
            }
        }

        private const int kSyntheticMaxInstances = 10000;
        private const int kPoolBucketInstances = 4096;
        private const long kMaxPooledBufferBytes = 32L * 1024 * 1024;
        private static readonly Dictionary<int, Stack<GraphicsBuffer>> s_bufferPool =
            new Dictionary<int, Stack<GraphicsBuffer>>();
        private static long s_pooledBufferBytes;

        private const int kLodCount = 3;

        private const int kDetailTypeCount = 7;

        private static readonly string[] kDetailMeshNames =
        {
            "PW_WildGrass_General_Lod1",
            "PW_LawnGrass_DeadPatch",
            "PW_WildGrass_Understory_Foliage",
            "PW_WildGrass_Understory_Sticks",
            "PW_LawnGrass_CloverFlower",
            "PW_LawnGrass_Daisy",
            "PW_LawnGrass_Dandelions",
        };

        private GrassInstance[] _instances;

        private GrassChunkData _chunkData;

        private List<GrassChunkData> _chunkDataList;

        [Tooltip("烘焙数据目录（相对项目根）。非空时启动自动加载该目录下所有 .bytes。")]
        [SerializeField] private string _grassDataDir = string.Empty;

        [Tooltip("7 种 detail 原型的 LOD0 mesh（索引 = detail 层）。由 DetailBrgSetup 写入。")]
        [SerializeField] private Mesh[] _detailPrototypeMeshes = new Mesh[kDetailTypeCount];

        [Tooltip("各类型 LOD0 的 albedo 图集（索引 = detail 层）。由 DetailBrgSetup 写入。")]
        [SerializeField] private Texture2D[] _detailPrototypeTextures = new Texture2D[kDetailTypeCount];

        [Tooltip("各类型在图集里的精灵格（UV 矩形）。由 DetailBrgSetup 写入。")]
        [SerializeField] private Rect[] _detailPrototypeUvRects = new Rect[kDetailTypeCount];

        [Tooltip("各类型基础尺寸（xz 倍率, y 倍率, 网格高度m）。由 DetailBrgSetup 写入。")]
        [SerializeField] private Vector3[] _detailPrototypeScales = new Vector3[kDetailTypeCount];

        [Tooltip("LOD1 通用 billboard（Gaia PW_DetailCrossPlanes）。由 DetailBrgSetup 写入。")]
        [SerializeField] private Mesh _billboardMedium;

        [Tooltip("LOD2 通用 billboard（Gaia PW_DetailQuad）。由 DetailBrgSetup 写入。")]
        [SerializeField] private Mesh _billboardLow;

        [Tooltip("各类型的 tint（Color 属性）。默认全白。")]
        [SerializeField] private Color[] _detailPrototypeTints =
        {
            Color.white, Color.white, Color.white, Color.white,
            Color.white, Color.white, Color.white,
        };

        private bool _triedAutoLoad;

        private BatchRendererGroup _brg;
        private BatchRendererGroup.OnPerformCulling _performCulling;

        private BatchMaterialID _materialID;

        private readonly BatchMaterialID[] _typeMaterialIDs = new BatchMaterialID[kDetailTypeCount];

        private readonly Mesh[,] _typeLodMeshes = new Mesh[kDetailTypeCount, kLodCount];
        private readonly BatchMeshID[,] _typeLodMeshIDs = new BatchMeshID[kDetailTypeCount, kLodCount];

        private readonly int[] _lodTriangleCounts = new int[kLodCount];

        private Material _runtimeMaterial;

        private readonly Material[] _typeMaterials = new Material[kDetailTypeCount];
        private float _windSpeedBase = 1.2f;
        private float _windStrengthBase = 0.08f;

        private NativeArray<float3> _positions;

        private NativeArray<int> _visibleCount;

        private NativeArray<int> _lodCounts;

        private bool _ready;
        private JobHandle _lastCullHandle;
        private int _lastVisibleCount;

        private void OnEnable()
        {
            Initialize();
        }

        private void OnDisable()
        {
            Shutdown();
        }

        public void Initialize()
        {
            if (_ready)
                return;

            _performCulling = OnPerformCulling;
            _brg = new BatchRendererGroup(_performCulling, IntPtr.Zero);
            _brg.SetEnabledViewTypes(new[] { BatchCullingViewType.Camera });

            var mat = ResolveMaterial();
            if (mat == null)
            {
                Debug.LogError("[GrassBRG] 找不到材质/Shader，BRG 未启动。", this);
                return;
            }

            _windSpeedBase = mat.HasProperty("_WindSpeed") ? mat.GetFloat("_WindSpeed") : 1.2f;
            _windStrengthBase = mat.HasProperty("_WindStrength") ? mat.GetFloat("_WindStrength") : 0.08f;

            BuildLodMeshes();

            _materialID = _brg.RegisterMaterial(mat);

            for (int t = 0; t < kDetailTypeCount; t++)
            {
                _typeMaterialIDs[t] = BatchMaterialID.Null;
                var tex = _detailPrototypeTextures != null && t < _detailPrototypeTextures.Length
                    ? _detailPrototypeTextures[t] : null;
                if (tex == null)
                    continue;

                var m = new Material(mat) { hideFlags = HideFlags.DontSave };
                m.SetTexture("_MainTex", tex);
                m.SetFloat("_UseAlbedoTex", 1f);
                m.EnableKeyword("_USE_ALBEDO_TEX");
                var tint = _detailPrototypeTints != null && t < _detailPrototypeTints.Length
                    ? _detailPrototypeTints[t] : Color.white;
                m.SetColor("_TypeTint", tint);

                var sc = _detailPrototypeScales != null && t < _detailPrototypeScales.Length
                    ? _detailPrototypeScales[t] : Vector3.zero;
                if (sc.x <= 1e-4f || sc.y <= 1e-4f)
                    sc = new Vector3(1f, 1f, 1f);
                if (sc.z <= 1e-4f)
                    sc.z = 1f;
                m.SetVector("_TypeScaleBase", new Vector4(sc.x, sc.y, sc.z, 0f));

                _typeMaterials[t] = m;
                _typeMaterialIDs[t] = _brg.RegisterMaterial(m);
            }

            if (Application.isPlaying && s_streamingRequested &&
                _chunkData == null && _chunkDataList == null)
            {
                bool dataDirReady = !string.IsNullOrEmpty(_grassDataDir) &&
                                    System.IO.Directory.Exists(_grassDataDir);
                if (dataDirReady || !Application.isEditor)
                {
                    if (!dataDirReady)
                        Debug.LogError($"[GrassBRG] 流式模式已请求，但草数据目录不存在：{_grassDataDir}。" +
                                       "打包后 Assets/ 不可用，必须先接 YooAsset；本次不渲染草。");
                    InitStreamingEmpty();
                    _ready = true;
                    return;
                }
            }

            BuildInstances();

            int maxPerBatch = kMaxBatchBytes / kBytesPerInstance;
            var typeRanges = ComputeTypeRanges();
            var slices = new List<(int type, int start, int count)>();
            for (int t = 0; t < kDetailTypeCount; t++)
            {
                var (tStart, tCount) = typeRanges[t];
                for (int off = 0; off < tCount; off += maxPerBatch)
                {
                    int cnt = Mathf.Min(maxPerBatch, tCount - off);
                    slices.Add((t, tStart + off, cnt));
                }
            }

            _batches = new List<BatchSlice>(slices.Count);
            _batchType = new List<int>(slices.Count);

            for (int b = 0; b < slices.Count; b++)
            {
                var (type, start, count) = slices[b];

                int elementCount = count * (kBytesPerInstance / 4);
                var buffer = new GraphicsBuffer(GraphicsBuffer.Target.Raw, elementCount, 4);
                buffer.name = $"GrassBRG_T{type}_Data[{b}]";

                UploadInstanceData(buffer, start, count);

                var metadata = new NativeArray<MetadataValue>(2, Allocator.Temp);
                metadata[0] = new MetadataValue
                {
                    NameID = Shader.PropertyToID("_GrassPos"),
                    Value = 0x80000000u,
                };
                metadata[1] = new MetadataValue
                {
                    NameID = Shader.PropertyToID("_GrassNormalScale"),
                    Value = 0x80000000u | (uint)(count * 16),
                };
                var batchID = _brg.AddBatch(metadata, buffer.bufferHandle);
                metadata.Dispose();

                _batches.Add(new BatchSlice
                {
                    Buffer = buffer,
                    BatchID = batchID,
                    Start = start,
                    Count = count,
                });
                _batchType.Add(type);
            }

            Debug.Log($"[GrassBRG] {_instanceCount:N0} 实例切分为 {slices.Count} 个 batch " +
                      $"（{kDetailTypeCount} 类型分段 × 上限 {maxPerBatch:N0}/批）");

            _slices = slices;
            AssignBlockBatches();
            BuildBlockArray();

            _brg.SetGlobalBounds(ComputeWorldBounds());

            _ready = true;
        }

        private void InitStreamingEmpty()
        {
            _streaming = true;
            GroundResolved = true;
            _instanceCount = 0;
            _positions = default;
            _blocksNative = default;
            _blocks = null;
            _batches = new List<BatchSlice>();
            _batchType = new List<int>();
            _visibleCount = new NativeArray<int>(1, Allocator.Persistent, NativeArrayOptions.ClearMemory);
            _lodCounts = new NativeArray<int>(kLodCount, Allocator.Persistent, NativeArrayOptions.ClearMemory);
            _brg.SetGlobalBounds(new Bounds(transform.position, Vector3.one));
            Debug.Log($"[GrassBRG] 流式模式：等待 SECTR chunk（数据目录 {_grassDataDir}）");
        }

        public void SetWeatherWind(float windSpeed, float turbulence)
        {
            float speedMul = 0.6f + Mathf.Clamp01(windSpeed) * 2.0f;
            float strengthMul = 0.5f + Mathf.Clamp01(windSpeed) * 2.0f +
                                Mathf.Clamp01(turbulence) * 0.5f;

            for (int t = 0; t < kDetailTypeCount; t++)
            {
                var m = _typeMaterials[t];
                if (m == null) continue;
                m.SetFloat("_WindSpeed", _windSpeedBase * speedMul);
                m.SetFloat("_WindStrength", _windStrengthBase * strengthMul);
            }
        }

        private void AssignBlockBatches()
        {
            if (_blocks == null || _blocks.Count == 0 || _slices == null || _slices.Count == 0)
                return;

            long covered = 0;
            int expectedNext = 0;
            bool contiguous = true;
            foreach (var (_, start, count) in _slices)
            {
                if (start != expectedNext) contiguous = false;
                expectedNext = start + count;
                covered += count;
            }
            if (!contiguous || covered != _instanceCount)
            {
                Debug.LogError($"[GrassBRG] batch 分段未覆盖全部实例：" +
                               $"covered={covered} n={_instanceCount} contiguous={contiguous} " +
                               $"（草可能整体消失，检查 SortInstancesIntoBlocks 的类型排序键）");
            }

            var result = new List<GrassBlock>(_blocks.Count);
            int si = 0;
            for (int bi = 0; bi < _blocks.Count; bi++)
            {
                GrassBlock block = _blocks[bi];
                int s = block.Start;
                int end = s + block.Count;

                while (si < _slices.Count - 1 &&
                       _slices[si].start + _slices[si].count <= s)
                    si++;

                int j = si;
                while (s < end && j < _slices.Count)
                {
                    int sliceEnd = _slices[j].start + _slices[j].count;
                    int take = Mathf.Min(end, sliceEnd) - s;
                    if (take <= 0) { j++; continue; }

                    var nb = block;
                    nb.Start = s;
                    nb.Count = take;
                    nb.BatchIndex = j;
                    nb.BatchStart = _slices[j].start;
                    nb.ChunkBase = 0;
                    result.Add(nb);

                    s += take;
                    j++;
                }
            }

            _blocks = result;
        }

        public void Shutdown()
        {
            if (!_ready && _brg == null)
                return;

            _ready = false;

            _lastCullHandle.Complete();

            if (_brg != null)
            {
                _brg.Dispose();
                _brg = null;
            }

            ClearStreamChunks(returnBuffers: true);

            if (!_streaming && _batches != null)
            {
                foreach (var b in _batches)
                    b.Buffer.Release();
            }
            _batches = null;
            _streaming = false;

            var ownedMeshes = new HashSet<Mesh>();
            for (int t = 0; t < kDetailTypeCount; t++)
            {
                for (int lod = 0; lod < kLodCount; lod++)
                {
                    var m = _typeLodMeshes[t, lod];
                    if (m != null && m.name.StartsWith("GrassBRG_"))
                        ownedMeshes.Add(m);
                    _typeLodMeshes[t, lod] = null;
                }
            }
            foreach (var m in ownedMeshes)
                DestroyRuntimeObject(m);

            _batchType?.Clear();
            _slices = null;

            if (_runtimeMaterial != null)
            {
                DestroyRuntimeObject(_runtimeMaterial);
                _runtimeMaterial = null;
            }

            for (int t = 0; t < kDetailTypeCount; t++)
            {
                if (_typeMaterials[t] != null)
                {
                    DestroyRuntimeObject(_typeMaterials[t]);
                    _typeMaterials[t] = null;
                }
                _typeMaterialIDs[t] = BatchMaterialID.Null;
            }

            if (_positions.IsCreated)
                _positions.Dispose();
            if (_visibleCount.IsCreated)
                _visibleCount.Dispose();
            if (_lodCounts.IsCreated)
                _lodCounts.Dispose();
            if (_blocksNative.IsCreated)
                _blocksNative.Dispose();
            _blocks = null;
        }

        private static void DestroyRuntimeObject(UnityEngine.Object obj)
        {
            if (Application.isPlaying)
                Destroy(obj);
            else
                DestroyImmediate(obj);
        }


        public bool AddGrassChunk(string sceneName)
        {
            if (!_streaming || string.IsNullOrEmpty(sceneName))
                return false;
            if (FindStreamChunk(sceneName) >= 0)
                return false;

            string path = System.IO.Path.Combine(_grassDataDir, sceneName + ".bytes");
            if (!System.IO.File.Exists(path))
            {
                Debug.LogWarning($"[GrassBRG] 草数据缺失，跳过 chunk: {path}");
                return false;
            }

            NativeArray<GrassInstance> instances = default;
            NativeArray<GrassBlock> localBlocks = default;
            StreamChunk chunk = null;
            try
            {
                GrassChunkFile.Read(path, out instances, out Bounds bounds);

                localBlocks = GrassChunkFile.SortIntoBlocks(
                    instances, kBlockSize, _cullRadius, EffectiveNominalHeight * _scaleMultiplier);

                var slices = BuildStreamSlices(instances, sceneName);

                var positions = new NativeArray<float3>(instances.Length, Allocator.Persistent,
                    NativeArrayOptions.UninitializedMemory);
                for (int i = 0; i < instances.Length; i++)
                    positions[i] = instances[i].Position;

                chunk = new StreamChunk
                {
                    Name = sceneName,
                    Positions = positions,
                    LocalBlocks = localBlocks,
                    Slices = slices,
                    Bounds = bounds,
                };
                localBlocks = default;
                _streamChunks.Add(chunk);
            }
            catch (Exception e)
            {
                Debug.LogError($"[GrassBRG] 加载 chunk 失败 {sceneName}: {e.Message}");
                if (chunk != null)
                    ReleaseStreamChunk(chunk, removeBatches: true);
                else if (localBlocks.IsCreated)
                    localBlocks.Dispose();
                return false;
            }
            finally
            {
                if (instances.IsCreated) instances.Dispose();
            }

            RebuildStreamingLayout();
            return true;
        }

        public bool RemoveGrassChunk(string sceneName)
        {
            if (!_streaming)
                return false;
            int idx = FindStreamChunk(sceneName);
            if (idx < 0)
                return false;

            _lastCullHandle.Complete();
            StreamChunk chunk = _streamChunks[idx];
            _streamChunks.RemoveAt(idx);
            ReleaseStreamChunk(chunk, removeBatches: true);
            RebuildStreamingLayout();
            return true;
        }

        public bool HasGrassChunk(string sceneName) => FindStreamChunk(sceneName) >= 0;

        public void ClearGrassChunks()
        {
            if (!_streaming || _streamChunks.Count == 0)
                return;

            _lastCullHandle.Complete();
            for (int i = 0; i < _streamChunks.Count; i++)
                ReleaseStreamChunk(_streamChunks[i], removeBatches: true);
            _streamChunks.Clear();
            RebuildStreamingLayout();
        }

        private int FindStreamChunk(string sceneName)
        {
            for (int i = 0; i < _streamChunks.Count; i++)
                if (string.Equals(_streamChunks[i].Name, sceneName, StringComparison.Ordinal))
                    return i;
            return -1;
        }

        private StreamSlice[] BuildStreamSlices(NativeArray<GrassInstance> instances, string sceneName)
        {
            int maxPerBatch = kMaxBatchBytes / kBytesPerInstance;
            var list = new List<StreamSlice>();
            try
            {
                int n = instances.Length;
                int i = 0;
                while (i < n)
                {
                    int t = Mathf.Clamp(instances[i].Type, 0, kDetailTypeCount - 1);
                    int j = i + 1;
                    while (j < n && Mathf.Clamp(instances[j].Type, 0, kDetailTypeCount - 1) == t)
                        j++;

                    for (int off = i; off < j; off += maxPerBatch)
                    {
                        int cnt = Mathf.Min(maxPerBatch, j - off);
                        var buffer = RentGrassBuffer(cnt,
                            $"GrassBRG_{sceneName}_T{t}[{list.Count}]");
                        UploadInstanceData(buffer, instances, off, cnt);
                        var batchID = RegisterBatch(buffer, cnt);
                        list.Add(new StreamSlice
                        {
                            Type = t,
                            Start = off,
                            Count = cnt,
                            Buffer = buffer,
                            BatchID = batchID,
                        });
                    }
                    i = j;
                }
            }
            catch
            {
                foreach (var s in list)
                {
                    if (_brg != null) _brg.RemoveBatch(s.BatchID);
                    ReturnGrassBuffer(s.Buffer);
                }
                throw;
            }
            return list.ToArray();
        }

        private BatchID RegisterBatch(GraphicsBuffer buffer, int count)
        {
            var metadata = new NativeArray<MetadataValue>(2, Allocator.Temp);
            metadata[0] = new MetadataValue
            {
                NameID = Shader.PropertyToID("_GrassPos"),
                Value = 0x80000000u,
            };
            metadata[1] = new MetadataValue
            {
                NameID = Shader.PropertyToID("_GrassNormalScale"),
                Value = 0x80000000u | (uint)(count * 16),
            };
            var id = _brg.AddBatch(metadata, buffer.bufferHandle);
            metadata.Dispose();
            return id;
        }

        private void ReleaseStreamChunk(StreamChunk chunk, bool removeBatches)
        {
            if (chunk.Slices != null)
            {
                foreach (var s in chunk.Slices)
                {
                    if (removeBatches && _brg != null)
                        _brg.RemoveBatch(s.BatchID);
                    ReturnGrassBuffer(s.Buffer);
                }
            }
            if (chunk.Positions.IsCreated) chunk.Positions.Dispose();
            if (chunk.LocalBlocks.IsCreated) chunk.LocalBlocks.Dispose();
        }

        private void ClearStreamChunks(bool returnBuffers)
        {
            for (int i = 0; i < _streamChunks.Count; i++)
                ReleaseStreamChunk(_streamChunks[i], removeBatches: false);
            _streamChunks.Clear();
        }

        private void RebuildStreamingLayout()
        {
            _lastCullHandle.Complete();

            int total = 0;
            int batchCount = 0;
            for (int i = 0; i < _streamChunks.Count; i++)
            {
                total += _streamChunks[i].Positions.Length;
                batchCount += _streamChunks[i].Slices.Length;
            }

            if (_positions.IsCreated) _positions.Dispose();
            _positions = total > 0
                ? new NativeArray<float3>(total, Allocator.Persistent, NativeArrayOptions.UninitializedMemory)
                : default;

            _batches = new List<BatchSlice>(batchCount);
            _batchType = new List<int>(batchCount);

            int posOffset = 0;
            int batchBase = 0;
            int stride = UnsafeUtility.SizeOf<float3>();
            for (int ci = 0; ci < _streamChunks.Count; ci++)
            {
                var c = _streamChunks[ci];
                c.PosOffset = posOffset;
                c.GlobalBatchBase = batchBase;

                if (c.Positions.Length > 0)
                {
                    unsafe
                    {
                        UnsafeUtility.MemCpy(
                            (byte*)_positions.GetUnsafePtr() + (long)posOffset * stride,
                            c.Positions.GetUnsafePtr(),
                            (long)c.Positions.Length * stride);
                    }
                }
                posOffset += c.Positions.Length;

                for (int si = 0; si < c.Slices.Length; si++)
                {
                    var s = c.Slices[si];
                    _batches.Add(new BatchSlice
                    {
                        Buffer = s.Buffer,
                        BatchID = s.BatchID,
                        Start = s.Start,
                        Count = s.Count,
                    });
                    _batchType.Add(s.Type);
                }
                batchBase += c.Slices.Length;
            }

            var blocks = new List<GrassBlock>(batchCount * 8);
            for (int ci = 0; ci < _streamChunks.Count; ci++)
            {
                var c = _streamChunks[ci];
                for (int bi = 0; bi < c.LocalBlocks.Length; bi++)
                {
                    var lb = c.LocalBlocks[bi];
                    for (int si = 0; si < c.Slices.Length; si++)
                    {
                        var s = c.Slices[si];
                        if (s.Type != lb.TypeIndex)
                            continue;
                        int ovStart = Mathf.Max(lb.Start, s.Start);
                        int ovEnd = Mathf.Min(lb.Start + lb.Count, s.Start + s.Count);
                        if (ovEnd <= ovStart)
                            continue;

                        var gb = lb;
                        gb.Start = c.PosOffset + ovStart;
                        gb.Count = ovEnd - ovStart;
                        gb.BatchIndex = c.GlobalBatchBase + si;
                        gb.BatchStart = s.Start;
                        gb.ChunkBase = c.PosOffset;
                        blocks.Add(gb);
                    }
                }
            }

            if (_blocksNative.IsCreated) _blocksNative.Dispose();
            _blocksNative = blocks.Count > 0
                ? new NativeArray<GrassBlock>(blocks.Count, Allocator.Persistent, NativeArrayOptions.UninitializedMemory)
                : default;
            for (int i = 0; i < blocks.Count; i++)
                _blocksNative[i] = blocks[i];

            _instanceCount = total;

            Bounds bounds;
            if (_streamChunks.Count > 0)
            {
                bounds = _streamChunks[0].Bounds;
                for (int i = 1; i < _streamChunks.Count; i++)
                    bounds.Encapsulate(_streamChunks[i].Bounds);
                bounds.min = new Vector3(bounds.min.x, bounds.min.y - _cullRadius, bounds.min.z);
                bounds.max = new Vector3(bounds.max.x, bounds.max.y + _cullRadius + 1f, bounds.max.z);
            }
            else
            {
                bounds = new Bounds(transform.position, Vector3.one);
            }
            _brg.SetGlobalBounds(bounds);
        }


        private static int RoundedBucket(int instances)
        {
            int b = Mathf.CeilToInt(instances / (float)kPoolBucketInstances) * kPoolBucketInstances;
            return Mathf.Max(kPoolBucketInstances, b);
        }

        private static GraphicsBuffer RentGrassBuffer(int instances, string name)
        {
            int bucket = RoundedBucket(instances);
            if (s_bufferPool.TryGetValue(bucket, out var stack) && stack.Count > 0)
            {
                var reused = stack.Pop();
                s_pooledBufferBytes -= (long)bucket * kBytesPerInstance;
                reused.name = name;
                return reused;
            }
            return new GraphicsBuffer(GraphicsBuffer.Target.Raw,
                bucket * (kBytesPerInstance / 4), 4) { name = name };
        }

        private static void ReturnGrassBuffer(GraphicsBuffer buffer)
        {
            if (buffer == null)
                return;

            int bucket = buffer.count / (kBytesPerInstance / 4);
            long bytes = (long)bucket * kBytesPerInstance;
            if (bytes > kMaxPooledBufferBytes || s_pooledBufferBytes + bytes > kMaxPooledBufferBytes)
            {
                buffer.Release();
                return;
            }

            if (!s_bufferPool.TryGetValue(bucket, out var stack))
            {
                stack = new Stack<GraphicsBuffer>();
                s_bufferPool[bucket] = stack;
            }
            stack.Push(buffer);
            s_pooledBufferBytes += bytes;
        }

        public static void ReleaseBufferPool()
        {
            foreach (var stack in s_bufferPool.Values)
            {
                while (stack.Count > 0)
                {
                    var b = stack.Pop();
                    if (b != null) b.Release();
                }
            }
            s_bufferPool.Clear();
            s_pooledBufferBytes = 0;
        }

        private void OnApplicationQuit()
        {
            ReleaseBufferPool();
        }

        public void SetMaterialForValidation(Material mat)
        {
            _material = mat;
        }

        public void SetChunkData(GrassChunkData data)
        {
            if (_ready)
                Shutdown();

            _chunkData = data;
            _chunkDataList = null;
            Initialize();
        }

        public void SetChunkDataList(List<GrassChunkData> chunks)
        {
            if (_ready)
                Shutdown();

            _chunkData = null;
            _chunkDataList = chunks;
            Initialize();
        }

        public void SetDetailPrototypeMeshes(Mesh[] meshes)
        {
            _detailPrototypeMeshes = meshes;

            if (_ready) Shutdown();
            Initialize();
        }

        public void ApplyDetailPrototypeSetup(Mesh[] meshes, Texture2D[] textures, Color[] tints,
            Rect[] spriteUvRects = null, Vector3[] typeScales = null,
            Mesh billboardMedium = null, Mesh billboardLow = null, bool rebuild = true)
        {
            if (meshes != null && meshes.Length == kDetailTypeCount)
                _detailPrototypeMeshes = meshes;
            if (textures != null && textures.Length == kDetailTypeCount)
                _detailPrototypeTextures = textures;
            if (tints != null && tints.Length == kDetailTypeCount)
                _detailPrototypeTints = tints;
            if (spriteUvRects != null && spriteUvRects.Length == kDetailTypeCount)
                _detailPrototypeUvRects = spriteUvRects;
            if (typeScales != null && typeScales.Length == kDetailTypeCount)
                _detailPrototypeScales = typeScales;
            if (billboardMedium != null)
                _billboardMedium = billboardMedium;
            if (billboardLow != null)
                _billboardLow = billboardLow;

            if (!rebuild)
                return;

            if (_ready) Shutdown();
            Initialize();
        }

        public void SetDetailPrototypeTextures(Texture2D[] textures, Color[] tints)
        {
            _detailPrototypeTextures = textures;
            if (tints != null && tints.Length == kDetailTypeCount)
                _detailPrototypeTints = tints;

            if (_ready) Shutdown();
            Initialize();
        }

        public void SetGrassDataDir(string dir)
        {
            if (_ready)
                Shutdown();

            _grassDataDir = dir ?? string.Empty;
            _chunkData = null;
            _chunkDataList = null;
            _triedAutoLoad = false;
            Initialize();
        }

        public Vector3 GetInstancePosition(int i)
        {
            if (_streaming)
            {
                if (!_positions.IsCreated || i < 0 || i >= _positions.Length)
                    return Vector3.zero;
                var p = _positions[i];
                return new Vector3(p.x, p.y, p.z);
            }
            return _instances != null && i >= 0 && i < _instances.Length
                ? new Vector3(_instances[i].Position.x, _instances[i].Position.y, _instances[i].Position.z)
                : Vector3.zero;
        }

        public int InstanceCount => _streaming ? _instanceCount : (_instances?.Length ?? 0);

        public void SetDebugCullShots(int count) => _debugCullShots = count;

        public void ConfigureLodForValidation(bool enabled, float lod0Px, float lod1Px, float cullPx)
        {
            Shutdown();
            _enableLod = enabled;
            _lod0PixelHeight = lod0Px;
            _lod1PixelHeight = lod1Px;
            _cullPixelHeight = cullPx;
        }

        public void ConfigureForValidation(int instanceCount, float spacing, float cullRadius)
        {
            Shutdown();
            _instanceCount = Mathf.Max(1, instanceCount);
            _spacing = spacing;
            _cullRadius = cullRadius;
        }

        public void SetAnchor(Transform anchor, bool snapToGround)
        {
            _anchor = anchor;
            _snapToGround = snapToGround;
        }

        public int LastVisibleCount => _visibleCount.IsCreated ? _visibleCount[0] : -1;

        public int GetLastLodCount(int lod) =>
            _lodCounts.IsCreated && lod >= 0 && lod < kLodCount ? _lodCounts[lod] : -1;

        public int GetLodTriangleCount(int lod) =>
            lod >= 0 && lod < kLodCount ? _lodTriangleCounts[lod] : 0;

        public long LastSubmittedTriangles
        {
            get
            {
                if (!_lodCounts.IsCreated) return -1;
                long sum = 0;
                for (int i = 0; i < kLodCount; i++)
                    sum += (long)_lodCounts[i] * _lodTriangleCounts[i];
                return sum;
            }
        }

        public int[] GetLodTriangleCounts() => (int[])_lodTriangleCounts.Clone();

        public bool IsReady => _ready;

        public bool GroundResolved { get; private set; }

        private float _regroundTimer = 0.5f;

        private void Update()
        {
            if (_visibleCount.IsCreated)
                _lastVisibleCount = _visibleCount[0];

            if (_ready && _snapToGround && !GroundResolved && Application.isPlaying)
            {
                _regroundTimer -= Time.unscaledDeltaTime;
                if (_regroundTimer <= 0f)
                {
                    _regroundTimer = 0.5f;
                    TryReground();
                }
            }
        }

        private void TryReground()
        {
            if (FindObjectsOfType<Terrain>().Length == 0)
                return;

            BuildInstances();
            AssignBlockBatches();
            BuildBlockArray();
            foreach (var b in _batches)
                UploadInstanceData(b.Buffer, b.Start, b.Count);

            _brg.SetGlobalBounds(ComputeWorldBounds());
            Debug.Log($"[GrassBRG] 地表就绪，已重新贴地布点（{_instanceCount} 实例）");
        }

        public Material CurrentMaterial => _material != null ? _material : _runtimeMaterial;

        private void OnGUI()
        {
            if (!_ready)
                return;
            GUI.Label(new Rect(12, 12, 760, 22),
                _streaming
                    ? $"GrassBRG 流式 | chunk {_streamChunks.Count} | 驻留实例 {_instanceCount:N0} | GPU {StreamingBufferBytes / 1048576f:F1}MB | 可见 {_lastVisibleCount}"
                    : $"GrassBRG M2 | 实例 {_instanceCount:N0} | 可见 {_lastVisibleCount}");
        }


        private Material ResolveMaterial()
        {
            if (_material != null)
                return _material;

            var shader = Shader.Find(_shaderName);
            if (shader == null)
                return null;

            _runtimeMaterial = new Material(shader) { hideFlags = HideFlags.DontSave };
            return _runtimeMaterial;
        }

        private void SortInstancesIntoBlocks()
        {
            int n = _instances.Length;
            if (n == 0) { _blocks = null; return; }

            float minX = float.MaxValue, minZ = float.MaxValue;
            float maxX = float.MinValue, maxZ = float.MinValue;
            for (int i = 0; i < n; i++)
            {
                var p = _instances[i].Position;
                if (p.x < minX) minX = p.x;
                if (p.x > maxX) maxX = p.x;
                if (p.z < minZ) minZ = p.z;
                if (p.z > maxZ) maxZ = p.z;
            }

            int gridW = Mathf.Max(1, Mathf.CeilToInt((maxX - minX) / kBlockSize));
            int gridH = Mathf.Max(1, Mathf.CeilToInt((maxZ - minZ) / kBlockSize));

            int blockCount = gridW * gridH;
            int keySpace = kDetailTypeCount * blockCount;

            var counts = new int[keySpace + 1];
            var keyOf = new int[n];
            for (int i = 0; i < n; i++)
            {
                var p = _instances[i].Position;
                int bx = Mathf.Clamp((int)((p.x - minX) / kBlockSize), 0, gridW - 1);
                int bz = Mathf.Clamp((int)((p.z - minZ) / kBlockSize), 0, gridH - 1);
                int spatial = bz * gridW + bx;
                int t = Mathf.Clamp(_instances[i].Type, 0, kDetailTypeCount - 1);
                int key = t * blockCount + spatial;
                keyOf[i] = key;
                counts[key + 1]++;
            }

            for (int k = 0; k < keySpace; k++)
                counts[k + 1] += counts[k];

            var sorted = new GrassInstance[n];
            var write = (int[])counts.Clone();
            for (int i = 0; i < n; i++)
                sorted[write[keyOf[i]]++] = _instances[i];
            _instances = sorted;

            var blocks = new List<GrassBlock>();
            for (int k = 0; k < keySpace; k++)
            {
                int start = counts[k];
                int count = counts[k + 1] - start;
                if (count == 0) continue;

                int typeIndex = k / blockCount;


                float byMin = float.MaxValue, byMax = float.MinValue;
                float bxMin = float.MaxValue, bxMax = float.MinValue;
                float bzMin = float.MaxValue, bzMax = float.MinValue;
                for (int i = start; i < start + count; i++)
                {
                    var p = _instances[i].Position;
                    if (p.y < byMin) byMin = p.y;
                    if (p.y > byMax) byMax = p.y;
                    if (p.x < bxMin) bxMin = p.x;
                    if (p.x > bxMax) bxMax = p.x;
                    if (p.z < bzMin) bzMin = p.z;
                    if (p.z > bzMax) bzMax = p.z;
                }

                float grassH = EffectiveNominalHeight * _scaleMultiplier;
                blocks.Add(new GrassBlock
                {
                    Start = start,
                    Count = count,
                    Center = new float3(
                        (bxMin + bxMax) * 0.5f,
                        (byMin + byMax) * 0.5f + grassH * 0.5f,
                        (bzMin + bzMax) * 0.5f),
                    HalfExtent = new float3(
                        (bxMax - bxMin) * 0.5f + _cullRadius,
                        (byMax - byMin) * 0.5f + grassH * 0.5f + _cullRadius,
                        (bzMax - bzMin) * 0.5f + _cullRadius),
                    TypeIndex = typeIndex,
                });
            }

            _blocks = blocks;

            var perType = new int[kDetailTypeCount];
            foreach (var b in blocks) perType[b.TypeIndex] += b.Count;
            var typeStatSb = new System.Text.StringBuilder();
            for (int i = 0; i < kDetailTypeCount; i++)
            {
                if (i > 0) typeStatSb.Append(' ');
                typeStatSb.Append($"T{i}={perType[i]:N0}");
            }
            string typeStat = typeStatSb.ToString();

            Debug.Log($"[GrassBRG] 分块（类型×空间）：{n:N0} 实例 → {blocks.Count} 个非空块 " +
                      $"（{kBlockSize}m 网格，{gridW}x{gridH}）；{typeStat}");
        }

        private const float kBlockSize = 32f;

        private List<GrassBlock> _blocks;

        private NativeArray<GrassBlock> _blocksNative;

        private void BuildBlockArray()
        {
            if (_blocksNative.IsCreated)
                _blocksNative.Dispose();

            if (_blocks == null || _blocks.Count == 0)
            {
                _blocksNative = default;
                return;
            }

            _blocksNative = new NativeArray<GrassBlock>(_blocks.Count, Allocator.Persistent);
            for (int i = 0; i < _blocks.Count; i++)
                _blocksNative[i] = _blocks[i];
        }

        private (int start, int count)[] ComputeTypeRanges()
        {
            var ranges = new (int start, int count)[kDetailTypeCount];
            int n = _instances?.Length ?? 0;
            for (int t = 0; t < kDetailTypeCount; t++)
                ranges[t] = (0, 0);

            int i = 0;
            while (i < n)
            {
                int t = Mathf.Clamp(_instances[i].Type, 0, kDetailTypeCount - 1);
                int j = i;
                while (j < n && Mathf.Clamp(_instances[j].Type, 0, kDetailTypeCount - 1) == t)
                    j++;

                ranges[t] = (i, j - i);
                i = j;
            }
            return ranges;
        }

        private void BuildInstances()
        {
            if (_visibleCount.IsCreated)
                _visibleCount.Dispose();
            if (_lodCounts.IsCreated)
                _lodCounts.Dispose();

            if (!_triedAutoLoad && _chunkDataList == null && _chunkData == null
                && !string.IsNullOrEmpty(_grassDataDir))
            {
                _triedAutoLoad = true;
                TryAutoLoadChunkData();
            }

            if (_chunkDataList != null && _chunkDataList.Count > 0)
            {
                int total = 0;
                foreach (var c in _chunkDataList)
                    total += c.Instances?.Length ?? 0;

                _instances = new GrassInstance[total];
                int offset = 0;
                foreach (var c in _chunkDataList)
                {
                    if (c.Instances == null || c.Instances.Length == 0) continue;
                    System.Array.Copy(c.Instances, 0, _instances, offset, c.Instances.Length);
                    offset += c.Instances.Length;
                }

                SortInstancesIntoBlocks();
                _instanceCount = total;
                GroundResolved = true;
            }
            else if (_chunkData != null && _chunkData.Instances != null && _chunkData.Instances.Length > 0)
            {
                _instances = (GrassInstance[])_chunkData.Instances.Clone();
                SortInstancesIntoBlocks();
                _instanceCount = _instances.Length;
                GroundResolved = true;
            }
            else
            {
                _instances = SynthesizeGridInstances();
                _instanceCount = _instances.Length;
                SortInstancesIntoBlocks();
                GroundResolved = !_snapToGround || FindObjectsOfType<Terrain>().Length > 0;
            }

            if (_positions.IsCreated)
                _positions.Dispose();
            _positions = new NativeArray<float3>(_instanceCount, Allocator.Persistent);
            for (int i = 0; i < _instanceCount; i++)
                _positions[i] = _instances[i].Position;


            _visibleCount = new NativeArray<int>(1, Allocator.Persistent);
            _lodCounts = new NativeArray<int>(kLodCount, Allocator.Persistent);
        }

        private void TryAutoLoadChunkData()
        {
            string dir = _grassDataDir;
            if (!System.IO.Directory.Exists(dir))
            {
                Debug.LogWarning($"[GrassBRG] 草数据目录不存在，退回 M1 合成方阵: {dir}");
                return;
            }

            string[] files;
            try
            {
                files = System.IO.Directory.GetFiles(dir, "*.bytes");
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[GrassBRG] 枚举草数据失败: {e.Message}");
                return;
            }

            if (files.Length == 0)
            {
                Debug.LogWarning($"[GrassBRG] 草数据目录为空，退回 M1 合成方阵: {dir}");
                return;
            }

            var list = new List<GrassChunkData>(files.Length);
            long total = 0;
            foreach (var f in files)
            {
                try
                {
                    var d = GrassChunkData.Read(System.IO.File.ReadAllBytes(f),
                        System.IO.Path.GetFileNameWithoutExtension(f));
                    if (d.Instances != null && d.Instances.Length > 0)
                    {
                        list.Add(d);
                        total += d.Instances.Length;
                    }
                }
                catch (Exception e)
                {
                    Debug.LogError($"[GrassBRG] 读取失败 {f}: {e.Message}");
                }
            }

            if (list.Count == 0)
                return;

            _chunkDataList = list;
            Debug.Log($"[GrassBRG] 自动加载 {list.Count} 个 chunk 的草数据，共 {total:N0} 实例");
        }

        private GrassInstance[] SynthesizeGridInstances()
        {
            int count = Mathf.Min(_instanceCount, kSyntheticMaxInstances);
            if (count != _instanceCount)
                Debug.LogWarning($"[GrassBRG] 合成兜底实例数从 {_instanceCount:N0} 夹到 {count:N0}，避免移动端 OOM。");
            int side = Mathf.CeilToInt(Mathf.Sqrt(count));
            var result = new GrassInstance[count];

            Vector3 origin = _anchor != null ? _anchor.position : transform.position;
            Terrain[] terrains = _snapToGround ? FindObjectsOfType<Terrain>() : null;

            for (int i = 0; i < count; i++)
            {
                int x = i % side;
                int z = i / side;
                float ox = (x - (side - 1) * 0.5f) * _spacing;
                float oz = (z - (side - 1) * 0.5f) * _spacing;
                float wx = origin.x + ox;
                float wz = origin.z + oz;
                float wy = _snapToGround
                    ? SampleGroundHeight(wx, wz, origin.y, terrains)
                    : origin.y;

                var inst = new GrassInstance
                {
                    Position = new float3(wx, wy, wz),
                    Normal = new float3(0f, 1f, 0f),
                    Color = 0xFFFFFFFFu,
                };
                inst.YawQ = (ushort)((i * 40503) & 0xFFFF);
                inst.ScaleQ = (byte)(128 + (i % 7) * 4 - 12);
                inst.Type = 0;
                result[i] = inst;
            }

            return result;
        }

        private static float SampleGroundHeight(float x, float z, float fallbackY, Terrain[] terrains)
        {
            if (terrains != null)
            {
                foreach (var t in terrains)
                {
                    if (t == null || t.terrainData == null) continue;
                    Vector3 tp = t.transform.position;
                    Vector3 ts = t.terrainData.size;
                    if (x < tp.x || x > tp.x + ts.x || z < tp.z || z > tp.z + ts.z)
                        continue;

                    return t.SampleHeight(new Vector3(x, 0f, z)) + tp.y;
                }
            }

            if (Physics.Raycast(new Vector3(x, fallbackY + 200f, z), Vector3.down,
                    out RaycastHit hit, 600f, ~0, QueryTriggerInteraction.Ignore))
            {
                return hit.point.y;
            }

            return fallbackY;
        }

        private void UploadInstanceData(GraphicsBuffer buffer, int sliceStart, int sliceCount)
        {
            var packed = new NativeArray<uint>(sliceCount * 8, Allocator.Temp);

            for (int i = 0; i < sliceCount; i++)
            {
                var inst = _instances[sliceStart + i];
                int o = i * 4;
                packed[o + 0] = math.asuint(inst.Position.x);
                packed[o + 1] = math.asuint(inst.Position.y);
                packed[o + 2] = math.asuint(inst.Position.z);
                packed[o + 3] = math.asuint(inst.YawRadians / (2f * math.PI));
            }

            int baseOffset = sliceCount * 4;
            for (int i = 0; i < sliceCount; i++)
            {
                var inst = _instances[sliceStart + i];
                int o = baseOffset + i * 4;
                packed[o + 0] = math.asuint(inst.Normal.x);
                packed[o + 1] = math.asuint(inst.Normal.y);
                packed[o + 2] = math.asuint(inst.Normal.z);
                packed[o + 3] = math.asuint(inst.Scale * _scaleMultiplier);
            }

            buffer.SetData(packed);
            packed.Dispose();
        }

        private void UploadInstanceData(GraphicsBuffer buffer, NativeArray<GrassInstance> src, int start, int count)
        {
            var packed = new NativeArray<uint>(count * 8, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
            for (int i = 0; i < count; i++)
            {
                var inst = src[start + i];
                int o = i * 4;
                packed[o + 0] = math.asuint(inst.Position.x);
                packed[o + 1] = math.asuint(inst.Position.y);
                packed[o + 2] = math.asuint(inst.Position.z);
                packed[o + 3] = math.asuint(inst.YawRadians / (2f * math.PI));
            }
            int baseOffset = count * 4;
            for (int i = 0; i < count; i++)
            {
                var inst = src[start + i];
                int o = baseOffset + i * 4;
                packed[o + 0] = math.asuint(inst.Normal.x);
                packed[o + 1] = math.asuint(inst.Normal.y);
                packed[o + 2] = math.asuint(inst.Normal.z);
                packed[o + 3] = math.asuint(inst.Scale * _scaleMultiplier);
            }
            buffer.SetData(packed);
            packed.Dispose();
        }

        private Bounds ComputeWorldBounds()
        {
            if (!_positions.IsCreated || _positions.Length == 0)
                return new Bounds(transform.position, Vector3.one);

            float3 min = _positions[0];
            float3 max = _positions[0];
            for (int i = 1; i < _positions.Length; i++)
            {
                min = math.min(min, _positions[i]);
                max = math.max(max, _positions[i]);
            }
            min.y -= _cullRadius;
            max.y += _cullRadius + 1f;

            Vector3 center = (Vector3)((min + max) * 0.5f);
            Vector3 size = (Vector3)(max - min);
            return new Bounds(center, size);
        }

        private void BuildLodMeshes()
        {
            int registered = 0;
            int missing = 0;
            int spriteMissing = 0;

            for (int t = 0; t < kDetailTypeCount; t++)
            {
                Mesh proto = null;
                if (_detailPrototypeMeshes != null && t < _detailPrototypeMeshes.Length)
                    proto = _detailPrototypeMeshes[t];

                if (proto == null)
                    proto = LoadDetailPrototypeMesh(kDetailMeshNames[t]);

                if (proto == null)
                {
                    missing++;
                    proto = null;
                }

                Rect cell = GetSpriteCell(t);
                spriteMissing += (_detailPrototypeUvRects == null
                                  || t >= _detailPrototypeUvRects.Length
                                  || _detailPrototypeUvRects[t].width <= 1e-5f) ? 1 : 0;

                float h = proto != null ? Mathf.Max(0.01f, proto.bounds.size.y) : 1f;
                float aspect = cell.height > 1e-5f ? cell.width / cell.height : 1f;
                float w = Mathf.Max(0.01f, h * aspect);

                _typeLodMeshes[t, 1] = BuildScaledBillboard(_billboardMedium, w, h, cell);
                _typeLodMeshes[t, 2] = BuildScaledBillboard(_billboardLow, w, h, cell);

                if (_typeLodMeshes[t, 1] == null)
                    _typeLodMeshes[t, 1] = BuildFallbackCrossMesh(w, h, cell, planes: 3);
                if (_typeLodMeshes[t, 2] == null)
                    _typeLodMeshes[t, 2] = BuildFallbackCrossMesh(w, h, cell, planes: 2);

                _typeLodMeshes[t, 0] = proto != null ? proto : _typeLodMeshes[t, 1];

                for (int lod = 0; lod < kLodCount; lod++)
                {
                    _typeLodMeshIDs[t, lod] = _brg.RegisterMesh(_typeLodMeshes[t, lod]);
                    registered++;
                }
            }

            for (int lod = 0; lod < kLodCount; lod++)
            {
                var m = _typeLodMeshes[0, lod];
                _lodTriangleCounts[lod] = m != null ? m.triangles.Length / 3 : 0;
            }

            Debug.Log($"[GrassBRG] 注册 {registered} 个 mesh（{kDetailTypeCount} 类型 × {kLodCount} LOD）" +
                      (missing > 0 ? $"，{missing} 个原型缺失已用兜底 mesh" : "") +
                      (spriteMissing > 0 ? $"，{spriteMissing} 个类型无精灵格位（按整张图集采样，花色可能错）" : "") +
                      $"；LOD0 三角形 = {string.Join("/", _lodTriangleCounts)}");
        }

        private static Mesh LoadDetailPrototypeMesh(string meshName)
        {
            if (_meshCache == null)
            {
                _meshCache = new Dictionary<string, Mesh>();
                foreach (var m in Resources.FindObjectsOfTypeAll<Mesh>())
                {
                    if (m == null || string.IsNullOrEmpty(m.name)) continue;
                    if (!_meshCache.ContainsKey(m.name))
                        _meshCache[m.name] = m;
                }
            }

            _meshCache.TryGetValue(meshName, out var mesh);
            return mesh;
        }

        private static Dictionary<string, Mesh> _meshCache;

        private Rect GetSpriteCell(int t)
        {
            if (_detailPrototypeUvRects != null && t < _detailPrototypeUvRects.Length)
            {
                var r = _detailPrototypeUvRects[t];
                if (r.width > 1e-5f && r.height > 1e-5f)
                    return r;
            }
            return new Rect(0f, 0f, 1f, 1f);
        }

        private static Mesh BuildFallbackCrossMesh(float w, float h, Rect cell, int planes)
        {
            planes = Mathf.Max(2, planes);
            var vertices = new Vector3[planes * 4];
            var normals = new Vector3[planes * 4];
            var uvs = new Vector2[planes * 4];
            var triangles = new int[planes * 6];
            float hw = w * 0.5f;

            for (int p = 0; p < planes; p++)
            {
                float ang = p * Mathf.PI / planes;
                var dir = new Vector3(Mathf.Cos(ang), 0f, Mathf.Sin(ang));
                var nrm = new Vector3(-dir.z, 0f, dir.x);
                int v = p * 4;
                vertices[v + 0] = -dir * hw;
                vertices[v + 1] = dir * hw;
                vertices[v + 2] = dir * hw + Vector3.up * h;
                vertices[v + 3] = -dir * hw + Vector3.up * h;
                for (int k = 0; k < 4; k++) normals[v + k] = nrm;

                uvs[v + 0] = new Vector2(cell.xMin, cell.yMin);
                uvs[v + 1] = new Vector2(cell.xMax, cell.yMin);
                uvs[v + 2] = new Vector2(cell.xMax, cell.yMax);
                uvs[v + 3] = new Vector2(cell.xMin, cell.yMax);

                int ti = p * 6;
                triangles[ti + 0] = v + 0; triangles[ti + 1] = v + 2; triangles[ti + 2] = v + 1;
                triangles[ti + 3] = v + 0; triangles[ti + 4] = v + 3; triangles[ti + 5] = v + 2;
            }

            var mesh = new Mesh { name = $"GrassBRG_Fallback{planes}_{w:F2}x{h:F2}" };
            mesh.SetVertices(vertices);
            mesh.SetNormals(normals);
            mesh.SetUVs(0, uvs);
            mesh.SetTriangles(triangles, 0);
            float r = Mathf.Max(hw, 0.01f);
            mesh.bounds = new Bounds(new Vector3(0f, h * 0.5f, 0f),
                new Vector3(r * 2f, h, r * 2f));
            mesh.UploadMeshData(true);
            return mesh;
        }

        private static Mesh BuildScaledBillboard(Mesh src, float w, float h, Rect cell)
        {
            if (src == null)
                return null;

            var mesh = UnityEngine.Object.Instantiate(src);
            mesh.name = $"GrassBRG_BB_{src.name}_{w:F2}x{h:F2}";
            mesh.hideFlags = HideFlags.DontSave;

            var verts = mesh.vertices;
            var bounds = src.bounds;
            float srcW = Mathf.Max(1e-4f, bounds.size.x);
            float srcH = Mathf.Max(1e-4f, bounds.size.y);
            float kx = w / srcW;
            float ky = h / srcH;
            float baseY = bounds.min.y;

            for (int i = 0; i < verts.Length; i++)
            {
                var p = verts[i];
                verts[i] = new Vector3(p.x * kx, (p.y - baseY) * ky, p.z * kx);
            }
            mesh.vertices = verts;

            var uvs = mesh.uv;
            if (uvs != null && uvs.Length > 0)
            {
                float uMin = cell.xMin, uSpan = cell.width;
                float vMin = cell.yMin, vSpan = cell.height;
                for (int i = 0; i < uvs.Length; i++)
                    uvs[i] = new Vector2(uMin + uvs[i].x * uSpan, vMin + uvs[i].y * vSpan);
                mesh.uv = uvs;
            }

            mesh.RecalculateBounds();
            mesh.UploadMeshData(true);
            return mesh;
        }


        private unsafe JobHandle OnPerformCulling(
            BatchRendererGroup rendererGroup,
            BatchCullingContext cullingContext,
            BatchCullingOutput cullingOutput,
            IntPtr userContext)
        {
            var output = (BatchCullingOutputDrawCommands*)
                NativeArrayUnsafeUtility.GetUnsafePtr(cullingOutput.drawCommands);

            output->drawCommandPickingInstanceIDs = null;
            output->instanceSortingPositions = null;
            output->instanceSortingPositionFloatCount = 0;

            if (!_ready || cullingContext.viewType != BatchCullingViewType.Camera)
            {
                output->visibleInstanceCount = 0;
                output->visibleInstances = null;
                output->drawCommandCount = 0;
                output->drawCommands = null;
                output->drawRangeCount = 0;
                output->drawRanges = null;
                return default;
            }

            if (_batches == null || _batches.Count == 0 || !_positions.IsCreated || _positions.Length == 0)
            {
                output->visibleInstanceCount = 0;
                output->visibleInstances = null;
                output->drawCommandCount = 0;
                output->drawCommands = null;
                output->drawRangeCount = 0;
                output->drawRanges = null;
                return default;
            }

            if (!_blocksNative.IsCreated || _blocksNative.Length == 0)
            {
                Debug.LogError($"[GrassBRG] 剔除块表未就绪：positions={_positions.Length}" +
                               $" blocks={(_blocksNative.IsCreated ? _blocksNative.Length : -1)}" +
                               $" batches={_batches.Count}");
                output->visibleInstanceCount = 0;
                output->visibleInstances = null;
                output->drawCommandCount = 0;
                output->drawCommands = null;
                output->drawRangeCount = 0;
                output->drawRanges = null;
                return default;
            }

            int capacity = _positions.Length;
            int batchCount = _batches?.Count ?? 0;
            int commandCount = batchCount * kLodCount;

            var visible = (int*)UnsafeUtility.Malloc(
                sizeof(int) * capacity, UnsafeUtility.AlignOf<int>(), Allocator.TempJob);
            var commands = (BatchDrawCommand*)UnsafeUtility.Malloc(
                sizeof(BatchDrawCommand) * commandCount, UnsafeUtility.AlignOf<BatchDrawCommand>(), Allocator.TempJob);
            var ranges = (BatchDrawRange*)UnsafeUtility.Malloc(
                sizeof(BatchDrawRange) * batchCount, UnsafeUtility.AlignOf<BatchDrawRange>(), Allocator.TempJob);

            output->visibleInstances = visible;
            output->drawCommands = commands;
            output->drawRanges = ranges;

            for (int b = 0; b < batchCount; b++)
            {
                var slice = _batches[b];
                int batchType = _batchType[b];
                BatchMaterialID typeMat = _typeMaterialIDs[batchType];
                for (int lod = 0; lod < kLodCount; lod++)
                {
                    commands[b * kLodCount + lod] = new BatchDrawCommand
                    {
                        batchID = slice.BatchID,
                        materialID = typeMat != BatchMaterialID.Null ? typeMat : _materialID,
                        meshID = _typeLodMeshIDs[batchType, lod],
                        submeshIndex = 0,
                        splitVisibilityMask = 0x01,
                        flags = BatchDrawCommandFlags.None,
                        sortingPosition = 0,
                        visibleOffset = 0,
                        visibleCount = 0,
                    };
                }

                ranges[b] = new BatchDrawRange
                {
                    drawCommandsBegin = (uint)(b * kLodCount),
                    drawCommandsCount = (uint)kLodCount,
                    filterSettings = new BatchFilterSettings
                    {
                        layer = 0,
                        renderingLayerMask = 1u,
                        shadowCastingMode = ShadowCastingMode.Off,
                        receiveShadows = false,
                        motionMode = MotionVectorGenerationMode.ForceNoMotion,
                        staticShadowCaster = false,
                        allDepthSorted = false,
                    },
                };
            }

            int planeCount = cullingContext.cullingPlanes.Length;
            var planes = new NativeArray<float4>(planeCount, Allocator.TempJob,
                NativeArrayOptions.UninitializedMemory);
            for (int i = 0; i < planeCount; i++)
            {
                Plane plane = cullingContext.cullingPlanes[i];
                Vector3 n = plane.normal;
                planes[i] = new float4(n.x, n.y, n.z, plane.distance);
            }

            Vector3 camPos = cullingContext.lodParameters.cameraPosition;
            float fov = cullingContext.lodParameters.fieldOfView;
            int pixelHeight = cullingContext.lodParameters.cameraPixelHeight;

            if (camPos == Vector3.zero || fov <= 0f || pixelHeight <= 0)
            {
                var cam = Camera.main;
                if (cam != null)
                {
                    camPos = cam.transform.position;
                    fov = cam.fieldOfView;
                    pixelHeight = cam.pixelHeight;
                }
                else
                {
                    camPos = Vector3.zero;
                    fov = 60f;
                    pixelHeight = 1080;
                }
            }

            var job = new CullJob
            {
                Positions = _positions,
                Planes = planes,
                Radius = _cullRadius,
                VisibleInstances = visible,
                Output = output,
                VisibleCountDebug = (int*)NativeArrayUnsafeUtility.GetUnsafePtr(_visibleCount),
                LodCountsDebug = (int*)NativeArrayUnsafeUtility.GetUnsafePtr(_lodCounts),
                CameraPosition = new float3(camPos.x, camPos.y, camPos.z),
                LodPixelScale = EffectiveNominalHeight * _scaleMultiplier * pixelHeight / (2f * Mathf.Tan(fov * 0.5f * Mathf.Deg2Rad)),
                Lod0PixelHeight = _enableLod ? _lod0PixelHeight : 0f,
                Lod1PixelHeight = _enableLod ? _lod1PixelHeight : 0f,
                CullPixelHeight = _enableLod ? _cullPixelHeight : 0f,
                DebugShot = Mathf.Max(0, _debugCullShots),
                Blocks = _blocksNative,
                BatchCount = batchCount,
                MaxVisibleDistance = _enableLod && _cullPixelHeight > 0f
                    ? EffectiveNominalHeight * _scaleMultiplier * pixelHeight
                      / (2f * Mathf.Tan(fov * 0.5f * Mathf.Deg2Rad)) / _cullPixelHeight
                    : 0f,
            };

            _lastCullHandle = job.Schedule(_lastCullHandle);
            return _lastCullHandle;
        }

        [BurstCompile]
        private unsafe struct CullJob : IJob
        {
            [ReadOnly] public NativeArray<float3> Positions;

            [DeallocateOnJobCompletion]
            public NativeArray<float4> Planes;

            public float Radius;

            [NativeDisableUnsafePtrRestriction] public int* VisibleInstances;
            [NativeDisableUnsafePtrRestriction] public BatchCullingOutputDrawCommands* Output;
            [NativeDisableUnsafePtrRestriction] public int* VisibleCountDebug;
            [NativeDisableUnsafePtrRestriction] public int* LodCountsDebug;

            public float3 CameraPosition;

            public float LodPixelScale;

            public float Lod0PixelHeight;
            public float Lod1PixelHeight;
            public float CullPixelHeight;

            public int DebugShot;

            public float MaxVisibleDistance;

            public const int BatchMaxInstances = 16 * 1024 * 1024 / 32;

            public int BatchCount;

            [ReadOnly] public NativeArray<GrassBlock> Blocks;

            public void Execute()
            {
                bool lodEnabled = Lod0PixelHeight > 0f && LodPixelScale > 0f;
                int batchCount = BatchCount;
                int cmdCount = batchCount * 3;

                var counts = (int*)UnsafeUtility.Malloc(
                    sizeof(int) * cmdCount, UnsafeUtility.AlignOf<int>(), Allocator.TempJob);
                var writePtr = (int*)UnsafeUtility.Malloc(
                    sizeof(int) * cmdCount, UnsafeUtility.AlignOf<int>(), Allocator.TempJob);

                for (int i = 0; i < cmdCount; i++)
                    counts[i] = 0;

                int blockCount = Blocks.Length;
                int visibleBlocks = 0;
                int scannedInstances = 0;
                for (int bi = 0; bi < blockCount; bi++)
                {
                    var block = Blocks[bi];
                    if (!BlockVisible(block))
                        continue;

                    visibleBlocks++;
                    scannedInstances += block.Count;

                    int end = block.Start + block.Count;
                    for (int i = block.Start; i < end; i++)
                    {
                        float3 pos = Positions[i];
                        if (!InFrustum(pos))
                            continue;

                        int lod = lodEnabled ? ResolveLod(pos) : 0;
                        if (lod < 0)
                            continue;

                        int batch = block.BatchIndex;
                        counts[batch * 3 + lod]++;
                    }
                }

                int dst = 0;
                for (int c = 0; c < cmdCount; c++)
                {
                    int n = counts[c];
                    writePtr[c] = dst;
                    Output->drawCommands[c].visibleOffset = (uint)dst;
                    Output->drawCommands[c].visibleCount = (uint)n;
                    dst += n;
                }
                Output->visibleInstanceCount = dst;

                for (int bi = 0; bi < blockCount; bi++)
                {
                    var block = Blocks[bi];
                    if (!BlockVisible(block))
                        continue;

                    int end = block.Start + block.Count;
                    for (int i = block.Start; i < end; i++)
                    {
                        float3 pos = Positions[i];
                        if (!InFrustum(pos))
                            continue;

                        int lod = lodEnabled ? ResolveLod(pos) : 0;
                        if (lod < 0)
                            continue;

                        int batch = block.BatchIndex;
                        int slot = batch * 3 + lod;

                        int local = i - block.ChunkBase;
                        VisibleInstances[writePtr[slot]++] = local - block.BatchStart;
                    }
                }

                UnsafeUtility.Free(counts, Allocator.TempJob);
                UnsafeUtility.Free(writePtr, Allocator.TempJob);

                if (!lodEnabled)
                {
                    for (int b = 0; b < batchCount; b++)
                    {
                        for (int lod = 1; lod < 3; lod++)
                        {
                            Output->drawCommands[b * 3 + lod].visibleOffset = 0;
                            Output->drawCommands[b * 3 + lod].visibleCount = 0;
                        }
                    }
                }

                if (LodCountsDebug != null)
                {
                    int c0 = 0, c1 = 0, c2 = 0;
                    for (int b = 0; b < batchCount; b++)
                    {
                        c0 += (int)Output->drawCommands[b * 3 + 0].visibleCount;
                        c1 += (int)Output->drawCommands[b * 3 + 1].visibleCount;
                        c2 += (int)Output->drawCommands[b * 3 + 2].visibleCount;
                    }
                    LodCountsDebug[0] = c0;
                    LodCountsDebug[1] = c1;
                    LodCountsDebug[2] = c2;
                }

                Output->drawCommandCount = dst > 0 ? cmdCount : 0;
                Output->drawRangeCount = dst > 0 ? batchCount : 0;
                if (VisibleCountDebug != null)
                    *VisibleCountDebug = dst;

                if (DebugShot > 0)
                {
                    DebugShot--;
                    LogCullDebug(visibleBlocks, blockCount, scannedInstances,
                        Positions.Length, dst, Output, cmdCount);
                }

            }

            [BurstDiscard]
            private static unsafe void LogCullDebug(
                int visibleBlocks, int blockCount, int scannedInstances,
                int totalInstances, int visibleInstances,
                BatchCullingOutputDrawCommands* output, int cmdCount)
            {
                float scannedPct = scannedInstances * 100f / math.max(1, totalInstances);
                Debug.Log($"[CullJob] 块 {visibleBlocks}/{blockCount} 可见，"
                          + $"扫描 {scannedInstances}/{totalInstances} 实例 "
                          + $"({scannedPct:F1}%)，可见 {visibleInstances}");

                var sb = new System.Text.StringBuilder();
                for (int c = 0; c < cmdCount; c++)
                {
                    var cmd = output->drawCommands[c];
                    if (cmd.visibleCount > 0)
                        sb.Append($" cmd[{c}](b{c / 3},L{c % 3})={cmd.visibleCount}");
                }
                Debug.Log($"[CullJob] total={visibleInstances} cmds>{sb}");
            }

            private bool BlockVisible(GrassBlock block)
            {
                if (MaxVisibleDistance > 0f)
                {
                    float3 gap = math.abs(CameraPosition - block.Center) - block.HalfExtent;
                    gap = math.max(gap, 0f);
                    if (math.length(gap) > MaxVisibleDistance)
                        return false;
                }

                int planeCount = Planes.Length;
                for (int k = 0; k < planeCount; k++)
                {
                    float4 pl = Planes[k];
                    float dd = math.dot(pl.xyz, block.Center) + pl.w;
                    float projR = math.abs(pl.x) * block.HalfExtent.x
                                + math.abs(pl.y) * block.HalfExtent.y
                                + math.abs(pl.z) * block.HalfExtent.z;
                    if (dd < -projR)
                        return false;
                }
                return true;
            }

            private bool InFrustum(float3 pos)
            {
                int planeCount = Planes.Length;
                for (int k = 0; k < planeCount; k++)
                {
                    float4 pl = Planes[k];
                    if (math.dot(pl.xyz, pos) + pl.w < -Radius)
                        return false;
                }
                return true;
            }

            private int ResolveLod(float3 pos)
            {
                float dist = math.distance(pos, CameraPosition);
                float pixelH = dist > 1e-4f ? LodPixelScale / dist : float.MaxValue;

                if (pixelH >= Lod0PixelHeight) return 0;
                if (pixelH >= Lod1PixelHeight) return 1;
                if (pixelH >= CullPixelHeight) return 2;
                return -1;
            }
        }
    }
}
