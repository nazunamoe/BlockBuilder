using System.Collections.Generic;
using System.Linq;
using BlockBuilder.Core;
using Xunit;

namespace BlockBuilder.Core.Tests
{
    public class RoadGenerationTests
    {
        private static List<Vec2> Rect(double x0, double y0, double x1, double y1) =>
            new List<Vec2> { new Vec2(x0, y0), new Vec2(x1, y0), new Vec2(x1, y1), new Vec2(x0, y1) };

        private static RoadSegment Road(double ax, double ay, double bx, double by, int id) =>
            new RoadSegment { A = new Vec2(ax, ay), B = new Vec2(bx, by), Width = 8, Id = id, CurveT0 = 0, CurveT1 = 1 };

        private static LayoutRequest Block(bool sideRoads)
        {
            // 160 x 120 block, road along the bottom (its edge at y=0), optionally on the left and right.
            var request = new LayoutRequest
            {
                Boundary = Rect(0, 0, 160, 120),
                ExistingRoads = { Road(-20, -4, 180, -4, 1) },
                Items = { new PaletteItem { Id = "house", Width = 16, Depth = 16 } },
                Options = { GenerateRoads = true, NewRoadWidth = 8, MaxBlockLength = 1000 },
            };
            if (sideRoads)
            {
                request.ExistingRoads.Add(Road(-4, -20, -4, 140, 2));
                request.ExistingRoads.Add(Road(164, -20, 164, 140, 3));
            }

            return request;
        }

        [Fact]
        public void Streets_AreSpacedForTwoRowsBackToBack()
        {
            var result = LayoutEngine.Run(Block(true));
            var ys = result.Roads.Select(r => r.A.Y).Distinct().OrderBy(y => y).ToList();
            Assert.NotEmpty(ys);
            // Bottom road edge at 0; two 16 m rows; then the street centre half a width further.
            Assert.Equal(36, ys[0], 6);
            for (int i = 1; i < ys.Count; i++)
            {
                Assert.Equal(40, ys[i] - ys[i - 1], 6);
            }

            Assert.All(result.Roads, r => Assert.Equal(r.A.Y, r.B.Y, 6));
        }

        [Fact]
        public void StreetEnds_SnapOntoSideRoads_AndAreConnected()
        {
            var result = LayoutEngine.Run(Block(true));
            Assert.NotEmpty(result.Roads);
            foreach (var road in result.Roads)
            {
                Assert.Equal(JoinKind.Existing, road.StartJoin.Kind);
                Assert.Equal(JoinKind.Existing, road.EndJoin.Kind);
                Assert.Contains(new[] { road.A.X, road.B.X }, x => System.Math.Abs(x + 4) < 1e-6);
                Assert.Contains(new[] { road.A.X, road.B.X }, x => System.Math.Abs(x - 164) < 1e-6);
                Assert.True(road.Connected);
            }

            Assert.DoesNotContain(result.Warnings, w => w.Contains("do not reach"));
        }

        [Fact]
        public void WithoutSideRoads_StreetsAreFreeEnded_AndWarned()
        {
            var result = LayoutEngine.Run(Block(false));
            Assert.NotEmpty(result.Roads);
            Assert.All(result.Roads, r => Assert.False(r.Connected));
            Assert.Contains(result.Warnings, w => w.Contains("do not reach"));
        }

        [Fact]
        public void GeneratedStreets_GetLotsOnBothSides_NoOverlaps()
        {
            var request = Block(true);
            var result = LayoutEngine.Run(request);
            var generatedFrontage = result.Placements.Where(p => p.RoadIndex >= 0).ToList();
            Assert.Contains(generatedFrontage, p => p.Box.Forward.Y < -0.5);
            Assert.Contains(generatedFrontage, p => p.Box.Forward.Y > 0.5);

            var boxes = result.Placements.Select(p => p.Box).ToList();
            for (int i = 0; i < boxes.Count; i++)
            {
                for (int j = i + 1; j < boxes.Count; j++)
                {
                    Assert.False(boxes[i].Overlaps(boxes[j]));
                }

                foreach (var r in result.Roads)
                {
                    Assert.False(boxes[i].Overlaps(Obb.FromSegment(r.A, r.B, r.Width)));
                }
            }
        }

        [Fact]
        public void LongStreets_GetCrossStreetsSharingVertices()
        {
            var request = Block(true);
            request.Boundary = Rect(0, 0, 400, 120);
            request.ExistingRoads.Clear();
            request.ExistingRoads.Add(Road(-20, -4, 420, -4, 1));
            request.ExistingRoads.Add(Road(-4, -20, -4, 140, 2));
            request.ExistingRoads.Add(Road(404, -20, 404, 140, 3));
            request.Options.MaxBlockLength = 120;

            var result = LayoutEngine.Run(request);
            var cross = result.Roads.Where(r => System.Math.Abs(r.A.X - r.B.X) < 1e-6).ToList();
            Assert.NotEmpty(cross);
            foreach (var c in cross)
            {
                Assert.Equal(JoinKind.Generated, c.StartJoin.Kind);
                // Every cross street starts on a vertex shared with a split street piece.
                Assert.Contains(result.Roads, r => r != c && (Vec2.Distance(r.A, c.A) < 1e-6 || Vec2.Distance(r.B, c.A) < 1e-6));
                Assert.True(c.Connected);
            }
        }

        [Fact]
        public void ExistingRoadThroughBlock_SplitsTheStreet()
        {
            var request = Block(true);
            request.ExistingRoads.Add(Road(80, -20, 80, 140, 9));
            var result = LayoutEngine.Run(request);
            Assert.Contains(result.Roads, r => r.EndJoin.Kind == JoinKind.Existing && r.EndJoin.TargetId == 9 || r.StartJoin.Kind == JoinKind.Existing && r.StartJoin.TargetId == 9);
            Assert.DoesNotContain(result.Roads, r => System.Math.Min(r.A.X, r.B.X) < 79 && System.Math.Max(r.A.X, r.B.X) > 81);
        }

        [Fact]
        public void ShallowBlock_NoStreets()
        {
            var request = Block(true);
            request.Boundary = Rect(0, 0, 160, 30);
            var result = LayoutEngine.Run(request);
            Assert.Empty(result.Roads);
        }
    }

    public class CatalogTests
    {
        private static CatalogItem Growable(string zone, string areaType, int level = 1) =>
            new CatalogItem { Name = "x", Kind = ItemKind.Growable, Zone = zone, AreaType = areaType, LotWidth = 2, LotDepth = 2, Level = level };

        [Theory]
        [InlineData("Office High", "Industrial", ItemCategory.Office, "")]
        [InlineData("FR Office High", "Industrial", ItemCategory.Office, "FR")]
        [InlineData("EU Residential Mixed Old Town", "Residential", ItemCategory.ResidentialMixed, "EU")]
        [InlineData("Residential LowRent", "Residential", ItemCategory.ResidentialLowRent, "")]
        [InlineData("EE Residential High", "Residential", ItemCategory.ResidentialHigh, "EE")]
        [InlineData("USSW Residential Medium", "Residential", ItemCategory.ResidentialMedium, "USSW")]
        [InlineData("NA Residential Low", "Residential", ItemCategory.ResidentialLow, "NA")]
        [InlineData("NA Commercial Low Waterfront", "Commercial", ItemCategory.CommercialLow, "NA")]
        [InlineData("SpecializedIndustrialManufacturingOilGeneral", "Industrial", ItemCategory.SpecializedIndustry, "")]
        [InlineData("Industrial Manufacturing", "Industrial", ItemCategory.Industrial, "")]
        public void Classifier_UsesZoneNames(string zone, string areaType, ItemCategory expected, string theme)
        {
            var item = Growable(zone, areaType);
            Assert.Equal(expected, item.Category);
            Assert.Equal(theme, item.Theme);
        }

        [Fact]
        public void ParksAndParking_AreTheirOwnCategories()
        {
            Assert.Equal(ItemCategory.Park, new CatalogItem { Kind = ItemKind.Park | ItemKind.Service }.Category);
            Assert.Equal(ItemCategory.Parking, new CatalogItem { Kind = ItemKind.Parking | ItemKind.Service }.Category);
            Assert.Equal(ItemCategory.Service, new CatalogItem { Kind = ItemKind.Service }.Category);
        }

        [Fact]
        public void Filter_CombinesCriteria()
        {
            var items = new List<CatalogItem>
            {
                Growable("EU Residential Low", "Residential"),
                Growable("NA Residential Low", "Residential"),
                Growable("EU Residential Low Waterfront", "Residential"),
                new CatalogItem { Name = "sig", Kind = ItemKind.Growable | ItemKind.Signature, Zone = "EU Residential Low", AreaType = "Residential", LotWidth = 2, LotDepth = 2 },
                new CatalogItem { Name = "big", Kind = ItemKind.Growable, Zone = "EU Residential Low", AreaType = "Residential", LotWidth = 6, LotDepth = 6 },
                new CatalogItem { Name = "modded", Kind = ItemKind.Growable, Zone = "EU Residential Low", AreaType = "Residential", LotWidth = 2, LotDepth = 2, FromMod = true },
            };

            var filter = new CatalogFilter { Categories = { ItemCategory.ResidentialLow }, Themes = { "EU" }, MaxWidth = 4, Source = SourceFilter.Vanilla };
            var hits = filter.Apply(items).ToList();
            Assert.Single(hits);
            Assert.Equal("EU Residential Low", hits[0].Zone);

            filter.IncludeSignature = true;
            filter.IncludeWaterfront = true;
            Assert.Equal(3, filter.Apply(items).Count());
        }

        [Fact]
        public void Filter_TextMatchesNameOrZone()
        {
            var item = new CatalogItem { Name = "EU_MixedOldTown01_L1_1x2", Kind = ItemKind.Growable, Zone = "EU Residential Mixed Old Town", AreaType = "Residential", LotWidth = 1, LotDepth = 2 };
            Assert.True(new CatalogFilter { Text = "oldtown" }.Matches(item));
            Assert.True(new CatalogFilter { Text = "mixed old" }.Matches(item));
            Assert.False(new CatalogFilter { Text = "tower" }.Matches(item));
        }

        [Fact]
        public void Palette_RoundTripsAndReportsErrors()
        {
            var palette = new Palette { Name = "Old town" };
            palette.Add("EU_MixedOldTown01_L1_1x2", 2.5, 0);
            palette.Add("Park01", 1, 3);
            var parsed = Palette.Parse(palette.Serialize());
            Assert.Equal("Old town", parsed.Name);
            Assert.Equal(2, parsed.Entries.Count);
            Assert.Equal(2.5, parsed.Entries[0].Weight);
            Assert.Equal(3, parsed.Entries[1].MaxCount);

            var errors = new List<string>();
            var bad = Palette.Parse("# x\nfoo\tabc\t1\n\t1\t1\n", errors);
            Assert.Single(bad.Entries);
            Assert.Equal(2, errors.Count);
        }

        [Fact]
        public void Palette_Parse_KeepsWeightsUsable()
        {
            var errors = new List<string>();
            var palette = Palette.Parse("a\tInfinity\t0\nb\tNaN\t0\nc\t99999\t999999999\nd\t-3\t-1\n", errors);
            Assert.Equal(4, palette.Entries.Count);
            Assert.Equal(1, palette.Entries[0].Weight);
            Assert.Equal(1, palette.Entries[1].Weight);
            Assert.Equal(1000, palette.Entries[2].Weight);
            Assert.Equal(100000, palette.Entries[2].MaxCount);
            Assert.Equal(0, palette.Entries[3].Weight);
            Assert.Equal(0, palette.Entries[3].MaxCount);
            Assert.All(palette.Entries, e => Assert.True(!double.IsNaN(e.Weight) && !double.IsInfinity(e.Weight)));
        }

        [Fact]
        public void Palette_ToItems_ConvertsCellsAndReportsMissing()
        {
            var catalog = new Dictionary<string, CatalogItem>
            {
                ["a"] = new CatalogItem { Name = "a", LotWidth = 2, LotDepth = 3, RequiresRoad = false },
            };
            var palette = new Palette();
            palette.Add("a", 2, 5);
            palette.Add("missing");
            var missing = new List<string>();
            var items = palette.ToItems(n => catalog.TryGetValue(n, out var c) ? c : null, missing);
            Assert.Single(items);
            Assert.Equal(16, items[0].Width);
            Assert.Equal(24, items[0].Depth);
            Assert.False(items[0].RequiresRoad);
            Assert.Equal(new[] { "missing" }, missing);
        }

        [Fact]
        public void UniqueBuilding_IsUniqueObjectOrSignature()
        {
            Assert.True(new CatalogItem { Kind = ItemKind.Service | ItemKind.Unique }.IsUniqueBuilding);
            Assert.True(new CatalogItem { Kind = ItemKind.Growable | ItemKind.Signature }.IsUniqueBuilding);
            Assert.False(new CatalogItem { Kind = ItemKind.Growable }.IsUniqueBuilding);
            Assert.False(new CatalogItem { Kind = ItemKind.Service | ItemKind.Park }.IsUniqueBuilding);
            // Marking a building unique must not change its use category.
            Assert.Equal(ItemCategory.Service, new CatalogItem { Kind = ItemKind.Service | ItemKind.Unique }.Category);
        }

        [Fact]
        public void Palette_ToItems_CapsUniqueBuildingsAtOne()
        {
            var catalog = new Dictionary<string, CatalogItem>
            {
                ["landmark"] = new CatalogItem { Name = "landmark", Kind = ItemKind.Service | ItemKind.Unique, LotWidth = 4, LotDepth = 4 },
                ["sig"] = new CatalogItem { Name = "sig", Kind = ItemKind.Growable | ItemKind.Signature, LotWidth = 2, LotDepth = 2 },
                ["house"] = new CatalogItem { Name = "house", Kind = ItemKind.Growable, LotWidth = 1, LotDepth = 1 },
            };
            var palette = new Palette();
            palette.Add("landmark");        // unlimited in the file
            palette.Add("sig", 1, 5);       // hand-edited to 5
            palette.Add("house", 1, 5);
            palette.Add("house2");
            var items = palette.ToItems(n => catalog.TryGetValue(n, out var c) ? c : null);
            Assert.Equal(1, items.Single(i => i.Id == "landmark").MaxCount);
            Assert.Equal(1, items.Single(i => i.Id == "sig").MaxCount);
            Assert.Equal(5, items.Single(i => i.Id == "house").MaxCount);
        }

        [Fact]
        public void Polyline_SimplifiesStraightRuns()
        {
            var straight = Enumerable.Range(0, 20).Select(i => new Vec2(i * 4, 0)).ToList();
            Assert.Equal(new[] { 0, 19 }, Polyline.Simplify(straight, 0.5));

            var bend = new List<Vec2> { new Vec2(0, 0), new Vec2(10, 0), new Vec2(20, 0), new Vec2(20, 10), new Vec2(20, 20) };
            Assert.Equal(new[] { 0, 2, 4 }, Polyline.Simplify(bend, 0.5));
        }
    }

    public class SizeFilterTests
    {
        [Theory]
        [InlineData(SizeCompare.Less, 3, 1, 2)]
        [InlineData(SizeCompare.Equal, 3, 3, 3)]
        [InlineData(SizeCompare.Greater, 3, 4, int.MaxValue)]
        [InlineData(SizeCompare.Less, 1, 1, 0)] // "< 1" matches nothing
        [InlineData(SizeCompare.Greater, 0, 1, int.MaxValue)] // 0 = any
        [InlineData(SizeCompare.Equal, 0, 1, int.MaxValue)]
        public void SizeRange_IsStrictAsWritten(SizeCompare compare, int value, int min, int max)
        {
            CatalogFilter.SizeRange(compare, value, out int lo, out int hi);
            Assert.Equal(min, lo);
            Assert.Equal(max, hi);
        }

        [Fact]
        public void WidthAndDepth_FilterSeparately()
        {
            CatalogItem Lot(string name, int w, int d) => new CatalogItem { Name = name, Kind = ItemKind.Growable, Zone = "EU Residential Low", AreaType = "Residential", LotWidth = w, LotDepth = d };
            var items = new[] { Lot("2x3", 2, 3), Lot("3x2", 3, 2), Lot("3x4", 3, 4), Lot("4x4", 4, 4) };

            var filter = new CatalogFilter();
            filter.SetWidth(SizeCompare.Equal, 3);
            filter.SetDepth(SizeCompare.Greater, 2);
            Assert.Equal(new[] { "3x4" }, filter.Apply(items).Select(i => i.Name));

            filter = new CatalogFilter();
            filter.SetWidth(SizeCompare.Less, 4);
            Assert.Equal(new[] { "2x3", "3x2", "3x4" }, filter.Apply(items).Select(i => i.Name));

            filter.SetDepth(SizeCompare.Less, 3);
            Assert.Equal(new[] { "3x2" }, filter.Apply(items).Select(i => i.Name));
        }
    }
}
