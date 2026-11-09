using System.Linq;
using UnityEditor;
using UnityEngine;
using Gaia;
using static Gaia.GaiaConstants;

namespace FishDemo.EditorTools
{
    /// <summary>
    /// 用 Gaia Pro 的 Biome 预设给地形撒植被、上地表材质。
    ///
    /// ── 为什么需要它 ──
    /// 世界创建只生成空白地形网格；地貌印章只塑造高度。地表材质、树、草、石头
    /// 全部来自 Biome 预设。不跑 Biome，地形就是"有起伏但没有植被、没有贴图"的裸地。
    ///
    /// ── 为什么照抄 BiomeControllerEditor 的流程 ──
    /// Gaia 官方 GUI 的 "Spawn Biome" 按钮走的是同一套运行时 API
    /// （BiomeControllerEditor.cs:490-520）：
    ///   1. preset.CreateBiome(autoAssignPrototypes:true)
    ///        —— 创建 Spawner，并把 TerrainLayer / TreePrototype / DetailPrototype
    ///           灌进所有地形（这一步就解决了"地表材质缺失"）
    ///   2. UpdateSpawnerList()
    ///   3. Spawner.HandleAutoSpawnerStack(...)  —— 启动异步撒点协程
    ///   4. 轮询 m_spawnComplete 判断结束
    /// 撒点是异步协程（StartEditorUpdates 挂到 EditorApplication.update），
    /// 因此必须由状态机分帧等待，不能阻塞主线程。
    /// </summary>
    public static class BiomeSpawner
    {
        private const string Tag = "[BiomeSpawner]";

        /// <summary>海平面高度，必须与 WorldPipeline.SeaLevel 一致，否则植被高度判定会错位。</summary>
        private const float SeaLevel = 60f;
        private const string BiomePresetPath =
            "Assets/Procedural Worlds/Packages - Install/Gaia Pro Assets and Biomes/Biomes/Alpine Meadow.asset";

        private static BiomeController s_biome;
        private static bool s_spawnStarted;

        /// <summary>
        /// 创建 Biome 并启动撒点。返回后每帧调用 TickBiomeSpawn() 判断是否完成。
        /// </summary>
        public static bool BeginSpawn()
        {
            var preset = AssetDatabase.LoadAssetAtPath<BiomePreset>(BiomePresetPath);
            if (preset == null)
            {
                Debug.LogError($"{Tag} 找不到 Biome 预设：{BiomePresetPath}");
                return false;
            }

            // 撒点依赖 drawInstanced，否则部分 mask 结果错误（官方也会检查这一项）
            foreach (var t in Terrain.activeTerrains)
            {
                if (!t.drawInstanced) t.drawInstanced = true;
            }

            // 关键修复：在 CreateBiome 之前把预设里树规则的 HeightMask 对齐到本世界海拔。
            // 必须在 CreateBiome 之前，因为 CreateSpawner 会 Instantiate 预设规则的副本。
            // 原因详见 FixTreeHeightMasksOnPreset 注释。
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

            // 全地图撒点
            s_biome.m_autoSpawnerArea = AutoSpawnerArea.World;
            float range = s_biome.m_settings.m_range;

            Debug.Log($"{Tag} 启动 HandleAutoSpawnerStack，range={range}，worldSpawn=true");
            Spawner.HandleAutoSpawnerStack(activeSpawners, s_biome.transform, range, true, s_biome.m_settings);
            s_spawnStarted = true;
            return true;
        }

        /// <summary>
        /// 每帧检查撒点是否完成。true 表示全部完成。
        /// </summary>
        public static bool TickBiomeSpawn()
        {
            if (!s_spawnStarted || s_biome == null) return false;

            // 官方判定：首个 spawner 的 m_spawnComplete 为 true 即视为整栈完成
            // （HandleAutoSpawnerStack 内部按顺序串起所有 spawner）
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

        /// <summary>
        /// 把 Biome 预设里 TerrainTree 规则的 HeightMask 对齐到本世界的实际海拔。
        ///
        /// ── 问题根因（实测数据，非推测）──
        /// 预设里的 HeightMask 是按它原本设计的世界标定的，例如 Spruce_02 规则：
        ///   type=RelativeToSeaLevel, unit=Percent, seaLevel=25,
        ///   relMin=5.56, relMax=69.54, minWorldHeight=0, maxWorldHeight=114.94
        /// 也就是说它假设：世界总高约 115m、海平面 25m。
        /// 但本工程世界是：地形高 700m、海平面 60m、实测地表高度约 236~351m。
        /// 用 seaLevel=25 这个错误基准去算相对高度，算出的区间完全错位，
        /// 树的 fitness 达不到 minRequiredFitness(0.355~0.478)，于是 0 棵树。
        /// 实测对照：规则掩码保留→0 棵；清空掩码→单块地形 217558 棵。
        ///
        /// ── 修法 ──
        /// 把 HeightMask 改为 WorldSpace + Meter 的绝对高度区间
        /// （海平面+2m ～ 地形顶 95%），不再依赖预设的世界高度假设。
        /// 同时保留 NoiseMask / SlopeMask / ConcaveConvex 等形态掩码，
        /// 使分布仍然自然，而不是退化成"处处均匀撒树"。
        ///
        /// 只处理 TerrainTree（树/灌木/蕨）。草丛走 TerrainDetail、石头走 GameObject，
        /// 它们的阈值低（0.25）本身能正常产出，不动它们以降低风险。
        /// </summary>
        private static void FixTreeHeightMasksOnPreset(BiomePreset preset, float seaLevel, float terrainHeight)
        {
            int scanned = 0, fixedRules = 0;
            if (preset.m_spawnerPresetList == null) return;

            // 有效陆地高度区间：海平面以上 2m 到地形顶部 95%
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

                        // 改为绝对世界高度区间，去掉对预设世界高度的隐含假设
                        m.m_heightMaskType = HeightMaskType.WorldSpace;
                        m.HeightMaskUnit = HeightMaskUnit.Meter;
                        m.m_absoluteHeightMin = landMin;
                        m.m_absoluteHeightMax = landMax;
                        m.m_invert = false;
                        m.m_blendMode = ImageMaskBlendMode.Multiply;
                        touched = true;
                    }

                    // 该规则没有 HeightMask 时补一个，防止树长到水面以下
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
