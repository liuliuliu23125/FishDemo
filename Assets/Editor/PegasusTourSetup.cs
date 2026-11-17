using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Pegasus;

namespace FishDemo.EditorTools
{
    public static class PegasusTourSetup
    {
        private const string Tag = "[PegasusTour]";
        private const string WorldScenePath = "Assets/Scenes/World.unity";
        private const string ManagerRootName = "Pegasus Tour";

        private const float RouteRadius = 1100f;

        private const int PoiCount = 16;

        private const float AltLow = 3f;
        private const float AltHigh = 8f;

        private const float CruiseSpeed = 8f;

        private const float LookAhead = 25f;

        [MenuItem("FishDemo/World/② 布置 Pegasus 环绕巡游", false, 21)]
        public static void Run()
        {
            var sb = new StringBuilder();
            void W(string s) { Debug.Log($"{Tag} {s}"); sb.AppendLine(s); }

            var scene = EditorSceneManager.OpenScene(WorldScenePath, OpenSceneMode.Single);

            var camera = Object.FindObjectsOfType<Camera>()
                .FirstOrDefault(c => c.CompareTag("MainCamera")) ?? Object.FindObjectOfType<Camera>();
            if (camera == null)
            {
                W("主场景里没有相机，先跑 WorldPipeline 生成世界");
                Flush(sb);
                return;
            }

            var route = BuildRoute();
            W($"路线：半径 {RouteRadius}m，{PoiCount} 个 POI，周长约 {2f * Mathf.PI * RouteRadius / 1000f:F1}km");
            if (!BakeGroundHeights(scene, route, W))
            {
                W("地形高度采样失败");
                Flush(sb);
                return;
            }

            RemoveExistingManagers();

            var root = new GameObject(ManagerRootName);
            var manager = root.AddComponent<PegasusManager>();

            manager.m_target = camera.gameObject;
            manager.m_flythroughType = PegasusConstants.FlythroughType.Looped;
            manager.m_targetFramerateType = PegasusConstants.TargetFrameRate.LeaveAlone;
            manager.m_heightCheckType = PegasusConstants.HeightCheckType.None;
            manager.m_autoStartAtRuntime = true;
            manager.m_displayDebug = false;
            manager.m_positionDamping = 0.1f;
            manager.m_rotationDamping = 0.1f;

            for (int i = 0; i < PoiCount; i++)
            {
                var p = route[i];
                var poiGo = new GameObject($"POI {i:00}");
                poiGo.transform.SetParent(root.transform, false);
                poiGo.transform.position = p.Position;

                var poi = poiGo.AddComponent<PegasusPoi>();
                poi.m_manager = manager;

                poi.m_lookatType = PegasusConstants.LookatType.Target;
                poi.m_lookatLocation = p.LookAt;

                poi.m_startSpeedType = PegasusConstants.SpeedType.Custom;
                poi.m_startSpeed = CruiseSpeed;

                poi.m_velocityEasingType = PegasusConstants.EasingType.Linear;
                poi.m_positionEasingType = PegasusConstants.EasingType.Linear;
                poi.m_rotationEasingType = PegasusConstants.EasingType.Linear;
                poi.m_autoRollOn = false;
                poi.m_heightCheckType = PegasusConstants.PoiHeightCheckType.None;
            }

            manager.InitialiseFlythrough();

            EnsureTourController(camera.gameObject);

            W($"已生成 {PoiCount} 个 POI，总里程 {manager.m_totalDistance / 1000f:F2}km，" +
              $"预计 {manager.m_totalDuration.TotalMinutes:F1} 分钟/圈");
            W($"高度范围（离地）{AltLow}m ~ {AltHigh}m；地面 Y 区间 " +
              $"[{route.Min(r => r.GroundY):F1}, {route.Max(r => r.GroundY):F1}]");

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            AssetDatabase.SaveAssets();
            W("已保存 World.unity");
            Flush(sb);
        }

        private struct RoutePoint
        {
            public Vector3 Position;
            public Vector3 LookAt;
            public float GroundY;
        }

        private static List<RoutePoint> BuildRoute()
        {
            var list = new List<RoutePoint>(PoiCount);
            for (int i = 0; i < PoiCount; i++)
            {
                float theta = 2f * Mathf.PI * i / PoiCount;
                float cos = Mathf.Cos(theta);
                float sin = Mathf.Sin(theta);

                var tangent = new Vector3(-sin, 0f, cos);

                float t = 0.5f - 0.5f * Mathf.Cos(2f * theta);
                float altitudeAboveGround = Mathf.Lerp(AltLow, AltHigh, t);

                var basePos = new Vector3(cos * RouteRadius, altitudeAboveGround, sin * RouteRadius);

                list.Add(new RoutePoint
                {
                    Position = basePos,
                    LookAt = new Vector3(basePos.x, basePos.y, basePos.z)
                             + tangent * LookAhead
                             - Vector3.up * 9f,
                    GroundY = float.NaN,
                });
            }
            return list;
        }

        private static bool BakeGroundHeights(Scene scene, List<RoutePoint> route, System.Action<string> w)
        {
            var chunks = Object.FindObjectsOfType<SECTR_Chunk>().OrderBy(c => c.ScenePath).ToList();
            if (chunks.Count == 0)
            {
                w("主场景里没有 SECTR_Chunk");
                return false;
            }

            var heights = new float[route.Count];
            for (int i = 0; i < heights.Length; i++)
                heights[i] = float.NaN;

            int opened = 0;
            foreach (var c in chunks)
            {
                if (string.IsNullOrEmpty(c.NodeName) || !File.Exists(c.NodeName))
                    continue;

                var chunkScene = EditorSceneManager.OpenScene(c.NodeName, OpenSceneMode.Additive);
                opened++;

                var terrains = Object.FindObjectsOfType<Terrain>()
                    .Where(t => t.gameObject.scene == chunkScene)
                    .ToArray();

                for (int i = 0; i < route.Count; i++)
                {
                    if (!float.IsNaN(heights[i]))
                        continue;

                    var rp = route[i];
                    foreach (var t in terrains)
                    {
                        if (t.terrainData == null)
                            continue;

                        Vector3 origin = t.transform.position;
                        Vector3 size = t.terrainData.size;
                        if (rp.Position.x < origin.x || rp.Position.x > origin.x + size.x)
                            continue;
                        if (rp.Position.z < origin.z || rp.Position.z > origin.z + size.z)
                            continue;

                        heights[i] = t.SampleHeight(new Vector3(rp.Position.x, 0f, rp.Position.z))
                                     + origin.y;
                        break;
                    }
                }

                EditorSceneManager.CloseScene(chunkScene, true);
            }

            int missing = 0;
            for (int i = 0; i < route.Count; i++)
            {
                if (float.IsNaN(heights[i]))
                {
                    missing++;
                    w($"POI {i} 未落在任何地形上（x={route[i].Position.x:F0}, z={route[i].Position.z:F0}）");
                    continue;
                }

                var rp = route[i];
                rp.GroundY = heights[i];

                float dy = heights[i];
                rp.Position.y += dy;
                rp.LookAt.y += dy;
                route[i] = rp;
            }

            w($"扫描 {opened} 个 chunk 场景采高度，未命中 {missing} 个点");
            return missing == 0;
        }

        private static void RemoveExistingManagers()
        {
            var existing = Object.FindObjectsOfType<PegasusManager>();
            foreach (var m in existing)
            {
                Object.DestroyImmediate(m.gameObject);
            }
            if (existing.Length > 0)
            {
                Debug.Log($"{Tag} 已移除 {existing.Length} 个旧 Pegasus 节点");
            }
        }

        private static void EnsureTourController(GameObject cameraGo)
        {
            var existing = cameraGo.GetComponent<FishDemo.Gameplay.PegasusTourController>();
            if (existing == null)
            {
                cameraGo.AddComponent<FishDemo.Gameplay.PegasusTourController>();
            }
        }

        private static void Flush(StringBuilder sb)
        {
            Directory.CreateDirectory("Logs");
            File.WriteAllText("Logs/pegasus-tour-setup.txt", sb.ToString());
        }
    }
}
