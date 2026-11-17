using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

public static class TreeEndingMaterialFix
{
    private const string Tag = "[TreeEnding]";
    private const string TargetGuid = "3eae9beb201a57d449c20d45949ed190";
    private const string ShaderGuid = "896c4566c534d544085eac02e8d305d9";
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
