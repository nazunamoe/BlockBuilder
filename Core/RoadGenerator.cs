using System;
using System.Collections.Generic;
using System.Linq;

namespace BlockBuilder.Core
{
    /// <summary>
    /// Generates internal streets for a block. Streets run along the block's longest edge, spaced so
    /// two rows of the deepest lot fit back to back between them. Street ends snap onto existing roads
    /// they reach; streets never cross an existing road mid-span without being split there. Long
    /// streets get cross streets toward the previous street (or the boundary road).
    /// </summary>
    public static class RoadGenerator
    {
        private sealed class Split
        {
            public double T;
            public RoadJoin Join;
        }

        private sealed class Piece
        {
            public Vec2 A;
            public Vec2 B;
            public int Line;
            public readonly List<Split> Splits = new List<Split>();
        }

        public static void Generate(List<Vec2> poly, List<RoadSegment> existing, double rowDepth, LayoutOptions options, List<Placement> fixedItems, LayoutResult result)
        {
            double w = options.NewRoadWidth;
            Vec2 u = Polygon.PrincipalAxis(poly);
            Vec2 v = u.Perp;
            double uMin = double.MaxValue, uMax = double.MinValue, vMin = double.MaxValue, vMax = double.MinValue;
            foreach (var p in poly)
            {
                uMin = Math.Min(uMin, Vec2.Dot(p, u));
                uMax = Math.Max(uMax, Vec2.Dot(p, u));
                vMin = Math.Min(vMin, Vec2.Dot(p, v));
                vMax = Math.Max(vMax, Vec2.Dot(p, v));
            }

            // If the long edge already has a road, the first row faces it, so the first street sits
            // behind two rows; otherwise one row faces the new street from the edge side.
            bool edgeHasRoad = LongEdgeHasRoad(poly, existing, u);
            double spacing = 2 * rowDepth + w;
            double first = vMin + (edgeHasRoad ? 2 * rowDepth : rowDepth) + w / 2;

            var pieces = new List<Piece>();
            int line = 0;
            for (double vk = first; vk + w / 2 + rowDepth <= vMax + 1e-6; vk += spacing, line++)
            {
                Vec2 a = u * (uMin - 50) + v * vk;
                Vec2 b = u * (uMax + 50) + v * vk;
                foreach (var (t0, t1) in Polygon.ClipSegment(poly, a, b))
                {
                    var piece = new Piece { A = Vec2.Lerp(a, b, t0), B = Vec2.Lerp(a, b, t1), Line = line };
                    if (Vec2.Distance(piece.A, piece.B) < 16)
                    {
                        continue;
                    }

                    if (fixedItems.Any(f => Obb.FromSegment(piece.A, piece.B, w).Overlaps(f.Box)))
                    {
                        result.Warnings.Add("a generated street was dropped because it crosses a fixed item");
                        continue;
                    }

                    pieces.Add(piece);
                }
            }

            if (pieces.Count == 0)
            {
                if (!edgeHasRoad || vMax - vMin > 2 * rowDepth + 1)
                {
                    // Depth across the streets: rows before the first street, the street, one row after it.
                    double needed = (edgeHasRoad ? 3 : 2) * rowDepth + w;
                    result.Warnings.Add(string.Format(
                        System.Globalization.CultureInfo.InvariantCulture,
                        "the area is too shallow for a generated street: {0:0} m deep, needs {1:0} m ({2} rows of {3:0} m lots + {4:0} m street)",
                        vMax - vMin, needed, edgeHasRoad ? 3 : 2, rowDepth, w));
                }

                return;
            }

            foreach (var piece in pieces)
            {
                piece.Splits.Add(new Split { T = 0, Join = SnapEnd(ref piece.A, piece.B, existing, options) });
                piece.Splits.Add(new Split { T = 1, Join = SnapEnd(ref piece.B, piece.A, existing, options) });
                foreach (var seg in existing)
                {
                    if (Segment.Intersect(piece.A, piece.B, seg.A, seg.B, out double t, out double s) && t > 1e-3 && t < 1 - 1e-3)
                    {
                        piece.Splits.Add(new Split { T = t, Join = new RoadJoin { Kind = JoinKind.Existing, TargetId = seg.Id, TargetCurveT = Lerp(seg.CurveT0, seg.CurveT1, s) } });
                    }
                }
            }

            var connectors = new List<Piece>();
            foreach (var piece in pieces.ToList())
            {
                double length = Vec2.Distance(piece.A, piece.B);
                int cuts = (int)Math.Floor(length / Math.Max(32, options.MaxBlockLength));
                for (int j = 1; j <= cuts; j++)
                {
                    double t = (double)j / (cuts + 1);
                    var connector = BuildConnector(poly, piece, t, v, spacing, rowDepth, pieces, existing, options, w, fixedItems);
                    if (connector != null)
                    {
                        connectors.Add(connector);
                    }
                }
            }

            Emit(pieces.Concat(connectors), w, result);
        }

        private static double Lerp(double a, double b, double t) => a + (b - a) * t;

        private static bool LongEdgeHasRoad(List<Vec2> poly, List<RoadSegment> existing, Vec2 u)
        {
            for (int i = 0; i < poly.Count; i++)
            {
                var a = poly[i];
                var b = poly[(i + 1) % poly.Count];
                if (Vec2.Dot((b - a).Normalized, u) < 0.999)
                {
                    continue;
                }

                var mid = (a + b) / 2;
                return existing.Any(s => Segment.Distance(s.A, s.B, mid, out _) <= s.Width / 2 + 4);
            }

            return false;
        }

        /// <summary>
        /// Moves a street end that stops at the boundary onto an existing road centre line it points at.
        /// </summary>
        private static RoadJoin SnapEnd(ref Vec2 end, Vec2 other, List<RoadSegment> existing, LayoutOptions options)
        {
            Vec2 dir = (end - other).Normalized;
            RoadJoin best = default;
            double bestDistance = double.MaxValue;
            Vec2 bestPoint = end;
            foreach (var seg in existing)
            {
                double reach = seg.Width / 2 + options.JoinTolerance + 2;
                Vec2 far = end + dir * reach;
                if (!Segment.Intersect(other, far, seg.A, seg.B, out double t, out double s))
                {
                    continue;
                }

                Vec2 hit = Vec2.Lerp(other, far, t);
                double distance = Vec2.Distance(hit, end);
                if (Vec2.Dot(hit - end, dir) < -seg.Width / 2 - 1 || distance > reach || distance >= bestDistance)
                {
                    continue;
                }

                bestDistance = distance;
                bestPoint = hit;
                best = new RoadJoin { Kind = JoinKind.Existing, TargetId = seg.Id, TargetCurveT = Lerp(seg.CurveT0, seg.CurveT1, s) };
            }

            end = bestPoint;
            return best;
        }

        private static Piece BuildConnector(List<Vec2> poly, Piece piece, double t, Vec2 v, double spacing, double rowDepth, List<Piece> pieces, List<RoadSegment> existing, LayoutOptions options, double w, List<Placement> fixedItems)
        {
            Vec2 start = Vec2.Lerp(piece.A, piece.B, t);
            Vec2 far = start - v * (spacing + 4 * rowDepth);
            double best = double.MaxValue;
            Vec2 end = far;
            RoadJoin endJoin = default;
            Piece hitPiece = null;
            double hitT = 0;

            foreach (var other in pieces)
            {
                if (other == piece || other.Line >= piece.Line)
                {
                    continue;
                }

                if (Segment.Intersect(start, far, other.A, other.B, out double ct, out double ot) && ct > 1e-3 && ct < best)
                {
                    best = ct;
                    end = Vec2.Lerp(start, far, ct);
                    hitPiece = other;
                    hitT = ot;
                    endJoin = new RoadJoin { Kind = JoinKind.Generated };
                }
            }

            foreach (var seg in existing)
            {
                if (Segment.Intersect(start, far, seg.A, seg.B, out double ct, out double s) && ct > 1e-3 && ct < best)
                {
                    best = ct;
                    end = Vec2.Lerp(start, far, ct);
                    hitPiece = null;
                    endJoin = new RoadJoin { Kind = JoinKind.Existing, TargetId = seg.Id, TargetCurveT = Lerp(seg.CurveT0, seg.CurveT1, s) };
                }
            }

            if (best == double.MaxValue)
            {
                return null;
            }

            // Stay inside the site except for the final snap onto an existing road.
            var inside = Polygon.ClipSegment(poly, start, end);
            if (inside.Count == 0 || inside[0].Item1 > 1e-3)
            {
                return null;
            }

            if (endJoin.Kind == JoinKind.Generated && inside[0].Item2 < 1 - 1e-3)
            {
                return null;
            }

            if (Vec2.Distance(start, end) < 8 || fixedItems.Any(f => Obb.FromSegment(start, end, w).Overlaps(f.Box)))
            {
                return null;
            }

            piece.Splits.Add(new Split { T = t, Join = new RoadJoin { Kind = JoinKind.Generated } });
            if (hitPiece != null)
            {
                hitPiece.Splits.Add(new Split { T = hitT, Join = new RoadJoin { Kind = JoinKind.Generated } });
            }

            var connector = new Piece { A = start, B = end, Line = -1 };
            connector.Splits.Add(new Split { T = 0, Join = new RoadJoin { Kind = JoinKind.Generated } });
            connector.Splits.Add(new Split { T = 1, Join = endJoin });
            return connector;
        }

        private static void Emit(IEnumerable<Piece> pieces, double width, LayoutResult result)
        {
            int startIndex = result.Roads.Count;
            foreach (var piece in pieces)
            {
                var splits = piece.Splits.OrderBy(s => s.T).ToList();
                for (int i = 0; i + 1 < splits.Count; i++)
                {
                    if (splits[i + 1].T - splits[i].T < 1e-6)
                    {
                        continue;
                    }

                    var a = Vec2.Lerp(piece.A, piece.B, splits[i].T);
                    var b = Vec2.Lerp(piece.A, piece.B, splits[i + 1].T);
                    if (Vec2.Distance(a, b) < 1)
                    {
                        continue;
                    }

                    result.Roads.Add(new PlannedRoad { A = a, B = b, Width = width, StartJoin = splits[i].Join, EndJoin = splits[i + 1].Join });
                }
            }

            if (!MarkConnected(result.Roads, startIndex))
            {
                result.Warnings.Add("some generated streets do not reach an existing road");
            }
        }

        /// <summary>Returns true when every generated road reaches an existing road.</summary>
        private static bool MarkConnected(List<PlannedRoad> roads, int startIndex)
        {
            int n = roads.Count - startIndex;
            var parent = Enumerable.Range(0, n).ToArray();
            int Find(int x) => parent[x] == x ? x : parent[x] = Find(parent[x]);
            var byVertex = new Dictionary<(long, long), int>();
            (long, long) Key(Vec2 p) => ((long)Math.Round(p.X * 100), (long)Math.Round(p.Y * 100));
            for (int i = 0; i < n; i++)
            {
                foreach (var p in new[] { roads[startIndex + i].A, roads[startIndex + i].B })
                {
                    var key = Key(p);
                    if (byVertex.TryGetValue(key, out int other))
                    {
                        parent[Find(i)] = Find(other);
                    }
                    else
                    {
                        byVertex[key] = i;
                    }
                }
            }

            var rooted = new HashSet<int>();
            for (int i = 0; i < n; i++)
            {
                var r = roads[startIndex + i];
                if (r.StartJoin.Kind == JoinKind.Existing || r.EndJoin.Kind == JoinKind.Existing)
                {
                    rooted.Add(Find(i));
                }
            }

            bool all = true;
            for (int i = 0; i < n; i++)
            {
                roads[startIndex + i].Connected = rooted.Contains(Find(i));
                all &= roads[startIndex + i].Connected;
            }

            return all;
        }
    }
}
