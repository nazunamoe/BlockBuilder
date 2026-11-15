using System;
using System.Linq;
using System.Reflection;
using Game.Tools;

namespace BlockBuilder
{
    /// <summary>
    /// Optional link to the Anarchy mod through its public <c>Anarchy.Bridge.AnarchyBridge</c>, found by
    /// reflection so Block Builder has no hard dependency.
    /// - <c>TryAddToolSystem(tool)</c> puts the builder in Anarchy's tool list (its toggle shows for it).
    /// - <c>GetAnarchyToggleState()</c> tells whether Anarchy is on; while it is on, Anarchy marks the
    ///   error types the player chose (e.g. OverlapExisting) as non-blocking for whichever tool is active.
    /// Anarchy does not add its override protection to buildings, so none is needed here.
    /// </summary>
    internal static class AnarchyLink
    {
        private static bool s_Searched;
        private static MethodInfo s_TryAddTool;
        private static MethodInfo s_ToggleState;
        private static bool s_Registered;

        public static bool Installed
        {
            get
            {
                Find();
                return s_TryAddTool != null;
            }
        }

        private static void Find()
        {
            if (s_Searched)
            {
                return;
            }

            s_Searched = true;
            try
            {
                var type = AppDomain.CurrentDomain.GetAssemblies()
                    .Where(a => a.GetName().Name == "Anarchy")
                    .Select(a => a.GetType("Anarchy.Bridge.AnarchyBridge", false))
                    .FirstOrDefault(t => t != null);
                if (type == null)
                {
                    Mod.log.Info("anarchy: not installed");
                    return;
                }

                s_TryAddTool = type.GetMethod("TryAddToolSystem", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(ToolBaseSystem) }, null);
                s_ToggleState = type.GetMethod("GetAnarchyToggleState", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
                Mod.log.Info($"anarchy: bridge found (register={s_TryAddTool != null}, state={s_ToggleState != null})");
            }
            catch (Exception e)
            {
                Mod.log.Warn($"anarchy: lookup failed: {e.Message}");
            }
        }

        /// <summary>Registers the tool once; safe to call repeatedly.</summary>
        public static void Register(ToolBaseSystem tool)
        {
            if (s_Registered || !Installed)
            {
                return;
            }

            try
            {
                object result = s_TryAddTool.Invoke(null, new object[] { tool });
                s_Registered = true;
                Mod.log.Info($"anarchy: registered {tool.toolID} ({result})");
            }
            catch (Exception e)
            {
                Mod.log.Warn($"anarchy: register failed: {e.InnerException?.Message ?? e.Message}");
            }
        }

        private static Func<bool> s_State;
        private static int s_StateFrame = -1;
        private static bool s_StateValue;

        /// <summary>
        /// True when Anarchy is installed and switched on. Read through a delegate at most once per
        /// frame (the plan key and the panel ask several times a frame).
        /// </summary>
        public static bool Enabled
        {
            get
            {
                if (!Installed || s_ToggleState == null)
                {
                    return false;
                }

                int frame = UnityEngine.Time.frameCount;
                if (frame == s_StateFrame)
                {
                    return s_StateValue;
                }

                s_StateFrame = frame;
                try
                {
                    s_State ??= (Func<bool>)Delegate.CreateDelegate(typeof(Func<bool>), s_ToggleState);
                    s_StateValue = s_State();
                }
                catch
                {
                    s_StateValue = false;
                }

                return s_StateValue;
            }
        }
    }
}
