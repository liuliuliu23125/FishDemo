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
    /// <summary>
    /// 把 World.unity 里的 GrassBRG (M1) 节点接入烘焙数据：
    /// 加载第一个 chunk 的 .bytes（作为演示），替换硬编码方阵。
    ///
    /// M2 阶段单节点单 chunk 是演示性质；M3 才做按 SECTR 流式的多 chunk 管理
    /// （GrassChunkManager），届时本节点的职责会被拆分。
    ///
    /// 用法：-executeMethod FishDemo.EditorTools.GrassSceneSetupM2.Run
    /// 菜单：FishDemo/Rendering/⑪ 场景草接入烘焙数据（M2）
    /// </summary>
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

            // 加载全部烘焙产物（36 个 chunk）。
            //
            // 早先只加载 files[0]，导致「只有一个 chunk 有草」——这是实现缺口不是配置问题。
            // 现在全量加载，实例合并成一个连续数组，batch 按 16MB 容量自动切分。
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

            // 写「数据目录」而不是注入实例数组。
            //
            // 为什么改：GrassChunkData 不可序列化（无 [Serializable]，且 1265 万实例
            // 也放不进场景文件）。早先这里直接 SetChunkDataList 再 SaveScene，
            // 日志显示"已接入"，但数据根本没落盘——重开工程就退回 M1 合成方阵。
            // 改存路径后，组件在 OnEnable 时自己读盘，配置真正持久化。
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
