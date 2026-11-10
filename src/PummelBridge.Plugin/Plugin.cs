using System;
using System.IO;
using BepInEx;
using BepInEx.Logging;
using PummelBridge.Core;
using PummelBridge.Game;
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
    /// Entry point. Phase 1: read-only view of the mod editor over a token-protected HTTP API on 127.0.0.1.
    /// The HTTP thread only parses requests; all Unity/game access is queued to Update() via the dispatcher.
    /// No Harmony patches, no game method that mutates the editor, the mod, the scene or the disk.
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
        private BridgeHttpServer m_server;
        private bool m_logHooked;
        private bool m_shutDown;

        private void Awake()
        {
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
            Logger.LogInfo($"{PluginInfo.Name} {PluginInfo.Version} (read-only build, api v{PluginInfo.ApiVersion}); game version {info.GameVersion}, Unity {info.UnityVersion}");

            m_logRing = new LogRingBuffer(2000);
            Application.logMessageReceivedThreaded += OnUnityLog;
            m_logHooked = true;

            m_config = new BridgeConfig(Config);
            if (!m_config.Enabled)
            {
                Logger.LogInfo("Server disabled in config ([Server] Enabled = false).");
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
                ToolRegistry registry = new ToolRegistry();
                StatusTools.Register(registry, info);
                HierarchyTools.Register(registry);
                ObjectTools.Register(registry);
                ActionTools.Register(registry);
                AssetTools.Register(registry);
                LogTools.Register(registry, m_logRing, info);
                ScreenshotTool.Register(registry, this);

                RpcHandler handler = new RpcHandler(registry, m_dispatcher, log, MainThreadTimeout);
                m_server = new BridgeHttpServer(new BridgeHttpServerOptions { Port = m_config.Port, Token = token }, handler.Handle, log);
                m_server.Start();
                Logger.LogInfo($"Listening on http://127.0.0.1:{m_server.Port}/rpc ({string.Join(", ", new System.Collections.Generic.List<string>(registry.Names).ToArray())})");
            }
            catch (Exception e)
            {
                Logger.LogError($"Failed to start the RPC server on 127.0.0.1:{m_config.Port}: {e.GetType().Name}: {e.Message}");
                m_server = null;
            }
        }

        private void Update()
        {
            // Drain never throws: tool exceptions are captured into the waiting request.
            m_dispatcher?.Drain(MaxWorkItemsPerFrame, MaxWorkMillisecondsPerFrame);
        }

        private void OnDestroy() => Shutdown("OnDestroy");

        private void OnApplicationQuit() => Shutdown("OnApplicationQuit");

        private void Shutdown(string reason)
        {
            if (m_shutDown)
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
