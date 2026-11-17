using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using YooAsset.Editor;

namespace FishDemo.EditorTools
{
    public static class YooAssetSetup
    {
        private const string SettingAssetPath = "Assets/YooAssetSettings.asset";
        private const string CollectorSettingPath = "Assets/BundleCollectorSetting.asset";

        private const string PackageName = "DefaultPackage";

        [MenuItem("YooAsset/FishDemo/初始化资源收集配置", false, 1)]
        public static void SetupCollector()
        {
            var setting = LoadOrCreate<BundleCollectorSetting>(CollectorSettingPath);

            setting.Packages.RemoveAll(p => p.PackageName == PackageName);

            var package = new BundleCollectorPackage
            {
                PackageName = PackageName,
                PackageDesc = "主资源包：地形/植被/建筑/角色等大世界流式资源",
                EnableAddressable = true,
                LocationToLower = true,
                IncludeAssetGUID = false,
                AutoCollectShaders = true,
            };

            AddGroup(package, "Scene", "场景资源，按场景整包", "Assets/Scenes", nameof(PackDirectory));
            AddGroup(package, "Terrain", "地形分块 mesh，按目录打包便于流式加载", "Assets/Art/Terrain", nameof(PackDirectory));
            AddGroup(package, "Vegetation", "植被资源", "Assets/Art/Vegetation", nameof(PackDirectory));
            AddGroup(package, "Character", "角色资源", "Assets/Art/Character", nameof(PackDirectory));
            AddGroup(package, "Water", "水体相关", "Assets/Art/Water", nameof(PackDirectory));
            AddGroup(package, "UI", "UI 资源", "Assets/Art/UI", nameof(PackDirectory));

            setting.Packages.Add(package);

            EditorUtility.SetDirty(setting);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log($"[YooAsset] 已初始化资源收集配置，包名 {PackageName}，共 {package.Groups.Count} 个分组。");
        }

        private static void AddGroup(
            BundleCollectorPackage package,
            string groupName,
            string desc,
            string collectPath,
            string packRule)
        {
            if (!Directory.Exists(collectPath))
            {
                Debug.LogWarning($"[YooAsset] 目录不存在，跳过分组 {groupName}: {collectPath}");
                return;
            }

            var group = new BundleCollectorGroup
            {
                GroupName = groupName,
                GroupDesc = desc,
            };

            group.Collectors.Add(new BundleCollector
            {
                CollectPath = collectPath,
                AddressRuleName = nameof(AddressByFolderAndFileName),
                PackRuleName = packRule,
                FilterRuleName = nameof(CollectAll),
            });

            package.Groups.Add(group);
        }

        private static T LoadOrCreate<T>(string path) where T : ScriptableObject
        {
            var asset = AssetDatabase.LoadAssetAtPath<T>(path);
            if (asset != null)
            {
                return asset;
            }

            asset = ScriptableObject.CreateInstance<T>();
            AssetDatabase.CreateAsset(asset, path);
            AssetDatabase.SaveAssets();
            return asset;
        }
    }
}
