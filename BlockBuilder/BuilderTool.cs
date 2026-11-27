using System.Collections.Generic;
using System.Linq;
using BlockBuilder.Core;
using Colossal.Collections;
using Colossal.Mathematics;
using Game.City;
using Game.Common;
using Game.Net;
using Game.Prefabs;
using Game.Rendering;
using Game.Simulation;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace BlockBuilder
{
    /// <summary>
    /// Area fill tool. Draw a polygon, the layout engine plans lots (and optional streets) from the
    /// active palette, the plan is submitted through the same native definition path the spike
    /// proved, and a click commits it once the native preview has settled without errors.
    /// Holds no state in entities or saves; activates only from the settings button.
    /// </summary>
    public partial class BuilderTool : ObjectToolBaseSystem
    {
        private enum State
        {
            Drawing,
            Planned,
        }

        private struct RoadRef
        {
            public Entity Edge;
            public Bezier4x3 Curve;
        }

        private struct NetCollector : INativeQuadTreeIterator<Entity, QuadTreeBoundsXZ>
        {
            public Bounds2 Box;
            public NativeList<Entity> Items;

            public bool Intersect(QuadTreeBoundsXZ bounds) => MathUtils.Intersect(Box, bounds.m_Bounds.xz);

            public void Iterate(QuadTreeBoundsXZ bounds, Entity item)
            {
                if (Intersect(bounds))
                {
                    Items.Add(item);
                }
            }
        }

        private const float kCloseDistance = 8f;
        private const int kMinSettleFrames = 8;
        private const int kStableFrames = 4;
        private const int kMaxSettleFrames = 240;

        public override string toolID => "BlockBuilder.Builder";

        public override PrefabBase GetPrefab() => null;

        public override bool TrySetPrefab(PrefabBase prefab) => false;

        private EntityQuery m_DefinitionQuery;
        private EntityQuery m_AllDefinitions;
        private EntityQuery m_BuildingQuery;
        private EntityQuery m_TempQuery;
        private EntityQuery m_ErrorEntities;
        private CityConfigurationSystem m_City;
        private TerrainSystem m_Terrain;
        private CatalogSystem m_Catalog;
        private DefaultToolSystem m_DefaultTool;
        private Game.Net.SearchSystem m_NetSearch;
        private OverlayRenderSystem m_Overlay;

        private readonly List<float3> m_Points = new List<float3>();
        private readonly List<RoadRef> m_RoadRefs = new List<RoadRef>();
        private readonly List<RandomSeed> m_Seeds = new List<RandomSeed>();
        private readonly List<Placement> m_Fixed = new List<Placement>();
        private List<RoadSegment> m_LastRoads = new List<RoadSegment>();
        private List<Obb> m_LastObstacles = new List<Obb>();
        private State m_State;
        private LayoutResult m_Plan;
        private string m_PlanKey = string.Empty;
        private int m_Submitted;
        private bool m_PendingClick;
        private int m_SettleFrames;
        private int m_StableFrames;
        private int m_LastTempCount = -1;
        private float3 m_Cursor;
        private bool m_HasCursor;

        protected override void OnCreate()
        {
            base.OnCreate();
            m_DefinitionQuery = GetDefinitionQuery();
            m_AllDefinitions = GetEntityQuery(ComponentType.ReadOnly<CreationDefinition>());
            m_BuildingQuery = GetEntityQuery(ComponentType.ReadOnly<Game.Buildings.Building>(), ComponentType.ReadOnly<Game.Objects.Transform>(), ComponentType.ReadOnly<PrefabRef>(), ComponentType.Exclude<Temp>(), ComponentType.Exclude<Deleted>());
            m_TempQuery = GetEntityQuery(ComponentType.ReadOnly<Temp>(), ComponentType.Exclude<Deleted>());
            m_ErrorEntities = GetEntityQuery(ComponentType.ReadOnly<Error>());
            m_City = World.GetOrCreateSystemManaged<CityConfigurationSystem>();
            m_Terrain = World.GetOrCreateSystemManaged<TerrainSystem>();
            m_Catalog = World.GetOrCreateSystemManaged<CatalogSystem>();
            m_DefaultTool = World.GetOrCreateSystemManaged<DefaultToolSystem>();
            m_NetSearch = World.GetOrCreateSystemManaged<Game.Net.SearchSystem>();
            m_Overlay = World.GetOrCreateSystemManaged<OverlayRenderSystem>();
            m_SnapReady = true;
        }

        public void Activate()
        {
            if (!m_Catalog.Ready)
            {
                m_Catalog.Scan(false);
            }

            AnarchyLink.Register(this);
            m_ToolSystem.activeTool = this;
        }

        /// <summary>Overlap allowances in effect: only while Anarchy is on (the game blocks overlaps otherwise).</summary>
        internal static double EffectiveSide(Setting options) => MaxActive(options) ? options.OverlapSide : 0;

        internal static double EffectiveBack(Setting options) => MaxActive(options) ? options.OverlapBack : 0;

        /// <summary>Max density packs with overlaps, which the game only allows while Anarchy is on.</summary>
        private static bool MaxActive(Setting options) => options.Density == FillDensity.Custom && AnarchyLink.Enabled;

        private static bool WantsOverlap(Setting options) => options.Density == FillDensity.Custom && (options.OverlapSide > 0 || options.OverlapBack > 0);

        /// <summary>Gap between lots along a road for the density level.</summary>
        internal static double DensityGap(Setting options) => options.Density == FillDensity.Relaxed ? 4 : options.Density == FillDensity.Custom ? options.CustomGap : 0;

        /// <summary>Search step along the road: finer steps find lots in odd gaps.</summary>
        internal static double DensityStep(Setting options) => options.Density == FillDensity.Dense || options.Density == FillDensity.Custom ? 2 : 8;

        protected override void OnStartRunning()
        {
            base.OnStartRunning();
            applyAction.shouldBeEnabled = true;
            secondaryApplyAction.shouldBeEnabled = true;
            // The tool stops whenever the settings menu opens. Keep the drawn area and designated
            // items so changed options (palette, shuffle, mode) act on it; replan on return.
            m_PendingClick = false;
            m_PlanKey = string.Empty;
            m_Submitted = 0;
            InvalidateGraph(); // roads may have changed while another tool was active
            Mod.log.Info($"builder: started state={m_State} corners={m_Points.Count} designated={m_Fixed.Count}");
        }

        protected override void OnStopRunning()
        {
            applyAction.shouldBeEnabled = false;
            secondaryApplyAction.shouldBeEnabled = false;
            m_PendingClick = false;
            m_Moving = null; // a drag interrupted by the menu is cancelled, not dropped later
            if (m_StrokeActive)
            {
                FinishStroke();
            }

            m_PlanKey = string.Empty;
            m_Submitted = 0;
            m_Submissions.Clear();
            m_Undo = UndoPhase.None; // an undo cut short by the menu is simply not done
            m_AllDefinitions.CompleteDependency();
            EntityManager.DestroyEntity(m_AllDefinitions);
            Mod.log.Info("builder: stopped (area kept)");
            base.OnStopRunning();
        }

        protected override void OnGamePreload(Colossal.Serialization.Entities.Purpose purpose, Game.GameMode mode)
        {
            base.OnGamePreload(purpose, mode);
            ResetAll();
            m_LastBuild.Clear(); // positions of another city's build mean nothing here
        }

        private void ResetAll()
        {
            m_Frozen.Clear();
            m_Moving = null;
            m_Picked = false;
            m_Paint?.Clear();
            m_Regions = new List<PaintRegion>();
            m_StrokeActive = false;
            m_LastPaint = new float2(float.MaxValue);
            m_Points.Clear();
            m_Fixed.Clear();
            m_State = State.Drawing;
            m_Plan = null;
            m_PlanKey = string.Empty;
            m_Submitted = 0;
            m_PendingClick = false;
        }

        public override void InitializeRaycast()
        {
            base.InitializeRaycast();
            m_ToolRaycastSystem.typeMask = TypeMask.Terrain;
            m_ToolRaycastSystem.collisionMask = CollisionMask.OnGround;
        }

        private JobHandle ClearPreview(JobHandle deps)
        {
            m_Submitted = 0;
            m_Submissions.Clear();
            m_PlanKey = string.Empty;
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

            m_HasCursor = GetRaycastResult(out ControlPoint cursor);
            if (m_HasCursor)
            {
                m_Cursor = cursor.m_HitPosition;
            }

            if (m_ClearRequested)
            {
                m_ClearRequested = false;
                inputDeps = ClearPreview(inputDeps);
                ResetAll();
                Note("area cleared", "영역을 지웠습니다");
                return inputDeps;
            }

            if (UpdateUndo(ref inputDeps))
            {
                DrawOverlay();
                return inputDeps;
            }

            if (m_PendingClick)
            {
                inputDeps = Settle(inputDeps);
                DrawOverlay();
                return inputDeps;
            }

            if (m_ApplyRequested)
            {
                m_ApplyRequested = false;
                if (m_State == State.Planned && PlanKey(options) == m_PlanKey)
                {
                    StartApply();
                    DrawOverlay();
                    return inputDeps;
                }
            }

            // The game's Apply/Cancel actions do not fire while a modifier is held, so Shift+click
            // is read from the raw mouse. Nothing reacts while the cursor is over the UI (the panel),
            // so a click on a panel button can never place or commit anything.
            var keyboard = UnityEngine.InputSystem.Keyboard.current;
            var mouse = UnityEngine.InputSystem.Mouse.current;
            bool shift = keyboard != null && keyboard.shiftKey.isPressed;
            bool overUI = Game.Input.InputManager.instance != null && Game.Input.InputManager.instance.mouseOverUI;
            FinishStrokeIfReleased(mouse); // before the panel check: a stroke may be released over the panel
            if (overUI && m_Moving != null && mouse != null && !mouse.leftButton.isPressed)
            {
                m_Moving = null; // dropped on the panel: cancel the move
            }

            if (overUI)
            {
                DrawOverlay();
                return m_State == State.Planned ? ReplanIfChanged(options, inputDeps) : inputDeps;
            }
            bool shiftLeft = kDesignateEnabled && shift && !overUI && mouse != null && mouse.leftButton.wasPressedThisFrame;
            bool shiftRight = kDesignateEnabled && shift && !overUI && mouse != null && mouse.rightButton.wasPressedThisFrame;

            if (shiftRight && m_State == State.Planned)
            {
                if (m_Fixed.Count > 0)
                {
                    // Remove the designated item under the cursor; elsewhere, the last one placed.
                    int index = m_Fixed.Count - 1;
                    if (m_HasCursor)
                    {
                        var p = new Vec2(m_Cursor.x, m_Cursor.z);
                        int hit = m_Fixed.FindLastIndex(f => Polygon.PointInObb(f.Box, p));
                        if (hit >= 0)
                        {
                            index = hit;
                        }
                    }

                    m_Fixed.RemoveAt(index);
                    m_PlanKey = string.Empty;
                    Note($"removed designated item #{index + 1}, {m_Fixed.Count} left", $"지정 항목을 지웠습니다 (남은 개수 {m_Fixed.Count})");
                }
                else
                {
                    Note("no designated item to remove", "지울 지정 항목이 없습니다");
                }

                DrawOverlay();
                return inputDeps;
            }

            bool ctrl = keyboard != null && keyboard.ctrlKey.isPressed;
            if (UpdateMove(options, mouse, ctrl))
            {
                DrawOverlay();
                return m_State == State.Planned ? ReplanIfChanged(options, inputDeps) : inputDeps;
            }

            if (options.Selection == AreaSelect.Paint)
            {
                if (shiftLeft)
                {
                    if (m_State == State.Planned && m_HasCursor)
                    {
                        Designate(options);
                    }
                    else
                    {
                        Note("paint blocks first, then shift+click to place a building", "먼저 블록을 칠한 뒤 Shift+클릭으로 건물을 놓으세요");
                    }
                }

                if (mouse != null && mouse.rightButton.wasPressedThisFrame && !shift && (m_Paint == null || m_Paint.IsEmpty) && !m_StrokeActive)
                {
                    // Nothing painted: right click leaves the tool, as in draw mode.
                    ResetAll();
                    m_ToolSystem.activeTool = m_DefaultTool;
                    return inputDeps;
                }

                Paint(options, mouse, shift);
                DrawOverlay();
                if (m_State == State.Planned)
                {
                    return ReplanIfChanged(options, inputDeps);
                }

                return m_Submitted > 0 ? ClearPreview(inputDeps) : inputDeps;
            }

            if (!shift && secondaryApplyAction.WasPressedThisFrame())
            {
                if (m_State == State.Planned && m_Picked)
                {
                    // A picked block has hundreds of corners: clear it instead of removing them one by one.
                    inputDeps = ClearPreview(inputDeps);
                    ResetAll();
                    Mod.log.Info("builder: picked block cleared");
                }
                else if (m_State == State.Planned)
                {
                    m_State = State.Drawing;
                    Mod.log.Info("builder: back to drawing");
                    inputDeps = ClearPreview(inputDeps);
                }
                else if (m_Points.Count > 0)
                {
                    m_Points.RemoveAt(m_Points.Count - 1);
                }
                else
                {
                    ResetAll();
                    m_ToolSystem.activeTool = m_DefaultTool;
                }

                DrawOverlay();
                return inputDeps;
            }

            bool click = !shift && !ctrl && applyAction.WasPressedThisFrame();
            if (m_State == State.Drawing)
            {
                if (shiftLeft)
                {
                    Note("close the area first, then shift+click to designate", "영역을 먼저 닫은 뒤 Shift+클릭으로 지정하세요");
                }

                UpdateSnap(options);
                if (click && m_HasCursor)
                {
                    if (m_Points.Count >= 3 && (math.distance(m_Cursor.xz, m_Points[0].xz) < kCloseDistance || math.distance(m_Snapped.xz, m_Points[0].xz) < 0.5f))
                    {
                        double drawn = System.Math.Abs(Polygon.SignedArea(m_Points.Select(p => new Vec2(p.x, p.z)).ToList()));
                        if (drawn > MaxAreaOf(options))
                        {
                            Note($"area {drawn:0} m2 is over the {MaxAreaOf(options)} m2 limit; draw a smaller area or raise the limit in the panel", $"영역 {drawn:0}㎡가 제한 {MaxAreaOf(options)}㎡를 넘습니다. 더 작게 그리거나 패널에서 제한을 올리세요");
                            DrawOverlay();
                            return inputDeps;
                        }

                        m_State = State.Planned;
                        m_PlanKey = string.Empty;
                        m_Frozen.Clear();
                        Mod.log.Info($"builder: area closed with {m_Points.Count} corners");
                    }
                    else
                    {
                        m_Points.Add(m_Snapped);
                    }
                }

                DrawOverlay();
                return m_Submitted > 0 ? ClearPreview(inputDeps) : inputDeps;
            }

            // Planned
            if (shiftLeft)
            {
                if (m_HasCursor)
                {
                    Designate(options);
                }
                else
                {
                    Mod.log.Info("builder: shift+click ignored, cursor is not on the terrain");
                }
            }

            if (PlanKey(options) != m_PlanKey)
            {
                inputDeps = ReplanIfChanged(options, inputDeps);
            }
            else if (click)
            {
                StartApply();
            }

            DrawOverlay();
            return inputDeps;
        }

        /// <summary>Build the current preview once it has settled (left click in draw mode, or the Build button).</summary>
        private void StartApply()
        {
            if (m_Submitted == 0)
            {
                Note("nothing to build (empty plan); check the list and the area", "지을 것이 없습니다. 리스트와 영역을 확인하세요");
                return;
            }

            m_PendingClick = true;
            m_SettleFrames = 0;
            m_StableFrames = 0;
            m_LastTempCount = -1;
        }

        // ---- Brush painting (like Placemaker's paint mode): roads are walls, lots fill the paint ----

        /// <summary>Roads around the first stroke are collected this far out; strokes further away rebuild.</summary>
        private const float kPaintRoadRadius = 800f;
        private const float kDefaultBrush = 16f;

        private PaintArea m_Paint;
        private float2 m_PaintCenter = new float2(float.MaxValue);
        private bool m_PaintStale;
        private List<PaintRegion> m_Regions = new List<PaintRegion>();
        private float2 m_LastPaint = new float2(float.MaxValue);
        private bool m_ApplyRequested;
        private bool m_StrokeActive;
        private bool m_StrokeErase;
        private int m_StrokeStartVersion;
        /// <summary>Outlines shown while a stroke is in progress (no engine rings yet).</summary>
        private List<PaintRegion> m_LiveRegions;
        private int m_StrokeFrames;
        /// <summary>The current area is painted (its rings already reach past the curbs).</summary>
        private bool m_Picked;

        internal bool IsPicked => m_Picked;

        internal int BlockCount => m_Regions.Count;

        internal static float BrushRadius(Setting options) => options.BrushRadius > 0 ? options.BrushRadius : kDefaultBrush;

        /// <summary>Build button in the panel (painting uses the mouse buttons, so it cannot build on click).</summary>
        internal void RequestApply() => m_ApplyRequested = true;

        /// <summary>Roads may have changed (tool restarted, build applied): rebuild the walls at the next stroke.</summary>
        private void InvalidateGraph() => m_PaintStale = true;

        /// <summary>Paint grid with the roads around <paramref name="at"/>; keeps what is painted when rebuilt.</summary>
        private void EnsurePaint(float2 at)
        {
            if (m_Paint != null && !m_PaintStale && math.distance(m_PaintCenter, at) <= kPaintRoadRadius - 400f)
            {
                return;
            }

            var roads = new List<RoadSegment>();
            CollectRoadSegments(new Bounds2(at - kPaintRoadRadius, at + kPaintRoadRadius), roads, new List<RoadRef>());
            var fresh = new PaintArea(roads);
            if (m_Paint != null)
            {
                foreach (var (i, j) in m_Paint.Cells)
                {
                    fresh.PaintCell(i, j);
                }
            }

            m_Paint = fresh;
            m_PaintCenter = at;
            m_PaintStale = false;
        }

        /// <summary>
        /// Left drag paints, right drag or Ctrl+left drag erases. A paint stroke only covers land
        /// reachable from where it started without crossing a road; at the end of a stroke a small gap
        /// to a curb is closed and the painted regions are planned.
        /// </summary>
        private void Paint(Setting options, UnityEngine.InputSystem.Mouse mouse, bool shift)
        {
            if (!m_HasCursor || mouse == null)
            {
                return;
            }

            var keyboard = UnityEngine.InputSystem.Keyboard.current;
            bool ctrl = keyboard != null && keyboard.ctrlKey.isPressed;
            bool pressed = (mouse.leftButton.wasPressedThisFrame || mouse.rightButton.wasPressedThisFrame) && !shift;
            if (pressed && !m_StrokeActive)
            {
                // The press is on the map (this code does not run over the panel).
                EnsurePaint(m_Cursor.xz);
                m_StrokeErase = mouse.rightButton.wasPressedThisFrame || ctrl;
                m_StrokeActive = true;
                m_LimitNoted = false;
                m_StrokeFrames = 0;
                m_StrokeStartVersion = m_Paint.Version;
                m_LastPaint = m_Cursor.xz;
                if (!m_StrokeErase && !m_Paint.BeginStroke(new Vec2(m_Cursor.x, m_Cursor.z), 300, !options.StopAtRoads))
                {
                    m_StrokeActive = false;
                    Note("a stroke cannot start on a road; start on the land you want", "도로 위에서는 칠하기를 시작할 수 없습니다. 칠할 땅에서 시작하세요");
                    return;
                }
            }

            if (!m_StrokeActive)
            {
                return;
            }

            m_Paint.MaxCells = (int)(MaxAreaOf(options) / (m_Paint.Cell * m_Paint.Cell));
            float r = BrushRadius(options);
            var from = new Vec2(m_LastPaint.x, m_LastPaint.y);
            var to = new Vec2(m_Cursor.x, m_Cursor.z);
            if (m_Paint.Stroke(from, to, r, m_StrokeErase) && ++m_StrokeFrames % 4 == 0)
            {
                m_LiveRegions = m_Paint.Regions(rings: false); // outlines only while painting; planned at the end
            }

            if (!m_StrokeErase && m_Paint.AtLimit && !m_LimitNoted)
            {
                m_LimitNoted = true;
                Note($"paint limit reached ({MaxAreaOf(options)} m2); raise it in the panel to paint more", $"칠하기 제한({MaxAreaOf(options)}㎡)에 도달했습니다. 더 칠하려면 패널에서 제한을 올리세요");
            }

            m_LastPaint = m_Cursor.xz;
        }

        private bool m_LimitNoted;

        /// <summary>Ends a stroke once no mouse button is held (also when released over the panel).</summary>
        private void FinishStrokeIfReleased(UnityEngine.InputSystem.Mouse mouse)
        {
            if (m_StrokeActive && (mouse == null || (!mouse.leftButton.isPressed && !mouse.rightButton.isPressed)))
            {
                FinishStroke();
            }
        }

        private void FinishStroke()
        {
            m_StrokeActive = false;
            m_LiveRegions = null;
            if (!m_StrokeErase)
            {
                m_Paint.EndStroke();
            }

            // A click that changed nothing must not re-plan (that would also release moved buildings).
            if (m_Paint.Version != m_StrokeStartVersion)
            {
                SyncPaint();
            }
        }

        /// <summary>After the painted area changes: regions, bounding points, state and a fresh plan.</summary>
        private void SyncPaint()
        {
            m_Regions = m_Paint == null ? new List<PaintRegion>() : m_Paint.Regions();
            m_Points.Clear();
            foreach (var region in m_Regions)
            {
                foreach (var v in region.Ring)
                {
                    m_Points.Add(new float3((float)v.X, m_Cursor.y, (float)v.Y)); // used for bounds only
                }
            }

            m_Picked = m_Regions.Count > 0;
            m_Frozen.Clear(); // the area changed: lay it out afresh
            m_State = m_Picked ? State.Planned : State.Drawing;
            m_PlanKey = string.Empty;
            double area = m_Regions.Sum(x => x.Area);
            if (m_Picked)
            {
                Note($"painted {area:0} m2 in {m_Regions.Count} area(s)", $"칠한 면적 {area:0}m² (영역 {m_Regions.Count}개)");
            }
            else
            {
                m_Status = string.Empty;
            }
        }

        // ---- Moving lots in the preview (Ctrl+drag) ----

        /// <summary>
        /// Lots of the current plan pinned after the first move, so moving one building does not
        /// reshuffle the others. Released when the layout options or the area change.
        /// </summary>
        private readonly List<Placement> m_Frozen = new List<Placement>();
        private string m_FrozenBase = string.Empty;
        private Placement m_Moving;
        private Obb m_MoveGhost;
        private bool m_MoveOk;
        private int m_MoveVersion;

        /// <summary>
        /// The options that decide the layout (not the area, the pins or the brush). Both keys are
        /// built from this one list, so a new layout option cannot be added to only one of them.
        /// </summary>
        private static string LayoutOptionsKey(Setting o) =>
            $"{o.ActivePalette}|{o.Seed}|{o.GenerateRoads}|{o.NewRoadName}|{o.MaxBlockLength}|{o.FillInterior}|{o.PlacementMode}|{o.Scope}|{o.CheckedItems?.Length}|{o.Density}|{o.SizeOrderMode}|{o.SizeKeyMode}|{o.RoadOrderMode}|{o.VariantIndex}|{EffectiveSide(o)}|{EffectiveBack(o)}|{DensityGap(o)}";

        /// <summary>The options whose change releases the moved/pinned lots.</summary>
        private static string OptionsKey(Setting o) => LayoutOptionsKey(o);

        /// <summary>
        /// Ctrl+left drag on a lot of the preview moves it: the lot follows the cursor, snapped against
        /// the nearest road and facing it, and is dropped on release if it overlaps nothing. Returns
        /// true while it handles the mouse (start, drag or drop).
        /// </summary>
        private bool UpdateMove(Setting options, UnityEngine.InputSystem.Mouse mouse, bool ctrl)
        {
            if (mouse == null)
            {
                return false;
            }

            if (m_Moving == null)
            {
                if (!ctrl || !mouse.leftButton.wasPressedThisFrame || m_State != State.Planned || m_Plan == null || !m_HasCursor)
                {
                    return false;
                }

                var at = new Vec2(m_Cursor.x, m_Cursor.z);
                var hit = m_Plan.Placements.LastOrDefault(x => Polygon.PointInObb(x.Box, at));
                if (hit == null)
                {
                    return false; // not on a lot: in paint mode Ctrl+drag erases
                }

                m_Moving = hit;
                m_MoveGhost = hit.Box;
                m_MoveOk = true;
                return true;
            }

            if (mouse.leftButton.isPressed)
            {
                if (m_HasCursor)
                {
                    m_MoveGhost = MoveTarget(m_Moving, new Vec2(m_Cursor.x, m_Cursor.z));
                    m_MoveOk = MoveFits(m_Moving, m_MoveGhost);
                }

                return true;
            }

            // Released: drop.
            var moving = m_Moving;
            m_Moving = null;
            if (Vec2.Distance(moving.Box.Center, m_MoveGhost.Center) < 0.5 && Vec2.Dot(moving.Box.Forward, m_MoveGhost.Forward) > 0.999)
            {
                return true;
            }

            if (!m_MoveOk)
            {
                Note("cannot drop there: it would overlap a building or a road", "그 자리에는 놓을 수 없습니다 (건물이나 도로와 겹침)");
                return true;
            }

            if (m_Frozen.Count == 0)
            {
                foreach (var p in m_Plan.Placements.Where(p => !p.IsFixed))
                {
                    m_Frozen.Add(new Placement { ItemId = p.ItemId, Box = p.Box });
                }

                m_FrozenBase = OptionsKey(options);
            }

            // Plan placements are copies: find the pinned one by item and position.
            bool Same(Placement p) => p.ItemId == moving.ItemId && Vec2.Distance(p.Box.Center, moving.Box.Center) < 0.01;
            var pinned = m_Fixed.FirstOrDefault(Same) ?? m_Frozen.FirstOrDefault(Same);
            if (pinned == null)
            {
                m_Frozen.Add(pinned = new Placement { ItemId = moving.ItemId });
            }

            pinned.Box = m_MoveGhost;
            m_MoveVersion++;
            Note($"moved {moving.ItemId}", $"{moving.ItemId}을(를) 옮겼습니다");
            return true;
        }

        /// <summary>Where a dragged lot goes: against the nearest road and facing it, or at the cursor.</summary>
        private Obb MoveTarget(Placement lot, Vec2 at) => SnapLot(lot.ItemId, lot.Box.Width, lot.Box.Depth, at, lot.Box.Forward);

        private bool MoveFits(Placement lot, Obb box) => LotFits(box, m_Plan?.Placements ?? Enumerable.Empty<Placement>(), lot);

        /// <summary>
        /// A lot placed by hand (Shift+click or drag): against the nearest curb and facing the road,
        /// fitted to curves like the fill; without a road nearby (or for items that need none) it
        /// sits at the point facing <paramref name="forward"/>.
        /// </summary>
        private Obb SnapLot(string itemId, double width, double depth, Vec2 at, Vec2 forward)
        {
            var entry = m_Catalog.FindBuilding(itemId);
            if ((entry == null || entry.RequiresRoad) && LayoutEngine.TrySnapLot(m_LastRoads.Concat(PlannedRoadSegments()).ToList(), at, width, depth, out var box))
            {
                return box;
            }

            return new Obb(at, forward, width, depth);
        }

        /// <summary>
        /// A hand-placed lot may not overlap the given lots, a road or an existing building: one
        /// overlap error in the game blocks the whole build. Dragging passes every lot of the plan;
        /// Shift+click only the pinned ones, as the automatic fill is redone around the new lot.
        /// </summary>
        private bool LotFits(Obb box, IEnumerable<Placement> lots, Placement ignore)
        {
            foreach (var other in lots)
            {
                if (!ReferenceEquals(other, ignore) && box.Overlaps(other.Box, 0.01))
                {
                    return false;
                }
            }

            foreach (var r in m_LastRoads.Concat(PlannedRoadSegments()))
            {
                if (box.Overlaps(Obb.FromSegment(r.A, r.B, r.Width), 0.01))
                {
                    return false;
                }
            }

            foreach (var o in m_LastObstacles)
            {
                if (box.Overlaps(o, 0.01))
                {
                    return false;
                }
            }

            return true;
        }

        // ---- Corner snapping while drawing ----

        private readonly List<RoadSegment> m_SnapRoads = new List<RoadSegment>();
        private readonly List<RoadRef> m_SnapRefs = new List<RoadRef>();
        private float2 m_SnapQueryCenter = new float2(float.MaxValue);
        private int m_SnapQueryFrame;
        private SnapResult m_Snap;
        private float3 m_Snapped;

        private const float kSnapQueryRadius = 96f;

        /// <summary>
        /// Snap options shown by the game's own tool options toolbar (same buttons as the road tool).
        /// The UI module renders that toolbar inside the builder panel and hides it at its usual place.
        /// </summary>
        internal const Snap kSnapMask = Snap.ExistingGeometry | Snap.StraightDirection | Snap.CellLength;

        private bool m_SnapReady;

        public override void GetAvailableSnapMask(out Snap onMask, out Snap offMask)
        {
            onMask = kSnapMask;
            offMask = kSnapMask;
        }

        /// <summary>Persisted in the settings as the bits the player turned off.</summary>
        public override Snap selectedSnap
        {
            get
            {
                var options = Mod.Options;
                return options == null ? Snap.All : ~((Snap)(uint)options.SnapOff & kSnapMask);
            }

            set
            {
                var options = Mod.Options;
                // The base OnCreate resets this to All; keep the saved choice instead.
                if (!m_SnapReady || options == null)
                {
                    return;
                }

                int off = (int)(uint)(~value & kSnapMask);
                if (off != options.SnapOff)
                {
                    options.SnapOff = off;
                    options.ApplyAndSave();
                }
            }
        }

        private void UpdateSnap(Setting options)
        {
            m_Snap = null;
            m_Snapped = m_Cursor;
            Snap snap = kSnapMask & ~(Snap)(uint)options.SnapOff;
            if (!m_HasCursor || (snap & kSnapMask) == 0)
            {
                return;
            }

            // Re-query nearby roads when the cursor moved away or every second (roads can change).
            m_SnapQueryFrame++;
            if (math.distance(m_SnapQueryCenter, m_Cursor.xz) > kSnapQueryRadius / 3 || m_SnapQueryFrame > 60)
            {
                m_SnapQueryFrame = 0;
                m_SnapQueryCenter = m_Cursor.xz;
                m_SnapRoads.Clear();
                m_SnapRefs.Clear();
                CollectRoadSegments(new Bounds2(m_Cursor.xz - kSnapQueryRadius, m_Cursor.xz + kSnapQueryRadius), m_SnapRoads, m_SnapRefs);
            }

            var corners = m_Points.Select(p => new Vec2(p.x, p.z)).ToList();
            m_Snap = DrawSnap.Snap(
                new Vec2(m_Cursor.x, m_Cursor.z),
                corners,
                m_SnapRoads,
                curbs: (snap & Snap.ExistingGeometry) != 0,
                angles: (snap & Snap.StraightDirection) != 0,
                cellLength: (snap & Snap.CellLength) != 0 ? 8 : 0);
            if (m_Snap.Kind != SnapKind.None)
            {
                m_Snapped = new float3((float)m_Snap.Point.X, m_Cursor.y, (float)m_Snap.Point.Y);
            }
        }

        private string PlanKey(Setting options) =>
            $"{LayoutOptionsKey(options)}|{m_Fixed.Count}|{m_Frozen.Count}|{m_MoveVersion}|{options.Revision}|{options.TreeFill && m_Moving == null}";

        private JobHandle ReplanIfChanged(Setting options, JobHandle deps)
        {
            if (m_Frozen.Count > 0 && OptionsKey(options) != m_FrozenBase)
            {
                // A shuffle or another layout option redoes the whole layout: moved lots are released.
                m_Frozen.Clear();
                Note("layout redone; moved buildings are no longer pinned", "배치를 새로 짰습니다. 옮긴 건물의 고정이 풀렸습니다");
            }

            string key = PlanKey(options);
            if (key == m_PlanKey)
            {
                return deps;
            }

            applyMode = ApplyMode.Clear;
            deps = DestroyDefinitions(m_DefinitionQuery, m_ToolOutputBarrier, deps);
            m_PlanKey = key;
            return PlanAndSubmit(options, deps);
        }

        // ---- State read by the in-game panel (BuilderUISystem) ----

        private string m_Status = string.Empty;
        private bool m_ClearRequested;

        internal bool IsActive => m_ToolSystem.activeTool == this;

        internal bool IsPlanned => m_State == State.Planned;

        internal int CornerCount => m_Points.Count;

        internal int DesignatedCount => m_Fixed.Count;

        internal int PlannedBuildings => m_State == State.Planned && m_Plan != null ? m_Plan.Placements.Count : 0;

        internal int PlannedStreets => m_State == State.Planned && m_Plan != null ? m_Plan.Roads.Count : 0;

        internal IEnumerable<string> PlanWarnings => m_State == State.Planned && m_Plan != null ? m_Plan.Warnings.Select(Localize) : Enumerable.Empty<string>();

        private static readonly System.Text.RegularExpressions.Regex kShallow = new System.Text.RegularExpressions.Regex(
            @"too shallow for a generated street: (\d+) m deep, needs (\d+) m \((\d+) rows of (\d+) m lots \+ (\d+) m street\)");

        private static string Localize(string warning)
        {
            if (!Mod.IsKorean)
            {
                return warning;
            }

            var m = kShallow.Match(warning);
            if (m.Success)
            {
                return $"영역이 얕아 내부 도로를 못 만듭니다: 깊이 {m.Groups[1].Value}m, 필요 {m.Groups[2].Value}m (필지 {m.Groups[4].Value}m × {m.Groups[3].Value}줄 + 도로 {m.Groups[5].Value}m)";
            }

            if (warning.StartsWith("some generated streets do not reach"))
            {
                return "일부 생성 도로가 기존 도로에 닿지 않습니다";
            }

            if (warning.StartsWith("a generated street was dropped"))
            {
                return "지정한 건물과 겹치는 생성 도로를 뺐습니다";
            }

            if (warning.StartsWith("no road frontage"))
            {
                return "영역 안에 도로에 접한 곳이 없습니다. 내부 도로 생성을 켜거나 도로를 포함해 그리세요";
            }

            return warning;
        }

        internal string Status => m_Status;

        internal void Toggle()
        {
            if (IsActive)
            {
                m_ToolSystem.activeTool = m_DefaultTool;
            }
            else
            {
                Activate();
            }
        }

        /// <summary>Forget the drawn area and designated items (applied on the next tool update).</summary>
        internal void ClearArea()
        {
            if (IsActive)
            {
                m_ClearRequested = true;
            }
            else
            {
                ResetAll();
            }

            m_Status = string.Empty;
        }

        private void Note(string en, string ko)
        {
            Mod.log.Info("builder: " + en);
            m_Status = Mod.IsKorean ? ko : en;
        }

        /// <summary>Wait for the Temp preview to stop changing, then apply or report errors.</summary>
        private JobHandle Settle(JobHandle deps)
        {
            m_SettleFrames++;
            int temps = m_TempQuery.CalculateEntityCount();
            m_StableFrames = temps == m_LastTempCount ? m_StableFrames + 1 : 0;
            m_LastTempCount = temps;
            if (m_SettleFrames < kMinSettleFrames || m_StableFrames < kStableFrames && m_SettleFrames < kMaxSettleFrames)
            {
                return deps;
            }

            m_PendingClick = false;
            bool allow = GetAllowApply();
            if (m_Undo == UndoPhase.Settling)
            {
                return FinishUndo(allow, deps);
            }

            if (!allow)
            {
                LogErrors(temps);
                return deps;
            }

            Mod.log.Info($"builder: APPLY buildings={m_Plan?.Placements.Count} roads={m_Plan?.Roads.Count} temps={temps} frames={m_SettleFrames}");
            m_Status = Mod.IsKorean
                ? $"건물 {m_Plan?.Placements.Count}채, 도로 {m_Plan?.Roads.Count}개를 지었습니다"
                : $"built {m_Plan?.Placements.Count} buildings and {m_Plan?.Roads.Count} streets";
            RememberBuild();
            for (int i = 0; i < m_Seeds.Count; i++)
            {
                m_Seeds[i] = RandomSeed.Next();
            }

            m_Points.Clear();
            m_Fixed.Clear();
            m_Frozen.Clear();
            m_Paint?.Clear();
            m_Regions = new List<PaintRegion>();
            m_Picked = false;
            InvalidateGraph(); // the build may have added streets
            m_State = State.Drawing;
            m_Plan = null;
            m_Submitted = 0;
            m_PlanKey = string.Empty;
            applyMode = ApplyMode.Apply;
            return DestroyDefinitions(m_DefinitionQuery, m_ToolOutputBarrier, deps);
        }

        /// <summary>
        /// A lot at <paramref name="at"/> (flush against the nearest road); when it does not fit there it
        /// slides along the road, up to its own width either way, so a gap next to the click is used.
        /// </summary>
        private bool TryFitNear(string itemId, double w, double d, Vec2 at, out Obb fit)
        {
            var first = SnapLot(itemId, w, d, at, new Vec2(0, 1));
            var pinned = m_Fixed.Concat(m_Frozen).ToList();
            if (LotFits(first, pinned, null))
            {
                fit = first;
                return true;
            }

            // Along the frontage: perpendicular to the facing direction of the snapped lot.
            var f = first.Forward;
            var along = new Vec2(-f.Y, f.X);
            for (double off = 1; off <= w; off += 1)
            {
                foreach (double sign in new[] { 1.0, -1.0 })
                {
                    var box = SnapLot(itemId, w, d, at + along * (off * sign), first.Forward);
                    if (LotFits(box, pinned, null))
                    {
                        fit = box;
                        return true;
                    }
                }
            }

            fit = first;
            return false;
        }

        /// <summary>
        /// Shift+click placement is on hold: it always takes the largest building that fits, with no
        /// way to choose one. Bringing it back needs this set to true and the panel parts and the
        /// PlacedOnly option shown again (see DESIGN.md). Not const, so the unused code stays
        /// warning-free.
        /// </summary>
        internal static readonly bool kDesignateEnabled = false;

        /// <summary>
        /// Shift+click: out of the list (or the checked items) pick the building that best fills the
        /// free space at the cursor, i.e. the largest lot that still fits there flush against the
        /// nearest road, and pin it.
        /// </summary>
        private void Designate(Setting options)
        {
            var palette = PaletteStore.Get(options.ActivePalette);
            if (palette == null || palette.Entries.Count == 0)
            {
                Note("no list selected; create one first", "선택된 리스트가 없습니다. 리스트를 먼저 만드세요");
                return;
            }

            var chosen = options.Scope == ItemScope.Checked ? options.Checked : null;
            var at = new Vec2(m_Cursor.x, m_Cursor.z);
            var seen = new HashSet<string>();
            string bestName = null;
            Obb bestBox = default;
            double bestArea = -1;
            double bestDist = double.MaxValue;
            int tried = 0;
            bool uniqueSkipped = false;
            foreach (var e in palette.Entries)
            {
                var entry = m_Catalog.FindBuilding(e.Prefab);
                if (entry == null || !seen.Add(entry.Name) || (chosen != null && !chosen.Contains(entry.Name)))
                {
                    continue;
                }

                // One per city: never pin a second copy of a unique building (placed or moved by hand).
                if (entry.Item.IsUniqueBuilding && m_Fixed.Concat(m_Frozen).Any(f => f.ItemId == entry.Name))
                {
                    uniqueSkipped = true;
                    continue;
                }

                tried++;
                double w = entry.LotWidth * 8;
                double d = entry.LotDepth * 8;
                if (!TryFitNear(entry.Name, w, d, at, out var box))
                {
                    continue;
                }

                double area = w * d;
                double dist = Vec2.Distance(box.Center, at);
                if (area > bestArea + 1e-6 || (System.Math.Abs(area - bestArea) <= 1e-6 && dist < bestDist))
                {
                    bestName = entry.Name;
                    bestBox = box;
                    bestArea = area;
                    bestDist = dist;
                }
            }

            if (bestName == null)
            {
                if (tried == 0 && uniqueSkipped)
                {
                    Note("the unique building is already placed (one per city)", "고유건물은 이미 놓았습니다 (도시에 하나만)");
                }
                else if (tried == 0)
                {
                    Note("no loaded item in the list to place", "놓을 수 있는 항목이 리스트에 없습니다");
                }
                else
                {
                    Note("nothing in the list fits there: every building would overlap a building or a road", "그 자리에 맞는 건물이 없습니다 (모두 건물이나 도로와 겹침)");
                }

                return;
            }

            m_Fixed.Add(new Placement { ItemId = bestName, Box = bestBox });
            m_PlanKey = string.Empty;
            Note($"placed {bestName} (best fit of {tried}, {m_Fixed.Count} total)", $"{bestName} 배치 (후보 {tried}개 중 가장 잘 맞음, 총 {m_Fixed.Count}개)");
        }

        private IEnumerable<RoadSegment> PlannedRoadSegments()
        {
            if (m_Plan == null)
            {
                yield break;
            }

            foreach (var r in m_Plan.Roads)
            {
                yield return new RoadSegment { A = r.A, B = r.B, Width = r.Width, Id = -2 };
            }
        }

        private bool m_OptimizeRequested;

        /// <summary>Panel button: compare several layouts of the current area and keep the best.</summary>
        internal void RequestOptimize()
        {
            m_OptimizeRequested = true;
            m_PlanKey = string.Empty;
        }

        internal const int kDefaultMaxArea = 60000;

        internal static int MaxAreaOf(Setting o) => o.MaxArea > 0 ? o.MaxArea : kDefaultMaxArea;

        /// <summary>Layouts compared by Optimize: fewer for big areas, so the search stays quick.</summary>
        private int OptimizeVariants()
        {
            double area = m_Picked || Mod.Options?.Selection == AreaSelect.Paint
                ? (m_Paint?.PaintedArea ?? 0)
                : System.Math.Abs(Polygon.SignedArea(m_Points.Select(p => new Vec2(p.x, p.z)).ToList()));
            return area <= 30000 ? 8 : area <= 80000 ? 4 : area <= 150000 ? 2 : 1;
        }

        private JobHandle PlanAndSubmit(Setting options, JobHandle deps)
        {
            m_Submitted = 0;
            m_Submissions.Clear();
            var palette = PaletteStore.Get(options.ActivePalette);
            if (palette == null || palette.Entries.Count == 0)
            {
                Note("no list selected; create one in Settings > Block Builder > Builder", "선택된 리스트가 없습니다. 옵션 > Block Builder > 빌더에서 만드세요");
                m_Plan = null;
                return deps;
            }

            var missing = new List<string>();
            var items = palette.ToItems(n => m_Catalog.FindBuilding(n)?.Item, missing);
            if (missing.Count > 0)
            {
                Mod.log.Warn($"builder: {missing.Count} palette entries not loaded, e.g. {string.Join(", ", missing.Take(5))}");
            }

            if (options.Scope == ItemScope.Checked)
            {
                var chosen = options.Checked;
                items = items.Where(i => chosen.Contains(i.Id)).ToList();
                if (items.Count == 0 && options.PlacementMode != FillMode.FixedOnly)
                {
                    Note("no items checked; check items in the panel or switch to the whole list", "체크한 항목이 없습니다. 패널에서 항목을 체크하거나 '리스트 전체'로 바꾸세요");
                }
            }

            var road = m_Catalog.FindRoad(options.NewRoadName?.Trim()) ?? m_Catalog.FindRoad("Small Road");
            var request = new LayoutRequest
            {
                Boundary = m_Points.Select(p => new Vec2(p.x, p.z)).ToList(),
                Items = items,
                Options = new LayoutOptions
                {
                    GenerateRoads = options.GenerateRoads && road != null,
                    NewRoadWidth = road?.Width ?? 16,
                    MaxBlockLength = options.MaxBlockLength,
                    FillInterior = options.FillInterior,
                    Seed = options.Seed,
                    Mode = options.PlacementMode,
                    // A picked block's ring is already the curb line; re-snapping could only distort it.
                    SnapBoundaryToCurbs = !m_Picked,
                    OverlapSide = EffectiveSide(options),
                    OverlapBack = EffectiveBack(options),
                    Gap = DensityGap(options),
                    Order = (SizeOrder)System.Math.Max(0, System.Math.Min(2, options.SizeOrderMode)),
                    SizeBy = (SizeKey)System.Math.Max(0, System.Math.Min(2, options.SizeKeyMode)),
                    Step = DensityStep(options),
                    FitRows = true,
                    Roads = (RoadOrder)System.Math.Max(0, System.Math.Min(3, options.RoadOrderMode)),
                    Variant = options.VariantIndex,
                },
                Fixed = m_Fixed.Concat(m_Frozen).Select(f => new Placement { ItemId = f.ItemId, Box = f.Box }).ToList(),
            };
            CollectRoads(request);
            CollectBuildings(request);
            m_LastRoads = request.ExistingRoads;
            m_LastObstacles = request.Obstacles;

            var watch = System.Diagnostics.Stopwatch.StartNew();
            int variants = m_OptimizeRequested ? OptimizeVariants() : 1;
            m_OptimizeRequested = false;
            if (variants > 1 && request.Fixed.Count == 0 && options.PlacementMode != FillMode.FixedOnly)
            {
                // Try the variants and keep the best; the winner is stored so normal replans repeat it in one pass.
                LayoutResult best = null;
                double bestScore = double.NegativeInfinity;
                int bestVariant = 0;
                for (int v = 0; v < variants; v++)
                {
                    request.Options.Variant = v;
                    var trial = m_Picked ? PlanBlocks(request) : LayoutEngine.Run(request);
                    double score = LayoutEngine.Score(trial);
                    if (score > bestScore + 1e-6)
                    {
                        best = trial;
                        bestScore = score;
                        bestVariant = v;
                    }
                }

                m_Plan = best;
                options.VariantIndex = bestVariant;
                options.ApplyAndSave();
                m_PlanKey = PlanKey(options);
                Note($"optimized: compared {variants} layouts, kept #{bestVariant + 1}", $"최적화: 배치 {variants}가지를 비교해 {bestVariant + 1}번을 골랐습니다");
            }
            else
            {
                request.Options.Variant = options.VariantIndex;
                m_Plan = m_Picked ? PlanBlocks(request) : LayoutEngine.Run(request);
            }
            m_PlanRings = m_Picked ? m_Regions.Select(x => x.Outline).ToList() : new List<List<Vec2>> { m_Plan.Boundary ?? new List<Vec2>() };
            if (options.PlacementMode == FillMode.FixedOnly && m_Fixed.Count == 0)
            {
                Note("'only Shift+clicked buildings' is on and nothing is placed yet", "'Shift+클릭한 것만 짓기'가 켜져 있고 아직 놓은 건물이 없습니다");
            }
            else if (m_Plan.Placements.Count == 0 && items.Count > 0)
            {
                Note("no lot fits; the area may not touch a road (draw over the road or enable streets)", "들어갈 필지가 없습니다. 영역이 도로에 닿는지 확인하거나 내부 도로 생성을 켜세요");
            }

            if (WantsOverlap(options) && !AnarchyLink.Enabled && m_Plan.Placements.Count > 0)
            {
                Note(
                    AnarchyLink.Installed ? "custom overlaps need Anarchy switched on; filled without overlaps" : "custom overlaps need the Anarchy mod; filled without overlaps",
                    AnarchyLink.Installed ? "사용자 지정 겹침은 Anarchy를 켜야 적용됩니다. 지금은 겹침 없이 채웠습니다" : "사용자 지정 겹침은 Anarchy 모드가 필요합니다. 지금은 겹침 없이 채웠습니다");
            }

            Mod.log.Info($"builder: plan density={options.Density} overlap={EffectiveSide(options)}/{EffectiveBack(options)} anarchy={AnarchyLink.Enabled} mode={options.PlacementMode} scope={options.Scope} checked={options.Checked.Count} palette='{palette.Name}' items={items.Count} existingRoadSegments={request.ExistingRoads.Count} existingBuildings={request.Obstacles.Count} -> buildings={m_Plan.Placements.Count} streets={m_Plan.Roads.Count} in {watch.ElapsedMilliseconds} ms");
            foreach (var w in m_Plan.Warnings)
            {
                Mod.log.Info($"builder: warning: {w}");
            }

            if (road != null)
            {
                foreach (var planned in m_Plan.Roads)
                {
                    SubmitRoad(road.Prefab, planned);
                }
            }

            for (int i = 0; i < m_Plan.Placements.Count; i++)
            {
                var p = m_Plan.Placements[i];
                var entry = m_Catalog.FindBuilding(p.ItemId);
                if (entry == null)
                {
                    continue;
                }

                var forward = new float3((float)p.Box.Forward.X, 0f, (float)p.Box.Forward.Y);
                var rotation = quaternion.LookRotationSafe(forward, math.up());
                deps = SubmitObject(entry.Prefab, Ground(p.Box.Center), rotation, i, deps);
            }

            m_TreeCount = 0;
            m_TreeCapped = false;
            if (options.TreeFill && m_Moving == null && options.PlacementMode != FillMode.FixedOnly)
            {
                deps = SubmitTrees(options, deps);
            }

            return deps;
        }

        private int m_TreeCount;

        /// <summary>The plan wanted more trees than the per-plan cap: the panel says so.</summary>
        private bool m_TreeCapped;

        internal int PlannedTrees => m_State == State.Planned ? m_TreeCount : 0;

        internal bool PlannedTreesCapped => m_State == State.Planned && m_TreeCapped;

        internal const int kDefaultTreeSpacing = 8;
        private const int kMaxTrees = 400;

        internal static int TreeDensityOf(Setting o) => o.TreeDensity > 0 ? System.Math.Min(100, o.TreeDensity) : 80;

        internal static int TreeSpacingOf(Setting o) => o.TreeSpacing > 0 ? o.TreeSpacing : kDefaultTreeSpacing;

        private static readonly Game.Tools.AgeMask[] kTreeAges = { Game.Tools.AgeMask.Sapling, Game.Tools.AgeMask.Young, Game.Tools.AgeMask.Mature, Game.Tools.AgeMask.Elderly };

        /// <summary>Checked trees, or every base-game tree when none is checked.</summary>
        private List<TreeEntry> TreeSpecies(Setting options)
        {
            var names = new HashSet<string>((options.TreeNames ?? string.Empty).Split(new[] { '\n' }, System.StringSplitOptions.RemoveEmptyEntries));
            var chosen = m_Catalog.Trees.Where(t => names.Contains(t.Name)).ToList();
            return chosen.Count > 0 ? chosen : m_Catalog.Trees.Where(t => !t.FromMod).ToList();
        }

        /// <summary>
        /// Trees on the ground the plan leaves empty (a separate pass: they are not lots, so moving,
        /// freezing and the counts ignore them). Clear of every lot, road and existing building.
        /// </summary>
        private JobHandle SubmitTrees(Setting options, JobHandle deps)
        {
            var species = TreeSpecies(options);
            if (species.Count == 0 || m_Plan == null)
            {
                Note("no tree prefabs found to plant", "심을 나무 프리팹을 찾지 못했습니다");
                return deps;
            }

            var watch = System.Diagnostics.Stopwatch.StartNew();
            var blocked = m_Plan.Placements.Select(p => p.Box).ToList();
            blocked.AddRange(m_LastRoads.Concat(PlannedRoadSegments()).Select(r => Obb.FromSegment(r.A, r.B, r.Width)));
            blocked.AddRange(m_LastObstacles);
            var rings = m_PlanRings.Where(r => r != null && r.Count >= 3).Cast<IReadOnlyList<Vec2>>().ToList();
            var spots = TreeScatter.Plan(rings, blocked, new TreeOptions
            {
                Spacing = TreeSpacingOf(options),
                Clearance = 2,
                Thinning = 1 - TreeDensityOf(options) / 100.0,
                Seed = options.Seed,
                MaxTrees = kMaxTrees,
                Species = species.Count,
            });

            for (int i = 0; i < spots.Count; i++)
            {
                var spot = spots[i];
                var rotation = quaternion.RotateY((float)spot.Yaw);
                deps = SubmitObject(species[spot.Species % species.Count].Prefab, Ground(spot.Position), rotation, 20000 + i, deps, kTreeAges[spot.Age & 3]);
            }

            m_TreeCount = spots.Count;
            m_TreeCapped = spots.Count >= kMaxTrees;
            Mod.log.Info($"builder: trees={spots.Count} species={species.Count} spacing={TreeSpacingOf(options)} in {watch.ElapsedMilliseconds} ms{(spots.Count >= kMaxTrees ? " (cap reached)" : string.Empty)}");
            return deps;
        }

        /// <summary>Outlines drawn for the current plan (one per painted block, or the drawn area).</summary>
        private List<List<Vec2>> m_PlanRings = new List<List<Vec2>>();

        /// <summary>
        /// Painted blocks are planned one by one (each ring is a curb outline, separated from the
        /// others by roads) with the shared roads and buildings, and merged into one plan.
        /// </summary>
        private LayoutResult PlanBlocks(LayoutRequest shared)
        {
            var merged = new LayoutResult();
            var counts = new Dictionary<string, int>();
            foreach (var block in m_Regions)
            {
                var fixedHere = shared.Fixed.Where(f => Polygon.Contains(block.Ring, f.Box.Center) || (block == m_Regions[0] && !m_Regions.Any(r => Polygon.Contains(r.Ring, f.Box.Center)))).ToList();
                // Lots pinned in other blocks use up max counts too. This block's own pins are left
                // out: the engine counts them itself.
                var fixedElsewhere = shared.Fixed.Where(f => !fixedHere.Contains(f)).ToList();
                var used = new Dictionary<string, int>(counts);
                foreach (var f in fixedElsewhere)
                {
                    used[f.ItemId] = (used.TryGetValue(f.ItemId, out int u) ? u : 0) + 1;
                }

                var request = new LayoutRequest
                {
                    Boundary = block.Ring,
                    ExistingRoads = shared.ExistingRoads,
                    // A lot pinned in another block may reach into this one: fill around it.
                    Obstacles = shared.Obstacles.Concat(fixedElsewhere.Select(f => f.Box)).ToList(),
                    // Each region its own seed, so neighbouring regions do not repeat the same sequence.
                    Options = RegionOptions(shared.Options, m_Regions.IndexOf(block)),
                    Items = RemainingItems(shared.Items, used),
                    Fixed = fixedHere,
                };

                var result = LayoutEngine.Run(request);
                int roadOffset = merged.Roads.Count;
                foreach (var p in result.Placements)
                {
                    if (p.RoadIndex >= 0)
                    {
                        p.RoadIndex += roadOffset;
                    }

                    merged.Placements.Add(p);
                    if (!p.IsFixed)
                    {
                        counts[p.ItemId] = (counts.TryGetValue(p.ItemId, out int n) ? n : 0) + 1;
                    }
                }

                merged.Roads.AddRange(result.Roads);
                foreach (var w in result.Warnings)
                {
                    if (!merged.Warnings.Contains(w))
                    {
                        merged.Warnings.Add(w);
                    }
                }
            }

            merged.Counts = counts;
            return merged;
        }

        private static LayoutOptions RegionOptions(LayoutOptions shared, int index)
        {
            var options = shared.Clone();
            options.Seed = shared.Seed + index * 7919;
            return options;
        }

        /// <summary>
        /// Max counts apply to the whole selection, not per block: each block gets what is left, and
        /// an item whose limit is used up is dropped (MaxCount 0 would mean unlimited).
        /// </summary>
        private static List<PaletteItem> RemainingItems(List<PaletteItem> items, Dictionary<string, int> used)
        {
            var left = new List<PaletteItem>();
            foreach (var i in items)
            {
                if (i.MaxCount <= 0)
                {
                    left.Add(i);
                    continue;
                }

                int remaining = i.MaxCount - (used.TryGetValue(i.Id, out int n) ? n : 0);
                if (remaining > 0)
                {
                    left.Add(new PaletteItem { Id = i.Id, Width = i.Width, Depth = i.Depth, Weight = i.Weight, RequiresRoad = i.RequiresRoad, MaxCount = remaining });
                }
            }

            return left;
        }

        private void CollectRoads(LayoutRequest request)
        {
            m_RoadRefs.Clear();
            var min = new float2(float.MaxValue);
            var max = new float2(float.MinValue);
            foreach (var p in m_Points)
            {
                min = math.min(min, p.xz);
                max = math.max(max, p.xz);
            }

            CollectRoadSegments(new Bounds2(min - 48f, max + 48f), request.ExistingRoads, m_RoadRefs);
        }

        /// <summary>Ground road segments in <paramref name="box"/>; Id indexes <paramref name="refs"/>.</summary>
        private static long Key(Entity e) => ((long)e.Index << 32) | (uint)e.Version;

        private void CollectRoadSegments(Bounds2 box, List<RoadSegment> output, List<RoadRef> refs, List<GraphEdge> graph = null)
        {
            var tree = m_NetSearch.GetNetSearchTree(true, out var dependency);
            dependency.Complete();
            using var found = new NativeList<Entity>(Allocator.Temp);
            var collector = new NetCollector { Box = box, Items = found };
            tree.Iterate(ref collector);

            var seen = new HashSet<Entity>();
            foreach (var e in found)
            {
                if (!seen.Add(e) || !EntityManager.Exists(e) || !EntityManager.HasComponent<Edge>(e) || !EntityManager.HasComponent<Curve>(e) || !EntityManager.HasComponent<PrefabRef>(e)
                    || EntityManager.HasComponent<Temp>(e) || EntityManager.HasComponent<Deleted>(e) || EntityManager.HasComponent<Owner>(e))
                {
                    continue;
                }

                var prefab = EntityManager.GetComponentData<PrefabRef>(e).m_Prefab;
                if (!EntityManager.HasComponent<RoadData>(prefab) || !EntityManager.HasComponent<NetGeometryData>(prefab))
                {
                    continue;
                }

                if (EntityManager.HasComponent<Game.Net.Elevation>(e) && math.any(math.abs(EntityManager.GetComponentData<Game.Net.Elevation>(e).m_Elevation) > 1f))
                {
                    continue; // bridges and tunnels give no ground frontage
                }

                var curve = EntityManager.GetComponentData<Curve>(e).m_Bezier;
                float width = EntityManager.GetComponentData<NetGeometryData>(prefab).m_DefaultWidth;
                int id = refs.Count;
                refs.Add(new RoadRef { Edge = e, Curve = curve });

                int samples = math.clamp((int)math.ceil(MathUtils.Length(curve) / 4f), 2, 64);
                var points = new List<Vec2>(samples + 1);
                var ts = new List<double>(samples + 1);
                for (int i = 0; i <= samples; i++)
                {
                    float t = i / (float)samples;
                    var p = MathUtils.Position(curve, t);
                    points.Add(new Vec2(p.x, p.z));
                    ts.Add(t);
                }

                // Fine tolerance: lots on the outside of a bend are pushed past these points, and the
                // game checks against the real curve.
                var keep = Polyline.Simplify(points, 0.1);
                if (graph != null)
                {
                    var ends = EntityManager.GetComponentData<Edge>(e);
                    graph.Add(new GraphEdge { Id = Key(e), Start = Key(ends.m_Start), End = Key(ends.m_End), Width = width, Points = keep.Select(i => points[i]).ToList() });
                }

                for (int k = 0; k + 1 < keep.Count; k++)
                {
                    output.Add(new RoadSegment
                    {
                        A = points[keep[k]],
                        B = points[keep[k + 1]],
                        Width = width,
                        Id = id,
                        CurveT0 = ts[keep[k]],
                        CurveT1 = ts[keep[k + 1]],
                    });
                }
            }
        }

        /// <summary>
        /// Existing buildings near the area become obstacles: a single native OverlapExisting error
        /// blocks the whole apply, so the plan must avoid them up front.
        /// </summary>
        private void CollectBuildings(LayoutRequest request)
        {
            var min = new float2(float.MaxValue);
            var max = new float2(float.MinValue);
            foreach (var p in m_Points)
            {
                min = math.min(min, p.xz);
                max = math.max(max, p.xz);
            }

            min -= 64f;
            max += 64f;
            m_BuildingQuery.CompleteDependency();
            using var entities = m_BuildingQuery.ToEntityArray(Allocator.Temp);
            foreach (var e in entities)
            {
                var transform = EntityManager.GetComponentData<Game.Objects.Transform>(e);
                if (math.any(transform.m_Position.xz < min) || math.any(transform.m_Position.xz > max))
                {
                    continue;
                }

                var prefab = EntityManager.GetComponentData<PrefabRef>(e).m_Prefab;
                if (!EntityManager.HasComponent<BuildingData>(prefab))
                {
                    continue;
                }

                var lot = EntityManager.GetComponentData<BuildingData>(prefab).m_LotSize;
                var forward = math.mul(transform.m_Rotation, new float3(0f, 0f, 1f));
                request.Obstacles.Add(new Obb(new Vec2(transform.m_Position.x, transform.m_Position.z), new Vec2(forward.x, forward.z), lot.x * 8, lot.y * 8));
            }
        }

        private JobHandle SubmitObject(Entity prefab, float3 position, quaternion rotation, int index, JobHandle deps, Game.Tools.AgeMask age = Game.Tools.AgeMask.Sapling)
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
                prefab, Entity.Null, Entity.Null, Entity.Null, Entity.Null, Entity.Null,
                m_City.defaultTheme, points, default(NativeReference<AttachmentData>),
                false, m_City.leftHandTraffic, false, false,
                0f, 0f, 0f, 0f, UnityEngine.Time.deltaTime, Seed(index),
                Snap.None, age, false, default(PlacementOverrides), deps);
            points.Dispose(handle);
            NoteSubmittedObject(prefab, position);
            m_Submitted++;
            return handle;
        }

        private void SubmitRoad(Entity prefab, PlannedRoad road)
        {
            var start = EndPosition(road.A, road.StartJoin, true);
            var end = EndPosition(road.B, road.EndJoin, false);
            float3 a = start.m_Position;
            float3 b = end.m_Position;
            if (math.distance(a.xz, b.xz) < 4f)
            {
                return;
            }

            var tangent = b - a;
            start.m_Rotation = NetUtils.GetNodeRotation(tangent);
            end.m_Rotation = NetUtils.GetNodeRotation(tangent);
            var curve = new Bezier4x3(a, math.lerp(a, b, 1f / 3f), math.lerp(a, b, 2f / 3f), b);
            var course = new NetCourse
            {
                m_Curve = curve,
                m_Length = MathUtils.Length(curve),
                m_FixedIndex = -1,
                m_Elevation = float2.zero,
                m_StartPosition = start,
                m_EndPosition = end,
            };

            var buffer = m_ToolOutputBarrier.CreateCommandBuffer();
            var e = buffer.CreateEntity();
            buffer.AddComponent(e, new CreationDefinition { m_Prefab = prefab, m_RandomSeed = Seed(10000 + m_Submitted).GetRandom(0).NextInt() });
            buffer.AddComponent(e, course);
            buffer.AddComponent(e, default(Updated));
            NoteSubmittedRoad(prefab, a, b);
            m_Submitted++;
        }

        /// <summary>
        /// Course end for a planned road end. Existing joins attach to the edge (or its node when the
        /// split falls at an end); generated joins share an exact position with the neighbour piece.
        /// </summary>
        private CoursePos EndPosition(Vec2 point, RoadJoin join, bool first)
        {
            var pos = new CoursePos
            {
                m_Entity = Entity.Null,
                m_Position = Ground(point),
                m_CourseDelta = first ? 0f : 1f,
                m_Elevation = float2.zero,
                m_Flags = first ? CoursePosFlags.IsFirst : CoursePosFlags.IsLast,
                m_ParentMesh = -1,
            };

            if (join.Kind == JoinKind.Existing && join.TargetId >= 0 && join.TargetId < m_RoadRefs.Count)
            {
                var target = m_RoadRefs[join.TargetId];
                if (EntityManager.Exists(target.Edge) && EntityManager.HasComponent<Edge>(target.Edge))
                {
                    float t = (float)join.TargetCurveT;
                    var edge = EntityManager.GetComponentData<Edge>(target.Edge);
                    if (t < 0.01f && EntityManager.HasComponent<Node>(edge.m_Start))
                    {
                        pos.m_Entity = edge.m_Start;
                        pos.m_Position = EntityManager.GetComponentData<Node>(edge.m_Start).m_Position;
                    }
                    else if (t > 0.99f && EntityManager.HasComponent<Node>(edge.m_End))
                    {
                        pos.m_Entity = edge.m_End;
                        pos.m_Position = EntityManager.GetComponentData<Node>(edge.m_End).m_Position;
                    }
                    else
                    {
                        pos.m_Entity = target.Edge;
                        pos.m_SplitPosition = t;
                        pos.m_Position = MathUtils.Position(target.Curve, t);
                    }
                }
            }

            return pos;
        }

        private RandomSeed Seed(int index)
        {
            while (m_Seeds.Count <= index)
            {
                m_Seeds.Add(RandomSeed.Next());
            }

            return m_Seeds[index];
        }

        private float3 Ground(Vec2 xz)
        {
            var heights = m_Terrain.GetHeightData();
            var p = new float3((float)xz.X, 0f, (float)xz.Y);
            p.y = TerrainUtils.SampleHeight(ref heights, p);
            return p;
        }

        private void LogErrors(int temps)
        {
            using var errors = m_ErrorEntities.ToEntityArray(Allocator.Temp);
            var reasons = new Dictionary<string, int>();
            foreach (var e in errors)
            {
                if (!EntityManager.HasBuffer<Game.Notifications.IconElement>(e))
                {
                    continue;
                }

                foreach (var icon in EntityManager.GetBuffer<Game.Notifications.IconElement>(e, true))
                {
                    if (!EntityManager.HasComponent<PrefabRef>(icon.m_Icon))
                    {
                        continue;
                    }

                    var prefab = EntityManager.GetComponentData<PrefabRef>(icon.m_Icon).m_Prefab;
                    if (EntityManager.HasComponent<ToolErrorData>(prefab))
                    {
                        string reason = EntityManager.GetComponentData<ToolErrorData>(prefab).m_Error.ToString();
                        reasons.TryGetValue(reason, out int n);
                        reasons[reason] = n + 1;
                    }
                }
            }

            string reasonText = string.Join(", ", reasons.Select(r => r.Key + "x" + r.Value));
            Mod.log.Info($"builder: apply BLOCKED temps={temps} errorEntities={errors.Length} reasons={reasonText}. Shuffle, edit the area or the palette.");
            m_Status = Mod.IsKorean
                ? $"게임 오류로 막혔습니다 ({reasonText}). 섞기나 영역·리스트를 바꿔 보세요"
                : $"blocked by game errors ({reasonText}); shuffle or change the area or list";
        }

        private void DrawOverlay()
        {
            if (m_Points.Count == 0 && !m_HasCursor)
            {
                return;
            }

            var buffer = m_Overlay.GetBuffer(out var dependency);
            dependency.Complete();
            var edge = new Color(0.31f, 0.76f, 0.97f, 0.9f);
            var closing = new Color(0.49f, 0.85f, 0.55f, 0.95f);
            var lot = new Color(1f, 1f, 1f, 0.35f);

            void Line(float3 a, float3 b, Color c, float width)
            {
                if (math.distancesq(a.xz, b.xz) < 0.01f)
                {
                    return;
                }

                buffer.DrawLine(c, c, 0f, OverlayRenderSystem.StyleFlags.Projected, new Line3.Segment(a, b), width, new float2(0f));
            }

            void Ring(List<Vec2> ring, float y, Color c, float width)
            {
                for (int i = 0; i < ring.Count; i++)
                {
                    var a = ring[i];
                    var b = ring[(i + 1) % ring.Count];
                    Line(new float3((float)a.X, y, (float)a.Y), new float3((float)b.X, y, (float)b.Y), c, width);
                }
            }

            bool painting = Mod.Options != null && Mod.Options.Selection == AreaSelect.Paint;
            if (painting)
            {
                // The painted outline (roads left out, curbs hugged) and the brush.
                float y = m_HasCursor ? m_Cursor.y : (m_Points.Count > 0 ? m_Points[0].y : 0f);
                foreach (var region in m_LiveRegions ?? m_Regions)
                {
                    Ring(region.Outline, y, edge, 0.6f);
                }

                if (m_HasCursor && Mod.Options != null)
                {
                    float diameter = BrushRadius(Mod.Options) * 2f;
                    buffer.DrawCircle(new Color(1f, 1f, 1f, 0.8f), new Color(1f, 1f, 1f, 0.08f), 0.4f, OverlayRenderSystem.StyleFlags.Projected, new float2(0f, 1f), m_Cursor, diameter);
                }
            }

            for (int i = 0; !painting && i + 1 < m_Points.Count; i++)
            {
                Line(m_Points[i], m_Points[i + 1], edge, 0.6f);
            }

            if (!painting && m_State == State.Planned && m_Points.Count >= 3)
            {
                Line(m_Points[m_Points.Count - 1], m_Points[0], edge, 0.6f);
            }
            else if (!painting && m_HasCursor && m_Points.Count > 0)
            {
                bool closes = m_Points.Count >= 3 && math.distance(m_Cursor.xz, m_Points[0].xz) < kCloseDistance;
                Line(m_Points[m_Points.Count - 1], closes ? m_Points[0] : m_Snapped, closes ? closing : edge, 0.4f);
            }

            if (m_State == State.Drawing && m_HasCursor && m_Snap != null && m_Snap.Kind != SnapKind.None)
            {
                // Snap feedback: yellow marker, the curb line used and the angle guide.
                var snapColor = new Color(1f, 0.85f, 0.3f, 0.95f);
                var guideColor = new Color(1f, 0.85f, 0.3f, 0.35f);
                float y = m_Snapped.y;
                if (m_Snap.Kind == SnapKind.Curb || m_Snap.Kind == SnapKind.CurbCorner || m_Snap.Kind == SnapKind.AngleOnCurb)
                {
                    var c = m_Snap.Point;
                    var d = m_Snap.CurbDir * 24;
                    Line(new float3((float)(c.X - d.X), y, (float)(c.Y - d.Y)), new float3((float)(c.X + d.X), y, (float)(c.Y + d.Y)), guideColor, 0.3f);
                }

                if ((m_Snap.Kind == SnapKind.Angle || m_Snap.Kind == SnapKind.AngleOnCurb) && m_Points.Count > 0)
                {
                    var from = m_Points[m_Points.Count - 1];
                    var g = m_Snap.GuideDir * 400;
                    Line(from, new float3(from.x + (float)g.X, from.y, from.z + (float)g.Y), guideColor, 0.25f);
                }

                buffer.DrawCircle(snapColor, new Color(1f, 0.85f, 0.3f, 0.25f), 0.3f, OverlayRenderSystem.StyleFlags.Projected, new float2(0f, 1f), m_Snapped, m_Snap.Kind == SnapKind.CurbCorner ? 3.2f : 2.4f);
            }

            for (int i = 0; !painting && i < m_Points.Count; i++)
            {
                buffer.DrawCircle(Color.white, new Color(1f, 1f, 1f, 0.15f), 0.24f, OverlayRenderSystem.StyleFlags.Projected, new float2(0f, 1f), m_Points[i], 2.4f);
            }

            if (m_State == State.Planned && m_Plan != null && m_Points.Count > 0)
            {
                float y = m_Points[0].y;
                // Boundary actually used after curb snapping (drawn areas; painted blocks are drawn above).
                if (!painting && m_Plan.Boundary != null)
                {
                    Ring(m_Plan.Boundary, y, closing, 0.35f);
                }

                foreach (var placement in m_Plan.Placements)
                {
                    var corners = placement.Box.Corners();
                    for (int i = 0; i < 4; i++)
                    {
                        var a = corners[i];
                        var b = corners[(i + 1) % 4];
                        Line(new float3((float)a.X, y, (float)a.Y), new float3((float)b.X, y, (float)b.Y), placement.IsFixed ? closing : lot, placement.IsFixed ? 0.5f : 0.2f);
                    }
                }

                if (m_Moving != null)
                {
                    // The lot being dragged: green where it can be dropped, red where it cannot.
                    var ghost = m_MoveOk ? new Color(0.49f, 0.95f, 0.55f, 0.95f) : new Color(1f, 0.35f, 0.3f, 0.95f);
                    var corners = m_MoveGhost.Corners();
                    for (int i = 0; i < 4; i++)
                    {
                        var a = corners[i];
                        var b = corners[(i + 1) % 4];
                        Line(new float3((float)a.X, y, (float)a.Y), new float3((float)b.X, y, (float)b.Y), ghost, 0.6f);
                    }

                    var front = m_MoveGhost.Center + m_MoveGhost.Forward * (m_MoveGhost.Depth / 2);
                    Line(new float3((float)m_MoveGhost.Center.X, y, (float)m_MoveGhost.Center.Y), new float3((float)front.X, y, (float)front.Y), ghost, 0.4f);
                }
            }

            m_Overlay.AddBufferWriter(default);
        }
    }
}
