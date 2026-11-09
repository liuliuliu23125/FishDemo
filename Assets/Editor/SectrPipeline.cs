using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// SECTR 地形切块 + 导出 chunk 的批处理入口。
///
/// ── 为什么这个文件不放 asmdef 目录 ──
/// SECTR 没有自己的 asmdef，其编辑器 API（SECTR_TerrainWindow.SectorizeTerrain、
/// SECTR_StreamExport.ExportSceneChunks）编译进默认的 Assembly-CSharp-Editor。
/// 而 asmdef 程序集（如 FishDemo.Editor）无法引用 Assembly-CSharp-Editor。
/// 所以这个脚本必须放在 Assets/Editor/ 下，走默认程序集。
///
/// ── 工序约束（顺序不可颠倒）──
/// SectorizeTerrain 会把原 TerrainData 的高度/Alpha/Detail/Tree 按块复制到新 Terrain，
/// 并把 sector 范围内的静态子物体收进 sector（SECTR_TerrainWindow.cs:230-330）。
/// 因此必须在植被撒点之后执行；执行后原始 Terrain 会被 SetActive(false)。
///
/// 用法：
///   Unity.exe -batchmode -projectPath <proj> -executeMethod SectrPipeline.SectorizeAndExport -logFile <log>
///   不要加 -quit，由脚本自己 Exit。
/// </summary>
public static class SectrPipeline
{
    private const int SectorsPerSide = 2;   // 2×2 → 每块 512m
    private const string WorldScenePath = "Assets/Scenes/World.unity";
    private const string Tag = "[SectrPipeline]";

    public static void SectorizeAndExport()
    {
        try
        {
            EditorSceneManager.OpenScene(WorldScenePath, OpenSceneMode.Single);
            Log($"已打开场景 {WorldScenePath}");

            // 直接遍历 Terrain 组件，不要用 transform.root 去重。
            // Gaia 会把 9 块地形统一挂在 "Gaia Terrains" 根节点下，
            // 若按 root 去重只会得到 1 个不含 Terrain 组件的根，导致一块都没切。
            // FindObjectsOfType 默认只返回激活对象，先取快照再处理——
            // SectorizeTerrain 结尾会把原始地形 SetActive(false)。
            var terrains = UnityEngine.Object.FindObjectsOfType<Terrain>();
            Log($"待切块地形数 = {terrains.Length}");

            if (terrains.Length == 0)
            {
                Abort("场景里没有 Terrain");
                return;
            }

            // 逐个地形切块，每块切成 2×2。
            // includeStatic=true 收静态物体；includeDynamic=false 排除水面等动态根；
            // sortGaiaObjects=true 让 Gaia 撒点物按位置归入各自 sector。
            foreach (var terrain in terrains)
            {
                Log($"切块：{terrain.name} @ {terrain.transform.position}");
                SECTR_TerrainWindow.SectorizeTerrain(
                    terrain,
                    SectorsPerSide,   // sectorsWidth
                    SectorsPerSide,   // sectorsLength
                    1,                // sectorsHeight
                    true,             // splitTerrain：真的把地形切成独立 TerrainData
                    false,            // createPortalGeo
                    true,             // includeStatic
                    false,            // includeDynamic（排除水面）
                    true);            // sortGaiaObjects
            }

            int sectorCount = SECTR_Sector.All.Count;
            int expected = terrains.Length * SectorsPerSide * SectorsPerSide;
            Log($"切块后 SECTR Sector 数量 = {sectorCount}（预期 {expected}）");
            if (sectorCount != expected)
            {
                Abort($"Sector 数量不符，预期 {expected} 实际 {sectorCount}");
                return;
            }

            EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
            EditorSceneManager.SaveScene(EditorSceneManager.GetActiveScene());
            SECTR_VC.WaitForVC();
            Log("切块结果已保存");

            // 导出：每个 sector 变成一个独立 .unity 场景文件
            Log("开始导出 chunk…");
            SECTR_StreamExport.ExportSceneChunks();
            SECTR_VC.WaitForVC();

            int frozen = SECTR_Sector.All.Count(s => s.Frozen);
            Log($"导出完成，已冻结（已导出）的 Sector 数量 = {frozen}");
            if (frozen != expected)
            {
                Abort($"导出数量不符，预期 {expected} 实际 {frozen}");
                return;
            }

            EditorSceneManager.SaveScene(EditorSceneManager.GetActiveScene());
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Log("SECTR 切块 + 导出完成");
            EditorApplication.Exit(0);
        }
        catch (Exception ex)
        {
            Debug.LogError($"{Tag} 异常：{ex}");
            EditorApplication.Exit(1);
        }
    }

    private static void Abort(string reason)
    {
        Debug.LogError($"{Tag} 中止：{reason}");
        EditorApplication.Exit(1);
    }

    private static void Log(string message)
    {
        Debug.Log($"{Tag} {message}");
    }
}
