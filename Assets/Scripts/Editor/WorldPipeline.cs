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
    public static class WorldPipeline
    {
        private const int TilesX = 3;
        private const int TilesZ = 3;
        private const int TileSize = 1024;
        private const int TileHeight = 700;
        private const int SeaLevel = 60;
        private const int SectorsPerSide = 2;

        private const string WorldScenePath = "Assets/Scenes/World.unity";
        private const string Tag = "[WorldPipeline]";

        private static Vector3 WorldCenter => Vector3.zero;


        private static readonly TimeSpan StageTimeout = TimeSpan.FromMinutes(40);

        private enum Stage { Idle, WorldCreating, Spawning, Finalizing }
        private static Stage s_stage = Stage.Idle;
        private static DateTime s_stageDeadline;
        private static bool s_worldCreated;

        [MenuItem("FishDemo/World/① 生成世界（地形+水）")]
        public static void RunAll()
        {
            BeginWorldCreation();
        }

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

                        Log("开始应用地貌印章（塑造起伏）…");
                        bool shaped = TerrainStamper.ApplyBiomeStamps();
                        if (!shaped)
                        {
                            throw new InvalidOperationException("地貌印章应用失败，地形仍为平地");
                        }
                        Log("地貌塑形完成");

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
                            return;
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
