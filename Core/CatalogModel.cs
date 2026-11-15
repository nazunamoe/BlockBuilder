using System;
using System.Collections.Generic;
using System.Linq;

namespace BlockBuilder.Core
{
    [Flags]
    public enum ItemKind
    {
        None = 0,
        Growable = 1,
        Park = 2,
        Parking = 4,
        Service = 8,
        Signature = 16,
        Upgrade = 32,
    }

    /// <summary>
    /// Primary use of a prefab. Derived from the zone name because the game's AreaType lumps
    /// offices in with industry (catalog.tsv: "Office High" → areaType Industrial).
    /// </summary>
    public enum ItemCategory
    {
        Other,
        ResidentialLow,
        ResidentialMedium,
        ResidentialHigh,
        ResidentialMixed,
        ResidentialLowRent,
        CommercialLow,
        CommercialHigh,
        Office,
        Industrial,
        SpecializedIndustry,
        Service,
        Park,
        Parking,
    }

    public sealed class CatalogItem
    {
        public string Name = string.Empty;
        public ItemKind Kind;
        /// <summary>Lot width in 8 m cells.</summary>
        public int LotWidth;
        /// <summary>Lot depth in 8 m cells.</summary>
        public int LotDepth;
        public string Zone = string.Empty;
        public string AreaType = string.Empty;
        public int Level;
        public bool FromMod;
        public bool RequiresRoad = true;

        public ItemCategory Category => Classifier.Category(this);
        public string Theme => Classifier.Theme(Zone);
        public bool Waterfront => Zone.IndexOf("Waterfront", StringComparison.OrdinalIgnoreCase) >= 0;
        public bool IsSignature => (Kind & ItemKind.Signature) != 0;
    }

    public static class Classifier
    {
        public static ItemCategory Category(CatalogItem item)
        {
            if ((item.Kind & ItemKind.Park) != 0)
            {
                return ItemCategory.Park;
            }

            if ((item.Kind & ItemKind.Parking) != 0 && (item.Kind & ItemKind.Growable) == 0)
            {
                return ItemCategory.Parking;
            }

            string zone = item.Zone ?? string.Empty;
            if ((item.Kind & ItemKind.Growable) != 0 || zone.Length > 0)
            {
                if (Has(zone, "Office"))
                {
                    return ItemCategory.Office;
                }

                if (Has(zone, "Specialized"))
                {
                    return ItemCategory.SpecializedIndustry;
                }

                if (item.AreaType == "Industrial" || Has(zone, "Industrial"))
                {
                    return ItemCategory.Industrial;
                }

                if (item.AreaType == "Commercial" || Has(zone, "Commercial"))
                {
                    return Has(zone, "High") ? ItemCategory.CommercialHigh : ItemCategory.CommercialLow;
                }

                if (item.AreaType == "Residential" || Has(zone, "Residential"))
                {
                    if (Has(zone, "LowRent"))
                    {
                        return ItemCategory.ResidentialLowRent;
                    }

                    if (Has(zone, "Mixed"))
                    {
                        return ItemCategory.ResidentialMixed;
                    }

                    if (Has(zone, "High"))
                    {
                        return ItemCategory.ResidentialHigh;
                    }

                    if (Has(zone, "Medium"))
                    {
                        return ItemCategory.ResidentialMedium;
                    }

                    return ItemCategory.ResidentialLow;
                }
            }

            if ((item.Kind & ItemKind.Service) != 0)
            {
                return ItemCategory.Service;
            }

            return ItemCategory.Other;
        }

        /// <summary>Region prefix of a zone name ("EU Residential Low" → "EU"), or "" when none.</summary>
        public static string Theme(string zone)
        {
            if (string.IsNullOrEmpty(zone))
            {
                return string.Empty;
            }

            int space = zone.IndexOf(' ');
            if (space < 2 || space > 4)
            {
                return string.Empty;
            }

            string token = zone.Substring(0, space);
            return token.All(char.IsUpper) ? token : string.Empty;
        }

        private static bool Has(string text, string word) => text.IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    public enum SourceFilter
    {
        All,
        Vanilla,
        Mods,
    }

    /// <summary>User-facing catalog filter. Empty sets mean "any".</summary>
    public sealed class CatalogFilter
    {
        public HashSet<ItemCategory> Categories = new HashSet<ItemCategory>();
        public HashSet<string> Themes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public int MinWidth = 1;
        public int MaxWidth = int.MaxValue;
        public int MinDepth = 1;
        public int MaxDepth = int.MaxValue;
        public int MinLevel;
        public int MaxLevel = int.MaxValue;
        public SourceFilter Source = SourceFilter.All;
        public bool IncludeSignature;
        public bool IncludeWaterfront;
        public bool IncludeUpgrades;
        public string Text = string.Empty;

        public bool Matches(CatalogItem item)
        {
            if (item == null)
            {
                return false;
            }

            if (!IncludeUpgrades && (item.Kind & ItemKind.Upgrade) != 0)
            {
                return false;
            }

            if (!IncludeSignature && item.IsSignature)
            {
                return false;
            }

            if (!IncludeWaterfront && item.Waterfront)
            {
                return false;
            }

            if (Categories.Count > 0 && !Categories.Contains(item.Category))
            {
                return false;
            }

            if (Themes.Count > 0 && !Themes.Contains(item.Theme))
            {
                return false;
            }

            if (item.LotWidth < MinWidth || item.LotWidth > MaxWidth || item.LotDepth < MinDepth || item.LotDepth > MaxDepth)
            {
                return false;
            }

            if ((item.Kind & ItemKind.Growable) != 0 && (item.Level < MinLevel || item.Level > MaxLevel))
            {
                return false;
            }

            if (Source == SourceFilter.Vanilla && item.FromMod || Source == SourceFilter.Mods && !item.FromMod)
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(Text))
            {
                string text = Text.Trim();
                if (item.Name.IndexOf(text, StringComparison.OrdinalIgnoreCase) < 0 && item.Zone.IndexOf(text, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    return false;
                }
            }

            return true;
        }

        public IEnumerable<CatalogItem> Apply(IEnumerable<CatalogItem> items) => items.Where(Matches);
    }
}
