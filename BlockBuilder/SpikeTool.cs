using System.Collections.Generic;
using System.Linq;
using Colossal.Mathematics;
using Game.City;
using Game.Common;
using Game.Net;
using Game.Prefabs;
using Game.Simulation;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;

namespace BlockBuilder
{
    /// <summary>
    /// Technical spike: places buildings through the game's own ObjectToolBaseSystem.CreateDefinitions
    /// (so sub-nets, sub-areas and sub-objects come from vanilla code) and roads through hand-built
    /// NetCourse definitions, then commits with the native apply pipeline. Everything is logged.
    /// </summary>
    public partial class SpikeTool : ObjectToolBaseSystem
    {
        public override string toolID => "BlockBuilder.Spike";

        public override PrefabBase GetPrefab() => null;

        public override bool TrySetPrefab(PrefabBase prefab) => false;

        private EntityQuery m_DefinitionQuery;
        private EntityQuery m_ErrorQuery2;
        private EntityQuery m_TempQuery;
        private CityConfigurationSystem m_City;
        private TerrainSystem m_Terrain;
        private PrefabSystem m_Prefabs;
        private CatalogSystem m_Catalog;
        private SpikeAuditSystem m_Audit;
        private DefaultToolSystem m_DefaultTool;

        private readonly List<RandomSeed> m_Seeds = new List<RandomSeed>();
        private readonly List<SpikeAuditSystem.Expected> m_Planned = new List<SpikeAuditSystem.Expected>();
        private bool m_HasStart;
        private float3 m_Start;
        private string m_LastPreview = string.Empty;

        protected override void OnCreate()
        {
            base.OnCreate();
            m_DefinitionQuery = GetDefinitionQuery();
            m_ErrorQuery2 = GetEntityQuery(ComponentType.ReadOnly<Error>());
            m_TempQuery = GetEntityQuery(ComponentType.ReadOnly<Temp>(), ComponentType.Exclude<Deleted>());
            m_City = World.GetOrCreateSystemManaged<CityConfigurationSystem>();
            m_Terrain = World.GetOrCreateSystemManaged<TerrainSystem>();
            m_Prefabs = World.GetOrCreateSystemManaged<PrefabSystem>();
            m_Catalog = World.GetOrCreateSystemManaged<CatalogSystem>();
            m_Audit = World.GetOrCreateSystemManaged<SpikeAuditSystem>();
            m_DefaultTool = World.GetOrCreateSystemManaged<DefaultToolSystem>();
        }

        public void Activate()
        {
            if (!m_Catalog.Ready)
            {
                m_Catalog.Scan(false);
            }

            m_ToolSystem.activeTool = this;
            Mod.log.Info($"spike: activated case={Mod.Options?.TestCase}");
        }

        protected override void OnStartRunning()
        {
            base.OnStartRunning();
            applyAction.shouldBeEnabled = true;
            secondaryApplyAction.shouldBeEnabled = true;
            m_HasStart = false;
            m_PendingClick = false;
            m_Signature = string.Empty;
            m_LastPreview = string.Empty;
            m_LoggedRaycast = false;
            Mod.log.Info($"spike: tool started applyEnabled={applyAction.enabled}");
        }

        protected override void OnStopRunning()
        {
            applyAction.shouldBeEnabled = false;
            secondaryApplyAction.shouldBeEnabled = false;
            m_HasStart = false;
            m_PendingClick = false;
            m_Signature = string.Empty;
            Mod.log.Info("spike: tool stopped");
            base.OnStopRunning();
        }

        public override void InitializeRaycast()
        {
            base.InitializeRaycast();
            m_ToolRaycastSystem.typeMask = TypeMask.Terrain;
            m_ToolRaycastSystem.collisionMask = CollisionMask.OnGround;
        }

        // A click is judged only after the preview has been left untouched for this many frames,
        // so late native validation (e.g. frontage on a Temp road) is not misread as an error.
        private const int kSettleFrames = 8;

        private string m_Signature = string.Empty;
        private int m_FramesSinceSubmit;
        private bool m_PendingClick;
        private bool m_LoggedRaycast;

        private JobHandle ClearPreview(JobHandle deps)
        {
            m_Signature = string.Empty;
            m_Planned.Clear();
            applyMode = ApplyMode.Clear;
            return DestroyDefinitions(m_DefinitionQuery, m_ToolOutputBarrier, deps);
        }

        protected override JobHandle OnUpdate(JobHandle inputDeps)
        {
            applyMode = ApplyMode.None;
            var options = Mod.Options;
            if (options == null)
            {
                return inputDeps;
            }

            bool twoClick = options.TestCase == SpikeCase.Road || options.TestCase == SpikeCase.RowOnNewRoad;

            if (m_PendingClick)
            {
                // Hold the current preview unchanged until it has settled, then decide.
                if (++m_FramesSinceSubmit < kSettleFrames)
                {
                    return inputDeps;
                }

                m_PendingClick = false;
                bool allow = GetAllowApply();
                LogPreviewState(allow);
                if (allow || options.ApplyDespiteErrors)
                {
                    m_Audit.Expect(m_Planned);
                    for (int i = 0; i < m_Seeds.Count; i++)
                    {
                        m_Seeds[i] = RandomSeed.Next();
                    }

                    Mod.log.Info($"spike: APPLY (allow={allow}) items={m_Planned.Count} settledFrames={m_FramesSinceSubmit}");
                    m_HasStart = false;
                    m_Signature = string.Empty;
                    m_Planned.Clear();
                    applyMode = ApplyMode.Apply;
                    return DestroyDefinitions(m_DefinitionQuery, m_ToolOutputBarrier, inputDeps);
                }

                Mod.log.Info("spike: apply BLOCKED by native errors (preview kept; move the cursor or right click)");
                return inputDeps;
            }

            if (secondaryApplyAction.WasPressedThisFrame())
            {
                if (m_HasStart)
                {
                    m_HasStart = false;
                    Mod.log.Info("spike: start point cleared");
                }
                else
                {
                    Mod.log.Info("spike: exit tool");
                    m_ToolSystem.activeTool = m_DefaultTool;
                }

                return ClearPreview(inputDeps);
            }

            if (!GetRaycastResult(out ControlPoint cursor))
            {
                return m_Signature.Length == 0 ? inputDeps : ClearPreview(inputDeps);
            }

            if (!m_LoggedRaycast)
            {
                m_LoggedRaycast = true;
                Mod.log.Info($"spike: first raycast hit {cursor.m_HitPosition} applyEnabled={applyAction.enabled}");
            }

            bool click = applyAction.WasPressedThisFrame();
            if (click)
            {
                Mod.log.Info($"spike: click case={options.TestCase} hasStart={m_HasStart} preview={(m_Signature.Length > 0)}");
            }

            if (twoClick && !m_HasStart)
            {
                if (click)
                {
                    m_HasStart = true;
                    m_Start = cursor.m_HitPosition;
                    Mod.log.Info($"spike: start point {m_Start}");
                }

                return m_Signature.Length == 0 ? inputDeps : ClearPreview(inputDeps);
            }

            if (click && m_Signature.Length > 0 && m_Planned.Count == 0)
            {
                Mod.log.Info("spike: nothing planned (e.g. road shorter than 8 m); click ignored");
                return inputDeps;
            }

            if (click && m_Signature.Length > 0)
            {
                m_PendingClick = true;
                m_FramesSinceSubmit = 0;
                return inputDeps;
            }

            // Only rebuild definitions when something actually changed; otherwise keep the
            // existing Temp preview alive so native validation can finish.
            string signature = $"{options.TestCase}|{options.PrefabOverride}|{options.RoadOverride}|{options.RowCount}|{m_HasStart}|{m_Start.xz}|{math.round(cursor.m_HitPosition.xz * 2f) / 2f}";
            if (signature == m_Signature)
            {
                m_FramesSinceSubmit++;
                return inputDeps;
            }

            applyMode = ApplyMode.Clear;
            inputDeps = DestroyDefinitions(m_DefinitionQuery, m_ToolOutputBarrier, inputDeps);
            m_Planned.Clear();
            m_Signature = signature;
            m_FramesSinceSubmit = 0;
            return Submit(options, cursor.m_HitPosition, inputDeps);
        }

        private JobHandle Submit(Setting options, float3 cursor, JobHandle deps)
        {
            switch (options.TestCase)
            {
                case SpikeCase.Road:
                {
                    var road = PickRoad(options.RoadOverride);
                    if (road == null)
                    {
                        return deps;
                    }

                    SubmitRoad(road, m_Start, cursor, 0);
                    NotePreview($"road {road.Name} w={road.Width}");
                    return deps;
                }

                case SpikeCase.RowOnNewRoad:
                {
                    var road = PickRoad(options.RoadOverride);
                    var building = PickBuilding(SpikeCase.Growable, options.PrefabOverride);
                    if (road == null || building == null)
                    {
                        return deps;
                    }

                    SubmitRoad(road, m_Start, cursor, 0);
                    float2 delta = cursor.xz - m_Start.xz;
                    float length = math.length(delta);
                    if (length < 1f)
                    {
                        return deps;
                    }

                    float2 dir = delta / length;
                    float2 left = new float2(-dir.y, dir.x);
                    float width = building.LotWidth * 8f;
                    float depth = building.LotDepth * 8f;
                    int count = math.min(math.max(1, options.RowCount), (int)math.floor(length / width));
                    // Building front is local +Z; face it toward the road (opposite of the left normal).
                    var rotation = quaternion.LookRotationSafe(new float3(-left.x, 0f, -left.y), math.up());
                    for (int i = 0; i < count; i++)
                    {
                        float2 p = m_Start.xz + dir * (width * (i + 0.5f)) + left * (road.Width * 0.5f + depth * 0.5f);
                        deps = SubmitObject(building, Ground(p), rotation, i + 1, deps);
                    }

                    NotePreview($"road {road.Name} + {count}x {building.Name} ({building.LotWidth}x{building.LotDepth})");
                    return deps;
                }

                default:
                {
                    var building = PickBuilding(options.TestCase, options.PrefabOverride);
                    if (building == null)
                    {
                        return deps;
                    }

                    NotePreview($"{options.TestCase} {building.Name} ({building.LotWidth}x{building.LotDepth}) kind={building.Kind}");
                    return SubmitObject(building, cursor, quaternion.identity, 0, deps);
                }
            }
        }

        private JobHandle SubmitObject(BuildingEntry building, float3 position, quaternion rotation, int index, JobHandle deps)
        {
            var points = new NativeList<ControlPoint>(1, Allocator.TempJob);
            points.Add(new ControlPoint
            {
                m_Position = position,
                m_HitPosition = position,
                m_Rotation = rotation,
                m_HitDirection = math.up(),
                m_OriginalEntity = Entity.Null,
                m_ElementIndex = new int2(-1, -1),
            });

            var handle = CreateDefinitions(
                building.Prefab, Entity.Null, Entity.Null, Entity.Null, Entity.Null, Entity.Null,
                m_City.defaultTheme, points, default(NativeReference<AttachmentData>),
                false, m_City.leftHandTraffic, false, false,
                0f, 0f, 0f, 0f, UnityEngine.Time.deltaTime, Seed(index),
                Snap.None, Game.Tools.AgeMask.Sapling, false, default(PlacementOverrides), deps);
            points.Dispose(handle);
            m_Planned.Add(new SpikeAuditSystem.Expected { Prefab = building.Prefab, Name = building.Name, Position = position, IsRoad = false });
            return handle;
        }

        private void SubmitRoad(RoadEntry road, float3 a, float3 b, int index)
        {
            a = Ground(a.xz);
            b = Ground(b.xz);
            if (math.distance(a.xz, b.xz) < 8f)
            {
                return;
            }

            var curve = new Bezier4x3(a, math.lerp(a, b, 1f / 3f), math.lerp(a, b, 2f / 3f), b);
            var tangent = b - a;
            var course = new NetCourse
            {
                m_Curve = curve,
                m_Length = MathUtils.Length(curve),
                m_FixedIndex = -1,
                m_Elevation = float2.zero,
                m_StartPosition = new CoursePos
                {
                    m_Entity = Entity.Null,
                    m_Position = a,
                    m_Rotation = NetUtils.GetNodeRotation(tangent),
                    m_CourseDelta = 0f,
                    m_Elevation = float2.zero,
                    m_Flags = CoursePosFlags.IsFirst,
                    m_ParentMesh = -1,
                },
                m_EndPosition = new CoursePos
                {
                    m_Entity = Entity.Null,
                    m_Position = b,
                    m_Rotation = NetUtils.GetNodeRotation(tangent),
                    m_CourseDelta = 1f,
                    m_Elevation = float2.zero,
                    m_Flags = CoursePosFlags.IsLast,
                    m_ParentMesh = -1,
                },
            };

            var buffer = m_ToolOutputBarrier.CreateCommandBuffer();
            var e = buffer.CreateEntity();
            buffer.AddComponent(e, new CreationDefinition { m_Prefab = road.Prefab, m_RandomSeed = Seed(index).GetRandom(0).NextInt() });
            buffer.AddComponent(e, course);
            buffer.AddComponent(e, default(Updated));
            m_Planned.Add(new SpikeAuditSystem.Expected { Prefab = road.Prefab, Name = road.Name, Position = a, End = b, IsRoad = true });
        }

        private RandomSeed Seed(int index)
        {
            while (m_Seeds.Count <= index)
            {
                m_Seeds.Add(RandomSeed.Next());
            }

            return m_Seeds[index];
        }

        private float3 Ground(float2 xz)
        {
            var heights = m_Terrain.GetHeightData();
            var p = new float3(xz.x, 0f, xz.y);
            p.y = TerrainUtils.SampleHeight(ref heights, p);
            return p;
        }

        private BuildingEntry PickBuilding(SpikeCase kind, string overrideName)
        {
            if (!string.IsNullOrWhiteSpace(overrideName))
            {
                var named = m_Catalog.FindBuilding(overrideName.Trim());
                if (named != null)
                {
                    return named;
                }
            }

            IEnumerable<BuildingEntry> candidates = m_Catalog.Buildings.Where(b => !b.FromMod && (b.Kind & EntryKind.Upgrade) == 0);
            switch (kind)
            {
                case SpikeCase.Growable:
                    candidates = candidates.Where(b => (b.Kind & EntryKind.Growable) != 0 && (b.Kind & EntryKind.Signature) == 0 && b.AreaType == "Residential" && b.LotWidth >= 1);
                    break;
                case SpikeCase.Park:
                    candidates = candidates.Where(b => (b.Kind & EntryKind.Park) != 0);
                    break;
                case SpikeCase.Parking:
                    candidates = candidates.Where(b => (b.Kind & EntryKind.Parking) != 0);
                    break;
                default:
                    candidates = candidates.Where(b => (b.Kind & EntryKind.Service) != 0 && (b.Kind & (EntryKind.Park | EntryKind.Parking | EntryKind.Signature)) == 0 && b.LotWidth <= 4 && b.LotDepth <= 4);
                    break;
            }

            var pick = candidates.OrderBy(b => b.LotWidth * b.LotDepth).ThenBy(b => b.Name).FirstOrDefault();
            if (pick == null)
            {
                NotePreview($"no candidate for {kind}");
            }

            return pick;
        }

        private RoadEntry PickRoad(string overrideName)
        {
            if (!string.IsNullOrWhiteSpace(overrideName))
            {
                var named = m_Catalog.FindRoad(overrideName.Trim());
                if (named != null)
                {
                    return named;
                }
            }

            var pick = m_Catalog.Roads.Where(r => !r.FromMod && r.Width >= 7f).OrderBy(r => r.Width).ThenBy(r => r.Name).FirstOrDefault();
            if (pick == null)
            {
                NotePreview("no road candidate");
            }

            return pick;
        }

        private void NotePreview(string text)
        {
            if (text == m_LastPreview)
            {
                return;
            }

            m_LastPreview = text;
            Mod.log.Info($"spike: preview {text}");
        }

        private void LogPreviewState(bool allow)
        {
            int temps = m_TempQuery.CalculateEntityCount();
            using var errors = m_ErrorQuery2.ToEntityArray(Allocator.Temp);
            Mod.log.Info($"spike: click allowApply={allow} temps={temps} errors={errors.Length} planned={m_Planned.Count}");
            foreach (var e in errors.Take(20))
            {
                Mod.log.Info($"spike:   error on {Describe(e)} reasons={string.Join(",", ErrorReasons(e))}");
            }
        }

        private IEnumerable<string> ErrorReasons(Entity e)
        {
            if (!EntityManager.HasBuffer<Game.Notifications.IconElement>(e))
            {
                yield break;
            }

            var icons = EntityManager.GetBuffer<Game.Notifications.IconElement>(e, true);
            var reasons = new List<string>();
            foreach (var icon in icons)
            {
                if (EntityManager.HasComponent<PrefabRef>(icon.m_Icon))
                {
                    var prefab = EntityManager.GetComponentData<PrefabRef>(icon.m_Icon).m_Prefab;
                    if (EntityManager.HasComponent<ToolErrorData>(prefab))
                    {
                        reasons.Add(EntityManager.GetComponentData<ToolErrorData>(prefab).m_Error.ToString());
                    }
                }
            }

            foreach (var r in reasons)
            {
                yield return r;
            }
        }

        private string Describe(Entity e)
        {
            string name = "?";
            if (EntityManager.HasComponent<PrefabRef>(e) && m_Prefabs.TryGetPrefab<PrefabBase>(EntityManager.GetComponentData<PrefabRef>(e).m_Prefab, out var prefab) && prefab != null)
            {
                name = prefab.name;
            }

            string kind = EntityManager.HasComponent<Game.Buildings.Building>(e) ? "building"
                : EntityManager.HasComponent<Edge>(e) ? "edge"
                : EntityManager.HasComponent<Node>(e) ? "node"
                : "other";
            return $"{e} {kind} {name}";
        }
    }
}
