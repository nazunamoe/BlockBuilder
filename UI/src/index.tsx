import { useValue } from "cs2/api";
import { ModRegistrar } from "cs2/modding";
import { parseState, stateBinding } from "mods/bindings";
import { BuilderButton } from "mods/builder-button";
import { BuilderPanel } from "mods/builder-panel";

const TOOL_OPTIONS_PANEL = "game-ui/game/components/tool-options/tool-options-panel.tsx";

const register: ModRegistrar = (moduleRegistry) => {
    // Toolbar button at the top left of the game screen; the panel is shown while the tool is active.
    moduleRegistry.append("GameTopLeft", BuilderButton);
    moduleRegistry.append("Game", BuilderPanel);

    // The game's tool options toolbar (snapping buttons) is rendered inside the builder panel, so hide
    // it at its usual place while the builder is active. The original hook is always called so the
    // hook order stays the same.
    const original = moduleRegistry.get(TOOL_OPTIONS_PANEL, "useToolOptionsVisible");
    if (typeof original === "function") {
        moduleRegistry.override(TOOL_OPTIONS_PANEL, "useToolOptionsVisible", () => {
            const visible = original();
            const builderActive = parseState(useValue(stateBinding)).active;
            return visible && !builderActive;
        });
    }
};

export default register;
