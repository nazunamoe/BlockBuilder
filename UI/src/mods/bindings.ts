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
    overlap: number;
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
    overlap: 0,
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
    setOverlap: (n: number) => trigger(GROUP, "setOverlap", n),
    setGenerateRoads: (b: boolean) => trigger(GROUP, "setGenerateRoads", b),
    setFillInterior: (b: boolean) => trigger(GROUP, "setFillInterior", b),
    setBlockLength: (n: number) => trigger(GROUP, "setBlockLength", n),
};
