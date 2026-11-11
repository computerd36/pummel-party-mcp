using System;
using System.IO;
using System.Linq;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using PummelBridge.Core;
using PummelBridge.Game;
using PummelBridge.Hooks;
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
        private EditServices m_edit;
        private ToolRegistry m_registry;
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
            PublishBlockMonitor block = InstallPublishBlock(log);

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
                m_edit = new EditServices(m_poller, log);
                InstallSaveWatch(log);
                BridgeConfig config = m_config;
                WriteSafety safety = new WriteSafety(() => config.EnableWrites, block.IsComplete, new GameEditorGuardHost(log));
                ToolRegistry registry = new ToolRegistry(safety, new WriteQueue(log));
                m_registry = registry;
                StatusTools.Register(registry, info, () => config.EnableWrites, block.Current, m_edit);
                HierarchyTools.Register(registry);
                ObjectTools.Register(registry);
                ActionTools.Register(registry);
                AssetTools.Register(registry);
                LogTools.Register(registry, m_logRing, info);
                ScreenshotTool.Register(registry, this);
                ModTools.Register(registry, m_poller, log, m_edit);
                ObjectEditTools.Register(registry, m_edit);
                ComponentTools.Register(registry, m_edit);
                ActionEditTools.Register(registry, m_edit);
                PlaytestTools.Register(registry, m_edit);
                BatchTool.Register(registry, m_poller); // last: steps are looked up in the registry at call time

                RpcHandler handler = new RpcHandler(registry, m_dispatcher, log, MainThreadTimeout);
                m_server = new BridgeHttpServer(new BridgeHttpServerOptions { Port = m_config.Port, Token = token }, handler.Handle, log);
                m_server.Start();
                Logger.LogInfo($"Listening on http://127.0.0.1:{m_server.Port}/rpc ({string.Join(", ", new System.Collections.Generic.List<string>(registry.Names).ToArray())}); " +
                               $"writes {(m_config.EnableWrites ? "enabled" : "disabled")}, publish block {(block.IsComplete() ? "complete" : "INCOMPLETE")}");
            }
            catch (Exception e)
            {
                Logger.LogError($"Failed to start the RPC server on 127.0.0.1:{m_config.Port} (port in use?): {e.GetType().Name}: {e.Message}. " +
                                "The bridge is idle: no tools are served and no per-frame work runs. The Workshop publish block stays active.");
                StopBridgeServices();
            }
        }

        /// <summary>
        /// Tears down everything the server needs (dispatcher, poller, edit observer, registry, save-watch callback) so Update()
        /// does nothing. The publish block (separate Harmony id) is untouched.
        /// </summary>
        private void StopBridgeServices()
        {
            try { m_server?.Stop(); } catch { /* ignore */ }
            m_server = null;
            try { m_dispatcher?.Shutdown(); } catch { /* ignore */ }
            m_dispatcher = null;
            m_poller = null;
            m_edit = null;
            m_registry = null;
            SaveWatch.OnSaved = null;
        }

        /// <summary>Installs the block; the returned monitor re-verifies the required prefixes live (guard step 2, get_status).</summary>
        private PublishBlockMonitor InstallPublishBlock(IBridgeLog log)
        {
            try
            {
                PublishBlocker.Configure(log, message => Debug.LogWarning(message));
                System.Collections.Generic.List<BlockTarget> targets = PublishBlockTargets.Build(typeof(EditorSystem).Assembly);
                PublishBlockStatus status = PublishBlocker.Apply(new Harmony(PublishBlocker.HarmonyId), targets);
                PublishBlockMonitor monitor = new PublishBlockMonitor(status, targets, PublishBlocker.HarmonyId, log);
                if (status.Complete)
                {
                    Logger.LogInfo($"Workshop publish block active: {status.PatchedCount} methods patched. Publishing to the Workshop is disabled while this plugin is enabled.");
                }
                else
                {
                    Logger.LogError($"Workshop publish block INCOMPLETE ({status.PatchedCount} methods patched; missing: {string.Join("; ", status.Missing.ToArray())}). All write tools are disabled.");
                }
                return monitor;
            }
            catch (Exception e)
            {
                Logger.LogError($"Installing the Workshop publish block failed: {e.GetType().Name}: {e.Message}. All write tools are disabled.");
                return new PublishBlockMonitor(new PublishBlockStatus(false, false, 0, new[] { "installer failed: " + e.Message }), null, PublishBlocker.HarmonyId, log);
            }
        }

        /// <summary>
        /// Harmony postfix on EditorSystem.SaveCurrentMod that only records "a save happened" (moves the edit tracker's
        /// clean point after user saves). Separate Harmony id; failure only disables the tracker (editor flag fallback).
        /// </summary>
        private void InstallSaveWatch(IBridgeLog log)
        {
            try
            {
                System.Reflection.MethodInfo target = typeof(EditorSystem).GetMethod("SaveCurrentMod",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public, null, new[] { typeof(bool) }, null);
                string error = SaveWatch.Install(new Harmony(SaveWatch.HarmonyId), target);
                if (error != null)
                {
                    Logger.LogWarning($"Save watch not installed ({error}); unsaved-edit tracking falls back to the editor's dirty flag.");
                    return;
                }
                EditServices edit = m_edit;
                SaveWatch.OnSaved = () => edit.OnSaved();
            }
            catch (Exception e)
            {
                Logger.LogWarning($"Save watch not installed ({e.GetType().Name}: {e.Message}); unsaved-edit tracking falls back to the editor's dirty flag.");
            }
        }

        private void Update()
        {
            // Observe the editor's undo stacks first (changes made by the editor's last Update / draw are seen before
            // any tool runs this frame).
            m_edit?.Observe();
            // Drain never throws: tool exceptions are captured into the waiting request.
            m_dispatcher?.Drain(MaxWorkItemsPerFrame, MaxWorkMillisecondsPerFrame);
            m_poller?.Tick();
            m_registry?.WriteQueue.Pump();
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
