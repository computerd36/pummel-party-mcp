using System;

namespace PummelPartyMcp.Core
{
    /// <summary>
    /// Review L7: the time each file-writing tool needs left at its commit point (inv.Commit), and its own time limit. Nothing was
    /// measured in the game; the values are deliberately generous for the work done AFTER the commit, so a call that waited in the
    /// write queue is refused before it writes anything (TIMEOUT "nothing was changed") instead of timing out after files were
    /// written. If the main thread stalls beyond the limit anyway, the dispatcher's reply says "the change was applied" (never a
    /// silent TIMEOUT). The MCP server's per-tool timeouts are at least the limit + the 4 s commit grace + a margin
    /// (src/PummelPartyMcp.Server/src/client.ts, TOOL_TIMEOUTS_MS; a test checks both sides).
    /// </summary>
    public static class CommitBudgets
    {
        /// <summary>import_asset: copy (up to MaxImportFileMB), checks on the copy, then the game's synchronous decode (audio up to MaxAudioSeconds, an 8192 px texture + compression).</summary>
        public static readonly TimeSpan ImportTimeout = TimeSpan.FromSeconds(120);
        public static readonly TimeSpan Import = TimeSpan.FromSeconds(90);

        /// <summary>The other asset tools (set_import_settings, create_material, set_material, remove_asset).</summary>
        public static readonly TimeSpan AssetEditTimeout = TimeSpan.FromSeconds(30);

        /// <summary>set_import_settings: a compression/normals change re-imports (decodes the texture or OBJ again, up to the import caps).</summary>
        public static readonly TimeSpan ImportSettings = TimeSpan.FromSeconds(20);
        public static readonly TimeSpan Material = TimeSpan.FromSeconds(10);
        public static readonly TimeSpan Remove = TimeSpan.FromSeconds(10);

        /// <summary>Prefab tools: serialising a large prefab (and refreshing its instances on close).</summary>
        public static readonly TimeSpan PrefabTimeout = TimeSpan.FromSeconds(30);
        public static readonly TimeSpan CreatePrefab = TimeSpan.FromSeconds(15);
        public static readonly TimeSpan ClosePrefab = TimeSpan.FromSeconds(15);
        public static readonly TimeSpan OpenPrefab = TimeSpan.FromSeconds(10);

        /// <summary>capture_preview_image: the editor renders and writes the JPEG over the next frames; the call polls up to CaptureWait after the commit.</summary>
        public static readonly TimeSpan CaptureTimeout = TimeSpan.FromSeconds(25);
        public static readonly TimeSpan CaptureWait = TimeSpan.FromSeconds(12);
        public static readonly TimeSpan Capture = CaptureWait + TimeSpan.FromSeconds(3);

        /// <summary>save_mod: scene, data files, every .pmeta/.pmat/.pfab and Preview.jpg, plus the prefab verification.</summary>
        public static readonly TimeSpan SaveTimeout = TimeSpan.FromSeconds(20);
        public static readonly TimeSpan Save = TimeSpan.FromSeconds(12);

        /// <summary>The dispatcher's commit grace (MainThreadDispatcher.DefaultCommitGrace) that the server timeouts must cover too.</summary>
        public static TimeSpan Grace => MainThreadDispatcher.DefaultCommitGrace;

        /// <summary>(tool, own limit, budget at the commit point) for tests and docs.</summary>
        public static readonly (string Tool, TimeSpan Limit, TimeSpan Budget)[] All =
        {
            ("import_asset", ImportTimeout, Import),
            ("set_import_settings", AssetEditTimeout, ImportSettings),
            ("create_material", AssetEditTimeout, Material),
            ("set_material", AssetEditTimeout, Material),
            ("remove_asset", AssetEditTimeout, Remove),
            ("create_prefab", PrefabTimeout, CreatePrefab),
            ("open_prefab", PrefabTimeout, OpenPrefab),
            ("close_prefab", PrefabTimeout, ClosePrefab),
            ("capture_preview_image", CaptureTimeout, Capture),
            ("save_mod", SaveTimeout, Save),
        };
    }
}
