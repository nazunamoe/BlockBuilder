using System;
using System.Collections.Generic;
using System.Linq;
using BlockBuilder.Core;
using Xunit;

namespace BlockBuilder.Core.Tests
{
    public class PaintAreaTests
    {
        private static readonly PaletteItem House = new PaletteItem { Id = "house", Width = 16, Depth = 16, Weight = 1, RequiresRoad = true };

        private static List<RoadSegment> Straight(Vec2 a, Vec2 b, double width = 16, int id = 1, double step = 20)
        {
            var segs = new List<RoadSegment>();
            int n = Math.Max(1, (int)Math.Ceiling(Vec2.Distance(a, b) / step));
            for (int k = 0; k < n; k++)
            {
                segs.Add(new RoadSegment { A = Vec2.Lerp(a, b, (double)k / n), B = Vec2.Lerp(a, b, (double)(k + 1) / n), Width = width, Id = id });
            }

            return segs;
        }

        private static LayoutResult Fill(PaintRegion region, List<RoadSegment> roads, PaletteItem item = null) => LayoutEngine.Run(new LayoutRequest
        {
            Boundary = region.Ring,
            ExistingRoads = roads,
            Items = { item ?? House },
            Options = new LayoutOptions { SnapBoundaryToCurbs = false },
        });

        private static void AssertClear(LayoutResult result, List<RoadSegment> roads, PaintArea paint)
        {
            foreach (var p in result.Placements)
            {
                foreach (var s in roads)
                {
                    Assert.False(p.Box.Overlaps(Obb.FromSegment(s.A, s.B, s.Width), 0.001), $"lot at {p.Box.Center} overlaps a road");
                }
            }
        }

        /// <summary>The invariant that makes lots appear: curb next to paint lies inside the engine ring.</summary>
        private static void AssertCurbInside(PaintArea paint, PaintRegion region, List<RoadSegment> roads)
        {
            int checkedPoints = 0;
            foreach (var s in roads)
            {
                Vec2 d = (s.B - s.A).Normalized;
                foreach (int side in new[] { 1, -1 })
                {
                    for (double t = 0; t <= 1; t += 0.1)
                    {
                        Vec2 curb = Vec2.Lerp(s.A, s.B, t) + d.Perp * side * (s.Width / 2);
                        // Only curb points inside a painted stretch (paint beside them and 4 m either
                        // way along the road); at the very end of the paint the ring crosses the curb.
                        Vec2 off = curb + d.Perp * side * 1.5;
                        if (!paint.IsPainted(off) || !paint.IsPainted(off + d * 4) || !paint.IsPainted(off - d * 4))
                        {
                            continue;
                        }

                        checkedPoints++;
                        Assert.True(Polygon.Contains(region.Ring, curb), $"curb point {curb} next to paint is outside the engine ring");
                    }
                }
            }

            Assert.True(checkedPoints > 0, "no curb next to paint was checked");
        }

        [Fact]
        public void BrushWiderThanRoad_DoesNotSpillAcross()
        {
            // The road runs past the 300 m reach, so the far side cannot be reached around its ends.
            var roads = Straight(new Vec2(-500, 0), new Vec2(600, 0));
            var paint = new PaintArea(roads);
            Assert.True(paint.BeginStroke(new Vec2(50, 20)));
            paint.Stroke(new Vec2(0, 20), new Vec2(100, 20), 40, erase: false);

            Assert.False(paint.IsPainted(new Vec2(50, 0)), "the road is not painted");
            Assert.False(paint.IsPainted(new Vec2(50, -20)), "the far side of the road is not painted");
            Assert.True(paint.IsPainted(new Vec2(50, 12)));
            Assert.Single(paint.Regions());
        }

        [Fact]
        public void StrokeStartingOnARoad_PaintsNothing()
        {
            var roads = Straight(new Vec2(-100, 0), new Vec2(200, 0));
            var paint = new PaintArea(roads);
            Assert.False(paint.BeginStroke(new Vec2(50, 0)));
            paint.Stroke(new Vec2(0, 0), new Vec2(100, 0), 40, erase: false);
            Assert.True(paint.IsEmpty);
        }

        [Fact]
        public void PaintBesideStraightRoad_LotsLineTheRoad()
        {
            var roads = Straight(new Vec2(-100, 0), new Vec2(200, 0));
            var paint = new PaintArea(roads);
            paint.BeginStroke(new Vec2(50, 25));
            paint.Stroke(new Vec2(10, 25), new Vec2(90, 25), 18, erase: false);
            var region = paint.Regions().Single();

            AssertCurbInside(paint, region, roads);
            var result = Fill(region, roads);
            AssertClear(result, roads, paint);
            Assert.True(result.Placements.Count(p => p.FrontageRoadId == 1) >= 4, $"{result.Placements.Count} lots");
            // The shown outline leaves the road out; the engine ring reaches into it.
            Assert.True(region.Outline.Min(v => v.Y) >= 6, "outline stays off the road");
            Assert.True(region.Ring.Min(v => v.Y) < 8, "engine ring reaches past the curb");
        }

        [Fact]
        public void BrushAcrossRoad_CrossRoadsOnPaintsBothSidesButNeverTheRoad()
        {
            var roads = Straight(new Vec2(-900, 0), new Vec2(900, 0));
            var walled = new PaintArea(roads);
            walled.BeginStroke(new Vec2(50, 25));
            walled.Stroke(new Vec2(50, 25), new Vec2(50, -25), 18, erase: false);
            Assert.False(walled.IsPainted(new Vec2(50, -25)));

            var open = new PaintArea(roads);
            Assert.True(open.BeginStroke(new Vec2(50, 25), 300, crossRoads: true));
            open.Stroke(new Vec2(50, 25), new Vec2(50, -25), 18, erase: false);
            Assert.True(open.IsPainted(new Vec2(50, 25)));
            Assert.True(open.IsPainted(new Vec2(50, -25)));
            Assert.False(open.IsPainted(new Vec2(50, 0)));
        }

        [Fact]
        public void PaintLimit_StopsAddingCellsAndErasingStillWorks()
        {
            var paint = new PaintArea(Straight(new Vec2(-900, 0), new Vec2(900, 0)));
            paint.MaxCells = 100;
            paint.BeginStroke(new Vec2(50, 100));
            paint.Stroke(new Vec2(0, 100), new Vec2(200, 100), 18, erase: false);
            Assert.True(paint.AtLimit);
            Assert.Equal(100, paint.Cells.Count());
            Assert.True(paint.PaintedArea <= 100 * 4 + 1e-6);
            paint.Stroke(new Vec2(0, 100), new Vec2(40, 100), 18, erase: true);
            Assert.False(paint.AtLimit);
        }

        [Fact]
        public void PaintBesideDiagonalRoad_LotsLineTheRoad()
        {
            Vec2 dir = new Vec2(Math.Cos(Math.PI / 6), Math.Sin(Math.PI / 6));
            var roads = Straight(dir * -200, dir * 300);
            var paint = new PaintArea(roads);
            Vec2 side = dir.Perp * 25;
            paint.BeginStroke(dir * 60 + side);
            paint.Stroke(dir * 10 + side, dir * 110 + side, 18, erase: false);
            var region = paint.Regions().Single();

            AssertCurbInside(paint, region, roads);
            var result = Fill(region, roads);
            AssertClear(result, roads, paint);
            Assert.True(result.Placements.Count >= 4, $"{result.Placements.Count} lots");
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void PaintBesideArc_LotsFollowTheCurve(bool outside)
        {
            var roads = CurvedFrontageTests.Arc(60, 16, 1, 0, 180);
            var paint = new PaintArea(roads);
            double r = outside ? 60 + 8 + 14 : 60 - 8 - 14;
            Vec2 At(double deg) => new Vec2(r * Math.Cos(deg * Math.PI / 180), r * Math.Sin(deg * Math.PI / 180));
            Assert.True(paint.BeginStroke(At(90)));
            for (int d = 20; d < 160; d += 5)
            {
                paint.Stroke(At(d), At(d + 5), 14, erase: false);
            }

            var region = paint.Regions().OrderByDescending(x => x.Area).First();
            AssertCurbInside(paint, region, roads);
            var result = Fill(region, roads);
            AssertClear(result, roads, paint);
            Assert.True(result.Placements.Count >= (outside ? 6 : 3), $"{result.Placements.Count} lots");
        }

        [Fact]
        public void ShownOutline_HugsTheCurbExactly()
        {
            var roads = Straight(new Vec2(-500, 0), new Vec2(600, 0));
            var paint = new PaintArea(roads);
            paint.BeginStroke(new Vec2(50, 25));
            paint.Stroke(new Vec2(0, 25), new Vec2(120, 25), 20, erase: false);
            paint.EndStroke();
            var region = paint.Regions().Single();

            var onCurb = region.Outline.Where(p => Math.Abs(p.Y - 8) < 0.01).Select(p => p.X).ToList();
            Assert.True(onCurb.Count >= 2 && onCurb.Max() - onCurb.Min() > 100, $"curb frontage {(onCurb.Count > 1 ? onCurb.Max() - onCurb.Min() : 0):0.0} m");
            Assert.True(region.Outline.All(p => p.Y >= 8 - 0.01), "the shown outline never enters the road");
        }

        [Fact]
        public void SmallGapToTheCurb_IsClosed()
        {
            var roads = Straight(new Vec2(-500, 0), new Vec2(600, 0));
            var paint = new PaintArea(roads);
            paint.BeginStroke(new Vec2(50, 32));
            // The brush stops about 4 m short of the curb at y = 8.
            paint.Stroke(new Vec2(0, 32), new Vec2(120, 32), 20, erase: false);
            Assert.False(paint.IsPainted(new Vec2(60, 10)));
            Assert.True(paint.EndStroke() > 0);
            Assert.True(paint.IsPainted(new Vec2(60, 9)), "the gap up to the curb is painted");

            var region = paint.Regions().Single();
            AssertCurbInside(paint, region, roads);
            var result = Fill(region, roads);
            AssertClear(result, roads, paint);
            Assert.True(result.Placements.Count(p => p.FrontageRoadId == 1) >= 5, $"{result.Placements.Count} lots");
        }

        [Fact]
        public void GapFurtherThanSnap_IsLeftAlone()
        {
            var roads = Straight(new Vec2(-500, 0), new Vec2(600, 0));
            var paint = new PaintArea(roads);
            paint.BeginStroke(new Vec2(50, 40));
            // Brush edge at y = 17: 9 m short of the curb, beyond the 6 m snap.
            paint.Stroke(new Vec2(0, 40), new Vec2(120, 40), 23, erase: false);
            Assert.Equal(0, paint.EndStroke());
            Assert.False(paint.IsPainted(new Vec2(60, 10)));
        }

        [Fact]
        public void GapClosing_OnlyMovesTowardTheRoad()
        {
            var roads = Straight(new Vec2(-500, 0), new Vec2(600, 0));
            var paint = new PaintArea(roads);
            paint.BeginStroke(new Vec2(50, 30));
            paint.Stroke(new Vec2(40, 30), new Vec2(60, 30), 18, erase: false); // reaches y = 12
            paint.EndStroke();
            // Closed straight down to the curb, not spread sideways along the road.
            Assert.True(paint.IsPainted(new Vec2(50, 9)));
            Assert.False(paint.IsPainted(new Vec2(80, 9)), "no spread along the road");
            Assert.False(paint.IsPainted(new Vec2(20, 9)), "no spread along the road");
        }

        [Fact]
        public void ErasedBandByTheCurb_StaysErasedAfterAStrokeElsewhere()
        {
            var roads = Straight(new Vec2(-500, 0), new Vec2(600, 0));
            var paint = new PaintArea(roads);
            paint.BeginStroke(new Vec2(50, 25));
            paint.Stroke(new Vec2(0, 25), new Vec2(120, 25), 20, erase: false);
            paint.EndStroke();
            Assert.True(paint.IsPainted(new Vec2(60, 9)));

            // Erase a band along the curb, then paint far from the road.
            paint.Stroke(new Vec2(0, 10), new Vec2(120, 10), 3, erase: true);
            Assert.False(paint.IsPainted(new Vec2(60, 9)));
            paint.BeginStroke(new Vec2(60, 40));
            paint.Stamp(new Vec2(60, 40), 6, erase: false);
            paint.EndStroke();
            Assert.False(paint.IsPainted(new Vec2(60, 9)), "the erased band is not refilled");
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void SnapLot_OnACurve_StaysClearOfTheRoad(bool outside)
        {
            var roads = CurvedFrontageTests.Arc(60, 16, 1, 0, 180);
            double r = outside ? 60 + 8 + 10 : 60 - 8 - 10;
            foreach (double deg in new[] { 30.0, 90.0, 140.0 })
            {
                var at = new Vec2(r * Math.Cos(deg * Math.PI / 180), r * Math.Sin(deg * Math.PI / 180));
                Assert.True(LayoutEngine.TrySnapLot(roads, at, 24, 16, out var box));
                foreach (var s in roads)
                {
                    Assert.False(box.Overlaps(Obb.FromSegment(s.A, s.B, s.Width), 0.001), $"lot snapped at {deg} deg overlaps the road");
                }

                // Faces the road and its front is close to the curb.
                double front = outside ? box.Center.Length - box.Depth / 2 : box.Center.Length + box.Depth / 2;
                Assert.InRange(Math.Abs(front - (outside ? 68 : 52)), 0, 2.5);
            }
        }

        [Fact]
        public void TinyBrushClick_StillMakesAVisibleRegion()
        {
            var paint = new PaintArea(new List<RoadSegment>());
            paint.BeginStroke(new Vec2(50, 50));
            paint.Stamp(new Vec2(50, 50), 4, erase: false); // the smallest brush, one click
            Assert.Single(paint.Regions());
        }

        [Fact]
        public void FreeEdges_KeepTheBrushSize()
        {
            var roads = Straight(new Vec2(-500, 0), new Vec2(600, 0));
            var paint = new PaintArea(roads);
            paint.BeginStroke(new Vec2(100, 100));
            paint.Stamp(new Vec2(100, 100), 30, erase: false);
            Assert.Equal(0, paint.EndStroke()); // far from every curb: nothing added
            var region = paint.Regions().Single();
            double circle = Math.PI * 30 * 30;
            Assert.InRange(Polygon.SignedArea(region.Outline), circle * 0.9, circle * 1.08);
        }

        [Fact]
        public void LargePaint_IsFastEnough()
        {
            // A 400 x 300 m area inside a grid of roads, painted with many strokes.
            var roads = new List<RoadSegment>();
            for (int k = -1; k <= 4; k++)
            {
                roads.AddRange(Straight(new Vec2(-200, k * 100), new Vec2(600, k * 100), 16, 10 + k));
                roads.AddRange(Straight(new Vec2(k * 100, -200), new Vec2(k * 100, 500), 16, 20 + k));
            }

            var paint = new PaintArea(roads);
            var watch = System.Diagnostics.Stopwatch.StartNew();
            for (int bx = 0; bx < 4; bx++)
            {
                for (int by = 0; by < 3; by++)
                {
                    Vec2 c = new Vec2(bx * 100 + 50, by * 100 + 50);
                    Assert.True(paint.BeginStroke(c));
                    paint.Stroke(c + new Vec2(-30, -20), c + new Vec2(30, 20), 30, erase: false);
                    paint.EndStroke();
                }
            }

            long strokes = watch.ElapsedMilliseconds;
            var regions = paint.Regions();
            watch.Stop();
            Assert.Equal(12, regions.Count); // one per block: roads keep them apart
            Assert.True(strokes < 2000, $"strokes {strokes} ms");
            Assert.True(watch.ElapsedMilliseconds - strokes < 1000, $"regions {watch.ElapsedMilliseconds - strokes} ms");
        }

        [Fact]
        public void EnclosedPocket_IsFilled()
        {
            var paint = new PaintArea(new List<RoadSegment>());
            // A ring of cells around an unpainted 3 x 3 pocket.
            for (int i = 0; i < 7; i++)
            {
                for (int j = 0; j < 7; j++)
                {
                    if (i < 2 || i > 4 || j < 2 || j > 4)
                    {
                        paint.PaintCell(i, j);
                    }
                }
            }

            var region = paint.Regions(minArea: 1).Single();
            Assert.Equal(4, region.Outline.Count);
            Assert.True(Polygon.Contains(region.Ring, new Vec2(7, 7)), "the pocket is inside");
        }

        [Fact]
        public void DiagonalPinch_GivesTwoSimpleRegions()
        {
            var paint = new PaintArea(new List<RoadSegment>());
            for (int i = 0; i < 3; i++)
            {
                for (int j = 0; j < 3; j++)
                {
                    paint.PaintCell(i, j);           // block A
                    paint.PaintCell(i + 3, j + 3);   // block B touching A only at a corner
                }
            }

            var regions = paint.Regions(minArea: 1);
            Assert.Equal(2, regions.Count);
            Assert.All(regions, r => Assert.Equal(4, r.Outline.Count));
        }

        [Fact]
        public void Erase_SplitsAndShrinks()
        {
            var paint = new PaintArea(new List<RoadSegment>());
            paint.BeginStroke(new Vec2(50, 0));
            paint.Stroke(new Vec2(0, 0), new Vec2(100, 0), 10, erase: false);
            int before = paint.Count;
            paint.Stroke(new Vec2(50, -20), new Vec2(50, 20), 6, erase: true);
            Assert.True(paint.Count < before);
            Assert.Equal(2, paint.Regions().Count);
        }

        [Fact]
        public void PaintedRegion_CanGenerateStreets()
        {
            // A 300 x 200 painted area with a road along its bottom: streets may be generated; report
            // what happens rather than require a layout (RoadGenerator keys off a long straight edge).
            var roads = Straight(new Vec2(-50, 0), new Vec2(350, 0));
            var paint = new PaintArea(roads);
            paint.BeginStroke(new Vec2(150, 100));
            for (int y = 20; y <= 190; y += 20)
            {
                paint.Stroke(new Vec2(10, y), new Vec2(290, y), 14, erase: false);
            }

            var region = paint.Regions().Single();
            var result = LayoutEngine.Run(new LayoutRequest
            {
                Boundary = region.Ring,
                ExistingRoads = roads,
                Items = { House },
                Options = new LayoutOptions { SnapBoundaryToCurbs = false, GenerateRoads = true, NewRoadWidth = 16 },
            });

            Assert.NotEmpty(result.Placements);
            AssertClear(result, roads.Concat(result.Roads.Select(r => new RoadSegment { A = r.A, B = r.B, Width = r.Width })).ToList(), paint);
        }
    }
}
