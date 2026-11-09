using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Gaia;
using static Gaia.GaiaConstants;

namespace FishDemo.EditorTools
{
    /// <summary>
    /// Gaia 大世界生成 + SECTR 流式分块的批处理流水线。
    ///
    /// ── 为什么用状态机而不是同步等待 ──
    /// Gaia 的世界创建不是同步调用：GaiaSessionManager.StartCreateWorld 会把一个
    /// IEnumerator 挂到 EditorApplication.update 上，靠编辑器主循环逐帧推进
    /// （GaiaSessionManager.cs:4159-4165、860-925）。
    /// 如果我们在 -executeMethod 里用 while 循环等待，就会占住主线程，
    /// 主循环无法继续派发 update，协程永远不前进 —— 实测会死等。
    /// 因此这里改用「入口立即返回 + update 回调驱动状态机」，让主循环自然运转。
    ///
    /// ── 为什么顺序是 地形 → 植被 → 水 → 切块 → 导出 ──
    /// 1. 植被必须在切块前：SectorizeTerrain 会把原 TerrainData 的高度/Alpha/Detail/Tree
    ///    按块复制到新 Terrain（SECTR_TerrainWindow.cs:230-330），并把 sector 范围内
    ///    的子物体收进 sector。切块后再撒点，点会落在已被 SetActive(false) 的原始地形上。
    /// 2. 水面必须在切块前且不进 chunk：海面是全局平面，分进每个 sector 会产生接缝。
    /// 3. m_createInScene 必须 false：那是 Gaia 自己的 TerrainLoader 流式方案，
    ///    与 SECTR 同时开启会造成双重加载。
    ///
    /// 用法：
    ///   Unity.exe -batchmode -projectPath <proj> -executeMethod FishDemo.EditorTools.WorldPipeline.RunStage -stage all -logFile <log>
    ///   注意：不要加 -quit，由脚本在完成时显式 EditorApplication.Exit(code)。
    /// </summary>
    public static class WorldPipeline
    {
        // ---------- 世界参数（已确认方案：3×3 tile，2×2 切块） ----------
        private const int TilesX = 3;
        private const int TilesZ = 3;
        private const int TileSize = 1024;
        private const int TileHeight = 700;
        private const int SeaLevel = 60;
        private const int SectorsPerSide = 2;

        private const string WorldScenePath = "Assets/Scenes/World.unity";
        private const string Tag = "[WorldPipeline]";

        /// <summary>
        /// 世界几何中心。
        ///
        /// Gaia 以世界原点为中心铺 tile 网格，所以 3×3×1024m 的实际范围是
        /// [-1536, +1536]，中心在 (0,0)，而不是 (1.5*tileSize)。
        /// 相机、水面都必须按这个中心摆放，否则会偏出世界。
        /// </summary>
        private static Vector3 WorldCenter => Vector3.zero;


        private static readonly TimeSpan StageTimeout = TimeSpan.FromMinutes(40);

        // 状态机
        private enum Stage { Idle, WorldCreating, Spawning, Finalizing }
        private static Stage s_stage = Stage.Idle;
        private static DateTime s_stageDeadline;
        private static bool s_worldCreated;

        [MenuItem("FishDemo/World/① 生成世界（地形+水）")]
        public static void RunAll()
        {
            BeginWorldCreation();
        }

        /// <summary>
        /// batchmode 入口。stage 参数保留以便将来分步执行。
        /// </summary>
        public static void RunStage()
        {
            BeginWorldCreation();
        }

        private static void BeginWorldCreation()
        {
            if (s_stage != Stage.Idle)
            {
                Log($"流水线已在运行中（{s_stage}），忽略重复调用");
                return;
            }

            try
            {
                PrepareScene();
                StartWorldCreation();
            }
            catch (Exception ex)
            {
                Debug.LogError($"{Tag} 启动失败：{ex}");
                EditorApplication.Exit(1);
            }
        }

        private static void PrepareScene()
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            var camGo = new GameObject("Main Camera");
            var cam = camGo.AddComponent<Camera>();
            cam.tag = "MainCamera";
            // 相机放在世界南侧边缘上方，俯视中心，便于观察整体地形
            camGo.transform.position = WorldCenter + new Vector3(0f, TileHeight * 0.6f, -TileSize * 1.2f);
            camGo.transform.rotation = Quaternion.Euler(25f, 0f, 0f);
            camGo.AddComponent<AudioListener>();

            var lightGo = new GameObject("Directional Light");
            var light = lightGo.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1.2f;
            light.shadows = LightShadows.Soft;
            lightGo.transform.rotation = Quaternion.Euler(50f, -30f, 0f);

            EditorSceneManager.SaveScene(scene, WorldScenePath);
            Log($"已建立起始场景 {WorldScenePath}");
        }

        private static void StartWorldCreation()
        {
            var settings = ScriptableObject.CreateInstance<WorldCreationSettings>();
            settings.m_targetSizePreset = EnvironmentSizePreset.Custom;
            settings.m_qualityPreset = EnvironmentTarget.Desktop;
            settings.m_xTiles = TilesX;
            settings.m_zTiles = TilesZ;
            settings.m_tileSize = TileSize;
            settings.m_tileHeight = TileHeight;
            settings.m_seaLevel = SeaLevel;

            // 关键：关掉 Gaia 自带 terrain 场景流式，改由 SECTR 负责
            settings.m_createInScene = false;
            settings.m_autoUnloadScenes = false;
            settings.m_applyFloatingPointFix = false;
            settings.m_recenterOnUpdate = true;
            settings.m_addLoadingScreen = false;

            GaiaSettings gaiaSettings = GaiaUtils.GetGaiaSettings();
            GaiaDefaults defaults = UnityEngine.Object.Instantiate(gaiaSettings.m_currentDefaults);
            defaults.m_heightmapResolution = 1025;
            defaults.m_controlTextureResolution = 1024;
            defaults.m_baseMapSize = 1024;
            defaults.m_detailResolution = 1024;
            defaults.m_detailResolutionPerPatch = 8;
            defaults.m_terrainSize = TileSize;
            defaults.m_terrainHeight = TileHeight;
            settings.m_gaiaDefaults = defaults;

            s_worldCreated = false;
            GaiaSessionManager.OnWorldCreated += HandleWorldCreated;
            EditorApplication.update += Tick;

            s_stage = Stage.WorldCreating;
            s_stageDeadline = DateTime.UtcNow + StageTimeout;

            Log($"开始生成地形：{TilesX}×{TilesZ} tiles，每块 {TileSize}m，高度 {TileHeight}m，海平面 {SeaLevel}");
            bool ok = GaiaSessionManager.CreateOrUpdateWorld(settings, true);
            Log($"CreateOrUpdateWorld 返回 {ok}");
            if (!ok)
            {
                Abort("Gaia 拒绝执行世界创建");
            }
        }

        private static void HandleWorldCreated()
        {
            s_worldCreated = true;
        }

        /// <summary>
        /// 每帧驱动状态机。此处只做轻量判断，绝不阻塞主线程。
        /// </summary>
        private static void Tick()
        {
            try
            {
                if (DateTime.UtcNow > s_stageDeadline)
                {
                    Abort($"阶段 {s_stage} 超时");
                    return;
                }

                switch (s_stage)
                {
                    case Stage.WorldCreating:
                        if (!s_worldCreated)
                        {
                            return;
                        }
                        Log("世界创建回调已触发");
                        VerifyTerrains();

                        // 关键：创建地形只是建了空白网格，高度恒为 0。
                        // 必须显式应用 Gaia 印章才能得到真实起伏地貌。
                        Log("开始应用地貌印章（塑造起伏）…");
                        bool shaped = TerrainStamper.ApplyBiomeStamps();
                        if (!shaped)
                        {
                            throw new InvalidOperationException("地貌印章应用失败，地形仍为平地");
                        }
                        Log("地貌塑形完成");

                        // 关键：Biome 撒点会灌入地表材质并撒树/草/石头。
                        // 撒点是异步协程，进入 Spawning 阶段分帧等待。
                        Log("开始 Biome 撒点（地表材质 + 植被）…");
                        if (!BiomeSpawner.BeginSpawn())
                        {
                            throw new InvalidOperationException("Biome 撒点启动失败");
                        }
                        s_stage = Stage.Spawning;
                        s_stageDeadline = DateTime.UtcNow + StageTimeout;
                        break;

                    case Stage.Spawning:
                        if (!BiomeSpawner.TickBiomeSpawn())
                        {
                            return;   // 还没撒完，继续等
                        }
                        Log("Biome 撒点完成");
                        SetupWater();
                        s_stage = Stage.Finalizing;
                        break;

                    case Stage.Finalizing:
                        EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
                        EditorSceneManager.SaveScene(EditorSceneManager.GetActiveScene(), WorldScenePath);
                        AssetDatabase.SaveAssets();
                        AssetDatabase.Refresh();
                        Log($"世界生成完成：{WorldScenePath}");
                        Complete(0);
                        break;
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"{Tag} Tick 异常：{ex}");
                Abort("Tick 异常");
            }
        }

        private static void VerifyTerrains()
        {
            var terrains = UnityEngine.Object.FindObjectsOfType<Terrain>();
            Log($"场景中 Terrain 数量 = {terrains.Length}");
            foreach (var t in terrains)
            {
                Log($"  {t.name} @ {t.transform.position} size={t.terrainData.size} " +
                    $"hm={t.terrainData.heightmapResolution}");
            }

            int expected = TilesX * TilesZ;
            if (terrains.Length != expected)
            {
                throw new InvalidOperationException($"预期 {expected} 块地形，实际 {terrains.Length} 块");
            }
        }

        /// <summary>
        /// 水面留在主场景，不参与 SECTR 切块。
        ///
        /// 尺寸不写死：预制体内部网格的真实尺寸随版本可能变，
        /// 所以实例化后在 scale=1 下量一次包围盒，再按世界需要反推缩放，
        /// 保证水面刚好覆盖 3×3 tile 范围（含 5% 余量防边缘漏底）。
        /// </summary>
        private static void SetupWater()
        {
            const string urpWaterPrefab = "Assets/Procedural Worlds/Packages - Install/Gaia Water/Water Prefabs/URP/Gaia Water.prefab";
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(urpWaterPrefab);
            if (prefab == null)
            {
                Log($"警告：找不到 URP 水预制体，跳过放水：{urpWaterPrefab}");
                return;
            }

            var water = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            water.name = "Gaia Water";

            // 量原始尺寸（缩放 1 时的世界包围盒）
            Bounds bounds = MeasureBounds(water);
            float worldSize = TileSize * Mathf.Max(TilesX, TilesZ) * 1.05f;
            if (bounds.size.x > 0.0001f && bounds.size.z > 0.0001f)
            {
                float sx = worldSize / bounds.size.x;
                float sz = worldSize / bounds.size.z;
                water.transform.localScale = new Vector3(sx, 1f, sz);
                Log($"水面原始尺寸 {bounds.size}，缩放到 ({sx:F1}, 1, {sz:F1}) 以覆盖 {worldSize:F0}m");
            }
            else
            {
                Log($"警告：无法测量水面包围盒（{bounds.size}），沿用 scale=1");
            }

            water.transform.position = WorldCenter + new Vector3(0f, SeaLevel, 0f);
            Log($"已放置水面 y={SeaLevel}");
        }

        /// <summary>递归合并所有 Renderer 的世界包围盒。</summary>
        private static Bounds MeasureBounds(GameObject root)
        {
            bool has = false;
            Bounds result = new Bounds(root.transform.position, Vector3.zero);
            foreach (var r in root.GetComponentsInChildren<Renderer>())
            {
                if (!has) { result = r.bounds; has = true; }
                else result.Encapsulate(r.bounds);
            }
            return result;
        }

        private static void Complete(int exitCode)
        {
            Cleanup();
            Log($"流水线结束，exit={exitCode}");
            EditorApplication.Exit(exitCode);
        }

        private static void Abort(string reason)
        {
            Debug.LogError($"{Tag} 中止：{reason}");
            Cleanup();
            EditorApplication.Exit(1);
        }

        private static void Cleanup()
        {
            EditorApplication.update -= Tick;
            GaiaSessionManager.OnWorldCreated -= HandleWorldCreated;
            s_stage = Stage.Idle;
        }

        private static void Log(string message)
        {
            Debug.Log($"{Tag} {message}");
        }
    }
}
