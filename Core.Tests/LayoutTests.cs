using System;
using System.Collections.Generic;
using System.Linq;
using BlockBuilder.Core;
using Xunit;

namespace BlockBuilder.Core.Tests
{
    public class LayoutTests
    {
        private static List<Vec2> Rect(double x0, double y0, double x1, double y1) =>
            new List<Vec2> { new Vec2(x0, y0), new Vec2(x1, y0), new Vec2(x1, y1), new Vec2(x0, y1) };

        private static RoadSegment Road(double ax, double ay, double bx, double by, double width = 8, int id = 0) =>
            new RoadSegment { A = new Vec2(ax, ay), B = new Vec2(bx, by), Width = width, Id = id };

        private static PaletteItem Item(string id, int cellsW, int cellsD, double weight = 1, int max = 0, bool road = true) =>
            new PaletteItem { Id = id, Width = cellsW * 8, Depth = cellsD * 8, Weight = weight, MaxCount = max, RequiresRoad = road };

        private static void AssertValid(LayoutRequest request, LayoutResult result)
        {
            var poly = Polygon.Normalize(request.Boundary);
            var boxes = result.Placements.Select(p => p.Box).ToList();
            for (int i = 0; i < boxes.Count; i++)
            {
                Assert.True(Polygon.ContainsObb(poly, boxes[i]), $"placement {i} at {boxes[i].Center} leaves the area");
                for (int j = i + 1; j < boxes.Count; j++)
                {
                    Assert.False(boxes[i].Overlaps(boxes[j]), $"placements {i} and {j} overlap");
                }

                foreach (var road in request.ExistingRoads)
                {
                    Assert.False(boxes[i].Overlaps(Obb.FromSegment(road.A, road.B, road.Width)), $"placement {i} overlaps existing road {road.Id}");
                }

                foreach (var road in result.Roads)
                {
                    Assert.False(boxes[i].Overlaps(Obb.FromSegment(road.A, road.B, road.Width)), $"placement {i} overlaps a generated road");
                }
            }
        }

        [Fact]
        public void RowAlongRoad_IsFlushFacingRoadAtLotPitch()
        {
            // Road centre y=-4, width 8: its edge is exactly the block's bottom edge (y=0).
            var request = new LayoutRequest
            {
                Boundary = Rect(0, 0, 96, 20),
                ExistingRoads = { Road(-20, -4, 120, -4) },
                Items = { Item("house", 2, 2) },
            };

            var result = LayoutEngine.Run(request);
            AssertValid(request, result);

            var row = result.Placements.OrderBy(p => p.Box.Center.X).ToList();
            Assert.Equal(6, row.Count);
            for (int i = 0; i < row.Count; i++)
            {
                Assert.Equal(8 + 16 * i, row[i].Box.Center.X, 6);
                Assert.Equal(8, row[i].Box.Center.Y, 6);
                // Front (+Z in game) faces the road below.
                Assert.Equal(0, row[i].Box.Forward.X, 6);
                Assert.Equal(-1, row[i].Box.Forward.Y, 6);
                Assert.Equal(0, row[i].FrontageRoadId);
            }
        }

        [Fact]
        public void EdgeToEdgeLots_DoNotCountAsOverlap()
        {
            var a = new Obb(new Vec2(8, 8), new Vec2(0, -1), 16, 16);
            var b = new Obb(new Vec2(24, 8), new Vec2(0, -1), 16, 16);
            var c = new Obb(new Vec2(20, 8), new Vec2(0, -1), 16, 16);
            Assert.False(a.Overlaps(b));
            Assert.True(a.Overlaps(c));
        }

        [Fact]
        public void RoadsOnSeveralSides_NoOverlapsNoneOutside()
        {
            var request = new LayoutRequest
            {
                Boundary = Rect(0, 0, 120, 90),
                ExistingRoads =
                {
                    Road(-20, -4, 140, -4, 8, 1),
                    Road(-4, -20, -4, 110, 8, 2),
                    Road(124, -20, 124, 110, 8, 3),
                    Road(-20, 94, 140, 94, 8, 4),
                },
                Items = { Item("a", 2, 2), Item("b", 3, 2), Item("c", 2, 4), Item("d", 4, 4) },
                Options = { Seed = 7 },
            };

            var result = LayoutEngine.Run(request);
            AssertValid(request, result);
            Assert.True(result.Placements.Count >= 12, $"only {result.Placements.Count} placed: " + string.Join(" | ", result.Placements.Select(p => $"r{p.FrontageRoadId}:{p.ItemId}@{p.Box.Center}")));
            Assert.All(result.Placements, p => Assert.InRange(p.FrontageRoadId, 1, 4));
            for (int road = 1; road <= 4; road++)
            {
                Assert.Contains(result.Placements, p => p.FrontageRoadId == road);
            }
        }

        [Fact]
        public void CrossingRoad_LotsStayOffIt()
        {
            var request = new LayoutRequest
            {
                Boundary = Rect(0, 0, 120, 40),
                ExistingRoads = { Road(-20, -4, 140, -4, 8, 1), Road(60, -20, 60, 60, 8, 2) },
                Items = { Item("a", 2, 2) },
            };

            var result = LayoutEngine.Run(request);
            AssertValid(request, result);
            Assert.Contains(result.Placements, p => p.FrontageRoadId == 2);
        }

        [Fact]
        public void MaxCount_IsRespected()
        {
            var request = new LayoutRequest
            {
                Boundary = Rect(0, 0, 200, 20),
                ExistingRoads = { Road(-20, -4, 220, -4) },
                Items = { Item("rare", 2, 2, weight: 100, max: 2), Item("common", 2, 2, weight: 1) },
            };

            var result = LayoutEngine.Run(request);
            AssertValid(request, result);
            Assert.Equal(2, result.Counts["rare"]);
            Assert.True(result.Counts["common"] > 2);
        }

        [Fact]
        public void SameSeed_SameLayout_DifferentSeed_Differs()
        {
            LayoutRequest Make(int seed) => new LayoutRequest
            {
                Boundary = Rect(0, 0, 300, 20),
                ExistingRoads = { Road(-20, -4, 320, -4) },
                Items = { Item("a", 1, 2), Item("b", 2, 2), Item("c", 3, 2) },
                Options = { Seed = seed },
            };

            string Key(LayoutResult r) => string.Join(";", r.Placements.Select(p => p.ItemId + "@" + p.Box.Center));
            Assert.Equal(Key(LayoutEngine.Run(Make(3))), Key(LayoutEngine.Run(Make(3))));
            Assert.NotEqual(Key(LayoutEngine.Run(Make(3))), Key(LayoutEngine.Run(Make(4))));
        }

        [Fact]
        public void FixedItems_AreKeptAndAvoided()
        {
            var fixedBox = new Obb(new Vec2(40, 8), new Vec2(0, -1), 16, 16);
            var request = new LayoutRequest
            {
                Boundary = Rect(0, 0, 96, 20),
                ExistingRoads = { Road(-20, -4, 120, -4) },
                Items = { Item("house", 2, 2) },
                Fixed = { new Placement { ItemId = "park", Box = fixedBox } },
            };

            var result = LayoutEngine.Run(request);
            AssertValid(request, result);
            Assert.Contains(result.Placements, p => p.IsFixed && p.ItemId == "park");
            Assert.Equal(1, result.Counts["park"]);
        }

        [Fact]
        public void FixedOnly_PlacesNothingElse()
        {
            var request = new LayoutRequest
            {
                Boundary = Rect(0, 0, 96, 20),
                ExistingRoads = { Road(-20, -4, 120, -4) },
                Items = { Item("house", 2, 2) },
                Fixed = { new Placement { ItemId = "park", Box = new Obb(new Vec2(40, 8), new Vec2(0, -1), 16, 16) } },
                Options = { Mode = FillMode.FixedOnly },
            };

            var result = LayoutEngine.Run(request);
            Assert.Single(result.Placements);
        }

        [Fact]
        public void ItemsWithoutRoadRequirement_FillInterior()
        {
            var request = new LayoutRequest
            {
                Boundary = Rect(0, 0, 64, 64),
                Items = { Item("plaza", 2, 2, road: false) },
            };

            var result = LayoutEngine.Run(request);
            AssertValid(request, result);
            Assert.Equal(16, result.Placements.Count);
            Assert.All(result.Placements, p => Assert.Equal(-1, p.FrontageRoadId));
        }

        [Fact]
        public void NoFrontage_Warns()
        {
            var request = new LayoutRequest { Boundary = Rect(0, 0, 64, 64), Items = { Item("house", 2, 2) } };
            var result = LayoutEngine.Run(request);
            Assert.Empty(result.Placements);
            Assert.NotEmpty(result.Warnings);
        }

        [Fact]
        public void ConcaveArea_LotsDoNotSpanTheNotch()
        {
            // L-shaped block with the road along the bottom.
            var boundary = new List<Vec2> { new Vec2(0, 0), new Vec2(96, 0), new Vec2(96, 16), new Vec2(32, 16), new Vec2(32, 64), new Vec2(0, 64) };
            var request = new LayoutRequest
            {
                Boundary = boundary,
                ExistingRoads = { Road(-20, -4, 120, -4) },
                Items = { Item("deep", 2, 4), Item("shallow", 2, 2) },
            };

            var result = LayoutEngine.Run(request);
            AssertValid(request, result);
            Assert.Contains(result.Placements, p => p.ItemId == "deep");
            Assert.Contains(result.Placements, p => p.ItemId == "shallow" && p.Box.Center.X > 32);
        }

        [Fact]
        public void SnapToFrontage_FlushAndFacingNearestRoad()
        {
            var roads = new List<RoadSegment> { Road(-20, -4, 120, -4, 8, 1), Road(-4, -20, -4, 120, 8, 2) };

            // Above the horizontal road, far from the vertical one.
            var box = LayoutEngine.SnapToFrontage(roads, new Vec2(50, 10), 16, 24);
            Assert.Equal(50, box.Center.X, 6);
            Assert.Equal(12, box.Center.Y, 6);
            Assert.Equal(-1, box.Forward.Y, 6);

            // Right of the vertical road.
            box = LayoutEngine.SnapToFrontage(roads, new Vec2(6, 60), 16, 16);
            Assert.Equal(8, box.Center.X, 6);
            Assert.Equal(-1, box.Forward.X, 6);

            // Below the horizontal road: lot hangs downward, front faces up.
            box = LayoutEngine.SnapToFrontage(roads, new Vec2(50, -12), 16, 16);
            Assert.Equal(-16, box.Center.Y, 6);
            Assert.Equal(1, box.Forward.Y, 6);

            // Nothing within reach: centred on the point.
            box = LayoutEngine.SnapToFrontage(roads, new Vec2(500, 500), 16, 16);
            Assert.Equal(500, box.Center.X, 6);
        }

        [Fact]
        public void DesignatedItems_ThenFill_RespectsMaxCountIncludingFixed()
        {
            var roads = new List<RoadSegment> { Road(-20, -4, 220, -4) };
            var request = new LayoutRequest
            {
                Boundary = Rect(0, 0, 200, 20),
                ExistingRoads = roads,
                Items = { Item("park", 2, 2, max: 1), Item("house", 2, 2) },
                Fixed = { new Placement { ItemId = "park", Box = LayoutEngine.SnapToFrontage(roads, new Vec2(100, 5), 16, 16) } },
            };

            var result = LayoutEngine.Run(request);
            AssertValid(request, result);
            Assert.Equal(1, result.Counts["park"]);
            Assert.True(result.Counts["house"] >= 10);
        }

        [Theory]
        [InlineData(-4)] // clicked on the road centre line
        [InlineData(0)]  // exactly on the curb
        [InlineData(2)]  // 2 m inside the curb
        [InlineData(-7)] // slightly past the centre line
        public void BoundaryNearRoad_SnapsToCurb_SameRow(double bottom)
        {
            var request = new LayoutRequest
            {
                Boundary = Rect(0, bottom, 96, 20),
                ExistingRoads = { Road(-20, -4, 120, -4) },
                Items = { Item("house", 2, 2) },
            };

            var result = LayoutEngine.Run(request);
            AssertValid(new LayoutRequest { Boundary = result.Boundary, ExistingRoads = request.ExistingRoads }, result);
            var row = result.Placements.OrderBy(p => p.Box.Center.X).ToList();
            Assert.Equal(6, row.Count);
            Assert.All(row, p => Assert.Equal(8, p.Box.Center.Y, 6));
        }

        [Fact]
        public void BoundaryOnCentreLinesAtJunction_GetsCurbCorner()
        {
            var request = new LayoutRequest
            {
                Boundary = Rect(-4, -4, 100, 60),
                ExistingRoads = { Road(-20, -4, 120, -4, 8, 1), Road(-4, -20, -4, 80, 8, 2) },
                Items = { Item("house", 2, 2) },
            };

            var result = LayoutEngine.Run(request);
            Assert.Contains(result.Boundary, v => Vec2.Distance(v, new Vec2(0, 0)) < 1e-6);
            AssertValid(new LayoutRequest { Boundary = result.Boundary, ExistingRoads = request.ExistingRoads }, result);
            Assert.Contains(result.Placements, p => p.FrontageRoadId == 1);
            Assert.Contains(result.Placements, p => p.FrontageRoadId == 2);
        }

        [Fact]
        public void FarBoundaryEdges_AreNotSnapped()
        {
            var request = new LayoutRequest
            {
                Boundary = Rect(0, 0, 96, 40),
                ExistingRoads = { Road(-20, -4, 120, -4) },
                Items = { Item("house", 2, 2) },
            };

            var result = LayoutEngine.Run(request);
            Assert.Contains(result.Boundary, v => Vec2.Distance(v, new Vec2(96, 40)) < 1e-6);
            Assert.Contains(result.Boundary, v => Vec2.Distance(v, new Vec2(0, 40)) < 1e-6);
        }

        [Fact]
        public void ExistingBuildings_AreAvoided()
        {
            var existing = new Obb(new Vec2(40, 8), new Vec2(0, -1), 16, 16);
            var request = new LayoutRequest
            {
                Boundary = Rect(0, 0, 96, 20),
                ExistingRoads = { Road(-20, -4, 120, -4) },
                Items = { Item("house", 2, 2) },
                Obstacles = { existing },
            };

            var result = LayoutEngine.Run(request);
            AssertValid(request, result);
            Assert.DoesNotContain(result.Placements, p => p.Box.Overlaps(existing));
            Assert.Equal(5, result.Placements.Count);
        }

        [Theory]
        [InlineData(8.0)]
        [InlineData(2.0)] // Dense / Max density search step
        public void LargePalette_LargeArea_IsFastEnough(double step)
        {
            var rng = new Random(5);
            var request = new LayoutRequest
            {
                Boundary = Rect(0, 0, 600, 600),
                ExistingRoads =
                {
                    Road(-20, -4, 620, -4, 8, 1),
                    Road(-4, -20, -4, 620, 8, 2),
                    Road(604, -20, 604, 620, 8, 3),
                    Road(-20, 604, 620, 604, 8, 4),
                },
                Options = { GenerateRoads = true, NewRoadWidth = 16, MaxBlockLength = 160, Step = step, FillInterior = true },
            };
            for (int i = 0; i < 300; i++)
            {
                request.Items.Add(Item("p" + i, rng.Next(1, 7), rng.Next(1, 7)));
            }

            // A few no-road items so the interior pass runs too.
            for (int i = 0; i < 5; i++)
            {
                request.Items.Add(new PaletteItem { Id = "plaza" + i, Width = 16, Depth = 16, Weight = 1, RequiresRoad = false });
            }

            var watch = System.Diagnostics.Stopwatch.StartNew();
            var result = LayoutEngine.Run(request);
            watch.Stop();
            Assert.True(result.Placements.Count > 200, $"placed {result.Placements.Count}");
            Assert.True(watch.ElapsedMilliseconds < 3000, $"took {watch.ElapsedMilliseconds} ms for {result.Placements.Count} lots");
        }

        [Fact]
        public void InvalidBoundary_Warns()
        {
            var result = LayoutEngine.Run(new LayoutRequest { Boundary = { new Vec2(0, 0), new Vec2(1, 0) } });
            Assert.NotEmpty(result.Warnings);
        }
    }
}
