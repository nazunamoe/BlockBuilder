import { useValue } from "cs2/api";
import { getModule } from "cs2/modding";
import { Scrollable } from "cs2/ui";
import { useState } from "react";
import { actions, BuilderState, listsBinding, parseLists, parseState, SNAP_ANGLE, SNAP_CELL, SNAP_CURB, stateBinding } from "mods/bindings";
import { Check, loadJson, saveJson, Section, Segmented, Stepper, Switch, useDraggable } from "mods/ui-parts";
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
const OPEN_KEY = "blockBuilder.sections";
const DEFAULT_POS = { x: 12, y: 120 };
const DEFAULT_OPEN: { [id: string]: boolean } = { list: true, items: true, snap: false, options: true, overlap: false };

// [English, Korean]
const T: { [key: string]: [string, string] } = {
    stepDraw: ["Draw", "영역 그리기"],
    stepPreview: ["Preview", "미리보기"],
    stepBuild: ["Build", "짓기"],
    drawStart: ["Click corners on the map. The yellow marker shows where a corner snaps.", "지도에서 꼭짓점을 찍으세요. 노란 표시는 맞춰질 위치입니다."],
    drawMore: ["Keep adding corners; click the first one to close the area.", "꼭짓점을 계속 찍고, 첫 꼭짓점을 누르면 영역이 닫힙니다."],
    plannedHint: ["Left click builds · Right click goes back · Shift+click places the selected building", "왼쪽 클릭 짓기 · 오른쪽 클릭 되돌리기 · Shift+클릭 선택한 건물 놓기"],
    corners: ["corners", "꼭짓점"],
    buildings: ["Buildings", "건물"],
    streets: ["Streets", "도로"],
    designated: ["Placed", "직접 놓음"],
    list: ["Placement list", "배치 리스트"],
    reload: ["Reload", "다시 읽기"],
    noLists: ["No lists yet. Make one in Options > Block Builder.", "리스트가 없습니다. 옵션 > Block Builder에서 만드세요."],
    scopeAll: ["Whole list", "리스트 전체"],
    scopeChecked: ["Checked only", "고른 항목만"],
    items: ["Buildings", "건물 목록"],
    itemsHint: ["Click a name, then Shift+click on the map to place it.", "이름을 고른 뒤 지도에서 Shift+클릭하면 그 자리에 놓습니다."],
    itemsHintChecked: ["Tick the buildings to fill with. Click a name, then Shift+click to place it.", "채울 건물을 체크하세요. 이름을 고른 뒤 Shift+클릭하면 그 자리에 놓습니다."],
    selected: ["Shift+click", "Shift+클릭"],
    checkAll: ["All", "모두"],
    checkNone: ["None", "해제"],
    notLoaded: ["missing", "없음"],
    snap: ["Corner snapping", "꼭짓점 맞춤"],
    snapCurb: ["Curbs and junction corners", "연석·교차로 모서리"],
    snapAngle: ["Parallel / perpendicular to roads", "도로와 평행·직각"],
    snapCell: ["Edge length in 8 m steps", "변 길이 8m 단위"],
    options: ["Options", "옵션"],
    fillRest: ["Fill the rest", "빈자리 채우기"],
    fillRestHint: ["Off: build only Shift+clicked buildings", "끄면 Shift+클릭한 건물만 짓습니다"],
    generateRoads: ["Generate streets", "내부 도로 생성"],
    generateRoadsHint: ["For deep areas; ends join existing roads", "깊은 영역용, 끝은 기존 도로에 연결"],
    blockLength: ["Max block length", "최대 블록 길이"],
    roadType: ["Street type", "도로 종류"],
    fillInterior: ["Fill interior", "내부 채우기"],
    fillInteriorHint: ["Items that need no road (plazas, statues)", "도로가 필요 없는 항목 (광장, 조형물)"],
    overlap: ["Overlap", "겹침"],
    overlapSide: ["Left / right", "좌우"],
    overlapBack: ["Back", "상하 (뒤쪽)"],
    anarchyOn: ["Anarchy ON", "Anarchy 켜짐"],
    anarchyOff: ["Anarchy OFF", "Anarchy 꺼짐"],
    overlapActive: ["Lots may overlap neighbours by these amounts. Never roads or the front.", "설정한 만큼 이웃과 겹칠 수 있습니다. 도로와 앞면은 겹치지 않습니다."],
    overlapAnarchyOff: ["Switch Anarchy on to change these. Lots are kept apart now.", "Anarchy를 켜야 바꿀 수 있습니다. 지금은 겹치지 않게 배치합니다."],
    overlapNoAnarchy: ["Needs the Anarchy mod. Lots are kept apart now.", "Anarchy 모드가 필요합니다. 지금은 겹치지 않게 배치합니다."],
    shuffle: ["Shuffle", "배치 섞기"],
    clear: ["Clear area", "영역 지우기"],
    resetPos: ["Reset position", "위치 초기화"],
    close: ["Close", "닫기"],
};

function Steps({ state, t }: { state: BuilderState; t: (k: string) => string }) {
    const current = state.planned ? 1 : 0;
    const steps = [t("stepDraw"), t("stepPreview"), t("stepBuild")];
    return (
        <div className={styles.steps}>
            {steps.map((s, i) => (
                <div key={i} className={`${styles.stepChip} ${i === current ? styles.stepCurrent : i < current ? styles.stepDone : ""}`}>
                    <span className={styles.stepNumber}>{i + 1}</span>
                    {s}
                </div>
            ))}
        </div>
    );
}

function StatusCard({ state, t }: { state: BuilderState; t: (k: string) => string }) {
    return (
        <div className={styles.card}>
            <Steps state={state} t={t} />
            {state.planned ? (
                <>
                    <div className={styles.stats}>
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
                    <div className={styles.hint}>{t("plannedHint")}</div>
                </>
            ) : (
                <div className={styles.hint}>{state.corners === 0 ? t("drawStart") : `${t("drawMore")} (${state.corners} ${t("corners")})`}</div>
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

export const BuilderPanel = () => {
    const state = parseState(useValue(stateBinding));
    const lists = parseLists(useValue(listsBinding));
    const { pos, onHandleDown, reset } = useDraggable(POS_KEY, DEFAULT_POS, { width: PANEL_WIDTH, height: 600 });
    const [open, setOpen] = useState<{ [id: string]: boolean }>(() => loadJson(OPEN_KEY, DEFAULT_OPEN));
    if (!state.active) {
        return null;
    }

    const t = (k: string) => (T[k] ? T[k][state.ko ? 1 : 0] : k);
    const toggleSection = (id: string) => {
        const next = { ...open, [id]: !open[id] };
        setOpen(next);
        saveJson(OPEN_KEY, next);
    };
    const bodyMaxHeight = Math.max(240, window.innerHeight - pos.y - 120);
    const checkedScope = state.scope === 1;

    return (
        <div className={styles.window} style={{ left: `${pos.x}px`, top: `${pos.y}px`, width: `${PANEL_WIDTH}rem` }}>
            <div className={styles.titleBar} onMouseDown={onHandleDown}>
                <span className={styles.title}>Block Builder</span>
                <button className={styles.iconButton} title={t("resetPos")} onMouseDown={(e) => e.stopPropagation()} onClick={reset}>
                    <img className={styles.glyph} src="Media/Glyphs/ArrowCircular.svg" />
                </button>
                <button className={styles.iconButton} title={t("close")} onMouseDown={(e) => e.stopPropagation()} onClick={actions.toggle}>
                    <img className={styles.glyph} src="Media/Glyphs/Close.svg" />
                </button>
            </div>

            <Scrollable className={styles.body} style={{ maxHeight: `${bodyMaxHeight}px` }} vertical>
                <StatusCard state={state} t={t} />

                <Section
                    id="list"
                    title={t("list")}
                    open={open.list}
                    onToggle={toggleSection}
                    extra={
                        <button className={styles.chip} onClick={actions.reloadLists}>
                            {t("reload")}
                        </button>
                    }
                >
                    {lists.palettes.length === 0 ? (
                        <div className={styles.empty}>{t("noLists")}</div>
                    ) : (
                        <div className={styles.paletteList}>
                            {lists.palettes.map((p, i) => (
                                <button key={i} className={`${styles.row} ${i === state.palette ? styles.rowOn : ""}`} onClick={() => actions.setPalette(i)}>
                                    <span className={styles.name}>{p.name}</span>
                                    <span className={styles.badge}>{p.count}</span>
                                </button>
                            ))}
                        </div>
                    )}
                    <Segmented
                        value={state.scope}
                        onChange={actions.setScope}
                        options={[
                            { value: 0, label: t("scopeAll") },
                            { value: 1, label: `${t("scopeChecked")} · ${lists.checkedCount}` },
                        ]}
                    />
                </Section>

                <Section
                    id="items"
                    title={`${t("items")} (${lists.entries.length})`}
                    open={open.items}
                    onToggle={toggleSection}
                    extra={
                        checkedScope ? (
                            <>
                                <button className={styles.chip} onClick={() => actions.checkAll(true)}>
                                    {t("checkAll")}
                                </button>
                                <button className={styles.chip} onClick={() => actions.checkAll(false)}>
                                    {t("checkNone")}
                                </button>
                            </>
                        ) : undefined
                    }
                >
                    <div className={styles.note}>{t(checkedScope ? "itemsHintChecked" : "itemsHint")}</div>
                    <Scrollable className={styles.itemList} vertical>
                        {lists.entries.map((e, i) => {
                            const selected = i === state.designate;
                            return (
                                <div key={i} className={`${styles.itemRow} ${selected ? styles.rowOn : ""} ${e.loaded ? "" : styles.dim}`}>
                                    {checkedScope && <Check checked={e.checked} onClick={() => actions.toggleChecked(i)} />}
                                    <button className={styles.itemName} onClick={() => actions.setDesignate(i)}>
                                        <span className={styles.name}>{e.name}</span>
                                        {selected && <span className={styles.tag}>{t("selected")}</span>}
                                        <span className={styles.badge}>{e.loaded ? e.size : t("notLoaded")}</span>
                                    </button>
                                </div>
                            );
                        })}
                    </Scrollable>
                </Section>

                <Section id="snap" title={t("snap")} open={open.snap} onToggle={toggleSection}>
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
                </Section>

                <Section id="options" title={t("options")} open={open.options} onToggle={toggleSection}>
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
                </Section>

                <Section
                    id="overlap"
                    title={t("overlap")}
                    open={open.overlap}
                    onToggle={toggleSection}
                    extra={
                        state.anarchyInstalled ? (
                            <button className={`${styles.chip} ${state.anarchyOn ? styles.chipDanger : ""}`} onClick={actions.toggleAnarchy}>
                                {t(state.anarchyOn ? "anarchyOn" : "anarchyOff")}
                            </button>
                        ) : undefined
                    }
                >
                    <Stepper label={t("overlapSide")} value={state.overlapSide} unit="m" step={1} onChange={actions.setOverlapSide} disabled={!state.anarchyOn} />
                    <Stepper label={t("overlapBack")} value={state.overlapBack} unit="m" step={1} onChange={actions.setOverlapBack} disabled={!state.anarchyOn} />
                    <div className={state.anarchyOn ? styles.note : styles.warning}>
                        {t(!state.anarchyInstalled ? "overlapNoAnarchy" : state.anarchyOn ? "overlapActive" : "overlapAnarchyOff")}
                    </div>
                </Section>
            </Scrollable>

            <div className={styles.footer}>
                <button className={`${styles.button} ${styles.primary}`} onClick={actions.shuffle}>
                    {t("shuffle")}
                </button>
                <button className={styles.button} onClick={actions.clearArea}>
                    {t("clear")}
                </button>
            </div>
        </div>
    );
};
