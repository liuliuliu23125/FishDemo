using System.Linq;
using UnityEditor;
using UnityEngine;
using Gaia;
using static Gaia.GaiaConstants;

namespace FishDemo.EditorTools
{
    public static class BiomeSpawner
    {
        private const string Tag = "[BiomeSpawner]";

        private const float SeaLevel = 60f;
        private const string BiomePresetPath =
            "Assets/Procedural Worlds/Packages - Install/Gaia Pro Assets and Biomes/Biomes/Alpine Meadow.asset";

        private static BiomeController s_biome;
        private static bool s_spawnStarted;

        public static bool BeginSpawn()
        {
            var preset = AssetDatabase.LoadAssetAtPath<BiomePreset>(BiomePresetPath);
            if (preset == null)
            {
                Debug.LogError($"{Tag} 找不到 Biome 预设：{BiomePresetPath}");
                return false;
            }

            foreach (var t in Terrain.activeTerrains)
            {
                if (!t.drawInstanced) t.drawInstanced = true;
            }

            float terrainHeight = Terrain.activeTerrains.FirstOrDefault()?.terrainData.size.y ?? 700f;
            FixTreeHeightMasksOnPreset(preset, SeaLevel, terrainHeight);

            Debug.Log($"{Tag} 创建 Biome：{preset.name}（自动灌入地表材质/树/草原型）");
            s_biome = preset.CreateBiome(true);
            if (s_biome == null)
            {
                Debug.LogError($"{Tag} CreateBiome 返回 null");
                return false;
            }

            var layers = Terrain.activeTerrains.FirstOrDefault()?.terrainData.terrainLayers;
            Debug.Log($"{Tag} 原型灌入后，首个地形贴图层数 = {(layers != null ? layers.Length : 0)}");

            s_biome.UpdateSpawnerList();
            var activeSpawners = s_biome.m_autoSpawners.FindAll(x => x != null && x.isActive);
            Debug.Log($"{Tag} 活跃 Spawner 数 = {activeSpawners.Count}");
            if (activeSpawners.Count == 0)
            {
                Debug.LogError($"{Tag} 没有活跃 Spawner，无法撒点");
                return false;
            }

            s_biome.m_autoSpawnerArea = AutoSpawnerArea.World;
            float range = s_biome.m_settings.m_range;

            Debug.Log($"{Tag} 启动 HandleAutoSpawnerStack，range={range}，worldSpawn=true");
            Spawner.HandleAutoSpawnerStack(activeSpawners, s_biome.transform, range, true, s_biome.m_settings);
            s_spawnStarted = true;
            return true;
        }

        public static bool TickBiomeSpawn()
        {
            if (!s_spawnStarted || s_biome == null) return false;

            var first = s_biome.m_autoSpawners.FirstOrDefault(x => x != null && x.spawner != null);
            if (first != null && first.spawner.m_spawnComplete)
            {
                s_spawnStarted = false;
                LogResultStats();
                return true;
            }
            return false;
        }

        private static void LogResultStats()
        {
            int trees = 0, details = 0;
            foreach (var t in Terrain.activeTerrains)
            {
                if (t.terrainData == null) continue;
                trees += t.terrainData.treeInstanceCount;
                details += t.terrainData.detailPrototypes != null ? t.terrainData.detailPrototypes.Length : 0;
            }
            int terrainLayers = Terrain.activeTerrains.FirstOrDefault()?.terrainData.terrainLayers?.Length ?? 0;
            var goSpawns = GameObject.Find("Gaia Game Object Spawns");
            int goChildren = goSpawns != null ? goSpawns.transform.childCount : 0;
            Debug.Log($"{Tag} 撒点完成：树实例={trees}，贴图层={terrainLayers}，" +
                      $"细节原型={details}，GameObject 撒点组={goChildren}");
        }

        private static void FixTreeHeightMasksOnPreset(BiomePreset preset, float seaLevel, float terrainHeight)
        {
            int scanned = 0, fixedRules = 0;
            if (preset.m_spawnerPresetList == null) return;

            float landMin = seaLevel + 2f;
            float landMax = terrainHeight * 0.95f;

            foreach (var entry in preset.m_spawnerPresetList)
            {
                var ss = entry?.m_spawnerSettings;
                if (ss?.m_spawnerRules == null) continue;

                foreach (var rule in ss.m_spawnerRules)
                {
                    if (rule?.m_imageMasks == null) continue;
                    scanned++;
                    if (rule.m_resourceType != GaiaConstants.SpawnerResourceType.TerrainTree) continue;

                    bool touched = false;
                    foreach (var m in rule.m_imageMasks)
                    {
                        if (m == null || m.m_operation != ImageMaskOperation.HeightMask) continue;

                        m.m_heightMaskType = HeightMaskType.WorldSpace;
                        m.HeightMaskUnit = HeightMaskUnit.Meter;
                        m.m_absoluteHeightMin = landMin;
                        m.m_absoluteHeightMax = landMax;
                        m.m_invert = false;
                        m.m_blendMode = ImageMaskBlendMode.Multiply;
                        touched = true;
                    }

                    if (!touched)
                    {
                        var list = rule.m_imageMasks.Where(m => m != null).ToList();
                        list.Add(new ImageMask
                        {
                            m_operation = ImageMaskOperation.HeightMask,
                            m_blendMode = ImageMaskBlendMode.Multiply,
                            m_heightMaskType = HeightMaskType.WorldSpace,
                            HeightMaskUnit = HeightMaskUnit.Meter,
                            m_absoluteHeightMin = landMin,
                            m_absoluteHeightMax = landMax,
                        });
                        rule.m_imageMasks = list.ToArray();
                        touched = true;
                    }

                    if (touched)
                    {
                        fixedRules++;
                        EditorUtility.SetDirty(ss);
                    }
                }
            }
            Debug.Log($"{Tag} 树 HeightMask 对齐：扫描 {scanned} 条规则，修正 {fixedRules} 条，" +
                      $"有效陆地高度=[{landMin:F1}, {landMax:F1}]m");
        }
    }
}
