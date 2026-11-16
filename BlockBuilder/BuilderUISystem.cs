using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Colossal.UI.Binding;
using Game;
using Game.Input;
using Game.SceneFlow;
using Game.UI;

namespace BlockBuilder
{
    /// <summary>
    /// Bridge between the builder and the in-game React panel (UI/src). The panel reads two JSON
    /// blobs and calls triggers; <see cref="Setting"/> stays the single source of truth, and the
    /// tool replans on its own when an option changes. Also handles the toggle shortcut.
    /// </summary>
    public partial class BuilderUISystem : UISystemBase
    {
        private const string kGroup = "blockBuilder";

        private ValueBinding<string> m_State;
        private ValueBinding<string> m_Lists;
        private BuilderTool m_Tool;
        private CatalogSystem m_Catalog;
        private ProxyAction m_ToggleAction;
        private string m_ListsKey = string.Empty;
        private bool m_WasActive = true;

        public override GameMode gameMode => GameMode.Game;

        private Game.UI.ImageSystem m_Images;
        private readonly System.Collections.Generic.Dictionary<string, string> m_Thumbs = new System.Collections.Generic.Dictionary<string, string>();

        /// <summary>The game's thumbnail URL for a building (what its toolbar shows); empty when it has none.</summary>
        private string Thumb(BuildingEntry b)
        {
            if (b == null)
            {
                return string.Empty;
            }

            if (!m_Thumbs.TryGetValue(b.Name, out var url))
            {
                try
                {
                    url = m_Images.GetThumbnail(b.Prefab) ?? string.Empty;
                }
                catch (System.Exception e)
                {
                    Mod.log.Warn($"thumbnail of '{b.Name}' failed: {e.Message}");
                    url = string.Empty;
                }

                m_Thumbs[b.Name] = url;
            }

            return url;
        }

        protected override void OnCreate()
        {
            base.OnCreate();
            m_Tool = World.GetOrCreateSystemManaged<BuilderTool>();
            m_Catalog = World.GetOrCreateSystemManaged<CatalogSystem>();
            m_Images = World.GetOrCreateSystemManaged<Game.UI.ImageSystem>();

            AddBinding(m_State = new ValueBinding<string>(kGroup, "state", "{}"));
            AddBinding(m_Lists = new ValueBinding<string>(kGroup, "lists", "{\"palettes\":[],\"entries\":[]}"));

            AddBinding(new TriggerBinding(kGroup, "toggle", () => m_Tool.Toggle()));
            AddBinding(new TriggerBinding(kGroup, "clearArea", () => m_Tool.ClearArea()));
            AddBinding(new TriggerBinding(kGroup, "shuffle", () => Edit(o => o.NextSeed())));
            AddBinding(new TriggerBinding(kGroup, "reloadLists", () =>
            {
                PaletteStore.Reload();
                Edit(o => { });
            }));
            AddBinding(new TriggerBinding<int>(kGroup, "setPalette", i => Edit(o =>
            {
                if (o.ActivePalette != i)
                {
                    o.ActivePalette = i;
                }
            })));
            AddBinding(new TriggerBinding<int>(kGroup, "setScope", i => Edit(o => o.Scope = i == 1 ? ItemScope.Checked : ItemScope.All)));
            AddBinding(new TriggerBinding<bool>(kGroup, "setFillRest", b => Edit(o => o.PlacedOnly = !b)));
            AddBinding(new TriggerBinding<int>(kGroup, "toggleChecked", i => Edit(o =>
            {
                var palette = PaletteStore.Get(o.ActivePalette);
                if (palette != null && i >= 0 && i < palette.Entries.Count)
                {
                    string name = palette.Entries[i].Prefab;
                    o.SetChecked(new[] { name }, !o.Checked.Contains(name));
                }
            })));
            AddBinding(new TriggerBinding<bool>(kGroup, "checkAll", b => Edit(o =>
            {
                var palette = PaletteStore.Get(o.ActivePalette);
                if (palette != null)
                {
                    o.SetChecked(palette.Entries.Select(e => e.Prefab), b);
                }
            })));
            AddBinding(new TriggerBinding<int>(kGroup, "toggleSnap", bit => Edit(o =>
            {
                if ((bit & (int)BuilderTool.kSnapMask) != 0)
                {
                    o.SnapOff ^= bit;
                }
            })));
            // Custom density values, in 0.5 m steps.
            AddBinding(new TriggerBinding<float>(kGroup, "setOverlapSide", v => EditIfAnarchy(o => o.OverlapSide = HalfMetres(v, 8))));
            AddBinding(new TriggerBinding<float>(kGroup, "setOverlapBack", v => EditIfAnarchy(o => o.OverlapBack = HalfMetres(v, 16))));
            AddBinding(new TriggerBinding<float>(kGroup, "setCustomGap", v => Edit(o => o.CustomGap = HalfMetres(v, 8))));
            AddBinding(new TriggerBinding<bool>(kGroup, "setGenerateRoads", b => Edit(o => o.GenerateRoads = b)));
            AddBinding(new TriggerBinding<int>(kGroup, "setSizeOrder", n => Edit(o => o.SizeOrderMode = System.Math.Max(0, System.Math.Min(2, n)))));
            AddBinding(new TriggerBinding<int>(kGroup, "setSizeKey", n => Edit(o => o.SizeKeyMode = System.Math.Max(0, System.Math.Min(2, n)))));
            AddBinding(new TriggerBinding<bool>(kGroup, "setCrossRoads", b => Edit(o => o.StopAtRoads = !b)));
            AddBinding(new TriggerBinding<bool>(kGroup, "setFillInterior", b => Edit(o => o.FillInterior = b)));
            AddBinding(new TriggerBinding<int>(kGroup, "setBlockLength", n => Edit(o => o.MaxBlockLength = System.Math.Max(64, System.Math.Min(400, n)))));
            AddBinding(new TriggerBinding<int>(kGroup, "setSelection", n =>
            {
                var mode = n == 1 ? AreaSelect.Draw : AreaSelect.Paint;
                if (Mod.Options != null && Mod.Options.Selection != mode)
                {
                    m_Tool.ClearArea(); // corners or a picked ring do not carry over between modes
                    Edit(o => o.Selection = mode);
                }
            }));
            AddBinding(new TriggerBinding(kGroup, "build", () => m_Tool.RequestApply()));
            // Brush size does not change the layout: save it without bumping the plan revision.
            AddBinding(new TriggerBinding<int>(kGroup, "setBrush", n =>
            {
                if (Mod.Options != null)
                {
                    Mod.Options.BrushRadius = System.Math.Max(4, System.Math.Min(80, n));
                    Mod.Options.ApplyAndSave();
                }
            }));
            AddBinding(new TriggerBinding<int>(kGroup, "setDensity", n => Edit(o =>
            {
                o.Density = n >= 0 && n <= 3 ? (FillDensity)n : FillDensity.Normal;
            })));

            // ---- List (preset) management ----
            AddBinding(new TriggerBinding<string>(kGroup, "newList", name => CreateList(name, fromChecked: false)));
            AddBinding(new TriggerBinding<string>(kGroup, "newListFromChecked", name => CreateList(name, fromChecked: true)));
            AddBinding(new TriggerBinding(kGroup, "duplicateList", DuplicateList));
            AddBinding(new TriggerBinding<string>(kGroup, "renameList", RenameList));
            AddBinding(new TriggerBinding(kGroup, "deleteList", DeleteList));
            AddBinding(new TriggerBinding<int>(kGroup, "removeItem", RemoveItem));
            AddBinding(new TriggerBinding(kGroup, "clearItems", () =>
            {
                var options = Mod.Options;
                var palette = options == null ? null : PaletteStore.Get(options.ActivePalette);
                if (palette == null || palette.Entries.Count == 0)
                {
                    return;
                }

                palette.Entries.Clear();
                PaletteStore.Update(options.ActivePalette);
            }));
            AddBinding(new TriggerBinding<string>(kGroup, "removeItemByName", name =>
            {
                var palette = Mod.Options == null ? null : PaletteStore.Get(Mod.Options.ActivePalette);
                int index = palette?.Entries.FindIndex(e => e.Prefab == name) ?? -1;
                if (index >= 0)
                {
                    RemoveItem(index);
                }
            }));
            AddBinding(new TriggerBinding<string>(kGroup, "addItem", name => AddItems(new[] { name })));
            AddBinding(new TriggerBinding(kGroup, "addResults", () =>
            {
                // Guard against adding the whole catalog with one click.
                var found = Search();
                if (found.Count <= kMaxAddAll)
                {
                    AddItems(found.Select(b => b.Name));
                }
            }));

            // ---- Catalog search (not persisted) ----
            AddBinding(m_Search = new ValueBinding<string>(kGroup, "search", "{\"total\":0,\"results\":[],\"categories\":[],\"themes\":[]}"));
            AddBinding(new TriggerBinding<string>(kGroup, "setSearchText", s => { m_SearchText = s ?? string.Empty; m_SearchKey = null; }));
            AddBinding(new TriggerBinding<int>(kGroup, "setSearchCategory", c => { m_SearchCategory = c; m_SearchKey = null; }));
            AddBinding(new TriggerBinding<string>(kGroup, "setSearchTheme", s => { m_SearchTheme = s ?? string.Empty; m_SearchKey = null; }));
            AddBinding(new TriggerBinding<int>(kGroup, "setSearchWidth", n => { m_SearchWidth = System.Math.Max(0, System.Math.Min(16, n)); m_SearchKey = null; }));
            AddBinding(new TriggerBinding<int>(kGroup, "setSearchWidthOp", n => { m_SearchWidthOp = ToCompare(n); m_SearchKey = null; }));
            AddBinding(new TriggerBinding<int>(kGroup, "setSearchDepth", n => { m_SearchDepth = System.Math.Max(0, System.Math.Min(16, n)); m_SearchKey = null; }));
            AddBinding(new TriggerBinding<int>(kGroup, "setSearchDepthOp", n => { m_SearchDepthOp = ToCompare(n); m_SearchKey = null; }));
        }

        // ---- List (preset) management ----

        private const int kMaxSearchResults = 80;

        /// <summary>"Add all" works up to this many matches (same cap as making a list from a filter).</summary>
        private const int kMaxAddAll = 300;

        private ValueBinding<string> m_Search;
        private string m_SearchText = string.Empty;
        private int m_SearchCategory = -1;
        private string m_SearchTheme = string.Empty;
        // Lot size filter in cells (0 = any), each with its own < / = / > comparison.
        private int m_SearchWidth;
        private int m_SearchDepth;
        private Core.SizeCompare m_SearchWidthOp = Core.SizeCompare.Equal;
        private Core.SizeCompare m_SearchDepthOp = Core.SizeCompare.Equal;

        private static Core.SizeCompare ToCompare(int n) => n == 0 ? Core.SizeCompare.Less : n == 2 ? Core.SizeCompare.Greater : Core.SizeCompare.Equal;
        private string m_SearchKey;

        private static void Select(string path)
        {
            int index = PaletteStore.IndexOf(path);
            Edit(o =>
            {
                if (index >= 0)
                {
                    o.ActivePalette = index;
                }
            });
        }

        private static void CreateList(string name, bool fromChecked)
        {
            var options = Mod.Options;
            if (options == null)
            {
                return;
            }

            var palette = new Core.Palette { Name = PaletteStore.UniqueName(name) };
            if (fromChecked)
            {
                // The checked items of the current list, in its order (matches the count the panel shows).
                var current = PaletteStore.Get(options.ActivePalette);
                var names = current == null
                    ? new List<string>()
                    : current.Entries.Select(e => e.Prefab).Where(options.Checked.Contains).Distinct().ToList();
                foreach (var n in names)
                {
                    var entry = current?.Entries.FirstOrDefault(e => e.Prefab == n);
                    palette.Add(n, entry?.Weight ?? 1, entry?.MaxCount ?? 0);
                }
            }

            Select(PaletteStore.Save(palette));
        }

        private static void DuplicateList()
        {
            var options = Mod.Options;
            var current = options == null ? null : PaletteStore.Get(options.ActivePalette);
            if (current == null)
            {
                return;
            }

            var copy = Core.Palette.Parse(current.Serialize());
            copy.Name = PaletteStore.UniqueName(current.Name + (Mod.IsKorean ? " 복사본" : " copy"));
            Select(PaletteStore.Save(copy));
        }

        private static void RenameList(string name)
        {
            var options = Mod.Options;
            if (options == null || string.IsNullOrWhiteSpace(name))
            {
                return;
            }

            string path = PaletteStore.Rename(options.ActivePalette, name);
            if (path != null)
            {
                Select(path);
            }
        }

        private static void DeleteList()
        {
            var options = Mod.Options;
            if (options == null || PaletteStore.Get(options.ActivePalette) == null)
            {
                return;
            }

            PaletteStore.Delete(options.ActivePalette);
            Edit(o =>
            {
                o.ActivePalette = System.Math.Max(0, System.Math.Min(o.ActivePalette, PaletteStore.All.Count - 1));
            });
        }

        private static void RemoveItem(int index)
        {
            var options = Mod.Options;
            var palette = options == null ? null : PaletteStore.Get(options.ActivePalette);
            if (palette == null || index < 0 || index >= palette.Entries.Count)
            {
                return;
            }

            palette.Entries.RemoveAt(index);
            PaletteStore.Update(options.ActivePalette);
        }

        private static void AddItems(IEnumerable<string> names)
        {
            var options = Mod.Options;
            var palette = options == null ? null : PaletteStore.Get(options.ActivePalette);
            if (palette == null)
            {
                return;
            }

            int before = palette.Entries.Count;
            foreach (var n in names.Where(n => !string.IsNullOrEmpty(n)).ToList())
            {
                if (!palette.Entries.Any(e => e.Prefab == n))
                {
                    palette.Add(n);
                }
            }

            if (palette.Entries.Count != before)
            {
                PaletteStore.Update(options.ActivePalette);
                Edit(o => { });
            }
        }

        private List<BuildingEntry> Search()
        {
            if (!m_Catalog.Ready)
            {
                return new List<BuildingEntry>();
            }

            var filter = new Core.CatalogFilter
            {
                Text = m_SearchText.Trim(),
                Source = Core.SourceFilter.All,
                IncludeSignature = true,
                IncludeWaterfront = true,
            };
            if (m_SearchCategory >= 0)
            {
                filter.Categories.Add((Core.ItemCategory)m_SearchCategory);
            }

            if (!string.IsNullOrEmpty(m_SearchTheme))
            {
                filter.Themes.Add(m_SearchTheme);
            }

            filter.SetWidth(m_SearchWidthOp, m_SearchWidth);
            filter.SetDepth(m_SearchDepthOp, m_SearchDepth);

            return m_Catalog.Buildings.Where(b => filter.Matches(b.Item)).GroupBy(b => b.Name).Select(g => g.First()).ToList();
        }

        private string BuildSearch(Setting o)
        {
            var all = Search();
            var palette = PaletteStore.Get(o.ActivePalette);
            var inList = new HashSet<string>(palette?.Entries.Select(e => e.Prefab) ?? Enumerable.Empty<string>());
            var sb = new StringBuilder(8192);
            sb.Append('{');
            Prop(sb, "total", all.Count);
            Prop(sb, "category", m_SearchCategory);
            Prop(sb, "theme", m_SearchTheme);
            Prop(sb, "width", m_SearchWidth);
            Prop(sb, "widthOp", (int)m_SearchWidthOp);
            Prop(sb, "depth", m_SearchDepth);
            Prop(sb, "depthOp", (int)m_SearchDepthOp);
            Prop(sb, "text", m_SearchText);
            Prop(sb, "maxAddAll", kMaxAddAll);
            sb.Append("\"results\":[");
            bool first = true;
            foreach (var b in all.Take(kMaxSearchResults))
            {
                if (!first)
                {
                    sb.Append(',');
                }

                first = false;
                sb.Append('{');
                Prop(sb, "name", b.Name);
                Prop(sb, "size", $"{b.LotWidth}x{b.LotDepth}");
                Prop(sb, "thumb", Thumb(b));
                Prop(sb, "inList", inList.Contains(b.Name), last: true);
                sb.Append('}');
            }

            sb.Append("],\"categories\":[");
            first = true;
            foreach (Core.ItemCategory c in System.Enum.GetValues(typeof(Core.ItemCategory)))
            {
                if (c == Core.ItemCategory.Other)
                {
                    continue;
                }

                if (!first)
                {
                    sb.Append(',');
                }

                first = false;
                sb.Append('{');
                Prop(sb, "id", (int)c);
                Prop(sb, "label", Mod.IsKorean ? BuilderLocale.CategoryKo(c) : c.ToString(), last: true);
                sb.Append('}');
            }

            sb.Append("],\"themes\":");
            WriteArray(sb, m_Catalog.Ready
                ? m_Catalog.Buildings.Select(b => b.Item.Theme).Where(t => !string.IsNullOrEmpty(t)).Distinct().OrderBy(t => t, System.StringComparer.Ordinal)
                : Enumerable.Empty<string>());
            sb.Append('}');
            return sb.ToString();
        }

        /// <summary>Rounds to 0.5 m and clamps to 0..max.</summary>
        private static float HalfMetres(float v, float max) => float.IsNaN(v) ? 0 : System.Math.Max(0, System.Math.Min(max, (float)System.Math.Round(v * 2) / 2));

        /// <summary>Overlap values only change while Anarchy is on (they have no effect otherwise).</summary>
        private static void EditIfAnarchy(System.Action<Setting> change)
        {
            if (AnarchyLink.Enabled)
            {
                Edit(change);
            }
        }

        private static void Edit(System.Action<Setting> change)
        {
            var options = Mod.Options;
            if (options == null)
            {
                return;
            }

            change(options);
            options.NotifyChanged();
        }

        protected override void OnUpdate()
        {
            base.OnUpdate();
            var options = Mod.Options;
            if (options == null)
            {
                return;
            }

            if (m_ToggleAction == null)
            {
                m_ToggleAction = options.GetAction(Setting.kToggleAction);
                if (m_ToggleAction != null)
                {
                    m_ToggleAction.shouldBeEnabled = true;
                }
            }

            if (m_ToggleAction != null && m_ToggleAction.WasPressedThisFrame() && GameManager.instance.gameMode == GameMode.Game)
            {
                m_Tool.Toggle();
            }

            // The catalog search runs only while the builder is open and only when something changed.
            if (m_Tool.IsActive)
            {
                string searchKey = $"{m_SearchText}|{m_SearchCategory}|{m_SearchTheme}|{m_SearchWidth}{m_SearchWidthOp}|{m_SearchDepth}{m_SearchDepthOp}|{PaletteStore.Version}|{options.ActivePalette}|{m_Catalog.Ready}|{Mod.IsKorean}";
                if (searchKey != m_SearchKey)
                {
                    m_SearchKey = searchKey;
                    m_Search.Update(BuildSearch(options));
                }
            }

            string listsKey = $"{PaletteStore.Version}|{options.ActivePalette}|{m_Catalog.Ready}|{options.CheckedItems}";
            if (listsKey != m_ListsKey)
            {
                m_ListsKey = listsKey;
                m_Lists.Update(BuildLists(options));
            }

            // The panel only shows while the builder is active; the toolbar button just needs to see it
            // switch off once. Skip building the state every frame while the builder is off.
            bool active = m_Tool.IsActive;
            if (active || m_WasActive)
            {
                m_State.Update(BuildState(options));
            }

            m_WasActive = active;
        }

        private string BuildState(Setting o)
        {
            var sb = new StringBuilder(256);
            sb.Append('{');
            Prop(sb, "active", m_Tool.IsActive);
            Prop(sb, "planned", m_Tool.IsPlanned);
            Prop(sb, "corners", m_Tool.CornerCount);
            Prop(sb, "designated", m_Tool.DesignatedCount);
            Prop(sb, "buildings", m_Tool.PlannedBuildings);
            Prop(sb, "streets", m_Tool.PlannedStreets);
            Prop(sb, "palette", o.ActivePalette);
            Prop(sb, "scope", o.Scope == ItemScope.Checked ? 1 : 0);
            Prop(sb, "fillRest", !o.PlacedOnly);
            Prop(sb, "snapOff", o.SnapOff);
            Prop(sb, "selection", (int)o.Selection);
            Prop(sb, "picked", m_Tool.IsPicked);
            Prop(sb, "blocks", m_Tool.BlockCount);
            Prop(sb, "brush", (int)BuilderTool.BrushRadius(o));
            Prop(sb, "density", (int)o.Density);
            Prop(sb, "sizeOrder", o.SizeOrderMode);
            Prop(sb, "sizeKey", o.SizeKeyMode);
            Prop(sb, "overlapSide", o.OverlapSide);
            Prop(sb, "overlapBack", o.OverlapBack);
            Prop(sb, "customGap", o.CustomGap);
            Prop(sb, "anarchyInstalled", AnarchyLink.Installed);
            Prop(sb, "anarchyOn", AnarchyLink.Enabled);
            Prop(sb, "generateRoads", o.GenerateRoads);
            Prop(sb, "crossRoads", !o.StopAtRoads);
            Prop(sb, "fillInterior", o.FillInterior);
            Prop(sb, "blockLength", o.MaxBlockLength);
            Prop(sb, "road", o.NewRoadName ?? string.Empty);
            Prop(sb, "status", m_Tool.Status ?? string.Empty);
            Prop(sb, "ko", Mod.IsKorean);
            sb.Append("\"warnings\":");
            WriteArray(sb, m_Tool.PlanWarnings);
            sb.Append('}');
            return sb.ToString();
        }

        private string BuildLists(Setting o)
        {
            var sb = new StringBuilder(4096);
            sb.Append("{\"palettes\":[");
            var all = PaletteStore.All;
            for (int i = 0; i < all.Count; i++)
            {
                if (i > 0)
                {
                    sb.Append(',');
                }

                sb.Append('{');
                Prop(sb, "name", all[i].Palette.Name);
                Prop(sb, "count", all[i].Palette.Entries.Count, last: true);
                sb.Append('}');
            }

            sb.Append("],");
            int checkedCount = palette0(o).Count(e => o.Checked.Contains(e.Prefab));
            Prop(sb, "checkedCount", checkedCount);
            sb.Append("\"entries\":[");
            var palette = PaletteStore.Get(o.ActivePalette);
            if (palette != null)
            {
                for (int i = 0; i < palette.Entries.Count; i++)
                {
                    if (i > 0)
                    {
                        sb.Append(',');
                    }

                    var entry = palette.Entries[i];
                    var building = m_Catalog.Ready ? m_Catalog.FindBuilding(entry.Prefab) : null;
                    sb.Append('{');
                    Prop(sb, "name", entry.Prefab);
                    Prop(sb, "size", building != null ? $"{building.LotWidth}x{building.LotDepth}" : "?");
                    Prop(sb, "thumb", Thumb(building));
                    Prop(sb, "loaded", building != null || !m_Catalog.Ready);
                    Prop(sb, "checked", o.Checked.Contains(entry.Prefab), last: true);
                    sb.Append('}');
                }
            }

            sb.Append("]}");
            return sb.ToString();
        }

        private static IEnumerable<Core.PaletteEntry> palette0(Setting o) =>
            (IEnumerable<Core.PaletteEntry>)PaletteStore.Get(o.ActivePalette)?.Entries ?? System.Array.Empty<Core.PaletteEntry>();

        private static void Prop(StringBuilder sb, string name, bool value, bool last = false)
        {
            sb.Append('"').Append(name).Append("\":").Append(value ? "true" : "false");
            if (!last)
            {
                sb.Append(',');
            }
        }

        private static void Prop(StringBuilder sb, string name, float value, bool last = false)
        {
            sb.Append('"').Append(name).Append("\":").Append(value.ToString("0.###", CultureInfo.InvariantCulture));
            if (!last)
            {
                sb.Append(',');
            }
        }

        private static void Prop(StringBuilder sb, string name, int value, bool last = false)
        {
            sb.Append('"').Append(name).Append("\":").Append(value.ToString(CultureInfo.InvariantCulture));
            if (!last)
            {
                sb.Append(',');
            }
        }

        private static void Prop(StringBuilder sb, string name, string value, bool last = false)
        {
            sb.Append('"').Append(name).Append("\":");
            Str(sb, value);
            if (!last)
            {
                sb.Append(',');
            }
        }

        private static void WriteArray(StringBuilder sb, IEnumerable<string> values)
        {
            sb.Append('[');
            bool first = true;
            foreach (var v in values)
            {
                if (!first)
                {
                    sb.Append(',');
                }

                first = false;
                Str(sb, v);
            }

            sb.Append(']');
        }

        private static void Str(StringBuilder sb, string value)
        {
            sb.Append('"');
            foreach (char c in value ?? string.Empty)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20)
                        {
                            sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            sb.Append(c);
                        }

                        break;
                }
            }

            sb.Append('"');
        }
    }
}
