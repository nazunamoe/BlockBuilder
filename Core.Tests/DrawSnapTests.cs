using System.Linq;
using System.Collections.Generic;
using BlockBuilder.Core;
using Xunit;

namespace BlockBuilder.Core.Tests
{
    public class OverlapAllowanceTests
    {
        private static LayoutRequest Row(double length, double allowance) => new LayoutRequest
        {
            Boundary = new List<Vec2> { new Vec2(0, 0), new Vec2(length, 0), new Vec2(length, 40), new Vec2(0, 40) },
            ExistingRoads = { new RoadSegment { A = new Vec2(-20, -8), B = new Vec2(length + 20, -8), Width = 16, Id = 1 } },
            Items = { new PaletteItem { Id = "house", Width = 24, Depth = 24, Weight = 1, RequiresRoad = true } },
            Options = new LayoutOptions { OverlapAllowance = allowance },
        };

        [Fact]
        public void Zero_KeepsLotsApart()
        {
            var result = LayoutEngine.Run(Row(104, 0));
            Assert.Equal(4, result.Placements.Count);
            for (int i = 0; i < result.Placements.Count; i++)
            {
                for (int j = i + 1; j < result.Placements.Count; j++)
                {
                    Assert.False(result.Placements[i].Box.Overlaps(result.Placements[j].Box));
                }
            }
        }

        [Fact]
        public void Allowance_PacksTighterWithBoundedOverlap()
        {
            var result = LayoutEngine.Run(Row(104, 4));
            Assert.Equal(5, result.Placements.Count);
            var xs = result.Placements.Select(p => p.Box.Center.X).OrderBy(x => x).ToList();
            for (int i = 0; i + 1 < xs.Count; i++)
            {
                // Neighbours overlap by at most the allowance.
                Assert.True(24 - (xs[i + 1] - xs[i]) <= 4 + 1e-6);
            }

            // Fronts stay flush with the curb and lots stay inside the area.
            Assert.All(result.Placements, p => Assert.Equal(12, p.Box.Center.Y, 6));
        }

        [Fact]
        public void Allowance_NeverOverlapsRoads()
        {
            var request = Row(104, 6);
            // A road crossing the area: lots must keep clear of it in full.
            request.ExistingRoads.Add(new RoadSegment { A = new Vec2(52, -20), B = new Vec2(52, 60), Width = 12, Id = 2 });
            var result = LayoutEngine.Run(request);
            var road = Obb.FromSegment(new Vec2(52, -20), new Vec2(52, 60), 12);
            Assert.All(result.Placements, p => Assert.False(p.Box.Overlaps(road)));
        }
    }

    public class DrawSnapTests
    {
        private static RoadSegment Road(double ax, double ay, double bx, double by, double width = 16, int id = 1) =>
            new RoadSegment { A = new Vec2(ax, ay), B = new Vec2(bx, by), Width = width, Id = id };

        private static readonly List<Vec2> NoCorners = new List<Vec2>();

        [Fact]
        public void NearCurb_SnapsOntoCurbOnCursorSide()
        {
            var roads = new[] { Road(-100, 0, 100, 0) };
            var r = DrawSnap.Snap(new Vec2(10, 11), NoCorners, roads);
            Assert.Equal(SnapKind.Curb, r.Kind);
            Assert.Equal(10, r.Point.X, 6);
            Assert.Equal(8, r.Point.Y, 6);

            r = DrawSnap.Snap(new Vec2(10, -5), NoCorners, roads);
            Assert.Equal(-8, r.Point.Y, 6);
        }

        [Fact]
        public void FarFromRoads_NoSnap()
        {
            var r = DrawSnap.Snap(new Vec2(10, 40), NoCorners, new[] { Road(-100, 0, 100, 0) });
            Assert.Equal(SnapKind.None, r.Kind);
            Assert.Equal(40, r.Point.Y, 6);
        }

        [Fact]
        public void Junction_SnapsToCurbCorner()
        {
            var roads = new[] { Road(-100, 0, 100, 0, 16, 1), Road(0, -100, 0, 100, 12, 2) };
            var r = DrawSnap.Snap(new Vec2(9, 11), NoCorners, roads);
            Assert.Equal(SnapKind.CurbCorner, r.Kind);
            Assert.Equal(6, r.Point.X, 6);
            Assert.Equal(8, r.Point.Y, 6);
        }

        [Fact]
        public void AwayFromCurbs_EdgeSnapsPerpendicularToRoad()
        {
            var roads = new[] { Road(-100, 0, 100, 0) };
            var corners = new List<Vec2> { new Vec2(20, 8) };
            // Almost straight up from the corner: snapped to exactly perpendicular.
            var r = DrawSnap.Snap(new Vec2(22, 60), corners, roads);
            Assert.Equal(SnapKind.Angle, r.Kind);
            Assert.Equal(20, r.Point.X, 6);
            Assert.Equal(60, r.Point.Y, 6);
        }

        [Fact]
        public void AngleAndCurb_Combine()
        {
            // Block between two parallel roads; the second corner goes straight across to the far curb.
            var roads = new[] { Road(-100, 0, 100, 0, 16, 1), Road(-100, 80, 100, 80, 16, 2) };
            var corners = new List<Vec2> { new Vec2(20, 8) };
            var r = DrawSnap.Snap(new Vec2(23, 75), corners, roads);
            Assert.Equal(SnapKind.AngleOnCurb, r.Kind);
            Assert.Equal(20, r.Point.X, 6);
            Assert.Equal(72, r.Point.Y, 6);
        }

        [Fact]
        public void RightAngleToPreviousEdge()
        {
            var corners = new List<Vec2> { new Vec2(0, 0), new Vec2(100, 30) };
            // Previous edge direction (100, 30); perpendicular is (-30, 100).
            var r = DrawSnap.Snap(new Vec2(100 - 30 * 0.5 + 1, 30 + 100 * 0.5), corners, new RoadSegment[0]);
            Assert.Equal(SnapKind.Angle, r.Kind);
            var edge = r.Point - corners[1];
            Assert.Equal(0, Vec2.Dot(edge, new Vec2(100, 30)), 6);
        }

        [Fact]
        public void Options_TurnSnapsOff()
        {
            var roads = new[] { Road(-100, 0, 100, 0) };
            var corners = new List<Vec2> { new Vec2(20, 8) };
            Assert.Equal(SnapKind.None, DrawSnap.Snap(new Vec2(10, 11), new List<Vec2>(), roads, curbs: false).Kind);
            Assert.Equal(SnapKind.None, DrawSnap.Snap(new Vec2(22, 60), corners, roads, angles: false).Kind);
            Assert.Equal(SnapKind.Angle, DrawSnap.Snap(new Vec2(22, 60), corners, roads, curbs: false).Kind);
        }

        [Fact]
        public void CellLength_RoundsFreeEdgesOnly()
        {
            var roads = new[] { Road(-100, 0, 100, 0) };
            var corners = new List<Vec2> { new Vec2(20, 8) };
            // Perpendicular edge of 51 m becomes 48 m (6 cells of 8 m).
            var r = DrawSnap.Snap(new Vec2(21, 59), corners, roads, cellLength: 8);
            Assert.Equal(SnapKind.Angle, r.Kind);
            Assert.Equal(48, r.CellLength, 6);
            Assert.Equal(56, r.Point.Y, 6);

            // A curb snap keeps the curb position.
            r = DrawSnap.Snap(new Vec2(53, 11), corners, roads, cellLength: 8);
            Assert.Equal(SnapKind.Curb, r.Kind);
            Assert.Equal(53, r.Point.X, 6);
        }

        [Fact]
        public void LargeAngleOff_NoAngleSnap()
        {
            var corners = new List<Vec2> { new Vec2(20, 30) };
            var r = DrawSnap.Snap(new Vec2(60, 70), corners, new[] { Road(-100, 0, 100, 0) });
            Assert.Equal(SnapKind.None, r.Kind);
        }
    }
}
