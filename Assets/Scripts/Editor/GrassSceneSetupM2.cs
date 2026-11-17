using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using FishDemo.Rendering;

namespace FishDemo.EditorTools
{
    public static class GrassSceneSetupM2
    {
        private const string Tag = "[GrassSceneM2]";
        private const string ResultPath = "Logs/Grass/scene-setup-m2.txt";
        private const string WorldScenePath = "Assets/Scenes/World.unity";
        private const string GrassDataDir = "Assets/Scenes/World/GrassData";
        private const string NodeName = "GrassBRG (M1)";

        [MenuItem("FishDemo/Rendering/⑪ 场景草接入烘焙数据（M2）", false, 113)]
        public static void Run()
        {
            var sb = new StringBuilder();
            void W(string s) { Debug.Log($"{Tag} {s}"); sb.AppendLine(s); }

            string[] files = Directory.GetFiles(GrassDataDir, "*.bytes");
            if (files.Length == 0)
            {
                W("没有烘焙数据，先跑 GrassBaker.RunAll");
                Flush(sb); return;
            }

            var chunks = new List<GrassChunkData>(files.Length);
            long totalInstances = 0;
            long totalBytes = 0;
            foreach (var f in files)
            {
                var d = GrassChunkData.Read(File.ReadAllBytes(f), Path.GetFileNameWithoutExtension(f));
                chunks.Add(d);
                totalInstances += d.Instances.Length;
                totalBytes += new FileInfo(f).Length;
            }
            W($"加载 {chunks.Count} 个 chunk：{totalInstances:N0} 实例，{totalBytes / 1024f / 1024f:F0} MB");

            var scene = EditorSceneManager.OpenScene(WorldScenePath, OpenSceneMode.Single);
            var mgr = FindOrCreateManager(scene, W);
            if (mgr == null) { Flush(sb); return; }

            mgr.SetGrassDataDir(GrassDataDir);

            if (!mgr.IsReady)
            {
                W("初始化失败");
                Flush(sb); return;
            }

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            W($"已写入草数据目录 {GrassDataDir} 并保存场景。总实例 {mgr.InstanceCount:N0}");
            Flush(sb);
        }

        private static GrassBRGManager FindOrCreateManager(
            Scene scene, System.Action<string> w)
        {
            foreach (var root in scene.GetRootGameObjects())
            {
                if (root.name == NodeName)
                    return root.GetComponent<GrassBRGManager>();
            }

            w($"场景里没有 {NodeName} 节点，新建");
            var go = new GameObject(NodeName);
            var mgr = go.AddComponent<GrassBRGManager>();
            mgr.ConfigureForValidation(1000, 0.6f, 0.6f);
            return mgr;
        }

        private static void Flush(StringBuilder sb)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ResultPath));
            File.WriteAllText(ResultPath, sb.ToString());
            if (Application.isBatchMode) EditorApplication.Exit(0);
        }
    }
}
