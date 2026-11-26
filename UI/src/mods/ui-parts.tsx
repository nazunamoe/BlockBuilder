import { Component, ReactNode, useCallback, useEffect, useRef, useState } from "react";
import { getModule } from "cs2/modding";
import styles from "./builder-panel.module.scss";

/** localStorage may be missing or throw in the game's UI host; never let that break the panel. */
export function loadJson<T>(key: string, fallback: T): T {
    try {
        const raw = window.localStorage?.getItem(key);
        return raw ? { ...fallback, ...JSON.parse(raw) } : fallback;
    } catch {
        return fallback;
    }
}

export function saveJson(key: string, value: unknown) {
    try {
        window.localStorage?.setItem(key, JSON.stringify(value));
    } catch {
        // ignore
    }
}

export interface Position {
    x: number;
    y: number;
}

/** Space in px kept between a floating window and the right edge of the screen. */
export const EDGE_GAP = 8;

/**
 * Drag a floating window by its handle. The position is kept inside the screen and remembered
 * between sessions. The width is read from the window element, as its CSS size is in rem and
 * positions are in px.
 */
export function useDraggable(storageKey: string, initial: Position, windowRef: React.RefObject<HTMLElement>) {
    const [pos, setPos] = useState<Position>(() => loadJson(storageKey, initial));
    const drag = useRef<{ dx: number; dy: number } | null>(null);
    const latest = useRef(pos);
    latest.current = pos;

    const clamp = useCallback(
        (p: Position): Position => {
            const width = windowRef.current?.getBoundingClientRect().width ?? 0;
            // Closed or not laid out yet: only keep a corner of the window reachable.
            const maxX = Math.max(0, window.innerWidth - (width > 0 ? width + EDGE_GAP : 48));
            const maxY = Math.max(0, window.innerHeight - 48);
            return { x: Math.min(Math.max(0, p.x), maxX), y: Math.min(Math.max(0, p.y), maxY) };
        },
        [windowRef]
    );

    useEffect(() => {
        const move = (e: MouseEvent) => {
            if (!drag.current) {
                return;
            }
            setPos(clamp({ x: e.clientX - drag.current.dx, y: e.clientY - drag.current.dy }));
        };
        const up = () => {
            if (drag.current) {
                drag.current = null;
                saveJson(storageKey, latest.current);
            }
        };
        window.addEventListener("mousemove", move);
        window.addEventListener("mouseup", up);
        return () => {
            window.removeEventListener("mousemove", move);
            window.removeEventListener("mouseup", up);
        };
    }, [clamp, storageKey]);

    // Keep the window on screen after a resolution change.
    useEffect(() => {
        const fix = () => setPos((p) => clamp(p));
        window.addEventListener("resize", fix);
        return () => window.removeEventListener("resize", fix);
    }, [clamp]);

    const onHandleDown = (e: React.MouseEvent) => {
        if (e.button !== 0) {
            return;
        }
        // A remembered position can be past the limit (other screen size). Start from where the
        // window is drawn, or the mouse would have to travel the difference before it moves.
        const start = clamp(latest.current);
        latest.current = start;
        setPos(start);
        drag.current = { dx: e.clientX - start.x, dy: e.clientY - start.y };
        e.preventDefault();
        e.stopPropagation();
    };

    const reset = () => {
        setPos(initial);
        saveJson(storageKey, initial);
    };

    return { pos, onHandleDown, reset };
}

/**
 * Single-colour icon drawn the way the game does it (tinted-icon.tsx): a div filled with the
 * text colour and masked by the SVG, so the glyph takes the colour of its CSS class.
 */
export const Glyph = ({ src, className }: { src: string; className?: string }) => (
    <div className={`${styles.glyphBase} ${className ?? ""}`} style={{ maskImage: `url(${src})` } as React.CSSProperties} />
);

/** Collapsible section; open/closed state is remembered per section id. */
export const Section = ({
    id,
    title,
    extra,
    children,
    open,
    onToggle,
}: {
    id: string;
    title: string;
    extra?: ReactNode;
    children: ReactNode;
    open: boolean;
    onToggle: (id: string) => void;
}) => (
    <div className={`${styles.section} ${open ? "" : styles.collapsed}`}>
        <div className={styles.sectionHeader}>
            <button className={styles.sectionTitle} onClick={() => onToggle(id)}>
                <Glyph className={`${styles.chevron} ${open ? "" : styles.chevronClosed}`} src="Media/Glyphs/StrokeArrowDown.svg" />
                {title}
            </button>
            {extra && <div className={styles.sectionExtra}>{extra}</div>}
        </div>
        {open && <div className={styles.sectionBody}>{children}</div>}
    </div>
);

export const Segmented = ({ options, value, onChange }: { options: { value: number; label: string }[]; value: number; onChange: (v: number) => void }) => (
    <div className={styles.segmented}>
        {options.map((o) => (
            <button key={o.value} className={`${styles.segment} ${o.value === value ? styles.segmentOn : ""}`} onClick={() => onChange(o.value)}>
                {o.label}
            </button>
        ))}
    </div>
);

export const Switch = ({ value, onChange, label, hint, disabled }: { value: boolean; onChange: (v: boolean) => void; label: string; hint?: string; disabled?: boolean }) => (
    <button className={`${styles.switchRow} ${disabled ? styles.disabled : ""}`} disabled={disabled} onClick={() => onChange(!value)}>
        <span className={styles.switchText}>
            <span>{label}</span>
            {hint && <span className={styles.switchHint}>{hint}</span>}
        </span>
        <span className={`${styles.switch} ${value ? styles.switchOn : ""}`}>
            <span className={styles.knob} />
        </span>
    </button>
);

export const Stepper = ({
    label,
    value,
    unit,
    step,
    onChange,
    disabled,
    display,
}: {
    label: string;
    value: number;
    unit: string;
    step: number;
    onChange: (v: number) => void;
    disabled?: boolean;
    /** Shown instead of the number, e.g. "no limit" for 0. */
    display?: string;
}) => (
    <div className={`${styles.stepper} ${disabled ? styles.disabled : ""}`}>
        <span className={styles.stepperLabel}>{label}</span>
        <button className={styles.stepButton} disabled={disabled} onClick={() => onChange(value - step)}>
            -
        </button>
        <span className={styles.stepValue}>
            {display ?? value}
            {display === undefined && <small>{unit}</small>}
        </span>
        <button className={styles.stepButton} disabled={disabled} onClick={() => onChange(value + step)}>
            +
        </button>
    </div>
);

/**
 * Lot size filter row like Find It: a comparison (< = >) and a cell count; 0 means any size.
 * One cell is 8 m, shown next to the count.
 */
/** Smallest and largest lot in cells (0 = no limit), two steppers under the axis name. */
export const RangeFilter = ({
    label,
    min,
    max,
    minLabel,
    maxLabel,
    anyLabel,
    onMin,
    onMax,
    inline,
}: {
    /** Title on the left of the two steppers instead of above them. */
    inline?: boolean;
    label: string;
    min: number;
    max: number;
    minLabel: string;
    maxLabel: string;
    anyLabel: string;
    onMin: (v: number) => void;
    onMax: (v: number) => void;
}) => {
    const part = (name: string, value: number, onValue: (v: number) => void) => (
        <div className={styles.rangePart}>
            <span className={styles.rangeName}>{name}</span>
            <button className={styles.stepButton} onClick={() => onValue(Math.max(0, value - 1))}>
                -
            </button>
            <span className={styles.rangeValue}>{value === 0 ? anyLabel : value}</span>
            <button className={styles.stepButton} onClick={() => onValue(value + 1)}>
                +
            </button>
        </div>
    );
    return (
        <div className={`${styles.rangeBlock} ${inline ? styles.rangeInline : ""}`}>
            <span className={styles.rangeTitle}>{label}</span>
            <div className={styles.rangeRow}>
                {part(minLabel, min, onMin)}
                {part(maxLabel, max, onMax)}
            </div>
        </div>
    );
};

export const SizeFilter = ({
    label,
    value,
    op,
    anyLabel,
    onValue,
    onOp,
}: {
    label: string;
    value: number;
    op: number;
    anyLabel: string;
    onValue: (v: number) => void;
    onOp: (op: number) => void;
}) => (
    <div className={styles.sizeRow}>
        <span className={styles.sizeLabel}>{label}</span>
        <div className={`${styles.opGroup} ${value === 0 ? styles.disabled : ""}`}>
            {["<", "=", ">"].map((s, i) => (
                <button key={i} className={`${styles.opButton} ${op === i ? styles.opOn : ""}`} disabled={value === 0} onClick={() => onOp(i)}>
                    {s}
                </button>
            ))}
        </div>
        <button className={styles.stepButton} onClick={() => onValue(Math.max(0, value - 1))}>
            -
        </button>
        <span className={styles.sizeValue}>
            {value === 0 ? (
                anyLabel
            ) : (
                <>
                    {value}
                    <small>{`${value * 8}m`}</small>
                </>
            )}
        </span>
        <button className={styles.stepButton} onClick={() => onValue(value + 1)}>
            +
        </button>
    </div>
);

/**
 * The game's own text input: while it has focus the game's shortcuts are suspended, so typing a
 * name does not trigger tools. Falls back to a plain input if the module is not found.
 */
const GameTextInput: any = (() => {
    try {
        return getModule("game-ui/common/input/text/text-input.tsx", "TextInput");
    } catch {
        return undefined;
    }
})();

export const TextField = ({ value, onChange, onEnter, className }: { value: string; onChange: (v: string) => void; onEnter?: () => void; className?: string }) => {
    const onKeyDown = (e: React.KeyboardEvent) => {
        if (e.keyCode === 13 && onEnter) {
            onEnter();
        }
    };
    const handle = (e: React.ChangeEvent<HTMLInputElement>) => onChange(e.target.value);
    const plain = <input className={`${styles.input} ${className ?? ""}`} value={value} onChange={handle} onKeyDown={onKeyDown} />;
    return GameTextInput ? (
        <Fallback fallback={plain}>
            <GameTextInput className={`${styles.input} ${className ?? ""}`} value={value} onChange={handle} onKeyDown={onKeyDown} />
        </Fallback>
    ) : (
        plain
    );
};

/** Renders the fallback if the child throws (e.g. the game input needs context we do not provide). */
class Fallback extends Component<{ fallback: ReactNode; children: ReactNode }, { failed: boolean }> {
    state = { failed: false };

    static getDerivedStateFromError() {
        return { failed: true };
    }

    render() {
        return this.state.failed ? this.props.fallback : this.props.children;
    }
}

/** Inline name prompt: text field with confirm / cancel. */
export const NamePrompt = ({ label, initial, okLabel, cancelLabel, onOk, onCancel }: { label: string; initial: string; okLabel: string; cancelLabel: string; onOk: (name: string) => void; onCancel: () => void }) => {
    const [name, setName] = useState(initial);
    const ok = () => {
        if (name.trim() !== "") {
            onOk(name.trim());
        }
    };
    return (
        <div className={styles.prompt}>
            <div className={styles.promptLabel}>{label}</div>
            <div className={styles.promptRow}>
                <TextField value={name} onChange={setName} onEnter={ok} className={styles.promptInput} />
                <button className={`${styles.chip} ${styles.chipPrimary}`} onClick={ok}>
                    {okLabel}
                </button>
                <button className={styles.chip} onClick={onCancel}>
                    {cancelLabel}
                </button>
            </div>
        </div>
    );
};

export const Check = ({ checked, onClick }: { checked: boolean; onClick: () => void }) => (
    <button className={`${styles.check} ${checked ? styles.checkOn : ""}`} onClick={onClick}>
        {checked && <Glyph className={styles.glyphSmall} src="Media/Glyphs/Checkmark.svg" />}
    </button>
);
