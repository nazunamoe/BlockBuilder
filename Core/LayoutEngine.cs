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
        /// <summary>
        /// How far a lot may poke past the area or into a neighbour and still count as touching.
        /// Only float noise: a few centimetres into a road is an overlap error in the game, which
        /// blocks the whole build.
        /// </summary>
        private const double kFit = 0.01;

        private sealed class Context
        {
            public List<Vec2> Poly;
            public LayoutOptions Options;
            public Random Rng;
            /// <summary>Items placed last, newest at the end; sized orders avoid repeating them.</summary>
            public readonly List<string> Recent = new List<string>();
            /// <summary>Lots and buildings: may be overlapped by the overlap allowances.</summary>
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
            int variant = Math.Max(0, options.Variant);
            result.Variant = variant;
            var ctx = new Context { Poly = poly, Options = options, Rng = new Random(options.Seed + (variant >> 2) * 104729), Result = result };
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

            var fronts = Frontage.Build(request.ExistingRoads, result.Roads, ctx.Poly);

            if (roadItems.Count > 0)
            {
                double minWidth = roadItems.Min(i => i.Width);
                var usable = fronts.Where(f => f.Length + 1e-6 >= minWidth);
                // Long frontages first by default; variants let the short ones take the corners.
                var ordered = (variant & 2) != 0 ? usable.OrderBy(f => f.Length) : usable.OrderByDescending(f => f.Length);
                foreach (var front in ordered.ToList())
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
        /// Box for a lot the user places or drags to <paramref name="point"/>: against the nearest
        /// curb within <paramref name="maxDistance"/>, facing the road, fitted to curves the same way
        /// as the fill. False when no curb is that close.
        /// </summary>
        public static bool TrySnapLot(IReadOnlyList<RoadSegment> roads, Vec2 point, double width, double depth, out Obb box, double maxDistance = 48) =>
            Frontage.TrySnapLot(roads, point, width, depth, maxDistance, out box);

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

        private const int kRecentKept = 12;

        /// <summary>
        /// Unused frontage left at the end of a run of a given length when it is filled with the
        /// available widths (unbounded knapsack on a 0.5 m grid). Mid-row obstacles are not known
        /// here; the live fit check still decides every lot.
        /// </summary>
        private sealed class RowFit
        {
            private const double kRes = 0.5;
            private readonly double[] m_Waste;
            private readonly double m_Side;
            private readonly double m_Gap;

            public RowFit(List<PaletteItem> items, double length, LayoutOptions options)
            {
                m_Side = Math.Max(0, options.OverlapSide);
                m_Gap = Math.Max(0, options.Gap);
                int n = Math.Max(0, (int)Math.Floor(length / kRes)) + 1;
                // The last lot needs its width; every lot before it uses its pitch (width - overlap + gap).
                var pitches = items.Select(i => Math.Max(kRes, i.Width - m_Side + m_Gap)).Select(p => (int)Math.Round(p / kRes)).Where(p => p > 0).Distinct().ToArray();
                var widths = items.Select(i => (int)Math.Ceiling(i.Width / kRes - 1e-6)).Distinct().ToArray();
                var best = new int[n];
                m_Waste = new double[n];
                for (int len = 0; len < n; len++)
                {
                    int b = len;
                    foreach (int w in widths)
                    {
                        if (w <= len && len - w < b)
                        {
                            b = len - w;
                        }
                    }

                    foreach (int p in pitches)
                    {
                        if (p <= len && best[len - p] < b)
                        {
                            b = best[len - p];
                        }
                    }

                    best[len] = b;
                    m_Waste[len] = b * kRes;
                }
            }

            /// <summary>Road left unused if this item goes here with <paramref name="left"/> metres to go.</summary>
            public double WasteAfter(double left, PaletteItem item)
            {
                double after = left - Math.Max(kRes, item.Width - m_Side + m_Gap);
                if (after < 0)
                {
                    // The item ends the run: what is left past it.
                    return Math.Round(Math.Max(0, left - item.Width) / kRes) * kRes;
                }

                int k = Math.Min(m_Waste.Length - 1, (int)Math.Floor(after / kRes));
                return m_Waste[k];
            }
        }

        /// <summary>
        /// Lays the request out in <paramref name="variants"/> variants (variant 0 is the plain fill,
        /// so the winner is never worse than it) and keeps the highest <see cref="Score"/>.
        /// Deterministic: the same request always gives the same winner.
        /// </summary>
        public static LayoutResult RunBest(LayoutRequest request, int variants)
        {
            var baseOptions = request.Options ?? new LayoutOptions();
            LayoutResult best = null;
            double bestScore = double.NegativeInfinity;
            for (int v = 0; v < Math.Max(1, variants); v++)
            {
                var options = baseOptions.Clone();
                options.Variant = v;
                var trial = new LayoutRequest
                {
                    Boundary = request.Boundary,
                    ExistingRoads = request.ExistingRoads,
                    Obstacles = request.Obstacles,
                    Items = request.Items,
                    Fixed = request.Fixed.Select(f => new Placement { ItemId = f.ItemId, Box = f.Box }).ToList(),
                    Options = options,
                };
                var result = Run(trial);
                double score = Score(result);
                if (score > bestScore + 1e-6)
                {
                    best = result;
                    bestScore = score;
                }
            }

            return best;
        }

        /// <summary>
        /// Higher is better: lot area filled (least empty space first), minus a little for each pair
        /// of identical buildings side by side on the same road.
        /// </summary>
        public static double Score(LayoutResult result)
        {
            double area = 0;
            int repeats = 0;
            Placement prev = null;
            foreach (var p in result.Placements)
            {
                if (p.IsFixed)
                {
                    continue;
                }

                area += p.Box.Width * p.Box.Depth;
                if (prev != null && prev.ItemId == p.ItemId && prev.FrontageRoadId == p.FrontageRoadId && Vec2.Distance(prev.Box.Center, p.Box.Center) < (prev.Box.Width + p.Box.Width) * 0.75)
                {
                    repeats++;
                }

                prev = p;
            }

            return area - repeats * 8;
        }

        private static double SizeOf(PaletteItem i, SizeKey key) => key == SizeKey.Width ? i.Width : key == SizeKey.Depth ? i.Depth : i.Width * i.Depth;

        private static void Count(Context ctx, string id)
        {
            ctx.Counts.TryGetValue(id, out int n);
            ctx.Counts[id] = n + 1;
            ctx.Recent.Add(id);
            if (ctx.Recent.Count > kRecentKept)
            {
                ctx.Recent.RemoveAt(0);
            }
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

        private static IEnumerable<PaletteItem> Order(Context ctx, List<PaletteItem> candidates)
        {
            if (ctx.Options.Order != SizeOrder.Mixed)
            {
                foreach (var c in SizedOrder(ctx, candidates))
                {
                    yield return c;
                }

                yield break;
            }

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

        /// <summary>
        /// Biggest or smallest first by the chosen size. Items used recently go to the back, so the
        /// next lot is the next best size instead of the same building again (they still serve as the
        /// fallback when nothing else fits).
        /// </summary>
        /// <summary>Same value on every run and runtime, so a seed always lays out the same.</summary>
        private static uint StableHash(string id, int seed)
        {
            uint h = 2166136261u ^ (uint)seed;
            foreach (char ch in id)
            {
                h = (h ^ ch) * 16777619u;
            }

            return h;
        }

        private static IEnumerable<PaletteItem> SizedOrder(Context ctx, List<PaletteItem> candidates)
        {
            var key = ctx.Options.SizeBy;
            bool large = ctx.Options.Order == SizeOrder.LargeFirst;
            int seed = ctx.Options.Seed;
            var ordered = (large ? candidates.OrderByDescending(c => SizeOf(c, key)) : candidates.OrderBy(c => SizeOf(c, key)))
                .ThenBy(c => StableHash(c.Id, seed))
                .ThenBy(c => c.Id, StringComparer.Ordinal)
                .ToList();
            // The newest uses weigh most: an item used just now goes behind one used a while ago.
            foreach (var c in ordered.Where(c => !ctx.Recent.Contains(c.Id)))
            {
                yield return c;
            }

            foreach (var c in ordered.Where(c => ctx.Recent.Contains(c.Id)).OrderBy(c => ctx.Recent.LastIndexOf(c.Id)))
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

            double side = Math.Max(0, ctx.Options.OverlapSide);
            double back = Math.Max(0, ctx.Options.OverlapBack);
            if (side <= 0 && back <= 0)
            {
                return Clear(ctx.Obstacles, box);
            }

            // Shrink at the sides and the back only, so the front stays flush with the road.
            double width = Math.Max(1, box.Width - 2 * side);
            double depth = Math.Max(1, box.Depth - back);
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

                if (box.Overlaps(o, kFit))
                {
                    return false;
                }
            }

            return true;
        }

        private static void FillFrontage(Context ctx, Frontage front, List<PaletteItem> items)
        {
            bool reverse = (Math.Max(0, ctx.Options.Variant) & 1) != 0;
            var fit = ctx.Options.FitRows ? new RowFit(items, front.Length, ctx.Options) : null;
            double t = 0;
            // Right after a lot, search finely: on the inside of a bend the next lot only fits a
            // little further on (the backs converge), and a full step would waste most of a lot.
            double lastEnd = double.NegativeInfinity;
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
                var order = Order(ctx, candidates);
                if (fit != null)
                {
                    // Stable sort: within the same leftover the size order and the no-repeat rule decide.
                    double left = front.Length - t;
                    order = order.ToList().OrderBy(i => fit.WasteAfter(left, i));
                }

                foreach (var item in order)
                {
                    if (!failed.Add((item.Width, item.Depth)))
                    {
                        continue;
                    }

                    // Reversed variants walk the road from its other end (same rules, other start).
                    var box = reverse ? front.LotAt(front.Length - t - item.Width, item.Width, item.Depth) : front.LotAt(t, item.Width, item.Depth);
                    if (!Polygon.ContainsObb(ctx.Poly, box, kFit) || !Free(ctx, box))
                    {
                        continue;
                    }

                    ctx.Obstacles.Add(box);
                    ctx.Result.Placements.Add(new Placement { ItemId = item.Id, Box = box, FrontageRoadId = front.RoadId, RoadIndex = front.RoadIndex });
                    Count(ctx, item.Id);
                    // With an overlap allowance the next lot may start that far inside this one.
                    t += Math.Max(ctx.Options.Step / 2, item.Width - Math.Max(0, ctx.Options.OverlapSide) + Math.Max(0, ctx.Options.Gap));
                    lastEnd = t;
                    placed = true;
                    break;
                }

                if (!placed)
                {
                    t += t - lastEnd < ctx.Options.Step ? Math.Min(1, ctx.Options.Step) : ctx.Options.Step;
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

            // The interior grid has its own step: a fine frontage step (Dense) would make it 16x denser.
            double step = Math.Max(1, ctx.Options.InteriorStep);
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
                        if (!Polygon.ContainsObb(ctx.Poly, box, kFit) || !Free(ctx, box))
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
