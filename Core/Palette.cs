using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace BlockBuilder.Core
{
    public sealed class PaletteEntry
    {
        public string Prefab = string.Empty;
        public double Weight = 1;
        /// <summary>0 means unlimited.</summary>
        public int MaxCount;
    }

    /// <summary>
    /// A named list of things to place. Stored as a small tab-separated text file so users can
    /// edit it by hand:
    /// <code>
    /// # BlockBuilder palette v1
    /// name=Old town
    /// EU_MixedOldTown01_L1_1x2	2	0
    /// </code>
    /// </summary>
    public sealed class Palette
    {
        public const string Header = "# BlockBuilder palette v1";

        public string Name = string.Empty;
        public List<PaletteEntry> Entries = new List<PaletteEntry>();

        public void Add(string prefab, double weight = 1, int maxCount = 0)
        {
            var existing = Entries.FirstOrDefault(e => e.Prefab == prefab);
            if (existing != null)
            {
                existing.Weight = weight;
                existing.MaxCount = maxCount;
                return;
            }

            Entries.Add(new PaletteEntry { Prefab = prefab, Weight = weight, MaxCount = maxCount });
        }

        public bool Remove(string prefab) => Entries.RemoveAll(e => e.Prefab == prefab) > 0;

        public string Serialize()
        {
            var sb = new StringBuilder();
            sb.Append(Header).Append('\n');
            sb.Append("name=").Append(Sanitize(Name)).Append('\n');
            foreach (var e in Entries)
            {
                sb.Append(Sanitize(e.Prefab)).Append('\t')
                  .Append(e.Weight.ToString("0.###", CultureInfo.InvariantCulture)).Append('\t')
                  .Append(e.MaxCount.ToString(CultureInfo.InvariantCulture)).Append('\n');
            }

            return sb.ToString();
        }

        private const double kMaxWeight = 1000;
        private const int kMaxCount = 100000;

        public static Palette Parse(string text, List<string> errors = null)
        {
            var palette = new Palette();
            if (string.IsNullOrEmpty(text))
            {
                return palette;
            }

            int lineNumber = 0;
            foreach (var raw in text.Split('\n'))
            {
                lineNumber++;
                string line = raw.TrimEnd('\r');
                if (line.Trim().Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
                {
                    continue;
                }

                if (line.StartsWith("name=", StringComparison.Ordinal))
                {
                    palette.Name = line.Substring(5).Trim();
                    continue;
                }

                var parts = line.Split('\t');
                string prefab = parts[0].Trim();
                if (prefab.Length == 0)
                {
                    errors?.Add($"line {lineNumber}: empty prefab name");
                    continue;
                }

                double weight = 1;
                int max = 0;
                if (parts.Length > 1 && !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out weight))
                {
                    errors?.Add($"line {lineNumber}: bad weight '{parts[1]}'");
                    weight = 1;
                }

                if (parts.Length > 2 && !int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out max))
                {
                    errors?.Add($"line {lineNumber}: bad max count '{parts[2]}'");
                    max = 0;
                }

                // Hand-edited or shared files: NaN, Infinity or absurd values would upset the picker.
                if (double.IsNaN(weight) || double.IsInfinity(weight))
                {
                    errors?.Add($"line {lineNumber}: weight is not a usable number");
                    weight = 1;
                }

                palette.Add(prefab, Math.Min(kMaxWeight, Math.Max(0, weight)), Math.Min(kMaxCount, Math.Max(0, max)));
            }

            return palette;
        }

        /// <summary>Resolves entries against the catalog; unknown prefabs are reported, not dropped silently.</summary>
        public List<PaletteItem> ToItems(Func<string, CatalogItem> lookup, List<string> missing = null)
        {
            var items = new List<PaletteItem>();
            foreach (var e in Entries)
            {
                var c = lookup(e.Prefab);
                if (c == null)
                {
                    missing?.Add(e.Prefab);
                    continue;
                }

                items.Add(new PaletteItem
                {
                    Id = c.Name,
                    Width = c.LotWidth * 8,
                    Depth = c.LotDepth * 8,
                    Weight = e.Weight,
                    MaxCount = e.MaxCount,
                    RequiresRoad = c.RequiresRoad,
                });
            }

            return items;
        }

        private static string Sanitize(string s) => (s ?? string.Empty).Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ');
    }

    public static class Polyline
    {
        /// <summary>
        /// Douglas–Peucker simplification. Returns indices of the kept points (always first and last),
        /// so callers can keep per-point data such as curve parameters.
        /// </summary>
        public static List<int> Simplify(IReadOnlyList<Vec2> points, double tolerance)
        {
            var keep = new List<int>();
            if (points.Count == 0)
            {
                return keep;
            }

            if (points.Count < 3)
            {
                for (int i = 0; i < points.Count; i++)
                {
                    keep.Add(i);
                }

                return keep;
            }

            var marked = new bool[points.Count];
            marked[0] = marked[points.Count - 1] = true;
            // List as a stack: the game's mscorlib and System both define Stack<T>.
            var stack = new List<(int First, int Last)> { (0, points.Count - 1) };
            while (stack.Count > 0)
            {
                var (first, last) = stack[stack.Count - 1];
                stack.RemoveAt(stack.Count - 1);
                double best = 0;
                int index = -1;
                for (int i = first + 1; i < last; i++)
                {
                    double d = Segment.Distance(points[first], points[last], points[i], out _);
                    if (d > best)
                    {
                        best = d;
                        index = i;
                    }
                }

                if (index >= 0 && best > tolerance)
                {
                    marked[index] = true;
                    stack.Add((first, index));
                    stack.Add((index, last));
                }
            }

            for (int i = 0; i < marked.Length; i++)
            {
                if (marked[i])
                {
                    keep.Add(i);
                }
            }

            return keep;
        }
    }
}
