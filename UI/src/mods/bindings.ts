import { bindValue, trigger } from "cs2/api";

/** Must match BuilderUISystem.kGroup on the C# side. */
const GROUP = "blockBuilder";

export interface BuilderState {
    active: boolean;
    planned: boolean;
    corners: number;
    designated: number;
    buildings: number;
    streets: number;
    palette: number;
    designate: number;
    scope: number;
    fillRest: boolean;
    snapOff: number;
    selection: number;
    picked: boolean;
    blocks: number;
    brush: number;
    density: number;
    overlapSide: number;
    overlapBack: number;
    customGap: number;
    anarchyInstalled: boolean;
    anarchyOn: boolean;
    generateRoads: boolean;
    fillInterior: boolean;
    blockLength: number;
    road: string;
    status: string;
    ko: boolean;
    warnings: string[];
}

export interface PaletteInfo {
    name: string;
    count: number;
}

export interface EntryInfo {
    name: string;
    size: string;
    loaded: boolean;
    checked: boolean;
}

export interface Lists {
    palettes: PaletteInfo[];
    checkedCount: number;
    entries: EntryInfo[];
}

/** Game.Tools.Snap bits used by the builder. */
export const SNAP_CURB = 1; // ExistingGeometry
export const SNAP_CELL = 2; // CellLength
export const SNAP_ANGLE = 4; // StraightDirection

export interface SearchResult {
    name: string;
    size: string;
    inList: boolean;
}

export interface Search {
    total: number;
    category: number;
    theme: string;
    width: number;
    widthOp: number;
    depth: number;
    depthOp: number;
    text: string;
    maxAddAll: number;
    results: SearchResult[];
    categories: { id: number; label: string }[];
    themes: string[];
}

/** FillDensity values on the C# side. */
export const DENSITY = { normal: 0, relaxed: 1, dense: 2, custom: 3 };

export const searchBinding = bindValue<string>(GROUP, "search", "{}");

const emptySearch: Search = { total: 0, category: -1, theme: "", width: 0, widthOp: 1, depth: 0, depthOp: 1, text: "", maxAddAll: 300, results: [], categories: [], themes: [] };

export function parseSearch(json: string): Search {
    try {
        return { ...emptySearch, ...JSON.parse(json) };
    } catch {
        return emptySearch;
    }
}

export const stateBinding = bindValue<string>(GROUP, "state", "{}");
export const listsBinding = bindValue<string>(GROUP, "lists", '{"palettes":[],"entries":[]}');

const emptyState: BuilderState = {
    active: false,
    planned: false,
    corners: 0,
    designated: 0,
    buildings: 0,
    streets: 0,
    palette: 0,
    designate: 0,
    scope: 0,
    fillRest: true,
    snapOff: 0,
    selection: 0,
    picked: false,
    blocks: 0,
    brush: 16,
    density: 0,
    overlapSide: 0,
    overlapBack: 0,
    customGap: 0,
    anarchyInstalled: false,
    anarchyOn: false,
    generateRoads: false,
    fillInterior: false,
    blockLength: 160,
    road: "",
    status: "",
    ko: false,
    warnings: [],
};

export function parseState(json: string): BuilderState {
    try {
        return { ...emptyState, ...JSON.parse(json) };
    } catch {
        return emptyState;
    }
}

export function parseLists(json: string): Lists {
    try {
        const v = JSON.parse(json);
        return { palettes: v.palettes ?? [], checkedCount: v.checkedCount ?? 0, entries: v.entries ?? [] };
    } catch {
        return { palettes: [], checkedCount: 0, entries: [] };
    }
}

export const actions = {
    toggle: () => trigger(GROUP, "toggle"),
    clearArea: () => trigger(GROUP, "clearArea"),
    shuffle: () => trigger(GROUP, "shuffle"),
    reloadLists: () => trigger(GROUP, "reloadLists"),
    setPalette: (i: number) => trigger(GROUP, "setPalette", i),
    setDesignate: (i: number) => trigger(GROUP, "setDesignate", i),
    setScope: (s: number) => trigger(GROUP, "setScope", s),
    setFillRest: (b: boolean) => trigger(GROUP, "setFillRest", b),
    toggleChecked: (i: number) => trigger(GROUP, "toggleChecked", i),
    checkAll: (b: boolean) => trigger(GROUP, "checkAll", b),
    toggleSnap: (bit: number) => trigger(GROUP, "toggleSnap", bit),
    setOverlapSide: (n: number) => trigger(GROUP, "setOverlapSide", n),
    setOverlapBack: (n: number) => trigger(GROUP, "setOverlapBack", n),
    setCustomGap: (n: number) => trigger(GROUP, "setCustomGap", n),
    /** Anarchy's own UI trigger (group "Anarchy"); only call when Anarchy is installed. */
    toggleAnarchy: () => trigger("Anarchy", "AnarchyToggled"),
    setSelection: (n: number) => trigger(GROUP, "setSelection", n),
    setBrush: (n: number) => trigger(GROUP, "setBrush", n),
    build: () => trigger(GROUP, "build"),
    setDensity: (n: number) => trigger(GROUP, "setDensity", n),
    newList: (name: string) => trigger(GROUP, "newList", name),
    newListFromChecked: (name: string) => trigger(GROUP, "newListFromChecked", name),
    duplicateList: () => trigger(GROUP, "duplicateList"),
    renameList: (name: string) => trigger(GROUP, "renameList", name),
    deleteList: () => trigger(GROUP, "deleteList"),
    removeItem: (i: number) => trigger(GROUP, "removeItem", i),
    removeItemByName: (name: string) => trigger(GROUP, "removeItemByName", name),
    clearItems: () => trigger(GROUP, "clearItems"),
    addItem: (name: string) => trigger(GROUP, "addItem", name),
    addResults: () => trigger(GROUP, "addResults"),
    setSearchText: (s: string) => trigger(GROUP, "setSearchText", s),
    setSearchCategory: (c: number) => trigger(GROUP, "setSearchCategory", c),
    setSearchTheme: (s: string) => trigger(GROUP, "setSearchTheme", s),
    setSearchWidth: (n: number) => trigger(GROUP, "setSearchWidth", n),
    setSearchWidthOp: (op: number) => trigger(GROUP, "setSearchWidthOp", op),
    setSearchDepth: (n: number) => trigger(GROUP, "setSearchDepth", n),
    setSearchDepthOp: (op: number) => trigger(GROUP, "setSearchDepthOp", op),
    setGenerateRoads: (b: boolean) => trigger(GROUP, "setGenerateRoads", b),
    setFillInterior: (b: boolean) => trigger(GROUP, "setFillInterior", b),
    setBlockLength: (n: number) => trigger(GROUP, "setBlockLength", n),
};
