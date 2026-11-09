using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// 清理历次重跑产生的孤儿资产。
///
/// 每执行一次世界生成都会新建一个带时间戳的 Gaia session 和一批 TerrainSplits /
/// Chunk 场景。多次重跑后，旧批次（如 131434）的资产仍留在磁盘上，虽然主场景已
/// 不再引用它们，但会占用空间、干扰排查（审计时新旧批次混在一起）。
/// 这里只保留主场景实际引用的 chunk 场景及其 TerrainData，其余按批次前缀删除。
///
/// 安全前提：先解析当前主场景里 36 个 SECTR_Chunk 的 NodeName，得到"活跃批次"标识，
/// 只删除不匹配的批次，绝不盲删整个目录。
/// </summary>
public static class CleanupOrphanAssets
{
    private const string Tag = "[Cleanup]";

    public static void Run()
    {
        var sb = new StringBuilder();
        void W(string s) { Debug.Log($"{Tag} {s}"); sb.AppendLine(s); }

        var scene = EditorSceneManager.OpenScene("Assets/Scenes/World.unity", OpenSceneMode.Single);
        var chunks = Object.FindObjectsOfType<SECTR_Chunk>();
        W($"主场景活跃 chunk 数 = {chunks.Length}");

        // 从 NodeName 里提取活跃批次标识（形如 "20261109 - 140021"）
        var activeTokens = new System.Collections.Generic.HashSet<string>();
        foreach (var c in chunks)
        {
            var m = System.Text.RegularExpressions.Regex.Match(c.NodeName, @"(\d{8} - \d{6})");
            if (m.Success) activeTokens.Add(m.Groups[1].Value);
        }
        W($"活跃批次标识：{string.Join(", ", activeTokens)}");

        // 收集主场景实际引用的 chunk 场景文件名（basename）
        var activeChunkNames = new System.Collections.Generic.HashSet<string>(
            chunks.Select(c => Path.GetFileNameWithoutExtension(c.NodeName)));

        // 1. 清理 Chunks 目录下不属于活跃批次的场景
        string chunkDir = "Assets/Scenes/World/Chunks";
        int delChunks = 0;
        if (Directory.Exists(chunkDir))
        {
            foreach (string f in Directory.GetFiles(chunkDir, "*.unity"))
            {
                string bn = Path.GetFileNameWithoutExtension(f);
                if (!activeChunkNames.Contains(bn))
                {
                    AssetDatabase.DeleteAsset(f.Replace('\\', '/'));
                    delChunks++;
                }
            }
        }
        W($"删除非活跃 chunk 场景 {delChunks} 个");

        // 2. 清理 TerrainSplits 下非活跃批次的 TerrainData
        string splitDir = "Assets/Scenes/World/TerrainSplits";
        int delSplits = 0;
        if (Directory.Exists(splitDir))
        {
            foreach (string f in Directory.GetFiles(splitDir, "*.asset"))
            {
                string bn = Path.GetFileName(f);
                bool isActive = activeTokens.Any(t => bn.Contains(t));
                if (!isActive)
                {
                    AssetDatabase.DeleteAsset(f.Replace('\\', '/'));
                    delSplits++;
                }
            }
        }
        W($"删除非活跃 TerrainData {delSplits} 个");

        // 3. 清理旧 session 目录（保留活跃批次对应 session）
        string sessionsDir = "Assets/Gaia User Data/Sessions";
        int delSessions = 0;
        if (Directory.Exists(sessionsDir))
        {
            foreach (string d in Directory.GetDirectories(sessionsDir))
            {
                string dn = Path.GetFileName(d);
                bool isActive = activeTokens.Any(t => dn.Contains(t));
                if (!isActive)
                {
                    AssetDatabase.DeleteAsset(d.Replace('\\', '/'));
                    delSessions++;
                }
            }
        }
        W($"删除非活跃 session 目录 {delSessions} 个");

        AssetDatabase.Refresh();
        W("清理完成");
        Directory.CreateDirectory("Logs/Gaia");
        File.WriteAllText("Logs/Gaia/cleanup.txt", sb.ToString());
        EditorApplication.Exit(0);
    }
}
