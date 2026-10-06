/*!
 * Cities: Skylines II UI Module
 *
 * Id: RealisticRoadWorks
 * Author: RealisticRoadWorks
 * Version: 3.0.0
 * Dependencies:
 */
// "Road works" section of the selected-info panel (SPEC 4.8 / 7), built from the game's own panel components
// (InfoSection, InfoRow, CapacityBar, InfoButton) so it looks and navigates like a vanilla section.
// Every text arrives ready-made from RealisticRoadWorks.V3.UI.WorksInfoSection (C#); buttons call its triggers.
// Robustness: missing game components fall back to plain markup, and an error boundary keeps a render error inside
// this section (the rest of the info panel is never affected).
const React = window.React;
const api = window["cs2/api"];
const h = React.createElement;

const SECTIONS = "game-ui/game/components/selected-info-panel/selected-info-sections/selected-info-sections.tsx";
const SHARED = "game-ui/game/components/selected-info-panel/shared-components/";
const SECTION_TYPE = "RealisticRoadWorks.V3.UI.WorksInfoSection";
const GROUP = "RealisticRoadWorks";

const ICON = {
    works: "Media/Game/Icons/RoadMaintenanceDepot.svg",
    crew: "Media/Game/Icons/Workers.svg",
    traffic: "Media/Game/Icons/Traffic.svg",
    money: "Media/Game/Icons/Money.svg",
    rush: "Media/Glyphs/SimulationSpeed3.svg",
    cancel: "Media/Game/Icons/Bulldozer.svg",
    focus: "Media/Game/Icons/Camera.svg",
    check: "Media/Glyphs/Checkmark.svg",
};

// Game theme colours (CSS variables of the game UI) with fallbacks.
const C = {
    accent: "var(--accentColorNormal, #4bc3f1)",
    accentLight: "var(--accentColorLight, #8fdcff)",
    track: "rgba(255, 255, 255, 0.16)",
    hollow: "rgba(255, 255, 255, 0.34)",
    dotBg: "rgba(10, 20, 35, 0.55)",
    text: "var(--textColor, #ffffff)",
    dim: "var(--textColorDim, rgba(255, 255, 255, 0.75))",
    dimmer: "var(--textColorDimmer, rgba(255, 255, 255, 0.5))",
    positive: "var(--positiveColor, #8bdb46)",
    warning: "var(--warningColor, #ffa42d)",
    negative: "var(--negativeColor, #e95f4a)",
};

// ETA states (C# EtaState; 2 was "paused", removed in round 2) and closures (ClosureLevel; the C# side sends a Closed
// road with one side open as CLOSURE_SLOW, so its pip is amber).
const ETA_WORKING = 0, ETA_CREWS_OFF = 1, ETA_CLEARING = 3, ETA_FINISHING = 4;
const CLOSURE_OPEN = 0, CLOSURE_SLOW = 1, CLOSURE_CLOSED = 2;

// Keyframe animations need a stylesheet; injected once. If the host refuses it, everything still renders (static).
const CSS = [
    "@keyframes rrw-pulse { 0% { transform: scale(1); opacity: 0.8; } 100% { transform: scale(2.4); opacity: 0; } }",
    "@keyframes rrw-breathe { 0% { opacity: 1; } 50% { opacity: 0.35; } 100% { opacity: 1; } }",
    ".rrw-pulse { animation-name: rrw-pulse; animation-duration: 1.6s; animation-timing-function: ease-out; animation-iteration-count: infinite; }",
    ".rrw-breathe { animation-name: rrw-breathe; animation-duration: 1.4s; animation-timing-function: ease-in-out; animation-iteration-count: infinite; }",
].join("\n");

function injectStyle() {
    try {
        if (typeof document === "undefined" || document.getElementById("rrw-style")) return;
        const style = document.createElement("style");
        style.id = "rrw-style";
        style.type = "text/css";
        style.appendChild(document.createTextNode(CSS));
        (document.head || document.body).appendChild(style);
    } catch (e) {
        console.warn("[RealisticRoadWorks] animations unavailable: " + e);
    }
}

function trigger(name) {
    try { api.trigger(GROUP, name); } catch (e) { console.error("[RealisticRoadWorks] trigger " + name + " failed: " + e); }
}

function clamp01(x) { return x < 0 ? 0 : x > 1 ? 1 : x; }

// ------------------------------------------------------------------ fallbacks (only if a game component is missing)

const FallbackSection = (p) => h("div", { style: { padding: "6rem 0" } }, p.children);
const FallbackRow = (p) => h("div", { style: { display: "flex", flexDirection: "row", alignItems: "center", padding: "5rem 10rem", fontSize: "var(--fontSizeS)" } },
    p.icon ? h("img", { src: p.icon, style: { width: "20rem", height: "20rem", marginRight: "8rem" } }) : null,
    h("div", { style: { flex: 1, textTransform: p.uppercase ? "uppercase" : "none" } }, p.left),
    p.right ? h("div", null, p.right) : null);
const FallbackBar = (p) => h("div", { style: { padding: "6rem 10rem" } },
    h("div", { style: { position: "relative", height: "20rem", borderRadius: "5rem", backgroundColor: C.track } },
        h("div", { style: { position: "absolute", left: 0, top: 0, bottom: 0, width: (100 * clamp01(p.progress / (p.max || 100))) + "%", borderRadius: "5rem", backgroundColor: "rgb(55, 158, 46)" } }),
        h("div", { style: { position: "absolute", left: 0, top: 0, right: 0, bottom: 0, display: "flex", alignItems: "center", justifyContent: "center", fontSize: "var(--fontSizeS)" } }, p.children)));
const FallbackButton = (p) => h("div", { onClick: p.onSelect, style: { margin: "4rem 10rem", padding: "6rem 10rem", backgroundColor: p.selected ? C.accent : "rgba(255,255,255,0.12)", borderRadius: "4rem" } }, p.label);

// Error boundary: a render error stays in this section and is logged once.
class Boundary extends React.Component {
    constructor(props) { super(props); this.state = { failed: false }; }
    static getDerivedStateFromError() { return { failed: true }; }
    componentDidCatch(error) { console.error("[RealisticRoadWorks] info section render error: " + error); }
    render() { return this.state.failed ? null : this.props.children; }
}

// ------------------------------------------------------------------ small parts

// Coloured status pip (right side of a row). pulse = soft expanding halo (crews at work).
function Pip({ color, pulse, breathe }) {
    return h("div", { style: { position: "relative", width: "10rem", height: "10rem", marginLeft: "8rem" } },
        pulse ? h("div", { className: "rrw-pulse", style: { position: "absolute", left: 0, top: 0, width: "10rem", height: "10rem", borderRadius: "5rem", backgroundColor: color } }) : null,
        h("div", { className: breathe ? "rrw-breathe" : undefined, style: { position: "absolute", left: 0, top: 0, width: "10rem", height: "10rem", borderRadius: "5rem", backgroundColor: color } }));
}

const DOT = 16;     // rem
const LINE = 3;     // rem

// Line half (left or right of a dot) with a track and a fill growing left to right.
function Half({ side, fill }) {
    const pos = side === "left" ? { left: 0, right: "50%" } : { left: "50%", right: 0 };
    return h("div", { style: { position: "absolute", top: ((DOT - LINE) / 2) + "rem", height: LINE + "rem", backgroundColor: C.track, ...pos } },
        h("div", { style: { position: "absolute", left: 0, top: 0, bottom: 0, width: (100 * clamp01(fill)) + "%", backgroundColor: C.accent, transitionProperty: "width", transitionDuration: "0.4s" } }));
}

function Dot({ state, working }) {
    const base = { position: "relative", width: DOT + "rem", height: DOT + "rem", borderRadius: (DOT / 2) + "rem" };
    if (state === "done") {
        return h("div", { style: { ...base, backgroundColor: C.accent, display: "flex", alignItems: "center", justifyContent: "center" } },
            h("img", { src: ICON.check, style: { width: "10rem", height: "10rem" } }));
    }
    if (state === "current") {
        return h("div", { style: { ...base } },
            working ? h("div", { className: "rrw-pulse", style: { position: "absolute", left: 0, top: 0, width: DOT + "rem", height: DOT + "rem", borderRadius: (DOT / 2) + "rem", backgroundColor: C.accent } }) : null,
            h("div", { style: { position: "absolute", left: 0, top: 0, width: DOT + "rem", height: DOT + "rem", borderRadius: (DOT / 2) + "rem", borderWidth: "2.5rem", borderStyle: "solid", borderColor: C.accentLight, backgroundColor: C.dotBg, display: "flex", alignItems: "center", justifyContent: "center" } },
                h("div", { style: { width: "6rem", height: "6rem", borderRadius: "3rem", backgroundColor: C.accentLight } })));
    }
    return h("div", { style: { ...base, borderWidth: "2rem", borderStyle: "solid", borderColor: C.hollow, backgroundColor: "rgba(0, 0, 0, 0.15)" } });
}

// Phase stepper: dots joined by a thin line. Done = filled accent with a tick, current = ring (+ halo while working)
// and bright label, upcoming = hollow. The connector after the current dot fills with the phase fraction.
function Stepper({ steps, index, fraction, working }) {
    const n = steps.length;
    const f = clamp01(fraction);
    const cols = [];
    for (let i = 0; i < n; i++) {
        const state = i < index ? "done" : i === index ? "current" : "todo";
        const leftFill = i <= index ? 1 : i === index + 1 ? Math.max(0, 2 * f - 1) : 0;
        const rightFill = i < index ? 1 : i === index ? Math.min(1, 2 * f) : 0;
        const labelColor = state === "current" ? C.text : state === "done" ? C.dim : C.dimmer;
        cols.push(h("div", { key: "s" + i, style: { flex: 1, position: "relative", display: "flex", flexDirection: "column", alignItems: "center" } },
            i > 0 ? h(Half, { side: "left", fill: leftFill }) : null,
            i < n - 1 ? h(Half, { side: "right", fill: rightFill }) : null,
            h(Dot, { state: state, working: working && state === "current" }),
            h("div", { style: { marginTop: "5rem", padding: "0 2rem", textAlign: "center", fontSize: "var(--fontSizeXS)", lineHeight: 1.15, color: labelColor, whiteSpace: "normal" } }, steps[i])));
    }
    return h("div", { style: { display: "flex", flexDirection: "row", alignItems: "flex-start", padding: "8rem 6rem 6rem 6rem" } }, cols);
}

// Round 4: one small bar per crew section, side by side in chain order, each filled with that crew's own sweep through its
// section. The crew nearest the camera (the one "Show work front" goes to first) is drawn brighter.
function CrewGauge({ fills, focus }) {
    const segs = [];
    for (let i = 0; i < fills.length; i++) {
        const f = clamp01(+fills[i] || 0);
        segs.push(h("div", { key: "c" + i, style: { position: "relative", width: "16rem", height: "6rem", marginLeft: i > 0 ? "3rem" : 0, borderRadius: "2rem", backgroundColor: C.track } },
            h("div", { style: { position: "absolute", left: 0, top: 0, bottom: 0, width: (100 * f) + "%", borderRadius: "2rem", backgroundColor: i === focus ? C.accentLight : C.accent, transitionProperty: "width", transitionDuration: "0.4s" } })));
    }
    return h("div", { style: { display: "flex", flexDirection: "row", alignItems: "center", marginLeft: "8rem" } }, segs);
}

// ------------------------------------------------------------------ registration

export default function register(registry) {
    injectStyle();

    const get = (path, name, fallback) => {
        try {
            const c = registry.get(SHARED + path, name);
            if (c) return c;
        } catch (e) { }
        console.warn("[RealisticRoadWorks] game component " + name + " not found, using a fallback");
        return fallback;
    };
    const InfoSection = get("info-section/info-section.tsx", "InfoSection", FallbackSection);
    const InfoRow = get("info-row/info-row.tsx", "InfoRow", FallbackRow);
    const CapacityBar = get("capacity-bar/capacity-bar.tsx", "CapacityBar", FallbackBar);
    const InfoButton = get("info-button/info-button.tsx", "InfoButton", FallbackButton);

    // Button that keeps the native look but ignores clicks while disabled (InfoButton has no disabled prop).
    const Button = ({ label, icon, selected, disabled, onSelect }) => {
        const btn = h(InfoButton, { label: label, icon: icon, selected: !!selected, onSelect: disabled ? () => { } : onSelect });
        return disabled ? h("div", { style: { opacity: 0.45 } }, btn) : btn;
    };

    const WorksBody = (props) => {
        const [armed, setArmed] = React.useState(false);
        const projectId = props.projectId || 0;
        React.useEffect(() => { setArmed(false); }, [projectId]);
        React.useEffect(() => {
            if (!armed) return undefined;
            const t = setTimeout(() => setArmed(false), 4000);
            return () => clearTimeout(t);
        }, [armed]);

        const steps = Array.isArray(props.steps) ? props.steps : [];
        const progress = Math.max(0, Math.min(100, props.progress || 0));
        const complete = !!props.complete;
        const eta = props.etaState | 0;
        const working = eta === ETA_WORKING;

        const etaPip = eta === ETA_WORKING ? h(Pip, { color: C.positive, pulse: true })
            : eta === ETA_CLEARING ? h(Pip, { color: C.warning, breathe: true })
            : eta === ETA_CREWS_OFF ? h(Pip, { color: C.warning })
            : eta === ETA_FINISHING ? h(Pip, { color: C.positive })
            : h(Pip, { color: C.dimmer });
        const closure = props.closure | 0;
        const trafficPip = closure === CLOSURE_CLOSED ? h(Pip, { color: C.negative })
            : closure === CLOSURE_SLOW ? h(Pip, { color: C.warning })
            : h(Pip, { color: C.positive });

        const rows = [];
        rows.push(h(InfoRow, { key: "head", icon: ICON.works, left: props.title, right: (props.kindText || "").toUpperCase(), uppercase: true, disableFocus: true }));
        if (steps.length > 0) rows.push(h(Stepper, { key: "steps", steps: steps, index: props.stepIndex | 0, fraction: props.stepFraction || 0, working: working }));
        rows.push(h(CapacityBar, { key: "bar", progress: progress, max: 100, plain: true }, props.phaseText));
        rows.push(h(InfoRow, { key: "eta", icon: ICON.crew, left: props.etaText, right: etaPip, disableFocus: true }));
        if (props.crewsText) {
            const fills = Array.isArray(props.crewFills) ? props.crewFills : [];
            rows.push(h(InfoRow, { key: "crews", left: props.crewsText, right: fills.length > 1 ? h(CrewGauge, { fills: fills, focus: props.focusCrew | 0 }) : null, subRow: true, disableFocus: true }));
        }
        rows.push(h(InfoRow, { key: "traffic", icon: ICON.traffic, left: props.trafficText, right: trafficPip, disableFocus: true }));
        if (props.paidText) rows.push(h(InfoRow, { key: "money", icon: ICON.money, left: props.paidText, right: props.refundText, subRow: true, disableFocus: true }));
        if (props.segmentsText) rows.push(h(InfoRow, { key: "segments", left: props.segmentsText, subRow: true, disableFocus: true }));

        // Actions: a compact 2-column grid (native InfoButtons, labels wrap inside their half) instead of four
        // full-width buttons, so the section fits the info panel without scrolling. Order: Rush | Cancel, Focus
        // (pause works was removed in round 2); an odd last button spans the full width.
        const buttons = [];
        let cantAfford = null;
        if (!complete) {
            if (props.canRush || props.rushed) {
                buttons.push(h(Button, {
                    key: "rush", label: props.rushText, icon: ICON.rush, selected: !!props.rushed,
                    disabled: !props.rushed && !props.canAfford,
                    onSelect: () => { if (props.canRush && props.canAfford) trigger("rush"); },
                }));
                if (props.canRush && !props.canAfford)
                    cantAfford = h(InfoRow, { key: "afford", left: h("div", { style: { color: C.negative } }, props.cantAffordText), subRow: true, disableFocus: true });
            }
            if (props.showCancel !== false) buttons.push(h(Button, {
                key: "cancel", label: armed ? props.confirmText : props.cancelText, icon: ICON.cancel, selected: armed,
                disabled: !props.canCancel,
                onSelect: () => {
                    if (!props.canCancel) return;
                    if (!armed) { setArmed(true); return; }
                    setArmed(false);
                    trigger("cancel");
                },
            }));
        }
        buttons.push(h(Button, { key: "focus", label: props.focusText, icon: ICON.focus, selected: false, onSelect: () => trigger("focus") }));
        for (let i = 0; i < buttons.length; i += 2) {
            const second = i + 1 < buttons.length ? buttons[i + 1] : null;
            rows.push(h("div", { key: "actions" + i, style: { display: "flex", flexDirection: "row", alignItems: "center" } },
                h("div", { style: { flex: 1, minWidth: 0 } }, buttons[i]),
                second ? h("div", { style: { flex: 1, minWidth: 0 } }, second) : null));
        }
        if (cantAfford) rows.push(cantAfford);

        return h(InfoSection, { focusKey: GROUP }, rows);
    };

    // The boundary wraps the whole body, so even an error in our own render code never reaches the info panel.
    const WorksSection = (props) => h(Boundary, { key: "rrw-" + (props.projectId || 0) }, h(WorksBody, props));

    try {
        const components = registry.get(SECTIONS, "selectedInfoSectionComponents");
        registry.override(SECTIONS, "selectedInfoSectionComponents", { ...components, [SECTION_TYPE]: WorksSection });
        console.log("[RealisticRoadWorks] info section registered (" + SECTION_TYPE + ")");
    } catch (e) {
        console.error("[RealisticRoadWorks] could not register info section: " + e);
    }
}

// The deploy copies only the .mjs: keep false (a missing .css would stall the mod UI loader).
export const hasCSS = false;
