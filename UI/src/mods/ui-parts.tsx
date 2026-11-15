import { ReactNode, useCallback, useEffect, useRef, useState } from "react";
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

/**
 * Drag a floating window by its handle. The position is kept inside the screen and remembered
 * between sessions.
 */
export function useDraggable(storageKey: string, initial: Position, size: { width: number; height: number }) {
    const [pos, setPos] = useState<Position>(() => loadJson(storageKey, initial));
    const drag = useRef<{ dx: number; dy: number } | null>(null);
    const latest = useRef(pos);
    latest.current = pos;

    const clamp = useCallback(
        (p: Position): Position => {
            const maxX = Math.max(0, window.innerWidth - Math.min(size.width, window.innerWidth));
            const maxY = Math.max(0, window.innerHeight - 48);
            return { x: Math.min(Math.max(0, p.x), maxX), y: Math.min(Math.max(0, p.y), maxY) };
        },
        [size.width]
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
        drag.current = { dx: e.clientX - latest.current.x, dy: e.clientY - latest.current.y };
        e.preventDefault();
        e.stopPropagation();
    };

    const reset = () => {
        setPos(initial);
        saveJson(storageKey, initial);
    };

    return { pos, onHandleDown, reset };
}

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
                <img className={`${styles.chevron} ${open ? "" : styles.chevronClosed}`} src="Media/Glyphs/StrokeArrowDown.svg" />
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
}: {
    label: string;
    value: number;
    unit: string;
    step: number;
    onChange: (v: number) => void;
    disabled?: boolean;
}) => (
    <div className={`${styles.stepper} ${disabled ? styles.disabled : ""}`}>
        <span className={styles.stepperLabel}>{label}</span>
        <button className={styles.stepButton} disabled={disabled} onClick={() => onChange(value - step)}>
            -
        </button>
        <span className={styles.stepValue}>
            {value}
            <small>{unit}</small>
        </span>
        <button className={styles.stepButton} disabled={disabled} onClick={() => onChange(value + step)}>
            +
        </button>
    </div>
);

export const Check = ({ checked, onClick }: { checked: boolean; onClick: () => void }) => (
    <button className={`${styles.check} ${checked ? styles.checkOn : ""}`} onClick={onClick}>
        {checked && <img className={styles.glyphSmall} src="Media/Glyphs/Checkmark.svg" />}
    </button>
);
