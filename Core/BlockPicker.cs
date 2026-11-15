using System;
using System.Collections.Generic;
using System.Linq;

namespace BlockBuilder.Core
{
    /// <summary>A road between two nodes: centre-line polyline from Start to End.</summary>
    public sealed class GraphEdge
    {
        public long Id;
        public long Start;
        public long End;
        public List<Vec2> Points = new List<Vec2>();
        public double Width;
    }

    public enum BlockPickStatus
    {
        Ok,
        NoRoads,
        /// <summary>The point is not inside any closed block.</summary>
        Outside,
        /// <summary>A block was found but its curb outline is not a valid polygon.</summary>
        Degenerate,
    }

    public sealed class BlockPick
    {
        public BlockPickStatus Status;
        /// <summary>Curb outline of the block, counter-clockwise.</summary>
        public List<Vec2> Ring = new List<Vec2>();
        /// <summary>Centre-line outline the curb ring was offset from.</summary>
        public List<Vec2> Centerline = new List<Vec2>();
        public List<long> EdgeIds = new List<long>();
        public double Area;
    }

    /// <summary>
    /// The closed faces (blocks) of a road graph, built once and then queried per point. Faces come
    /// from a planar walk over half-edges sorted by angle at each node; dead-end roads are removed
    /// first so cul-de-sacs do not cut into an outline. Curb rings are made on demand by offsetting
    /// every side inward by its road's half width, so curved roads keep their shape.
    /// </summary>
    public sealed class BlockGraph
    {
        private sealed class Half
        {
            public int Edge;
            public bool Forward;
            public long Target;
            public double Angle;
            public Half Twin;
            public Half Next;
        }

        private sealed class Face
        {
            public List<Half> Cycle;
            public List<Vec2> Centerline;
            public double Area;
            public double MinX, MinY, MaxX, MaxY;
            public BlockPick Curb;
        }

        private readonly List<GraphEdge> m_Edges;
        private readonly List<Face> m_Faces = new List<Face>();

        public int EdgeCount => m_Edges.Count;

        public int FaceCount => m_Faces.Count;

        public BlockGraph(IReadOnlyList<GraphEdge> input, int maxFaceEdges = 2000)
        {
            m_Edges = StripDeadEnds(input.Where(e => e.Points != null && e.Points.Count >= 2 && Length(e.Points) > 0.1).ToList());

            // Half-edges, grouped by origin node and sorted counter-clockwise by leaving direction.
            var halves = new List<Half>();
            var outgoing = new Dictionary<long, List<Half>>();
            for (int i = 0; i < m_Edges.Count; i++)
            {
                var e = m_Edges[i];
                var f = new Half { Edge = i, Forward = true, Target = e.End, Angle = LeaveAngle(e.Points, false) };
                var b = new Half { Edge = i, Forward = false, Target = e.Start, Angle = LeaveAngle(e.Points, true) };
                f.Twin = b;
                b.Twin = f;
                halves.Add(f);
                halves.Add(b);
                Out(outgoing, e.Start).Add(f);
                Out(outgoing, e.End).Add(b);
            }

            foreach (var list in outgoing.Values)
            {
                list.Sort((x, y) => x.Angle.CompareTo(y.Angle));
            }

            // Arriving at a node along h, turn to the next edge clockwise from the way back: this
            // walks every bounded face counter-clockwise with the face on the left.
            foreach (var h in halves)
            {
                var around = outgoing[h.Target];
                int i = around.IndexOf(h.Twin);
                h.Next = around[(i - 1 + around.Count) % around.Count];
            }

            var visited = new HashSet<Half>();
            foreach (var start in halves)
            {
                if (visited.Contains(start))
                {
                    continue;
                }

                var cycle = new List<Half>();
                var h = start;
                while (!visited.Contains(h) && cycle.Count <= maxFaceEdges)
                {
                    visited.Add(h);
                    cycle.Add(h);
                    h = h.Next;
                }

                if (h != start || cycle.Count > maxFaceEdges)
                {
                    continue;
                }

                var ring = CentreRing(cycle);
                double area = Polygon.SignedArea(ring);
                if (area <= 1)
                {
                    continue; // the outer face of each component is clockwise (negative area)
                }

                var face = new Face { Cycle = cycle, Centerline = ring, Area = area, MinX = double.MaxValue, MinY = double.MaxValue, MaxX = double.MinValue, MaxY = double.MinValue };
                foreach (var p in ring)
                {
                    face.MinX = Math.Min(face.MinX, p.X);
                    face.MinY = Math.Min(face.MinY, p.Y);
                    face.MaxX = Math.Max(face.MaxX, p.X);
                    face.MaxY = Math.Max(face.MaxY, p.Y);
                }

                m_Faces.Add(face);
            }
        }

        /// <summary>The smallest block containing <paramref name="point"/>, with its curb outline.</summary>
        public BlockPick Pick(Vec2 point)
        {
            if (m_Edges.Count == 0)
            {
                return new BlockPick { Status = BlockPickStatus.NoRoads };
            }

            Face best = null;
            foreach (var f in m_Faces)
            {
                if (point.X < f.MinX || point.X > f.MaxX || point.Y < f.MinY || point.Y > f.MaxY || (best != null && f.Area >= best.Area))
                {
                    continue;
                }

                if (Polygon.Contains(f.Centerline, point))
                {
                    best = f;
                }
            }

            if (best == null)
            {
                return new BlockPick { Status = BlockPickStatus.Outside };
            }

            return best.Curb ??= MakeCurb(best);
        }

        /// <summary>
        /// Every block a brush circle touches: its curb outline contains the centre or comes within
        /// <paramref name="radius"/>. Measured against the curb, not the centre line, so a brush on a
        /// road only takes the block on the side it actually reaches into.
        /// </summary>
        public List<BlockPick> PickAll(Vec2 center, double radius)
        {
            var found = new List<BlockPick>();
            foreach (var f in m_Faces)
            {
                if (center.X < f.MinX - radius || center.X > f.MaxX + radius || center.Y < f.MinY - radius || center.Y > f.MaxY + radius)
                {
                    continue;
                }

                var curb = f.Curb ??= MakeCurb(f);
                if (curb.Status != BlockPickStatus.Ok)
                {
                    continue;
                }

                if (Polygon.Contains(curb.Ring, center) || Polygon.DistanceToBoundary(curb.Ring, center) <= radius)
                {
                    found.Add(curb);
                }
            }

            return found;
        }

        /// <summary>Stable identity of a block across graph rebuilds: its road edges.</summary>
        public static string KeyOf(BlockPick pick) => string.Join(",", pick.EdgeIds.OrderBy(id => id));

        private BlockPick MakeCurb(Face face)
        {
            var pick = new BlockPick { Centerline = face.Centerline, EdgeIds = face.Cycle.Select(h => m_Edges[h.Edge].Id).Distinct().ToList() };

            // Offset each side inward by its road's half width, with one offset per segment.
            var pts = new List<Vec2>();
            var offsets = new List<double>();
            foreach (var h in face.Cycle)
            {
                var e = m_Edges[h.Edge];
                int n = e.Points.Count;
                for (int k = 0; k + 1 < n; k++)
                {
                    pts.Add(h.Forward ? e.Points[k] : e.Points[n - 1 - k]);
                    offsets.Add(e.Width / 2);
                }
            }

            var curb = PolylineOffset.Clean(PolylineOffset.Offset(pts, offsets, closed: true));
            if (curb.Count < 3 || Polygon.SignedArea(curb) <= 1 || SelfIntersects(curb))
            {
                pick.Status = BlockPickStatus.Degenerate;
                return pick;
            }

            pick.Ring = curb;
            pick.Area = Polygon.SignedArea(curb);
            pick.Status = BlockPickStatus.Ok;
            return pick;
        }

        private static List<Half> Out(Dictionary<long, List<Half>> map, long node)
        {
            if (!map.TryGetValue(node, out var list))
            {
                map[node] = list = new List<Half>();
            }

            return list;
        }

        private static double Length(List<Vec2> pts)
        {
            double len = 0;
            for (int k = 0; k + 1 < pts.Count; k++)
            {
                len += Vec2.Distance(pts[k], pts[k + 1]);
            }

            return len;
        }

        /// <summary>Direction leaving an end of the polyline, measured a little way along to skip jitter.</summary>
        private static double LeaveAngle(List<Vec2> pts, bool fromEnd)
        {
            int n = pts.Count;
            Vec2 a = fromEnd ? pts[n - 1] : pts[0];
            Vec2 b = a;
            for (int k = 1; k < n; k++)
            {
                b = fromEnd ? pts[n - 1 - k] : pts[k];
                if (Vec2.Distance(a, b) >= 0.5)
                {
                    break;
                }
            }

            return Math.Atan2(b.Y - a.Y, b.X - a.X);
        }

        private List<Vec2> CentreRing(List<Half> cycle)
        {
            var ring = new List<Vec2>();
            foreach (var h in cycle)
            {
                var p = m_Edges[h.Edge].Points;
                int n = p.Count;
                for (int k = 0; k + 1 < n; k++)
                {
                    ring.Add(h.Forward ? p[k] : p[n - 1 - k]);
                }
            }

            return ring;
        }

        /// <summary>Repeatedly removes roads that end at a node with no other road (dead ends).</summary>
        private static List<GraphEdge> StripDeadEnds(List<GraphEdge> edges)
        {
            var list = new List<GraphEdge>(edges);
            while (true)
            {
                var degree = new Dictionary<long, int>();
                foreach (var e in list)
                {
                    degree[e.Start] = (degree.TryGetValue(e.Start, out int a) ? a : 0) + 1;
                    degree[e.End] = (degree.TryGetValue(e.End, out int b) ? b : 0) + 1;
                }

                int before = list.Count;
                list.RemoveAll(e => e.Start != e.End && (degree[e.Start] == 1 || degree[e.End] == 1));
                if (list.Count == before)
                {
                    return list;
                }
            }
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

    /// <summary>One-shot convenience: build the graph and pick the block around a point.</summary>
    public static class BlockPicker
    {
        public static BlockPick Pick(IReadOnlyList<GraphEdge> edges, Vec2 point) => new BlockGraph(edges).Pick(point);
    }
}