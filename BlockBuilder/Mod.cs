using Colossal.IO.AssetDatabase;
using Colossal.Logging;
using Game;
using Game.Modding;
using Game.SceneFlow;
using Unity.Entities;

namespace BlockBuilder
{
    public class Mod : IMod
    {
        public static ILog log = LogManager.GetLogger(nameof(BlockBuilder)).SetShowsErrorsInUI(false);

        public static Setting Options { get; private set; }

        private LocaleEN m_LocaleEN;
        private LocaleKO m_LocaleKO;

        public void OnLoad(UpdateSystem updateSystem)
        {
            log.Info($"{nameof(BlockBuilder)} {typeof(Mod).Assembly.GetName().Version} loading");

            Options = new Setting(this);
            Options.RegisterInOptionsUI();
            m_LocaleEN = new LocaleEN(Options);
            m_LocaleKO = new LocaleKO(Options);
            GameManager.instance.localizationManager.AddSource("en-US", m_LocaleEN);
            GameManager.instance.localizationManager.AddSource("ko-KR", m_LocaleKO);
            // Key bindings register before loading so saved rebinds apply (official template order).
            Options.RegisterKeyBindings();
            AssetDatabase.global.LoadSettings(nameof(BlockBuilder), Options, new Setting(this));

            updateSystem.UpdateAt<CatalogSystem>(SystemUpdatePhase.Modification1);
            updateSystem.UpdateAt<SpikeTool>(SystemUpdatePhase.ToolUpdate);
            updateSystem.UpdateAt<BuilderTool>(SystemUpdatePhase.ToolUpdate);
            updateSystem.UpdateAt<SpikeAuditSystem>(SystemUpdatePhase.Modification1);
            updateSystem.UpdateAt<BuilderUISystem>(SystemUpdatePhase.UIUpdate);
        }

        public static bool IsKorean => GameManager.instance?.localizationManager?.activeLocaleId == "ko-KR";

        public void OnDispose()
        {
            log.Info(nameof(OnDispose));
            var world = World.DefaultGameObjectInjectionWorld;
            if (world != null && world.IsCreated)
            {
                var tools = world.GetExistingSystemManaged<Game.Tools.ToolSystem>();
                if (tools != null && (tools.activeTool is SpikeTool || tools.activeTool is BuilderTool))
                {
                    tools.activeTool = world.GetOrCreateSystemManaged<Game.Tools.DefaultToolSystem>();
                }
            }

            if (m_LocaleEN != null)
            {
                GameManager.instance.localizationManager.RemoveSource("en-US", m_LocaleEN);
            }

            if (m_LocaleKO != null)
            {
                GameManager.instance.localizationManager.RemoveSource("ko-KR", m_LocaleKO);
            }

            Options?.UnregisterInOptionsUI();
            Options = null;
        }
    }
}
