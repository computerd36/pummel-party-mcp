using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using PummelPartyMcp.Core;
using PummelPartyMcp.Game;
using PummelPartyMcp.Hooks;
using PummelPartyMcp.Safety;
using PummelPartyMcp.Tools;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace PummelPartyMcp
{
    /// <summary>Values captured once on the main thread at startup (safe to read from any thread).</summary>
    internal sealed class PluginRuntimeInfo
    {
        public string GameVersion;
        public string UnityVersion;
        public string SessionId;
    }

    /// <summary>
    /// Entry point. Decision P2: the plugin is active only while the Mod Editor is open. Outside the editor only one
    /// SceneManager.sceneLoaded callback exists (one scene-name compare and one static field read per scene load); the component
    /// is disabled, so Update does not run. When the editor scene loads, <see cref="Arm"/> installs the Workshop publish block
    /// first, then starts the token-protected HTTP API on 127.0.0.1 and the record-only hooks. When the editor object is gone,
    /// <see cref="BeginDisarm"/> stops the listener and refuses queued work, and <see cref="FinishDisarm"/> (after a short grace
    /// for running work) removes every patch and goes dormant again. The state machine is <see cref="EditorLifecycle"/> (Core).
    /// The HTTP thread only parses requests; all Unity/game access is queued to Update() via the dispatcher.
    /// </summary>
    [BepInPlugin(PluginInfo.Guid, PluginInfo.Name, PluginInfo.Version)]
    public sealed class PummelPartyMcpPlugin : BaseUnityPlugin
    {
        private const int MaxWorkItemsPerFrame = 16;
        private const double MaxWorkMillisecondsPerFrame = 8.0;
        private static readonly TimeSpan MainThreadTimeout = TimeSpan.FromSeconds(10);

        private PluginConfig m_config;
        private bool m_enabledAtStart;
        private IPluginLog m_log;
        private PluginRuntimeInfo m_info;
        private readonly EditorLifecycle m_lifecycle = new EditorLifecycle();
        private readonly System.Diagnostics.Stopwatch m_clock = System.Diagnostics.Stopwatch.StartNew();
        private bool m_sceneHooked;
        private bool m_shutDown;

        // Kept for the whole game run once created (Q7: playtest event ids and the undo tracker continue across editor sessions).
        private LogRingBuffer m_logRing;
        private FramePoller m_poller;
        private EditServices m_edit;
        private ToolRegistry m_registry;

        // New per editor session.
        private PublishBlockMonitor m_monitor;
        private MainThreadDispatcher m_dispatcher;
        private RpcHttpServer m_server;
        private volatile ToolAccess m_access = ToolAccess.Starting;
        private volatile string m_blockMissing;
        private int m_cancelledAtDisarm;
        private bool m_logHooked;

        // Object scan for an editor kept alive without EditorSystem.Instance (only while armed and Instance is null).
        private static readonly TimeSpan ScanInterval = TimeSpan.FromSeconds(0.5);
        private bool m_scanned;
        private TimeSpan m_lastScanAt;
        private bool m_lastScanResult;

        private TimeSpan Now => m_clock.Elapsed;

        /// <summary>Application.version, or null if it cannot be read (the gate then treats the version as untested).</summary>
        private static string ReadGameVersion()
        {
            try
            {
                return Application.version;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private void Awake()
        {
            m_config = new PluginConfig(Config);
            m_enabledAtStart = m_config.PluginEnabled;
            enabled = false; // no Update while dormant
            if (!m_enabledAtStart)
            {
                Logger.LogInfo($"{PluginInfo.Name} {PluginInfo.Version} is disabled ([General] PluginEnabled = false): no RPC server, no Workshop publish block.");
                return;
            }

            m_log = new BepInExPluginLog(Logger);
            GameReflection.Log = m_log;

            if (transform.parent == null)
            {
                DontDestroyOnLoad(gameObject); // BepInEx's manager object normally already is; harmless if so.
            }

            m_info = new PluginRuntimeInfo
            {
                GameVersion = ReadGameVersion(),
                UnityVersion = Application.unityVersion,
                SessionId = Guid.NewGuid().ToString("N"),
            };
            Logger.LogInfo($"{PluginInfo.Name} {PluginInfo.Version} (api v{PluginInfo.ApiVersion}); game version {m_info.GameVersion}, Unity {m_info.UnityVersion}. " +
                           "Dormant: the plugin starts when the Mod Editor opens and stops when it closes.");

            SceneManager.sceneLoaded += OnSceneLoaded;
            m_sceneHooked = true;
            if (InstanceExistsOr(false))
            {
                OnSceneLoadedCore(SceneManager.GetActiveScene().name); // loaded while the editor is already open
            }
        }

        // ------------------------------------------------------------------ lifecycle events

        /// <summary>The only plugin code that runs outside the Mod Editor: one scene-name compare and one static field read.</summary>
        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            try
            {
                OnSceneLoadedCore(scene.name);
            }
            catch (Exception e)
            {
                Logger.LogError($"scene-loaded handler failed: {e.GetType().Name}: {e.Message}");
            }
        }

        private void OnSceneLoadedCore(string sceneName)
        {
            if (m_shutDown)
            {
                return;
            }
            // Dormant: one static field read only. Armed: also the object scan (an editor kept alive without EditorSystem.Instance).
            bool editor = m_lifecycle.State == LifecycleState.Dormant ? InstanceExistsOr(false) : EditorPresent(fresh: true);
            switch (m_lifecycle.OnSceneLoaded(sceneName, editor, Now))
            {
                case LifecycleAction.Arm:
                    Arm("scene " + sceneName);
                    break;
                case LifecycleAction.BeginDisarm:
                    BeginDisarm(); // the editor never appeared and another scene replaced its scene
                    break;
            }
        }

        private void Update()
        {
            if (m_shutDown)
            {
                return;
            }
            LifecycleAction action = LifecycleAction.None;
            try
            {
                bool editor = EditorPresent(fresh: false);
                if (!editor && m_lifecycle.SeenEditor && (m_lifecycle.State == LifecycleState.Active || m_lifecycle.State == LifecycleState.Failed))
                {
                    editor = EditorPresent(fresh: true); // about to disarm: never on a cached scan result
                }
                action = m_lifecycle.OnFrame(editor, Now, m_lifecycle.State == LifecycleState.Disarming && WorkIdle());
            }
            catch (Exception e)
            {
                GameReflection.WarnOnce("lifecycle.frame", "lifecycle check failed: " + e.GetType().Name + ": " + e.Message);
            }
            if (action == LifecycleAction.BeginDisarm)
            {
                BeginDisarm();
            }
            if (m_lifecycle.State != LifecycleState.Dormant)
            {
                Pump();
            }
            if (action == LifecycleAction.FinishDisarm)
            {
                FinishDisarm();
            }
        }

        private void Pump()
        {
            // Observe the editor's undo stacks first (changes made by the editor's last Update / draw are seen before
            // any tool runs this frame).
            m_edit?.Observe();
            // Drain never throws: tool exceptions are captured into the waiting request.
            m_dispatcher?.Drain(MaxWorkItemsPerFrame, MaxWorkMillisecondsPerFrame);
            m_poller?.Tick();
            m_registry?.WriteQueue.Pump();
        }

        /// <summary>Nothing queued or running (read only while disarming).</summary>
        private bool WorkIdle()
        {
            if (m_poller != null && m_poller.Count > 0)
            {
                return false;
            }
            if (m_registry != null && (m_registry.WriteQueue.IsBusy || m_registry.WriteQueue.PendingCount > 0))
            {
                return false;
            }
            return m_dispatcher == null || (m_dispatcher.PendingCount == 0 && m_dispatcher.RunningCount == 0);
        }

        /// <summary>EditorSystem.Instance only (the dormant check: one static field read per scene load).</summary>
        private bool InstanceExistsOr(bool onError)
        {
            try
            {
                return ReadInstanceExists();
            }
            catch (Exception e)
            {
                GameReflection.WarnOnce("lifecycle.editor", "cannot read EditorSystem.Instance (game version changed?): " + e.GetType().Name + ": " + e.Message);
                return onError;
            }
        }

        /// <summary>
        /// While armed or disarming: EditorSystem.Instance first; when it is null or unreadable, any active EditorSystem object
        /// (the game nulls Instance in every editor's OnDestroy, so after a rare exit that kept an old editor object alive, closing
        /// a second editor nulls Instance while the old one lives). The scan runs at most every <see cref="ScanInterval"/> unless
        /// <paramref name="fresh"/>; a scan that fails counts as "editor present" (the safe side).
        /// </summary>
        private bool EditorPresent(bool fresh)
        {
            if (InstanceExistsOr(false))
            {
                return true;
            }
            if (!fresh && m_scanned && Now - m_lastScanAt < ScanInterval)
            {
                return m_lastScanResult;
            }
            try
            {
                m_lastScanResult = ScanForEditors();
            }
            catch (Exception e)
            {
                GameReflection.WarnOnce("lifecycle.scan", "cannot look for EditorSystem objects: " + e.GetType().Name + ": " + e.Message);
                m_lastScanResult = true;
            }
            m_scanned = true;
            m_lastScanAt = Now;
            return m_lastScanResult;
        }

        /// <summary>Own method, so a missing game member fails here (JIT) and is caught by the caller.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool ReadInstanceExists() => EditorAccess.EditorOrNull() != null;

        /// <summary>Active EditorSystem objects (needs only the type, not the Instance member).</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool ScanForEditors() => UnityEngine.Object.FindObjectsOfType<EditorSystem>().Length > 0;

        /// <summary>Whether EditorSystem.Instance can be read at all (false: game version changed?).</summary>
        private static bool InstanceReadable()
        {
            try
            {
                ReadInstanceExists();
                return true;
            }
            catch
            {
                return false;
            }
        }

        // ------------------------------------------------------------------ arm

        /// <summary>
        /// Main thread, inside the scene-loaded callback (before the editor's Start and first frame, so before the File menu can
        /// open the publish dialog). Fail closed: the tools run only if every step passed; an incomplete publish block leaves a
        /// diagnostics-only server (ping, get_status, get_log, self_test) for the rest of this editor session.
        /// </summary>
        private void Arm(string trigger)
        {
            System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
            bool reported = false;
            bool blockInstalled = false;
            try
            {
                // 1. Publish block first, before anything else can run or throw. InstallPublishBlock never throws.
                m_monitor = InstallPublishBlock();
                blockInstalled = true;
                m_access = ToolAccess.Starting;
                m_blockMissing = null;
                m_scanned = false;
                HookLog(); // so the arm messages reach get_log
                m_config.RefreshIfChanged();
                RemoveLeftoverHooks();
                PublishBlockStatus block = m_monitor.Current();
                if (!block.Complete)
                {
                    m_blockMissing = string.Join("; ", block.Missing.ToArray());
                    ArmError($"the Workshop publish block could not be installed completely (missing: {m_blockMissing}). " +
                             "The editor and mod tools are off for this Mod Editor session (only ping, get_status, get_log and self_test answer).");
                }
                bool editorReadable = InstanceReadable();
                if (!editorReadable)
                {
                    ArmError("cannot read EditorSystem.Instance (game version changed?). The Workshop publish block is installed, but the editor and " +
                             "mod tools are off for this Mod Editor session (only ping, get_status, get_log and self_test answer).");
                }
                bool sessionOk = block.Complete && editorReadable;

                LogVersionGate();

                // 1b. Review M1: an import interrupted by a crash while the game decoded it would be decoded again on every reopen of
                // that sandbox mod; quarantine it (rename only, sandbox mods only, hash-checked) before the editor reads its mod list.
                if (sessionOk && m_config.EnableWrites && m_config.ServerEnabled)
                {
                    RecoverImports();
                }

                // 2. RPC server (optional) and the record-only hooks.
                bool toolsOn;
                string serverText;
                string hooksText = "";
                if (!m_config.ServerEnabled)
                {
                    serverText = "RPC server off ([Server] Enabled = false)";
                    toolsOn = sessionOk;
                }
                else if (!StartServer(sessionOk, out serverText))
                {
                    toolsOn = false;
                }
                else
                {
                    toolsOn = sessionOk;
                    if (toolsOn)
                    {
                        hooksText = ", " + InstallHooks();
                    }
                }
                m_access = !block.Complete ? ToolAccess.BlockIncomplete : !editorReadable ? ToolAccess.EditorUnreadable : ToolAccess.Enabled;
                m_lifecycle.Armed(toolsOn, EditorPresent(fresh: true), Now, DateTime.UtcNow);
                reported = true;
                enabled = true;
                Logger.LogInfo($"Mod Editor opened ({trigger}): Workshop publish block " +
                               (block.Complete ? $"active ({block.PatchedCount} methods)" : "INCOMPLETE") +
                               $"{hooksText}; {serverText}; armed in {sw.ElapsedMilliseconds} ms.");
            }
            catch (Exception e)
            {
                if (!blockInstalled)
                {
                    m_monitor = InstallPublishBlock(); // never leave the editor without the block
                }
                ArmError($"starting for the Mod Editor failed: {e.GetType().Name}: {e.Message}. The tools are off for this Mod Editor session.");
                StopSessionServer(); // fail closed: no tool runs; the publish block keeps what was installed
                if (!reported)
                {
                    m_lifecycle.ArmAborted(EditorPresent(fresh: true), Now, DateTime.UtcNow);
                }
                enabled = true; // the disarm still has to notice the editor closing
            }
        }

        /// <summary>Error in the BepInEx log and in the Unity log (the editor's Log window shows it).</summary>
        private void ArmError(string message)
        {
            Logger.LogError(message);
            try
            {
                Debug.LogError("[PummelPartyMcp] " + message);
            }
            catch
            {
                // the Unity log is a second destination only
            }
        }

        private void HookLog()
        {
            if (m_logHooked)
            {
                return;
            }
            if (m_logRing == null)
            {
                m_logRing = new LogRingBuffer(2000);
            }
            Application.logMessageReceivedThreaded += OnUnityLog;
            m_logHooked = true;
        }

        private void UnhookLog()
        {
            if (!m_logHooked)
            {
                return;
            }
            Application.logMessageReceivedThreaded -= OnUnityLog;
            m_logHooked = false;
        }

        private void LogVersionGate()
        {
            GameVersionStatus version = m_config.CurrentGameVersion(m_info.GameVersion);
            if (version.WritesAllowed)
            {
                Logger.LogInfo(version.IsTested
                    ? $"Game version {m_info.GameVersion} is in the tested list ({string.Join(", ", version.Tested)})."
                    : $"Game version {m_info.GameVersion} is not in the tested list ({string.Join(", ", version.Tested)}) but is accepted by [Safety] AcceptedGameVersion.");
            }
            else
            {
                Logger.LogWarning(GameVersionGate.Refusal(version).Message + " (this is a warning: read tools keep working)");
            }
        }

        private void RecoverImports()
        {
            try
            {
                foreach (string line in ImportJournal.RecoverAll(Path.Combine(Application.persistentDataPath, "WorkshopMods")))
                {
                    Logger.LogWarning("import recovery: " + line);
                }
            }
            catch (Exception e)
            {
                Logger.LogWarning("import recovery skipped: " + e.Message);
            }
        }

        /// <summary>
        /// Hook patches that an earlier failed removal left behind are removed before installing again (no doubles). Leftover
        /// publish-block prefixes stay: the block is installed first (a second blocking prefix on a method changes nothing) and the
        /// next removal takes every prefix of the id.
        /// </summary>
        private void RemoveLeftoverHooks()
        {
            foreach (string id in new[] { SaveWatch.HarmonyId, ModSwitchGuard.HarmonyId, PlaytestHooks.HarmonyId })
            {
                PatchRemoval.Result r = PatchRemoval.Remove(id);
                if (r.Removed > 0 || !r.Clean)
                {
                    Logger.LogWarning($"Removed {r.Removed} patched methods of {id} left from the last Mod Editor session" +
                                      (r.Clean ? "." : $"; still left: {string.Join(", ", r.Left.Concat(r.Errors).ToArray())}."));
                }
            }
        }

        /// <summary>Installs the block; the returned monitor re-verifies the required prefixes live (guard step 2, get_status).</summary>
        private PublishBlockMonitor InstallPublishBlock()
        {
            try
            {
                PublishBlocker.Configure(m_log, message => Debug.LogWarning(message));
                List<BlockTarget> targets = PublishBlockTargets.Build(typeof(EditorSystem).Assembly);
                PublishBlockStatus status = PublishBlocker.Apply(new Harmony(PublishBlocker.HarmonyId), targets);
                return new PublishBlockMonitor(status, targets, PublishBlocker.HarmonyId, m_log);
            }
            catch (Exception e)
            {
                return new PublishBlockMonitor(new PublishBlockStatus(false, false, 0, new[] { "installer failed: " + e.GetType().Name + ": " + e.Message }),
                    null, PublishBlocker.HarmonyId, m_log);
            }
        }

        /// <summary>Token, then a new dispatcher, handler and listener for this editor session. False (logged) when it cannot start.</summary>
        private bool StartServer(bool blockComplete, out string text)
        {
            string tokenPath = Path.Combine(Paths.ConfigPath, PluginInfo.TokenFileName);
            string token;
            try
            {
                token = TokenAuth.LoadOrCreate(tokenPath, out bool created);
                Logger.LogInfo((created ? "Created new token file: " : "Using token file: ") + tokenPath);
            }
            catch (Exception e)
            {
                ArmError($"cannot read or create the token file {tokenPath}: {e.Message}. No RPC server; the tools are off for this Mod Editor session.");
                text = "no RPC server (token file problem)";
                return false;
            }

            int port = m_config.Port;
            try
            {
                EnsureServices();
                ModTools.ResetSession();
                ScreenshotTool.StartSession();
                m_dispatcher = new MainThreadDispatcher();
                RpcHandler handler = new RpcHandler(m_registry, m_dispatcher, m_log, MainThreadTimeout,
                    method => SessionGate.Check(m_access, method, m_blockMissing));
                m_server = new RpcHttpServer(new RpcHttpServerOptions { Port = port, Token = token }, handler.Handle, m_log);
                m_server.Start();
                text = $"listening on http://127.0.0.1:{m_server.Port}/rpc, " + (blockComplete
                    ? $"writes {(m_config.EnableWrites ? "enabled" : "disabled")}"
                    : "only ping, get_status, get_log and self_test answer");
                return true;
            }
            catch (Exception e)
            {
                ArmError($"failed to start the RPC server on 127.0.0.1:{port} (port in use?): {e.GetType().Name}: {e.Message}. " +
                         "The tools are off for this Mod Editor session; the Workshop publish block stays until the Mod Editor closes.");
                StopSessionServer();
                text = "no RPC server (port problem)";
                return false;
            }
        }

        /// <summary>Stops this session's listener and dispatcher (queued calls fail). Never throws.</summary>
        private void StopSessionServer()
        {
            try { m_server?.Stop(); } catch { /* ignore */ }
            m_server = null;
            try { m_dispatcher?.Shutdown(); } catch { /* ignore */ }
            m_dispatcher = null;
        }

        /// <summary>Created at the first editor session with the server on, then kept for the whole game run.</summary>
        private void EnsureServices()
        {
            if (m_registry != null)
            {
                return;
            }
            IPluginLog log = m_log;
            PluginRuntimeInfo info = m_info;
            PluginConfig config = m_config;
            FramePoller poller = new FramePoller(log);
            EditServices edit = new EditServices(poller, log);
            // Config values are re-read from the file on every guarded call (BepInEx 5.4 has no file watcher): see PluginConfig.RefreshIfChanged.
            Func<bool> writesEnabled = () => { config.RefreshIfChanged(); return config.EnableWrites; };
            Func<GameVersionStatus> gameVersionPeek = () => config.CurrentGameVersion(info.GameVersion);
            Func<GameVersionStatus> gameVersionMain = () => { config.RefreshIfChanged(); return gameVersionPeek(); };
            // The current editor session's monitor (a new one per session); none = not complete (fail closed).
            Func<PublishBlockStatus> blockStatus = () => m_monitor?.Current() ?? PublishBlockStatus.NotApplied;
            Func<bool> blockComplete = () => blockStatus().Complete;
            WriteSafety safety = new WriteSafety(writesEnabled, blockComplete, gameVersionMain, new GameEditorGuardHost(log));
            ToolRegistry registry = new ToolRegistry(safety, new WriteQueue(log));
            StatusTools.Register(registry, info, writesEnabled, blockStatus, gameVersionPeek, gameVersionMain, edit, m_lifecycle);
            SelfTestTool.Register(registry, info, gameVersionMain, blockStatus);
            HierarchyTools.Register(registry);
            ObjectTools.Register(registry);
            ActionTools.Register(registry);
            AssetTools.Register(registry);
            AssetImportTools.Register(registry, edit, () => { config.RefreshIfChanged(); return config.AssetLimits; });
            PrefabTools.Register(registry, edit);
            LogTools.Register(registry, m_logRing, info);
            ScreenshotTool.Register(registry, this, poller, log);
            ModTools.Register(registry, poller, log, edit);
            ObjectEditTools.Register(registry, edit);
            ComponentTools.Register(registry, edit);
            SettingsTools.Register(registry, edit);
            ActionEditTools.Register(registry, edit);
            PlaytestTools.Register(registry, edit, info);
            BatchTool.Register(registry, poller); // last: steps are looked up in the registry at call time
            m_poller = poller;
            m_edit = edit;
            m_registry = registry;
        }

        /// <summary>The record-only hooks and the mod-switch guard. A failure only reduces features (logged).</summary>
        private string InstallHooks()
        {
            InstallSaveWatch();
            InstallModSwitchGuard();
            InstallPlaytestHooks();
            return $"playtest hooks {PlaytestHooks.TriggerHooks + PlaytestHooks.ActionHooks}, save watch {(SaveWatch.Installed ? "on" : "OFF")}, " +
                   $"mod-switch guard {(ModSwitchGuard.Installed ? "on" : "OFF")}";
        }

        /// <summary>
        /// Harmony postfix on EditorSystem.SaveCurrentMod that only records "a save happened" (moves the edit tracker's
        /// clean point after user saves). Separate Harmony id; failure only disables the tracker (editor flag fallback).
        /// </summary>
        private void InstallSaveWatch()
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

        /// <summary>
        /// Harmony prefixes that refuse opening, creating, closing and deleting a mod only while capture_preview_image waits for the
        /// editor's screenshot (bounded; targets and reasons in ModSwitchGuard). Separate Harmony id; not part of the publish block. If
        /// any target is missing or cannot be patched, only capture_preview_image refuses to run (fail closed for that tool);
        /// everything else keeps working.
        /// </summary>
        private void InstallModSwitchGuard()
        {
            try
            {
                const System.Reflection.BindingFlags pub = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public;
                const System.Reflection.BindingFlags priv = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.DeclaredOnly;
                System.Reflection.MethodBase open = typeof(EditorSystem).GetMethod("OpenMod", pub, null, new[] { typeof(string) }, null);
                System.Reflection.MethodBase create = typeof(EditorSystem).GetMethod("CreateNewMod", pub, null, new[] { typeof(string), typeof(PummelModType), typeof(string) }, null);
                // the New Mod dialog's action: EditorSystem.CreateNewMod, then always a mod-list entry (NewOption is a private nested struct)
                System.Reflection.MethodBase newModDialog = typeof(NewModPopup).GetMethods(priv)
                    .FirstOrDefault(m => m.Name == "CreateNewMod" && m.GetParameters().Length == 1);
                System.Reflection.MethodBase deleteFromMenu = typeof(MainMenuWidget).GetMethod("DeleteModResult", priv, null, new[] { typeof(int) }, null);
                System.Reflection.MethodBase deleteFromDialog = typeof(LoadModPopup).GetMethod("DeleteModResult", priv, null, new[] { typeof(int) }, null);
                System.Reflection.MethodBase close = typeof(EditorSystem).GetMethod("SetCurrentMod", pub, null, new[] { typeof(PummelMod) }, null);
                System.Reflection.MethodBase shutdown = typeof(EditorSystem).GetMethod("OnDisable", pub, null, Type.EmptyTypes, null);
                ModSwitchGuard.Log = m_log;
                ModSwitchGuard.ExtraSink = message => Debug.LogWarning(message);
                string error = ModSwitchGuard.Install(new Harmony(ModSwitchGuard.HarmonyId),
                    new[] { open, newModDialog, create, deleteFromMenu, deleteFromDialog }, close, shutdown);
                if (error != null)
                {
                    Logger.LogWarning($"Mod-switch guard not installed ({error}); capture_preview_image is disabled.");
                }
            }
            catch (Exception e)
            {
                Logger.LogWarning($"Mod-switch guard not installed ({e.GetType().Name}: {e.Message}); capture_preview_image is disabled.");
            }
        }

        /// <summary>
        /// Recording-only postfixes for the playtest event feed (trigger fired / action executed). Failure only removes those
        /// two event types; the diff-based events and get_playtest_state do not depend on them.
        /// </summary>
        private void InstallPlaytestHooks()
        {
            try
            {
                List<string> problems = PlaytestHooks.Install(new Harmony(PlaytestHooks.HarmonyId));
                if (problems.Count > 0)
                {
                    Logger.LogWarning($"Playtest hooks: {PlaytestHooks.TriggerHooks} trigger and {PlaytestHooks.ActionHooks} action hooks installed; problems: {string.Join("; ", problems.ToArray())}. " +
                                      "get_playtest_events may lack trigger_fired / action_executed entries.");
                }
            }
            catch (Exception e)
            {
                Logger.LogWarning($"Playtest hooks not installed ({e.GetType().Name}: {e.Message}); get_playtest_events has no trigger_fired / action_executed entries.");
            }
        }

        // ------------------------------------------------------------------ disarm

        /// <summary>The editor is gone: no new connections, queued calls fail with EDITOR_NOT_OPEN; running work gets a short grace.</summary>
        private void BeginDisarm()
        {
            m_access = ToolAccess.Stopping;
            int failed = 0;
            try { m_server?.Stop(); } catch (Exception e) { Logger.LogWarning("stopping the RPC server: " + e.Message); }
            m_server = null;
            try { m_dispatcher?.Shutdown(SessionGate.EditorClosed); } catch (Exception e) { Logger.LogWarning("stopping the dispatcher: " + e.Message); }
            try
            {
                if (m_registry != null)
                {
                    failed += m_registry.WriteQueue.FailPending(SessionGate.EditorClosed);
                }
                failed += ScreenshotTool.BeginStop(SessionGate.EditorClosed); // no capture of the main menu
            }
            catch (Exception e)
            {
                Logger.LogWarning("failing queued calls: " + e.Message);
            }
            m_cancelledAtDisarm = failed;
        }

        /// <summary>
        /// After the grace: fails what is still running, removes the hooks and then the publish block (last), and goes dormant.
        /// Every step is guarded: the plugin always ends dormant. Patches that could not be removed are NOT inert: a leftover
        /// playtest postfix still runs (and returns at once) in every mod minigame, and a leftover block prefix still blocks the
        /// main-menu Workshop delete. One Error names them; the next editor open removes the hook leftovers again.
        /// </summary>
        private void FinishDisarm()
        {
            int cancelled = m_cancelledAtDisarm;
            try
            {
                if (m_registry != null)
                {
                    cancelled += m_registry.WriteQueue.Reset(SessionGate.EditorClosed, SessionGate.EditorClosedWhileRunning);
                }
                m_poller?.Clear();
                cancelled += ScreenshotTool.ResetSession(SessionGate.EditorClosedWhileRunning);
                StopAllCoroutines(); // only the screenshot captures run on this component; their queue was reset above
                ModTools.ResetSession();
                ModSwitchGuard.Disarm();
                if (m_dispatcher != null)
                {
                    cancelled += m_dispatcher.AbandonRunning(SessionGate.EditorClosedWhileRunning);
                }
            }
            catch (Exception e)
            {
                Logger.LogError($"ending the running calls failed: {e.GetType().Name}: {e.Message}");
            }
            m_dispatcher = null;
            PlaytestHooks.Collecting = false;
            try
            {
                PlaytestLogicRecorder.Reset();
            }
            catch
            {
                // only drops two references
            }

            int restored = 0;
            List<string> left = new List<string>();
            void Take(string what, Func<PatchRemoval.Result> remove)
            {
                try
                {
                    PatchRemoval.Result r = remove();
                    restored += r.Removed;
                    left.AddRange(r.Left.Select(n => what + " " + n));
                    left.AddRange(r.Errors.Select(n => what + " " + n));
                }
                catch (Exception e)
                {
                    left.Add(what + ": " + e.GetType().Name + ": " + e.Message);
                }
            }
            Take("save watch", SaveWatch.Uninstall);
            Take("mod-switch guard", ModSwitchGuard.Uninstall);
            Take("playtest hooks", PlaytestHooks.Uninstall);
            Take("publish block", PublishBlocker.Remove); // last: the editor is gone
            m_monitor = null;
            m_blockMissing = null;
            if (left.Count > 0)
            {
                Logger.LogError($"Mod Editor closed, but {left.Count} patches could not be removed ({string.Join("; ", left.ToArray())}). " +
                                "They stay in the game until the next Mod Editor open and close removes them: a leftover playtest hook still runs (and returns at once) " +
                                "in mod minigames, a leftover Workshop block still blocks the main-menu Workshop delete. Restart the game to be sure.");
            }

            string reason = m_lifecycle.DisarmReason ?? "the Mod Editor was closed";
            try { UnhookLog(); } catch { /* ignore */ }
            enabled = false;
            LifecycleAction next = m_lifecycle.Disarmed(EditorPresent(fresh: true)); // an editor object still there: arm again at once
            Logger.LogInfo($"Dormant again ({reason}): server stopped, {cancelled} calls cancelled, {restored} patched methods restored ({left.Count} left).");
            if (next == LifecycleAction.Arm)
            {
                Arm("again, while the plugin was stopping");
            }
        }

        // ------------------------------------------------------------------ quit

        private void OnDestroy() => Shutdown("OnDestroy");

        private void OnApplicationQuit() => Shutdown("OnApplicationQuit");

        /// <summary>The game quits: stop the listener, fail queued calls, drop the log listener. No unpatching (the process ends).</summary>
        private void Shutdown(string reason)
        {
            if (m_shutDown || !m_enabledAtStart)
            {
                return;
            }
            m_shutDown = true;
            try
            {
                if (m_sceneHooked)
                {
                    SceneManager.sceneLoaded -= OnSceneLoaded;
                    m_sceneHooked = false;
                }
                m_access = ToolAccess.Stopping;
                StopSessionServer();
                UnhookLog();
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

        private sealed class BepInExPluginLog : IPluginLog
        {
            private readonly ManualLogSource m_source;

            public BepInExPluginLog(ManualLogSource source)
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
