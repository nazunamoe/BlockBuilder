using System;
using System.Collections.Generic;
using System.Linq;

namespace BlockBuilder.Core
{
    /// <summary>
    /// A curb line inside the area that lots are laid along: a polyline walked by arc length, with
    /// the lots on one side. Curved roads become one continuous frontage, so a lot may span several
    /// of the short segments a curve is sampled into.
    /// </summary>
    internal sealed class Frontage
    {
        /// <summary>Turn above which two road segments are not joined into one frontage (corners).</summary>
        private const double kMaxJoinTurnDegrees = 30;

        /// <summary>
        /// Extra push wherever the curb is curved: the real curve bulges past the sampled polyline
        /// between its vertices (BuilderTool samples curves at 0.1 m tolerance), and one overlap
        /// error blocks the whole build.
        /// </summary>
        private const double kCurveMargin = 0.25;

        public readonly List<Vec2> Points = new List<Vec2>();
        public readonly List<double> Cum = new List<double>();
        /// <summary>+1: lots on the left of the walking direction (Perp), -1: on the right.</summary>
        public int Side;
        public int RoadId;
        public int RoadIndex = -1;

        public double Length => Cum.Count == 0 ? 0 : Cum[Cum.Count - 1];

        private void Add(Vec2 p)
        {
            if (Points.Count > 0 && Vec2.Distance(Points[Points.Count - 1], p) < 1e-9)
            {
                return;
            }

            Cum.Add(Points.Count == 0 ? 0 : Cum[Cum.Count - 1] + Vec2.Distance(Points[Points.Count - 1], p));
            Points.Add(p);
        }

        private int SegmentAt(double s)
        {
            int k = 0;
            while (k < Points.Count - 2 && Cum[k + 1] < s)
            {
                k++;
            }

            return k;
        }

        public Vec2 PointAt(double s)
        {
            if (Points.Count == 1)
            {
                return Points[0];
            }

            s = Math.Max(0, Math.Min(Length, s));
            int k = SegmentAt(s);
            double len = Cum[k + 1] - Cum[k];
            return len < 1e-12 ? Points[k] : Vec2.Lerp(Points[k], Points[k + 1], (s - Cum[k]) / len);
        }

        private Vec2 DirAt(double s)
        {
            int k = SegmentAt(s);
            return (Points[k + 1] - Points[k]).Normalized;
        }

        /// <summary>
        /// Lot of <paramref name="width"/> x <paramref name="depth"/> starting at arc length
        /// <paramref name="s"/>: front on the chord of the curb span, pushed out past any curb point
        /// that bulges toward the lots, facing the road.
        /// </summary>
        public Obb LotAt(double s, double width, double depth)
        {
            Vec2 p0 = PointAt(s);
            Vec2 p1 = PointAt(s + width);
            Vec2 chord = p1 - p0;
            Vec2 dir = chord.Length > 1e-9 ? chord.Normalized : DirAt(s);
            Vec2 n = dir.Perp * Side;

            // Push the front out past every curb point within the lot's full width (not just its arc
            // span): on the inside of a bend the lot is wider than its chord, and its front corners
            // would otherwise cross the curb beyond the chord ends.
            Vec2 mid = (p0 + p1) / 2;
            double half = width / 2;
            double push = 0;
            bool curved = false;
            for (int k = 0; k + 1 < Points.Count; k++)
            {
                if (Cum[k + 1] < s - width || Cum[k] > s + 2 * width)
                {
                    continue;
                }

                Vec2 a = Points[k];
                Vec2 b = Points[k + 1];
                double la = Vec2.Dot(a - mid, dir);
                double lb = Vec2.Dot(b - mid, dir);
                if (ClipLateral(la, lb, half, out double t0, out double t1))
                {
                    push = Math.Max(push, Vec2.Dot(Vec2.Lerp(a, b, t0) - p0, n));
                    push = Math.Max(push, Vec2.Dot(Vec2.Lerp(a, b, t1) - p0, n));
                }

                if (k > 0 && Math.Abs(Vec2.Cross((a - Points[k - 1]).Normalized, (b - a).Normalized)) > 1e-6)
                {
                    curved = true;
                }
            }

            if (curved)
            {
                push += kCurveMargin;
            }

            Vec2 center = (p0 + p1) / 2 + n * (push + depth / 2);
            return new Obb(center, -n, width, depth);
        }

        /// <summary>Part of a segment whose lateral coordinate (la..lb) lies within [-half, half].</summary>
        private static bool ClipLateral(double la, double lb, double half, out double t0, out double t1)
        {
            t0 = 0;
            t1 = 1;
            double d = lb - la;
            if (Math.Abs(d) < 1e-12)
            {
                return Math.Abs(la) <= half;
            }

            double ta = (-half - la) / d;
            double tb = (half - la) / d;
            t0 = Math.Max(0, Math.Min(ta, tb));
            t1 = Math.Min(1, Math.Max(ta, tb));
            return t0 <= t1;
        }

        /// <summary>
        /// Lot of the given size placed against the curb nearest to <paramref name="point"/> (within
        /// <paramref name="maxDistance"/> of it), centred along the curb at the point and facing the
        /// road. Uses the same curb walk as the fill, so it also fits on curved roads.
        /// </summary>
        public static bool TrySnapLot(IReadOnlyList<RoadSegment> roads, Vec2 point, double width, double depth, double maxDistance, out Obb box)
        {
            box = default;
            double reach = maxDistance + width + depth + 50;
            var square = new List<Vec2>
            {
                point + new Vec2(-reach, -reach),
                point + new Vec2(reach, -reach),
                point + new Vec2(reach, reach),
                point + new Vec2(-reach, reach),
            };
            var near = roads.Where(r => Segment.Distance(r.A, r.B, point, out _) <= reach).ToList();
            Frontage best = null;
            double bestDistance = double.MaxValue;
            double bestS = 0;
            foreach (var f in Build(near, new List<PlannedRoad>(), square))
            {
                for (int k = 0; k + 1 < f.Points.Count; k++)
                {
                    Vec2 a = f.Points[k];
                    Vec2 b = f.Points[k + 1];
                    double d = Segment.Distance(a, b, point, out double t);
                    Vec2 n = (b - a).Normalized.Perp * f.Side;
                    // The point must be on the lots' side of this curb (or just on it).
                    if (d >= bestDistance || Vec2.Dot(point - Vec2.Lerp(a, b, t), n) < -1)
                    {
                        continue;
                    }

                    bestDistance = d;
                    best = f;
                    bestS = f.Cum[k] + t * Vec2.Distance(a, b);
                }
            }

            if (best == null || bestDistance > maxDistance || best.Length < width)
            {
                return false;
            }

            double s0 = Math.Max(0, Math.Min(best.Length - width, bestS - width / 2));
            box = best.LotAt(s0, width, depth);
            return true;
        }

        /// <summary>Frontages along both sides of every road, clipped to the area.</summary>
        public static List<Frontage> Build(IReadOnlyList<RoadSegment> existing, IReadOnlyList<PlannedRoad> generated, List<Vec2> poly)
        {
            var fronts = new List<Frontage>();
            foreach (var chain in Chains(existing))
            {
                AddSides(fronts, chain.Points, chain.Width, chain.Id, -1, poly);
            }

            for (int i = 0; i < generated.Count; i++)
            {
                AddSides(fronts, new List<Vec2> { generated[i].A, generated[i].B }, generated[i].Width, -2, i, poly);
            }

            return fronts;
        }

        private sealed class Chain
        {
            public List<Vec2> Points;
            public double Width;
            public int Id;
        }

        private static (long, long) Key(Vec2 p) => ((long)Math.Round(p.X * 1000), (long)Math.Round(p.Y * 1000));

        /// <summary>
        /// Joins road segments into polylines where exactly two segments of the same width meet at a
        /// shared end with a gentle turn (curve samples, degree-2 nodes); junctions and corners split.
        /// </summary>
        private static List<Chain> Chains(IReadOnlyList<RoadSegment> segs)
        {
            var valid = segs.Where(s => Vec2.Distance(s.A, s.B) > 1e-6).ToList();
            var ends = new Dictionary<(long, long), List<(int Seg, bool AtB)>>();
            for (int i = 0; i < valid.Count; i++)
            {
                foreach (bool atB in new[] { false, true })
                {
                    var key = Key(atB ? valid[i].B : valid[i].A);
                    if (!ends.TryGetValue(key, out var list))
                    {
                        ends[key] = list = new List<(int, bool)>();
                    }

                    list.Add((i, atB));
                }
            }

            double minCos = Math.Cos(kMaxJoinTurnDegrees * Math.PI / 180);

            // The segment continuing from segment i at its end (atB) and whether it must be reversed.
            bool Next(int i, bool atB, out int next, out bool nextAtB)
            {
                next = -1;
                nextAtB = false;
                var here = ends[Key(atB ? valid[i].B : valid[i].A)];
                if (here.Count != 2)
                {
                    return false;
                }

                var other = here[0].Seg == i && here[0].AtB == atB ? here[1] : here[0];
                if (other.Seg == i || Math.Abs(valid[other.Seg].Width - valid[i].Width) > 0.01)
                {
                    return false;
                }

                // Direction arriving at the shared point and leaving it on the other segment.
                Vec2 arrive = atB ? (valid[i].B - valid[i].A).Normalized : (valid[i].A - valid[i].B).Normalized;
                Vec2 leave = other.AtB ? (valid[other.Seg].A - valid[other.Seg].B).Normalized : (valid[other.Seg].B - valid[other.Seg].A).Normalized;
                if (Vec2.Dot(arrive, leave) < minCos)
                {
                    return false;
                }

                next = other.Seg;
                nextAtB = !other.AtB; // continue from the far end of the next segment
                return true;
            }

            var used = new bool[valid.Count];
            var chains = new List<Chain>();
            for (int start = 0; start < valid.Count; start++)
            {
                if (used[start])
                {
                    continue;
                }

                // State (segment, forward): forward walks A -> B and leaves through B. Walk the chain
                // in reverse from `start` to find its beginning (stopping on a loop), then flip.
                int cur = start;
                bool reverse = false; // walking `start` B -> A, leaving through A
                var seen = new HashSet<int> { start };
                while (Next(cur, reverse, out int prev, out bool prevDir) && !seen.Contains(prev) && !used[prev])
                {
                    seen.Add(prev);
                    cur = prev;
                    reverse = prevDir;
                }

                int first = cur;
                bool firstForward = !reverse;

                var chain = new Chain { Points = new List<Vec2>(), Width = valid[first].Width, Id = valid[first].Id };
                int seg = first;
                bool forward = firstForward;
                chain.Points.Add(forward ? valid[seg].A : valid[seg].B);
                while (true)
                {
                    used[seg] = true;
                    chain.Points.Add(forward ? valid[seg].B : valid[seg].A);
                    if (!Next(seg, forward, out int next, out bool nextFar) || used[next])
                    {
                        break;
                    }

                    seg = next;
                    forward = nextFar; // next runs toward its far end: forward if the far end is B
                }

                chains.Add(chain);
            }

            return chains;
        }

        private static void AddSides(List<Frontage> fronts, List<Vec2> centre, double width, int roadId, int roadIndex, List<Vec2> poly)
        {
            foreach (int side in new[] { 1, -1 })
            {
                var curb = PolylineOffset.Offset(centre, side * width / 2, closed: false);
                if (curb.Count < 2)
                {
                    continue;
                }

                // The curb often lies exactly on the area boundary; test inside-ness a hair toward
                // the lots so a collinear edge is not lost to point-on-edge ambiguity.
                Frontage run = null;
                bool open = false;
                for (int k = 0; k + 1 < curb.Count; k++)
                {
                    Vec2 a = curb[k];
                    Vec2 b = curb[k + 1];
                    if (Vec2.Distance(a, b) < 1e-9)
                    {
                        continue;
                    }

                    Vec2 nudge = (b - a).Normalized.Perp * side * 0.05;
                    var intervals = Polygon.ClipSegment(poly, a + nudge, b + nudge);
                    if (intervals.Count == 0)
                    {
                        Close(fronts, ref run);
                        open = false;
                        continue;
                    }

                    foreach (var (t0, t1) in intervals)
                    {
                        if (!(open && run != null && t0 < 1e-6))
                        {
                            Close(fronts, ref run);
                            run = new Frontage { Side = side, RoadId = roadId, RoadIndex = roadIndex };
                            run.Add(Vec2.Lerp(a, b, t0));
                        }

                        run.Add(Vec2.Lerp(a, b, t1));
                        open = t1 > 1 - 1e-6;
                    }

                    if (!open)
                    {
                        Close(fronts, ref run);
                    }
                }

                Close(fronts, ref run);
            }
        }

        private static void Close(List<Frontage> fronts, ref Frontage run)
        {
            if (run != null && run.Points.Count >= 2 && run.Length >= 1)
            {
                fronts.Add(run);
            }

            run = null;
        }
    }
}
