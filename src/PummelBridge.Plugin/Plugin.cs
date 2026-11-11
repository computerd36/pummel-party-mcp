using System;
using System.IO;
using System.Linq;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using PummelBridge.Core;
using PummelBridge.Game;
using PummelBridge.Safety;
using PummelBridge.Tools;
using UnityEngine;

namespace PummelBridge
{
    /// <summary>Values captured once on the main thread at startup (safe to read from any thread).</summary>
    internal sealed class BridgeRuntimeInfo
    {
        public string GameVersion;
        public string UnityVersion;
        public string SessionId;
    }

    /// <summary>
    /// Entry point. Phase 2: token-protected HTTP API on 127.0.0.1 with read tools and guarded write tools
    /// (PB_TEST_ mods only), plus an always-on Harmony block of every Workshop publish/update/delete path.
    /// The HTTP thread only parses requests; all Unity/game access is queued to Update() via the dispatcher.
    /// </summary>
    [BepInPlugin(PluginInfo.Guid, PluginInfo.Name, PluginInfo.Version)]
    public sealed class PummelBridgePlugin : BaseUnityPlugin
    {
        private const int MaxWorkItemsPerFrame = 16;
        private const double MaxWorkMillisecondsPerFrame = 8.0;
        private static readonly TimeSpan MainThreadTimeout = TimeSpan.FromSeconds(10);

        private BridgeConfig m_config;
        private LogRingBuffer m_logRing;
        private MainThreadDispatcher m_dispatcher;
        private FramePoller m_poller;
        private BridgeHttpServer m_server;
        private bool m_logHooked;
        private bool m_shutDown;

        private void Awake()
        {
            m_config = new BridgeConfig(Config);
            if (!m_config.PluginEnabled)
            {
                Logger.LogInfo($"{PluginInfo.Name} {PluginInfo.Version} is disabled ([General] PluginEnabled = false): no RPC server, no Workshop publish block.");
                return;
            }

            BepInExBridgeLog log = new BepInExBridgeLog(Logger);
            GameReflection.Log = log;

            if (transform.parent == null)
            {
                DontDestroyOnLoad(gameObject); // BepInEx's manager object normally already is; harmless if so.
            }

            BridgeRuntimeInfo info = new BridgeRuntimeInfo
            {
                GameVersion = Application.version,
                UnityVersion = Application.unityVersion,
                SessionId = Guid.NewGuid().ToString("N"),
            };
            Logger.LogInfo($"{PluginInfo.Name} {PluginInfo.Version} (api v{PluginInfo.ApiVersion}); game version {info.GameVersion}, Unity {info.UnityVersion}");

            m_logRing = new LogRingBuffer(2000);
            Application.logMessageReceivedThreaded += OnUnityLog;
            m_logHooked = true;

            // 1. Publish block first, before anything else can run. Never removed at runtime.
            PublishBlockStatus block = InstallPublishBlock(log);

            // 2. RPC server (optional).
            if (!m_config.ServerEnabled)
            {
                Logger.LogInfo("RPC server disabled in config ([Server] Enabled = false); the Workshop publish block stays active.");
                return;
            }

            string tokenPath = Path.Combine(Paths.ConfigPath, PluginInfo.TokenFileName);
            string token;
            try
            {
                token = TokenAuth.LoadOrCreate(tokenPath, out bool created);
                Logger.LogInfo((created ? "Created new token file: " : "Using token file: ") + tokenPath);
            }
            catch (Exception e)
            {
                Logger.LogError($"Cannot read or create the token file {tokenPath}: {e.Message}. Server not started.");
                return;
            }

            try
            {
                m_dispatcher = new MainThreadDispatcher();
                m_poller = new FramePoller(log);
                BridgeConfig config = m_config;
                WriteSafety safety = new WriteSafety(() => config.EnableWrites, () => block.Complete, new GameEditorGuardHost(log));
                ToolRegistry registry = new ToolRegistry(safety);
                StatusTools.Register(registry, info, () => config.EnableWrites, block);
                HierarchyTools.Register(registry);
                ObjectTools.Register(registry);
                ActionTools.Register(registry);
                AssetTools.Register(registry);
                LogTools.Register(registry, m_logRing, info);
                ScreenshotTool.Register(registry, this);
                ModTools.Register(registry, m_poller, log);

                RpcHandler handler = new RpcHandler(registry, m_dispatcher, log, MainThreadTimeout);
                m_server = new BridgeHttpServer(new BridgeHttpServerOptions { Port = m_config.Port, Token = token }, handler.Handle, log);
                m_server.Start();
                Logger.LogInfo($"Listening on http://127.0.0.1:{m_server.Port}/rpc ({string.Join(", ", new System.Collections.Generic.List<string>(registry.Names).ToArray())}); " +
                               $"writes {(m_config.EnableWrites ? "enabled" : "disabled")}, publish block {(block.Complete ? "complete" : "INCOMPLETE")}");
            }
            catch (Exception e)
            {
                Logger.LogError($"Failed to start the RPC server on 127.0.0.1:{m_config.Port}: {e.GetType().Name}: {e.Message}");
                m_server = null;
            }
        }

        private PublishBlockStatus InstallPublishBlock(IBridgeLog log)
        {
            try
            {
                PublishBlocker.Configure(log, message => Debug.LogWarning(message));
                PublishBlockStatus status = PublishBlocker.Apply(new Harmony(PublishBlocker.HarmonyId), PublishBlockTargets.Build());
                if (status.Complete)
                {
                    Logger.LogInfo($"Workshop publish block active: {status.PatchedCount} methods patched. Publishing to the Workshop is disabled while this plugin is enabled.");
                }
                else
                {
                    Logger.LogError($"Workshop publish block INCOMPLETE ({status.PatchedCount} methods patched; missing: {string.Join("; ", status.Missing.ToArray())}). All write tools are disabled.");
                }
                return status;
            }
            catch (Exception e)
            {
                Logger.LogError($"Installing the Workshop publish block failed: {e.GetType().Name}: {e.Message}. All write tools are disabled.");
                return new PublishBlockStatus(false, false, 0, new[] { "installer failed: " + e.Message });
            }
        }

        private void Update()
        {
            // Drain never throws: tool exceptions are captured into the waiting request.
            m_dispatcher?.Drain(MaxWorkItemsPerFrame, MaxWorkMillisecondsPerFrame);
            m_poller?.Tick();
        }

        private void OnDestroy() => Shutdown("OnDestroy");

        private void OnApplicationQuit() => Shutdown("OnApplicationQuit");

        private void Shutdown(string reason)
        {
            if (m_shutDown || m_config == null || !m_config.PluginEnabled)
            {
                return;
            }
            m_shutDown = true;
            try
            {
                m_server?.Stop();
                m_dispatcher?.Shutdown();
                if (m_logHooked)
                {
                    Application.logMessageReceivedThreaded -= OnUnityLog;
                    m_logHooked = false;
                }
                Logger.LogInfo($"Stopped ({reason}).");
            }
            catch (Exception e)
            {
                Logger.LogError("Error during shutdown: " + e.Message);
            }
        }

        /// <summary>May run on any thread. Must not log through Unity (recursion).</summary>
        private void OnUnityLog(string condition, string stackTrace, LogType type)
        {
            try
            {
                bool keepStack = type == LogType.Error || type == LogType.Exception || type == LogType.Assert;
                m_logRing?.Add(LogTools.LevelName(type), condition, keepStack ? stackTrace : null);
            }
            catch
            {
                // never throw into Unity's logging pipeline
            }
        }

        private sealed class BepInExBridgeLog : IBridgeLog
        {
            private readonly ManualLogSource m_source;

            public BepInExBridgeLog(ManualLogSource source)
            {
                m_source = source;
            }

            public void Debug(string message) => m_source.LogDebug(message);
            public void Info(string message) => m_source.LogInfo(message);
            public void Warning(string message) => m_source.LogWarning(message);
            public void Error(string message) => m_source.LogError(message);
        }
    }
}
