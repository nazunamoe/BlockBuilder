using System.Collections.Generic;
using Colossal.Mathematics;
using Game.Common;
using Game.Net;
using Game.Prefabs;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;

namespace BlockBuilder
{
    /// <summary>
    /// "Undo last build": what the last apply created is remembered by prefab and position (entities
    /// do not exist yet when the build is submitted). Undo finds those objects and streets again and
    /// removes them through the game's own delete path (delete definitions, then apply), the same way
    /// the bulldozer does, so sub-objects, lanes and connections are cleaned up by the game.
    /// </summary>
    public partial class BuilderTool
    {
        private struct Submission
        {
            public bool IsRoad;
            public Entity Prefab;
            /// <summary>Object position, or the street's start.</summary>
            public float3 A;
            /// <summary>The street's end.</summary>
            public float3 B;
        }

        /// <summary>Everything sent to the game for the current preview.</summary>
        private readonly List<Submission> m_Submissions = new List<Submission>();

        /// <summary>What the last successful build created (empty when there is nothing to undo).</summary>
        private List<Submission> m_LastBuild = new List<Submission>();

        private enum UndoPhase
        {
            None,
            /// <summary>Preview cleared this frame; deletes are submitted on the next one.</summary>
            Clearing,
            /// <summary>Delete definitions submitted, waiting for them to settle (see Settle).</summary>
            Settling,
        }

        private UndoPhase m_Undo;
        private bool m_UndoRequested;

        internal int UndoCount => m_LastBuild.Count;

        internal bool Undoing => m_Undo != UndoPhase.None;

        /// <summary>Panel button.</summary>
        internal void RequestUndo() => m_UndoRequested = true;

        private void NoteSubmittedObject(Entity prefab, float3 position) =>
            m_Submissions.Add(new Submission { Prefab = prefab, A = position });

        private void NoteSubmittedRoad(Entity prefab, float3 a, float3 b) =>
            m_Submissions.Add(new Submission { IsRoad = true, Prefab = prefab, A = a, B = b });

        /// <summary>Called when a build is applied: it becomes the one Undo removes.</summary>
        private void RememberBuild()
        {
            m_LastBuild = new List<Submission>(m_Submissions);
            m_Submissions.Clear();
        }

        /// <summary>Runs from OnUpdate before anything else; true when it handled the frame.</summary>
        private bool UpdateUndo(ref JobHandle deps)
        {
            if (m_UndoRequested)
            {
                m_UndoRequested = false;
                if (m_LastBuild.Count == 0)
                {
                    Note("nothing to undo", "되돌릴 짓기가 없습니다");
                    return false;
                }

                // The preview's own definitions would be applied together with the deletes.
                deps = ClearPreview(deps);
                ResetAll();
                m_Undo = UndoPhase.Clearing;
                return true;
            }

            if (m_Undo == UndoPhase.Clearing)
            {
                int found = SubmitDeletes();
                if (found == 0)
                {
                    m_Undo = UndoPhase.None;
                    m_LastBuild.Clear();
                    Note("the last build is already gone", "지난번에 지은 것이 이미 남아 있지 않습니다");
                    return true;
                }

                Mod.log.Info($"builder: undo: deleting {found} of {m_LastBuild.Count} built objects/streets");
                m_Undo = UndoPhase.Settling;
                StartApply();
                return true;
            }

            return false;
        }

        /// <summary>Settle finished an undo: apply on success, drop the deletes on failure.</summary>
        private JobHandle FinishUndo(bool allowed, JobHandle deps)
        {
            m_Undo = UndoPhase.None;
            if (!allowed)
            {
                Note("undo was blocked by the game; nothing removed", "게임이 되돌리기를 막았습니다. 아무것도 지우지 않았습니다");
                return ClearPreview(deps);
            }

            Note($"removed the last build ({m_Submitted})", $"지난번 짓기를 되돌렸습니다 ({m_Submitted}개)");
            m_LastBuild.Clear();
            m_Submissions.Clear();
            m_Submitted = 0;
            m_PlanKey = string.Empty;
            InvalidateGraph(); // streets were removed
            applyMode = ApplyMode.Apply;
            return DestroyDefinitions(m_DefinitionQuery, m_ToolOutputBarrier, deps);
        }

        private int SubmitDeletes()
        {
            m_Submitted = 0;
            var used = new HashSet<Entity>();
            var buffer = m_ToolOutputBarrier.CreateCommandBuffer();

            // Objects: buildings and trees at exactly the submitted spot (a building that levelled
            // up has another prefab, so buildings match by position only).
            using (var query = EntityManager.CreateEntityQuery(
                ComponentType.ReadOnly<Game.Objects.Transform>(), ComponentType.ReadOnly<PrefabRef>(),
                ComponentType.Exclude<Temp>(), ComponentType.Exclude<Deleted>(), ComponentType.Exclude<Owner>()))
            {
                query.CompleteDependency();
                using var entities = query.ToEntityArray(Allocator.Temp);
                using var transforms = query.ToComponentDataArray<Game.Objects.Transform>(Allocator.Temp);
                using var prefabs = query.ToComponentDataArray<PrefabRef>(Allocator.Temp);
                var bySpot = new Dictionary<long, List<int>>();
                for (int i = 0; i < entities.Length; i++)
                {
                    long key = SpotKey(transforms[i].m_Position);
                    if (!bySpot.TryGetValue(key, out var list))
                    {
                        bySpot[key] = list = new List<int>();
                    }

                    list.Add(i);
                }

                foreach (var s in m_LastBuild)
                {
                    if (s.IsRoad)
                    {
                        continue;
                    }

                    bool building = EntityManager.HasComponent<BuildingData>(s.Prefab);
                    int best = -1;
                    float bestDistance = 0.75f;
                    foreach (long key in SpotKeysAround(s.A))
                    {
                        if (!bySpot.TryGetValue(key, out var list))
                        {
                            continue;
                        }

                        foreach (int i in list)
                        {
                            float d = math.distance(transforms[i].m_Position.xz, s.A.xz);
                            bool sameKind = building ? EntityManager.HasComponent<Game.Buildings.Building>(entities[i]) : prefabs[i].m_Prefab == s.Prefab;
                            if (d < bestDistance && sameKind && !used.Contains(entities[i]))
                            {
                                best = i;
                                bestDistance = d;
                            }
                        }
                    }

                    if (best < 0)
                    {
                        continue;
                    }

                    used.Add(entities[best]);
                    var e = buffer.CreateEntity();
                    buffer.AddComponent(e, new CreationDefinition { m_Original = entities[best], m_Flags = CreationFlags.Delete });
                    buffer.AddComponent(e, new ObjectDefinition
                    {
                        m_Position = transforms[best].m_Position,
                        m_Rotation = transforms[best].m_Rotation,
                        m_LocalPosition = transforms[best].m_Position,
                        m_LocalRotation = transforms[best].m_Rotation,
                        m_Scale = new float3(1f),
                        m_ParentMesh = -1,
                        m_Probability = 100,
                        m_PrefabSubIndex = -1,
                    });
                    buffer.AddComponent(e, default(Updated));
                    m_Submitted++;
                }
            }

            // Streets: every edge of the street's prefab lying along a built street (later builds
            // may have split it at junctions, so pieces are matched, not whole streets).
            bool anyRoad = m_LastBuild.Exists(s => s.IsRoad);
            if (anyRoad)
            {
                using var query = EntityManager.CreateEntityQuery(
                    ComponentType.ReadOnly<Edge>(), ComponentType.ReadOnly<Curve>(), ComponentType.ReadOnly<PrefabRef>(),
                    ComponentType.Exclude<Temp>(), ComponentType.Exclude<Deleted>(), ComponentType.Exclude<Owner>());
                query.CompleteDependency();
                using var entities = query.ToEntityArray(Allocator.Temp);
                foreach (var e in entities)
                {
                    var prefab = EntityManager.GetComponentData<PrefabRef>(e).m_Prefab;
                    var curve = EntityManager.GetComponentData<Curve>(e).m_Bezier;
                    bool ours = false;
                    foreach (var s in m_LastBuild)
                    {
                        if (s.IsRoad && s.Prefab == prefab && OnSegment(curve.a, s) && OnSegment(curve.d, s) && OnSegment(MathUtils.Position(curve, 0.5f), s))
                        {
                            ours = true;
                            break;
                        }
                    }

                    if (!ours || !used.Add(e))
                    {
                        continue;
                    }

                    var edge = EntityManager.GetComponentData<Edge>(e);
                    var course = new NetCourse
                    {
                        m_Curve = curve,
                        m_Length = MathUtils.Length(curve),
                        m_FixedIndex = -1,
                        m_StartPosition = new CoursePos
                        {
                            m_Entity = edge.m_Start,
                            m_Position = curve.a,
                            m_Rotation = NetUtils.GetNodeRotation(MathUtils.StartTangent(curve)),
                            m_CourseDelta = 0f,
                            m_Flags = CoursePosFlags.IsFirst,
                            m_ParentMesh = -1,
                        },
                        m_EndPosition = new CoursePos
                        {
                            m_Entity = edge.m_End,
                            m_Position = curve.d,
                            m_Rotation = NetUtils.GetNodeRotation(MathUtils.EndTangent(curve)),
                            m_CourseDelta = 1f,
                            m_Flags = CoursePosFlags.IsLast,
                            m_ParentMesh = -1,
                        },
                    };
                    var d = buffer.CreateEntity();
                    buffer.AddComponent(d, new CreationDefinition { m_Original = e, m_Flags = CreationFlags.Delete });
                    buffer.AddComponent(d, course);
                    buffer.AddComponent(d, default(Updated));
                    m_Submitted++;
                }
            }

            return m_Submitted;
        }

        private static bool OnSegment(float3 p, Submission s)
        {
            var a = s.A.xz;
            var b = s.B.xz;
            var ab = b - a;
            float len2 = math.lengthsq(ab);
            float t = len2 < 1e-6f ? 0f : math.clamp(math.dot(p.xz - a, ab) / len2, 0f, 1f);
            return math.distance(p.xz, a + ab * t) < 1f;
        }

        private const float kSpot = 4f;

        private static long SpotKey(float3 p) => ((long)(int)math.floor(p.x / kSpot) << 32) ^ (uint)(int)math.floor(p.z / kSpot);

        private static IEnumerable<long> SpotKeysAround(float3 p)
        {
            int x = (int)math.floor(p.x / kSpot);
            int z = (int)math.floor(p.z / kSpot);
            for (int dx = -1; dx <= 1; dx++)
            {
                for (int dz = -1; dz <= 1; dz++)
                {
                    yield return ((long)(x + dx) << 32) ^ (uint)(z + dz);
                }
            }
        }
    }
}
