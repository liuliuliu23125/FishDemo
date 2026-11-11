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
    /// <summary>
    /// M1：最小 BRG（BatchRendererGroup）草链路。
    ///
    /// 目标只有一件事——证明 URP 14.0.12 + BRG 的 DOTS_INSTANCING 链路能跑通：
    /// 1000 个硬编码实例 → 一个 batch → 一次 draw command → 屏幕上真的画出东西。
    /// 数据管线（GrassBaker / chunk 流式 / 光照阴影风摆）留给 M2、M3，
    /// 见 Docs/grass_brg_plan.md §6。
    ///
    /// ── 关键约束（都是核实过的代码事实，不是推测）──
    /// 1. AddBatch 只接受「raw buffer」：BatchRendererGroup.BufferTarget 在
    ///    D3D11/Vulkan 上返回 RawBuffer（EntitiesGraphicsUtils.cs:87 也是这么判断的）。
    ///    所以 GraphicsBuffer 必须用 Target.Raw，步长 4，元素数 = 字节数 / 4。
    ///    用 Structured 会在 AddBatch 时直接失败（方案文档 §3.1 写的 Structured 是错的）。
    /// 2. 每实例数据是 SoA：一个属性一整段连续内存，metadata 的 Value 高位 0x80000000
    ///    表示「这段数据按实例索引寻址」（UnityDOTSInstancing.hlsl:225,312-321）。
    /// 3. unity_ObjectToWorld 在 DOTS 下是 float3x4 = 48B，不是 64B 的 float4x4
    ///    （UniversalDOTSInstancing.hlsl:12）。布局见 PackMatrix3x4。
    /// 4. OnPerformCulling 里给引擎的输出数组必须用 UnsafeUtility.Malloc(..., Allocator.TempJob)
    ///    分配，由引擎在渲染结束后释放（EntitiesGraphicsSystem/DrawCommandGeneration.cs:894-900）。
    ///    用 NativeArray 分配再 Dispose 或不管都会泄漏/报警。
    ///
    /// ── 批处理路径 ──
    /// BRG（GPU 驱动剔除 + 单次提交）。不走 SRP Batcher、不走 GPU Instancing。
    /// 本类属于 Render Data 层，Gameplay 不直接碰 Graphics.*（rendering_rules §1）。
    /// </summary>
    /// <remarks>
    /// [ExecuteAlways] 是必须的：不用它，编辑模式下 OnEnable 不会触发，BRG 根本不创建，
    /// 打开场景看到的是「一片空」，会误判成 BRG 不工作（实测踩过，LastVisibleCount = -1）。
    /// 有了它，打开场景/切回编辑器就能直接看到草，无需进 Play Mode。
    /// </remarks>
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

        [Tooltip("草在世界里的标称高度（米）。用于把屏幕像素反算成距离。")]
        [SerializeField] private float _nominalHeight = 1f;

        [Tooltip("关掉 LOD 时只用 LOD0，用于 A/B 对比验证。")]
        [SerializeField] private bool _enableLod = true;

        /// <summary>诊断：打印前 N 次剔除的内部状态（0 = 关闭）。排查用，正常应为 0。</summary>
        [SerializeField] private int _debugCullShots = 0;

        /// <summary>M2 目标格式：32 字节（两个 float4 的语义数据）。</summary>
        private const int kBytesPerInstance = 32;

        /// <summary>
        /// 单个 batch 的字节上限。与 EntitiesGraphicsSystem 的
        /// kMaxBytesPerBatchRawBuffer（16MB）保持一致——超过它 shader 读到垃圾数据
        /// 且无报错（M2 实测 65.6MB batch 全黑）。
        /// </summary>
        private const int kMaxBatchBytes = 16 * 1024 * 1024;

        /// <summary>
        /// 一个 batch 切片的运行时表示：独立 buffer + 独立 batchID + 全局实例区间 [Start, Start+Count)。
        /// 剔除输出时每个切片 × 每层 LOD = 一条 draw command。
        /// </summary>
        private struct BatchSlice
        {
            public GraphicsBuffer Buffer;
            public BatchID BatchID;
            public int Start;
            public int Count;
        }

        private List<BatchSlice> _batches;

        /// <summary>
        /// 每个 batch 对应的 detail 类型（与 _batches 同索引）。
        /// batch 按类型分段，所以同一 batch 内实例类型一致，共用一条 mesh。
        /// </summary>
        private List<int> _batchType;

        /// <summary>LOD 层数。0=近（原型网格）、1=中（共享低模）、2=远（十字面片）。</summary>
        private const int kLodCount = 3;

        /// <summary>
        /// detail 原型类型数（0..6 共 7 种）。
        /// 与 TerrainData.detailPrototypes 的层索引一一对应，
        /// 烘焙时 GrassBaker 把层号写进 GrassInstance.Type。
        /// 每种类型一个 mesh，所以类型数直接决定 draw command 的上限。
        /// </summary>
        private const int kDetailTypeCount = 7;

        /// <summary>
        /// 各类型的原型 mesh 资源名（与 Gaia 资产文件名一致）。
        /// 顺序必须与 detailPrototypes 的层序一致，否则花会长成草的样子。
        /// 索引 = GrassInstance.Type。
        /// </summary>
        private static readonly string[] kDetailMeshNames =
        {
            "PW_WildGrass_General_Lod1",        // 0 主草
            "PW_LawnGrass_DeadPatch",           // 1 枯草块
            "PW_WildGrass_Understory_Foliage",  // 2 林下叶
            "PW_WildGrass_Understory_Sticks",   // 3 林下枯枝
            "PW_LawnGrass_CloverFlower",        // 4 三叶草花
            "PW_LawnGrass_Daisy",               // 5 雏菊
            "PW_LawnGrass_Dandelions",          // 6 蒲公英
        };

        /// <summary>
        /// 实例数据源。M2 正常路径由 GrassChunkData 注入（烘焙产物）；
        /// 为空时退化为 M1 式合成方阵。两种来源最终都转成 _instances。
        /// </summary>
        private GrassInstance[] _instances;

        /// <summary>烘焙数据（可为空）。验证脚本/测试可直接注入。</summary>
        private GrassChunkData _chunkData;

        /// <summary>
        /// 多个 chunk 的烘焙数据。非空时优先于 _chunkData，实例按顺序拼接成一个数组。
        /// M3 接 SECTR 流式后，这里会变成「已加载 chunk 的集合」，支持增删。
        /// </summary>
        private List<GrassChunkData> _chunkDataList;

        /// <summary>
        /// 烘焙数据目录（相对项目根，如 Assets/Scenes/World/GrassData）。
        ///
        /// 为什么需要这个字段：GrassChunkData 数组无法序列化（它没有 [Serializable]，
        /// 且 12.65M 实例 × 16B 也不可能进场景文件）。早先 GrassSceneSetupM2 只调
        /// SetChunkDataList 再保存场景，看着"成功"但数据根本没落盘——重新打开工程
        /// 就退回 M1 硬编码方阵（实测验证过）。
        /// 存路径、运行时读盘，配置才能持久化。
        ///
        /// 只在编辑器/开发期有效（打包后 Assets/ 不存在）。正式版应改为经 YooAsset
        /// 加载，见 Runtime/AGENTS.md「禁止 Resources.Load」。
        /// </summary>
        [Tooltip("烘焙数据目录（相对项目根）。非空时启动自动加载该目录下所有 .bytes。")]
        [SerializeField] private string _grassDataDir = string.Empty;

        /// <summary>
        /// 7 种 detail 原型的近距 mesh（LOD0）。索引 = GrassInstance.Type。
        ///
        /// 为什么要把引用序列化到组件上、而不是运行时按名字找：
        /// 这些 FBX 只是被 TerrainData 以子资源形式引用，运行时
        /// Resources.FindObjectsOfTypeAll&lt;Mesh&gt;() 找不到它们（实测 0 命中，
        /// 全部退回程序化兜底草簇，LOD0 三角形恒为 24 而不是原型面数）。
        /// 且 FBX 内部的 mesh 名带后缀（如 PW_WildGrass_General_Lod1_E），
        /// 不是文件名，按名字匹配也不可靠。
        /// 由编辑器工具（DetailBrgSetup）在编辑期用 AssetDatabase 加载后写入，
        /// 运行时直接读字段——这是唯一稳定且不引入 UnityEditor 依赖的做法。
        /// </summary>
        [Tooltip("7 种 detail 原型的 LOD0 mesh（索引 = detail 层）。由 DetailBrgSetup 写入。")]
        [SerializeField] private Mesh[] _detailPrototypeMeshes = new Mesh[kDetailTypeCount];

        /// <summary>是否已尝试过从 _grassDataDir 自动加载（避免每帧重试）。</summary>
        private bool _triedAutoLoad;

        private BatchRendererGroup _brg;
        private BatchRendererGroup.OnPerformCulling _performCulling;
        private BatchMaterialID _materialID;

        /// <summary>
        /// 各类型 × 各 LOD 的 mesh 与注册 ID。索引 [type, lod]。
        /// LOD0 是各类型自己的原型 mesh，LOD1/2 是共享的草簇/面片。
        /// </summary>
        private readonly Mesh[,] _typeLodMeshes = new Mesh[kDetailTypeCount, kLodCount];
        private readonly BatchMeshID[,] _typeLodMeshIDs = new BatchMeshID[kDetailTypeCount, kLodCount];

        /// <summary>三层各自的三角形数，仅用于验证输出（不参与渲染决策）。</summary>
        private readonly int[] _lodTriangleCounts = new int[kLodCount];

        private Material _runtimeMaterial;

        /// <summary>CPU 侧实例位置，剔除 Job 用（GPU buffer 里只有矩阵）。</summary>
        private NativeArray<float3> _positions;

        /// <summary>调试用：最近一次剔除后的可见实例数。Job 写、主线程读，仅用于 HUD。</summary>
        private NativeArray<int> _visibleCount;

        /// <summary>调试用：最近一次剔除后各 LOD 层的可见实例数。索引即 LOD 层级。</summary>
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

        /// <summary>
        /// 建立 BRG 与 batch。幂等：已初始化则直接返回。
        ///
        /// 为什么公开：M1 的编辑期渲染验证需要在不进 Play Mode 的情况下驱动一次，
        /// 而普通 MonoBehaviour 在编辑模式下不会触发 OnEnable（不是 [ExecuteAlways]）。
        /// 运行期由 OnEnable 自动调用。
        /// </summary>
        public void Initialize()
        {
            if (_ready)
                return;

            _performCulling = OnPerformCulling;
            _brg = new BatchRendererGroup(_performCulling, IntPtr.Zero);
            // 只订阅相机视图：M1 不投阴影，也不处理 Scene 拾取。
            _brg.SetEnabledViewTypes(new[] { BatchCullingViewType.Camera });

            var mat = ResolveMaterial();
            if (mat == null)
            {
                Debug.LogError("[GrassBRG] 找不到材质/Shader，BRG 未启动。", this);
                return;
            }

            BuildLodMeshes();

            _materialID = _brg.RegisterMaterial(mat);

            BuildInstances();

            // ── 按 (类型段 × 16MB 容量) 切分 batch ──
            //
            // 两个约束同时满足：
            //   1. 一个 batch 只能绑定一个 mesh → 同一 batch 内实例必须同类型
            //      （SortInstancesIntoBlocks 已保证同类型实例连续）。
            //   2. BRG 单个 batch 的 raw buffer 上限 16MB
            //      （EntitiesGraphicsSystem.cs:1156 kMaxBytesPerBatchRawBuffer）。
            //      32B/实例 → 最多 524,288 实例/批。
            //      超限实测：shader 读到垃圾数据 → 画面全黑且无报错。
            //
            // 所以先按类型分段，段内再按 16MB 切。大类型（主草 400 万）会切成多批，
            // 小类型（枯枝）可能只有 0 实例、不建 batch。
            int maxPerBatch = kMaxBatchBytes / kBytesPerInstance;
            var typeRanges = ComputeTypeRanges();      // [t] = (start, count)
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

            BuildBatchLookup(slices);

            // 引擎会用它做粗剔除；不设的话整个 BRG 可能被直接跳过。
            _brg.SetGlobalBounds(ComputeWorldBounds());

            _ready = true;
        }

        /// <summary>
        /// 生成「实例号 → batch 号」与「batch 起点」两张表。
        ///
        /// 为什么必须要表：batch 现在按「类型段 × 16MB」切分，
        /// 各类型段起点不同、段内还可能再切，不存在一条除法公式
        /// （旧代码 i / BatchMaxInstances 只对均匀切分成立）。
        /// 剔除 Job 的内层循环是每帧对几十万实例跑两遍，必须 O(1) 且无分支，
        /// 所以提前把映射摊平成数组（1265 万条 int ≈ 50MB，一次性开销）。
        ///
        /// 用 MemCpy：逐元素赋值在 1265 万规模下会多花几十毫秒，
        /// 且 BatchSlice 已按实例顺序连续排列，直接整段填充即可。
        /// </summary>
        private void BuildBatchLookup(List<(int type, int start, int count)> slices)
        {
            if (_batchOfInstance.IsCreated) _batchOfInstance.Dispose();
            if (_batchStartNative.IsCreated) _batchStartNative.Dispose();

            int n = _instanceCount;

            // 用 ClearMemory 而不是 UninitializedMemory：
            // 若 slices 覆盖的总数少于 n（类型统计与实际数组不一致时会发生），
            // 未填充的表项会是随机值，Job 里 counts[batch*3+lod] 会写到堆外，
            // 破坏内存且不报错——表现为整片草消失。清零后至少越界是 0 号 batch。
            _batchOfInstance = new NativeArray<int>(Mathf.Max(1, n), Allocator.Persistent,
                NativeArrayOptions.ClearMemory);
            _batchStartNative = new NativeArray<int>(Mathf.Max(1, slices.Count), Allocator.Persistent);

            for (int b = 0; b < slices.Count; b++)
            {
                var (_, start, count) = slices[b];
                _batchStartNative[b] = start;

                // 整段填充：这一段实例全属 batch b。
                var seg = _batchOfInstance.GetSubArray(start, count);
                for (int k = 0; k < seg.Length; k++)
                    seg[k] = b;
            }

            // 覆盖性校验：slices 必须恰好覆盖 [0, n)。
            // 不满足时说明类型统计与实际实例数组不一致（排序键算错/类型越界），
            // 后果是部分实例的 batch 号是垃圾值 → 剔除时写坏内存 → 整片草消失。
            long covered = 0;
            int expectedNext = 0;
            bool contiguous = true;
            foreach (var (_, start, count) in slices)
            {
                if (start != expectedNext) contiguous = false;
                expectedNext = start + count;
                covered += count;
            }
            if (!contiguous || covered != n)
            {
                Debug.LogError($"[GrassBRG] batch 分段未覆盖全部实例：" +
                               $"covered={covered} n={n} contiguous={contiguous} " +
                               $"（草可能整体消失，检查 SortInstancesIntoBlocks 的类型排序键）");
            }
        }

        /// <summary>释放 BRG 与所有 GPU/原生资源。幂等。</summary>
        public void Shutdown()
        {
            if (!_ready && _brg == null)
                return;

            _ready = false;

            // 先让在飞的剔除 Job 结束，再释放它引用的内存。
            _lastCullHandle.Complete();

            if (_brg != null)
            {
                _brg.Dispose();
                _brg = null;
            }

            if (_batches != null)
            {
                foreach (var b in _batches)
                    b.Buffer.Release();
                _batches = null;
            }

            // 只销毁自己创建的程序化 mesh（草簇/面片）。
            // LOD0 是各类型从资产加载的原型 mesh 引用，不能 Destroy——
            // 那是别人的资产，销毁会破坏其它使用方。
            // LOD1/LOD2 被多个类型共享同一个实例，用 HashSet 去重避免重复销毁。
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

            if (_batchOfInstance.IsCreated) _batchOfInstance.Dispose();
            if (_batchStartNative.IsCreated) _batchStartNative.Dispose();
            _batchType?.Clear();

            if (_runtimeMaterial != null)
            {
                DestroyRuntimeObject(_runtimeMaterial);
                _runtimeMaterial = null;
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

        /// <summary>
        /// 编辑期验证会直接调 Shutdown，此时 Destroy 不被允许（只能在 Play Mode 用），
        /// 必须切 DestroyImmediate，否则资源泄漏且控制台报错。
        /// 显式写 UnityEngine.Object：本文件同时 using System，裸 Object 会有歧义。
        /// 方法名不能叫 DestroyObject：会遮蔽（CS0108）UnityEngine.Object 上同名的
        /// 已废弃静态方法，编译期一直报警告。
        /// </summary>
        private static void DestroyRuntimeObject(UnityEngine.Object obj)
        {
            if (Application.isPlaying)
                Destroy(obj);
            else
                DestroyImmediate(obj);
        }

        /// <summary>编辑器验证用：把材质直接注入，绕开 Shader.Find 在批处理下的不确定性。</summary>
        public void SetMaterialForValidation(Material mat)
        {
            _material = mat;
        }

        /// <summary>
        /// 注入烘焙数据（M2 正常路径）。设置后总是重新初始化。
        ///
        /// 为什么不能只判断 _ready：调用序列可能是
        /// ConfigureLodForValidation()（内部 Shutdown，_ready 变 false）→ SetChunkData()，
        /// 此时 _ready 已为 false，若"只在已就绪时重建"就永远不会初始化（实测踩过）。
        /// </summary>
        public void SetChunkData(GrassChunkData data)
        {
            if (_ready)
                Shutdown();

            _chunkData = data;
            _chunkDataList = null;
            Initialize();
        }

        /// <summary>
        /// 注入多个 chunk 的烘焙数据（M3 流式的运行时形态）。
        /// 实例按列表顺序拼接；chunk 数量与 batch 划分无关，batch 只由 16MB 容量决定。
        /// </summary>
        public void SetChunkDataList(List<GrassChunkData> chunks)
        {
            if (_ready)
                Shutdown();

            _chunkData = null;
            _chunkDataList = chunks;
            Initialize();
        }

        /// <summary>
        /// 编辑器工具用：写入 7 种 detail 原型的 LOD0 mesh 并重建。
        /// 传 null 项表示该类型没有原型（会用兜底草簇）。
        /// </summary>
        public void SetDetailPrototypeMeshes(Mesh[] meshes)
        {
            _detailPrototypeMeshes = meshes;

            // 重建以应用新 mesh。Initialize 内部会先 Shutdown（若已就绪）。
            if (_ready) Shutdown();
            Initialize();
        }

        /// <summary>
        /// 设置烘焙数据目录并立即重载。用于把配置写进场景（可序列化、可持久化）。
        /// 传空字符串表示清除配置、退回 M1 合成方阵。
        /// </summary>
        public void SetGrassDataDir(string dir)
        {
            if (_ready)
                Shutdown();

            _grassDataDir = dir ?? string.Empty;
            _chunkData = null;
            _chunkDataList = null;
            _triedAutoLoad = false;   // 允许按新目录重新尝试
            Initialize();
        }

        /// <summary>验证用：取第 i 个实例的世界位置，用于确认数据源正确。</summary>
        public Vector3 GetInstancePosition(int i) =>
            _instances != null && i >= 0 && i < _instances.Length
                ? new Vector3(_instances[i].Position.x, _instances[i].Position.y, _instances[i].Position.z)
                : Vector3.zero;

        /// <summary>验证用：实例总数（来自数据源，不是序列化字段）。</summary>
        public int InstanceCount => _instances?.Length ?? 0;

        /// <summary>诊断用：让 CullJob 打印前 N 次执行的内部状态。排查完应置 0。</summary>
        public void SetDebugCullShots(int count) => _debugCullShots = count;

        /// <summary>
        /// 验证用：覆写 LOD 阈值。
        /// M1 链路验证用很小的离屏 RT（256px），1m 草在 56m 处换算只有 4 像素，
        /// 会被 8px 的剔除阈值全部剔掉——那是阈值在低分辨率下的正常行为，
        /// 不是链路故障。所以链路验证必须能关掉 LOD，只验「实例能不能画出来」。
        ///
        /// 会先 Shutdown：因为 [ExecuteAlways] 下 AddComponent 会立刻触发 OnEnable →
        /// Initialize()，此时用的还是默认字段值。不重建的话，这里的修改不会生效。
        /// </summary>
        public void ConfigureLodForValidation(bool enabled, float lod0Px, float lod1Px, float cullPx)
        {
            Shutdown();
            _enableLod = enabled;
            _lod0PixelHeight = lod0Px;
            _lod1PixelHeight = lod1Px;
            _cullPixelHeight = cullPx;
        }

        /// <summary>
        /// 编辑器验证用：改实例数与间距（需在 Initialize 前调用）。
        /// 同样先 Shutdown，原因见 ConfigureLodForValidation。
        /// </summary>
        public void ConfigureForValidation(int instanceCount, float spacing, float cullRadius)
        {
            Shutdown();
            _instanceCount = Mathf.Max(1, instanceCount);
            _spacing = spacing;
            _cullRadius = cullRadius;
        }

        /// <summary>
        /// 编辑器验证用：设置锚点并重新布点。验证脚本用它把草放到指定世界位置，
        /// 避免依赖场景里手摆的锚点。
        /// </summary>
        public void SetAnchor(Transform anchor, bool snapToGround)
        {
            _anchor = anchor;
            _snapToGround = snapToGround;
        }

        /// <summary>最近一次剔除的可见实例数；-1 表示还没跑过剔除。</summary>
        public int LastVisibleCount => _visibleCount.IsCreated ? _visibleCount[0] : -1;

        /// <summary>最近一次剔除后第 lod 层的可见实例数；无数据返回 -1。</summary>
        public int GetLastLodCount(int lod) =>
            _lodCounts.IsCreated && lod >= 0 && lod < kLodCount ? _lodCounts[lod] : -1;

        /// <summary>第 lod 层网格的三角形数；无数据返回 0。用于验证几何预算。</summary>
        public int GetLodTriangleCount(int lod) =>
            lod >= 0 && lod < kLodCount ? _lodTriangleCounts[lod] : 0;

        /// <summary>
        /// 最近一次剔除提交的三角形总数 = Σ(各层实例数 × 该层三角形数)。
        /// 这是直接对标 performance_budget.md 里「1.5M 三角形/帧」的那个数。
        /// </summary>
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

        /// <summary>三层 LOD mesh 的三角形数快照，验证用。</summary>
        public int[] GetLodTriangleCounts() => (int[])_lodTriangleCounts.Clone();

        /// <summary>
        /// 是否已完成初始化（BRG、batch、buffer 都就绪）。</summary>
        public bool IsReady => _ready;

        /// <summary>
        /// 布点是否拿到了真实地表高度。
        ///
        /// 为什么需要暴露这个：SECTR 在 Play Mode 里异步加载 chunk 场景，
        /// 而 OnEnable 可能先于 Terrain 出现就跑了。这时 SampleHeight 拿不到东西，
        /// 草会以锚点高度平铺（悬空或埋地），但 BRG 本身「正常工作」——
        /// 表现为"草在但位置不对"，很容易误判成矩阵布局错误。
        /// M2 接烘焙数据后此问题自然消失。
        /// </summary>
        public bool GroundResolved { get; private set; }

        private float _regroundTimer = 0.5f;

        /// <summary>
        /// 地表还没就绪时，周期性重试布点（直到拿到真实高度）。
        /// 仅在 Play Mode 且 GroundResolved=false 时生效，正常情况零开销。
        /// </summary>
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

        /// <summary>
        /// 重新按当前地表高度布点并重传 GPU buffer。
        /// 只在第一次成功拿到地面时执行一次，之后不再触发。
        /// </summary>
        private void TryReground()
        {
            if (FindObjectsOfType<Terrain>().Length == 0)
                return;

            // 重新贴地后：实例数据变了，必须重传所有 batch 的 buffer 并刷新包围盒。
            BuildInstances();
            foreach (var b in _batches)
                UploadInstanceData(b.Buffer, b.Start, b.Count);

            _brg.SetGlobalBounds(ComputeWorldBounds());
            Debug.Log($"[GrassBRG] 地表就绪，已重新贴地布点（{_instanceCount} 实例）");
        }

        /// <summary>
        /// 当前使用的材质（注入的或运行时创建的）。可能为 null。
        /// 验证脚本用它改写颜色，把 BRG 草与地形自带 detail 草区分开——
        /// 两者都是绿色，只靠颜色统计无法判断到底画的是谁。
        /// </summary>
        public Material CurrentMaterial => _material != null ? _material : _runtimeMaterial;

        private void OnGUI()
        {
            if (!_ready)
                return;
            GUI.Label(new Rect(12, 12, 420, 22),
                $"GrassBRG M1 | 实例 {_instanceCount} | 可见 {_lastVisibleCount}");
        }

        // ───────────────────────── 数据准备 ─────────────────────────

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

        /// <summary>
        /// M1 硬编码布点：以锚点（或本物体）为中心的方阵，带确定性 yaw 与缩放抖动。
        ///
        /// 地表吸附：M1 没有烘焙数据，如果直接把实例放在锚点的 y 上，
        /// 在起伏地形上会整片悬空或埋进地里，肉眼根本判断不出"BRG 到底画没画对"。
        /// 所以用 Terrain.SampleHeight 取真实地面高度（地形虽已 mesh 化，
        /// Terrain 组件与 TerrainData 仍保留，见 world_implementation §4.1）。
        /// 找不到 Terrain 时回落到 Physics.Raycast，再不行就保持锚点高度。
        /// M2 起改用烘焙时的 GetHeight，这段逻辑会被替换。
        /// </summary>
        /// <summary>
        /// 把 _instances 按 (类型, 空间网格) 重排，并生成块表。
        ///
        /// 为什么需要：1265 万实例每帧全量扫描实测 1244ms（2 遍扫描），不可用。
        /// 但视野半径只有约 117m（LOD2 外沿），一个 511m 的 chunk 里大部分实例在视野外。
        /// 按 32m 分块后，剔除时先用块包围盒整体判定，不可见的块整块跳过。
        ///
        /// ── 为什么分块键是 (类型, 空间格) 而不是只用空间格 ──
        /// BRG 的一条 draw command 绑定一个 meshID，而 7 种 detail 原型要用各自的 mesh。
        /// 所以「可见实例索引」必须能按类型切成连续段：
        ///   段内 = 同一类型（同一 mesh）→ 一条 command 画完
        ///   段间 = 不同类型 → 分别 command
        /// 若只按空间格排序，一个格里的 7 种类型会交错，只能拆成 7 条 command，
        /// 且每条都要扫描整格，失去块级剔除的意义。
        /// 加上类型维度后，每块既空间局部（可整块剔除）又单一类型（可单条 command）。
        ///
        /// 用计数排序（O(n)）而不是 Array.Sort（O(n log n)）：
        /// 1265 万实例时计数排序快一个量级。键空间约 9216 格 × 7 类型。
        ///
        /// 重排会改变实例索引，所以必须在 GPU 上传之前调用。
        /// </summary>
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

            // 键 = type * blockCount + 空间格索引。
            // 类型作为高位，保证排序后同类型的所有块连续排列。
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

                // AABB 用实例的真实范围，而不是网格的理论范围——
                // 边缘块往往只占网格的一部分，用真实范围能紧很多。
                // 竖直方向额外加草高余量（草从根部向上长 _nominalHeight*scale）。
                float grassH = _nominalHeight * _scaleMultiplier;
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

            // 统计每种类型的实例数，便于确认 7 种原型都真的烘进来了。
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

        /// <summary>一个空间块：实例区间 + 剔除用 AABB。</summary>
        private struct GrassBlock
        {
            public int Start;
            public int Count;
            public float3 Center;
            /// <summary>AABB 半尺寸（xyz）。用于精确的平面-盒支持函数。</summary>
            public float3 HalfExtent;
            /// <summary>
            /// 本块实例的 detail 原型类型（0..6）。
            /// 分块键含类型，所以同一块内类型必然一致——
            /// 这是「一个 draw command 只能用一个 mesh」的硬约束决定的。
            /// </summary>
            public int TypeIndex;
        }

        /// <summary>空间分块边长（米）。32m 在「块数量」与「块内实例数」间取平衡。</summary>
        private const float kBlockSize = 32f;

        /// <summary>块表（非空块）。剔除时只遍历它。</summary>
        private List<GrassBlock> _blocks;

        /// <summary>
        /// 全局实例号 → batch 索引（1265 万条 int，约 50MB）。
        /// 只在 batch 划分确定后生成一次；batch 按类型段切分，无法用公式推导。
        /// </summary>
        private NativeArray<int> _batchOfInstance;

        /// <summary>各 batch 的起始全局实例号。</summary>
        private NativeArray<int> _batchStartNative;

        /// <summary>
        /// 块表的 NativeArray 形式（供 Burst Job 使用）。
        /// 在 BuildInstances 时一次性生成，避免每帧从 List 转换产生托管分配。
        /// </summary>
        private NativeArray<GrassBlock> _blocksNative;

        /// <summary>把 _blocks 转成 NativeArray。空表时释放并置空。</summary>
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

        /// <summary>
        /// 计算各类型在 _instances 里的连续区间。
        ///
        /// 依赖 SortInstancesIntoBlocks 的排序键以类型为高位——
        /// 排序后同类型实例连续排列，所以只需扫一遍找每段的首尾。
        /// 若某类型没有实例（例如枯枝层为 0），返回 (0, 0)。
        /// </summary>
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

        /// <summary>
        /// 建立实例数据。两个来源：
        ///   1. 有 baking 数据（GrassChunkData）→ 直接用，这是 M2 的正常路径；
        ///   2. 没有 → 生成 M1 式的合成方阵，供链路验证与无数据的快速试跑。
        ///
        /// 统一产出 GrassInstance[]，下游（GPU 上传、剔除、包围盒）只有一条路径。
        /// 这样 M1 的验证脚本继续有效，不需要维护两套代码。
        /// </summary>
        private void BuildInstances()
        {
            // 允许重复调用（TryReground 会再调一次），先释放旧数组避免泄漏。
            if (_visibleCount.IsCreated)
                _visibleCount.Dispose();
            if (_lodCounts.IsCreated)
                _lodCounts.Dispose();

            // 没有任何注入数据时，尝试按序列化的目录自加载。
            // 只尝试一次：失败就退回 M1 合成方阵，不要每帧重试拖慢启动。
            if (!_triedAutoLoad && _chunkDataList == null && _chunkData == null
                && !string.IsNullOrEmpty(_grassDataDir))
            {
                _triedAutoLoad = true;
                TryAutoLoadChunkData();
            }

            if (_chunkDataList != null && _chunkDataList.Count > 0)
            {
                // 多 chunk：把各 chunk 的实例拼成一个连续数组。
                //
                // 为什么合并而不是「每 chunk 一个独立 BRG」：
                // 剔除是一次线性扫描，合并后一遍扫完所有 chunk；
                // batch 划分只由 16MB 容量决定，与 chunk 边界无关（一个 batch 可跨 chunk）。
                // 代价是失去「按 chunk 单独卸载」的能力——M3 接 SECTR 流式时
                // 需要改成按 chunk 分段并支持增删，届时用同样的合并布局 + 区间表即可。
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

                // 空间分块排序（在合并后的全局数组上做，跨 chunk 的块才完整）。
                SortInstancesIntoBlocks();
                _instanceCount = total;
                GroundResolved = true;
            }
            else if (_chunkData != null && _chunkData.Instances != null && _chunkData.Instances.Length > 0)
            {
                // 单 chunk 也走空间分块，保证剔除路径唯一。
                // 复制一份再排序，避免就地改动调用方持有的数据。
                _instances = (GrassInstance[])_chunkData.Instances.Clone();
                SortInstancesIntoBlocks();
                _instanceCount = _instances.Length;
                // 烘焙数据已含真实地表高度，不需要再吸附。
                GroundResolved = true;
            }
            else
            {
                _instances = SynthesizeGridInstances();
                SortInstancesIntoBlocks();
                GroundResolved = !_snapToGround || FindObjectsOfType<Terrain>().Length > 0;
            }

            // CPU 侧位置数组（剔除 Job 用）。GPU buffer 里是语义数据，剔除以位置为准。
            // 必须在空间分块排序之后构建——排序改变了实例索引。
            if (_positions.IsCreated)
                _positions.Dispose();
            _positions = new NativeArray<float3>(_instanceCount, Allocator.Persistent);
            for (int i = 0; i < _instanceCount; i++)
                _positions[i] = _instances[i].Position;

            BuildBlockArray();

            _visibleCount = new NativeArray<int>(1, Allocator.Persistent);
            _lodCounts = new NativeArray<int>(kLodCount, Allocator.Persistent);
        }

        /// <summary>
        /// 从 _grassDataDir 读取全部 .bytes 并注入。
        ///
        /// 为什么用 File.ReadAllBytes 而不是 AssetDatabase / Resources：
        /// 这是**编辑器与开发期**的演示数据通路。AssetDatabase 是编辑器 API，
        /// 运行时程序集（FishDemo.Rendering 无 Editor 限定）不能引用；
        /// Resources.Load 被项目规约禁止（AGENTS.md §3）。
        /// 打包后 Assets/ 不存在，此路径会失败并回退——正式版应改为经 YooAsset 加载。
        /// </summary>
        private void TryAutoLoadChunkData()
        {
            // 相对路径基于项目根（Unity 进程的当前目录即项目根）。
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
                    // 单个文件坏了不该让整片草消失，跳过并报告。
                    Debug.LogError($"[GrassBRG] 读取失败 {f}: {e.Message}");
                }
            }

            if (list.Count == 0)
                return;

            _chunkDataList = list;
            Debug.Log($"[GrassBRG] 自动加载 {list.Count} 个 chunk 的草数据，共 {total:N0} 实例");
        }

        /// <summary>M1 式的合成方阵：规则格点 + 地表吸附 + 随机 yaw/缩放。
        /// 仅在没有任何烘焙数据时使用；M2 正常路径走 GrassChunkData。
        /// </summary>
        private GrassInstance[] SynthesizeGridInstances()
        {
            int side = Mathf.CeilToInt(Mathf.Sqrt(_instanceCount));
            var result = new GrassInstance[_instanceCount];

            Vector3 origin = _anchor != null ? _anchor.position : transform.position;
            Terrain[] terrains = _snapToGround ? FindObjectsOfType<Terrain>() : null;

            for (int i = 0; i < _instanceCount; i++)
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
                    Normal = new float3(0f, 1f, 0f), // 合成数据不做坡面贴合
                    Color = 0xFFFFFFFFu,
                };
                // 黄金角错开 yaw，避免网格感；缩放取中值附近抖动
                inst.YawQ = (ushort)((i * 40503) & 0xFFFF);
                inst.ScaleQ = (byte)(128 + (i % 7) * 4 - 12);
                inst.Type = 0;
                result[i] = inst;
            }

            return result;
        }

        /// <summary>
        /// 取 (x,z) 处的地表高度。优先 Terrain.SampleHeight，其次向下射线。
        /// 都失败返回 fallbackY —— 宁可悬空也不要埋到地下看不到。
        /// </summary>
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

                    // SampleHeight 返回相对地形原点的局部高度，必须加 transform.position.y。
                    return t.SampleHeight(new Vector3(x, 0f, z)) + tp.y;
                }
            }

            // Terrain 不可用（例如 mesh 化后 Terrain 被移除）时用射线兜底。
            if (Physics.Raycast(new Vector3(x, fallbackY + 200f, z), Vector3.down,
                    out RaycastHit hit, 600f, ~0, QueryTriggerInteraction.Ignore))
            {
                return hit.point.y;
            }

            return fallbackY;
        }

        /// <summary>
        /// 32B SoA 上传（M2 目标路径，渲染异常待查，暂未启用）。
        /// 格式：段0 = _GrassPos(x,y,z,yaw01)，段1 = _GrassNormalScale(nx,ny,nz,scale)。
        /// </summary>
        private void UploadInstanceData(GraphicsBuffer buffer, int sliceStart, int sliceCount)
        {
            var packed = new NativeArray<uint>(sliceCount * 8, Allocator.Temp);

            // 段 0：位置 + yaw（相对切片起始，因为每个 batch 有独立 buffer 与基址）
            for (int i = 0; i < sliceCount; i++)
            {
                var inst = _instances[sliceStart + i];
                int o = i * 4;
                packed[o + 0] = math.asuint(inst.Position.x);
                packed[o + 1] = math.asuint(inst.Position.y);
                packed[o + 2] = math.asuint(inst.Position.z);
                packed[o + 3] = math.asuint(inst.YawRadians / (2f * math.PI)); // → [0,1)
            }

            // 段 1：法线 + 缩放（乘上整体缩放倍数）
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
            // 竖直方向留出草片高度，避免 bounds 贴地导致早期剔除。
            min.y -= _cullRadius;
            max.y += _cullRadius + 1f;

            Vector3 center = (Vector3)((min + max) * 0.5f);
            Vector3 size = (Vector3)(max - min);
            return new Bounds(center, size);
        }

        /// <summary>
        /// 构建并注册各类型 × 各 LOD 的 mesh。
        ///
        /// ── 设计 ──
        /// 7 种 detail 原型各有自己的 mesh（草、三叶草花、雏菊、蒲公英…），
        /// 每种类型 × 3 层 LOD = 21 个 mesh，全部注册进 BRG。
        ///
        /// LOD 策略（用户确认：原型 mesh 不做额外 LOD）：
        ///   LOD0 = 原型 mesh 本体（近处，能看到花/叶的真实形状）
        ///   LOD1 = 程序化草簇（中距，原型 mesh 的直接降级会明显穿帮，
        ///          用统一草簇更省顶点；远处本来就分辨不出花的形状）
        ///   LOD2 = 十字面片（远距，4 tri）
        ///
        /// 为什么 LOD1/2 不按类型区分：远处不可辨，统一草簇/面片反而省顶点与
        /// draw command 数量（同类型才需要不同 mesh，中远距所有类型看起来一样）。
        /// 代价：中距离时花会变成普通草簇——这是有意的取舍，不是遗漏。
        ///
        /// 为什么不走 LODGroup：BRG 的 LOD 是每条 draw command 指定不同 meshID，
        /// 实例数据只存一份，切换 LOD 只换 meshID，不复制数据、不需要 LODGroup 组件。
        /// </summary>
        private void BuildLodMeshes()
        {
            // 中/远两层的共享 mesh（所有类型共用）。
            var midClump = BuildClumpMesh(blades: 6);
            var farCross = BuildCrossMesh();

            int registered = 0;
            int missing = 0;

            for (int t = 0; t < kDetailTypeCount; t++)
            {
                // 优先用序列化注入的原型 mesh（由 DetailBrgSetup 在编辑期写入）。
                // 没有注入时才回退到按名字查找——那只在 mesh 恰好被加载过时有效。
                Mesh proto = null;
                if (_detailPrototypeMeshes != null && t < _detailPrototypeMeshes.Length)
                    proto = _detailPrototypeMeshes[t];

                if (proto == null)
                    proto = LoadDetailPrototypeMesh(kDetailMeshNames[t]);

                if (proto == null)
                {
                    missing++;
                    // 兜底：原型缺失时用近距草簇，至少不丢实例。
                    proto = BuildClumpMesh(blades: 12);
                }

                _typeLodMeshes[t, 0] = proto;
                _typeLodMeshes[t, 1] = midClump;
                _typeLodMeshes[t, 2] = farCross;

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
                      $"；LOD0 三角形 = {string.Join("/", _lodTriangleCounts)}");
        }

        /// <summary>
        /// 从已加载的网格资产里按名字取 detail 原型 mesh。
        ///
        /// 为什么不用 AssetDatabase：运行时不引入 UnityEditor；
        /// 这里遍历 Resources.FindObjectsOfTypeAll&lt;Mesh&gt;，
        /// 只要 mesh 已被某个已加载资产引用过就能找到。
        /// 若没找到（例如该 mesh 尚未被任何对象加载），返回 null 由调用方兜底。
        /// </summary>
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

        /// <summary>原型 mesh 名字 → Mesh 的缓存（首次使用时建立）。</summary>
        private static Dictionary<string, Mesh> _meshCache;

        /// <summary>
        /// 程序化草簇：blades 片叶子绕 Y 轴散开、各自向外倾斜，原点在根部。
        ///
        /// 为什么叶片要倾斜而不是全竖直：
        /// 竖直平行面片从正上方看会叠成一条线，且侧面看像栅栏。
        /// 向外倾斜（顶部散开、根部收拢）才是草丛的自然形态，
        /// 也让任意视角都有足够的面片朝向相机。
        ///
        /// 叶片分布用黄金角 + 确定性伪随机（不用 Random，保证每次构建结果一致，
        /// 否则烘焙数据与渲染网格的对应关系会在重启后变化，难以复现问题）。
        /// </summary>
        private static Mesh BuildClumpMesh(int blades)
        {
            const float height = 1.0f;
            const float baseRadius = 0.04f;   // 根部收拢半径
            const float topRadius = 0.22f;    // 顶部散开半径
            const float halfWidth = 0.035f;   // 单片叶宽的一半

            int vertCount = blades * 4;
            var vertices = new Vector3[vertCount];
            var normals = new Vector3[vertCount];
            var uvs = new Vector2[vertCount];
            var triangles = new int[blades * 6];

            for (int s = 0; s < blades; s++)
            {
                // 黄金角分布 + 高度/倾斜的确定性抖动
                float angle = s * 2.39996323f; // 黄金角，rad
                float jitter = Frac(Mathf.Sin(s * 12.9898f) * 43758.5453f);

                float bladeHeight = height * (0.65f + 0.35f * jitter);
                float tilt = 0.25f + 0.35f * Frac(jitter * 7.31f); // 外倾程度

                var dir = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle));
                // 叶片面法线：与径向垂直（叶面朝外）
                var normal = new Vector3(-dir.z, 0f, dir.x);
                // 叶宽方向：沿切向
                var right = new Vector3(dir.z, 0f, -dir.x);

                Vector3 rootPos = dir * baseRadius;
                Vector3 topPos = dir * (baseRadius + topRadius * tilt) + Vector3.up * bladeHeight;

                int v = s * 4;
                vertices[v + 0] = rootPos - right * halfWidth;
                vertices[v + 1] = rootPos + right * halfWidth;
                vertices[v + 2] = topPos + right * halfWidth;
                vertices[v + 3] = topPos - right * halfWidth;

                for (int k = 0; k < 4; k++)
                {
                    normals[v + k] = normal;
                    uvs[v + k] = new Vector2(k == 1 || k == 2 ? 1f : 0f, k >= 2 ? 1f : 0f);
                }

                int t = s * 6;
                triangles[t + 0] = v + 0; triangles[t + 1] = v + 2; triangles[t + 2] = v + 1;
                triangles[t + 3] = v + 0; triangles[t + 4] = v + 3; triangles[t + 5] = v + 2;
            }

            var mesh = new Mesh { name = $"GrassBRG_Clump_{blades}" };
            mesh.SetVertices(vertices);
            mesh.SetNormals(normals);
            mesh.SetUVs(0, uvs);
            mesh.SetTriangles(triangles, 0);
            // 包围盒要覆盖顶部散开的范围（topRadius），否则远处会被过早剔除
            float r = baseRadius + topRadius + halfWidth;
            mesh.bounds = new Bounds(new Vector3(0f, height * 0.5f, 0f),
                new Vector3(r * 2f, height, r * 2f));
            mesh.UploadMeshData(true);
            return mesh;
        }

        /// <summary>取小数部分，用于确定性伪随机（不依赖 UnityEngine.Random 的全局状态）。</summary>
        private static float Frac(float v) => v - Mathf.Floor(v);

        /// <summary>
        /// M1 的草片：两张十字交叉的竖直面片（8 顶点 / 4 三角形），原点在根部。
        /// 用十字而不是单面片，是因为单面片在某些 yaw 下会侧对相机，
        /// 看不出「旋转是否正确应用」，验证会失真。
        /// LOD2 远层直接复用它。
        /// </summary>
        private static Mesh BuildCrossMesh()
        {
            const float halfWidth = 0.2f;
            const float height = 1.0f;

            var vertices = new Vector3[8];
            var normals = new Vector3[8];
            var uvs = new Vector2[8];
            var triangles = new int[12];

            // 面片 A（法线 +Z）
            vertices[0] = new Vector3(-halfWidth, 0f, 0f);
            vertices[1] = new Vector3(halfWidth, 0f, 0f);
            vertices[2] = new Vector3(halfWidth, height, 0f);
            vertices[3] = new Vector3(-halfWidth, height, 0f);
            // 面片 B（法线 +X）
            vertices[4] = new Vector3(0f, 0f, -halfWidth);
            vertices[5] = new Vector3(0f, 0f, halfWidth);
            vertices[6] = new Vector3(0f, height, halfWidth);
            vertices[7] = new Vector3(0f, height, -halfWidth);

            for (int i = 0; i < 8; i++)
            {
                normals[i] = Vector3.up; // M1 无光照，仅占位
                uvs[i] = new Vector2((i & 1), (i >= 2 && i < 4) || (i >= 6) ? 1f : 0f);
            }

            triangles[0] = 0; triangles[1] = 2; triangles[2] = 1;
            triangles[3] = 0; triangles[4] = 3; triangles[5] = 2;
            triangles[6] = 4; triangles[7] = 6; triangles[8] = 5;
            triangles[9] = 4; triangles[10] = 7; triangles[11] = 6;

            var mesh = new Mesh { name = "GrassBRG_Cross" };
            mesh.SetVertices(vertices);
            mesh.SetNormals(normals);
            mesh.SetUVs(0, uvs);
            mesh.SetTriangles(triangles, 0);
            mesh.bounds = new Bounds(new Vector3(0f, height * 0.5f, 0f),
                new Vector3(halfWidth * 2f, height, halfWidth * 2f));
            // 上传后释放 CPU 侧副本：BRG 只需要 GPU 上的顶点/索引数据。
            mesh.UploadMeshData(true);
            return mesh;
        }

        // ───────────────────────── 剔除回调 ─────────────────────────

        /// <summary>
        /// 引擎每次为该 BRG 剔除时调用。M1 在 Burst Job 里做逐实例视锥测试。
        ///
        /// 为什么单线程 IJob：M1 只有 1000 个实例，单线程 Burst 足够；
        /// M3 单 chunk 约 57 万实例，届时改成 IJobParallelFor + 原子计数压缩。
        /// 现在先把「Burst + 指针输出」的骨架立起来，M3 只需换内层循环。
        ///
        /// 不能在这里 Complete()：会阻塞主线程；引擎会等返回的 JobHandle。
        /// </summary>
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

            // 关键一致性检查：查找表长度必须覆盖全部实例，否则 Job 会读到越界/垃圾
            // （表现为整片草消失且无报错）。初始化时序出问题时这里会暴露。
            if (!_batchOfInstance.IsCreated || _batchOfInstance.Length < _positions.Length
                || !_batchStartNative.IsCreated || _batches == null || _batches.Count == 0)
            {
                Debug.LogError($"[GrassBRG] 剔除表未就绪：batchOfInstance={(_batchOfInstance.IsCreated ? _batchOfInstance.Length : -1)}" +
                               $" positions={_positions.Length} batchStart={(_batchStartNative.IsCreated ? _batchStartNative.Length : -1)}" +
                               $" batches={_batches?.Count ?? -1}");
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

            // 每个 batch × 每个 LOD 层 = 一条 draw command。
            // 引擎负责释放（Allocator.TempJob + 不 Dispose 是官方约定）。
            var visible = (int*)UnsafeUtility.Malloc(
                sizeof(int) * capacity, UnsafeUtility.AlignOf<int>(), Allocator.TempJob);
            var commands = (BatchDrawCommand*)UnsafeUtility.Malloc(
                sizeof(BatchDrawCommand) * commandCount, UnsafeUtility.AlignOf<BatchDrawCommand>(), Allocator.TempJob);
            var ranges = (BatchDrawRange*)UnsafeUtility.Malloc(
                sizeof(BatchDrawRange) * batchCount, UnsafeUtility.AlignOf<BatchDrawRange>(), Allocator.TempJob);

            output->visibleInstances = visible;
            output->drawCommands = commands;
            output->drawRanges = ranges;

            // 命令矩阵：命令 [b * kLodCount + lod] = batch b 的 LOD lod 层。
            // 关键：visibleInstances 里存的是「全局实例号」，但 shader 读取时
            // SV_InstanceID 是命令内的相对索引（从 visibleOffset 起算），
            // metadata 的基址属于该 batch 的 buffer。所以命令里的可见实例必须是
            // 「batch 内相对索引」，而不是全局号——Job 按 batch 分段写入时已转换。
            //
            // meshID 取 [该 batch 的类型, lod]：这是「7 种原型各用各自 mesh」的落点。
            // 同一类型的不同 batch（大类型被 16MB 切成多段）共用同一组 meshID。
            for (int b = 0; b < batchCount; b++)
            {
                var slice = _batches[b];
                int batchType = _batchType[b];
                for (int lod = 0; lod < kLodCount; lod++)
                {
                    commands[b * kLodCount + lod] = new BatchDrawCommand
                    {
                        batchID = slice.BatchID,
                        materialID = _materialID,
                        meshID = _typeLodMeshIDs[batchType, lod],
                        submeshIndex = 0,
                        // 相机视图只有一个 split，bit0 = 1。写成 0 会导致整批被丢弃。
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
                        layer = 0,             // Default，必须落在相机 cullingMask 内
                        renderingLayerMask = 1u,
                        shadowCastingMode = ShadowCastingMode.Off,             // M1 不投阴影
                        receiveShadows = false,
                        motionMode = MotionVectorGenerationMode.ForceNoMotion, // M1 无 motion pass
                        staticShadowCaster = false,
                        allDepthSorted = false,
                    },
                };
            }

            // 视锥平面抽成 float4（xyz = 法线，w = distance），避免 Burst 处理 UnityEngine.Plane。
            int planeCount = cullingContext.cullingPlanes.Length;
            var planes = new NativeArray<float4>(planeCount, Allocator.TempJob,
                NativeArrayOptions.UninitializedMemory);
            for (int i = 0; i < planeCount; i++)
            {
                Plane plane = cullingContext.cullingPlanes[i];
                Vector3 n = plane.normal;
                planes[i] = new float4(n.x, n.y, n.z, plane.distance);
            }

            // 相机参数用于把屏幕像素阈值反算成距离。
            // lodParameters 由引擎填充；个别情况（如渲染请求）可能为零值，用 Camera.main 兜底。
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
                    // 都拿不到就退化成「全部按近距离画」，宁可多画也不要画错。
                    camPos = Vector3.zero;
                    fov = 60f;
                    pixelHeight = 1080;
                }
            }

            var job = new CullJob
            {
                Positions = _positions,
                Planes = planes, // 由 Job 负责 Dispose
                Radius = _cullRadius,
                VisibleInstances = visible,
                Output = output,
                VisibleCountDebug = (int*)NativeArrayUnsafeUtility.GetUnsafePtr(_visibleCount),
                LodCountsDebug = (int*)NativeArrayUnsafeUtility.GetUnsafePtr(_lodCounts),
                CameraPosition = new float3(camPos.x, camPos.y, camPos.z),
                LodPixelScale = _nominalHeight * _scaleMultiplier * pixelHeight / (2f * Mathf.Tan(fov * 0.5f * Mathf.Deg2Rad)),
                Lod0PixelHeight = _enableLod ? _lod0PixelHeight : 0f,
                Lod1PixelHeight = _enableLod ? _lod1PixelHeight : 0f,
                CullPixelHeight = _enableLod ? _cullPixelHeight : 0f,
                DebugShot = Mathf.Max(0, _debugCullShots),
                Blocks = _blocksNative,
                BatchOfInstance = _batchOfInstance,
                BatchStart = _batchStartNative,
                BatchCount = batchCount,
                // 块级距离剔除上限：与逐实例的像素阈值同一套换算。
                // 关 LOD 时不做距离剔除（0 = 不限），保持「画全部」的语义。
                MaxVisibleDistance = _enableLod && _cullPixelHeight > 0f
                    ? _nominalHeight * _scaleMultiplier * pixelHeight
                      / (2f * Mathf.Tan(fov * 0.5f * Mathf.Deg2Rad)) / _cullPixelHeight
                    : 0f,
            };

            // 用上一次的 handle 作为依赖，把多次剔除串成链。
            //
            // 为什么必须串：OnPerformCulling 会「每个视图各调一次」（Game 视图 + Scene 视图），
            // 而 _lastCullHandle 只有一个字段。不串的话第二次会覆盖第一次的 handle，
            // Shutdown 时只 Complete 最后一个，早期 Job 仍可能引用即将被 Dispose 的 _positions
            // → 释放正在被读取的原生内存。
            // 串行代价可忽略（M1 单线程 IJob，1000 实例），换来的是生命周期严格有序。
            _lastCullHandle = job.Schedule(_lastCullHandle);
            return _lastCullHandle;
        }

        /// <summary>
        /// 逐实例做「视锥 + 距离 LOD」判定，并按层把可见索引压缩进 visibleInstances。
        ///
        /// 为什么用三遍扫描而不是一遍：
        /// BRG 的每条 draw command 用 visibleOffset/visibleCount 在一维数组里切片，
        /// 所以同一 LOD 层的实例索引必须连续。一遍扫描无法预知各层数量、只能事后重排，
        /// 三遍扫描（每遍只收一层）逻辑最简单且不需要额外缓冲区。
        /// 代价是 3 倍的位置读取——M1 的 1000 实例可忽略；
        /// M3 单 chunk 57 万实例时改成「先计数、再前缀和、最后并行填充」。
        ///
        /// 单线程 IJob：见 OnPerformCulling 的说明（M1 规模）。
        /// </summary>
        [BurstCompile]
        private unsafe struct CullJob : IJob
        {
            [ReadOnly] public NativeArray<float3> Positions;

            /// <summary>
            /// 视锥平面（xyz=法线，w=distance），主线程每帧从 cullingPlanes 转换后传入。
            /// [DeallocateOnJobCompletion] 让 Job 系统在 Job 完成时释放——
            /// 不能在 Job 内 Dispose（Unity 会抛 InvalidOperationException）。
            /// </summary>
            [DeallocateOnJobCompletion]
            public NativeArray<float4> Planes;

            public float Radius;

            [NativeDisableUnsafePtrRestriction] public int* VisibleInstances;
            [NativeDisableUnsafePtrRestriction] public BatchCullingOutputDrawCommands* Output;
            [NativeDisableUnsafePtrRestriction] public int* VisibleCountDebug;
            [NativeDisableUnsafePtrRestriction] public int* LodCountsDebug;

            public float3 CameraPosition;

            /// <summary>见 OnPerformCulling：dist = LodPixelScale / pixelHeight。</summary>
            public float LodPixelScale;

            /// <summary>三层像素阈值。Lod0PixelHeight &lt;= 0 表示关掉 LOD（全部按 LOD0 画）。</summary>
            public float Lod0PixelHeight;
            public float Lod1PixelHeight;
            public float CullPixelHeight;

            /// <summary>诊断用：剩余打印次数。</summary>
            public int DebugShot;

            /// <summary>草的最大可见距离（米）。由 CullPixelHeight 反算；0 表示不限。</summary>
            public float MaxVisibleDistance;

            /// <summary>与主线程侧 kMaxBatchBytes / kBytesPerInstance 一致：524,288。</summary>
            public const int BatchMaxInstances = 16 * 1024 * 1024 / 32;

            /// <summary>batch 总数（主线程已算好，Job 不再自行推导）。</summary>
            public int BatchCount;

            /// <summary>
            /// 空间块表。剔除时先测块包围盒，整块不可见就跳过其全部实例。
            /// 这是把 1265 万实例的全量扫描降到「视野内几十块」的关键。
            /// </summary>
            [ReadOnly] public NativeArray<GrassBlock> Blocks;

            /// <summary>
            /// 实例号 → batch 索引。
            ///
            /// 为什么用表而不是公式：batch 现在按「类型段 × 16MB」切分，
            /// 每个类型的段起点不同、段内还可能再切，无法用一条除法公式覆盖
            /// （旧代码 i / BatchMaxInstances 只对均匀切分成立）。
            /// 表在 BuildInstances 时一次性生成（int[N]，1265 万条约 50MB），
            /// 换取剔除内层循环 O(1) 且无分支。
            /// </summary>
            [ReadOnly] public NativeArray<int> BatchOfInstance;

            /// <summary>
            /// 每个 batch 的起始全局实例号（索引 = batch 号）。
            /// 用于把全局实例号换算成 batch 内相对索引。
            /// </summary>
            [ReadOnly] public NativeArray<int> BatchStart;

            public void Execute()
            {
                bool lodEnabled = Lod0PixelHeight > 0f && LodPixelScale > 0f;
                int batchCount = BatchCount;
                int cmdCount = batchCount * 3;

                // 两个临时缓冲：每命令计数、每命令写指针。
                // UnsafeUtility.Malloc 是裸内存，Job 内 Free 是允许的
                // （与 NativeArray 的 safety handle 不同，后者禁止在 Job 内 Dispose）。
                var counts = (int*)UnsafeUtility.Malloc(
                    sizeof(int) * cmdCount, UnsafeUtility.AlignOf<int>(), Allocator.TempJob);
                var writePtr = (int*)UnsafeUtility.Malloc(
                    sizeof(int) * cmdCount, UnsafeUtility.AlignOf<int>(), Allocator.TempJob);

                for (int i = 0; i < cmdCount; i++)
                    counts[i] = 0;

                // ── 第 1 遍：块级预剔除 + 计数 ──
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

                        // batch 由查找表给出：batch 按「类型段 × 16MB」切分，
                        // 实例号与 batch 号不再是简单的除法关系。
                        int batch = BatchOfInstance[i];
                        counts[batch * 3 + lod]++;
                    }
                }

                // 前缀和 → 各命令的写入起点
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

                // ── 第 2 遍：填充索引 ──
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

                        // batch 由查找表给出（类型段 × 16MB 切分，不能用除法）。
                        int batch = BatchOfInstance[i];
                        int slot = batch * 3 + lod;

                        // 写「batch 内相对索引」而不是全局号：
                        // shader 用 SV_InstanceID 作为 buffer 内的相对偏移，
                        // metadata 基址属于该 batch 自己的 buffer。
                        // batch 起点要按它的实际起始实例号算，不能再用 batch*BatchMaxInstances
                        // （那只有在均匀切分时才成立）。
                        int batchStart = BatchStart[batch];
                        VisibleInstances[writePtr[slot]++] = i - batchStart;
                    }
                }

                UnsafeUtility.Free(counts, Allocator.TempJob);
                UnsafeUtility.Free(writePtr, Allocator.TempJob);

                // 关 LOD 时后两层清零
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

                // LOD 统计：从命令直接累加，不额外扫描
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

                // 诊断：前几次剔除打印内部状态，定位「0 可见」到底卡在哪一步。
                if (DebugShot > 0)
                {
                    DebugShot--;
                    LogCullDebug(visibleBlocks, blockCount, scannedInstances,
                        Positions.Length, dst, Output, cmdCount);
                }

                // 不要在这里 Dispose(Planes)。
                // Unity 禁止在 Job 内部释放 native container：
                //   InvalidOperationException: The native container may not be deallocated on a job.
                // 正确做法是给字段加 [DeallocateOnJobCompletion]，由 Job 系统在完成时释放。
                // （实测踩过：数据写完之后才崩溃，所以画面正常、只有 Editor.log 里刷满异常，
                //   222 条全是这一条，极容易被漏掉。）
            }

            /// <summary>
            /// 诊断日志。用 [BurstDiscard] 隔离：Burst 编译时整个方法被丢弃，
            /// 只有走托管运行时（编辑器 + 关闭 Burst 时）才执行。
            ///
            /// 为什么不能直接写在 Execute 里：
            /// Burst 的字符串插值只支持 G/g/D/d/X/x 六种格式说明符
            /// （BurstString.cs:631 显式抛错），这里原本用的 :N0 会让 CullJob
            /// 整个编译失败（BC1343），后果不是「少打一条日志」而是**整个剔除 Job
            /// 不生效**——草照样显示（因为走托管回退），所以错误极易被忽略。
            /// 改用 [BurstDiscard] 后，诊断能力保留，Burst 路径不受影响。
            /// </summary>
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

                // 逐命令输出计数，看可见实例分布在哪些 batch/LOD
                var sb = new System.Text.StringBuilder();
                for (int c = 0; c < cmdCount; c++)
                {
                    var cmd = output->drawCommands[c];
                    if (cmd.visibleCount > 0)
                        sb.Append($" cmd[{c}](b{c / 3},L{c % 3})={cmd.visibleCount}");
                }
                Debug.Log($"[CullJob] total={visibleInstances} cmds>{sb}");
            }

            /// <summary>
            /// 块级预剔除：用块的**精确 AABB** 做 6 平面测试。
            ///
            /// 早先用「球 + 水平/竖直合成半径」太保守：实测 9216 块里有 1410 块被判可见
            /// （扫描 193 万实例 = 15.3%），而实际视野内的块只有几十个。
            /// 原因是合成半径把水平 22.6m 与竖直几十米叠加，导致视锥外远处的块也通过测试。
            ///
            /// 改用 AABB 的「中心 + 半尺寸」逐轴投影，比球紧得多：
            ///   平面 n·x + d 下，AABB 在法线方向的支持半径为
            ///   |n.x|*hx + |n.y|*hy + |n.z|*hz（h 为半尺寸）
            /// 这是 AABB 的精确支撑函数，不保守也不激进。
            /// </summary>
            private bool BlockVisible(GrassBlock block)
            {
                // 距离剔除（比视锥更关键）：相机 far clip 3000m 使视锥覆盖整个世界，
                // 只做视锥测试实测 1384/9216 块「可见」。但草在 CullPixelHeight
                // 对应距离（约 117m）外会被逐实例剔除，那些块扫了也白扫。
                // 用块 AABB 最近点距离粗筛，可把 9216 块砍到几十块。
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
                    // Unity Plane：法线指向内侧，dot(n, p) + d >= 0 为可见。
                    if (math.dot(pl.xyz, pos) + pl.w < -Radius)
                        return false;
                }
                return true;
            }

            /// <summary>
            /// 按屏幕像素高度决定 LOD 层。返回 0/1/2；返回 -1 表示超出最远距离（剔除）。
            ///
            /// 推导：世界尺寸 h 的物体在距离 d 处占的屏幕像素
            ///   pixelH = h * (screenH / (2*d*tan(fov/2)))
            /// 反解 d 得到像素阈值 → 距离阈值。
            /// LodPixelScale 里已经预乘了 h*screenH/(2*tan(fov/2))，所以 d = LodPixelScale / pixelH。
            ///
            /// 注意比较方向：像素高度随距离**递减**，所以：
            ///   近（像素大）→ LOD0；远（像素小）→ LOD2。
            /// </summary>
            private int ResolveLod(float3 pos)
            {
                float dist = math.distance(pos, CameraPosition);
                // 避免除零；距离 0 该是最精细的 LOD0。
                float pixelH = dist > 1e-4f ? LodPixelScale / dist : float.MaxValue;

                if (pixelH >= Lod0PixelHeight) return 0;
                if (pixelH >= Lod1PixelHeight) return 1;
                if (pixelH >= CullPixelHeight) return 2;
                return -1;
            }
        }
    }
}
