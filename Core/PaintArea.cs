using System;
using System.Collections.Generic;
using System.Linq;

namespace BlockBuilder.Core
{
    /// <summary>One connected painted region.</summary>
    public sealed class PaintRegion
    {
        /// <summary>Outline of the painted cells (what the player painted, roads left out), counter-clockwise.</summary>
        public List<Vec2> Outline = new List<Vec2>();
        /// <summary>
        /// Ring handed to the layout engine: the painted cells plus the road cells next to them, so
        /// every curb the paint reaches lies strictly inside it (roads are hard obstacles there).
        /// </summary>
        public List<Vec2> Ring = new List<Vec2>();
        public int Cells;
        public double Area;
    }

    /// <summary>
    /// Free brush painting on a cell grid where roads are walls (the behaviour of Placemaker's paint
    /// mode, implemented independently): a stroke can only paint the land reachable from where it
    /// started without crossing a road, so a brush wider than a road never spills to the far side.
    /// </summary>
    public sealed class PaintArea
    {
        /// <summary>Road cells added around the paint for the engine ring (see <see cref="PaintRegion.Ring"/>).</summary>
        private const int kRoadGrow = 2;

        private readonly List<RoadSegment> m_Roads;
        private readonly Dictionary<long, List<int>> m_Buckets = new Dictionary<long, List<int>>();
        private readonly Dictionary<long, bool> m_RoadCell = new Dictionary<long, bool>();
        private readonly HashSet<long> m_Painted = new HashSet<long>();
        /// <summary>Cells added by the current paint stroke (the only seeds for closing gaps to curbs).</summary>
        private readonly HashSet<long> m_StrokeCells = new HashSet<long>();
        private HashSet<long> m_Allowed;
        private const double kBucket = 32;

        public double Cell { get; }

        public int Count => m_Painted.Count;

        /// <summary>Bumped on every change of the painted cells.</summary>
        public int Version { get; private set; }

        /// <summary>Most cells that may be painted (0 = no limit); painting stops there.</summary>
        public int MaxCells { get; set; }

        /// <summary>True when painting is stopped by <see cref="MaxCells"/>.</summary>
        public bool AtLimit => MaxCells > 0 && m_Painted.Count >= MaxCells;

        /// <summary>Painted area in square metres.</summary>
        public double PaintedArea => m_Painted.Count * Cell * Cell;

        /// <summary>Painted cells as (column, row); used to carry the paint over when the roads are rebuilt.</summary>
        public IEnumerable<(int I, int J)> Cells => m_Painted.Select(Unkey).ToList();

        public bool IsEmpty => m_Painted.Count == 0;

        public PaintArea(IEnumerable<RoadSegment> roads, double cell = 2)
        {
            Cell = cell;
            m_Roads = roads.Where(r => Vec2.Distance(r.A, r.B) > 1e-6).ToList();
            for (int k = 0; k < m_Roads.Count; k++)
            {
                var r = m_Roads[k];
                double h = r.Width / 2 + cell;
                int x0 = (int)Math.Floor((Math.Min(r.A.X, r.B.X) - h) / kBucket);
                int x1 = (int)Math.Floor((Math.Max(r.A.X, r.B.X) + h) / kBucket);
                int y0 = (int)Math.Floor((Math.Min(r.A.Y, r.B.Y) - h) / kBucket);
                int y1 = (int)Math.Floor((Math.Max(r.A.Y, r.B.Y) + h) / kBucket);
                for (int x = x0; x <= x1; x++)
                {
                    for (int y = y0; y <= y1; y++)
                    {
                        long key = Key(x, y);
                        if (!m_Buckets.TryGetValue(key, out var list))
                        {
                            m_Buckets[key] = list = new List<int>();
                        }

                        list.Add(k);
                    }
                }
            }
        }

        private static long Key(int i, int j) => ((long)i << 32) ^ (uint)j;

        private static (int I, int J) Unkey(long k) => ((int)(k >> 32), (int)(uint)k);

        private (int I, int J) CellAt(Vec2 p) => ((int)Math.Floor(p.X / Cell), (int)Math.Floor(p.Y / Cell));

        private Vec2 Centre(int i, int j) => new Vec2((i + 0.5) * Cell, (j + 0.5) * Cell);

        /// <summary>A cell is road when its centre is on a road's surface.</summary>
        public bool IsRoad(int i, int j)
        {
            long key = Key(i, j);
            if (m_RoadCell.TryGetValue(key, out bool road))
            {
                return road;
            }

            Vec2 c = Centre(i, j);
            road = false;
            if (m_Buckets.TryGetValue(Key((int)Math.Floor(c.X / kBucket), (int)Math.Floor(c.Y / kBucket)), out var list))
            {
                foreach (int k in list)
                {
                    var r = m_Roads[k];
                    if (Segment.Distance(r.A, r.B, c, out _) < r.Width / 2)
                    {
                        road = true;
                        break;
                    }
                }
            }

            m_RoadCell[key] = road;
            return road;
        }

        public bool IsPainted(Vec2 p)
        {
            var (i, j) = CellAt(p);
            return m_Painted.Contains(Key(i, j));
        }

        /// <summary>
        /// Starts a paint stroke at <paramref name="at"/>: the land reachable from there without
        /// crossing a road (within <paramref name="reach"/> metres) becomes the only paintable area for
        /// this stroke. Returns false when the stroke starts on a road (nothing can be painted).
        /// </summary>
        public bool BeginStroke(Vec2 at, double reach = 300, bool crossRoads = false)
        {
            var (si, sj) = CellAt(at);
            m_Allowed = new HashSet<long>();
            m_StrokeCells.Clear();
            if (IsRoad(si, sj))
            {
                return false;
            }

            int r = (int)Math.Ceiling(reach / Cell);
            // Breadth-first over a list (Queue<T> is ambiguous between the game's mscorlib and System).
            var queue = new List<(int I, int J)> { (si, sj) };
            m_Allowed.Add(Key(si, sj));
            var visited = new HashSet<long> { Key(si, sj) };
            for (int head = 0; head < queue.Count; head++)
            {
                var (i, j) = queue[head];
                foreach (var (di, dj) in Neighbours)
                {
                    int ni = i + di;
                    int nj = j + dj;
                    if (Math.Abs(ni - si) > r || Math.Abs(nj - sj) > r)
                    {
                        continue;
                    }

                    long key = Key(ni, nj);
                    bool road = IsRoad(ni, nj);
                    if (visited.Contains(key) || (road && !crossRoads))
                    {
                        continue;
                    }

                    // With crossRoads the road is traversed (land on its far side is reachable) but never painted.
                    visited.Add(key);
                    queue.Add((ni, nj));
                    if (!road)
                    {
                        m_Allowed.Add(key);
                    }
                }
            }

            return true;
        }

        private static readonly (int, int)[] Neighbours = { (1, 0), (-1, 0), (0, 1), (0, -1) };

        /// <summary>Paints (or erases) a brush of <paramref name="radius"/> swept from a to b.</summary>
        public bool Stroke(Vec2 a, Vec2 b, double radius, bool erase)
        {
            int steps = Math.Max(1, (int)Math.Ceiling(Vec2.Distance(a, b) / Math.Max(Cell, radius * 0.5)));
            bool changed = false;
            for (int s = 0; s <= steps; s++)
            {
                changed |= Stamp(Vec2.Lerp(a, b, (double)s / steps), radius, erase);
            }

            return changed;
        }

        public bool Stamp(Vec2 c, double radius, bool erase)
        {
            int i0 = (int)Math.Floor((c.X - radius) / Cell);
            int i1 = (int)Math.Floor((c.X + radius) / Cell);
            int j0 = (int)Math.Floor((c.Y - radius) / Cell);
            int j1 = (int)Math.Floor((c.Y + radius) / Cell);
            bool changed = false;
            for (int i = i0; i <= i1; i++)
            {
                for (int j = j0; j <= j1; j++)
                {
                    if (Vec2.Distance(Centre(i, j), c) > radius)
                    {
                        continue;
                    }

                    long key = Key(i, j);
                    if (erase)
                    {
                        changed |= m_Painted.Remove(key);
                    }
                    else if (m_Allowed != null && m_Allowed.Contains(key) && (MaxCells <= 0 || m_Painted.Count < MaxCells || m_Painted.Contains(key)) && m_Painted.Add(key))
                    {
                        m_StrokeCells.Add(key);
                        changed = true;
                    }
                }
            }

            if (changed)
            {
                Version++;
            }

            return changed;
        }

        /// <summary>
        /// Ends a paint stroke: a gap of up to <paramref name="snap"/> metres between the paint and a
        /// curb is closed, because stopping just short of a road means "up to the road" (Placemaker's
        /// snap rule). Only land next to the paint and next to a road is added; free edges keep the
        /// brush shape. Returns the number of cells added.
        /// </summary>
        public int EndStroke(double snap = 6)
        {
            // Seeds: cells this stroke painted that are already within snap of a curb. From there the
            // gap is closed by stepping only toward the road (curb distance strictly decreasing), so
            // nothing spreads along the road or away from it, and paint erased earlier near a curb
            // is not brought back by a stroke elsewhere.
            var frontier = m_StrokeCells.Where(k => { var (i, j) = Unkey(k); return CurbDistance(Centre(i, j)) <= snap; }).ToList();
            var reached = new HashSet<long>(frontier);
            int added = 0;
            int depth = (int)Math.Ceiling(snap / Cell) + 1;
            for (int step = 0; step < depth && frontier.Count > 0; step++)
            {
                var next = new List<long>();
                foreach (long k in frontier)
                {
                    var (i, j) = Unkey(k);
                    double here = CurbDistance(Centre(i, j));
                    foreach (var (di, dj) in Neighbours)
                    {
                        int ni = i + di;
                        int nj = j + dj;
                        long n = Key(ni, nj);
                        if (reached.Contains(n) || IsRoad(ni, nj))
                        {
                            continue;
                        }

                        double there = CurbDistance(Centre(ni, nj));
                        if (there >= here - 1e-6 || there > snap)
                        {
                            continue;
                        }

                        reached.Add(n);
                        next.Add(n);
                        if (m_Painted.Add(n))
                        {
                            added++;
                        }
                    }
                }

                frontier = next;
            }

            if (added > 0)
            {
                Version++;
            }

            m_StrokeCells.Clear();
            m_Allowed = null;
            return added;
        }
        /// <summary>Distance from p to the nearest curb (0 on a road surface), or +inf with no road nearby.</summary>
        private double CurbDistance(Vec2 p) => NearestRoad(p, out _, out _, out double d) ? Math.Max(0, d) : double.PositiveInfinity;

        /// <summary>Nearest road segment to p: the foot on its centre line and the signed distance past its curb.</summary>
        private bool NearestRoad(Vec2 p, out RoadSegment road, out Vec2 foot, out double pastCurb)
        {
            road = null;
            foot = p;
            pastCurb = double.PositiveInfinity;
            int bx = (int)Math.Floor(p.X / kBucket);
            int by = (int)Math.Floor(p.Y / kBucket);
            for (int x = bx - 1; x <= bx + 1; x++)
            {
                for (int y = by - 1; y <= by + 1; y++)
                {
                    if (!m_Buckets.TryGetValue(Key(x, y), out var list))
                    {
                        continue;
                    }

                    foreach (int k in list)
                    {
                        var r = m_Roads[k];
                        double d = Segment.Distance(r.A, r.B, p, out double t) - r.Width / 2;
                        if (d < pastCurb)
                        {
                            pastCurb = d;
                            road = r;
                            foot = Vec2.Lerp(r.A, r.B, t);
                        }
                    }
                }
            }

            return road != null;
        }

        /// <summary>
        /// Moves outline vertices within about a cell of a curb onto the curb line, so the shown edge
        /// hugs the road exactly instead of following the cell staircase.
        /// </summary>
        private List<Vec2> SnapToCurb(List<Vec2> ring)
        {
            var snapped = new List<Vec2>(ring.Count);
            foreach (var v in ring)
            {
                if (NearestRoad(v, out var road, out var foot, out double past) && past <= Cell * 1.5 && Vec2.Distance(v, foot) > 1e-6)
                {
                    Vec2 n = (v - foot).Normalized;
                    snapped.Add(foot + n * (road.Width / 2));
                }
                else
                {
                    snapped.Add(v);
                }
            }

            var clean = PolylineOffset.Clean(snapped);
            return clean.Count >= 3 && Polygon.SignedArea(clean) > 0 && !SelfIntersects(clean) ? clean : ring;
        }

        /// <summary>For tests: paint one cell regardless of roads and strokes.</summary>
        public void PaintCell(int i, int j)
        {
            if (m_Painted.Add(Key(i, j)))
            {
                Version++;
            }
        }

        public void Clear()
        {
            if (m_Painted.Count > 0)
            {
                m_Painted.Clear();
                Version++;
            }

            m_StrokeCells.Clear();
        }

        /// <summary>Connected painted regions with their display outline and engine ring.</summary>
        public List<PaintRegion> Regions(double minArea = 16, bool rings = true)
        {
            var regions = new List<PaintRegion>();
            var seen = new HashSet<long>();
            foreach (long start in m_Painted)
            {
                if (seen.Contains(start))
                {
                    continue;
                }

                // 4-connected component.
                var cells = new HashSet<long> { start };
                var queue = new List<long> { start };
                seen.Add(start);
                for (int head = 0; head < queue.Count; head++)
                {
                    var (i, j) = Unkey(queue[head]);
                    foreach (var (di, dj) in Neighbours)
                    {
                        long n = Key(i + di, j + dj);
                        if (m_Painted.Contains(n) && seen.Add(n))
                        {
                            cells.Add(n);
                            queue.Add(n);
                        }
                    }
                }

                if (cells.Count * Cell * Cell < minArea)
                {
                    continue;
                }

                var outline = OuterRing(cells);
                if (outline.Count < 4)
                {
                    continue;
                }

                if (!rings)
                {
                    regions.Add(new PaintRegion { Outline = SnapToCurb(Simplify(outline, Cell * 0.45)), Cells = cells.Count, Area = cells.Count * Cell * Cell });
                    continue;
                }

                // Engine ring: grow into the neighbouring road cells (never into unpainted land).
                var grown = new HashSet<long>(cells);
                var frontier = new List<long>(cells);
                for (int step = 0; step < kRoadGrow; step++)
                {
                    var next = new List<long>();
                    foreach (long k in frontier)
                    {
                        var (i, j) = Unkey(k);
                        foreach (var (di, dj) in Neighbours)
                        {
                            int ni = i + di;
                            int nj = j + dj;
                            long n = Key(ni, nj);
                            if (!grown.Contains(n) && IsRoad(ni, nj))
                            {
                                grown.Add(n);
                                next.Add(n);
                            }
                        }
                    }

                    frontier = next;
                }

                var ring = OuterRing(grown);
                regions.Add(new PaintRegion
                {
                    Outline = SnapToCurb(Simplify(outline, Cell * 0.45)),
                    Ring = Simplify(ring, Cell * 0.45),
                    Cells = cells.Count,
                    Area = cells.Count * Cell * Cell,
                });
            }

            return regions;
        }

        /// <summary>
        /// Outer boundary of a cell set along cell edges, counter-clockwise, holes ignored (enclosed
        /// pockets count as filled). At a pinch where two cells touch only at a corner the walk turns
        /// left, so the ring never crosses itself.
        /// </summary>
        private List<Vec2> OuterRing(HashSet<long> cells)
        {
            // Directed boundary edges with the cell on the left, keyed by their start corner.
            var edges = new Dictionary<long, List<(int Dx, int Dy)>>();
            void AddEdge(int x, int y, int dx, int dy)
            {
                long k = Key(x, y);
                if (!edges.TryGetValue(k, out var list))
                {
                    edges[k] = list = new List<(int, int)>();
                }

                list.Add((dx, dy));
            }

            foreach (long k in cells)
            {
                var (i, j) = Unkey(k);
                if (!cells.Contains(Key(i, j - 1)))
                {
                    AddEdge(i, j, 1, 0); // bottom, walking +x
                }

                if (!cells.Contains(Key(i + 1, j)))
                {
                    AddEdge(i + 1, j, 0, 1); // right, walking +y
                }

                if (!cells.Contains(Key(i, j + 1)))
                {
                    AddEdge(i + 1, j + 1, -1, 0); // top, walking -x
                }

                if (!cells.Contains(Key(i - 1, j)))
                {
                    AddEdge(i, j + 1, 0, -1); // left, walking -y
                }
            }

            List<Vec2> best = null;
            double bestArea = 0;
            while (edges.Count > 0)
            {
                long startKey = edges.Keys.First();
                var (x, y) = Unkey(startKey);
                var ring = new List<Vec2>();
                int pdx = 0, pdy = 0;
                bool first = true;
                while (true)
                {
                    long k = Key(x, y);
                    if (!edges.TryGetValue(k, out var outs) || outs.Count == 0)
                    {
                        break;
                    }

                    // Prefer a left turn, then straight, then right (keeps pinches apart).
                    int pick = 0;
                    if (!first && outs.Count > 1)
                    {
                        int bestRank = int.MaxValue;
                        for (int o = 0; o < outs.Count; o++)
                        {
                            int cross = pdx * outs[o].Dy - pdy * outs[o].Dx;
                            int rank = cross > 0 ? 0 : cross == 0 ? 1 : 2;
                            if (rank < bestRank)
                            {
                                bestRank = rank;
                                pick = o;
                            }
                        }
                    }

                    var (dx, dy) = outs[pick];
                    outs.RemoveAt(pick);
                    if (outs.Count == 0)
                    {
                        edges.Remove(k);
                    }

                    ring.Add(new Vec2(x * Cell, y * Cell));
                    x += dx;
                    y += dy;
                    pdx = dx;
                    pdy = dy;
                    first = false;
                }

                double area = Polygon.SignedArea(ring);
                if (area > bestArea)
                {
                    bestArea = area;
                    best = ring;
                }
            }

            return best == null ? new List<Vec2>() : PolylineOffset.Clean(best);
        }

        /// <summary>Douglas-Peucker on the closed ring; keeps the staircase if simplifying would make it cross itself.</summary>
        private static List<Vec2> Simplify(List<Vec2> ring, double tolerance)
        {
            if (ring.Count < 5)
            {
                return ring;
            }

            var open = new List<Vec2>(ring) { ring[0] };
            var keep = Polyline.Simplify(open, tolerance);
            var simple = keep.Take(keep.Count - 1).Select(i => open[i]).ToList();
            if (simple.Count < 3 || Polygon.SignedArea(simple) <= 0 || SelfIntersects(simple))
            {
                return ring;
            }

            return simple;
        }

        private static bool SelfIntersects(List<Vec2> poly)
        {
            int n = poly.Count;
            for (int i = 0; i < n; i++)
            {
                for (int j = i + 2; j < n; j++)
                {
                    if (i == 0 && j == n - 1)
                    {
                        continue;
                    }

                    if (Segment.ProperIntersect(poly[i], poly[(i + 1) % n], poly[j], poly[(j + 1) % n]))
                    {
                        return true;
                    }
                }
            }

            return false;
        }
    }
}
