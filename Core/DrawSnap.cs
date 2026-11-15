using System;
using System.Collections.Generic;
using System.Linq;

namespace BlockBuilder.Core
{
    public enum SnapKind
    {
        None,
        /// <summary>On the curb line of the nearest road.</summary>
        Curb,
        /// <summary>Where the curb lines of two non-parallel roads meet (a junction corner).</summary>
        CurbCorner,
        /// <summary>Edge from the previous corner kept parallel/perpendicular to a road or the previous edge.</summary>
        Angle,
        /// <summary>Angle constraint and curb line at once.</summary>
        AngleOnCurb,
    }

    public sealed class SnapResult
    {
        public Vec2 Point;
        public SnapKind Kind;
        /// <summary>Direction of the angle constraint from the previous corner (Angle, AngleOnCurb).</summary>
        public Vec2 GuideDir;
        /// <summary>Curb line used (Curb, CurbCorner, AngleOnCurb): a point on it and its direction.</summary>
        public Vec2 CurbPoint;
        public Vec2 CurbDir;
        /// <summary>Edge length after cell-length rounding, 0 when not rounded.</summary>
        public double CellLength;
    }

    /// <summary>
    /// Snapping for the corner being placed while drawing an area: curb lines, curb corners at
    /// junctions, and edges parallel or perpendicular to nearby roads or the previous edge.
    /// </summary>
    public static class DrawSnap
    {
        private struct Curb
        {
            public Vec2 Point;
            public Vec2 Dir;
            public double Distance;
            public int RoadId;
        }

        /// <param name="curbs">Snap to curb lines and junction corners (game option: existing geometry).</param>
        /// <param name="angles">Keep edges parallel/perpendicular (game option: straight direction).</param>
        /// <param name="cellLength">When &gt; 0, an edge not fixed by a curb gets a length in multiples of this.</param>
        public static SnapResult Snap(Vec2 cursor, IReadOnlyList<Vec2> corners, IEnumerable<RoadSegment> roads, double curbTolerance = 6, double angleToleranceDeg = 4, double directionRadius = 60, bool curbs = true, bool angles = true, double cellLength = 0)
        {
            var result = SnapGeometry(cursor, corners, curbs ? roads : Enumerable.Empty<RoadSegment>(), curbTolerance, angles ? angleToleranceDeg : -1, directionRadius, angles ? roads : Enumerable.Empty<RoadSegment>());
            if (cellLength > 0 && corners.Count > 0 && (result.Kind == SnapKind.None || result.Kind == SnapKind.Angle))
            {
                Vec2 prev = corners[corners.Count - 1];
                Vec2 d = result.Point - prev;
                double length = d.Length;
                if (length > cellLength / 2)
                {
                    double rounded = Math.Max(cellLength, Math.Round(length / cellLength) * cellLength);
                    result.Point = prev + d / length * rounded;
                    result.CellLength = rounded;
                }
            }

            return result;
        }

        private static SnapResult SnapGeometry(Vec2 cursor, IReadOnlyList<Vec2> corners, IEnumerable<RoadSegment> roads, double curbTolerance, double angleToleranceDeg, double directionRadius, IEnumerable<RoadSegment> directionRoads)
        {
            var roadList = roads as IList<RoadSegment> ?? roads.ToList();
            var directionList = directionRoads as IList<RoadSegment> ?? directionRoads.ToList();
            var curbs = new List<Curb>();
            foreach (var seg in roadList)
            {
                Vec2 d = seg.B - seg.A;
                if (d.Length < 1e-6)
                {
                    continue;
                }

                double centre = Segment.Distance(seg.A, seg.B, cursor, out double t);
                double distance = Math.Abs(centre - seg.Width / 2);
                if (distance > curbTolerance)
                {
                    continue;
                }

                Vec2 dir = d.Normalized;
                Vec2 on = Vec2.Lerp(seg.A, seg.B, t);
                Vec2 n = Vec2.Cross(dir, cursor - on) >= 0 ? dir.Perp : -dir.Perp;
                curbs.Add(new Curb { Point = on + n * (seg.Width / 2), Dir = dir, Distance = distance, RoadId = seg.Id });
            }

            curbs.Sort((a, b) => a.Distance.CompareTo(b.Distance));

            // 1. Junction corner: two curb lines meeting at an angle.
            SnapResult best = null;
            double bestDistance = double.MaxValue;
            for (int i = 0; i < curbs.Count; i++)
            {
                for (int j = i + 1; j < curbs.Count; j++)
                {
                    if (Math.Abs(Vec2.Cross(curbs[i].Dir, curbs[j].Dir)) < Math.Sin(20 * Math.PI / 180))
                    {
                        continue;
                    }

                    if (!LineIntersect(curbs[i].Point, curbs[i].Dir, curbs[j].Point, curbs[j].Dir, out Vec2 x))
                    {
                        continue;
                    }

                    double dist = Vec2.Distance(x, cursor);
                    if (dist <= curbTolerance * 1.5 && dist < bestDistance)
                    {
                        bestDistance = dist;
                        best = new SnapResult { Point = x, Kind = SnapKind.CurbCorner, CurbPoint = curbs[i].Point, CurbDir = curbs[i].Dir };
                    }
                }
            }

            if (best != null)
            {
                return best;
            }

            // 2. Angle constraint from the previous corner.
            Vec2? guide = null;
            Vec2 prev = default;
            if (corners.Count > 0 && angleToleranceDeg >= 0)
            {
                prev = corners[corners.Count - 1];
                Vec2 toCursor = cursor - prev;
                if (toCursor.Length > 4)
                {
                    var dirs = new List<Vec2>();
                    if (corners.Count > 1)
                    {
                        Vec2 last = prev - corners[corners.Count - 2];
                        if (last.Length > 1e-6)
                        {
                            dirs.Add(last.Normalized);
                            dirs.Add(last.Normalized.Perp);
                        }
                    }

                    foreach (var seg in directionList)
                    {
                        Vec2 d = seg.B - seg.A;
                        if (d.Length < 1e-6)
                        {
                            continue;
                        }

                        if (Segment.Distance(seg.A, seg.B, prev, out _) <= directionRadius || Segment.Distance(seg.A, seg.B, cursor, out _) <= directionRadius)
                        {
                            dirs.Add(d.Normalized);
                            dirs.Add(d.Normalized.Perp);
                        }
                    }

                    Vec2 want = toCursor.Normalized;
                    double bestSin = Math.Sin(angleToleranceDeg * Math.PI / 180);
                    foreach (var d in dirs)
                    {
                        double s = Math.Abs(Vec2.Cross(d, want));
                        if (s < bestSin && Vec2.Dot(d, want) != 0)
                        {
                            bestSin = s;
                            guide = Vec2.Dot(d, want) >= 0 ? d : -d;
                        }
                    }
                }
            }

            if (guide.HasValue)
            {
                Vec2 g = guide.Value;
                Vec2 onRay = prev + g * Vec2.Dot(cursor - prev, g);
                foreach (var c in curbs)
                {
                    if (Math.Abs(Vec2.Cross(g, c.Dir)) < Math.Sin(10 * Math.PI / 180))
                    {
                        continue;
                    }

                    if (LineIntersect(prev, g, c.Point, c.Dir, out Vec2 x) && Vec2.Distance(x, cursor) <= curbTolerance * 1.5 && Vec2.Dot(x - prev, g) > 0)
                    {
                        return new SnapResult { Point = x, Kind = SnapKind.AngleOnCurb, GuideDir = g, CurbPoint = c.Point, CurbDir = c.Dir };
                    }
                }

                if (curbs.Count == 0)
                {
                    return new SnapResult { Point = onRay, Kind = SnapKind.Angle, GuideDir = g };
                }
            }

            // 3. Plain curb.
            if (curbs.Count > 0)
            {
                return new SnapResult { Point = curbs[0].Point, Kind = SnapKind.Curb, CurbPoint = curbs[0].Point, CurbDir = curbs[0].Dir };
            }

            return new SnapResult { Point = cursor, Kind = SnapKind.None };
        }

        private static bool LineIntersect(Vec2 p, Vec2 r, Vec2 q, Vec2 s, out Vec2 x)
        {
            double denom = Vec2.Cross(r, s);
            if (Math.Abs(denom) < 1e-9)
            {
                x = default;
                return false;
            }

            double t = Vec2.Cross(q - p, s) / denom;
            x = p + r * t;
            return true;
        }
    }
}
