using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

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

        var activeTokens = new System.Collections.Generic.HashSet<string>();
        foreach (var c in chunks)
        {
            var m = System.Text.RegularExpressions.Regex.Match(c.NodeName, @"(\d{8} - \d{6})");
            if (m.Success) activeTokens.Add(m.Groups[1].Value);
        }
        W($"活跃批次标识：{string.Join(", ", activeTokens)}");

        var activeChunkNames = new System.Collections.Generic.HashSet<string>(
            chunks.Select(c => Path.GetFileNameWithoutExtension(c.NodeName)));

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
