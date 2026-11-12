using BepInEx.Configuration;

namespace PummelPartyMcp
{
    /// <summary>BepInEx config file (BepInEx\config\com.pummelpartymcp.plugin.cfg).</summary>
    internal sealed class PluginConfig
    {
        public const int DefaultPort = 38917;

        private readonly ConfigEntry<bool> m_pluginEnabled;
        private readonly ConfigEntry<bool> m_enableWrites;
        private readonly ConfigEntry<int> m_port;
        private readonly ConfigEntry<bool> m_serverEnabled;

        public PluginConfig(ConfigFile config)
        {
            m_pluginEnabled = config.Bind("General", "PluginEnabled", true,
                "Master switch, read at game start. false = the plugin does nothing at all: no RPC server and no Workshop " +
                "publish block. Set false (and restart the game) to publish a mod to the Workshop by hand.");
            m_enableWrites = config.Bind("Safety", "EnableWrites", true,
                "Allow write tools (they still only act on mods named PB_TEST_...). false = every write tool returns " +
                "WRITES_DISABLED. Does not affect the Workshop publish block, which is always on while the plugin is enabled.");
            m_port = config.Bind("Server", "Port", DefaultPort,
                new ConfigDescription("TCP port of the local RPC server (bound to 127.0.0.1 only).",
                    new AcceptableValueRange<int>(1024, 65535)));
            m_serverEnabled = config.Bind("Server", "Enabled", true,
                "Start the local RPC server. false = no server, but the Workshop publish block still applies " +
                "(as long as [General] PluginEnabled = true).");
        }

        public bool PluginEnabled => m_pluginEnabled.Value;

        /// <summary>Read live on every write call.</summary>
        public bool EnableWrites => m_enableWrites.Value;

        public int Port => m_port.Value is >= 1024 and <= 65535 ? m_port.Value : DefaultPort;

        public bool ServerEnabled => m_serverEnabled.Value;
    }
}
