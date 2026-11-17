using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class TreeDensityReduce
{
    private const string Tag = "[TreeDensity]";
    private const string WorldScenePath = "Assets/Scenes/World.unity";
    private const string ResultPath = "Logs/tree-density-reduce.txt";
    private const string BackupDir = "Tools/tree-instance-backup";

    public static void Apply10() { Apply(10, "apply-1of10"); }
    public static void Apply5() { Apply(5, "apply-1of5"); }
    public static void Apply3() { Apply(3, "apply-1of3"); }

    private static void Apply(int divisor, string mode)
    {
        var sb = new StringBuilder();
        void W(string s) { Debug.Log($"{Tag} {s}"); sb.AppendLine(s); }

        Directory.CreateDirectory(BackupDir);
        W($"模式 = {mode}（每 {divisor} 棵保留 1 棵）");

        var scene = EditorSceneManager.OpenScene(WorldScenePath, OpenSceneMode.Single);
        var chunks = Object.FindObjectsOfType<SECTR_Chunk>().OrderBy(c => c.NodeName).ToList();
        W($"活跃 chunk = {chunks.Count}");

        long beforeTotal = 0, afterTotal = 0;
        int changed = 0;

        foreach (var c in chunks)
        {
            if (string.IsNullOrEmpty(c.NodeName) || !File.Exists(c.NodeName)) continue;
            var cs = EditorSceneManager.OpenScene(c.NodeName, OpenSceneMode.Additive);

            foreach (var t in Object.FindObjectsOfType<Terrain>())
            {
                if (t.gameObject.scene != cs) continue;
                var td = t.terrainData;
                if (td == null) continue;

                var insts = td.treeInstances;
                if (insts.Length == 0) continue;
                beforeTotal += insts.Length;

                string bak = Path.Combine(BackupDir, Sanitize(td.name) + ".bytes");
                if (!File.Exists(bak)) WriteInstances(bak, insts);

                var rng = new System.Random(StableHash(td.name));
                var idx = Enumerable.Range(0, insts.Length).ToArray();
                for (int i = idx.Length - 1; i > 0; i--)
                {
                    int j = rng.Next(i + 1);
                    (idx[i], idx[j]) = (idx[j], idx[i]);
                }
                int keep = Mathf.Max(1, insts.Length / divisor);
                var kept = new TreeInstance[keep];
                for (int i = 0; i < keep; i++) kept[i] = insts[idx[i]];

                td.treeInstances = kept;
                EditorUtility.SetDirty(td);
                afterTotal += kept.Length;
                changed++;
            }

            EditorSceneManager.MarkSceneDirty(cs);
            EditorSceneManager.SaveScene(cs);
            EditorSceneManager.CloseScene(cs, true);
        }

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        W($"处理 Terrain = {changed}");
        W($"树实例总数：{beforeTotal:N0} → {afterTotal:N0}（{afterTotal * 100.0 / Mathf.Max(1, beforeTotal):F1}%）");
        W($"备份目录 = {BackupDir}（RestoreAll 可还原）");
        W($"==== 结论：{(changed > 0 ? "PASS" : "FAIL")} ====");

        Directory.CreateDirectory("Logs");
        File.WriteAllText(ResultPath, sb.ToString());
        if (Application.isBatchMode) EditorApplication.Exit(changed > 0 ? 0 : 2);
    }

    public static void RestoreAll()
    {
        var sb = new StringBuilder();
        void W(string s) { Debug.Log($"{Tag} {s}"); sb.AppendLine(s); }

        if (!Directory.Exists(BackupDir)) { W($"没有备份目录 {BackupDir}"); EditorApplication.Exit(3); return; }

        EditorSceneManager.OpenScene(WorldScenePath, OpenSceneMode.Single);
        var chunks = Object.FindObjectsOfType<SECTR_Chunk>().OrderBy(c => c.NodeName).ToList();

        long total = 0;
        int restored = 0;
        foreach (var c in chunks)
        {
            if (string.IsNullOrEmpty(c.NodeName) || !File.Exists(c.NodeName)) continue;
            var cs = EditorSceneManager.OpenScene(c.NodeName, OpenSceneMode.Additive);

            foreach (var t in Object.FindObjectsOfType<Terrain>())
            {
                if (t.gameObject.scene != cs) continue;
                var td = t.terrainData;
                if (td == null) continue;

                string bak = Path.Combine(BackupDir, Sanitize(td.name) + ".bytes");
                if (!File.Exists(bak)) continue;

                var insts = ReadInstances(bak);
                td.treeInstances = insts;
                EditorUtility.SetDirty(td);
                total += insts.Length;
                restored++;
            }

            EditorSceneManager.MarkSceneDirty(cs);
            EditorSceneManager.SaveScene(cs);
            EditorSceneManager.CloseScene(cs, true);
        }

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        W($"还原 Terrain = {restored}，树实例总数 = {total:N0}");
        File.WriteAllText(ResultPath, sb.ToString());
        if (Application.isBatchMode) EditorApplication.Exit(0);
    }

    private static void WriteInstances(string path, TreeInstance[] arr)
    {
        using var fs = new FileStream(path, FileMode.Create);
        using var bw = new BinaryWriter(fs);
        bw.Write(arr.Length);
        foreach (var t in arr)
        {
            bw.Write(t.position.x); bw.Write(t.position.y); bw.Write(t.position.z);
            bw.Write(t.widthScale); bw.Write(t.heightScale);
            bw.Write(t.rotation);
            bw.Write(t.color.r); bw.Write(t.color.g); bw.Write(t.color.b); bw.Write(t.color.a);
            bw.Write(t.lightmapColor.r); bw.Write(t.lightmapColor.g);
            bw.Write(t.lightmapColor.b); bw.Write(t.lightmapColor.a);
            bw.Write(t.prototypeIndex);
        }
    }

    private static TreeInstance[] ReadInstances(string path)
    {
        using var fs = new FileStream(path, FileMode.Open);
        using var br = new BinaryReader(fs);
        int n = br.ReadInt32();
        var arr = new TreeInstance[n];
        for (int i = 0; i < n; i++)
        {
            var t = new TreeInstance();
            t.position = new Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle());
            t.widthScale = br.ReadSingle();
            t.heightScale = br.ReadSingle();
            t.rotation = br.ReadSingle();
            t.color = new Color(br.ReadSingle(), br.ReadSingle(), br.ReadSingle(), br.ReadSingle());
            t.lightmapColor = new Color(br.ReadSingle(), br.ReadSingle(), br.ReadSingle(), br.ReadSingle());
            t.prototypeIndex = br.ReadInt32();
            arr[i] = t;
        }
        return arr;
    }

    private static string Sanitize(string s)
    {
        foreach (var ch in Path.GetInvalidFileNameChars()) s = s.Replace(ch, '_');
        return s;
    }

    private static int StableHash(string s)
    {
        unchecked
        {
            int h = 17;
            foreach (char c in s) h = h * 31 + c;
            return h;
        }
    }
}
