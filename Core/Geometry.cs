using System;
using System.Collections.Generic;

namespace BlockBuilder.Core
{
    /// <summary>2D vector on the ground plane (game x → X, game z → Y), in metres.</summary>
    public readonly struct Vec2 : IEquatable<Vec2>
    {
        public readonly double X;
        public readonly double Y;

        public Vec2(double x, double y)
        {
            X = x;
            Y = y;
        }

        public static Vec2 operator +(Vec2 a, Vec2 b) => new Vec2(a.X + b.X, a.Y + b.Y);
        public static Vec2 operator -(Vec2 a, Vec2 b) => new Vec2(a.X - b.X, a.Y - b.Y);
        public static Vec2 operator -(Vec2 a) => new Vec2(-a.X, -a.Y);
        public static Vec2 operator *(Vec2 a, double s) => new Vec2(a.X * s, a.Y * s);
        public static Vec2 operator *(double s, Vec2 a) => new Vec2(a.X * s, a.Y * s);
        public static Vec2 operator /(Vec2 a, double s) => new Vec2(a.X / s, a.Y / s);

        public double Length => Math.Sqrt(X * X + Y * Y);
        public double LengthSquared => X * X + Y * Y;

        public Vec2 Normalized
        {
            get
            {
                double l = Length;
                return l < 1e-12 ? new Vec2(0, 0) : new Vec2(X / l, Y / l);
            }
        }

        /// <summary>Counter-clockwise perpendicular.</summary>
        public Vec2 Perp => new Vec2(-Y, X);

        public static double Dot(Vec2 a, Vec2 b) => a.X * b.X + a.Y * b.Y;

        public static double Cross(Vec2 a, Vec2 b) => a.X * b.Y - a.Y * b.X;

        public static double Distance(Vec2 a, Vec2 b) => (a - b).Length;

        public static Vec2 Lerp(Vec2 a, Vec2 b, double t) => a + (b - a) * t;

        public bool Equals(Vec2 other) => X == other.X && Y == other.Y;

        public override bool Equals(object obj) => obj is Vec2 v && Equals(v);

        public override int GetHashCode() => X.GetHashCode() * 397 ^ Y.GetHashCode();

        public override string ToString() => FormattableString.Invariant($"({X:0.##}, {Y:0.##})");
    }

    /// <summary>Oriented rectangle. Forward is the building front (game local +Z).</summary>
    public readonly struct Obb
    {
        public readonly Vec2 Center;
        /// <summary>Unit vector the front faces.</summary>
        public readonly Vec2 Forward;
        /// <summary>Extent across the front (lot width), metres.</summary>
        public readonly double Width;
        /// <summary>Extent along Forward (lot depth), metres.</summary>
        public readonly double Depth;

        public Obb(Vec2 center, Vec2 forward, double width, double depth)
        {
            Center = center;
            Forward = forward.Normalized;
            Width = width;
            Depth = depth;
        }

        /// <summary>Unit vector along the front edge.</summary>
        public Vec2 Right => new Vec2(Forward.Y, -Forward.X);

        public Vec2[] Corners()
        {
            Vec2 r = Right * (Width / 2);
            Vec2 f = Forward * (Depth / 2);
            return new[] { Center - r - f, Center + r - f, Center + r + f, Center - r + f };
        }

        /// <summary>
        /// Separating-axis overlap. Touching or sharing an edge is NOT an overlap: the game accepts
        /// lots placed edge to edge, so anything within <paramref name="tolerance"/> counts as apart.
        /// </summary>
        public bool Overlaps(Obb other, double tolerance = 0.05)
        {
            var axes = new[] { Forward, Right, other.Forward, other.Right };
            var a = Corners();
            var b = other.Corners();
            foreach (var axis in axes)
            {
                Project(a, axis, out double minA, out double maxA);
                Project(b, axis, out double minB, out double maxB);
                if (maxA <= minB + tolerance || maxB <= minA + tolerance)
                {
                    return false;
                }
            }

            return true;
        }

        private static void Project(Vec2[] points, Vec2 axis, out double min, out double max)
        {
            min = double.MaxValue;
            max = double.MinValue;
            foreach (var p in points)
            {
                double d = Vec2.Dot(p, axis);
                if (d < min)
                {
                    min = d;
                }

                if (d > max)
                {
                    max = d;
                }
            }
        }

        /// <summary>Rectangle covering a road segment of the given width (a strip).</summary>
        public static Obb FromSegment(Vec2 a, Vec2 b, double width)
        {
            Vec2 d = b - a;
            double length = d.Length;
            Vec2 dir = length < 1e-9 ? new Vec2(1, 0) : d / length;
            // Forward is across the strip, Width along it.
            return new Obb((a + b) / 2, dir.Perp, length, width);
        }
    }

    public static class Polygon
    {
        public static double SignedArea(IReadOnlyList<Vec2> poly)
        {
            double area = 0;
            for (int i = 0; i < poly.Count; i++)
            {
                var a = poly[i];
                var b = poly[(i + 1) % poly.Count];
                area += Vec2.Cross(a, b);
            }

            return area / 2;
        }

        /// <summary>Returns a counter-clockwise copy with duplicate/closing vertices removed.</summary>
        public static List<Vec2> Normalize(IReadOnlyList<Vec2> poly)
        {
            var result = new List<Vec2>();
            foreach (var p in poly)
            {
                if (result.Count == 0 || Vec2.Distance(result[result.Count - 1], p) > 1e-6)
                {
                    result.Add(p);
                }
            }

            if (result.Count > 1 && Vec2.Distance(result[0], result[result.Count - 1]) <= 1e-6)
            {
                result.RemoveAt(result.Count - 1);
            }

            if (SignedArea(result) < 0)
            {
                result.Reverse();
            }

            return result;
        }

        public static bool Contains(IReadOnlyList<Vec2> poly, Vec2 p)
        {
            bool inside = false;
            for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
            {
                var a = poly[i];
                var b = poly[j];
                if ((a.Y > p.Y) != (b.Y > p.Y) && p.X < (b.X - a.X) * (p.Y - a.Y) / (b.Y - a.Y) + a.X)
                {
                    inside = !inside;
                }
            }

            return inside;
        }

        public static double DistanceToBoundary(IReadOnlyList<Vec2> poly, Vec2 p)
        {
            double best = double.MaxValue;
            for (int i = 0; i < poly.Count; i++)
            {
                best = Math.Min(best, Segment.Distance(poly[i], poly[(i + 1) % poly.Count], p, out _));
            }

            return best;
        }

        /// <summary>
        /// True when the rectangle lies inside the polygon. Points within <paramref name="tolerance"/>
        /// of the boundary count as inside, so a lot may sit flush on the site edge.
        /// </summary>
        public static bool ContainsObb(IReadOnlyList<Vec2> poly, Obb box, double tolerance = 0.05)
        {
            var corners = box.Corners();
            foreach (var c in corners)
            {
                if (!Contains(poly, c) && DistanceToBoundary(poly, c) > tolerance)
                {
                    return false;
                }
            }

            // A concave boundary can cut through a rectangle whose corners are all inside.
            Obb shrunk = new Obb(box.Center, box.Forward, Math.Max(0, box.Width - 2 * tolerance), Math.Max(0, box.Depth - 2 * tolerance));
            var inner = shrunk.Corners();
            for (int i = 0; i < 4; i++)
            {
                var a = inner[i];
                var b = inner[(i + 1) % 4];
                for (int k = 0; k < poly.Count; k++)
                {
                    if (Segment.ProperIntersect(a, b, poly[k], poly[(k + 1) % poly.Count]))
                    {
                        return false;
                    }
                }
            }

            foreach (var v in poly)
            {
                if (PointInObb(shrunk, v))
                {
                    return false;
                }
            }

            return true;
        }

        public static bool PointInObb(Obb box, Vec2 p)
        {
            Vec2 d = p - box.Center;
            return Math.Abs(Vec2.Dot(d, box.Right)) < box.Width / 2 && Math.Abs(Vec2.Dot(d, box.Forward)) < box.Depth / 2;
        }

        /// <summary>
        /// Parameter intervals [t0, t1] (0..1 along a→b) where the segment lies inside the polygon.
        /// </summary>
        public static List<(double, double)> ClipSegment(IReadOnlyList<Vec2> poly, Vec2 a, Vec2 b)
        {
            var ts = new List<double> { 0, 1 };
            for (int i = 0; i < poly.Count; i++)
            {
                if (Segment.Intersect(a, b, poly[i], poly[(i + 1) % poly.Count], out double t, out _))
                {
                    ts.Add(t);
                }
            }

            ts.Sort();
            var result = new List<(double, double)>();
            for (int i = 0; i + 1 < ts.Count; i++)
            {
                double t0 = ts[i];
                double t1 = ts[i + 1];
                if (t1 - t0 < 1e-9)
                {
                    continue;
                }

                if (Contains(poly, Vec2.Lerp(a, b, (t0 + t1) / 2)))
                {
                    if (result.Count > 0 && Math.Abs(result[result.Count - 1].Item2 - t0) < 1e-9)
                    {
                        result[result.Count - 1] = (result[result.Count - 1].Item1, t1);
                    }
                    else
                    {
                        result.Add((t0, t1));
                    }
                }
            }

            return result;
        }

        /// <summary>Direction of the longest edge; generated streets follow it.</summary>
        public static Vec2 PrincipalAxis(IReadOnlyList<Vec2> poly)
        {
            Vec2 best = new Vec2(1, 0);
            double bestLength = -1;
            for (int i = 0; i < poly.Count; i++)
            {
                Vec2 d = poly[(i + 1) % poly.Count] - poly[i];
                if (d.Length > bestLength)
                {
                    bestLength = d.Length;
                    best = d.Normalized;
                }
            }

            return best;
        }
    }

    public static class Segment
    {
        public static double Distance(Vec2 a, Vec2 b, Vec2 p, out double t)
        {
            Vec2 d = b - a;
            double len2 = d.LengthSquared;
            t = len2 < 1e-12 ? 0 : Math.Max(0, Math.Min(1, Vec2.Dot(p - a, d) / len2));
            return Vec2.Distance(a + d * t, p);
        }

        /// <summary>Intersection of segments ab and cd (endpoints included).</summary>
        public static bool Intersect(Vec2 a, Vec2 b, Vec2 c, Vec2 d, out double t, out double u)
        {
            Vec2 r = b - a;
            Vec2 s = d - c;
            double denom = Vec2.Cross(r, s);
            t = u = 0;
            if (Math.Abs(denom) < 1e-12)
            {
                return false;
            }

            t = Vec2.Cross(c - a, s) / denom;
            u = Vec2.Cross(c - a, r) / denom;
            return t >= -1e-9 && t <= 1 + 1e-9 && u >= -1e-9 && u <= 1 + 1e-9;
        }

        /// <summary>Crossing strictly inside both segments.</summary>
        public static bool ProperIntersect(Vec2 a, Vec2 b, Vec2 c, Vec2 d)
        {
            const double e = 1e-6;
            return Intersect(a, b, c, d, out double t, out double u) && t > e && t < 1 - e && u > e && u < 1 - e;
        }
    }
}
