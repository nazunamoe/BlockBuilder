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

        public override GameMode gameMode => GameMode.Game;

        protected override void OnCreate()
        {
            base.OnCreate();
            m_Tool = World.GetOrCreateSystemManaged<BuilderTool>();
            m_Catalog = World.GetOrCreateSystemManaged<CatalogSystem>();

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
                    o.DesignateItem = 0;
                }
            })));
            AddBinding(new TriggerBinding<int>(kGroup, "setDesignate", i => Edit(o => o.DesignateItem = i)));
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
            AddBinding(new TriggerBinding<int>(kGroup, "setOverlap", n => Edit(o => o.OverlapAllowance = System.Math.Max(0, System.Math.Min(8, n)))));
            AddBinding(new TriggerBinding<bool>(kGroup, "setGenerateRoads", b => Edit(o => o.GenerateRoads = b)));
            AddBinding(new TriggerBinding<bool>(kGroup, "setFillInterior", b => Edit(o => o.FillInterior = b)));
            AddBinding(new TriggerBinding<int>(kGroup, "setBlockLength", n => Edit(o => o.MaxBlockLength = System.Math.Max(64, System.Math.Min(400, n)))));
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

            string listsKey = $"{PaletteStore.Version}|{options.ActivePalette}|{m_Catalog.Ready}|{options.CheckedItems}";
            if (listsKey != m_ListsKey)
            {
                m_ListsKey = listsKey;
                m_Lists.Update(BuildLists(options));
            }

            m_State.Update(BuildState(options));
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
            Prop(sb, "designate", o.DesignateItem);
            Prop(sb, "scope", o.Scope == ItemScope.Checked ? 1 : 0);
            Prop(sb, "fillRest", !o.PlacedOnly);
            Prop(sb, "snapOff", o.SnapOff);
            Prop(sb, "overlap", o.OverlapAllowance);
            Prop(sb, "anarchyInstalled", AnarchyLink.Installed);
            Prop(sb, "anarchyOn", AnarchyLink.Enabled);
            Prop(sb, "generateRoads", o.GenerateRoads);
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
