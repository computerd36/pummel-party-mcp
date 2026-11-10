using BepInEx.Configuration;

namespace PummelBridge
{
    /// <summary>BepInEx config file (BepInEx\config\com.pummelbridge.plugin.cfg), section [Server].</summary>
    internal sealed class BridgeConfig
    {
        public const int DefaultPort = 38917;

        private readonly ConfigEntry<int> m_port;
        private readonly ConfigEntry<bool> m_enabled;

        public BridgeConfig(ConfigFile config)
        {
            m_port = config.Bind("Server", "Port", DefaultPort,
                new ConfigDescription("TCP port of the local RPC server (bound to 127.0.0.1 only).",
                    new AcceptableValueRange<int>(1024, 65535)));
            m_enabled = config.Bind("Server", "Enabled", true,
                "Start the local RPC server. When false the plugin loads but does nothing.");
        }

        public int Port => m_port.Value is >= 1024 and <= 65535 ? m_port.Value : DefaultPort;

        public bool Enabled => m_enabled.Value;
    }
}
