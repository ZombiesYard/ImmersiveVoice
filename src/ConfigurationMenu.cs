using System;
using System.Reflection;

namespace ImmersiveVoice
{
    internal static class ConfigurationMenu
    {
        public const string PluginId = "com.bepis.bepinex.configurationmanager";

        // Optional, event-driven integration. No assembly dependency or gameplay
        // polling; unsupported managers can still read the normal ConfigFile.
        public static bool Rebuild(object manager)
        {
            if (manager == null) return false;
            MethodInfo rebuild = manager.GetType().GetMethod("BuildSettingList", BindingFlags.Public | BindingFlags.Instance,
                null, Type.EmptyTypes, null);
            if (rebuild == null || rebuild.ReturnType != typeof(void)) return false;
            rebuild.Invoke(manager, null);
            return true;
        }
    }
}
