using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Colossal.Serialization.Entities;
using Game;
using Game.Prefabs;
using Unity.Collections;
using Unity.Entities;

namespace BlockBuilder
{
    [Flags]
    public enum EntryKind
    {
        None = 0,
        Growable = 1,
        Park = 2,
        Parking = 4,
        Service = 8,
        Signature = 16,
        Upgrade = 32,
    }

    public sealed class BuildingEntry
    {
        public string Name;
        public Entity Prefab;
        public EntryKind Kind;
        public int LotWidth;
        public int LotDepth;
        public string Zone = string.Empty;
        public string AreaType = string.Empty;
        public int Level;
        public bool FromMod;
        public BuildingFlags Flags;
        /// <summary>BuildingFlags.RequireRoad; the spike placed a lighthouse with no road and no error.</summary>
        public bool RequiresRoad => (Flags & BuildingFlags.RequireRoad) != 0;

        private Core.CatalogItem m_Item;

        /// <summary>Engine/filter view of this prefab.</summary>
        public Core.CatalogItem Item => m_Item ??= new Core.CatalogItem
        {
            Name = Name,
            Kind = (Core.ItemKind)(int)Kind,
            LotWidth = LotWidth,
            LotDepth = LotDepth,
            Zone = Zone,
            AreaType = AreaType,
            Level = Level,
            FromMod = FromMod,
            RequiresRoad = RequiresRoad,
        };
    }

    public sealed class RoadEntry
    {
        public string Name;
        public Entity Prefab;
        public float Width;
        public bool FromMod;
    }

    /// <summary>
    /// Scans loaded building and road prefabs. For the spike it answers whether parks and
    /// parking lots are ordinary building prefabs, and supplies test prefabs at runtime.
    /// </summary>
    public partial class CatalogSystem : GameSystemBase
    {
        public readonly List<BuildingEntry> Buildings = new List<BuildingEntry>();
        public readonly List<RoadEntry> Roads = new List<RoadEntry>();
        public bool Ready { get; private set; }

        public override int GetUpdateInterval(SystemUpdatePhase phase) => 1024;

        protected override void OnUpdate()
        {
        }

        protected override void OnGameLoadingComplete(Purpose purpose, GameMode mode)
        {
            base.OnGameLoadingComplete(purpose, mode);
            if (mode == GameMode.Game)
            {
                Scan(true);
                CreateStarterPalettes();
            }
        }

        /// <summary>Gives a first-time user something to fill with; never touches existing lists.</summary>
        private void CreateStarterPalettes()
        {
            if (PaletteStore.All.Count > 0)
            {
                return;
            }

            void Make(string name, Core.CatalogFilter filter)
            {
                var palette = new Core.Palette { Name = name };
                foreach (var b in Buildings.Where(b => filter.Matches(b.Item)).Select(b => b.Name).Distinct().Take(kMaxPaletteFromFilter))
                {
                    palette.Add(b);
                }

                if (palette.Entries.Count > 0)
                {
                    PaletteStore.Save(palette);
                }
            }

            Make("Starter - Residential Low", new Core.CatalogFilter { Categories = { Core.ItemCategory.ResidentialLow }, MaxWidth = 4, MaxDepth = 4, Source = Core.SourceFilter.Vanilla });
            Make("Starter - Parks", new Core.CatalogFilter { Categories = { Core.ItemCategory.Park }, MaxWidth = 8, MaxDepth = 8, Source = Core.SourceFilter.Vanilla });
        }

        public void Scan(bool write)
        {
            try
            {
                ScanBuildings();
                ScanRoads();
                Ready = true;
                Mod.log.Info($"catalog: buildings={Buildings.Count} growable={Count(EntryKind.Growable)} park={Count(EntryKind.Park)} parking={Count(EntryKind.Parking)} service={Count(EntryKind.Service)} signature={Count(EntryKind.Signature)} upgrade={Count(EntryKind.Upgrade)} roads={Roads.Count}");
                if (write)
                {
                    Write();
                }
            }
            catch (Exception e)
            {
                Mod.log.Error(e, "catalog scan failed");
            }
        }

        private int Count(EntryKind kind) => Buildings.Count(b => (b.Kind & kind) != 0);

        private void ScanBuildings()
        {
            Buildings.Clear();
            m_ByName = null;
            var prefabs = World.GetOrCreateSystemManaged<PrefabSystem>();
            using var query = EntityManager.CreateEntityQuery(ComponentType.ReadOnly<BuildingData>(), ComponentType.ReadOnly<ObjectGeometryData>());
            using var entities = query.ToEntityArray(Allocator.Temp);
            foreach (var e in entities)
            {
                if (!prefabs.TryGetPrefab<PrefabBase>(e, out var prefab) || prefab == null)
                {
                    continue;
                }

                var data = EntityManager.GetComponentData<BuildingData>(e);
                var entry = new BuildingEntry
                {
                    Name = prefab.name,
                    Prefab = e,
                    LotWidth = data.m_LotSize.x,
                    LotDepth = data.m_LotSize.y,
                    FromMod = prefab.isSubscribedMod,
                    Flags = data.m_Flags,
                };

                if (EntityManager.HasComponent<SpawnableBuildingData>(e))
                {
                    entry.Kind |= EntryKind.Growable;
                    var spawn = EntityManager.GetComponentData<SpawnableBuildingData>(e);
                    entry.Level = spawn.m_Level;
                    if (spawn.m_ZonePrefab != Entity.Null)
                    {
                        if (prefabs.TryGetPrefab<PrefabBase>(spawn.m_ZonePrefab, out var zone) && zone != null)
                        {
                            entry.Zone = zone.name;
                        }

                        if (EntityManager.HasComponent<ZoneData>(spawn.m_ZonePrefab))
                        {
                            entry.AreaType = EntityManager.GetComponentData<ZoneData>(spawn.m_ZonePrefab).m_AreaType.ToString();
                        }
                    }
                }

                if (EntityManager.HasComponent<ParkData>(e))
                {
                    entry.Kind |= EntryKind.Park;
                }

                if (EntityManager.HasComponent<ParkingFacilityData>(e))
                {
                    entry.Kind |= EntryKind.Parking;
                }

                if (EntityManager.HasComponent<ServiceObjectData>(e))
                {
                    entry.Kind |= EntryKind.Service;
                }

                if (EntityManager.HasComponent<SignatureBuildingData>(e))
                {
                    entry.Kind |= EntryKind.Signature;
                }

                if (EntityManager.HasComponent<ServiceUpgradeData>(e))
                {
                    entry.Kind |= EntryKind.Upgrade;
                }

                Buildings.Add(entry);
            }

            Buildings.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        }

        private void ScanRoads()
        {
            Roads.Clear();
            var prefabs = World.GetOrCreateSystemManaged<PrefabSystem>();
            using var query = EntityManager.CreateEntityQuery(ComponentType.ReadOnly<RoadData>(), ComponentType.ReadOnly<NetGeometryData>());
            using var entities = query.ToEntityArray(Allocator.Temp);
            foreach (var e in entities)
            {
                if (!prefabs.TryGetPrefab<PrefabBase>(e, out var prefab) || prefab == null)
                {
                    continue;
                }

                Roads.Add(new RoadEntry
                {
                    Name = prefab.name,
                    Prefab = e,
                    Width = EntityManager.GetComponentData<NetGeometryData>(e).m_DefaultWidth,
                    FromMod = prefab.isSubscribedMod,
                });
            }

            Roads.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        }

        private void Write()
        {
            var dir = Path.Combine(UnityEngine.Application.persistentDataPath, "ModsData", nameof(BlockBuilder));
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "catalog.tsv");
            var sb = new StringBuilder();
            // Columns 1-10 keep the spike's layout; category/theme/flags are appended.
            sb.AppendLine("type\tname\tkind\tlotW\tlotD\tzone\tareaType\tlevel\tfromMod\twidth\tcategory\ttheme\trequiresRoad\tflags");
            foreach (var b in Buildings)
            {
                sb.Append("building\t").Append(b.Name).Append('\t').Append(b.Kind).Append('\t')
                  .Append(b.LotWidth).Append('\t').Append(b.LotDepth).Append('\t')
                  .Append(b.Zone).Append('\t').Append(b.AreaType).Append('\t').Append(b.Level).Append('\t')
                  .Append(b.FromMod).Append("\t\t")
                  .Append(b.Item.Category).Append('\t').Append(b.Item.Theme).Append('\t')
                  .Append(b.RequiresRoad).Append('\t').Append(b.Flags).Append('\n');
            }

            foreach (var r in Roads)
            {
                sb.Append("road\t").Append(r.Name).Append("\t\t\t\t\t\t\t").Append(r.FromMod).Append('\t')
                  .Append(r.Width.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append("\t\t\t\t\n");
            }

            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
            Mod.log.Info($"catalog written: {path}");
        }

        private Dictionary<string, BuildingEntry> m_ByName;

        public BuildingEntry FindBuilding(string name)
        {
            if (m_ByName == null || m_ByName.Count != Buildings.Count)
            {
                m_ByName = new Dictionary<string, BuildingEntry>(StringComparer.Ordinal);
                foreach (var b in Buildings)
                {
                    m_ByName[b.Name] = b;
                }
            }

            return name != null && m_ByName.TryGetValue(name, out var entry) ? entry : null;
        }

        public RoadEntry FindRoad(string name) => Roads.FirstOrDefault(r => r.Name == name);

        public const int kMaxPaletteFromFilter = 300;

        /// <summary>Saves every prefab matching the settings filter as a new palette (weight 1).</summary>
        public void CreatePaletteFromFilter(Setting settings)
        {
            if (!Ready)
            {
                Scan(false);
            }

            var filter = settings.BuildFilter();
            var matches = Buildings.Where(b => filter.Matches(b.Item)).Select(b => b.Name).Distinct().ToList();
            if (matches.Count == 0)
            {
                Mod.log.Info("palette: the filter matched nothing; nothing saved");
                return;
            }

            var palette = new Core.Palette { Name = string.IsNullOrWhiteSpace(settings.NewPaletteName) ? "palette" : settings.NewPaletteName.Trim() };
            foreach (var name in matches.Take(kMaxPaletteFromFilter))
            {
                palette.Add(name);
            }

            if (matches.Count > kMaxPaletteFromFilter)
            {
                Mod.log.Info($"palette: {matches.Count} matches, kept the first {kMaxPaletteFromFilter}");
            }

            PaletteStore.Save(palette);
            var all = PaletteStore.All;
            for (int i = 0; i < all.Count; i++)
            {
                if (all[i].Palette.Name == palette.Name)
                {
                    settings.ActivePalette = i;
                }
            }
        }
    }
}
