using System;
using System.Collections.Generic;

namespace BlockBuilder.Core
{
    public static class BoundarySnap
    {
        /// <summary>
        /// Moves boundary edges that run along a road onto that road's side (curb line), so it does
        /// not matter whether the user clicked on the centre line, the curb, or a little inside.
        /// Corners become intersections of the adjusted edge lines, which gives clean corners at
        /// junctions. Returns the input unchanged if the result would not be a valid polygon.
        /// </summary>
        /// <param name="poly">Counter-clockwise polygon (see <see cref="Polygon.Normalize"/>).</param>
        public static List<Vec2> SnapToCurbs(List<Vec2> poly, IReadOnlyList<RoadSegment> roads, double tolerance = 6)
        {
            int n = poly.Count;
            if (n < 3 || roads == null || roads.Count == 0)
            {
                return poly;
            }

            var linePoint = new Vec2[n];
            var lineDir = new Vec2[n];
            var moved = new bool[n];
            for (int i = 0; i < n; i++)
            {
                Vec2 a = poly[i];
                Vec2 b = poly[(i + 1) % n];
                Vec2 d = (b - a).Normalized;
                Vec2 inward = d.Perp;
                linePoint[i] = a;
                lineDir[i] = d;

                double bestError = double.MaxValue;
                Vec2 mid = (a + b) / 2;
                foreach (var seg in roads)
                {
                    Vec2 sd = (seg.B - seg.A).Normalized;
                    double alignment = Vec2.Dot(d, sd);
                    if (Math.Abs(alignment) < 0.985)
                    {
                        continue;
                    }

                    double length = Vec2.Distance(seg.A, seg.B);
                    double along = Vec2.Dot(mid - seg.A, sd);
                    if (along < -tolerance || along > length + tolerance)
                    {
                        continue;
                    }

                    Vec2 normal = Vec2.Dot(sd.Perp, inward) >= 0 ? sd.Perp : -sd.Perp;
                    double half = seg.Width / 2;
                    double oa = Vec2.Dot(a - seg.A, normal);
                    double ob = Vec2.Dot(b - seg.A, normal);
                    // Accept edges anywhere from slightly outside the centre line to a little
                    // inside the curb.
                    if (oa < -tolerance || ob < -tolerance || oa > half + tolerance || ob > half + tolerance)
                    {
                        continue;
                    }

                    double error = Math.Abs(oa - half) + Math.Abs(ob - half);
                    if (error >= bestError)
                    {
                        continue;
                    }

                    bestError = error;
                    linePoint[i] = seg.A + normal * half;
                    lineDir[i] = alignment >= 0 ? sd : -sd;
                    moved[i] = true;
                }
            }

            var result = new List<Vec2>(n);
            for (int i = 0; i < n; i++)
            {
                int prev = (i + n - 1) % n;
                if (!moved[prev] && !moved[i])
                {
                    result.Add(poly[i]);
                    continue;
                }

                if (LineIntersection(linePoint[prev], lineDir[prev], linePoint[i], lineDir[i], out Vec2 corner))
                {
                    result.Add(corner);
                }
                else
                {
                    // Parallel neighbours: project the original corner onto the moved line.
                    int k = moved[i] ? i : prev;
                    result.Add(linePoint[k] + lineDir[k] * Vec2.Dot(poly[i] - linePoint[k], lineDir[k]));
                }
            }

            if (Polygon.SignedArea(result) <= 1 || SelfIntersects(result))
            {
                return poly;
            }

            return result;
        }

        private static bool LineIntersection(Vec2 p, Vec2 r, Vec2 q, Vec2 s, out Vec2 point)
        {
            double denom = Vec2.Cross(r, s);
            point = p;
            if (Math.Abs(denom) < 1e-6)
            {
                return false;
            }

            double t = Vec2.Cross(q - p, s) / denom;
            point = p + r * t;
            return true;
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
