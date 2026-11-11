using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using YooAsset.Editor;

namespace FishDemo.EditorTools
{
    /// <summary>
    /// YooAsset 资源收集器初始化工具。
    ///
    /// 为什么用代码建配置而不是手点 UI：
    /// 1. 配置可版本化，新人拉代码即得，不依赖口头传授。
    /// 2. 避免多人各自点出不同的包名/规则，导致构建产物不一致。
    /// 3. 项目结构变化时可重跑，快速同步。
    ///
    /// 用法：菜单 YooAsset/FishDemo/初始化资源收集配置
    /// </summary>
    public static class YooAssetSetup
    {
        private const string SettingAssetPath = "Assets/YooAssetSettings.asset";
        private const string CollectorSettingPath = "Assets/BundleCollectorSetting.asset";

        /// <summary>资源包名，必须与 YooAssetInitializer.DefaultPackageName 一致。</summary>
        private const string PackageName = "DefaultPackage";

        [MenuItem("YooAsset/FishDemo/初始化资源收集配置", false, 1)]
        public static void SetupCollector()
        {
            var setting = LoadOrCreate<BundleCollectorSetting>(CollectorSettingPath);

            // 已有同名包则先移除，保证可重复执行
            setting.Packages.RemoveAll(p => p.PackageName == PackageName);

            var package = new BundleCollectorPackage
            {
                PackageName = PackageName,
                PackageDesc = "主资源包：地形/植被/建筑/角色等大世界流式资源",
                EnableAddressable = true,
                // 定位地址统一小写，避免大小写不一致导致真机加载失败
                LocationToLower = true,
                IncludeAssetGUID = false,
                // 自动收集 shader，避免变体在构建时丢失
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

        /// <summary>
        /// 向包中添加一个分组。目录不存在时跳过，避免生成无效收集器。
        /// </summary>
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
                // 用「文件夹名_文件名」而不是纯文件名。
                //
                // 原因：Assets/Scenes 下场景与烘焙数据同名——
                //   World/Chunks/World_SECTR Terrain_0_0-… Sector.unity
                //   World/GrassData/World_SECTR Terrain_0_0-… Sector.bytes
                // 纯文件名会让两者得到同一个 Address，YooAsset 收集阶段直接抛
                // "Address already exists"（BundleCollector.cs:226），模拟构建整体失败。
                // 加文件夹前缀后地址唯一，且仍然可读、可从地址反推来源目录。
                AddressRuleName = nameof(AddressByFolderAndFileName),
                PackRuleName = packRule,
                FilterRuleName = nameof(CollectAll),
            });

            package.Groups.Add(group);
        }

        /// <summary>加载配置资产，不存在则创建。</summary>
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
