using System;
using System.Collections.Generic;
using System.Linq;

namespace BlockBuilder.Core
{
    /// <summary>
    /// Frontage-based block filler. Lots are laid in rows along road edges, flush with the road side
    /// and facing it (the game only connects a building whose front is within ~8 m of a road).
    /// Optionally generates internal streets first so deep blocks get frontage too.
    /// Pure C#: no Unity or game types, so it runs under dotnet test.
    /// </summary>
    public static class LayoutEngine
    {
        private sealed class Frontage
        {
            public Vec2 Start;
            public Vec2 Dir;
            /// <summary>Points away from the road, into the lots.</summary>
            public Vec2 Normal;
            public double Length;
            public int RoadId;
            public int RoadIndex = -1;
        }

        private sealed class Context
        {
            public List<Vec2> Poly;
            public LayoutOptions Options;
            public Random Rng;
            /// <summary>Lots and buildings: may be overlapped by <see cref="LayoutOptions.OverlapAllowance"/>.</summary>
            public readonly List<Obb> Obstacles = new List<Obb>();
            /// <summary>Roads: never overlapped.</summary>
            public readonly List<Obb> Hard = new List<Obb>();
            public readonly Dictionary<string, int> Counts = new Dictionary<string, int>();
            public LayoutResult Result;
        }

        public static LayoutResult Run(LayoutRequest request)
        {
            var result = new LayoutResult();
            var poly = Polygon.Normalize(request.Boundary);
            if (poly.Count < 3 || Math.Abs(Polygon.SignedArea(poly)) < 16)
            {
                result.Warnings.Add("boundary is not a valid polygon");
                return result;
            }

            var options = request.Options ?? new LayoutOptions();
            if (options.SnapBoundaryToCurbs)
            {
                poly = BoundarySnap.SnapToCurbs(poly, request.ExistingRoads);
            }

            result.Boundary = poly;
            var ctx = new Context { Poly = poly, Options = options, Rng = new Random(options.Seed), Result = result };
            ctx.Obstacles.AddRange(request.Obstacles);

            foreach (var item in request.Fixed)
            {
                item.IsFixed = true;
                result.Placements.Add(item);
                ctx.Obstacles.Add(item.Box);
                Count(ctx, item.ItemId);
            }

            if (ctx.Options.Mode == FillMode.FixedOnly)
            {
                result.Counts = ctx.Counts;
                return result;
            }

            var items = request.Items.Where(i => i != null && i.Width > 0 && i.Depth > 0 && i.Weight > 0).ToList();
            var roadItems = items.Where(i => i.RequiresRoad).ToList();
            var interiorItems = items.Where(i => !i.RequiresRoad).ToList();

            foreach (var seg in request.ExistingRoads)
            {
                ctx.Hard.Add(Obb.FromSegment(seg.A, seg.B, seg.Width));
            }

            if (ctx.Options.GenerateRoads && roadItems.Count > 0)
            {
                var blockers = request.Fixed.Concat(request.Obstacles.Select(o => new Placement { Box = o })).ToList();
                RoadGenerator.Generate(ctx.Poly, request.ExistingRoads, roadItems.Max(i => i.Depth), ctx.Options, blockers, result);
                foreach (var road in result.Roads)
                {
                    ctx.Hard.Add(Obb.FromSegment(road.A, road.B, road.Width));
                }
            }

            var fronts = new List<Frontage>();
            foreach (var seg in request.ExistingRoads)
            {
                AddFrontages(ctx, fronts, seg.A, seg.B, seg.Width, seg.Id, -1);
            }

            for (int i = 0; i < result.Roads.Count; i++)
            {
                AddFrontages(ctx, fronts, result.Roads[i].A, result.Roads[i].B, result.Roads[i].Width, -2, i);
            }

            if (roadItems.Count > 0)
            {
                double minWidth = roadItems.Min(i => i.Width);
                foreach (var front in fronts.Where(f => f.Length + 1e-6 >= minWidth).OrderByDescending(f => f.Length))
                {
                    FillFrontage(ctx, front, roadItems);
                }
            }

            if (ctx.Options.FillInterior && interiorItems.Count > 0)
            {
                FillInterior(ctx, interiorItems);
            }

            if (roadItems.Count > 0 && fronts.Count == 0)
            {
                result.Warnings.Add("no road frontage inside the area; enable road generation or include a road");
            }

            result.Counts = ctx.Counts;
            return result;
        }

        /// <summary>
        /// Box for an item the user drops at <paramref name="point"/> (designate mode): flush against
        /// the nearest road side within <paramref name="maxDistance"/> and facing it, centred along
        /// the road at the cursor. Without a nearby road it is centred on the point facing +Y.
        /// </summary>
        public static Obb SnapToFrontage(IEnumerable<RoadSegment> roads, Vec2 point, double width, double depth, double maxDistance = 48)
        {
            RoadSegment best = null;
            double bestDistance = double.MaxValue;
            double bestT = 0;
            foreach (var seg in roads)
            {
                double d = Segment.Distance(seg.A, seg.B, point, out double t) - seg.Width / 2;
                if (d < bestDistance)
                {
                    bestDistance = d;
                    best = seg;
                    bestT = t;
                }
            }

            if (best == null || bestDistance > maxDistance)
            {
                return new Obb(point, new Vec2(0, 1), width, depth);
            }

            Vec2 dir = (best.B - best.A).Normalized;
            Vec2 on = Vec2.Lerp(best.A, best.B, bestT);
            Vec2 n = Vec2.Cross(dir, point - on) >= 0 ? dir.Perp : -dir.Perp;
            Vec2 center = on + n * (best.Width / 2 + depth / 2);
            return new Obb(center, -n, width, depth);
        }

        private static void Count(Context ctx, string id)
        {
            ctx.Counts.TryGetValue(id, out int n);
            ctx.Counts[id] = n + 1;
        }

        private static bool Available(Context ctx, PaletteItem item)
        {
            if (item.MaxCount <= 0)
            {
                return true;
            }

            ctx.Counts.TryGetValue(item.Id, out int n);
            return n < item.MaxCount;
        }

        private static void AddFrontages(Context ctx, List<Frontage> fronts, Vec2 a, Vec2 b, double width, int roadId, int roadIndex)
        {
            Vec2 d = b - a;
            double length = d.Length;
            if (length < 1e-6)
            {
                return;
            }

            Vec2 dir = d / length;
            foreach (int side in new[] { 1, -1 })
            {
                Vec2 n = dir.Perp * side;
                Vec2 oa = a + n * (width / 2);
                Vec2 ob = b + n * (width / 2);
                // The road side often lies exactly on the site boundary; test inside-ness a hair
                // toward the lots so a collinear edge is not lost to point-on-edge ambiguity.
                Vec2 nudge = n * 0.05;
                foreach (var (t0, t1) in Polygon.ClipSegment(ctx.Poly, oa + nudge, ob + nudge))
                {
                    double len = (t1 - t0) * length;
                    if (len < 1)
                    {
                        continue;
                    }

                    // Walk in the direction that keeps the lot side consistent for both sides.
                    fronts.Add(new Frontage { Start = Vec2.Lerp(oa, ob, t0), Dir = dir, Normal = n, Length = len, RoadId = roadId, RoadIndex = roadIndex });
                }
            }
        }

        private static IEnumerable<PaletteItem> Order(Context ctx, List<PaletteItem> candidates)
        {
            double total = candidates.Sum(c => c.Weight);
            double r = ctx.Rng.NextDouble() * total;
            PaletteItem first = candidates[candidates.Count - 1];
            foreach (var c in candidates)
            {
                r -= c.Weight;
                if (r <= 0)
                {
                    first = c;
                    break;
                }
            }

            yield return first;
            foreach (var c in candidates.Where(c => c != first).OrderByDescending(c => c.Width).ThenBy(c => c.Id, StringComparer.Ordinal))
            {
                yield return c;
            }
        }

        private static bool Free(Context ctx, Obb box)
        {
            if (!Clear(ctx.Hard, box))
            {
                return false;
            }

            double a = Math.Max(0, ctx.Options.OverlapAllowance);
            if (a <= 0)
            {
                return Clear(ctx.Obstacles, box);
            }

            // Shrink at the sides and the back only, so the front stays flush with the road.
            double width = Math.Max(1, box.Width - 2 * a);
            double depth = Math.Max(1, box.Depth - a);
            var inner = new Obb(box.Center + box.Forward * ((box.Depth - depth) / 2), box.Forward, width, depth);
            return Clear(ctx.Obstacles, inner);
        }

        private static bool Clear(List<Obb> obstacles, Obb box)
        {
            double radius = Math.Sqrt(box.Width * box.Width + box.Depth * box.Depth) / 2;
            foreach (var o in obstacles)
            {
                double other = Math.Sqrt(o.Width * o.Width + o.Depth * o.Depth) / 2;
                if (Vec2.Distance(o.Center, box.Center) >= radius + other)
                {
                    continue;
                }

                if (box.Overlaps(o))
                {
                    return false;
                }
            }

            return true;
        }

        private static void FillFrontage(Context ctx, Frontage front, List<PaletteItem> items)
        {
            double t = 0;
            while (true)
            {
                var candidates = items.Where(i => Available(ctx, i) && i.Width <= front.Length - t + 1e-6).ToList();
                if (candidates.Count == 0)
                {
                    return;
                }

                bool placed = false;
                // Fit depends only on lot size, so each size is tested once per position; large
                // palettes have hundreds of entries but only a few dozen sizes.
                var failed = new HashSet<(double, double)>();
                foreach (var item in Order(ctx, candidates))
                {
                    if (!failed.Add((item.Width, item.Depth)))
                    {
                        continue;
                    }

                    Vec2 center = front.Start + front.Dir * (t + item.Width / 2) + front.Normal * (item.Depth / 2);
                    var box = new Obb(center, -front.Normal, item.Width, item.Depth);
                    if (!Polygon.ContainsObb(ctx.Poly, box) || !Free(ctx, box))
                    {
                        continue;
                    }

                    ctx.Obstacles.Add(box);
                    ctx.Result.Placements.Add(new Placement { ItemId = item.Id, Box = box, FrontageRoadId = front.RoadId, RoadIndex = front.RoadIndex });
                    Count(ctx, item.Id);
                    // With an overlap allowance the next lot may start that far inside this one.
                    t += Math.Max(ctx.Options.Step / 2, item.Width - Math.Max(0, ctx.Options.OverlapAllowance));
                    placed = true;
                    break;
                }

                if (!placed)
                {
                    t += ctx.Options.Step;
                }
            }
        }

        private static void FillInterior(Context ctx, List<PaletteItem> items)
        {
            Vec2 u = Polygon.PrincipalAxis(ctx.Poly);
            Vec2 v = u.Perp;
            double uMin = double.MaxValue, uMax = double.MinValue, vMin = double.MaxValue, vMax = double.MinValue;
            foreach (var p in ctx.Poly)
            {
                uMin = Math.Min(uMin, Vec2.Dot(p, u));
                uMax = Math.Max(uMax, Vec2.Dot(p, u));
                vMin = Math.Min(vMin, Vec2.Dot(p, v));
                vMax = Math.Max(vMax, Vec2.Dot(p, v));
            }

            double step = Math.Max(1, ctx.Options.Step);
            for (double pv = vMin; pv <= vMax; pv += step)
            {
                for (double pu = uMin; pu <= uMax; pu += step)
                {
                    var candidates = items.Where(i => Available(ctx, i)).ToList();
                    if (candidates.Count == 0)
                    {
                        return;
                    }

                    var failed = new HashSet<(double, double)>();
                    foreach (var item in Order(ctx, candidates))
                    {
                        if (!failed.Add((item.Width, item.Depth)))
                        {
                            continue;
                        }

                        Vec2 corner = u * pu + v * pv;
                        Vec2 center = corner + u * (item.Width / 2) + v * (item.Depth / 2);
                        var box = new Obb(center, v, item.Width, item.Depth);
                        if (!Polygon.ContainsObb(ctx.Poly, box) || !Free(ctx, box))
                        {
                            continue;
                        }

                        ctx.Obstacles.Add(box);
                        ctx.Result.Placements.Add(new Placement { ItemId = item.Id, Box = box });
                        Count(ctx, item.Id);
                        break;
                    }
                }
            }
        }
    }
}
