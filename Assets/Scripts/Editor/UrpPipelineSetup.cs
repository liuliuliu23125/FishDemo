using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace FishDemo.EditorTools
{
    public static class UrpPipelineSetup
    {
        private const string PipelineAssetPath = "Assets/Settings/UniversalRP.asset";
        private const string RendererAssetPath = "Assets/Settings/UniversalRenderer.asset";
        private const string SampleScenePath = "Assets/Scenes/SampleScene.unity";

        private const string DefaultPostProcessDataPath =
            "Packages/com.unity.render-pipelines.universal/Runtime/Data/PostProcessData.asset";

        private const string UrpPackagePath = "Packages/com.unity.render-pipelines.universal";

        [MenuItem("FishDemo/Rendering/切换到 3D 通用渲染器", false, 100)]
        public static void SetupAll()
        {
            if (!File.Exists(PipelineAssetPath))
            {
                Debug.LogError($"[UrpPipelineSetup] 找不到管线资产：{PipelineAssetPath}");
                return;
            }

            var previousPipeline = GraphicsSettings.defaultRenderPipeline;
            GraphicsSettings.defaultRenderPipeline = null;

            try
            {
                EditorSettings.defaultBehaviorMode = EditorBehaviorMode.Mode3D;

                var rendererData = EnsureUniversalRendererData();
                if (rendererData == null)
                    return;

                AssignRendererToPipeline(rendererData);
                RebuildSampleSceneAs3D();

                GraphicsSettings.defaultRenderPipeline =
                    AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(PipelineAssetPath);
            }
            finally
            {
                if (GraphicsSettings.defaultRenderPipeline == null)
                    GraphicsSettings.defaultRenderPipeline = previousPipeline;
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log("[UrpPipelineSetup] 已切换到 3D 通用渲染器，并重建 SampleScene。");
        }

        private static UniversalRendererData EnsureUniversalRendererData()
        {
            var existing = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(RendererAssetPath);
            if (existing != null)
                return existing;

            var data = ScriptableObject.CreateInstance<UniversalRendererData>();

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
