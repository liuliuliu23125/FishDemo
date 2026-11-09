using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace FishDemo.EditorTools
{
    /// <summary>
    /// 把工程从 URP 2D 模板切到 3D 通用渲染器，并重建一个 3D 示例场景。
    ///
    /// 为什么用代码而不是手点 UI：
    /// 1. Pipeline 资产、Renderer 资产、场景都是二进制序列化风险点，脚本可重复执行、可 review。
    /// 2. `UniversalRP.asset` 里 m_RendererDataList 指向的是 Renderer2D（模板遗留错配），
    ///    必须显式换成 UniversalRendererData，否则 3D 物体在 2D Renderer 下不可见。
    /// 3. 这些字段是 internal，只能用 SerializedObject 改，正好集中在一处。
    ///
    /// 用法：
    ///   - 菜单 FishDemo/Rendering/切换到 3D 通用渲染器
    ///   - 或批处理：Unity.exe -batchmode -quit -executeMethod FishDemo.EditorTools.UrpPipelineSetup.SetupAll
    /// </summary>
    public static class UrpPipelineSetup
    {
        private const string PipelineAssetPath = "Assets/Settings/UniversalRP.asset";
        private const string RendererAssetPath = "Assets/Settings/UniversalRenderer.asset";
        private const string SampleScenePath = "Assets/Scenes/SampleScene.unity";

        /// <summary>URP 包内默认后处理资源路径，与 PostProcessData.GetDefaultPostProcessData() 内部用的相同。</summary>
        private const string DefaultPostProcessDataPath =
            "Packages/com.unity.render-pipelines.universal/Runtime/Data/PostProcessData.asset";

        /// <summary>URP 包根路径，ResourceReloader 会按它补全 shader 引用。</summary>
        private const string UrpPackagePath = "Packages/com.unity.render-pipelines.universal";

        [MenuItem("FishDemo/Rendering/切换到 3D 通用渲染器", false, 100)]
        public static void SetupAll()
        {
            if (!File.Exists(PipelineAssetPath))
            {
                Debug.LogError($"[UrpPipelineSetup] 找不到管线资产：{PipelineAssetPath}");
                return;
            }

            // 关键：施工期间必须摘掉激活管线。
            // 原 2D 模板的管线资产指向已删除的 Renderer2D，此时只要有任何资产操作触发管线重建，
            // URP 会在 UniversalRenderPipelineAsset.CreatePipeline() 里对 null renderer 解引用
            // （源码 :738 只判了版本不匹配分支，没有 null 保护），抛出空引用并中断后续流程。
            var previousPipeline = GraphicsSettings.defaultRenderPipeline;
            GraphicsSettings.defaultRenderPipeline = null;

            try
            {
                // 编辑器行为模式决定 NewScene 生成 2D 还是 3D 默认物体：
                // 2D 模式给正交相机、不给平行光。模板遗留值是 2D。
                EditorSettings.defaultBehaviorMode = EditorBehaviorMode.Mode3D;

                var rendererData = EnsureUniversalRendererData();
                if (rendererData == null)
                    return;

                AssignRendererToPipeline(rendererData);
                RebuildSampleSceneAs3D();

                // 恢复激活管线，指向新的 3D Renderer。
                GraphicsSettings.defaultRenderPipeline =
                    AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(PipelineAssetPath);
            }
            finally
            {
                // 失败时也不能把工程留在"无管线"状态：退回原值好过直接崩掉渲染。
                if (GraphicsSettings.defaultRenderPipeline == null)
                    GraphicsSettings.defaultRenderPipeline = previousPipeline;
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log("[UrpPipelineSetup] 已切换到 3D 通用渲染器，并重建 SampleScene。");
        }

        /// <summary>
        /// 创建或复用 UniversalRendererData。
        /// 注意 postProcessData 没有 [Reload] 特性，ResourceReloader 不会补它，必须手动赋值，
        /// 否则后处理（Bloom/Tonemapping 等 Volumume 效果）全部失效。
        /// </summary>
        private static UniversalRendererData EnsureUniversalRendererData()
        {
            var existing = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(RendererAssetPath);
            if (existing != null)
                return existing;

            var data = ScriptableObject.CreateInstance<UniversalRendererData>();

            // 与 URP 官方 CreateRendererData 一致：先挂默认 PostProcessData，再补 shader 引用。
            var postProcessData = AssetDatabase.LoadAssetAtPath<PostProcessData>(DefaultPostProcessDataPath);
            if (postProcessData == null)
            {
                Debug.LogError($"[UrpPipelineSetup] 找不到默认后处理资源：{DefaultPostProcessDataPath}");
                Object.DestroyImmediate(data);
                return null;
            }

            data.postProcessData = postProcessData;
            ResourceReloader.ReloadAllNullIn(data, UrpPackagePath);

            Directory.CreateDirectory(Path.GetDirectoryName(RendererAssetPath));
            AssetDatabase.CreateAsset(data, RendererAssetPath);
            AssetDatabase.SaveAssets();
            return data;
        }

        /// <summary>
        /// 把管线资产的渲染器列表指向 3D Renderer。
        /// m_RendererType / m_RendererDataList 均为 internal 序列化字段，只能走 SerializedObject。
        /// </summary>
        private static void AssignRendererToPipeline(UniversalRendererData rendererData)
        {
            var pipeline = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(PipelineAssetPath);
            if (pipeline == null)
            {
                Debug.LogError($"[UrpPipelineSetup] {PipelineAssetPath} 不是 UniversalRenderPipelineAsset。");
                return;
            }

            var so = new SerializedObject(pipeline);

            var rendererType = so.FindProperty("m_RendererType");
            if (rendererType != null)
            {
                // RendererType 是 UnityEngine.Rendering.Universal 命名空间下的顶层枚举，不在管线资产类内。
                // 序列化层存的是 int，直接用枚举强转比依赖 enumValueIndex 更稳。
                rendererType.intValue = (int)RendererType.UniversalRenderer;
            }

            var list = so.FindProperty("m_RendererDataList");
            if (list != null)
            {
                list.arraySize = 1;
                list.GetArrayElementAtIndex(0).objectReferenceValue = rendererData;
            }

            var defaultIndex = so.FindProperty("m_DefaultRendererIndex");
            if (defaultIndex != null)
                defaultIndex.intValue = 0;

            so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(pipeline);
            AssetDatabase.SaveAssets();
        }

        /// <summary>
        /// 用 3D 默认物体（透视相机 + 平行光）重建示例场景。
        /// 覆盖写入而不是新建文件，是为了保住 .meta 里的 guid ——
        /// EditorBuildSettings.asset 按 guid 引用该场景，换 guid 会让构建列表失效。
        /// </summary>
        private static void RebuildSampleSceneAs3D()
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);

            var camera = Object.FindObjectOfType<Camera>();
            if (camera != null)
                camera.transform.SetPositionAndRotation(new Vector3(0f, 1f, -10f), Quaternion.identity);

            Directory.CreateDirectory(Path.GetDirectoryName(SampleScenePath));
            EditorSceneManager.SaveScene(scene, SampleScenePath);
        }
    }
}
