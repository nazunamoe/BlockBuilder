import { useValue } from "cs2/api";
import { getModule } from "cs2/modding";
import { Panel, Scrollable } from "cs2/ui";
import { ReactNode } from "react";
import { actions, BuilderState, listsBinding, parseLists, parseState, SNAP_ANGLE, SNAP_CELL, SNAP_CURB, stateBinding } from "mods/bindings";
import styles from "./builder-panel.module.scss";

type Text = { [key: string]: [string, string] };

/** The game's own tool options toolbar (snapping buttons), shown inside the builder panel. */
const MouseToolOptions: any = (() => {
    try {
        return getModule("game-ui/game/components/tool-options/mouse-tool-options/mouse-tool-options.tsx", "MouseToolOptions");
    } catch {
        return undefined;
    }
})();

// [English, Korean]
const T: Text = {
    drawStart: ["Click corners to draw the area. Yellow marker: snapped to a curb, junction or road angle.", "꼭짓점을 클릭해 영역을 그리세요. 노란 표시는 연석·교차로·도로 각도에 맞춰진 위치입니다."],
    drawMore: ["Click the first corner to close the area.", "첫 꼭짓점을 클릭하면 영역이 닫힙니다."],
    planned: ["Left click: build. Right click: back. Shift+click: designate.", "왼쪽 클릭: 짓기 · 오른쪽 클릭: 되돌리기 · Shift+클릭: 지정"],
    corners: ["corners", "꼭짓점"],
    buildings: ["buildings", "건물"],
    streets: ["streets", "도로"],
    designated: ["designated", "지정"],
    list: ["Placement list", "배치 리스트"],
    reload: ["Reload", "다시 읽기"],
    noLists: ["No lists yet. Make one in Options > Block Builder.", "리스트가 없습니다. 옵션 > Block Builder에서 만드세요."],
    scope: ["Fill with", "채울 건물"],
    scopeAll: ["Whole list", "리스트 전체"],
    scopeChecked: ["Checked only", "고른 항목만"],
    items: ["Buildings", "건물 목록"],
    itemsHint: ["Click a name, then Shift+click on the map to place it there.", "이름을 클릭한 뒤 지도에서 Shift+클릭하면 그 자리에 놓습니다."],
    itemsHintChecked: [
        "Check the buildings to fill with. Click a name, then Shift+click on the map to place it there.",
        "채울 건물을 체크하세요. 이름을 클릭한 뒤 지도에서 Shift+클릭하면 그 자리에 놓습니다.",
    ],
    snap: ["Corner snapping", "꼭짓점 맞춤"],
    snapCurb: ["Curbs and junction corners", "연석·교차로 모서리"],
    snapAngle: ["Parallel / perpendicular to roads", "도로와 평행·직각"],
    snapCell: ["Edge length in 8 m steps", "변 길이 8m 단위"],
    overlap: ["Overlap allowance", "겹침 허용"],
    overlapActive: ["Anarchy is on: lots may overlap at the sides and back.", "Anarchy 켜짐: 필지가 옆·뒤로 겹칠 수 있습니다."],
    overlapAnarchyOff: ["Switch Anarchy on to use this; lots are kept apart now.", "Anarchy를 켜야 적용됩니다. 지금은 겹치지 않게 배치합니다."],
    overlapNoAnarchy: ["Needs the Anarchy mod; lots are kept apart now.", "Anarchy 모드가 필요합니다. 지금은 겹치지 않게 배치합니다."],
    checkAll: ["All", "모두"],
    checkNone: ["None", "해제"],
    fillRest: ["Fill the rest (off: only Shift+clicked)", "빈자리 채우기 (끄면 Shift+클릭한 것만 짓기)"],
    notLoaded: ["not loaded", "없음"],
    options: ["Options", "옵션"],
    generateRoads: ["Generate streets", "내부 도로 생성"],
    fillInterior: ["Fill interior", "내부 채우기"],
    blockLength: ["Max block length", "최대 블록 길이"],
    roadType: ["Street type", "도로 종류"],
    shuffle: ["Shuffle", "배치 섞기"],
    clear: ["Clear area", "영역 지우기"],
    close: ["Close", "닫기"],
};

const Section = ({ title, children, extra }: { title: string; children: ReactNode; extra?: ReactNode }) => (
    <div className={styles.section}>
        <div className={styles.sectionHeader}>
            <span>{title}</span>
            {extra}
        </div>
        {children}
    </div>
);

const Choice = ({ selected, onClick, children, dim }: { selected: boolean; onClick: () => void; children: ReactNode; dim?: boolean }) => (
    <button className={`${styles.choice} ${selected ? styles.selected : ""} ${dim ? styles.dim : ""}`} onClick={onClick}>
        {children}
    </button>
);

const Toggle = ({ value, onChange, label }: { value: boolean; onChange: (v: boolean) => void; label: string }) => (
    <button className={`${styles.toggle} ${value ? styles.on : ""}`} onClick={() => onChange(!value)}>
        <span className={styles.box}>{value ? "✓" : ""}</span>
        {label}
    </button>
);

function Hint({ state, t }: { state: BuilderState; t: (k: string) => string }) {
    if (!state.planned) {
        return (
            <div className={styles.hint}>
                {state.corners === 0 ? t("drawStart") : `${t("drawMore")} (${state.corners} ${t("corners")})`}
            </div>
        );
    }

    return (
        <div className={styles.hint}>
            <div className={styles.stats}>
                <span>
                    {t("buildings")} <b>{state.buildings}</b>
                </span>
                <span>
                    {t("streets")} <b>{state.streets}</b>
                </span>
                <span>
                    {t("designated")} <b>{state.designated}</b>
                </span>
            </div>
            <div>{t("planned")}</div>
        </div>
    );
}

export const BuilderPanel = () => {
    const state = parseState(useValue(stateBinding));
    const lists = parseLists(useValue(listsBinding));
    if (!state.active) {
        return null;
    }

    const t = (k: string) => (T[k] ? T[k][state.ko ? 1 : 0] : k);

    return (
        <div className={styles.anchor}>
            <Panel header="Block Builder" className={styles.panel} onClose={actions.toggle}>
                <Hint state={state} t={t} />
                {state.status !== "" && <div className={styles.status}>{state.status}</div>}
                {state.warnings.map((w, i) => (
                    <div key={i} className={styles.warning}>
                        {w}
                    </div>
                ))}

                <Section
                    title={t("list")}
                    extra={
                        <button className={styles.small} onClick={actions.reloadLists}>
                            {t("reload")}
                        </button>
                    }
                >
                    {lists.palettes.length === 0 ? (
                        <div className={styles.empty}>{t("noLists")}</div>
                    ) : (
                        <Scrollable className={styles.listShort} vertical>
                            {lists.palettes.map((p, i) => (
                                <Choice key={i} selected={i === state.palette} onClick={() => actions.setPalette(i)}>
                                    <span className={styles.name}>{p.name}</span>
                                    <span className={styles.meta}>{p.count}</span>
                                </Choice>
                            ))}
                        </Scrollable>
                    )}
                </Section>

                <Section title={t("scope")}>
                    <div className={styles.row}>
                        <Choice selected={state.scope === 0} onClick={() => actions.setScope(0)}>
                            {t("scopeAll")}
                        </Choice>
                        <Choice selected={state.scope === 1} onClick={() => actions.setScope(1)}>
                            {`${t("scopeChecked")} (${lists.checkedCount})`}
                        </Choice>
                    </div>
                </Section>

                <Section
                    title={t("items")}
                    extra={
                        state.scope === 1 ? (
                            <span>
                                <button className={styles.small} onClick={() => actions.checkAll(true)}>
                                    {t("checkAll")}
                                </button>{" "}
                                <button className={styles.small} onClick={() => actions.checkAll(false)}>
                                    {t("checkNone")}
                                </button>
                            </span>
                        ) : undefined
                    }
                >
                    <div className={styles.meta}>{t(state.scope === 1 ? "itemsHintChecked" : "itemsHint")}</div>
                    <Scrollable className={styles.listTall} vertical>
                        {lists.entries.map((e, i) => (
                            <div key={i} className={styles.itemRow}>
                                {state.scope === 1 && (
                                    <button className={`${styles.check} ${e.checked ? styles.on : ""}`} onClick={() => actions.toggleChecked(i)}>
                                        <span className={styles.box}>{e.checked ? "✓" : ""}</span>
                                    </button>
                                )}
                                <Choice selected={i === state.designate} dim={!e.loaded} onClick={() => actions.setDesignate(i)}>
                                    <span className={styles.name}>{e.name}</span>
                                    <span className={styles.meta}>{e.loaded ? e.size : t("notLoaded")}</span>
                                </Choice>
                            </div>
                        ))}
                    </Scrollable>
                </Section>

                <Section title={t("snap")}>
                    {MouseToolOptions ? (
                        <div className={styles.toolOptions}>
                            <MouseToolOptions />
                        </div>
                    ) : (
                        <>
                    <Toggle value={(state.snapOff & SNAP_CURB) === 0} onChange={() => actions.toggleSnap(SNAP_CURB)} label={t("snapCurb")} />
                    <Toggle value={(state.snapOff & SNAP_ANGLE) === 0} onChange={() => actions.toggleSnap(SNAP_ANGLE)} label={t("snapAngle")} />
                    <Toggle value={(state.snapOff & SNAP_CELL) === 0} onChange={() => actions.toggleSnap(SNAP_CELL)} label={t("snapCell")} />
                        </>
                    )}
                </Section>

                <Section title={t("options")}>
                    <Toggle value={state.fillRest} onChange={actions.setFillRest} label={t("fillRest")} />
                    <div className={styles.stepper}>
                        <span>{t("overlap")}</span>
                        <button className={styles.small} onClick={() => actions.setOverlap(state.overlap - 1)}>
                            −
                        </button>
                        <b>{state.overlap} m</b>
                        <button className={styles.small} onClick={() => actions.setOverlap(state.overlap + 1)}>
                            +
                        </button>
                    </div>
                    {state.overlap > 0 && (
                        <div className={state.anarchyOn ? styles.meta : styles.warning}>
                            {t(!state.anarchyInstalled ? "overlapNoAnarchy" : state.anarchyOn ? "overlapActive" : "overlapAnarchyOff")}
                        </div>
                    )}
                    <Toggle value={state.generateRoads} onChange={actions.setGenerateRoads} label={t("generateRoads")} />
                    {state.generateRoads && (
                        <>
                            <div className={styles.stepper}>
                                <span>{t("blockLength")}</span>
                                <button className={styles.small} onClick={() => actions.setBlockLength(state.blockLength - 16)}>
                                    −
                                </button>
                                <b>{state.blockLength} m</b>
                                <button className={styles.small} onClick={() => actions.setBlockLength(state.blockLength + 16)}>
                                    +
                                </button>
                            </div>
                            <div className={styles.meta}>
                                {t("roadType")}: {state.road}
                            </div>
                        </>
                    )}
                    <Toggle value={state.fillInterior} onChange={actions.setFillInterior} label={t("fillInterior")} />
                </Section>

                <div className={styles.footer}>
                    <button className={styles.action} onClick={actions.shuffle}>
                        {t("shuffle")}
                    </button>
                    <button className={styles.action} onClick={actions.clearArea}>
                        {t("clear")}
                    </button>
                    <button className={styles.action} onClick={actions.toggle}>
                        {t("close")}
                    </button>
                </div>
            </Panel>
        </div>
    );
};
