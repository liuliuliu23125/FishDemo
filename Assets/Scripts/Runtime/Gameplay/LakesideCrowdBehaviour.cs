using GPUECSAnimationBaker.Engine.AnimatorSystem;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;

namespace FishDemo.Gameplay
{
    public struct LakesideCrowdSpawner : IComponentData
    {
        public int Count;
        public uint Seed;
        public float Scale;
    }

    public struct LakesideCrowdPrefab : IBufferElementData
    {
        public Entity Value;
    }

    public struct LakesideCrowdPose : IBufferElementData
    {
        public float3 Position;
        public quaternion Rotation;
    }

    public struct LakesideCrowdSpawned : IComponentData
    {
    }

    [DisallowMultipleComponent]
    public sealed class LakesideCrowdBehaviour : MonoBehaviour
    {
        public GameObject[] prefabs = System.Array.Empty<GameObject>();
        public Vector3[] positions = System.Array.Empty<Vector3>();
        public float[] yaws = System.Array.Empty<float>();
        public int count = 1;
        public uint seed = 20261117u;
        public float scale = 4f;
    }

    public sealed class LakesideCrowdBaker : Baker<LakesideCrowdBehaviour>
    {
        public override void Bake(LakesideCrowdBehaviour authoring)
        {
            Entity entity = GetEntity(TransformUsageFlags.None);

            int count = math.max(0, authoring.count);
            if (authoring.positions != null)
                count = math.min(count, authoring.positions.Length);

            AddComponent(entity, new LakesideCrowdSpawner
            {
                Count = count,
                Seed = authoring.seed,
                Scale = math.max(0.01f, authoring.scale)
            });

            DynamicBuffer<LakesideCrowdPrefab> prefabs = AddBuffer<LakesideCrowdPrefab>(entity);
            if (authoring.prefabs != null)
            {
                for (int i = 0; i < authoring.prefabs.Length; i++)
                {
                    GameObject prefab = authoring.prefabs[i];
                    if (prefab == null) continue;
                    GpuEcsAnimatorBehaviour animator = prefab.GetComponent<GpuEcsAnimatorBehaviour>();
                    TransformUsageFlags flags = animator != null
                        ? animator.transformUsageFlags
                        : TransformUsageFlags.Dynamic;
                    prefabs.Add(new LakesideCrowdPrefab { Value = GetEntity(prefab, flags) });
                }
            }

            DynamicBuffer<LakesideCrowdPose> poses = AddBuffer<LakesideCrowdPose>(entity);
            for (int i = 0; i < count; i++)
            {
                Vector3 position = authoring.positions[i];
                float yaw = authoring.yaws != null && i < authoring.yaws.Length ? authoring.yaws[i] : 0f;
                poses.Add(new LakesideCrowdPose
                {
                    Position = new float3(position.x, position.y, position.z),
                    Rotation = quaternion.Euler(0f, math.radians(yaw), 0f)
                });
            }
        }
    }

    [UpdateInGroup(typeof(InitializationSystemGroup))]
    public partial class LakesideCrowdSpawnSystem : SystemBase
    {
        protected override void OnUpdate()
        {
            EntityManager entityManager = EntityManager;
            EntityQuery query = GetEntityQuery(
                ComponentType.ReadOnly<LakesideCrowdSpawner>(),
                ComponentType.ReadOnly<LakesideCrowdPrefab>(),
                ComponentType.ReadOnly<LakesideCrowdPose>(),
                ComponentType.Exclude<LakesideCrowdSpawned>());

            using NativeArray<Entity> spawners = query.ToEntityArray(Allocator.Temp);
            for (int s = 0; s < spawners.Length; s++)
            {
                Entity spawnerEntity = spawners[s];
                LakesideCrowdSpawner spawner = entityManager.GetComponentData<LakesideCrowdSpawner>(spawnerEntity);
                DynamicBuffer<LakesideCrowdPrefab> prefabs =
                    entityManager.GetBuffer<LakesideCrowdPrefab>(spawnerEntity, true);
                DynamicBuffer<LakesideCrowdPose> poses =
                    entityManager.GetBuffer<LakesideCrowdPose>(spawnerEntity, true);

                if (prefabs.Length == 0 || poses.Length == 0)
                {
                    entityManager.AddComponent<LakesideCrowdSpawned>(spawnerEntity);
                    continue;
                }

                System.Random random = new System.Random((int)spawner.Seed);
                for (int i = 0; i < poses.Length; i++)
                {
                    Entity prefab = prefabs[(i * 7) % prefabs.Length].Value;
                    if (prefab == Entity.Null) continue;
                    LakesideCrowdPose pose = poses[i];
                    Entity instance = entityManager.Instantiate(prefab);

                    float scale = entityManager.GetComponentData<LocalTransform>(prefab).Scale * spawner.Scale;
                    entityManager.SetComponentData(instance,
                        LocalTransform.FromPositionRotationScale(pose.Position, pose.Rotation, scale));

                    int animationCount = entityManager
                        .GetBuffer<GpuEcsAnimationDataBufferElement>(prefab, true).Length;
                    int animationId = animationCount > 0 ? random.Next(0, animationCount) : 0;
                    float startNormalizedTime = (float)random.NextDouble();

                    entityManager.SetComponentData(instance, new GpuEcsAnimatorControlComponent
                    {
                        animatorInfo = new AnimatorInfo
                        {
                            animationID = animationId,
                            blendFactor = 0f,
                            speedFactor = 1f
                        },
                        startNormalizedTime = startNormalizedTime,
                        transitionSpeed = 0f
                    });

                    entityManager.SetComponentData(instance, new GpuEcsAnimatorControlStateComponent
                    {
                        state = GpuEcsAnimatorControlStates.Start
                    });
                }

                entityManager.AddComponent<LakesideCrowdSpawned>(spawnerEntity);
            }
        }
    }
}
