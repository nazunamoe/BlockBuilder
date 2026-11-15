using System;
using System.Collections.Generic;
using System.Linq;
using BlockBuilder.Core;
using Xunit;

namespace BlockBuilder.Core.Tests
{
    public class CurvedFrontageTests
    {
        private const double R = 150;
        private const double W = 16;

        /// <summary>Quarter circle road around the origin, sampled like the game tool does.</summary>
        internal static List<RoadSegment> Arc(double radius, double width, int id, double fromDeg = 0, double toDeg = 90)
        {
            var pts = new List<Vec2>();
            double len = radius * (toDeg - fromDeg) * Math.PI / 180;
            int samples = Math.Max(2, (int)Math.Ceiling(len / 4));
            for (int i = 0; i <= samples; i++)
            {
                double a = (fromDeg + (toDeg - fromDeg) * i / samples) * Math.PI / 180;
                pts.Add(new Vec2(radius * Math.Cos(a), radius * Math.Sin(a)));
            }

            var keep = Polyline.Simplify(pts, 0.1);
            var segs = new List<RoadSegment>();
            for (int k = 0; k + 1 < keep.Count; k++)
            {
                segs.Add(new RoadSegment { A = pts[keep[k]], B = pts[keep[k + 1]], Width = width, Id = id });
            }

            return segs;
        }

        /// <summary>Annulus sector between two radii (counter-clockwise).</summary>
        private static List<Vec2> Sector(double r0, double r1, double fromDeg = 2, double toDeg = 88)
        {
            var ring = new List<Vec2>();
            for (double d = fromDeg; d <= toDeg + 1e-9; d += 2)
            {
                ring.Add(new Vec2(r1 * Math.Cos(d * Math.PI / 180), r1 * Math.Sin(d * Math.PI / 180)));
            }

            for (double d = toDeg; d >= fromDeg - 1e-9; d -= 2)
            {
                ring.Add(new Vec2(r0 * Math.Cos(d * Math.PI / 180), r0 * Math.Sin(d * Math.PI / 180)));
            }

            return ring;
        }

        private static void AssertClearOfRoads(LayoutResult result, List<RoadSegment> roads)
        {
            foreach (var p in result.Placements)
            {
                foreach (var s in roads)
                {
                    Assert.False(p.Box.Overlaps(Obb.FromSegment(s.A, s.B, s.Width)), $"lot at {p.Box.Center} overlaps the road");
                }
            }

            for (int i = 0; i < result.Placements.Count; i++)
            {
                for (int j = i + 1; j < result.Placements.Count; j++)
                {
                    Assert.False(result.Placements[i].Box.Overlaps(result.Placements[j].Box));
                }
            }
        }

        [Fact]
        public void OutsideOfBend_LotsFollowTheCurve()
        {
            var roads = Arc(R, W, 1);
            Assert.True(roads.Count > 5, "the curve is many short segments");
            var request = new LayoutRequest
            {
                Boundary = Sector(R + W / 2, R + W / 2 + 40),
                ExistingRoads = roads,
                Items = { new PaletteItem { Id = "house", Width = 16, Depth = 16, Weight = 1, RequiresRoad = true } },
            };

            var result = LayoutEngine.Run(request);
            AssertClearOfRoads(result, roads);

            // Curb arc inside the sector: radius 158 over 86 degrees ~ 237 m, so about 14 lots of 16 m.
            Assert.True(result.Placements.Count >= 12, $"placed {result.Placements.Count}");
            foreach (var p in result.Placements)
            {
                // Each lot faces the road (forward points back toward the centre of the arc) and its
                // front is close to the curb, within reach of the road.
                Vec2 toCentre = (-p.Box.Center).Normalized;
                Assert.True(Vec2.Dot(p.Box.Forward, toCentre) > 0.97);
                double front = p.Box.Center.Length - p.Box.Depth / 2;
                Assert.InRange(front, R + W / 2 - 0.01, R + W / 2 + 1.5);
            }
        }

        [Fact]
        public void InsideOfBend_LotsFollowTheCurve()
        {
            var roads = Arc(R, W, 1);
            var request = new LayoutRequest
            {
                Boundary = Sector(R - W / 2 - 40, R - W / 2),
                ExistingRoads = roads,
                Items = { new PaletteItem { Id = "house", Width = 16, Depth = 16, Weight = 1, RequiresRoad = true } },
            };

            var result = LayoutEngine.Run(request);
            AssertClearOfRoads(result, roads);
            // Curb arc 142 m radius over 86 degrees ~ 213 m; lots fan in, so fewer than 13 fit.
            Assert.True(result.Placements.Count >= 9, $"placed {result.Placements.Count}");
            foreach (var p in result.Placements)
            {
                Vec2 outward = p.Box.Center.Normalized;
                Assert.True(Vec2.Dot(p.Box.Forward, outward) > 0.97);
                double front = p.Box.Center.Length + p.Box.Depth / 2;
                Assert.InRange(front, R - W / 2 - 1.5, R - W / 2 + 0.01);
            }
        }

        [Fact]
        public void CornerOfTwoRoads_IsNotJoined()
        {
            // Two roads meeting at a right angle at their ends: lots must not be laid diagonally
            // across the corner.
            var roads = new List<RoadSegment>
            {
                new RoadSegment { A = new Vec2(-8, -8), B = new Vec2(120, -8), Width = 16, Id = 1 },
                new RoadSegment { A = new Vec2(-8, -8), B = new Vec2(-8, 120), Width = 16, Id = 2 },
            };
            var request = new LayoutRequest
            {
                Boundary = new List<Vec2> { new Vec2(0, 0), new Vec2(112, 0), new Vec2(112, 112), new Vec2(0, 112) },
                ExistingRoads = roads,
                Items = { new PaletteItem { Id = "house", Width = 16, Depth = 16, Weight = 1, RequiresRoad = true } },
            };

            var result = LayoutEngine.Run(request);
            AssertClearOfRoads(result, roads);
            foreach (var p in result.Placements)
            {
                bool axisAligned = Math.Abs(p.Box.Forward.X) > 0.999 || Math.Abs(p.Box.Forward.Y) > 0.999;
                Assert.True(axisAligned, $"lot at {p.Box.Center} faces {p.Box.Forward}");
            }
        }
    }

    public class BlockPickerTests
    {
        private static GraphEdge E(long id, long a, long b, double width, params (double X, double Y)[] pts) =>
            new GraphEdge { Id = id, Start = a, End = b, Width = width, Points = pts.Select(p => new Vec2(p.X, p.Y)).ToList() };

        /// <summary>A 100 x 100 block of 16 m roads with corner nodes 1..4.</summary>
        private static List<GraphEdge> Square() => new List<GraphEdge>
        {
            E(10, 1, 2, 16, (0, 0), (100, 0)),
            E(11, 2, 3, 16, (100, 0), (100, 100)),
            E(12, 3, 4, 16, (100, 100), (0, 100)),
            E(13, 4, 1, 16, (0, 100), (0, 0)),
        };

        private static void AssertSquareRing(List<Vec2> ring, double minX, double minY, double maxX, double maxY)
        {
            Assert.Equal(4, ring.Count);
            Assert.All(ring, p => Assert.True(Math.Abs(p.X - minX) < 1e-6 || Math.Abs(p.X - maxX) < 1e-6));
            Assert.All(ring, p => Assert.True(Math.Abs(p.Y - minY) < 1e-6 || Math.Abs(p.Y - maxY) < 1e-6));
            Assert.True(Polygon.SignedArea(ring) > 0);
        }

        [Fact]
        public void Square_RingIsTheCurbLine()
        {
            var pick = BlockPicker.Pick(Square(), new Vec2(50, 50));
            Assert.Equal(BlockPickStatus.Ok, pick.Status);
            AssertSquareRing(pick.Ring, 8, 8, 92, 92);
            Assert.Equal(4, pick.EdgeIds.Count);
        }

        [Fact]
        public void TwoBlocks_PicksTheOneClicked()
        {
            var edges = Square();
            edges.AddRange(new[]
            {
                E(20, 2, 5, 16, (100, 0), (200, 0)),
                E(21, 5, 6, 16, (200, 0), (200, 100)),
                E(22, 6, 3, 16, (200, 100), (100, 100)),
            });

            var left = BlockPicker.Pick(edges, new Vec2(50, 50));
            AssertSquareRing(left.Ring, 8, 8, 92, 92);
            var right = BlockPicker.Pick(edges, new Vec2(150, 50));
            Assert.Equal(BlockPickStatus.Ok, right.Status);
            AssertSquareRing(right.Ring, 108, 8, 192, 92);
        }

        [Fact]
        public void DeadEndSpur_IsIgnored()
        {
            var edges = Square();
            edges.Add(E(30, 1, 7, 12, (0, 0), (40, 40))); // cul-de-sac into the block
            var pick = BlockPicker.Pick(edges, new Vec2(70, 30));
            Assert.Equal(BlockPickStatus.Ok, pick.Status);
            AssertSquareRing(pick.Ring, 8, 8, 92, 92);
        }

        [Fact]
        public void DegreeTwoNodeMidSide_GivesStraightSide()
        {
            var edges = new List<GraphEdge>
            {
                E(10, 1, 9, 16, (0, 0), (40, 0)),
                E(14, 9, 2, 16, (40, 0), (100, 0)),
                E(11, 2, 3, 16, (100, 0), (100, 100)),
                E(12, 3, 4, 16, (100, 100), (0, 100)),
                E(13, 4, 1, 16, (0, 100), (0, 0)),
            };
            var pick = BlockPicker.Pick(edges, new Vec2(50, 50));
            AssertSquareRing(pick.Ring, 8, 8, 92, 92);
        }

        [Fact]
        public void WidthChangeMidSide_StaysValid()
        {
            var edges = new List<GraphEdge>
            {
                E(10, 1, 9, 16, (0, 0), (40, 0)),
                E(14, 9, 2, 24, (40, 0), (100, 0)), // wider road on the second half
                E(11, 2, 3, 16, (100, 0), (100, 100)),
                E(12, 3, 4, 16, (100, 100), (0, 100)),
                E(13, 4, 1, 16, (0, 100), (0, 0)),
            };
            var pick = BlockPicker.Pick(edges, new Vec2(50, 50));
            Assert.Equal(BlockPickStatus.Ok, pick.Status);
            Assert.True(Polygon.SignedArea(pick.Ring) > 0);
            Assert.Contains(pick.Ring, p => Math.Abs(p.Y - 8) < 1e-6);
            Assert.Contains(pick.Ring, p => Math.Abs(p.Y - 12) < 1e-6);
            Assert.True(Polygon.Contains(pick.Ring, new Vec2(50, 50)));
        }

        [Fact]
        public void CurvedSide_RingFollowsTheCurb()
        {
            // Bottom side bows outward as a polyline arc through (50, -20).
            var arc = new List<(double, double)>();
            for (int i = 0; i <= 20; i++)
            {
                double x = i * 5;
                arc.Add((x, -20 * Math.Sin(Math.PI * x / 100)));
            }

            var edges = Square();
            edges[0] = E(10, 1, 2, 16, arc.ToArray());
            var pick = BlockPicker.Pick(edges, new Vec2(50, 50));
            Assert.Equal(BlockPickStatus.Ok, pick.Status);
            Assert.True(pick.Ring.Count > 10, "the curved side keeps its vertices");
            // Middle of the curved curb: 8 m inside the centre line's lowest point.
            double lowest = pick.Ring.Min(p => p.Y);
            Assert.InRange(lowest, -20 + 8 - 0.5, -20 + 8 + 0.5);
        }

        [Fact]
        public void ClickOutsideEveryBlock_ReportsOutside()
        {
            var pick = BlockPicker.Pick(Square(), new Vec2(500, 500));
            Assert.Equal(BlockPickStatus.Outside, pick.Status);
            Assert.Empty(pick.Ring);
        }

        [Fact]
        public void NoRoads_ReportsNoRoads()
        {
            Assert.Equal(BlockPickStatus.NoRoads, BlockPicker.Pick(new List<GraphEdge>(), new Vec2(0, 0)).Status);
        }

        [Fact]
        public void PickedBlock_WithSplitSides_GeneratesConnectedStreets()
        {
            // 240 x 120 block whose long sides are split by degree-2 nodes (like real road edges).
            var edges = new List<GraphEdge>
            {
                E(1, 1, 5, 16, (0, 0), (120, 0)),
                E(2, 5, 2, 16, (120, 0), (240, 0)),
                E(3, 2, 3, 16, (240, 0), (240, 120)),
                E(4, 3, 6, 16, (240, 120), (120, 120)),
                E(5, 6, 4, 16, (120, 120), (0, 120)),
                E(6, 4, 1, 16, (0, 120), (0, 0)),
            };
            var pick = BlockPicker.Pick(edges, new Vec2(100, 60));
            Assert.Equal(BlockPickStatus.Ok, pick.Status);
            Assert.Equal(4, pick.Ring.Count); // collinear split points merged

            var roads = edges.SelectMany(e => Enumerable.Range(0, e.Points.Count - 1).Select(k => new RoadSegment { A = e.Points[k], B = e.Points[k + 1], Width = e.Width, Id = (int)e.Id })).ToList();
            var request = new LayoutRequest
            {
                Boundary = pick.Ring,
                ExistingRoads = roads,
                Items = { new PaletteItem { Id = "house", Width = 16, Depth = 16, Weight = 1, RequiresRoad = true } },
                Options = new LayoutOptions { SnapBoundaryToCurbs = false, GenerateRoads = true, NewRoadWidth = 16 },
            };

            var result = LayoutEngine.Run(request);
            Assert.NotEmpty(result.Roads);
            Assert.All(result.Roads, r => Assert.True(r.Connected));
            // Streets run along the long side.
            Assert.All(result.Roads.Where(r => Vec2.Distance(r.A, r.B) > 50), r => Assert.True(Math.Abs(r.A.Y - r.B.Y) < 1e-6));
        }

        /// <summary>100 m wide block whose bottom road is a circular arc of radius 60 bowing outward.</summary>
        private static List<GraphEdge> ArcBlock()
        {
            // Centre (50, 33.17): the arc from (0, 0) to (100, 0) dips to y = -26.8.
            double cy = Math.Sqrt(60 * 60 - 50 * 50);
            double a0 = Math.Atan2(0 - cy, 0 - 50);
            double a1 = Math.Atan2(0 - cy, 100 - 50);
            var pts = new List<Vec2>();
            int samples = 30;
            for (int i = 0; i <= samples; i++)
            {
                double a = a0 + (a1 - a0) * i / samples;
                pts.Add(new Vec2(50 + 60 * Math.Cos(a), cy + 60 * Math.Sin(a)));
            }

            var keep = Polyline.Simplify(pts, 0.1);
            var edges = Square();
            edges[0] = new GraphEdge { Id = 10, Start = 1, End = 2, Width = 16, Points = keep.Select(i => pts[i]).ToList() };
            return edges;
        }

        [Theory]
        [InlineData(16.0, 3)]
        [InlineData(24.0, 2)]
        [InlineData(32.0, 1)]
        public void PickedCurvedBlock_LotsAlongTheCurveStayInsideAndClearOfRoads(double lot, int minOnCurve)
        {
            var edges = ArcBlock();
            var pick = BlockPicker.Pick(edges, new Vec2(50, 40));
            Assert.Equal(BlockPickStatus.Ok, pick.Status);
            var roads = edges.SelectMany(e => Enumerable.Range(0, e.Points.Count - 1).Select(k => new RoadSegment { A = e.Points[k], B = e.Points[k + 1], Width = e.Width, Id = (int)e.Id })).ToList();
            var request = new LayoutRequest
            {
                Boundary = pick.Ring,
                ExistingRoads = roads,
                Items = { new PaletteItem { Id = "lot", Width = lot, Depth = 16, Weight = 1, RequiresRoad = true } },
                Options = new LayoutOptions { SnapBoundaryToCurbs = false },
            };

            var result = LayoutEngine.Run(request);
            var onCurve = result.Placements.Where(p => p.FrontageRoadId == 10).ToList();
            Assert.True(onCurve.Count >= minOnCurve, $"{onCurve.Count} lots on the curved side");
            foreach (var p in result.Placements)
            {
                // Strictly inside: every corner inside the curb ring (no 5 cm tolerance).
                Assert.All(p.Box.Corners(), c => Assert.True(Polygon.Contains(pick.Ring, c) || Polygon.DistanceToBoundary(pick.Ring, c) < 0.001, $"corner {c} of the {lot} m lot at {p.Box.Center} is outside the curb"));
                foreach (var s in roads)
                {
                    Assert.False(p.Box.Overlaps(Obb.FromSegment(s.A, s.B, s.Width), 0.001), "lot overlaps a road");
                }
            }
        }

        [Fact]
        public void Brush_TakesTouchedBlocksOnly()
        {
            // Two blocks side by side sharing the road at x = 100 (curbs at x = 92 and x = 108).
            var edges = Square();
            edges.AddRange(new[]
            {
                E(20, 2, 5, 16, (100, 0), (200, 0)),
                E(21, 5, 6, 16, (200, 0), (200, 100)),
                E(22, 6, 3, 16, (200, 100), (100, 100)),
            });
            var graph = new BlockGraph(edges);

            // Inside the left block, far from the shared road: only the left block.
            var one = graph.PickAll(new Vec2(40, 50), 10);
            Assert.Single(one);
            Assert.True(Polygon.Contains(one[0].Ring, new Vec2(40, 50)));

            // On the shared road's centre line with a small brush: reaches neither curb (8 m away).
            Assert.Empty(graph.PickAll(new Vec2(100, 50), 6));

            // A bigger brush on the shared road reaches both curbs.
            var both = graph.PickAll(new Vec2(100, 50), 10);
            Assert.Equal(2, both.Count);
            Assert.NotEqual(BlockGraph.KeyOf(both[0]), BlockGraph.KeyOf(both[1]));

            // Outside every block.
            Assert.Empty(graph.PickAll(new Vec2(500, 500), 20));
        }

        [Fact]
        public void LargeGraph_PickIsFast()
        {
            // 40 x 40 grid of 100 m blocks: about 3,300 road edges.
            var edges = new List<GraphEdge>();
            long Node(int x, int y) => x * 1000 + y;
            int id = 0;
            for (int x = 0; x <= 40; x++)
            {
                for (int y = 0; y <= 40; y++)
                {
                    if (x < 40)
                    {
                        edges.Add(E(id++, Node(x, y), Node(x + 1, y), 16, (x * 100, y * 100), (x * 100 + 100, y * 100)));
                    }

                    if (y < 40)
                    {
                        edges.Add(E(id++, Node(x, y), Node(x, y + 1), 16, (x * 100, y * 100), (x * 100, y * 100 + 100)));
                    }
                }
            }

            var watch = System.Diagnostics.Stopwatch.StartNew();
            var graph = new BlockGraph(edges);
            long build = watch.ElapsedMilliseconds;
            for (int i = 0; i < 200; i++)
            {
                var pick = graph.Pick(new Vec2(50 + (i % 40) * 100, 50 + (i / 40) * 100));
                Assert.Equal(BlockPickStatus.Ok, pick.Status);
            }

            watch.Stop();
            Assert.Equal(1600, graph.FaceCount);
            Assert.True(build < 1000, $"build {build} ms");
            Assert.True(watch.ElapsedMilliseconds < 1500, $"build + 200 picks {watch.ElapsedMilliseconds} ms");
        }

        [Fact]
        public void PickedBlock_FillsWithoutTouchingRoads()
        {
            var edges = Square();
            var pick = BlockPicker.Pick(edges, new Vec2(50, 50));
            var roads = edges.SelectMany(e => Enumerable.Range(0, e.Points.Count - 1).Select(k => new RoadSegment { A = e.Points[k], B = e.Points[k + 1], Width = e.Width, Id = (int)e.Id })).ToList();
            var request = new LayoutRequest
            {
                Boundary = pick.Ring,
                ExistingRoads = roads,
                Items = { new PaletteItem { Id = "house", Width = 16, Depth = 16, Weight = 1, RequiresRoad = true } },
                Options = new LayoutOptions { SnapBoundaryToCurbs = false },
            };

            var result = LayoutEngine.Run(request);
            Assert.True(result.Placements.Count >= 16, $"placed {result.Placements.Count}");
            foreach (var p in result.Placements)
            {
                Assert.True(Polygon.ContainsObb(pick.Ring, p.Box));
            }
        }
    }
}
