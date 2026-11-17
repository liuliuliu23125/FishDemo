using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class SectrPipeline
{
    private const int SectorsPerSide = 2;
    private const string WorldScenePath = "Assets/Scenes/World.unity";
    private const string Tag = "[SectrPipeline]";

    public static void SectorizeAndExport()
    {
        try
        {
            EditorSceneManager.OpenScene(WorldScenePath, OpenSceneMode.Single);
            Log($"已打开场景 {WorldScenePath}");

            var terrains = UnityEngine.Object.FindObjectsOfType<Terrain>();
            Log($"待切块地形数 = {terrains.Length}");

            if (terrains.Length == 0)
            {
                Abort("场景里没有 Terrain");
                return;
            }

            foreach (var terrain in terrains)
            {
                Log($"切块：{terrain.name} @ {terrain.transform.position}");
                SECTR_TerrainWindow.SectorizeTerrain(
                    terrain,
                    SectorsPerSide,
                    SectorsPerSide,
                    1,
                    true,
                    false,
                    true,
                    false,
                    true);
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
