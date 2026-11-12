using System;

namespace PummelPartyMcp.Core
{
    /// <summary>What a write tool acts on. Every write tool must declare exactly one (enforced by the factories).</summary>
    public enum WriteGuardKind
    {
        /// <summary>Acts on the mod that is open in the editor: it must be a loaded test mod (Meta name and folder name).</summary>
        OpenMod,

        /// <summary>Creates or opens a mod: the target name must be a test-mod name and any open mod must be a test mod.</summary>
        Lifecycle,
    }

    /// <summary>Guard declaration of a write tool. Only obtainable through <see cref="OpenMod"/> or <see cref="Lifecycle"/>.</summary>
    public sealed class WriteGuard
    {
        public WriteGuardKind Kind { get; }

        /// <summary>OpenMod only: the tool may run while a playtest is starting/running/ending.</summary>
        public bool AllowPlayMode { get; }

        /// <summary>Lifecycle only: extracts the target mod name from the parsed arguments.</summary>
        public Func<object, string> TargetName { get; }

        private WriteGuard(WriteGuardKind kind, bool allowPlayMode, Func<object, string> targetName)
        {
            Kind = kind;
            AllowPlayMode = allowPlayMode;
            TargetName = targetName;
        }

        public static WriteGuard OpenMod(bool allowPlayMode = false) => new WriteGuard(WriteGuardKind.OpenMod, allowPlayMode, null);

        public static WriteGuard Lifecycle(Func<object, string> targetName) =>
            new WriteGuard(WriteGuardKind.Lifecycle, false, targetName ?? throw new ArgumentNullException(nameof(targetName)));
    }

    /// <summary>Editor state captured on the main thread for one guard evaluation.</summary>
    public sealed class EditorSnapshot
    {
        public bool EditorOpen;
        public bool IsLoading;
        public bool ModOpen;
        public bool ModLoaded;
        /// <summary>Meta.json name of the open mod (in memory).</summary>
        public string ModName;
        /// <summary>Set when the mod name could not be read because a game member is missing (game version changed?); the guard still refuses.</summary>
        public string ModNameError;
        /// <summary>Leaf folder name of the open mod.</summary>
        public string FolderName;
        /// <summary>EditorState == Editing.</summary>
        public bool IsEditing;
        public string StateName;

        public bool OpenModIsTestMod => ModOpen && TestModName.IsValid(ModName) && TestModName.IsValid(FolderName);

        public string OpenModLabel => FolderName == ModName || ModName == null ? $"'{FolderName}'" : $"'{FolderName}' (name '{ModName}')";
    }

    /// <summary>Game side of the guard. Called on the main thread only.</summary>
    public interface IEditorGuardHost
    {
        EditorSnapshot Capture();

        /// <summary>
        /// Workshop id hygiene for the open mod (only called when it is a loaded test mod): if its in-memory
        /// publishedFileId is not ModWorkshopItem.DefaultWorkshopID, reset it and log a warning.
        /// </summary>
        void EnforceDefaultWorkshopId();
    }

    /// <summary>Switches and providers every write tool is checked against.</summary>
    public sealed class WriteSafety
    {
        public Func<bool> WritesEnabled { get; }
        public Func<bool> PublishBlockComplete { get; }
        public IEditorGuardHost Host { get; }

        public WriteSafety(Func<bool> writesEnabled, Func<bool> publishBlockComplete, IEditorGuardHost host)
        {
            WritesEnabled = writesEnabled ?? throw new ArgumentNullException(nameof(writesEnabled));
            PublishBlockComplete = publishBlockComplete ?? throw new ArgumentNullException(nameof(publishBlockComplete));
            Host = host ?? throw new ArgumentNullException(nameof(host));
        }
    }

    /// <summary>The ordered guard checks. Pure: all state comes from the arguments.</summary>
    public static class WriteGuardEvaluator
    {
        /// <summary>Returns null when the write may proceed, otherwise the error to return to the caller.</summary>
        public static RpcException Evaluate(WriteSafety safety, WriteGuard guard, object args, EditorSnapshot snap)
        {
            if (safety == null || guard == null)
            {
                return new RpcException(ErrorCodes.Internal, "write tool without guard configuration");
            }
            if (!SafeCall(safety.WritesEnabled))
            {
                return new RpcException(ErrorCodes.WritesDisabled,
                    "write tools are disabled ([Safety] EnableWrites = false in BepInEx\\config\\com.pummelpartymcp.plugin.cfg)");
            }
            if (!SafeCall(safety.PublishBlockComplete))
            {
                return new RpcException(ErrorCodes.PublishBlockIncomplete,
                    "the Workshop publish block could not be fully installed (see get_status.publishBlock.missing and the BepInEx log); write tools are disabled");
            }
            if (snap == null || !snap.EditorOpen)
            {
                return new RpcException(ErrorCodes.EditorNotOpen, "the mod editor is not open");
            }
            if (snap.IsLoading)
            {
                return new RpcException(ErrorCodes.BusyLoading, "the editor is loading a mod");
            }

            switch (guard.Kind)
            {
                case WriteGuardKind.OpenMod:
                    if (!snap.ModOpen)
                    {
                        return new RpcException(ErrorCodes.NoModOpen, "no mod is open in the editor");
                    }
                    if (!snap.ModLoaded)
                    {
                        return new RpcException(ErrorCodes.BusyLoading, "the open mod has not finished loading");
                    }
                    if (!snap.OpenModIsTestMod)
                    {
                        return NotTestMod(snap, $"the open mod {snap.OpenModLabel} is not a test mod; write tools only act on mods whose name and folder start with '{TestModName.Prefix}'");
                    }
                    if (!guard.AllowPlayMode && !snap.IsEditing)
                    {
                        return new RpcException(ErrorCodes.InvalidState, $"the editor is not in edit mode (state {snap.StateName}); stop the playtest first");
                    }
                    return null;

                case WriteGuardKind.Lifecycle:
                    string target;
                    try
                    {
                        target = guard.TargetName(args);
                    }
                    catch (Exception e)
                    {
                        return new RpcException(ErrorCodes.Internal, "cannot read target name: " + e.Message);
                    }
                    string why = TestModName.Validate(target);
                    if (why != null)
                    {
                        return new RpcException(ErrorCodes.GuardNotTestMod, $"'{target}' is not a test-mod name: it {why}");
                    }
                    if (snap.ModOpen && !snap.OpenModIsTestMod)
                    {
                        return NotTestMod(snap, $"the mod {snap.OpenModLabel} is open and is not a test mod; close {snap.OpenModLabel} by hand first (File > Close Mod) so its unsaved work is not lost");
                    }
                    if (!snap.IsEditing)
                    {
                        return new RpcException(ErrorCodes.InvalidState, $"the editor is not in edit mode (state {snap.StateName}); stop the playtest first");
                    }
                    return null;

                default:
                    return new RpcException(ErrorCodes.Internal, "unknown guard kind");
            }
        }

        private static RpcException NotTestMod(EditorSnapshot snap, string message)
        {
            if (snap != null && snap.ModNameError != null)
            {
                // Still a refusal (fail closed), but say why: the name is unreadable, not "not a test mod".
                return new RpcException(ErrorCodes.Internal, "cannot read the open mod's name: " + snap.ModNameError + "; refusing the write");
            }
            return new RpcException(ErrorCodes.GuardNotTestMod, message);
        }

        private static bool SafeCall(Func<bool> f)
        {
            try
            {
                return f();
            }
            catch
            {
                return false; // fail closed
            }
        }
    }

    /// <summary>Handed to a write tool body after its guard passed (main thread).</summary>
    public sealed class WriteInvocation
    {
        public ToolContext Context { get; }
        public object Args { get; }
        public IWorkCompletion Completion { get; }
        public EditorSnapshot Snapshot { get; }
        public WriteSafety Safety { get; }

        public WriteInvocation(ToolContext context, object args, IWorkCompletion completion, EditorSnapshot snapshot, WriteSafety safety)
        {
            Context = context;
            Args = args;
            Completion = completion;
            Snapshot = snapshot;
            Safety = safety;
        }

        public T ArgsAs<T>() where T : class => (T)Args;

        /// <summary>
        /// Commit point of a body that changes the editor/mod outside <see cref="EditPipeline"/> (save, create/open, undo):
        /// call it right before the first change. Throws TIMEOUT (nothing changed, safe to retry) when the caller already
        /// gave up or less than <paramref name="needed"/> is left until the deadline; afterwards the caller waits for the
        /// body's own result (see <see cref="IWorkCompletion.TryCommit"/>).
        /// </summary>
        public void Commit(TimeSpan needed)
        {
            TimeSpan left = Completion.Remaining;
            if (left < needed)
            {
                throw new RpcException(ErrorCodes.Timeout,
                    $"not enough of the call's time limit left to finish it ({Math.Max(0, left.TotalSeconds):0.#} s left, {needed.TotalSeconds:0.#} s needed; " +
                    "it waited behind other write calls); nothing was changed (safe to retry)");
            }
            if (!Completion.TryCommit())
            {
                throw new RpcException(ErrorCodes.Timeout, "the call timed out before it changed anything; nothing was changed (safe to retry)");
            }
        }

        public void Complete(object result) => Completion.SetResult(result);

        public void Fail(Exception error) => Completion.SetException(error);
    }
}
