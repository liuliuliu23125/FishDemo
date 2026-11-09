using UnityEngine;
namespace ProceduralWorlds.GTS
{
    /// <summary>
    /// Experimental: Use at your own risk!
    /// Attach the GTS Mesh script to a converted Terrain to update the Material with the Profile.
    /// </summary>
    [ExecuteAlways]
    [RequireComponent(typeof(MeshRenderer))]
    public class GTSMesh : GTSComponent
    {
        public GTSAssetID<Material> m_defaultMaterialID = new GTSAssetID<Material>();
        private Material m_defaultMaterial;
        public Material defaultMaterial
        {
            get
            {
#if UNITY_EDITOR
                if (m_defaultMaterial == null)
                    m_defaultMaterial = m_defaultMaterialID?.LoadAsset();
#endif
                return m_defaultMaterial;
            }
            set
            {
#if UNITY_EDITOR
                if (m_defaultMaterial != value)
                    m_defaultMaterialID?.SaveAsset(value);
#endif
                m_defaultMaterial = value;
            }
        }
        private MeshRenderer m_meshRenderer;
        public MeshRenderer meshRenderer
        {
            get
            {
                if (m_meshRenderer == null)
                    m_meshRenderer = GetComponent<MeshRenderer>();
                return m_meshRenderer;
            }
        }
        private MeshFilter m_meshFilter;
        public MeshFilter meshFilter
        {
            get
            {
                if (m_meshFilter == null)
                    m_meshFilter = GetComponent<MeshFilter>();
                return m_meshFilter;
            }
        }
        public Mesh sharedMesh
        {
            get
            {
                if (meshFilter != null)
                    return meshFilter.sharedMesh;
                return null;
            }
        }
        public override Material material
        {
            get => meshRenderer.sharedMaterial;
            set => meshRenderer.sharedMaterial = value;
        }
        public override Vector2 position
        {
            get
            {
                Vector3 worldPos = transform.position;
                return new Vector2(worldPos.x, worldPos.z);
            }
        }
        public override Vector2 size
        {
            get => new Vector2(1024, 1024);
        }
        /// <summary>
        /// 地形世界尺寸，用于计算贴图 tiling（tiling = worldSize / layer.tileSize）。
        ///
        /// ── 为什么不再硬编码 1024 ──
        /// 原实现返回固定 (1024, 0, 1024)，是给"一个 Terrain = 1024m"的场景用的。
        /// 本工程把每个 512m chunk 再切成 2×2 的 256m subTile，
        /// 若仍按 1024 计算，tiling 会是正确值的 4 倍 —— 贴图肉眼可见过密。
        ///
        /// 这里改为读取序列化字段 m_worldSizeOverride：
        ///   > 0 时用它（由构建脚本按 chunk 实际尺寸写入）
        ///   否则回退到 mesh 包围盒的 XZ 尺寸（转换后 mesh 单位就是米）
        /// </summary>
        public override Vector3 worldSize
        {
            get
            {
                if (m_worldSizeOverride.x > 0.001f && m_worldSizeOverride.z > 0.001f)
                    return new Vector3(m_worldSizeOverride.x, 0f, m_worldSizeOverride.z);

                var mesh = sharedMesh;
                if (mesh != null)
                {
                    var b = mesh.bounds.size;
                    if (b.x > 0.001f && b.z > 0.001f)
                        return new Vector3(b.x, 0f, b.z);
                }

                // 兜底：保留原行为
                return new Vector3(1024f, 0f, 1024f);
            }
        }
        /// <summary>构建期写入的地形世界尺寸；0 表示未设置，回退到 mesh 包围盒。</summary>
        [SerializeField] private Vector3 m_worldSizeOverride = Vector3.zero;

        /// <summary>
        /// 由构建脚本调用，写入该 mesh 实际覆盖的地形世界尺寸（米）。
        /// 必须在 Refresh() 之前调用，否则 tiling 会按旧值算。
        /// </summary>
        public void SetWorldSizeOverride(Vector3 worldSizeXZ)
        {
            m_worldSizeOverride = new Vector3(worldSizeXZ.x, 0f, worldSizeXZ.z);
        }
        public override int heightmapResolution
        {
            get => 1024;
        }
        public void GetDefaulTerrainMaterial()
        {
            if (defaultMaterial == null)
            {
                GTSUtility.GetPipelineShaders(out Shader gtsShader, out Shader unityShader);
                Material material = meshRenderer.sharedMaterial;
                if (material != null)
                    if (material.shader != gtsShader)
                        defaultMaterial = material;
#if UNITY_EDITOR
                    else
                        defaultMaterial = GTSDefaults.GetDefaultTerrainMaterial();
#endif
            }
        }
        public override void ApplyProfile()
        {
            if (profile == null)
                return;
            GetDefaulTerrainMaterial();
            SetTerrainToGTS();
        }
        public override void RemoveProfile()
        {
            if (defaultMaterial != null)
                material = defaultMaterial;
        }
    }
}