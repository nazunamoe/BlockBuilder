using System;
using System.Collections.Generic;
using System.Linq;
using BlockBuilder.Core;
using Xunit;

namespace BlockBuilder.Core.Tests
{
    public class TreeScatterTests
    {
        private static List<Vec2> Rect(double x0, double y0, double x1, double y1) =>
            new List<Vec2> { new Vec2(x0, y0), new Vec2(x1, y0), new Vec2(x1, y1), new Vec2(x0, y1) };

        private static List<IReadOnlyList<Vec2>> Rings(params List<Vec2>[] rings) => rings.Cast<IReadOnlyList<Vec2>>().ToList();

        private static bool Inside(Obb box, Vec2 p, double grow) =>
            Math.Abs(Vec2.Dot(p - box.Center, box.Forward)) < box.Depth / 2 + grow - 1e-6 &&
            Math.Abs(Vec2.Dot(p - box.Center, box.Right)) < box.Width / 2 + grow - 1e-6;

        [Fact]
        public void Trees_StayInsideTheAreaAndClearOfLotsAndRoads()
        {
            var ring = Rect(0, 0, 200, 120);
            var blocked = new List<Obb>
            {
                new Obb(new Vec2(40, 20), new Vec2(0, -1), 32, 24),
                new Obb(new Vec2(120, 60), new Vec2(1, 1), 40, 16),
                Obb.FromSegment(new Vec2(-20, 100), new Vec2(220, 100), 16),
            };
            var options = new TreeOptions { Spacing = 8, Clearance = 3, Thinning = 0, MaxTrees = 5000, Species = 3 };
            var trees = TreeScatter.Plan(Rings(ring), blocked, options);

            Assert.True(trees.Count > 50, $"{trees.Count} trees");
            foreach (var t in trees)
            {
                Assert.True(Polygon.Contains(ring, t.Position));
                Assert.True(Polygon.DistanceToBoundary(ring, t.Position) >= 3 - 1e-6);
                Assert.DoesNotContain(blocked, b => Inside(b, t.Position, 3));
                Assert.InRange(t.Species, 0, 2);
                Assert.InRange(t.Age, 0, 3);
            }

            Assert.True(trees.Select(t => t.Species).Distinct().Count() == 3);
        }

        [Fact]
        public void Trees_KeepApartAndRepeatForTheSameSeed()
        {
            var ring = Rect(0, 0, 160, 160);
            var options = new TreeOptions { Spacing = 10, Thinning = 0.25, MaxTrees = 5000 };
            var a = TreeScatter.Plan(Rings(ring), new List<Obb>(), options);
            var b = TreeScatter.Plan(Rings(ring), new List<Obb>(), options);
            Assert.Equal(a.Select(t => t.Position), b.Select(t => t.Position));

            for (int i = 0; i < a.Count; i++)
            {
                for (int j = i + 1; j < a.Count; j++)
                {
                    Assert.True(Vec2.Distance(a[i].Position, a[j].Position) >= 10 * 0.3 - 1e-6);
                }
            }

            // Thinning leaves gaps: clearly fewer trees than grid cells, clearly more than none.
            Assert.InRange(a.Count, 100, 240);
            var other = TreeScatter.Plan(Rings(ring), new List<Obb>(), new TreeOptions { Spacing = 10, Thinning = 0.25, MaxTrees = 5000, Seed = 2 });
            Assert.NotEqual(a.Select(t => t.Position), other.Select(t => t.Position));
        }

        [Fact]
        public void Trees_RespectTheCap()
        {
            var trees = TreeScatter.Plan(Rings(Rect(0, 0, 400, 400)), new List<Obb>(), new TreeOptions { Spacing = 4, Thinning = 0, MaxTrees = 150 });
            Assert.Equal(150, trees.Count);
        }

        [Fact]
        public void Density_PlantsAboutTheAskedShare()
        {
            var ring = Rect(0, 0, 400, 400);
            int Count(double thinning) => TreeScatter.Plan(Rings(ring), new List<Obb>(), new TreeOptions { Spacing = 8, Clearance = 0, Thinning = thinning, MaxTrees = 100000 }).Count;
            int all = Count(0);
            Assert.InRange(Count(0.9) / (double)all, 0.06, 0.14);
            Assert.InRange(Count(0.5) / (double)all, 0.44, 0.56);
            Assert.InRange(Count(0.2) / (double)all, 0.74, 0.86);
        }

        [Fact]
        public void Cap_KeepsTreesSpreadOverTheWholeArea()
        {
            var trees = TreeScatter.Plan(Rings(Rect(0, 0, 400, 400)), new List<Obb>(), new TreeOptions { Spacing = 8, Thinning = 0, MaxTrees = 200 });
            Assert.Equal(200, trees.Count);
            Assert.True(trees.Max(t => t.Position.Y) > 300, "trees reach the far side");
            Assert.True(trees.Min(t => t.Position.Y) < 100);
        }

        [Fact]
        public void MovingOneLot_LeavesDistantTreesUnchanged()
        {
            var ring = Rect(0, 0, 240, 120);
            var options = new TreeOptions { Spacing = 8, Clearance = 2, Thinning = 0.2, MaxTrees = 5000 };
            var before = TreeScatter.Plan(Rings(ring), new List<Obb> { new Obb(new Vec2(30, 30), new Vec2(0, -1), 24, 24) }, options);
            var after = TreeScatter.Plan(Rings(ring), new List<Obb> { new Obb(new Vec2(46, 30), new Vec2(0, -1), 24, 24) }, options);

            var far = before.Where(t => t.Position.X > 120).Select(t => t.Position).ToList();
            Assert.NotEmpty(far);
            Assert.Equal(far, after.Where(t => t.Position.X > 120).Select(t => t.Position).ToList());
            Assert.NotEqual(before.Select(t => t.Position), after.Select(t => t.Position));
        }
    }
}
