import { useValue } from "cs2/api";
import { getModule } from "cs2/modding";
import { Scrollable } from "cs2/ui";
import { useEffect, useRef, useState } from "react";
import { actions, BuilderState, DENSITY, Lists, listsBinding, parseLists, parseSearch, parseState, Search, searchBinding, SNAP_ANGLE, SNAP_CELL, SNAP_CURB, stateBinding } from "mods/bindings";
import { T } from "mods/texts";
import { Check, Glyph, loadJson, NamePrompt, saveJson, Segmented, SizeFilter, Stepper, Switch, TextField, useDraggable } from "mods/ui-parts";
import styles from "./builder-panel.module.scss";

/** The game's own tool options toolbar (snapping buttons), shown inside the builder panel. */
const MouseToolOptions: any = (() => {
    try {
        return getModule("game-ui/game/components/tool-options/mouse-tool-options/mouse-tool-options.tsx", "MouseToolOptions");
    } catch {
        return undefined;
    }
})();

const PANEL_WIDTH = 380;
const POS_KEY = "blockBuilder.panelPos";
const TAB_KEY = "blockBuilder.tab";
const DEFAULT_POS = { x: 12, y: 120 };

type Tr = (k: string) => string;
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
                    <div key={i} className={`${styles.stepChip} ${i === current ? styles.stepCurrent : i < current ? styles.stepDone : ""}`}>
                        <span className={styles.stepNumber}>{i + 1}</span>
                        {s}
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
                                {t("confirmDelete")} <b>{current.name}</b>
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
function ListContents({ state, lists, t, onAdd }: { state: BuilderState; lists: Lists; t: Tr; onAdd: () => void }) {
    const [confirmClear, setConfirmClear] = useState(false);
    const onlyChecked = state.scope === 1;
    const hasList = lists.palettes[state.palette] !== undefined;

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
                        <button className={styles.chip} onClick={() => actions.checkAll(true)}>
                            {t("checkAll")}
                        </button>
                        <button className={styles.chip} onClick={() => actions.checkAll(false)}>
                            {t("checkNone")}
                        </button>
                    </>
                )}
            </div>

            {confirmClear && lists.entries.length > 0 && (
                <div className={styles.prompt}>
                    <div className={styles.promptLabel}>
                        {t("confirmClearItems")} <b>{lists.palettes[state.palette]?.name}</b> ({lists.entries.length})
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
            ) : (
                <Scrollable className={styles.itemList} vertical>
                    {lists.entries.map((e, i) => {
                        return (
                            <div key={i} className={`${styles.itemRow} ${e.loaded ? "" : styles.dim}`}>
                                {onlyChecked && <Check checked={e.checked} onClick={() => actions.toggleChecked(i)} />}
                                <div className={styles.itemName}>
                                    <span className={styles.name}>{e.name}</span>
                                    <span className={styles.badge}>{e.loaded ? e.size : t("notLoaded")}</span>
                                </div>
                                <button className={styles.removeButton} title={t("removeFromList")} onClick={() => actions.removeItem(i)}>
                                    <Glyph className={styles.removeGlyph} src="Media/Glyphs/Close.svg" />
                                </button>
                            </div>
                        );
                    })}
                </Scrollable>
            )}
        </>
    );
}
/** Catalog search that adds to the current list; replaces the list view until Back. */
function AddBuildings({ search, lists, state, t, onBack }: { search: Search; lists: Lists; state: BuilderState; t: Tr; onBack: () => void }) {
    // Start from the text the C# side still filters by (it survives a UI reload).
    const [query, setQuery] = useState<string | null>(null);
    const onQuery = (s: string) => {
        setQuery(s);
        actions.setSearchText(s);
    };
    const tooMany = search.total > search.maxAddAll;
    const current = lists.palettes[state.palette];

    return (
        <>
            <div className={styles.subHeader}>
                <button className={styles.backButton} onClick={onBack}>
                    <Glyph className={styles.glyph} src="Media/Glyphs/ArrowLeft.svg" />
                    {t("back")}
                </button>
                <span className={styles.note}>
                    {t("addTo")}: <b>{current?.name ?? "-"}</b>
                </span>
            </div>

            <div className={styles.fieldLabel}>{t("searchName")}</div>
            <TextField value={query ?? search.text} onChange={onQuery} />

            <div className={styles.chipWrap}>
                <button className={`${styles.chip} ${search.category < 0 ? styles.chipOn : ""}`} onClick={() => actions.setSearchCategory(-1)}>
                    {t("anyCategory")}
                </button>
                {search.categories.map((c) => (
                    <button key={c.id} className={`${styles.chip} ${search.category === c.id ? styles.chipOn : ""}`} onClick={() => actions.setSearchCategory(c.id)}>
                        {c.label}
                    </button>
                ))}
            </div>

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

            <div className={styles.fieldLabel}>{t("lotSize")}</div>
            <SizeFilter label={t("lotWidth")} value={search.width} op={search.widthOp} anyLabel={t("anySize")} onValue={actions.setSearchWidth} onOp={actions.setSearchWidthOp} />
            <SizeFilter label={t("lotDepth")} value={search.depth} op={search.depthOp} anyLabel={t("anySize")} onValue={actions.setSearchDepth} onOp={actions.setSearchDepthOp} />

            <div className={styles.resultHeader}>
                <span>
                    <b>{search.total}</b> {t("results")}
                    {search.total > search.results.length ? ` · ${t("showingFirst")}` : ""}
                </span>
                <button className={`${styles.chip} ${styles.chipPrimary}`} disabled={search.total === 0 || tooMany || !current} onClick={actions.addResults}>
                    {tooMany ? `${t("addAllTooMany")} ${search.maxAddAll}` : `${t("addAll")} (${search.total})`}
                </button>
            </div>

            {search.results.length === 0 ? (
                <div className={styles.empty}>{t("noResults")}</div>
            ) : (
                <Scrollable className={styles.itemList} vertical>
                    {search.results.map((r) => (
                        <div key={r.name} className={styles.itemRow}>
                            <div className={styles.itemName}>
                                <span className={styles.name}>{r.name}</span>
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
        </>
    );
}

// ---------------------------------------------------------------- placement tab

function Group({ title, extra, children }: { title: string; extra?: React.ReactNode; children: React.ReactNode }) {
    return (
        <div className={styles.group}>
            <div className={styles.groupHeader}>
                <span>{title}</span>
                {extra}
            </div>
            {children}
        </div>
    );
}

function PlacementSettings({ state, t }: { state: BuilderState; t: Tr }) {
    const isCustom = state.density === DENSITY.custom;
    const describe = ["densityNormal", "densityRelaxed", "densityDense", "densityCustom"][state.density] ?? "densityNormal";
    return (
        <>
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
        </>
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

    // Fit the window on screen: measure where it actually is and give the scrolling body whatever
    // height is left down to the bottom of the screen (units of the computed layout, so no rem/px guess).
    const windowRef = useRef<HTMLDivElement>(null);
    const bodyRef = useRef<HTMLDivElement>(null);
    const [bodyMax, setBodyMax] = useState(480);
    useEffect(() => {
        const w = windowRef.current;
        const b = bodyRef.current;
        if (!w || !b) {
            return;
        }
        const screen = window.innerHeight || document.documentElement.clientHeight || 1080;
        const wr = w.getBoundingClientRect();
        const chrome = wr.height - b.getBoundingClientRect().height;
        const available = Math.max(160, Math.floor(screen - wr.top - chrome - 12));
        if (Math.abs(available - bodyMax) > 2) {
            setBodyMax(available);
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

    return (
        <div ref={windowRef} className={styles.window} style={{ left: `${pos.x}px`, top: `${pos.y}px`, width: `${PANEL_WIDTH}rem` }}>
            <div className={styles.titleBar} onMouseDown={onHandleDown}>
                <span className={styles.title}>Block Builder</span>
                <button className={styles.iconButton} title={t("resetPos")} onMouseDown={(e) => e.stopPropagation()} onClick={reset}>
                    <Glyph className={styles.glyph} src="Media/Glyphs/ArrowCircular.svg" />
                </button>
                <button className={styles.iconButton} title={t("close")} onMouseDown={(e) => e.stopPropagation()} onClick={actions.toggle}>
                    <Glyph className={styles.glyph} src="Media/Glyphs/Close.svg" />
                </button>
            </div>

            <div className={styles.tabs}>
                <button className={`${styles.tab} ${tab === "buildings" ? styles.tabOn : ""}`} onClick={() => setTab("buildings")}>
                    {t("tabBuildings")}
                </button>
                <button className={`${styles.tab} ${tab === "settings" ? styles.tabOn : ""}`} onClick={() => setTab("settings")}>
                    {t("tabSettings")}
                </button>
            </div>

            <div ref={bodyRef} className={styles.bodyWrap}>
                <Scrollable className={styles.body} style={{ maxHeight: `${bodyMax}px` }} vertical>
                    <StatusCard state={state} t={t} />
                    {tab === "buildings" &&
                        (adding ? (
                            <AddBuildings search={search} lists={lists} state={state} t={t} onBack={() => setAdding(false)} />
                        ) : (
                            <>
                                <ListPicker state={state} lists={lists} t={t} />
                                <ListContents state={state} lists={lists} t={t} onAdd={() => setAdding(true)} />
                            </>
                        ))}
                    {tab === "settings" && <PlacementSettings state={state} t={t} />}
                </Scrollable>
            </div>

            <div className={styles.footer}>
                <button className={`${styles.button} ${styles.primary}`} disabled={!state.planned || state.buildings + state.streets === 0} onClick={actions.build}>
                    {t("build")}
                </button>
                <button className={styles.button} onClick={actions.shuffle}>
                    {t("shuffle")}
                </button>
                <button className={styles.button} onClick={actions.clearArea}>
                    {t("clear")}
                </button>
            </div>
        </div>
    );
};
