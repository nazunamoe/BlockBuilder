import { useValue } from "cs2/api";
import { FloatingButton } from "cs2/ui";
import { actions, parseState, stateBinding } from "mods/bindings";

/** Built-in game icon (exists under Cities2_Data/Content/Game/UI/Media). */
const ICON = "Media/Game/Icons/LotTool.svg";

export const BuilderButton = () => {
    const state = parseState(useValue(stateBinding));
    return <FloatingButton src={ICON} selected={state.active} tooltipLabel="Block Builder" onSelect={actions.toggle} />;
};
