using System.Collections.Generic;

namespace BlockBuilder.Core
{
    /// <summary>An existing road piece near the site (a straight sample of a game curve).</summary>
    public sealed class RoadSegment
    {
        public Vec2 A;
        public Vec2 B;
        public double Width;
        /// <summary>Opaque id the game layer uses to find the edge again (index into its own table).</summary>
        public int Id;
        /// <summary>Curve parameter of A and B on the game edge, so a join can split it precisely.</summary>
        public double CurveT0;
        public double CurveT1 = 1;
    }

    /// <summary>Something the user wants placed: a building, park or parking lot.</summary>
    public sealed class PaletteItem
    {
        public string Id = string.Empty;
        /// <summary>Lot width across the front, metres (game cells × 8).</summary>
        public double Width;
        /// <summary>Lot depth, metres.</summary>
        public double Depth;
        public double Weight = 1;
        /// <summary>0 means unlimited.</summary>
        public int MaxCount;
        /// <summary>Needs a road at its front (game BuildingFlags.RequireRoad).</summary>
        public bool RequiresRoad = true;
    }

    public enum FillMode
    {
        /// <summary>Place fixed items, then fill every remaining space.</summary>
        Fill,
        /// <summary>Place only the user's fixed items.</summary>
        FixedOnly,
    }

    public sealed class LayoutOptions
    {
        public FillMode Mode = FillMode.Fill;
        public bool GenerateRoads;
        public double NewRoadWidth = 8;
        /// <summary>Longest stretch of generated street before a cross street is added.</summary>
        public double MaxBlockLength = 160;
        /// <summary>Step used when nothing fits at the current frontage position.</summary>
        public double Step = 8;
        /// <summary>Also fill interior space with items that do not need a road.</summary>
        public bool FillInterior = true;
        public int Seed = 1;
        /// <summary>Distance at which a generated road end snaps onto an existing road centre line.</summary>
        public double JoinTolerance = 2;
        /// <summary>Move boundary edges that follow a road onto its curb line before planning.</summary>
        public bool SnapBoundaryToCurbs = true;
        /// <summary>
        /// Metres a lot may overlap other lots and existing buildings at each side (left/right).
        /// Never roads, never the area boundary. Needs the game's overlap error off (Anarchy).
        /// </summary>
        public double OverlapSide;

        /// <summary>Metres a lot may overlap others at its back (depth direction); the front stays flush with the road.</summary>
        public double OverlapBack;

        /// <summary>Empty metres left between neighbouring lots along a road (relaxed fill).</summary>
        public double Gap;

        /// <summary>Grid step for interior (no-road) items; independent of the frontage search step.</summary>
        public double InteriorStep = 8;

        public LayoutOptions Clone() => (LayoutOptions)MemberwiseClone();
    }

    public sealed class LayoutRequest
    {
        public List<Vec2> Boundary = new List<Vec2>();
        public List<RoadSegment> ExistingRoads = new List<RoadSegment>();
        public List<PaletteItem> Items = new List<PaletteItem>();
        /// <summary>Items the user placed by hand (the "designate" mode). Kept as-is.</summary>
        public List<Placement> Fixed = new List<Placement>();
        /// <summary>Things already in the city (existing buildings) that lots must not overlap.</summary>
        public List<Obb> Obstacles = new List<Obb>();
        public LayoutOptions Options = new LayoutOptions();
    }

    public sealed class Placement
    {
        public string ItemId = string.Empty;
        public Obb Box;
        public bool IsFixed;
        /// <summary>Road the front faces; -1 for interior items, -2 for generated roads (see RoadIndex).</summary>
        public int FrontageRoadId = -1;
        /// <summary>Index into LayoutResult.Roads when the frontage is a generated road.</summary>
        public int RoadIndex = -1;
    }

    public enum JoinKind
    {
        /// <summary>Free end.</summary>
        None,
        /// <summary>Ends on an existing road; split it there.</summary>
        Existing,
        /// <summary>Shares an exact vertex with another generated road.</summary>
        Generated,
    }

    public struct RoadJoin
    {
        public JoinKind Kind;
        /// <summary>RoadSegment.Id for Existing.</summary>
        public int TargetId;
        /// <summary>Curve parameter on the existing edge for Existing.</summary>
        public double TargetCurveT;
    }

    public sealed class PlannedRoad
    {
        public Vec2 A;
        public Vec2 B;
        public double Width;
        public RoadJoin StartJoin;
        public RoadJoin EndJoin;
        /// <summary>Reaches an existing road through generated roads.</summary>
        public bool Connected;
    }

    public sealed class LayoutResult
    {
        public List<Placement> Placements = new List<Placement>();
        public List<PlannedRoad> Roads = new List<PlannedRoad>();
        public List<string> Warnings = new List<string>();
        /// <summary>The boundary actually used (after curb snapping), counter-clockwise.</summary>
        public List<Vec2> Boundary = new List<Vec2>();
        public Dictionary<string, int> Counts = new Dictionary<string, int>();
    }
}
