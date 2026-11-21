import { useValue } from "cs2/api";
import { getModule } from "cs2/modding";
import { Scrollable } from "cs2/ui";
import { useEffect, useRef, useState } from "react";
import { actions, BuilderState, DENSITY, EntryInfo, Lists, listsBinding, parseLists, parseSearch, parseState, Search, searchBinding, SNAP_ANGLE, SNAP_CELL, SNAP_CURB, stateBinding } from "mods/bindings";
import { T } from "mods/texts";
import { Check, Glyph, loadJson, NamePrompt, saveJson, Segmented, RangeFilter, Stepper, Switch, TextField, useDraggable } from "mods/ui-parts";
import styles from "./builder-panel.module.scss";

/** The game's own tool options toolbar (snapping buttons), shown inside the builder panel. */
const MouseToolOptions: any = (() => {
    try {
        return getModule("game-ui/game/components/tool-options/mouse-tool-options/mouse-tool-options.tsx", "MouseToolOptions");
    } catch {
        return undefined;
    }
})();

/** Landscape window: status and actions on the left, tabs on the right; adding buildings uses the full width. */
const PANEL_WIDTH = 820;
/** Tallest content area in rem, so the window stays short on any screen. */
const PANE_MAX_REM = 520;
/** Tabs row above the scrolling body, in rem (measured space the body gives up). */
const TABS_REM = 40;
const POS_KEY = "blockBuilder.panelPos";
const TAB_KEY = "blockBuilder.tab";
const VIEW_KEY = "blockBuilder.addView";
const SIZE_KEY = "blockBuilder.listSize";
const DEFAULT_POS = { x: 12, y: 120 };

type Tr = (k: string) => string;

/** What the hover preview shows: the game's name, the prefab id, size and picture. */
type Shown = { name: string; label: string; size: string; thumb: string };
type Prompt = "" | "new" | "fromChecked" | "rename" | "delete";
type Tab = "buildings" | "settings";

// ---------------------------------------------------------------- status

function StatusCard({ state, t }: { state: BuilderState; t: Tr }) {
    const current = state.planned ? 1 : 0;
    const steps = [t("stepDraw"), t("stepPreview"), t("stepBuild")];
    return (
        <div className={styles.card}>
            <div className={styles.selectRow}>
                <Segmented
                    value={state.selection}
                    onChange={actions.setSelection}
                    options={[
                        { value: 0, label: t("selectBlock") },
                        { value: 1, label: t("selectDraw") },
                    ]}
                />
                {state.selection === 0 && <Stepper label={t("brush")} value={state.brush} unit="m" step={4} onChange={actions.setBrush} />}
                {state.selection === 0 && <Switch value={state.crossRoads} onChange={actions.setCrossRoads} label={t("crossRoads")} hint={t("crossRoadsHint")} />}
            </div>
            <div className={styles.steps}>
                {steps.map((s, i) => (
                    <div key={i} title={s} className={`${styles.stepChip} ${i === current ? styles.stepCurrent : i < current ? styles.stepDone : ""}`}>
                        <span className={styles.stepNumber}>{i + 1}</span>
                        {i === current ? s : ""}
                    </div>
                ))}
            </div>
            {state.planned ? (
                <>
                    <div className={styles.stats}>
                        {state.picked && (
                            <div className={styles.stat}>
                                <b>{state.blocks}</b>
                                <span>{t("blocksSelected")}</span>
                            </div>
                        )}
                        <div className={styles.stat}>
                            <b>{state.buildings}</b>
                            <span>{t("buildings")}</span>
                        </div>
                        <div className={styles.stat}>
                            <b>{state.streets}</b>
                            <span>{t("streets")}</span>
                        </div>
                        <div className={styles.stat}>
                            <b>{state.designated}</b>
                            <span>{t("designated")}</span>
                        </div>
                    </div>
                    <div className={styles.hint}>{t(state.picked ? "plannedHintPicked" : "plannedHint")}</div>
                </>
            ) : (
                <div className={styles.hint}>
                    {state.selection === 0 ? t("blockHint") : state.corners === 0 ? t("drawStart") : `${t("drawMore")} (${state.corners} ${t("corners")})`}
                </div>
            )}
            {state.status !== "" && <div className={styles.status}>{state.status}</div>}
            {state.warnings.map((w, i) => (
                <div key={i} className={styles.warning}>
                    {w}
                </div>
            ))}
        </div>
    );
}

/** Build and its helpers, stacked under the status in the left column. */
function Actions({ state, t }: { state: BuilderState; t: Tr }) {
    const [confirmUndo, setConfirmUndo] = useState(false);
    return (
        <div className={styles.actions}>
            {confirmUndo && (
                <div className={styles.prompt}>
                    <div className={styles.promptLabel}>{t("confirmUndo")}</div>
                    <div className={styles.promptRow}>
                        <button
                            className={`${styles.chip} ${styles.chipDanger}`}
                            onClick={() => {
                                actions.undoBuild();
                                setConfirmUndo(false);
                            }}
                        >
                            {t("undoBuild")}
                        </button>
                        <button className={styles.chip} onClick={() => setConfirmUndo(false)}>
                            {t("cancel")}
                        </button>
                    </div>
                </div>
            )}
            <button className={`${styles.actionButton} ${styles.actionWide} ${styles.primary}`} disabled={!state.planned || state.buildings + state.streets === 0} onClick={actions.build}>
                {t("build")}
            </button>
            <div className={styles.actionGrid}>
                <button className={`${styles.actionButton} ${styles.actionHalf}`} disabled={!state.planned} onClick={actions.optimize} title={t("optimizeHint")}>
                    {t("optimize")}
                </button>
                <button className={`${styles.actionButton} ${styles.actionHalf}`} onClick={actions.shuffle}>
                    {t("shuffle")}
                </button>
                <button className={`${styles.actionButton} ${styles.actionHalf}`} onClick={actions.clearArea}>
                    {t("clear")}
                </button>
                <button
                    className={`${styles.actionButton} ${styles.actionHalf}`}
                    disabled={state.undoCount === 0 || state.undoing}
                    title={t("undoHint")}
                    onClick={() => {
                        if (state.planned || state.buildings + state.streets > 0) {
                            setConfirmUndo(true);
                        } else {
                            actions.undoBuild();
                        }
                    }}
                >
                    {t("undoBuild")}
                </button>
            </div>
        </div>
    );
}

// ---------------------------------------------------------------- buildings tab

/** Current list as one button; opening it shows every list and the list actions. */
function ListPicker({ state, lists, t }: { state: BuilderState; lists: Lists; t: Tr }) {
    const [open, setOpen] = useState(false);
    const [prompt, setPrompt] = useState<Prompt>("");
    const current = lists.palettes[state.palette];
    const close = () => setPrompt("");

    return (
        <div className={styles.picker}>
            <button className={`${styles.pickerButton} ${open ? styles.pickerOpen : ""}`} onClick={() => setOpen(!open)}>
                <span className={styles.pickerLabel}>{t("currentList")}</span>
                <span className={styles.name}>{current ? current.name : t("noListSelected")}</span>
                {current && <span className={styles.badge}>{current.count}</span>}
                <Glyph className={`${styles.chevron} ${open ? "" : styles.chevronClosed}`} src="Media/Glyphs/StrokeArrowDown.svg" />
            </button>

            {open && (
                <div className={styles.pickerMenu}>
                    {lists.palettes.length === 0 ? (
                        <div className={styles.empty}>{t("noLists")}</div>
                    ) : (
                        <Scrollable className={styles.paletteList} vertical>
                            {lists.palettes.map((p, i) => (
                                <button
                                    key={i}
                                    className={`${styles.row} ${i === state.palette ? styles.rowOn : ""}`}
                                    onClick={() => {
                                        actions.setPalette(i);
                                        setOpen(false);
                                    }}
                                >
                                    <span className={styles.name}>{p.name}</span>
                                    <span className={styles.badge}>{p.count}</span>
                                </button>
                            ))}
                        </Scrollable>
                    )}

                    <div className={styles.toolbar}>
                        <button className={styles.chip} onClick={() => setPrompt("new")}>
                            {t("newList")}
                        </button>
                        <button className={styles.chip} disabled={lists.checkedCount === 0} onClick={() => setPrompt("fromChecked")}>
                            {t("fromChecked")}
                        </button>
                        <button className={styles.chip} disabled={!current} onClick={actions.duplicateList}>
                            {t("duplicate")}
                        </button>
                        <button className={styles.chip} disabled={!current} onClick={() => setPrompt("rename")}>
                            {t("rename")}
                        </button>
                        <button className={`${styles.chip} ${styles.chipDangerText}`} disabled={!current} onClick={() => setPrompt("delete")}>
                            {t("deleteList")}
                        </button>
                        <button className={styles.chip} onClick={actions.reloadLists}>
                            {t("reload")}
                        </button>
                    </div>

                    {prompt === "new" && <NamePrompt label={t("promptNew")} initial={t("defaultName")} okLabel={t("ok")} cancelLabel={t("cancel")} onCancel={close} onOk={(n) => (actions.newList(n), close())} />}
                    {prompt === "fromChecked" && (
                        <NamePrompt label={t("promptFromChecked")} initial={t("defaultName")} okLabel={t("ok")} cancelLabel={t("cancel")} onCancel={close} onOk={(n) => (actions.newListFromChecked(n), close())} />
                    )}
                    {prompt === "rename" && current && (
                        <NamePrompt label={t("promptRename")} initial={current.name} okLabel={t("ok")} cancelLabel={t("cancel")} onCancel={close} onOk={(n) => (actions.renameList(n), close())} />
                    )}
                    {prompt === "delete" && current && (
                        <div className={styles.prompt}>
                            <div className={styles.promptLabel}>
                                {`${t("confirmDelete")} ${current.name}`}
                            </div>
                            <div className={styles.promptRow}>
                                <button className={`${styles.chip} ${styles.chipDanger}`} onClick={() => (actions.deleteList(), close())}>
                                    {t("deleteList")}
                                </button>
                                <button className={styles.chip} onClick={close}>
                                    {t("cancel")}
                                </button>
                            </div>
                        </div>
                    )}
                </div>
            )}
        </div>
    );
}

/** The buildings of the current list: pick one to Shift+click, tick to fill with, remove with the X. */
/** Building picture from the game's own thumbnails; a blank tile when it has none. */
function Thumb({ src, className }: { src: string; className: string }) {
    const [bad, setBad] = useState(false);
    useEffect(() => setBad(false), [src]);
    return src && !bad ? <img className={className} src={src} onError={() => setBad(true)} /> : <div className={`${className} ${styles.thumbBlank}`} />;
}

/** Larger picture of the hovered row (fixed height, so the list does not jump). */
function PreviewBox({ item, t }: { item: Shown | null; t: Tr }) {
    return (
        <div className={styles.previewBox}>
            {item ? (
                <>
                    <Thumb src={item.thumb} className={styles.previewImage} />
                    <div className={styles.previewText}>
                        <span className={styles.previewName}>{item.label || item.name}</span>
                        {item.label && item.label !== item.name && <span className={styles.previewPrefab}>{item.name}</span>}
                        <span className={styles.badge}>{item.size}</span>
                    </div>
                </>
            ) : (
                <span className={styles.note}>{t("previewHint")}</span>
            )}
        </div>
    );
}

type View = "grid" | "list";

/** Picture grid or list, remembered for both the list and the add view. */
function useView(): [View, (v: View) => void] {
    const [view, setViewState] = useState<View>(() => (loadJson(VIEW_KEY, { view: "grid" }).view === "list" ? "list" : "grid"));
    const setView = (v: View) => {
        setViewState(v);
        saveJson(VIEW_KEY, { view: v });
    };
    return [view, setView];
}

function ViewButtons({ view, setView, t }: { view: View; setView: (v: View) => void; t: Tr }) {
    return (
        <>
            <button className={`${styles.chip} ${view === "grid" ? styles.chipOn : ""}`} onClick={() => setView("grid")}>
                {t("viewGrid")}
            </button>
            <button className={`${styles.chip} ${view === "list" ? styles.chipOn : ""}`} onClick={() => setView("list")}>
                {t("viewList")}
            </button>
        </>
    );
}

function ListContents({ state, lists, t, onAdd, gridMax, rem }: { state: BuilderState; lists: Lists; t: Tr; onAdd: () => void; gridMax: number; rem: number }) {
    const [confirmClear, setConfirmClear] = useState(false);
    // Hover previews a building; a click selects it for the weight and limit card below.
    const [hover, setHover] = useState<number | null>(null);
    const [selectedRaw, setSelected] = useState<number | null>(null);
    const [view, setView] = useView();
    const onlyChecked = state.scope === 1;
    const selected = selectedRaw !== null && selectedRaw < lists.entries.length ? selectedRaw : null;
    const shownIndex = hover !== null && hover < lists.entries.length ? hover : selected;
    const shown = shownIndex !== null ? lists.entries[shownIndex] : null;
    const hasList = lists.palettes[state.palette] !== undefined;

    // Show only the buildings of one lot size (view only: indices stay those of the whole list).
    const [range, setRangeState] = useState(() => {
        const v = loadJson(SIZE_KEY, { minW: 0, maxW: 0, minD: 0, maxD: 0 });
        return { minW: v.minW | 0, maxW: v.maxW | 0, minD: v.minD | 0, maxD: v.maxD | 0 };
    });
    const [sizeOpen, setSizeOpen] = useState(range.minW + range.maxW + range.minD + range.maxD > 0);
    const setRange = (patch: Partial<typeof range>) => {
        const next = { ...range, ...patch };
        // The smallest never passes the largest: the other end follows.
        if (next.maxW > 0 && next.minW > next.maxW) {
            patch.minW !== undefined ? (next.maxW = next.minW) : (next.minW = next.maxW);
        }
        if (next.maxD > 0 && next.minD > next.maxD) {
            patch.minD !== undefined ? (next.maxD = next.minD) : (next.minD = next.maxD);
        }
        setRangeState(next);
        saveJson(SIZE_KEY, next);
    };
    const limited = range.minW + range.maxW + range.minD + range.maxD > 0;
    const visible = lists.entries
        .map((e, i) => ({ e, i }))
        .filter(({ e }) => {
            if (!limited) {
                return true;
            }
            const m = /^(\d+)x(\d+)$/.exec(e.size);
            if (!m) {
                return false; // not loaded: size unknown
            }
            const w = parseInt(m[1], 10);
            const d = parseInt(m[2], 10);
            return w >= range.minW && (range.maxW === 0 || w <= range.maxW) && d >= range.minD && (range.maxD === 0 || d <= range.maxD);
        });
    const listHeight = Math.max(100, gridMax - (sizeOpen ? 56 * rem : 0));

    return (
        <>
            <Switch
                value={onlyChecked}
                onChange={(v) => actions.setScope(v ? 1 : 0)}
                label={`${t("onlyChecked")}${onlyChecked ? ` (${lists.checkedCount})` : ""}`}
                hint={t("onlyCheckedHint")}
            />

            <div className={styles.listHeader}>
                <span className={styles.note}>{t("placeHint")}</span>
            </div>

            <div className={styles.toolbar}>
                <button className={`${styles.chip} ${styles.chipPrimary}`} disabled={!hasList} onClick={onAdd}>
                    + {t("addBuildings")}
                </button>
                <button className={`${styles.chip} ${styles.chipDangerText}`} disabled={lists.entries.length === 0} onClick={() => setConfirmClear(true)}>
                    {t("clearItems")}
                </button>
                {onlyChecked && (
                    <>
                        {/* With a size filter on, "all" picks exactly the shown buildings and "none" clears only those. */}
                        <button
                            className={styles.chip}
                            disabled={limited && visible.length === 0}
                            onClick={() => (limited ? actions.checkMany(visible.map((v) => v.i), 2) : actions.checkAll(true))}
                        >
                            {t("checkAll")}
                        </button>
                        <button
                            className={styles.chip}
                            disabled={limited && visible.length === 0}
                            onClick={() => (limited ? actions.checkMany(visible.map((v) => v.i), 0) : actions.checkAll(false))}
                        >
                            {t("checkNone")}
                        </button>
                    </>
                )}
                <button className={`${styles.chip} ${limited ? styles.chipOn : ""}`} onClick={() => setSizeOpen(!sizeOpen)}>
                    {t("sizeFilter")}
                    {limited ? ` (${visible.length}/${lists.entries.length})` : ""}
                </button>
                <span className={styles.toolbarSpacer} />
                <ViewButtons view={view} setView={setView} t={t} />
            </div>

            {sizeOpen && (
                <div className={styles.sizeBar}>
                    <div className={styles.sizeBarPart}>
                        <RangeFilter label={t("lotWidth")} min={range.minW} max={range.maxW} minLabel={t("sizeMin")} maxLabel={t("sizeMax")} anyLabel={t("anySize")} onMin={(v) => setRange({ minW: v })} onMax={(v) => setRange({ maxW: v })} />
                    </div>
                    <div className={styles.sizeBarPart}>
                        <RangeFilter label={t("lotDepth")} min={range.minD} max={range.maxD} minLabel={t("sizeMin")} maxLabel={t("sizeMax")} anyLabel={t("anySize")} onMin={(v) => setRange({ minD: v })} onMax={(v) => setRange({ maxD: v })} />
                    </div>
                    <button className={styles.chip} disabled={!limited} onClick={() => setRange({ minW: 0, maxW: 0, minD: 0, maxD: 0 })}>
                        {t("sizeReset")}
                    </button>
                </div>
            )}

            {confirmClear && lists.entries.length > 0 && (
                <div className={styles.prompt}>
                    <div className={styles.promptLabel}>
                        {`${t("confirmClearItems")} ${lists.palettes[state.palette]?.name ?? ""} (${lists.entries.length})`}
                    </div>
                    <div className={styles.promptRow}>
                        <button
                            className={`${styles.chip} ${styles.chipDanger}`}
                            onClick={() => {
                                actions.clearItems();
                                setConfirmClear(false);
                            }}
                        >
                            {t("clearItems")}
                        </button>
                        <button className={styles.chip} onClick={() => setConfirmClear(false)}>
                            {t("cancel")}
                        </button>
                    </div>
                </div>
            )}

            {lists.entries.length === 0 ? (
                <div className={styles.empty}>{hasList ? t("itemsEmpty") : t("noListSelected")}</div>
            ) : visible.length === 0 ? (
                <div className={styles.empty}>{t("sizeNone")}</div>
            ) : (
                <div className={styles.listPanes}>
                    <div className={styles.listGrid}>
            {view === "grid" ? (
                <Scrollable className={styles.gridList} style={{ height: `${listHeight}px`, maxHeight: "none" }} vertical>
                    <div className={styles.grid}>
                        {visible.map(({ e, i }) => (
                            <div
                                key={i}
                                className={`${styles.tile} ${styles.tileSmall} ${selected === i ? styles.tileSelected : ""} ${onlyChecked && e.checked ? styles.tileOn : ""} ${e.loaded ? "" : styles.dim}`}
                                onMouseEnter={() => setHover(i)}
                                onMouseLeave={() => setHover(null)}
                                onClick={() => setSelected(i)}
                            >
                                <div className={styles.tileImageWrap}>
                                    <Thumb src={e.thumb} className={`${styles.tileImage} ${styles.tileImageSmall}`} />
                                    <span className={styles.tileSize}>{e.loaded ? e.size : t("notLoaded")}</span>
                                    <MixBadge entry={e} className={styles.tileMix} />
                                    {onlyChecked && (
                                        <button
                                            className={`${styles.tileCheck} ${e.checked ? "" : styles.tileCheckOff}`}
                                            onClick={(ev) => {
                                                ev.stopPropagation();
                                                actions.toggleChecked(i);
                                            }}
                                        >
                                            {e.checked && <Glyph className={styles.tileCheckGlyph} src="Media/Glyphs/Checkmark.svg" />}
                                        </button>
                                    )}
                                    <button
                                        className={styles.tileRemove}
                                        title={t("removeFromList")}
                                        onClick={(ev) => {
                                            ev.stopPropagation();
                                            actions.removeItem(i);
                                        }}
                                    >
                                        <Glyph className={styles.removeGlyph} src="Media/Glyphs/Close.svg" />
                                    </button>
                                </div>
                                <span className={styles.tileName}>{e.label || e.name}</span>
                            </div>
                        ))}
                    </div>
                </Scrollable>
            ) : (
                <Scrollable className={styles.itemList} style={{ height: `${listHeight}px`, maxHeight: "none" }} vertical>
                    {visible.map(({ e, i }) => (
                        <div
                            key={i}
                            className={`${styles.itemRow} ${selected === i ? styles.itemRowSelected : ""} ${e.loaded ? "" : styles.dim}`}
                            onMouseEnter={() => setHover(i)}
                            onMouseLeave={() => setHover(null)}
                            onClick={() => setSelected(i)}
                        >
                            {onlyChecked && <Check checked={e.checked} onClick={() => actions.toggleChecked(i)} />}
                            <div className={styles.itemName}>
                                <Thumb src={e.thumb} className={styles.thumb} />
                                <span className={styles.name}>{e.label || e.name}</span>
                                <MixBadge entry={e} className={styles.badge} />
                                <span className={styles.badge}>{e.loaded ? e.size : t("notLoaded")}</span>
                            </div>
                            <button
                                className={styles.removeButton}
                                title={t("removeFromList")}
                                onClick={(ev) => {
                                    ev.stopPropagation();
                                    actions.removeItem(i);
                                }}
                            >
                                <Glyph className={styles.removeGlyph} src="Media/Glyphs/Close.svg" />
                            </button>
                        </div>
                    ))}
                </Scrollable>
            )}
                    </div>
                    <div className={styles.listDetail}>
                        <EntryDetail entry={shown} index={shownIndex} editable={shownIndex === selected} t={t} />
                    </div>
                </div>
            )}
        </>
    );
}
/** "x3" when picked more often, "max 2" when limited; nothing at the defaults. */
function MixBadge({ entry, className }: { entry: EntryInfo; className: string }) {
    if (entry.weight <= 1 && entry.max <= 0) {
        return null;
    }
    return (
        <span className={`${className} ${styles.mixBadge}`}>
            {entry.weight > 1 ? `x${entry.weight}` : ""}
            {entry.weight > 1 && entry.max > 0 ? " " : ""}
            {entry.max > 0 ? `≤${entry.max}` : ""}
        </span>
    );
}

/** The hovered or selected building, with its weight and limit when it is the selected one. */
function EntryDetail({ entry, index, editable, t }: { entry: EntryInfo | null; index: number | null; editable: boolean; t: Tr }) {
    if (!entry || index === null) {
        return (
            <div className={styles.previewBox}>
                <span className={styles.note}>{t("selectHint")}</span>
            </div>
        );
    }
    return (
        <div className={styles.detailCard}>
            <div className={styles.detailTop}>
                <Thumb src={entry.thumb} className={styles.detailImage} />
                <div className={styles.previewText}>
                    <span className={styles.previewName}>{entry.label || entry.name}</span>
                    {entry.label && entry.label !== entry.name && <span className={styles.previewPrefab}>{entry.name}</span>}
                    <span className={styles.badge}>{entry.loaded ? entry.size : t("notLoaded")}</span>
                </div>
            </div>
            {editable ? (
                <>
                    <Stepper label={t("weight")} value={entry.weight} unit="" step={1} display={`x${entry.weight}`} onChange={(v) => actions.setItemWeight(index, v)} />
                    <Stepper
                        label={t("maxCount")}
                        value={entry.max}
                        unit=""
                        step={1}
                        display={entry.max > 0 ? `${entry.max}` : t("noLimit")}
                        onChange={(v) => actions.setItemMax(index, v)}
                    />
                    <span className={styles.note}>{t("weightHint")}</span>
                </>
            ) : (
                <span className={styles.note}>{t("clickToEdit")}</span>
            )}
        </div>
    );
}

/** Star toggle for favourites; does not reach the tile's own click. */
function Star({ on, className, onToggle, t }: { on: boolean; className: string; onToggle: () => void; t: Tr }) {
    return (
        <button
            className={`${styles.star} ${on ? styles.starOn : ""} ${className}`}
            title={on ? t("unstar") : t("star")}
            onClick={(ev) => {
                ev.stopPropagation();
                onToggle();
            }}
        >
            <Glyph className={styles.starGlyph} src={on ? "Media/Glyphs/StarFilled.svg" : "Media/Glyphs/StarOutline.svg"} />
        </button>
    );
}

/** One use button: the game's zoning icon, the name and how many buildings it gives. */
function UseTile({ label, icon, count, on, onClick }: { label: string; icon: string; count: number; on: boolean; onClick: () => void }) {
    return (
        <button className={`${styles.useTile} ${on ? styles.useTileOn : ""} ${count === 0 && !on ? styles.dim : ""}`} onClick={onClick}>
            <img className={styles.useIcon} src={icon} />
            <span className={styles.useLabel}>{label}</span>
            <span className={styles.useCount}>{count}</span>
        </button>
    );
}

/** Catalog search that adds to the current list; replaces the list view until Back. */
function AddBuildings({ search, lists, state, t, onBack, paneMax, rem }: { search: Search; lists: Lists; state: BuilderState; t: Tr; onBack: () => void; paneMax: number; rem: number }) {
    // Start from the text the C# side still filters by (it survives a UI reload).
    const [query, setQuery] = useState<string | null>(null);
    const [hover, setHover] = useState<Shown | null>(null);
    const onQuery = (s: string) => {
        setQuery(s);
        actions.setSearchText(s);
    };
    const tooMany = search.total > search.maxAddAll;
    const current = lists.palettes[state.palette];
    const openGroup = search.groups.find((g) => g.id === search.group);
    const [view, setView] = useView();
    const [confirmRemove, setConfirmRemove] = useState(false);

    return (
        <div className={styles.addLayout}>
            <div className={styles.addFilters}>
            <div className={styles.subHeader}>
                <button className={styles.backButton} onClick={onBack}>
                    <Glyph className={styles.glyph} src="Media/Glyphs/ArrowLeft.svg" />
                    {t("back")}
                </button>
                <span className={styles.note}>
                    {t("addTo")}: <b>{current?.name ?? "-"}</b>
                </span>
            </div>
            <Scrollable className={styles.addFilterScroll} style={{ height: `${Math.max(120, paneMax - 64 * rem)}px` }} vertical>
            <div className={styles.fieldLabel}>{t("searchName")}</div>
            <TextField value={query ?? search.text} onChange={onQuery} />

            <div className={styles.shortcutRow}>
                {[
                    { id: "favorites", label: t("favorites"), glyph: "Media/Glyphs/StarFilled.svg", count: search.favoriteCount },
                    { id: "recent", label: t("recent"), glyph: "Media/Glyphs/ArrowCircular.svg", count: search.recentCount },
                    { id: "inlist", label: t("inThisList"), glyph: "Media/Glyphs/Checkmark.svg", count: search.inListCount },
                ].map((s) => (
                    <button
                        key={s.id}
                        className={`${styles.subChip} ${search.group === s.id ? styles.subChipOn : ""}`}
                        onClick={() => actions.setSearchGroup(search.group === s.id ? "" : s.id)}
                    >
                        <Glyph className={styles.shortcutGlyph} src={s.glyph} />
                        {s.label}
                        <span className={styles.subCount}>{s.count}</span>
                    </button>
                ))}
            </div>

            <div className={styles.fieldLabel}>{t("use")}</div>
            <div className={styles.useRow}>
                <UseTile label={t("anyCategory")} icon="Media/Game/Icons/Zones.svg" count={search.anyCount} on={search.group === ""} onClick={() => actions.setSearchGroup("")} />
                {search.groups.map((g) => (
                    <UseTile key={g.id} label={g.label} icon={g.icon} count={g.count} on={openGroup?.id === g.id} onClick={() => actions.setSearchGroup(g.id)} />
                ))}
            </div>
            {openGroup && openGroup.subs.length > 0 && (
                <div className={styles.subRow}>
                    <button className={`${styles.subChip} ${search.category < 0 ? styles.subChipOn : ""}`} onClick={() => actions.setSearchCategory(-1)}>
                        {t("allOf")} {openGroup.label}
                        <span className={styles.subCount}>{openGroup.count}</span>
                    </button>
                    {openGroup.subs.map((s) => (
                        <button
                            key={s.id}
                            className={`${styles.subChip} ${search.category === s.id ? styles.subChipOn : ""}`}
                            disabled={s.count === 0 && search.category !== s.id}
                            onClick={() => actions.setSearchCategory(s.id)}
                        >
                            <img className={styles.subIcon} src={s.icon} />
                            {s.label}
                            <span className={styles.subCount}>{s.count}</span>
                        </button>
                    ))}
                </div>
            )}

            {search.themes.length > 0 && (
                <div className={styles.chipWrap}>
                    <span className={styles.fieldLabelInline}>{t("theme")}</span>
                    <button className={`${styles.chip} ${search.theme === "" ? styles.chipOn : ""}`} onClick={() => actions.setSearchTheme("")}>
                        {t("anyTheme")}
                    </button>
                    {search.themes.map((th) => (
                        <button key={th} className={`${styles.chip} ${search.theme === th ? styles.chipOn : ""}`} onClick={() => actions.setSearchTheme(th)}>
                            {th}
                        </button>
                    ))}
                </div>
            )}

            <div className={styles.chipWrap}>
                <span className={styles.fieldLabelInline}>{t("level")}</span>
                {[0, 1, 2, 3, 4, 5].map((n) => (
                    <button key={n} className={`${styles.chip} ${search.level === n ? styles.chipOn : ""}`} onClick={() => actions.setSearchLevel(n)}>
                        {n === 0 ? t("anyLevel") : `${n}`}
                    </button>
                ))}
                {search.level > 0 && <span className={styles.note}>{t("levelHint")}</span>}
            </div>

            <div className={styles.chipWrap}>
                <span className={styles.fieldLabelInline}>{t("source")}</span>
                {[t("srcAll"), t("srcVanilla"), t("srcMods")].map((label, i) => (
                    <button key={i} className={`${styles.chip} ${search.source === i ? styles.chipOn : ""}`} onClick={() => actions.setSearchSource(i)}>
                        {label}
                    </button>
                ))}
            </div>

            <div className={styles.fieldLabel}>{t("lotSize")}</div>
            <RangeFilter label={t("lotWidth")} min={search.minWidth} max={search.maxWidth} minLabel={t("sizeMin")} maxLabel={t("sizeMax")} anyLabel={t("anySize")} onMin={actions.setSearchMinWidth} onMax={actions.setSearchMaxWidth} />
            <RangeFilter label={t("lotDepth")} min={search.minDepth} max={search.maxDepth} minLabel={t("sizeMin")} maxLabel={t("sizeMax")} anyLabel={t("anySize")} onMin={actions.setSearchMinDepth} onMax={actions.setSearchMaxDepth} />
            </Scrollable>
            </div>

            <div className={styles.addResults}>
            <div className={styles.resultHeader}>
                <span>
                    <b>{search.total}</b> {t("results")}
                    {search.total > search.results.length ? ` · ${t("showingFirst")}` : ""}
                </span>
                <div className={styles.resultTools}>
                    <ViewButtons view={view} setView={setView} t={t} />
                    <button className={`${styles.chip} ${styles.chipPrimary}`} disabled={search.total === 0 || tooMany || !current} onClick={actions.addResults}>
                        {tooMany ? `${t("addAllTooMany")} ${search.maxAddAll}` : `${t("addAll")} (${search.total})`}
                    </button>
                    <button className={`${styles.chip} ${styles.chipDangerText}`} disabled={search.removable === 0} onClick={() => setConfirmRemove(true)}>
                        {t("removeAll")} ({search.removable})
                    </button>
                </div>
            </div>
            {confirmRemove && search.removable > 0 && (
                <div className={styles.prompt}>
                    <div className={styles.promptLabel}>
                        {`${t("confirmRemoveAll")} ${current?.name ?? ""} (${search.removable})`}
                    </div>
                    <div className={styles.promptRow}>
                        <button
                            className={`${styles.chip} ${styles.chipDanger}`}
                            onClick={() => {
                                actions.removeResults();
                                setConfirmRemove(false);
                            }}
                        >
                            {t("removeAll")}
                        </button>
                        <button className={styles.chip} onClick={() => setConfirmRemove(false)}>
                            {t("cancel")}
                        </button>
                    </div>
                </div>
            )}

            {search.results.some((r) => r.inList) && <span className={styles.note}>{t("removeHint")}</span>}
            {search.results.length === 0 ? (
                <div className={styles.empty} style={{ height: `${Math.max(120, paneMax - 168 * rem)}px` }}>{t("noResults")}</div>
            ) : view === "grid" ? (
                <Scrollable className={styles.gridList} style={{ height: `${Math.max(120, paneMax - 168 * rem)}px`, maxHeight: "none" }} vertical>
                    <div className={styles.grid}>
                        {search.results.map((r) => (
                            <div
                                key={r.name}
                                className={`${styles.tile} ${r.inList ? styles.tileOn : ""} ${current ? "" : styles.dim}`}
                                onMouseEnter={() => setHover(r)}
                                onMouseLeave={() => setHover(null)}
                                onClick={() => current && (r.inList ? actions.removeItemByName(r.name) : actions.addItem(r.name))}
                            >
                                <div className={styles.tileImageWrap}>
                                    <Thumb src={r.thumb} className={styles.tileImage} />
                                    <span className={styles.tileSize}>{r.size}</span>
                                    {r.lv > 0 && <span className={styles.tileLevel}>Lv{r.lv}</span>}
                                    <Star on={r.fav} className={styles.tileStar} onToggle={() => actions.toggleFavorite(r.name)} t={t} />
                                    {r.inList && (
                                        <button
                                            className={`${styles.tileCheck} ${styles.tileCheckRemove}`}
                                            title={t("removeFromList")}
                                            onClick={(ev) => {
                                                ev.stopPropagation();
                                                actions.removeItemByName(r.name);
                                            }}
                                        >
                                            <Glyph className={styles.tileCheckGlyph} src="Media/Glyphs/Close.svg" />
                                        </button>
                                    )}
                                </div>
                                <span className={styles.tileName}>{r.label || r.name}</span>
                            </div>
                        ))}
                    </div>
                </Scrollable>
            ) : (
                <Scrollable className={styles.itemList} style={{ height: `${Math.max(120, paneMax - 168 * rem)}px`, maxHeight: "none" }} vertical>
                    {search.results.map((r) => (
                        <div key={r.name} className={styles.itemRow} onMouseEnter={() => setHover(r)} onMouseLeave={() => setHover(null)}>
                            <Star on={r.fav} className={styles.rowStar} onToggle={() => actions.toggleFavorite(r.name)} t={t} />
                            <div className={styles.itemName}>
                                <Thumb src={r.thumb} className={styles.thumb} />
                                <span className={styles.name}>{r.label || r.name}</span>
                                {r.lv > 0 && <span className={styles.badge}>Lv{r.lv}</span>}
                                <span className={styles.badge}>{r.size}</span>
                            </div>
                            {r.inList ? (
                                <button className={`${styles.chip} ${styles.chipDangerText} ${styles.rowAction}`} onClick={() => actions.removeItemByName(r.name)}>
                                    {t("remove")}
                                </button>
                            ) : (
                                <button className={`${styles.chip} ${styles.chipPrimary} ${styles.rowAction}`} disabled={!current} onClick={() => actions.addItem(r.name)}>
                                    {t("add")}
                                </button>
                            )}
                        </div>
                    ))}
                </Scrollable>
            )}
            <PreviewBox item={hover} t={t} />
            </div>
        </div>
    );
}

// ---------------------------------------------------------------- placement tab

function Group({ title, extra, wide, children }: { title: string; extra?: React.ReactNode; wide?: boolean; children: React.ReactNode }) {
    return (
        <div className={`${styles.group} ${wide ? styles.groupWide : ""}`}>
            <div className={styles.groupHeader}>
                <span>{title}</span>
                {extra}
            </div>
            {children}
        </div>
    );
}

/** Tree Controller set ids as short labels. */
function treeSetLabel(id: string, t: Tr): string {
    const custom = /^YYTC-custom-set-(\d+)$/.exec(id);
    if (custom) {
        return `${t("treeSetName")} ${custom[1]}`;
    }

    const known: { [id: string]: string } = {
        "YYTC-wild-deciduous-trees": t("treeSetDeciduous"),
        "YYTC-evergreen-trees": t("treeSetEvergreen"),
        "YYTC-wild-bushes": t("treeSetBushes"),
    };
    return known[id] ?? id.replace(/^YYTC-/, "");
}

function PlacementSettings({ state, lists, t }: { state: BuilderState; lists: Lists; t: Tr }) {
    const isCustom = state.density === DENSITY.custom;
    const describe = ["densityNormal", "densityRelaxed", "densityDense", "densityCustom"][state.density] ?? "densityNormal";
    return (
        <div className={styles.groupGrid}>
            <Group
                title={t("density")}
                extra={
                    state.anarchyInstalled ? (
                        <button className={`${styles.chip} ${state.anarchyOn ? styles.chipDanger : ""}`} onClick={actions.toggleAnarchy}>
                            {t(state.anarchyOn ? "anarchyOn" : "anarchyOff")}
                        </button>
                    ) : undefined
                }
            >
                <Segmented
                    value={state.density}
                    onChange={actions.setDensity}
                    options={[
                        { value: DENSITY.relaxed, label: t("relaxed") },
                        { value: DENSITY.normal, label: t("normal") },
                        { value: DENSITY.dense, label: t("dense") },
                        { value: DENSITY.custom, label: t("custom") },
                    ]}
                />
                <div className={styles.note}>{t(describe)}</div>
                {isCustom && (
                    <div className={styles.indent}>
                        <Stepper label={t("customGap")} value={state.customGap} unit="m" step={0.5} onChange={actions.setCustomGap} />
                        <Stepper label={t("overlapSide")} value={state.overlapSide} unit="m" step={0.5} onChange={actions.setOverlapSide} disabled={!state.anarchyOn} />
                        <Stepper label={t("overlapBack")} value={state.overlapBack} unit="m" step={0.5} onChange={actions.setOverlapBack} disabled={!state.anarchyOn} />
                        <div className={state.anarchyOn ? styles.note : styles.warning}>
                            {t(!state.anarchyInstalled ? "overlapNoAnarchy" : state.anarchyOn ? "overlapActive" : "overlapAnarchyOff")}
                        </div>
                    </div>
                )}
            </Group>

            <Group title={t("sizeOrder")}>
                <Segmented
                    value={state.sizeOrder}
                    onChange={actions.setSizeOrder}
                    options={[
                        { value: 0, label: t("sizeMixed") },
                        { value: 1, label: t("sizeLarge") },
                        { value: 2, label: t("sizeSmall") },
                    ]}
                />
                {state.sizeOrder !== 0 && (
                    <Segmented
                        value={state.sizeKey}
                        onChange={actions.setSizeKey}
                        options={[
                            { value: 0, label: t("sizeKeyArea") },
                            { value: 1, label: t("sizeKeyWidth") },
                            { value: 2, label: t("sizeKeyDepth") },
                        ]}
                    />
                )}
                <div className={styles.note}>{t("sizeOrderHint")}</div>
            </Group>

            <Group title={t("roadOrder")}>
                <Segmented
                    value={state.roadOrder}
                    onChange={actions.setRoadOrder}
                    options={[
                        { value: 0, label: t("roadDefault") },
                        { value: 1, label: t("roadWide") },
                        { value: 2, label: t("roadNarrow") },
                        { value: 3, label: t("roadRandom") },
                    ]}
                />
                <div className={styles.note}>{t("roadOrderHint")}</div>
            </Group>

            <Group title={t("options")}>
                <Switch value={state.fillRest} onChange={actions.setFillRest} label={t("fillRest")} hint={t("fillRestHint")} />
                <Switch value={state.generateRoads} onChange={actions.setGenerateRoads} label={t("generateRoads")} hint={t("generateRoadsHint")} />
                {state.generateRoads && (
                    <div className={styles.indent}>
                        <Stepper label={t("blockLength")} value={state.blockLength} unit="m" step={16} onChange={actions.setBlockLength} />
                        <div className={styles.note}>
                            {t("roadType")}: {state.road}
                        </div>
                    </div>
                )}
                <Stepper label={t("maxArea")} value={state.maxArea} unit="㎡" step={10000} onChange={actions.setMaxArea} />
                <div className={styles.note}>{t("maxAreaHint")}</div>
                <Switch value={state.fillInterior} onChange={actions.setFillInterior} label={t("fillInterior")} hint={t("fillInteriorHint")} />
            </Group>

            <Group title={t("snap")}>
                {MouseToolOptions ? (
                    <div className={styles.toolOptions}>
                        <MouseToolOptions />
                    </div>
                ) : (
                    <>
                        <Switch value={(state.snapOff & SNAP_CURB) === 0} onChange={() => actions.toggleSnap(SNAP_CURB)} label={t("snapCurb")} />
                        <Switch value={(state.snapOff & SNAP_ANGLE) === 0} onChange={() => actions.toggleSnap(SNAP_ANGLE)} label={t("snapAngle")} />
                        <Switch value={(state.snapOff & SNAP_CELL) === 0} onChange={() => actions.toggleSnap(SNAP_CELL)} label={t("snapCell")} />
                    </>
                )}
            </Group>

            <Group title={t("treeFill")} wide>
                <Switch value={state.treeFill} onChange={actions.setTreeFill} label={t("treeFill")} hint={t("treeFillHint")} />
                {state.treeFill && (
                    <div className={styles.indent}>
                        <Stepper label={t("treeSpacing")} value={state.treeSpacing} unit="m" step={2} onChange={actions.setTreeSpacing} />
                        <Stepper label={t("treeDensity")} value={state.treeDensity} unit="%" step={10} onChange={actions.setTreeDensity} />
                        <div className={styles.note}>{`${state.trees} ${t("treeCount")}`}</div>
                        {state.treesCapped && <div className={styles.note}>{t("treeCapNote")}</div>}
                        <div className={styles.note}>{t("treeSpecies")}</div>
                        <div className={styles.note}>{t("treeSets")}</div>
                        <div className={styles.chipRow}>
                            {lists.treeSets.length === 0 && <span className={styles.note}>{t("treeSetsNone")}</span>}
                            {lists.treeSets.map((set) => (
                                <button key={set.id} className={styles.chip} disabled={set.count === 0} onClick={() => actions.applyTreeSet(set.id)}>
                                    {`${treeSetLabel(set.id, t)} (${set.count})`}
                                </button>
                            ))}
                            <button className={styles.chip} onClick={actions.clearTrees}>
                                {t("treeClear")}
                            </button>
                        </div>
                        {lists.trees.length === 0 ? (
                            <div className={styles.empty}>{t("treeNone")}</div>
                        ) : (
                            <Scrollable className={styles.itemList} vertical>
                                {[...lists.trees.filter((x) => x.checked), ...lists.trees.filter((x) => !x.checked)].map((tree) => (
                                    <div key={tree.name} className={styles.itemRow}>
                                        <Check checked={tree.checked} onClick={() => actions.toggleTree(tree.name)} />
                                        <div className={styles.itemName}>
                                            <Thumb src={tree.thumb} className={styles.thumb} />
                                            <span className={styles.name}>{tree.label || tree.name}</span>
                                        </div>
                                    </div>
                                ))}
                            </Scrollable>
                        )}
                    </div>
                )}
            </Group>
        </div>
    );
}

// ---------------------------------------------------------------- window

export const BuilderPanel = () => {
    const state = parseState(useValue(stateBinding));
    const lists = parseLists(useValue(listsBinding));
    const search = parseSearch(useValue(searchBinding));
    const { pos, onHandleDown, reset } = useDraggable(POS_KEY, DEFAULT_POS, { width: PANEL_WIDTH, height: 600 });
    const [tab, setTabState] = useState<Tab>(() => (loadJson(TAB_KEY, { tab: "buildings" }).tab === "settings" ? "settings" : "buildings"));
    const [adding, setAdding] = useState(false);
    const addingNow = adding && tab === "buildings";

    // Fit the window on screen. Everything is measured from the laid-out window, so there is no
    // rem/px guess: rem = pixels per rem (the window is PANEL_WIDTH rem wide), the content area is
    // what is left below the title bar down to the screen bottom, and never more than PANE_MAX_REM,
    // so the window stays short even if the screen size is misreported. A window dragged near the
    // right edge is drawn further left.
    const windowRef = useRef<HTMLDivElement>(null);
    const titleRef = useRef<HTMLDivElement>(null);
    const [metrics, setMetrics] = useState({ pane: 500, rem: 1 });
    const [shiftLeft, setShiftLeft] = useState(0);
    useEffect(() => {
        const w = windowRef.current;
        const title = titleRef.current;
        if (!w || !title) {
            return;
        }
        const screen = window.innerHeight || document.documentElement.clientHeight || 1080;
        const screenW = window.innerWidth || document.documentElement.clientWidth || 1920;
        const wr = w.getBoundingClientRect();
        const rem = wr.width > 0 ? wr.width / PANEL_WIDTH : 1;
        const available = Math.floor(screen - wr.top - title.getBoundingClientRect().height - 14);
        const pane = Math.max(160 * rem, Math.min(available, PANE_MAX_REM * rem));
        if (Math.abs(pane - metrics.pane) > 2 || Math.abs(rem - metrics.rem) > 0.01) {
            setMetrics({ pane, rem });
        }
        const shift = Math.min(pos.x, Math.max(0, Math.ceil(pos.x + wr.width - screenW + 8)));
        if (Math.abs(shift - shiftLeft) > 2) {
            setShiftLeft(shift);
        }
    });

    if (!state.active) {
        return null;
    }

    const t: Tr = (k) => (T[k] ? T[k][state.ko ? 1 : 0] : k);
    const setTab = (next: Tab) => {
        setTabState(next);
        saveJson(TAB_KEY, { tab: next });
    };
    const { pane, rem } = metrics;
    const bodyMax = Math.max(120, pane - TABS_REM * rem);
    // The list grid gets what the buttons above it leave.
    const gridMax = Math.max(140, bodyMax - 170 * rem);

    return (
        <div ref={windowRef} className={styles.window} style={{ left: `${pos.x - shiftLeft}px`, top: `${pos.y}px`, width: `${PANEL_WIDTH}rem` }}>
            <div ref={titleRef} className={styles.titleBar} onMouseDown={onHandleDown}>
                <span className={styles.title}>Block Builder</span>
                <button className={styles.iconButton} title={t("resetPos")} onMouseDown={(e) => e.stopPropagation()} onClick={reset}>
                    <Glyph className={styles.glyph} src="Media/Glyphs/ArrowCircular.svg" />
                </button>
                <button className={styles.iconButton} title={t("close")} onMouseDown={(e) => e.stopPropagation()} onClick={actions.toggle}>
                    <Glyph className={styles.glyph} src="Media/Glyphs/Close.svg" />
                </button>
            </div>

            {addingNow ? (
                <AddBuildings search={search} lists={lists} state={state} t={t} onBack={() => setAdding(false)} paneMax={pane} rem={rem} />
            ) : (
                <div className={styles.columns}>
                    <Scrollable className={styles.side} style={{ height: `${pane}px` }} vertical>
                        <StatusCard state={state} t={t} />
                        <Actions state={state} t={t} />
                    </Scrollable>

                    <div className={styles.main}>
                        <div className={styles.tabs}>
                            <button className={`${styles.tab} ${tab === "buildings" ? styles.tabOn : ""}`} onClick={() => setTab("buildings")}>
                                {t("tabBuildings")}
                            </button>
                            <button className={`${styles.tab} ${tab === "settings" ? styles.tabOn : ""}`} onClick={() => setTab("settings")}>
                                {t("tabSettings")}
                            </button>
                        </div>

                        <Scrollable className={styles.body} style={{ height: `${bodyMax}px` }} vertical>
                            {tab === "buildings" && (
                                <>
                                    <ListPicker state={state} lists={lists} t={t} />
                                    <ListContents state={state} lists={lists} t={t} onAdd={() => setAdding(true)} gridMax={gridMax} rem={rem} />
                                </>
                            )}
                            {tab === "settings" && <PlacementSettings state={state} lists={lists} t={t} />}
                        </Scrollable>
                    </div>
                </div>
            )}
        </div>
    );
};