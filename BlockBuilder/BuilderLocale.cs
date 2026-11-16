using System;
using System.Collections.Generic;
using BlockBuilder.Core;

namespace BlockBuilder
{
    /// <summary>Locale strings for the Builder tab, shared by the English and Korean sources.</summary>
    internal static class BuilderLocale
    {
        public static void Add(Dictionary<string, string> d, Setting s, bool ko)
        {
            void Opt(string name, string en, string enDesc, string kr, string krDesc)
            {
                d[s.GetOptionLabelLocaleID(name)] = ko ? kr : en;
                d[s.GetOptionDescLocaleID(name)] = ko ? krDesc : enDesc;
            }

            d[s.GetOptionTabLocaleID(Setting.kBuilderTab)] = ko ? "빌더" : "Builder";
            d[s.GetOptionGroupLocaleID(Setting.kKeyGroup)] = ko ? "단축키" : "Shortcut";
            Opt(nameof(Setting.ToggleBinding), "Toggle builder", "Turns the builder and its panel on or off. The drawn area is kept.",
                "빌더 켜기/끄기", "빌더와 패널을 켜거나 끕니다. 그린 영역은 유지됩니다.");
            Opt(nameof(Setting.ResetBindings), "Reset shortcut", "Back to Ctrl+Shift+B.", "단축키 초기화", "Ctrl+Shift+B로 되돌립니다.");
            d[s.GetBindingKeyLocaleID(Setting.kToggleAction)] = ko ? "빌더 켜기/끄기" : "Toggle builder";
            d[s.GetBindingMapLocaleID()] = "Block Builder";
            d[s.GetOptionGroupLocaleID(Setting.kBuildGroup)] = ko ? "영역 채우기" : "Fill an area";
            d[s.GetOptionGroupLocaleID(Setting.kPaletteGroup)] = ko ? "필터로 리스트 만들기" : "Make a list from a filter";

            Opt(nameof(Setting.ActivateBuilder), "Activate builder", "Close the menu afterwards. Left click adds corners (on the road centre line or curb is fine); click the first corner to close the area. Left click then builds the preview, right click goes back. The area is kept while you change options here; press this again to return to it.",
                "빌더 켜기", "누른 뒤 메뉴를 닫으세요. 왼쪽 클릭으로 꼭짓점을 찍고(도로 중앙선이나 연석에 찍어도 됩니다), 첫 꼭짓점을 다시 클릭하면 영역이 닫힙니다. 그다음 왼쪽 클릭으로 미리보기를 짓고, 오른쪽 클릭으로 되돌립니다. 여기서 옵션을 바꾸는 동안에도 영역은 유지되니, 이 버튼을 다시 눌러 돌아가면 됩니다.");
            Opt(nameof(Setting.ActivePalette), "Placement list", "Which list fills the area. Lists are text files in ModsData/BlockBuilder/palettes.",
                "배치 리스트", "영역을 채울 리스트입니다. 리스트는 ModsData/BlockBuilder/palettes 폴더의 텍스트 파일입니다.");
            Opt(nameof(Setting.ReloadPalettes), "Reload lists", "Re-read the list files after editing them by hand.",
                "리스트 다시 읽기", "리스트 파일을 직접 고친 뒤 다시 읽어 옵니다.");
            Opt(nameof(Setting.Scope), "Fill with", "Whole list: every entry of the list. Checked only: just the entries checked in the panel.",
                "채울 건물", "리스트 전체: 리스트의 모든 항목을 씁니다. 고른 항목만: 패널에서 체크한 항목만 씁니다.");
            d[s.GetEnumValueLocaleID(ItemScope.All)] = ko ? "리스트 전체" : "Whole list";
            d[s.GetEnumValueLocaleID(ItemScope.Checked)] = ko ? "고른 항목만" : "Checked only";
            Opt(nameof(Setting.PlacedOnly), "Only Shift+clicked buildings", "On: build only what you placed with Shift+click; the rest stays empty.",
                "Shift+클릭한 것만 짓기", "켜면 Shift+클릭으로 직접 놓은 건물만 짓고 나머지는 비워 둡니다.");
            d[s.GetEnumValueLocaleID(FillMode.Fill)] = ko ? "채우기" : "Fill";
            d[s.GetEnumValueLocaleID(FillMode.FixedOnly)] = ko ? "지정만" : "Designated only";
            Opt(nameof(Setting.Selection), "Area selection", "Paint: brush the area freely (left drag paints, right drag or Ctrl+drag erases). Roads are walls: they are never painted and the edge hugs the curb, curves included. Draw: click the corners yourself.",
                "영역 선택 방식", "칠하기: 브러시로 자유롭게 칠합니다(왼쪽 드래그 칠하기, 오른쪽 드래그나 Ctrl+드래그 지우기). 도로는 벽처럼 칠해지지 않고, 경계는 곡선까지 연석에 맞춰집니다. 직접 그리기: 꼭짓점을 직접 찍습니다.");
            d[s.GetEnumValueLocaleID(AreaSelect.Paint)] = ko ? "칠하기" : "Paint";
            d[s.GetEnumValueLocaleID(AreaSelect.Draw)] = ko ? "직접 그리기" : "Draw";
            Opt(nameof(Setting.Density), "Fill density", "Relaxed leaves 4 m between lots. Dense searches every 2 m to fill odd gaps. Custom: set the gap and the overlaps yourself in the panel, in 0.5 m steps (overlaps need the Anarchy mod switched on).",
                "채움 강도", "여유: 필지 사이를 4m 띄웁니다. 촘촘: 2m 단위로 빈틈까지 찾습니다. 사용자 지정: 패널에서 간격과 겹침을 0.5m 단위로 직접 정합니다(겹침은 Anarchy를 켜야 적용).");
            d[s.GetEnumValueLocaleID(FillDensity.Relaxed)] = ko ? "여유" : "Relaxed";
            d[s.GetEnumValueLocaleID(FillDensity.Normal)] = ko ? "보통" : "Normal";
            d[s.GetEnumValueLocaleID(FillDensity.Dense)] = ko ? "촘촘" : "Dense";
            d[s.GetEnumValueLocaleID(FillDensity.Custom)] = ko ? "사용자 지정" : "Custom";
            Opt(nameof(Setting.OverlapSide), "Overlap left/right (m)", "Lots may overlap neighbours and existing buildings by this much at each side. Never roads. Applied only while the Anarchy mod is on.",
                "좌우 겹침 (m)", "필지가 양옆으로 이만큼 이웃 건물과 겹칠 수 있습니다. 도로와는 겹치지 않습니다. Anarchy 모드가 켜져 있을 때만 적용됩니다.");
            Opt(nameof(Setting.OverlapBack), "Overlap front/back (m)", "Lots may overlap by this much at the back (the front stays on the road), e.g. back-to-back rows in a shallow block. Applied only while the Anarchy mod is on.",
                "상하 겹침 (m)", "필지 뒤쪽이 이만큼 겹칠 수 있습니다 (앞면은 도로에 붙은 채). 얕은 블록에서 등을 맞댄 두 줄을 넣을 때 씁니다. Anarchy 모드가 켜져 있을 때만 적용됩니다.");
            Opt(nameof(Setting.GenerateRoads), "Generate streets", "Add internal streets so deep areas get road frontage. Street ends join existing roads they reach.",
                "내부 도로 생성", "깊은 영역에도 건물이 도로에 접하도록 내부 도로를 추가합니다. 도로 끝은 닿는 기존 도로에 연결됩니다.");
            Opt(nameof(Setting.NewRoadName), "Street road type", "Road prefab name for generated streets (see catalog.tsv, type=road).",
                "생성 도로 종류", "생성할 도로의 프리팹 이름입니다 (catalog.tsv의 type=road 참고).");
            Opt(nameof(Setting.MaxBlockLength), "Max block length", "Generated streets longer than this get cross streets.",
                "최대 블록 길이", "생성 도로가 이보다 길면 가로지르는 도로를 추가합니다.");
            Opt(nameof(Setting.FillInterior), "Fill interior", "Also place items that need no road (plazas, statues) in leftover interior space.",
                "내부 채우기", "도로가 필요 없는 항목(광장, 조형물 등)을 남은 안쪽 공간에도 배치합니다.");
            Opt(nameof(Setting.Shuffle), "Shuffle layout", "Pick a different random arrangement.",
                "배치 섞기", "다른 무작위 배치로 바꿉니다.");

            Opt(nameof(Setting.FilterCategory), "Category", "Use category. 'Other' means any.", "용도", "건물 용도입니다. '기타'는 전체를 뜻합니다.");
            Opt(nameof(Setting.FilterTheme), "Theme", "Region prefixes separated by commas, e.g. EU, NA. Empty means any.", "테마", "쉼표로 구분한 지역 접두어입니다 (예: EU, NA). 비우면 전체입니다.");
            Opt(nameof(Setting.FilterText), "Name contains", "Matches the prefab or zone name.", "이름 검색", "프리팹 이름이나 구역 이름에서 찾습니다.");
            Opt(nameof(Setting.FilterMaxWidth), "Max lot width (cells)", "One cell is 8 m.", "최대 필지 폭 (칸)", "한 칸은 8m입니다.");
            Opt(nameof(Setting.FilterMaxDepth), "Max lot depth (cells)", "One cell is 8 m.", "최대 필지 깊이 (칸)", "한 칸은 8m입니다.");
            Opt(nameof(Setting.FilterSource), "Source", "Vanilla, mod assets, or both.", "출처", "기본 에셋, 모드 에셋, 또는 둘 다입니다.");
            Opt(nameof(Setting.FilterIncludeSignature), "Include signature buildings", "", "시그니처 건물 포함", "");
            Opt(nameof(Setting.NewPaletteName), "List name", "Saved as a text file; an existing list with the same name is replaced.", "리스트 이름", "텍스트 파일로 저장됩니다. 같은 이름의 리스트는 덮어씁니다.");
            Opt(nameof(Setting.CreatePalette), "Create list from filter", "Saves every matching prefab (up to 300) with weight 1.", "필터로 리스트 만들기", "조건에 맞는 프리팹을 최대 300개까지 가중치 1로 저장합니다.");

            foreach (ItemCategory c in Enum.GetValues(typeof(ItemCategory)))
            {
                d[s.GetEnumValueLocaleID(c)] = ko ? CategoryKo(c) : c.ToString();
            }

            d[s.GetEnumValueLocaleID(SourceFilter.All)] = ko ? "전체" : "All";
            d[s.GetEnumValueLocaleID(SourceFilter.Vanilla)] = ko ? "기본" : "Vanilla";
            d[s.GetEnumValueLocaleID(SourceFilter.Mods)] = ko ? "모드" : "Mods";
        }

        internal static string CategoryKo(ItemCategory c)
        {
            switch (c)
            {
                case ItemCategory.ResidentialLow: return "주거 저밀도";
                case ItemCategory.ResidentialMedium: return "주거 중밀도";
                case ItemCategory.ResidentialHigh: return "주거 고밀도";
                case ItemCategory.ResidentialMixed: return "주거 혼합";
                case ItemCategory.ResidentialLowRent: return "저임대 주택";
                case ItemCategory.CommercialLow: return "상업 저밀도";
                case ItemCategory.CommercialHigh: return "상업 고밀도";
                case ItemCategory.Office: return "사무";
                case ItemCategory.Industrial: return "산업";
                case ItemCategory.SpecializedIndustry: return "특화 산업";
                case ItemCategory.Service: return "서비스";
                case ItemCategory.Park: return "공원";
                case ItemCategory.Parking: return "주차장";
                default: return "기타 (전체)";
            }
        }
    }
}
