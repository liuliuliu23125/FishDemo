using System;
using System.IO;
using System.Text;
using GPUECSAnimationBaker.Engine.AnimatorSystem;
using FishDemo.Gameplay;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Rendering;
using Unity.Transforms;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using WaveHarmonic.Crest;

[InitializeOnLoad]
public static class LakesideCrowdVerify
{
    private const string Tag = "[LakesideCrowdVerify]";
    private const string WorldScenePath = "Assets/Scenes/World.unity";
    private const string OutDir = "Logs/Crowd";
    private const string ResultPath = OutDir + "/lakeside-verify.txt";
    private const string PendingKey = "FishDemo.LakesideCrowdVerify.Pending";

    static LakesideCrowdVerify()
    {
        EditorApplication.update += Poll;
    }

    public static void Run()
    {
        Directory.CreateDirectory(OutDir);
        EditorSceneManager.OpenScene(WorldScenePath, OpenSceneMode.Single);
        var (loaded, skipped) = ChunkLoadScope.LoadRelevant(ChunkLoadScope.Area.FromLake());
        File.WriteAllText(OutDir + "/preload.txt", $"loaded={loaded} skipped={skipped}");
        EditorSceneManager.SetActiveScene(EditorSceneManager.GetSceneByPath(WorldScenePath));
        SessionState.SetString(PendingKey + ".Pre", $"预加载 chunk：加载 {loaded} / 跳过 {skipped}");
        SessionState.SetBool(PendingKey, true);
        EditorApplication.isPlaying = true;
    }

    private static void Poll()
    {
        if (!SessionState.GetBool(PendingKey, false) || !EditorApplication.isPlaying)
            return;

        SessionState.SetBool(PendingKey, false);
        EditorApplication.update -= Poll;
        new GameObject("~LakesideCrowdVerifyDriver").AddComponent<Driver>().Begin();
    }

    private sealed class Driver : MonoBehaviour
    {
        private readonly StringBuilder _sb = new StringBuilder();
        private float _startTime;
        private int _step;
        private int _waitFrames;
        private Entity _entity;
        private Vector3 _position;
        private float _startFrame;
        private Camera _camera;
        private RenderTexture _rt;
        private WaterRenderer _water;
        private string _shotPath;
        private string _shotPath2;
        private string _shotPath3;
        private int _expected;

        public void Begin()
        {
            gameObject.hideFlags = HideFlags.DontSave;
            _startTime = Time.realtimeSinceStartup;
            _sb.AppendLine(SessionState.GetString(PendingKey + ".Pre", ""));
            Terrain[] playTerrains = UnityEngine.Object.FindObjectsOfType<Terrain>();
            _sb.AppendLine($"Play Mode: Terrain={playTerrains.Length}, MeshRenderer={UnityEngine.Object.FindObjectsOfType<MeshRenderer>().Length}");
            for (int i = 0; i < playTerrains.Length && i < 10; i++)
                _sb.AppendLine($"  Terrain[{i}] {playTerrains[i].name} @ {playTerrains[i].transform.position}");
            foreach (LakeCameraRig rig in UnityEngine.Object.FindObjectsOfType<LakeCameraRig>())
                rig.enabled = false;
            foreach (LakeOrbitCamera orbit in UnityEngine.Object.FindObjectsOfType<LakeOrbitCamera>())
                orbit.Paused = true;
            foreach (FishDemo.Rendering.GrassBRGManager grass in
                     UnityEngine.Object.FindObjectsOfType<FishDemo.Rendering.GrassBRGManager>())
                grass.gameObject.SetActive(false);
            _sb.AppendLine($"Play Mode 开始，t={_startTime:F2}");
        }

        private void Update()
        {
            if (_step >= 90) return;

            switch (_step)
            {
                case 0:
                {
                    World world = World.DefaultGameObjectInjectionWorld;
                    if (world == null)
                    {
                        if (Time.realtimeSinceStartup - _startTime > 60f) Fail("60s 内没有 ECS World");
                        return;
                    }

                    EntityManager em = world.EntityManager;
                    EntityQuery query = em.CreateEntityQuery(
                        ComponentType.ReadOnly<GpuEcsAnimatorShaderDataComponent>(),
                        ComponentType.ReadOnly<LocalTransform>());
                    int count = query.CalculateEntityCount();
                    if (count == 0)
                    {
                        if (Time.realtimeSinceStartup - _startTime > 60f)
                            Fail("60s 内没有生成带 GpuEcsAnimatorShaderDataComponent 的实体");
                        return;
                    }

                    EntityQuery spawnerQuery = em.CreateEntityQuery(
                        ComponentType.ReadOnly<LakesideCrowdSpawner>());
                    _expected = 1;
                    using (NativeArray<Entity> spawners = spawnerQuery.ToEntityArray(Allocator.Temp))
                    {
                        if (spawners.Length > 0)
                            _expected = em.GetComponentData<LakesideCrowdSpawner>(spawners[0]).Count;
                    }
                    if (_expected <= 0)
                    {
                        Fail("spawner Count = 0");
                        return;
                    }
                    if (count < _expected)
                    {
                        if (Time.realtimeSinceStartup - _startTime > 60f)
                            Fail($"60s 内只生成 {count}/{_expected} 个实体");
                        return;
                    }

                    using NativeArray<Entity> entities = query.ToEntityArray(Allocator.Temp);
                    _entity = entities[0];
                    LocalTransform transform = em.GetComponentData<LocalTransform>(_entity);
                    _position = transform.Position;
                    if (!em.HasComponent<LocalToWorld>(_entity)) return;
                    LocalToWorld rootWorld = em.GetComponentData<LocalToWorld>(_entity);
                    if (math.distancesq(rootWorld.Position, transform.Position) > 0.0001f) return;
                    _sb.AppendLine($"实体数 = {count}（期望 {_expected}，按 GpuEcsAnimatorShaderDataComponent 统计，不含 prefab）");
                    _sb.AppendLine($"首位实体位置 = ({_position.x:F2}, {_position.y:F2}, {_position.z:F2})");
                    _sb.AppendLine($"根 LocalToWorld = ({rootWorld.Position.x:F2}, {rootWorld.Position.y:F2}, {rootWorld.Position.z:F2})");
                    if (em.HasBuffer<Child>(_entity))
                    {
                        DynamicBuffer<Child> children = em.GetBuffer<Child>(_entity);
                        for (int i = 0; i < children.Length; i++)
                        {
                            Entity child = children[i].Value;
                            if (!em.Exists(child) || !em.HasComponent<LocalToWorld>(child)) continue;
                            LocalToWorld childWorld = em.GetComponentData<LocalToWorld>(child);
                            _sb.AppendLine($"  child[{i}] LocalToWorld = ({childWorld.Position.x:F2}, {childWorld.Position.y:F2}, {childWorld.Position.z:F2})");
                        }
                    }

                    float minY = float.MaxValue, maxY = float.MinValue;
                    float minRadius = float.MaxValue, maxRadius = float.MinValue;
                    int bad = 0;
                    for (int i = 0; i < entities.Length; i++)
                    {
                        LocalTransform lt = em.GetComponentData<LocalTransform>(entities[i]);
                        float dx = lt.Position.x - 192f;
                        float dz = lt.Position.z - 96f;
                        float r = Mathf.Sqrt(dx * dx + dz * dz);
                        minY = Mathf.Min(minY, lt.Position.y);
                        maxY = Mathf.Max(maxY, lt.Position.y);
                        minRadius = Mathf.Min(minRadius, r);
                        maxRadius = Mathf.Max(maxRadius, r);
                        if (r < 150f || r > 220f ||
                            lt.Position.y < LakeCarve.WaterLevel + 0.05f ||
                            lt.Position.y > LakeCarve.WaterLevel + 60f)
                            bad++;
                    }
                    _sb.AppendLine($"Y 范围 = {minY:F2}–{maxY:F2}（水位 +{minY - LakeCarve.WaterLevel:F2} ~ +{maxY - LakeCarve.WaterLevel:F2}）");
                    _sb.AppendLine($"半径范围 = {minRadius:F2}–{maxRadius:F2}（要求 150–220）");
                    _sb.AppendLine($"位置检查: {(bad == 0 ? "PASS" : "FAIL")}（越界 {bad}/{entities.Length}）");
                    if (bad != 0)
                    {
                        Fail($"{bad} 个实例越界");
                        return;
                    }

                    GpuEcsAnimatorShaderDataComponent shaderData =
                        em.GetComponentData<GpuEcsAnimatorShaderDataComponent>(_entity);
                    _startFrame = shaderData.shaderData.c2.x;
                    _sb.AppendLine($"起始 frameIndex = {_startFrame:F2}");
                    _waitFrames = 0;
                    _step = 1;
                    return;
                }
                case 1:
                {
                    if (++_waitFrames < 30) return;
                    World world = World.DefaultGameObjectInjectionWorld;
                    EntityManager em = world.EntityManager;
                    if (!em.Exists(_entity))
                    {
                        Fail("实体在 30 帧内被销毁");
                        return;
                    }
                    GpuEcsAnimatorShaderDataComponent shaderData =
                        em.GetComponentData<GpuEcsAnimatorShaderDataComponent>(_entity);
                    float endFrame = shaderData.shaderData.c2.x;
                    bool moved = !Mathf.Approximately(endFrame, _startFrame);
                    _sb.AppendLine($"30 帧后 frameIndex = {endFrame:F2}");
                    _sb.AppendLine($"动画状态变化: {(moved ? "PASS" : "FAIL")}");
                    if (!moved)
                    {
                        Fail("动画状态在 30 帧内没有变化");
                        return;
                    }

                    CreateCamera();
                    _waitFrames = 0;
                    _step = 2;
                    return;
                }
                case 2:
                {
                    if (Time.realtimeSinceStartup - _startTime < 10f) return;
                    if (++_waitFrames < 30) return;
                    var graphics = World.DefaultGameObjectInjectionWorld
                        .GetExistingSystemManaged<EntitiesGraphicsSystem>();
                    if (graphics != null)
                    {
                        EntitiesGraphicsStats stats = graphics.Stats;
                        _sb.AppendLine($"EntitiesGraphics: batches={stats.BatchCount} " +
                                       $"draw={stats.DrawCommandCount} rendered={stats.RenderedInstanceCount}");
                    }
                    _shotPath = OutDir + "/lakeside-verify.png";
                    CaptureToTexture(_shotPath);
                    _sb.AppendLine($"抓屏 → {_shotPath}");
                    _waitFrames = 0;
                    _step = _expected > 1 ? 3 : 5;
                    return;
                }
                case 3:
                {
                    if (++_waitFrames == 1) MoveCameraOverview();
                    if (_waitFrames < 25) return;
                    var graphics = World.DefaultGameObjectInjectionWorld
                        .GetExistingSystemManaged<EntitiesGraphicsSystem>();
                    if (graphics != null)
                    {
                        EntitiesGraphicsStats stats = graphics.Stats;
                        _sb.AppendLine($"EntitiesGraphics(overview): batches={stats.BatchCount} " +
                                       $"draw={stats.DrawCommandCount} rendered={stats.RenderedInstanceCount}");
                    }
                    _shotPath2 = OutDir + "/lakeside-30-overview.png";
                    CaptureToTexture(_shotPath2);
                    _sb.AppendLine($"抓屏 → {_shotPath2}");
                    _waitFrames = 0;
                    _step = 4;
                    return;
                }
                case 4:
                {
                    if (++_waitFrames == 1) MoveCameraArc();
                    if (_waitFrames < 25) return;
                    var graphics = World.DefaultGameObjectInjectionWorld
                        .GetExistingSystemManaged<EntitiesGraphicsSystem>();
                    if (graphics != null)
                    {
                        EntitiesGraphicsStats stats = graphics.Stats;
                        _sb.AppendLine($"EntitiesGraphics(arc): batches={stats.BatchCount} " +
                                       $"draw={stats.DrawCommandCount} rendered={stats.RenderedInstanceCount}");
                    }
                    _shotPath3 = OutDir + "/lakeside-30-crowd.png";
                    CaptureToTexture(_shotPath3);
                    _sb.AppendLine($"抓屏 → {_shotPath3}");
                    _waitFrames = 0;
                    _step = 5;
                    return;
                }
                case 5:
                {
                    if (++_waitFrames < 5) return;
                    bool shotOk = File.Exists(_shotPath) && new FileInfo(_shotPath).Length > 1024;
                    bool overviewOk = _expected <= 1 ||
                                      (File.Exists(_shotPath2) && new FileInfo(_shotPath2).Length > 1024);
                    bool crowdOk = _expected <= 1 ||
                                   (File.Exists(_shotPath3) && new FileInfo(_shotPath3).Length > 1024);
                    _sb.AppendLine($"截图文件有效: close={shotOk} overview={overviewOk} crowd={crowdOk}");
                    Finish(shotOk && overviewOk && crowdOk ? 0 : 2);
                    return;
                }
            }
        }

        private void CreateCamera()
        {
            var go = new GameObject("~LakesideVerifyCamera");
            go.hideFlags = HideFlags.DontSave;
            _camera = go.AddComponent<Camera>();
            _camera.depth = 100f;
            _camera.fieldOfView = 40f;
            _camera.nearClipPlane = 0.1f;
            _camera.farClipPlane = 2000f;
            _camera.clearFlags = CameraClearFlags.Skybox;
            _camera.cullingMask = ~0;

            _rt = new RenderTexture(2560, 1440, 24, RenderTextureFormat.ARGB32,
                RenderTextureReadWrite.sRGB);
            _rt.Create();
            _camera.targetTexture = _rt;

            _water = UnityEngine.Object.FindObjectOfType<WaterRenderer>();
            if (_water != null)
            {
                _water.Viewer = _camera;
                _sb.AppendLine("Crest Viewer = 验证相机");
            }

            Vector3 toCenter = new Vector3(192f - _position.x, 0f, 96f - _position.z);
            if (toCenter.sqrMagnitude < 0.01f) toCenter = Vector3.forward;
            toCenter.Normalize();
            Vector3 camPos = _position + toCenter * 13f + Vector3.up * 5f;
            _camera.transform.position = camPos;
            Vector3 lookAt = _position + Vector3.up * 3.2f;
            _camera.transform.LookAt(lookAt);
            _sb.AppendLine($"验证相机 = ({camPos.x:F2}, {camPos.y:F2}, {camPos.z:F2}) → ({lookAt.x:F2}, {lookAt.y:F2}, {lookAt.z:F2})");
        }

        private void CaptureToTexture(string path)
        {
            RenderTexture.active = _rt;
            var tex = new Texture2D(_rt.width, _rt.height, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, _rt.width, _rt.height), 0, 0);
            tex.Apply();
            File.WriteAllBytes(path, tex.EncodeToPNG());
            RenderTexture.active = null;
            UnityEngine.Object.DestroyImmediate(tex);
        }

        private void MoveCameraOverview()
        {
            _camera.fieldOfView = 70f;
            Vector3 camPos = new Vector3(192f, LakeCarve.WaterLevel + 96f, 96f - 116f);
            Vector3 lookAt = new Vector3(192f, LakeCarve.WaterLevel + 4f, 96f);
            _camera.transform.position = camPos;
            _camera.transform.LookAt(lookAt);
            _sb.AppendLine($"俯视相机 = ({camPos.x:F2}, {camPos.y:F2}, {camPos.z:F2}) → ({lookAt.x:F2}, {lookAt.y:F2}, {lookAt.z:F2})");
        }

        private void MoveCameraArc()
        {
            _camera.fieldOfView = 90f;
            Vector3 camPos = new Vector3(192f, LakeCarve.WaterLevel + 21f, 96f - 146f);
            Vector3 lookAt = new Vector3(192f, LakeCarve.WaterLevel + 6f, 96f - 191f);
            _camera.transform.position = camPos;
            _camera.transform.LookAt(lookAt);
            _sb.AppendLine($"弧段相机 = ({camPos.x:F2}, {camPos.y:F2}, {camPos.z:F2}) → ({lookAt.x:F2}, {lookAt.y:F2}, {lookAt.z:F2})");
        }

        private void Fail(string reason)
        {
            _sb.AppendLine($"FAIL：{reason}");
            Finish(2);
        }

        private void Finish(int code)
        {
            _step = 90;
            _sb.AppendLine("");
            _sb.AppendLine(code == 0 ? "==== 结论：PASS ====" : "==== 结论：FAIL ====");
            File.WriteAllText(ResultPath, _sb.ToString());
            if (_camera != null) _camera.targetTexture = null;
            if (_rt != null)
            {
                UnityEngine.Object.DestroyImmediate(_rt);
                _rt = null;
            }
            gameObject.AddComponent<QuitAfter>().Init(code);
        }
    }

    private sealed class QuitAfter : MonoBehaviour
    {
        private int _frame;
        private int _code;

        public void Init(int code)
        {
            _code = code;
            gameObject.hideFlags = HideFlags.DontSave;
        }

        private void Update()
        {
            if (++_frame < 5) return;
            EditorApplication.Exit(_code);
        }
    }
}
