using BlockBuilder.Core;
using Xunit;

namespace BlockBuilder.Core.Tests
{
    public class DisplayNameSearchTests
    {
        private static CatalogItem Item(string name, string shown) => new CatalogItem { Name = name, DisplayName = shown, LotWidth = 2, LotDepth = 2, Kind = ItemKind.Park };

        [Fact]
        public void TextMatchesTheShownName()
        {
            var filter = new CatalogFilter { Text = "분수", Source = SourceFilter.All };
            Assert.True(filter.Matches(Item("ParkFountain01", "분수 공원")));
            Assert.False(filter.Matches(Item("ParkPlain01", "작은 공원")));
        }

        [Fact]
        public void TextStillMatchesThePrefabName()
        {
            var filter = new CatalogFilter { Text = "fountain", Source = SourceFilter.All };
            Assert.True(filter.Matches(Item("ParkFountain01", "분수 공원")));
        }

        [Fact]
        public void MissingShownName_IsHarmless()
        {
            var filter = new CatalogFilter { Text = "x", Source = SourceFilter.All };
            Assert.False(filter.Matches(new CatalogItem { Name = "Park", DisplayName = null, LotWidth = 1, LotDepth = 1, Kind = ItemKind.Park }));
        }
    }
}
