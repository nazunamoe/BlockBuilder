using System;
using System.Collections.Generic;

namespace BlockBuilder.Core
{
    /// <summary>
    /// Offsets polylines and rings sideways with a mitre join (and a bevel when the mitre would be
    /// too long). Used both for picked block rings and for curb lines along roads, so the two always
    /// agree exactly.
    /// </summary>
    public static class PolylineOffset
    {
        /// <summary>A mitre longer than this many times the offset becomes a bevel.</summary>
        private const double kMitreLimit = 4;

        /// <summary>
        /// Offset with one distance per segment (segment k runs from points[k] to points[k+1], and for
        /// a closed ring the last segment runs back to points[0]). Positive offsets go to the left
        /// (<see cref="Vec2.Perp"/>), which is inward for a counter-clockwise ring.
        /// </summary>
        public static List<Vec2> Offset(IReadOnlyList<Vec2> points, IReadOnlyList<double> offsets, bool closed)
        {
            int n = points.Count;
            int segments = closed ? n : n - 1;
            var result = new List<Vec2>(n + 4);
            if (n < 2 || offsets.Count < segments)
            {
                return result;
            }

            var dirs = new Vec2[segments];
            for (int k = 0; k < segments; k++)
            {
                dirs[k] = (points[(k + 1) % n] - points[k]).Normalized;
            }

            for (int i = 0; i < n; i++)
            {
                bool hasPrev = closed || i > 0;
                bool hasNext = closed || i < n - 1;
                int prev = (i - 1 + segments) % segments;
                int next = i % segments;
                if (!hasPrev)
                {
                    result.Add(points[i] + dirs[next].Perp * offsets[next]);
                    continue;
                }

                if (!hasNext)
                {
                    result.Add(points[i] + dirs[prev].Perp * offsets[prev]);
                    continue;
                }

                Join(points[i], dirs[prev], offsets[prev], dirs[next], offsets[next], result);
            }

            return result;
        }

        /// <summary>Same distance for every segment.</summary>
        public static List<Vec2> Offset(IReadOnlyList<Vec2> points, double offset, bool closed)
        {
            int segments = closed ? points.Count : points.Count - 1;
            var offsets = new double[Math.Max(0, segments)];
            for (int k = 0; k < offsets.Length; k++)
            {
                offsets[k] = offset;
            }

            return Offset(points, offsets, closed);
        }

        private static void Join(Vec2 p, Vec2 da, double oa, Vec2 db, double ob, List<Vec2> output)
        {
            Vec2 a = p + da.Perp * oa;
            Vec2 b = p + db.Perp * ob;
            double cross = Vec2.Cross(da, db);
            if (Math.Abs(cross) < 1e-9)
            {
                output.Add(a);
                if (Vec2.Distance(a, b) > 1e-6)
                {
                    output.Add(b); // same direction, different offset: a step
                }

                return;
            }

            // Intersection of the two offset lines.
            double t = Vec2.Cross(b - a, db) / cross;
            Vec2 mitre = a + da * t;
            double limit = kMitreLimit * Math.Max(Math.Abs(oa), Math.Abs(ob));
            if (Vec2.Distance(mitre, p) <= limit + 1e-9)
            {
                output.Add(mitre);
            }
            else
            {
                output.Add(a);
                output.Add(b);
            }
        }

        /// <summary>
        /// Drops vertices closer than <paramref name="minDistance"/> to the previous one and vertices
        /// that lie exactly on the line through their neighbours. Curves are kept as they are.
        /// </summary>
        public static List<Vec2> Clean(IReadOnlyList<Vec2> ring, double minDistance = 0.05)
        {
            var pts = new List<Vec2>();
            foreach (var p in ring)
            {
                if (pts.Count == 0 || Vec2.Distance(pts[pts.Count - 1], p) >= minDistance)
                {
                    pts.Add(p);
                }
            }

            if (pts.Count > 1 && Vec2.Distance(pts[0], pts[pts.Count - 1]) < minDistance)
            {
                pts.RemoveAt(pts.Count - 1);
            }

            bool changed = true;
            while (changed && pts.Count > 3)
            {
                changed = false;
                for (int i = 0; i < pts.Count && pts.Count > 3; i++)
                {
                    Vec2 a = pts[(i - 1 + pts.Count) % pts.Count];
                    Vec2 b = pts[i];
                    Vec2 c = pts[(i + 1) % pts.Count];
                    Vec2 ab = b - a;
                    Vec2 bc = c - b;
                    if (Math.Abs(Vec2.Cross(ab, bc)) <= 1e-6 * ab.Length * bc.Length && Vec2.Dot(ab, bc) > 0)
                    {
                        pts.RemoveAt(i);
                        changed = true;
                        i--;
                    }
                }
            }

            return pts;
        }
    }
}
