using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

/// <summary>
/// 重建丢失的 PW_Tree_Ending 材质并接回引用。
///
/// ── 为什么必须修 ──
/// guid `3eae9beb201a57d449c20d45949ed190` 被 Spruce_01/02/04/07 与 Pine 系的
/// FBX externalObjects 和 prefab 引用，但工程里**没有任何 .mat 拥有这个 guid**。
/// 运行时日志（Logs/tree-fd4.log）报了 216 条：
///   "The tree PW_Tree_Spruce_02 couldn't be instanced because one of the materials is missing."
/// 即：只要有一个材质槽缺失，Unity 就**完全放弃这批树的 GPU instancing**，
/// 退回逐实例提交 —— 这就是「树的批次很多」的真正根因，不是 keyword 差异。
///
/// ── 为什么必须复用同一个 guid ──
/// 引用它的地方是 FBX 的导入映射（externalObjects）和 45 个 prefab，
/// 新建材质拿新 guid 等于要改上百处引用。所以这里直接写 .mat + .meta，
/// 让 .meta 里带原始 guid，Unity 导入后所有旧引用自动接上。
///
/// ── 参数依据 ──
/// Ending 是树干根部/树桩端面的过渡几何（贴图是年轮横截面），
/// 与 Bark 同属不透明树干类，故照 Bark 的配置：
///   shader = PW_General_ShaderGraph_URP（全场唯一的树 shader）
///   keyword = _PW_SF_WIND_ON（随树干一起摆动；不启用 COVER/SSS/WORLDMAP，
///             这三项是给叶片用的，树干材质的 Bark 也都只有 WIND）
///   cutoff  = 0（不透明，不做 alpha test）
///   CullMode= 2（Back），与 Bark 一致
/// 贴图用 Terrain Details/Textures 下现成的 PW_Tree_Ending_D/N/M。
///
/// 用法：-executeMethod TreeEndingMaterialFix.Run
/// 幂等：已存在则只校验不改写。
/// </summary>
public static class TreeEndingMaterialFix
{
    private const string Tag = "[TreeEnding]";
    private const string TargetGuid = "3eae9beb201a57d449c20d45949ed190";
    private const string ShaderGuid = "896c4566c534d544085eac02e8d305d9";   // PW_General_ShaderGraph_URP
    private const string MatPath =
        "Assets/Procedural Worlds/Packages - Install/Gaia Pro Assets and Biomes/Content Resources/Trees/PW_Tree_Spruce/PW_Tree_Ending.mat";
    private const string TexDir =
        "Assets/Procedural Worlds/Packages - Install/Gaia Pro Assets and Biomes/Content Resources/Terrain Details/Textures";
    private const string ResultPath = "Logs/tree-ending-fix.txt";

    public static void Run()
    {
        var sb = new StringBuilder();
        void W(string s) { Debug.Log($"{Tag} {s}"); sb.AppendLine(s); }

        string dG = TexGuid("PW_Tree_Ending_D.png");
        string nG = TexGuid("PW_Tree_Ending_N.png");
        string mG = TexGuid("PW_Tree_Ending_M.png");
        W($"贴图 guid: D={dG} N={nG} M={mG}");
        if (dG == null) { W("FAIL：找不到 PW_Tree_Ending_D.png"); Flush(sb, 2); return; }

        if (File.Exists(MatPath))
        {
            W($"目标已存在，跳过创建：{MatPath}");
            var exist = AssetDatabase.LoadAssetAtPath<Material>(MatPath);
            W($"  已加载 = {exist != null}，shader = {(exist != null ? exist.shader.name : "-")}");
            W($"  _MainTex = {(exist != null && exist.GetTexture("_MainTex") != null ? exist.GetTexture("_MainTex").name : "NULL")}");
            Flush(sb, 0);
            return;
        }

        // ── 手写 .mat 与 .meta ──
        // 不用 new Material() + CreateAsset：那样拿不到指定 guid。
        // 必须由我们提供 .meta，Unity 才会把这个材质认成 guid 3eae9beb…。
        Directory.CreateDirectory(Path.GetDirectoryName(MatPath));

        string mat = $@"%YAML 1.1
%TAG !u! tag:unity3d.com,2011:
--- !u!21 &2100000
Material:
  serializedVersion: 8
  m_ObjectHideFlags: 0
  m_CorrespondingSourceObject: {{fileID: 0}}
  m_PrefabInstance: {{fileID: 0}}
  m_PrefabAsset: {{fileID: 0}}
  m_Name: PW_Tree_Ending
  m_Shader: {{fileID: -6465566751694194690, guid: {ShaderGuid}, type: 3}}
  m_Parent: {{fileID: 0}}
  m_ModifiedSerializedProperties: 0
  m_ValidKeywords:
  - _PW_SF_WIND_ON
  m_InvalidKeywords: []
  m_LightmapFlags: 4
  m_EnableInstancingVariants: 1
  m_DoubleSidedGI: 1
  m_CustomRenderQueue: -1
  stringTagMap: {{}}
  disabledShaderPasses:
  - TransparentDepthPrepass
  - TransparentDepthPostpass
  - TransparentBackface
  - RayTracingPrepass
  m_LockedProperties: 
  m_SavedProperties:
    serializedVersion: 3
    m_TexEnvs:
    - _BumpMap:
        m_Texture: {{fileID: 2800000, guid: {nG}, type: 3}}
        m_Scale: {{x: 1, y: 1}}
        m_Offset: {{x: 0, y: 0}}
    - _MainTex:
        m_Texture: {{fileID: 2800000, guid: {dG}, type: 3}}
        m_Scale: {{x: 1, y: 1}}
        m_Offset: {{x: 0, y: 0}}
    - _MetallicGlossMap:
        m_Texture: {{fileID: 2800000, guid: {mG}, type: 3}}
        m_Scale: {{x: 1, y: 1}}
        m_Offset: {{x: 0, y: 0}}
    - _PW_CoverLayer0:
        m_Texture: {{fileID: 0}}
        m_Scale: {{x: 1, y: 1}}
        m_Offset: {{x: 0, y: 0}}
    - _PW_CoverLayer0Normal:
        m_Texture: {{fileID: 0}}
        m_Scale: {{x: 1, y: 1}}
        m_Offset: {{x: 0, y: 0}}
    - _PW_CoverLayer1:
        m_Texture: {{fileID: 0}}
        m_Scale: {{x: 1, y: 1}}
        m_Offset: {{x: 0, y: 0}}
    - _PW_CoverLayer1Normal:
        m_Texture: {{fileID: 0}}
        m_Scale: {{x: 1, y: 1}}
        m_Offset: {{x: 0, y: 0}}
    - _PW_WorldMap:
        m_Texture: {{fileID: 0}}
        m_Scale: {{x: 1, y: 1}}
        m_Offset: {{x: 0, y: 0}}
    - unity_Lightmaps:
        m_Texture: {{fileID: 0}}
        m_Scale: {{x: 1, y: 1}}
        m_Offset: {{x: 0, y: 0}}
    - unity_LightmapsInd:
        m_Texture: {{fileID: 0}}
        m_Scale: {{x: 1, y: 1}}
        m_Offset: {{x: 0, y: 0}}
    - unity_ShadowMasks:
        m_Texture: {{fileID: 0}}
        m_Scale: {{x: 1, y: 1}}
        m_Offset: {{x: 0, y: 0}}
    m_Ints: []
    m_Floats:
    - _ALPHATEST: 0
    - _AOPower: 0.6
    - _AOPowerExp: 1
    - _AOVertexMask: 0.2
    - _AlphaTest: 0
    - _AlphaToMask: 0
    - _BumpMapScale: 1
    - _CullMode: 2
    - _Cutoff: 0
    - _Glossiness: 0.1
    - _Metallic: 0
    - _PW_SF_WIND: 1
    - _PW_ShaderMode: 0
    - _QueueControl: -1
    - _QueueOffset: 0
    - _RenderQueueType: 1
    - _SurfaceType: 0
    - _WrapLighting: 0
    - _ZWrite: 1
    m_Colors:
    - _Color: {{r: 0.8113208, g: 0.51106006, b: 0.36356357, a: 1}}
    - _EmissionColor: {{r: 1, g: 1, b: 1, a: 1}}
";

        string meta = $@"fileFormatVersion: 2
guid: {TargetGuid}
NativeFormatImporter:
  externalObjects: {{}}
  mainObjectFileID: 2100000
  userData: 
  assetBundleName: 
  assetBundleVariant: 
";

        File.WriteAllText(MatPath, mat.Replace("\r\n", "\n"));
        File.WriteAllText(MatPath + ".meta", meta.Replace("\r\n", "\n"));
        W($"已写出 {MatPath}（guid={TargetGuid}）");

        AssetDatabase.Refresh();

        var created = AssetDatabase.LoadAssetAtPath<Material>(MatPath);
        bool ok = created != null
                  && created.shader != null
                  && created.GetTexture("_MainTex") != null;
        W($"导入校验：mat={(created != null)} shader={(created != null ? created.shader.name : "-")} " +
          $"_MainTex={(created != null && created.GetTexture("_MainTex") != null ? created.GetTexture("_MainTex").name : "NULL")}");
        W($"实际 guid = {AssetDatabase.AssetPathToGUID(MatPath)}");
        W($"==== 结论：{(ok ? "PASS" : "FAIL")} ====");
        Flush(sb, ok ? 0 : 2);
    }

    private static string TexGuid(string file)
    {
        string p = $"{TexDir}/{file}.meta";
        if (!File.Exists(p)) return null;
        foreach (var line in File.ReadAllLines(p))
            if (line.StartsWith("guid: ")) return line.Substring(6).Trim();
        return null;
    }

    private static void Flush(StringBuilder sb, int code)
    {
        Directory.CreateDirectory("Logs");
        File.WriteAllText(ResultPath, sb.ToString());
        if (Application.isBatchMode) EditorApplication.Exit(code);
    }
}
