using System.Collections.Generic;
using Game;
using Game.Buildings;
using Game.Common;
using Game.Net;
using Game.Objects;
using Game.Prefabs;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace BlockBuilder
{
    /// <summary>
    /// After an apply, checks what actually exists in the city: committed buildings, their road
    /// connection, condemnation (growables vs. zoning), and committed road edges.
    /// </summary>
    public partial class SpikeAuditSystem : GameSystemBase
    {
        public struct Expected
        {
            public Entity Prefab;
            public string Name;
            public float3 Position;
            public float3 End;
            public bool IsRoad;
        }

        private sealed class Batch
        {
            public int Id;
            public List<Expected> Items;
            public double Early;
            public double Late;
            public bool EarlyDone;
        }

        private readonly List<Batch> m_Batches = new List<Batch>();
        private int m_NextId = 1;
        private EntityQuery m_Buildings;
        private EntityQuery m_Edges;

        protected override void OnCreate()
        {
            base.OnCreate();
            m_Buildings = GetEntityQuery(ComponentType.ReadOnly<Building>(), ComponentType.ReadOnly<Transform>(), ComponentType.ReadOnly<PrefabRef>(), ComponentType.Exclude<Temp>(), ComponentType.Exclude<Deleted>());
            m_Edges = GetEntityQuery(ComponentType.ReadOnly<Edge>(), ComponentType.ReadOnly<Curve>(), ComponentType.ReadOnly<PrefabRef>(), ComponentType.Exclude<Temp>(), ComponentType.Exclude<Deleted>());
        }

        public void Expect(List<Expected> items)
        {
            double now = UnityEngine.Time.realtimeSinceStartupAsDouble;
            var batch = new Batch { Id = m_NextId++, Items = new List<Expected>(items), Early = now + 2, Late = now + 30 };
            m_Batches.Add(batch);
            Mod.log.Info($"audit: batch #{batch.Id} queued with {batch.Items.Count} items");
        }

        protected override void OnUpdate()
        {
            double now = UnityEngine.Time.realtimeSinceStartupAsDouble;
            for (int i = m_Batches.Count - 1; i >= 0; i--)
            {
                var batch = m_Batches[i];
                if (!batch.EarlyDone && now >= batch.Early)
                {
                    batch.EarlyDone = true;
                    Audit(batch.Items, $"#{batch.Id} early");
                }
                else if (batch.EarlyDone && now >= batch.Late)
                {
                    Audit(batch.Items, $"#{batch.Id} late");
                    m_Batches.RemoveAt(i);
                }
            }
        }

        private void Audit(List<Expected> m_Expected, string stage)
        {
            m_Buildings.CompleteDependency();
            m_Edges.CompleteDependency();
            using var buildings = m_Buildings.ToEntityArray(Allocator.Temp);
            using var edges = m_Edges.ToEntityArray(Allocator.Temp);
            int found = 0;
            foreach (var item in m_Expected)
            {
                if (item.IsRoad)
                {
                    Entity match = Entity.Null;
                    foreach (var e in edges)
                    {
                        if (EntityManager.GetComponentData<PrefabRef>(e).m_Prefab != item.Prefab)
                        {
                            continue;
                        }

                        var c = EntityManager.GetComponentData<Curve>(e).m_Bezier;
                        if (math.distance(c.a.xz, item.Position.xz) < 4f || math.distance(c.d.xz, item.Position.xz) < 4f)
                        {
                            match = e;
                            break;
                        }
                    }

                    if (match != Entity.Null)
                    {
                        found++;
                    }

                    Mod.log.Info($"audit[{stage}]: road {item.Name} live={match != Entity.Null} entity={match}");
                    continue;
                }

                Entity building = Entity.Null;
                foreach (var e in buildings)
                {
                    if (EntityManager.GetComponentData<PrefabRef>(e).m_Prefab != item.Prefab)
                    {
                        continue;
                    }

                    if (math.distance(EntityManager.GetComponentData<Transform>(e).m_Position.xz, item.Position.xz) < 3f)
                    {
                        building = e;
                        break;
                    }
                }

                if (building == Entity.Null)
                {
                    Mod.log.Info($"audit[{stage}]: building {item.Name} at {item.Position} NOT FOUND");
                    continue;
                }

                found++;
                var data = EntityManager.GetComponentData<Building>(building);
                bool roadLive = data.m_RoadEdge != Entity.Null && EntityManager.Exists(data.m_RoadEdge) && !EntityManager.HasComponent<Deleted>(data.m_RoadEdge);
                bool condemned = EntityManager.HasComponent<Condemned>(building);
                bool power = EntityManager.HasComponent<ElectricityConsumer>(building) && EntityManager.GetComponentData<ElectricityConsumer>(building).electricityConnected;
                Mod.log.Info($"audit[{stage}]: building {item.Name} entity={building} roadEdge={(roadLive ? data.m_RoadEdge.ToString() : "none")} condemned={condemned} power={power}");
            }

            Mod.log.Info($"audit[{stage}]: {found}/{m_Expected.Count} expected items live");
        }
    }
}
