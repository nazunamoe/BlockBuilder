using System;
using System.Collections.Generic;

namespace BlockBuilder.Core
{
    public sealed class TreeOptions
    {
        /// <summary>Grid pitch in metres: about one tree per pitch × pitch of free ground.</summary>
        public double Spacing = 8;
        /// <summary>Metres kept free between a tree and any lot, road, building or the area edge.</summary>
        public double Clearance = 2;
        /// <summary>Share of the free spots left empty, in clumps (0 = every spot, 1 = none).</summary>
        public double Thinning = 0.25;
        public int Seed = 1;
        /// <summary>
        /// Most trees returned. When more spots qualify, the trees kept are an even sample of the
        /// whole area (by hash rank), so raising the cap or changing a nearby lot can change which
        /// trees survive even far away.
        /// </summary>
        public int MaxTrees = 400;
        /// <summary>Number of species to mix; each tree gets an index below this.</summary>
        public int Species = 1;
    }

    public struct TreeSpot
    {
        public Vec2 Position;
        /// <summary>Index into the chosen species.</summary>
        public int Species;
        /// <summary>0 sapling, 1 young, 2 mature, 3 elderly.</summary>
        public int Age;
        /// <summary>Rotation around the vertical axis, radians.</summary>
        public double Yaw;
    }

    /// <summary>
    /// Trees for the ground a layout leaves empty: single trees on a jittered grid, thinned into
    /// loose clumps (the "lawn scatter" behaviour of park tools). Everything about a spot comes
    /// from a hash of its grid cell and the seed, and the grid is anchored to the world, so the same
    /// input gives the same trees and changing one lot only changes the trees next to it. The one
    /// exception is the <see cref="TreeOptions.MaxTrees"/> cap: once it binds, the kept trees are a
    /// global sample, so a local change can shift which trees survive anywhere in the area.
    /// </summary>
    public static class TreeScatter
    {
        public static List<TreeSpot> Plan(IReadOnlyList<IReadOnlyList<Vec2>> rings, IReadOnlyList<Obb> blocked, TreeOptions options)
        {
            var trees = new List<TreeSpot>();
            var ranks = new List<uint>();
            options = options ?? new TreeOptions();
            double pitch = Math.Max(2, options.Spacing);
            double clear = Math.Max(0, options.Clearance);
            int species = Math.Max(1, options.Species);
            if (rings == null || options.MaxTrees <= 0)
            {
                return trees;
            }

            // Blocked boxes grown by the clearance, with a bounding radius for a quick reject.
            var boxes = new List<(Obb Box, double Radius)>();
            foreach (var b in blocked ?? Array.Empty<Obb>())
            {
                var grown = new Obb(b.Center, b.Forward, b.Width + 2 * clear, b.Depth + 2 * clear);
                boxes.Add((grown, Math.Sqrt(grown.Width * grown.Width + grown.Depth * grown.Depth) / 2));
            }

            foreach (var ring in rings)
            {
                if (ring == null || ring.Count < 3)
                {
                    continue;
                }

                double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
                foreach (var p in ring)
                {
                    x0 = Math.Min(x0, p.X);
                    y0 = Math.Min(y0, p.Y);
                    x1 = Math.Max(x1, p.X);
                    y1 = Math.Max(y1, p.Y);
                }

                int i0 = (int)Math.Floor(x0 / pitch), i1 = (int)Math.Floor(x1 / pitch);
                int j0 = (int)Math.Floor(y0 / pitch), j1 = (int)Math.Floor(y1 / pitch);
                for (int j = j0; j <= j1; j++)
                {
                    for (int i = i0; i <= i1; i++)
                    {
                        uint h = Hash(i, j, options.Seed);
                        // Clumps: neighbouring cells share a coarse noise value, so gaps and groups form.
                        double noise = 0.6 * Unit(Hash(Div(i, 3), Div(j, 3), options.Seed + 17)) + 0.4 * Unit(Hash(i, j, options.Seed + 31));
                        if (Uniform(noise) < options.Thinning)
                        {
                            continue;
                        }

                        // Jitter within the middle of the cell keeps neighbours at least pitch * 0.3 apart.
                        double jx = 0.15 + 0.7 * Unit(h);
                        double jy = 0.15 + 0.7 * Unit(h * 2654435761u + 1);
                        var at = new Vec2((i + jx) * pitch, (j + jy) * pitch);
                        if (!Polygon.Contains(ring, at) || Polygon.DistanceToBoundary(ring, at) < clear || !Free(boxes, at))
                        {
                            continue;
                        }

                        uint a = h * 2246822519u + 7;
                        double age = Unit(a);
                        ranks.Add(h * 374761393u + 5);
                        trees.Add(new TreeSpot
                        {
                            Position = at,
                            Species = (int)((h * 3266489917u + 3) % (uint)species),
                            // Mostly mature, some young, a few saplings and old trees.
                            Age = age < 0.08 ? 0 : age < 0.28 ? 1 : age < 0.92 ? 2 : 3,
                            Yaw = Unit(a * 668265263u + 11) * Math.PI * 2,
                        });
                    }
                }
            }

            if (trees.Count > options.MaxTrees)
            {
                // Over the cap: keep an even sample of the whole area, not just the first rows.
                var order = new int[trees.Count];
                for (int k = 0; k < order.Length; k++)
                {
                    order[k] = k;
                }

                Array.Sort(order, (a, b) => ranks[a] != ranks[b] ? ranks[a].CompareTo(ranks[b]) : a.CompareTo(b));
                var keep = new bool[trees.Count];
                for (int k = 0; k < options.MaxTrees; k++)
                {
                    keep[order[k]] = true;
                }

                var kept = new List<TreeSpot>(options.MaxTrees);
                for (int k = 0; k < trees.Count; k++)
                {
                    if (keep[k])
                    {
                        kept.Add(trees[k]);
                    }
                }

                return kept;
            }

            return trees;
        }

        /// <summary>
        /// The clump noise is 0.6·U + 0.4·V (a trapezoid, not uniform); this maps it to its rank in
        /// 0..1, so a thinning of t really leaves out the share t of the spots.
        /// </summary>
        private static double Uniform(double x)
        {
            if (x <= 0.4)
            {
                return x * x / 0.48;
            }

            return x <= 0.6 ? (x - 0.2) / 0.6 : 1 - (1 - x) * (1 - x) / 0.48;
        }

        private static bool Free(List<(Obb Box, double Radius)> boxes, Vec2 p)
        {
            foreach (var (box, radius) in boxes)
            {
                Vec2 d = p - box.Center;
                if (Math.Abs(d.X) > radius || Math.Abs(d.Y) > radius)
                {
                    continue;
                }

                if (Math.Abs(Vec2.Dot(d, box.Forward)) <= box.Depth / 2 && Math.Abs(Vec2.Dot(d, box.Right)) <= box.Width / 2)
                {
                    return false;
                }
            }

            return true;
        }

        private static int Div(int a, int b) => (int)Math.Floor((double)a / b);

        private static double Unit(uint h) => (h >> 8) / 16777216.0;

        /// <summary>Same value on every run and runtime.</summary>
        private static uint Hash(int i, int j, int seed)
        {
            unchecked
            {
                uint h = 2166136261u;
                h = (h ^ (uint)i) * 16777619u;
                h = (h ^ (uint)j) * 16777619u;
                h = (h ^ (uint)seed) * 16777619u;
                h ^= h >> 15;
                h *= 2246822519u;
                h ^= h >> 13;
                return h;
            }
        }
    }
}
