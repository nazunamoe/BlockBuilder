using System.Collections.Generic;
using Colossal;
using Colossal.IO.AssetDatabase;
using Game.Input;
using Game.Modding;
using Game.Settings;
using Game.UI.Widgets;
using Unity.Entities;

namespace BlockBuilder
{
    /// <summary>Which placement experiment the spike tool runs.</summary>
    public enum SpikeCase
    {
        ServiceBuilding,
        Growable,
        Park,
        Parking,
        Road,
        RowOnNewRoad,
    }

    /// <summary>How the area is chosen. Block is 0, the default.</summary>
    public enum AreaSelect
    {
        /// <summary>Paint the area with a brush; roads are walls and the edge hugs the curbs (Placemaker-style paint).</summary>
        Paint,
        /// <summary>Click corners by hand.</summary>
        Draw,
    }

    /// <summary>How hard the fill packs lots. Normal is 0 so a missing saved value means Normal.</summary>
    public enum FillDensity
    {
        Normal,
        Relaxed,
        Dense,
        /// <summary>User-set gap and overlaps (overlaps need Anarchy on); searches like Dense.</summary>
        Custom,
    }

    /// <summary>Which list entries the fill uses.</summary>
    public enum ItemScope
    {
        All,
        Checked,
    }

    [FileLocation(nameof(BlockBuilder))]
    [SettingsUITabOrder(kBuilderTab, kSection)]
    [SettingsUIGroupOrder(kKeyGroup, kBuildGroup, kPaletteGroup, kSpikeGroup, kDebugGroup)]
    [SettingsUIShowGroupName(kKeyGroup, kBuildGroup, kPaletteGroup, kSpikeGroup, kDebugGroup)]
    [SettingsUIKeyboardAction(kToggleAction, ActionType.Button, usages: new[] { Usages.kDefaultUsage, Usages.kToolUsage })]
    public class Setting : ModSetting
    {
        public const string kToggleAction = "ToggleBuilder";
        public const string kKeyGroup = "Keys";
        public const string kBuilderTab = "Builder";
        public const string kSection = "Main";
        public const string kBuildGroup = "Build";
        public const string kPaletteGroup = "Palette";
        public const string kSpikeGroup = "Spike";
        public const string kDebugGroup = "Debug";

        public Setting(IMod mod) : base(mod)
        {
            SetDefaults();
        }

        // ---- Builder tab: key binding ----

        /// <summary>Toggles the builder tool (and its panel). Ctrl+B is often taken by other mods.</summary>
        [SettingsUIKeyboardBinding(BindingKeyboard.B, kToggleAction, ctrl: true, shift: true)]
        [SettingsUISection(kBuilderTab, kKeyGroup)]
        public ProxyBinding ToggleBinding { get; set; }

        [SettingsUISection(kBuilderTab, kKeyGroup)]
        public bool ResetBindings
        {
            set => ResetKeyBindings();
        }

        // ---- Builder tab: area fill tool ----

        [SettingsUIButton]
        [SettingsUISection(kBuilderTab, kBuildGroup)]
        public bool ActivateBuilder
        {
            set
            {
                World.DefaultGameObjectInjectionWorld?.GetExistingSystemManaged<BuilderTool>()?.Activate();
            }
        }

        [SettingsUIDropdown(typeof(Setting), nameof(GetPaletteItems))]
        [SettingsUIValueVersion(typeof(Setting), nameof(GetListVersion))]
        [SettingsUISection(kBuilderTab, kBuildGroup)]
        public int ActivePalette { get; set; }

        /// <summary>Dropdown items are only re-read when this changes.</summary>
        public int GetListVersion() => PaletteStore.Version * 1000 + ActivePalette;

        [SettingsUIButton]
        [SettingsUISection(kBuilderTab, kBuildGroup)]
        public bool ReloadPalettes
        {
            set
            {
                PaletteStore.Reload();
                Changed();
            }
        }

        /// <summary>Fill with every entry of the list, or only the ones checked in the panel.</summary>
        [SettingsUISection(kBuilderTab, kBuildGroup)]
        public ItemScope Scope { get; set; }

        /// <summary>Off: build only what was placed with Shift+click.</summary>
        [SettingsUISection(kBuilderTab, kBuildGroup)]
        public bool PlacedOnly { get; set; }

        /// <summary>Prefab names checked in the panel, one per line (shared across lists).</summary>
        [SettingsUIHidden]
        public string CheckedItems { get; set; }

        private HashSet<string> m_Checked;
        private string m_CheckedSource;

        internal HashSet<string> Checked
        {
            get
            {
                if (m_Checked == null || !ReferenceEquals(m_CheckedSource, CheckedItems))
                {
                    m_CheckedSource = CheckedItems;
                    m_Checked = new HashSet<string>((CheckedItems ?? string.Empty).Split(new[] { '\n' }, System.StringSplitOptions.RemoveEmptyEntries));
                }

                return m_Checked;
            }
        }

        internal void SetChecked(IEnumerable<string> names, bool value)
        {
            var set = new HashSet<string>(Checked);
            foreach (var n in names)
            {
                if (value)
                {
                    set.Add(n);
                }
                else
                {
                    set.Remove(n);
                }
            }

            var sorted = new List<string>(set);
            sorted.Sort(System.StringComparer.Ordinal);
            CheckedItems = string.Join("\n", sorted);
        }

        internal Core.FillMode PlacementMode => PlacedOnly ? Core.FillMode.FixedOnly : Core.FillMode.Fill;

        /// <summary>Snap options (Game.Tools.Snap bits) turned off in the game's tool options panel. Default 0 = all on.</summary>
        [SettingsUIHidden]
        public int SnapOff { get; set; }

        [SettingsUISection(kBuilderTab, kBuildGroup)]
        public AreaSelect Selection { get; set; }

        /// <summary>Brush radius in metres for painting; 0 means the default (16 m). Set in the panel.</summary>
        [SettingsUIHidden]
        public int BrushRadius { get; set; }

        /// <summary>Inverted so the default (false) lets the brush paint across roads. Set in the panel.</summary>
        [SettingsUIHidden]
        public bool StopAtRoads { get; set; }

        /// <summary>Which lot the fill tries first: 0 mixed, 1 largest, 2 smallest. Set in the panel.</summary>
        [SettingsUIHidden]
        public int SizeOrderMode { get; set; }

        /// <summary>What size means for the order: 0 area, 1 width, 2 depth. Set in the panel.</summary>
        [SettingsUIHidden]
        public int SizeKeyMode { get; set; }

        /// <summary>Largest area (square metres) that may be drawn or painted; 0 means the default. Set in the panel.</summary>
        [SettingsUIHidden]
        public int MaxArea { get; set; }

        /// <summary>Layout variant found by the last Optimize (0 = plain fill). Set by the tool.</summary>
        [SettingsUIHidden]
        public int VariantIndex { get; set; }

        [SettingsUISection(kBuilderTab, kBuildGroup)]
        public FillDensity Density { get; set; }

        /// <summary>Metres lots may overlap at each side (left/right); applied only at Max density with Anarchy on. Set in the panel.</summary>
        [SettingsUIHidden]
        public float OverlapSide { get; set; }

        /// <summary>Custom density: empty metres left between lots along a road.</summary>
        [SettingsUIHidden]
        public float CustomGap { get; set; }

        /// <summary>Metres lots may overlap at the back; applied only at Max density with Anarchy on. Set in the panel.</summary>
        [SettingsUIHidden]
        public float OverlapBack { get; set; }

        [SettingsUISection(kBuilderTab, kBuildGroup)]
        public bool GenerateRoads { get; set; }

        [SettingsUITextInput]
        [SettingsUISection(kBuilderTab, kBuildGroup)]
        public string NewRoadName { get; set; }

        [SettingsUISlider(min = 64, max = 400, step = 8)]
        [SettingsUISection(kBuilderTab, kBuildGroup)]
        public int MaxBlockLength { get; set; }

        [SettingsUISection(kBuilderTab, kBuildGroup)]
        public bool FillInterior { get; set; }

        [SettingsUIHidden]
        public int Seed { get; set; }

        [SettingsUIButton]
        [SettingsUISection(kBuilderTab, kBuildGroup)]
        public bool Shuffle
        {
            set
            {
                Seed = Seed % 100000 + 1;
                Changed();
            }
        }

        // ---- Builder tab: make a palette from a catalog filter ----

        [SettingsUISection(kBuilderTab, kPaletteGroup)]
        public Core.ItemCategory FilterCategory { get; set; }

        [SettingsUITextInput]
        [SettingsUISection(kBuilderTab, kPaletteGroup)]
        public string FilterTheme { get; set; }

        [SettingsUITextInput]
        [SettingsUISection(kBuilderTab, kPaletteGroup)]
        public string FilterText { get; set; }

        [SettingsUISlider(min = 1, max = 12, step = 1)]
        [SettingsUISection(kBuilderTab, kPaletteGroup)]
        public int FilterMaxWidth { get; set; }

        [SettingsUISlider(min = 1, max = 12, step = 1)]
        [SettingsUISection(kBuilderTab, kPaletteGroup)]
        public int FilterMaxDepth { get; set; }

        [SettingsUISection(kBuilderTab, kPaletteGroup)]
        public Core.SourceFilter FilterSource { get; set; }

        [SettingsUISection(kBuilderTab, kPaletteGroup)]
        public bool FilterIncludeSignature { get; set; }

        [SettingsUITextInput]
        [SettingsUISection(kBuilderTab, kPaletteGroup)]
        public string NewPaletteName { get; set; }

        [SettingsUIButton]
        [SettingsUISection(kBuilderTab, kPaletteGroup)]
        public bool CreatePalette
        {
            set
            {
                World.DefaultGameObjectInjectionWorld?.GetExistingSystemManaged<CatalogSystem>()?.CreatePaletteFromFilter(this);
                Changed();
            }
        }

        /// <summary>Bumped when a builder option changes so an active tool can replan.</summary>
        internal int Revision;

        private void Changed() => Revision++;

        /// <summary>Called by the in-game panel after it edits an option.</summary>
        internal void NotifyChanged()
        {
            Changed();
            ApplyAndSave();
        }

        internal void NextSeed()
        {
            Seed = Seed % 100000 + 1;
            Changed();
        }

        public Core.CatalogFilter BuildFilter()
        {
            var filter = new Core.CatalogFilter
            {
                MaxWidth = FilterMaxWidth,
                MaxDepth = FilterMaxDepth,
                Source = FilterSource,
                IncludeSignature = FilterIncludeSignature,
                Text = FilterText ?? string.Empty,
            };
            if (FilterCategory != Core.ItemCategory.Other)
            {
                filter.Categories.Add(FilterCategory);
            }

            foreach (var theme in (FilterTheme ?? string.Empty).Split(new[] { ',', ' ' }, System.StringSplitOptions.RemoveEmptyEntries))
            {
                filter.Themes.Add(theme.Trim());
            }

            return filter;
        }

        public DropdownItem<int>[] GetPaletteItems()
        {
            var items = new List<DropdownItem<int>>();
            var all = PaletteStore.All;
            for (int i = 0; i < all.Count; i++)
            {
                items.Add(new DropdownItem<int> { value = i, displayName = $"{all[i].Palette.Name} ({all[i].Palette.Entries.Count})" });
            }

            if (items.Count == 0)
            {
                items.Add(new DropdownItem<int> { value = -1, displayName = "-" });
            }

            return items.ToArray();
        }

        [SettingsUISection(kSection, kSpikeGroup)]
        public SpikeCase TestCase { get; set; }

        /// <summary>Optional prefab name; empty means pick one from the catalog scan.</summary>
        [SettingsUITextInput]
        [SettingsUISection(kSection, kSpikeGroup)]
        public string PrefabOverride { get; set; }

        /// <summary>Optional road prefab name for the road tests; empty means the narrowest vanilla road.</summary>
        [SettingsUITextInput]
        [SettingsUISection(kSection, kSpikeGroup)]
        public string RoadOverride { get; set; }

        [SettingsUISlider(min = 1, max = 10, step = 1)]
        [SettingsUISection(kSection, kSpikeGroup)]
        public int RowCount { get; set; }

        [SettingsUISection(kSection, kSpikeGroup)]
        public bool ApplyDespiteErrors { get; set; }

        [SettingsUIButton]
        [SettingsUISection(kSection, kSpikeGroup)]
        public bool ActivateTool
        {
            set
            {
                World.DefaultGameObjectInjectionWorld?.GetExistingSystemManaged<SpikeTool>()?.Activate();
            }
        }

        [SettingsUIButton]
        [SettingsUISection(kSection, kDebugGroup)]
        public bool DumpCatalog
        {
            set
            {
                World.DefaultGameObjectInjectionWorld?.GetExistingSystemManaged<CatalogSystem>()?.Scan(true);
            }
        }

        public override void SetDefaults()
        {
            TestCase = SpikeCase.ServiceBuilding;
            PrefabOverride = string.Empty;
            RoadOverride = string.Empty;
            RowCount = 4;
            ApplyDespiteErrors = false;

            ActivePalette = 0;
            Scope = ItemScope.All;
            PlacedOnly = false;
            CheckedItems = string.Empty;
            GenerateRoads = false;
            NewRoadName = "Small Road";
            MaxBlockLength = 160;
            FillInterior = false;
            Seed = 1;
            FilterCategory = Core.ItemCategory.ResidentialLow;
            FilterTheme = string.Empty;
            FilterText = string.Empty;
            FilterMaxWidth = 4;
            FilterMaxDepth = 4;
            FilterSource = Core.SourceFilter.Vanilla;
            FilterIncludeSignature = false;
            NewPaletteName = "My palette";
        }
    }

    public class LocaleEN : IDictionarySource
    {
        private readonly Setting m_Setting;

        public LocaleEN(Setting setting)
        {
            m_Setting = setting;
        }

        public IEnumerable<KeyValuePair<string, string>> ReadEntries(IList<IDictionaryEntryError> errors, Dictionary<string, int> indexCounts)
        {
            var entries = new Dictionary<string, string>
            {
                { m_Setting.GetSettingsLocaleID(), "Block Builder" },
                { m_Setting.GetOptionTabLocaleID(Setting.kSection), "Main" },
                { m_Setting.GetOptionGroupLocaleID(Setting.kSpikeGroup), "Placement spike (test)" },
                { m_Setting.GetOptionGroupLocaleID(Setting.kDebugGroup), "Debug" },

                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.TestCase)), "Test case" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.TestCase)), "What the test tool places. Results go to Logs/BlockBuilder.log." },
                { m_Setting.GetEnumValueLocaleID(SpikeCase.ServiceBuilding), "Service building" },
                { m_Setting.GetEnumValueLocaleID(SpikeCase.Growable), "Growable building" },
                { m_Setting.GetEnumValueLocaleID(SpikeCase.Park), "Park" },
                { m_Setting.GetEnumValueLocaleID(SpikeCase.Parking), "Parking lot" },
                { m_Setting.GetEnumValueLocaleID(SpikeCase.Road), "Road (2 clicks)" },
                { m_Setting.GetEnumValueLocaleID(SpikeCase.RowOnNewRoad), "New road + growable row (2 clicks)" },

                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.PrefabOverride)), "Prefab name override" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.PrefabOverride)), "Leave empty to pick a prefab automatically. See ModsData/BlockBuilder/catalog.tsv for names." },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.RoadOverride)), "Road name override" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.RoadOverride)), "Leave empty to use the narrowest vanilla road. Names are in catalog.tsv (type=road)." },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.RowCount)), "Buildings in row" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.RowCount)), "Maximum buildings for the road + row test." },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.ApplyDespiteErrors)), "Apply despite errors" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.ApplyDespiteErrors)), "Experiment only: commit the preview even when the game reports errors." },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.ActivateTool)), "Activate test tool" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.ActivateTool)), "Close the menu afterwards. Left click places, right click cancels / exits." },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.DumpCatalog)), "Dump catalog" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.DumpCatalog)), "Write all building and road prefabs to ModsData/BlockBuilder/catalog.tsv." },
            };
            BuilderLocale.Add(entries, m_Setting, false);
            return entries;
        }

        public void Unload()
        {
        }
    }

    public class LocaleKO : IDictionarySource
    {
        private readonly Setting m_Setting;

        public LocaleKO(Setting setting)
        {
            m_Setting = setting;
        }

        public IEnumerable<KeyValuePair<string, string>> ReadEntries(IList<IDictionaryEntryError> errors, Dictionary<string, int> indexCounts)
        {
            var entries = new Dictionary<string, string>
            {
                { m_Setting.GetSettingsLocaleID(), "Block Builder" },
                { m_Setting.GetOptionTabLocaleID(Setting.kSection), "기본" },
                { m_Setting.GetOptionGroupLocaleID(Setting.kSpikeGroup), "배치 기술 검증 (테스트)" },
                { m_Setting.GetOptionGroupLocaleID(Setting.kDebugGroup), "디버그" },

                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.TestCase)), "테스트 항목" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.TestCase)), "테스트 도구가 배치할 대상입니다. 결과는 Logs/BlockBuilder.log에 기록됩니다." },
                { m_Setting.GetEnumValueLocaleID(SpikeCase.ServiceBuilding), "서비스 건물" },
                { m_Setting.GetEnumValueLocaleID(SpikeCase.Growable), "성장형 건물" },
                { m_Setting.GetEnumValueLocaleID(SpikeCase.Park), "공원" },
                { m_Setting.GetEnumValueLocaleID(SpikeCase.Parking), "주차장" },
                { m_Setting.GetEnumValueLocaleID(SpikeCase.Road), "도로 (2번 클릭)" },
                { m_Setting.GetEnumValueLocaleID(SpikeCase.RowOnNewRoad), "새 도로 + 성장형 건물 줄 (2번 클릭)" },

                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.PrefabOverride)), "프리팹 이름 지정" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.PrefabOverride)), "비워 두면 자동으로 고릅니다. 이름 목록은 ModsData/BlockBuilder/catalog.tsv에 있습니다." },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.RoadOverride)), "도로 이름 지정" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.RoadOverride)), "비워 두면 가장 좁은 기본 도로를 씁니다. 이름은 catalog.tsv(type=road)에 있습니다." },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.RowCount)), "줄에 놓을 건물 수" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.RowCount)), "도로 + 건물 줄 테스트의 최대 건물 수입니다." },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.ApplyDespiteErrors)), "오류가 있어도 적용" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.ApplyDespiteErrors)), "실험용: 게임이 오류를 보고해도 미리보기를 확정합니다." },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.ActivateTool)), "테스트 도구 켜기" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.ActivateTool)), "누른 뒤 메뉴를 닫으세요. 왼쪽 클릭으로 배치, 오른쪽 클릭으로 취소·종료합니다." },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.DumpCatalog)), "카탈로그 내보내기" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.DumpCatalog)), "모든 건물·도로 프리팹을 ModsData/BlockBuilder/catalog.tsv에 기록합니다." },
            };
            BuilderLocale.Add(entries, m_Setting, true);
            return entries;
        }

        public void Unload()
        {
        }
    }
}
