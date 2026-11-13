using System;
using System.IO;
using BepInEx.Configuration;
using PummelPartyMcp.Core;

namespace PummelPartyMcp
{
    /// <summary>BepInEx config file (BepInEx\config\com.pummelpartymcp.plugin.cfg).</summary>
    internal sealed class PluginConfig
    {
        public const int DefaultPort = 38917;

        private readonly ConfigFile m_file;
        private readonly ConfigEntry<bool> m_pluginEnabled;
        private readonly ConfigEntry<bool> m_enableWrites;
        private readonly ConfigEntry<string> m_acceptedGameVersion;
        private readonly ConfigEntry<bool> m_forceUntested;
        private readonly ConfigEntry<int> m_port;
        private readonly ConfigEntry<bool> m_serverEnabled;
        private readonly ConfigEntry<int> m_maxImportFileMB;
        private readonly ConfigEntry<int> m_maxModAssetsMB;
        private readonly ConfigEntry<int> m_maxAudioSeconds;
        private readonly ConfigEntry<int> m_maxCompressedAudioMB;
        private DateTime m_lastWriteUtc;

        public PluginConfig(ConfigFile config)
        {
            m_file = config;
            m_pluginEnabled = config.Bind("General", "PluginEnabled", true,
                "Master switch, read at game start. false = the plugin does nothing at all: no RPC server and no Workshop " +
                "publish block. Set false (and restart the game) to publish a mod to the Workshop by hand.");
            m_enableWrites = config.Bind("Safety", "EnableWrites", true,
                "Allow write tools (they still only act on mods named MCP_...). false = every write tool returns " +
                "WRITES_DISABLED. Does not affect the Workshop publish block, which is always on while the plugin is enabled.");
            m_acceptedGameVersion = config.Bind("Safety", "AcceptedGameVersion", "",
                "Game version you accept although this plugin was not tested with it (the tested versions are listed in the log at " +
                "startup and by get_status). Empty = none. After a game update write tools return GAME_VERSION_UNTESTED (read tools " +
                "keep working); run the self_test tool and, if it reports ok, set this to the exact running version, e.g. 1.14.2. " +
                "Applies only to that exact version string; the next game update closes the gate again. Re-read from this file on " +
                "every write call, no restart needed.");
            m_forceUntested = config.Bind("Safety", "ForceGameVersionUntested", false,
                "Test switch: true = treat the running game version as untested even if it is in the tested list (write tools then " +
                "return GAME_VERSION_UNTESTED unless AcceptedGameVersion equals the running version). It can only make the gate " +
                "stricter; no setting here can accept a version the plugin was not tested with other than AcceptedGameVersion.");
            m_port = config.Bind("Server", "Port", DefaultPort,
                new ConfigDescription("TCP port of the local RPC server (bound to 127.0.0.1 only).",
                    new AcceptableValueRange<int>(1024, 65535)));
            m_serverEnabled = config.Bind("Server", "Enabled", true,
                "Start the local RPC server. false = no server, but the Workshop publish block still applies " +
                "(as long as [General] PluginEnabled = true).");
            m_maxImportFileMB = config.Bind("Assets", "MaxImportFileMB", AssetLimits.DefaultMaxImportFileMB,
                new ConfigDescription("import_asset: largest source file accepted, in MB. The editor itself has no limit; the game decodes audio on the main thread " +
                    "and waits for it, so keep this moderate. Re-read from this file on every import call, no restart needed.",
                    new AcceptableValueRange<int>(1, 1024)));
            m_maxModAssetsMB = config.Bind("Assets", "MaxModAssetsMB", AssetLimits.DefaultMaxModAssetsMB,
                new ConfigDescription("import_asset: largest total size of the sandbox mod's Assets folder after an import, in MB. " +
                    "Re-read from this file on every import call, no restart needed.",
                    new AcceptableValueRange<int>(1, 8192)));
            m_maxAudioSeconds = config.Bind("Assets", "MaxAudioSeconds", AssetLimits.DefaultMaxAudioSeconds,
                new ConfigDescription("import_asset: longest audio clip accepted, in seconds, read from the WAV/OGG/MP3 header of the copy (the game decodes the whole clip on the main thread " +
                    "and waits for it). Re-read on every import call, no restart needed.",
                    new AcceptableValueRange<int>(1, 7200)));
            m_maxCompressedAudioMB = config.Bind("Assets", "MaxCompressedAudioMB", AssetLimits.DefaultMaxCompressedAudioMB,
                new ConfigDescription("import_asset: largest OGG/MP3 file accepted when its duration cannot be read from its header (e.g. a VBR MP3 without a Xing/VBRI header), in MB. " +
                    "Re-read on every import call, no restart needed.",
                    new AcceptableValueRange<int>(1, 1024)));
            m_lastWriteUtc = LastWrite();
        }

        /// <summary>Asset size limits ([Assets] section); call <see cref="RefreshIfChanged"/> first (the registry does, per call).</summary>
        public AssetLimits AssetLimits => new AssetLimits(
            m_maxImportFileMB.Value >= 1 && m_maxImportFileMB.Value <= 1024 ? m_maxImportFileMB.Value : AssetLimits.DefaultMaxImportFileMB,
            m_maxModAssetsMB.Value >= 1 && m_maxModAssetsMB.Value <= 8192 ? m_maxModAssetsMB.Value : AssetLimits.DefaultMaxModAssetsMB,
            m_maxAudioSeconds.Value >= 1 && m_maxAudioSeconds.Value <= 7200 ? m_maxAudioSeconds.Value : AssetLimits.DefaultMaxAudioSeconds,
            m_maxCompressedAudioMB.Value >= 1 && m_maxCompressedAudioMB.Value <= 1024 ? m_maxCompressedAudioMB.Value : AssetLimits.DefaultMaxCompressedAudioMB);

        public bool PluginEnabled => m_pluginEnabled.Value;

        /// <summary>Current value; call <see cref="RefreshIfChanged"/> first (main thread) to pick up edits made to the file.</summary>
        public bool EnableWrites => m_enableWrites.Value;

        public string AcceptedGameVersion => m_acceptedGameVersion.Value ?? "";

        public bool ForceGameVersionUntested => m_forceUntested.Value;

        public int Port => m_port.Value is >= 1024 and <= 65535 ? m_port.Value : DefaultPort;

        public bool ServerEnabled => m_serverEnabled.Value;

        private DateTime LastWrite()
        {
            try
            {
                return File.GetLastWriteTimeUtc(m_file.ConfigFilePath);
            }
            catch
            {
                return DateTime.MinValue;
            }
        }

        /// <summary>
        /// BepInEx 5.4 does not watch the .cfg file: an edit on disk is not seen until the game restarts unless something calls
        /// ConfigFile.Reload(). This reloads when the file's write time changed since the last look (one stat call otherwise), so
        /// EnableWrites, AcceptedGameVersion and ForceGameVersionUntested follow edits without a restart. Main thread only (the
        /// write guard, get_status, self_test). A malformed file keeps the values already loaded.
        /// </summary>
        public void RefreshIfChanged()
        {
            try
            {
                DateTime now = LastWrite();
                if (now == m_lastWriteUtc)
                {
                    return;
                }
                m_file.Reload();
                m_lastWriteUtc = LastWrite(); // Reload may save; do not treat our own write as an edit
            }
            catch
            {
                m_lastWriteUtc = LastWrite();
            }
        }

        /// <summary>Gate status from the live config values (call <see cref="RefreshIfChanged"/> first where it matters).</summary>
        public GameVersionStatus CurrentGameVersion(string running) =>
            GameVersionGate.Evaluate(running, GameVersionGate.TestedVersions, AcceptedGameVersion, ForceGameVersionUntested);
    }
}
