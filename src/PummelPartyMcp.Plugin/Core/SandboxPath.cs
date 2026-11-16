using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PummelPartyMcp.Core
{
    /// <summary>What the file system says about one path, without following a link at that path.</summary>
    public sealed class FileProbe
    {
        public bool Exists;
        public bool IsDirectory;
        public bool IsReparsePoint;

        /// <summary>Hard-link count of an existing file; -1 when it could not be determined.</summary>
        public int Links = -1;

        /// <summary>Set when the path could not be examined at all (the caller refuses).</summary>
        public string Error;
    }

    /// <summary>One asset as PummelMod.Save sees it: its file path and whether Save rewrites the file itself (not only its .pmeta).</summary>
    public sealed class SavedAssetFile
    {
        public string Name;
        public string FilePath;

        /// <summary>PummelMaterial (.pmat), PummelPrefab (.pfab), PummelVisualScript (.pgf): Save rewrites the file as well as the .pmeta.</summary>
        public bool RewritesFile;
    }

    /// <summary>
    /// Every file PummelMod.Save (and EditorSystem.SaveCurrentMod) writes for a mod (decompiled PummelMod.Save, SaveModSpecificData of
    /// the four mod types, ModDataReaderWriter, SavePreviewScreenshot): the Data files (a superset: files a mod type does not write are
    /// checked anyway) and, for every saved external asset, its .pmeta plus the file itself for materials, prefabs and visual scripts.
    /// </summary>
    public static class ModSaveTargets
    {
        public static readonly string[] DataFiles =
        {
            "Meta.json", "WorkshopItem.json", "Lighting.json", "PostProcessing.data", "MainScene.scene",
            "MinigameDefinitionData.json", "MapDetails.json", "ItemDetails.json", "ModSettings.json",
        };

        /// <summary>Written by SaveCurrentMod (save_mod), not by the playtest's PummelMod.Save.</summary>
        public const string PreviewFile = "Preview.jpg";

        /// <summary>The paths; an asset without a file path is refused (the game would write a .pmeta relative to the working folder).</summary>
        public static List<string> Build(string modRoot, IEnumerable<SavedAssetFile> assets, bool includePreview)
        {
            string data = Path.Combine(modRoot, "Data");
            List<string> paths = new List<string>();
            foreach (string f in DataFiles)
            {
                paths.Add(Path.Combine(data, f));
            }
            if (includePreview)
            {
                paths.Add(Path.Combine(data, PreviewFile));
            }
            foreach (SavedAssetFile a in assets)
            {
                if (string.IsNullOrEmpty(a.FilePath))
                {
                    throw new RpcException(ErrorCodes.InvalidState, $"the asset '{a.Name}' has no file path; saving would write its .pmeta outside the mod, so nothing was saved");
                }
                paths.Add(a.FilePath + AssetImportRules.MetaExtension);
                if (a.RewritesFile)
                {
                    paths.Add(a.FilePath);
                }
            }
            return paths;
        }
    }

    /// <summary>
    /// The one place that decides whether the plugin may let a file inside a sandbox mod be written, created, moved or deleted
    /// (docs/plugin-api.md, Safety: links). Every file operation of the plugin, and every game call that writes or deletes a file for
    /// the plugin, is preceded by <see cref="RequireInsideMod"/> on every path it touches, on the main thread, in the same dispatched
    /// step as the operation. Rules: both paths normalised; the path strictly below the mod root; the mod root a real folder (no
    /// reparse point) whose name passes the sandbox rule; no directory from the mod root down to the file's parent a reparse point
    /// (junction, directory symlink, mount point); the file itself, when it exists, a plain file (no symlink) with exactly one hard
    /// link (a link count that cannot be determined is refused). Reading through links stays allowed. Pure .NET + Win32; no Unity.
    /// </summary>
    public static class SandboxPath
    {
        /// <summary>Test seam: replaces the Win32 probe (null = the real one).</summary>
        public static Func<string, FileProbe> ProbeOverride;

        /// <summary>
        /// Returns the normalised full path when the plugin may write/create/move/delete <paramref name="fullPath"/> inside the sandbox
        /// mod at <paramref name="modRoot"/>; otherwise throws (LINK_REFUSED for links, GUARD_NOT_TEST_MOD for a non-sandbox root,
        /// INVALID_STATE for anything else). <paramref name="purpose"/> names the operation in the message.
        /// </summary>
        public static string RequireInsideMod(string modRoot, string fullPath, string purpose)
        {
            string why = Check(modRoot, fullPath, out string full, out string code);
            if (why != null)
            {
                throw new RpcException(code, $"{purpose}: {why}; nothing was written or deleted");
            }
            return full;
        }

        /// <summary><see cref="RequireInsideMod"/> for several paths; all are checked before the caller does anything.</summary>
        public static List<string> RequireAllInsideMod(string modRoot, IEnumerable<string> paths, string purpose)
        {
            List<string> result = new List<string>();
            foreach (string p in paths)
            {
                result.Add(RequireInsideMod(modRoot, p, purpose));
            }
            return result;
        }

        /// <summary>
        /// Like <see cref="RequireInsideMod"/> for a FOLDER the plugin or the game will create files in (or create): every existing
        /// component from the mod root down to and including <paramref name="folder"/> must be a real folder (no reparse point).
        /// </summary>
        public static string RequireFolderInsideMod(string modRoot, string folder, string purpose)
        {
            string why = CheckCore(modRoot, folder, true, out string full, out string code);
            if (why != null)
            {
                throw new RpcException(code, $"{purpose}: {why}; nothing was written or deleted");
            }
            return full;
        }

        /// <summary>Null when the path may be written; otherwise why not (with the error code to use).</summary>
        public static string Check(string modRoot, string fullPath, out string full, out string code) => CheckCore(modRoot, fullPath, false, out full, out code);

        private static string CheckCore(string modRoot, string fullPath, bool isFolder, out string full, out string code)
        {
            full = null;
            code = ErrorCodes.InvalidState;
            if (string.IsNullOrEmpty(modRoot) || string.IsNullOrEmpty(fullPath))
            {
                return "the mod folder or the target path is not known";
            }
            string textProblem = PathText(modRoot) ?? PathText(fullPath);
            if (textProblem != null)
            {
                return textProblem;
            }
            string root;
            try
            {
                root = Path.GetFullPath(modRoot).TrimEnd('\\', '/');
                full = Path.GetFullPath(fullPath);
            }
            catch (Exception e)
            {
                return "the path is invalid (" + e.Message + ")";
            }
            if (root.Length < 3)
            {
                return "the mod folder is a drive root";
            }
            string leaf = Path.GetFileName(root);
            string nameProblem = TestModName.Validate(leaf);
            if (nameProblem != null)
            {
                code = ErrorCodes.GuardNotTestMod;
                return $"the mod folder '{leaf}' is not a sandbox mod folder: it {nameProblem}";
            }
            string prefix = root + Path.DirectorySeparatorChar;
            if (full.Length <= prefix.Length || !full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return $"'{full}' is not strictly inside the mod folder '{leaf}'";
            }
            string rel = full.Substring(prefix.Length);
            string[] parts = rel.Split(Path.DirectorySeparatorChar);
            foreach (string part in parts)
            {
                if (part.Length == 0 || part == "." || part == ".." || part.EndsWith(".", StringComparison.Ordinal) || part.EndsWith(" ", StringComparison.Ordinal))
                {
                    return $"the path '{rel}' has an empty component or one ending with a dot or a space";
                }
            }

            FileProbe r = Probe(root);
            if (r.Error != null)
            {
                return $"the mod folder '{leaf}' cannot be examined ({r.Error})";
            }
            if (!r.Exists || !r.IsDirectory)
            {
                return $"the mod folder '{leaf}' does not exist as a folder";
            }
            if (r.IsReparsePoint)
            {
                code = ErrorCodes.LinkRefused;
                return $"the mod folder '{leaf}' itself is a link (junction/symlink/mount point)";
            }

            string cur = root;
            int dirCount = isFolder ? parts.Length : parts.Length - 1;
            for (int i = 0; i < dirCount; i++)
            {
                cur = cur + Path.DirectorySeparatorChar + parts[i];
                FileProbe d = Probe(cur);
                if (d.Error != null)
                {
                    return $"'{Rel(prefix, cur)}' cannot be examined ({d.Error})";
                }
                if (!d.Exists)
                {
                    break; // nothing below a missing folder exists either; it would be created as a real folder
                }
                if (d.IsReparsePoint)
                {
                    code = ErrorCodes.LinkRefused;
                    return $"'{Rel(prefix, cur)}' is a link (junction/symlink/mount point); the plugin never writes or deletes through links inside a mod folder";
                }
                if (!d.IsDirectory)
                {
                    return $"'{Rel(prefix, cur)}' exists as a file, not a folder";
                }
            }

            if (isFolder)
            {
                return null;
            }
            FileProbe f = Probe(full);
            if (f.Error != null)
            {
                return $"'{rel}' cannot be examined ({f.Error})";
            }
            if (f.Exists)
            {
                if (f.IsReparsePoint)
                {
                    code = ErrorCodes.LinkRefused;
                    return $"'{rel}' is a link (symlink/junction); the plugin never writes or deletes through links inside a mod folder";
                }
                if (f.IsDirectory)
                {
                    return $"'{rel}' is a folder, not a file";
                }
                if (f.Links < 0)
                {
                    code = ErrorCodes.LinkRefused;
                    return $"the hard-link count of '{rel}' cannot be determined, so it is treated as linked";
                }
                if (f.Links > 1)
                {
                    code = ErrorCodes.LinkRefused;
                    return $"'{rel}' is a hard-linked file ({f.Links} links): writing or deleting it would change the other copies";
                }
            }
            return null;
        }

        /// <summary>
        /// For listings (never throws): null when no directory from the mod root down to the file's parent and not the file itself is a
        /// link (reparse point or hard link); otherwise a short description of the first link found. Paths outside the root: null.
        /// </summary>
        public static string FindLink(string modRoot, string fullPath)
        {
            try
            {
                string root = Path.GetFullPath(modRoot).TrimEnd('\\', '/');
                string full = Path.GetFullPath(fullPath);
                string prefix = root + Path.DirectorySeparatorChar;
                if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || full.Length <= prefix.Length)
                {
                    return null;
                }
                FileProbe r = Probe(root);
                if (r.IsReparsePoint)
                {
                    return "the mod folder is a link";
                }
                string[] parts = full.Substring(prefix.Length).Split(Path.DirectorySeparatorChar);
                string cur = root;
                for (int i = 0; i < parts.Length - 1; i++)
                {
                    cur = cur + Path.DirectorySeparatorChar + parts[i];
                    FileProbe d = Probe(cur);
                    if (!d.Exists)
                    {
                        return null;
                    }
                    if (d.IsReparsePoint)
                    {
                        return $"'{Rel(prefix, cur)}' is a link";
                    }
                }
                FileProbe f = Probe(full);
                if (f.Exists && f.IsReparsePoint)
                {
                    return "the file is a symlink";
                }
                if (f.Exists && !f.IsDirectory && f.Links > 1)
                {
                    return $"the file has {f.Links} hard links";
                }
                return null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// Checks every path and refuses when ANY of them may not be written, naming each refused path (at most 8) in one error;
        /// nothing is written by the caller in that case. Used for whole-mod saves.
        /// </summary>
        public static void RequireAllWritable(string modRoot, IEnumerable<string> paths, string purpose)
        {
            List<string> problems = new List<string>();
            string code = null;
            int count = 0;
            foreach (string p in paths)
            {
                string why = Check(modRoot, p, out string _, out string c);
                if (why == null)
                {
                    continue;
                }
                count++;
                if (code == null || c == ErrorCodes.LinkRefused)
                {
                    code = c;
                }
                if (problems.Count < 8)
                {
                    problems.Add(why);
                }
            }
            if (count > 0)
            {
                throw new RpcException(code ?? ErrorCodes.InvalidState,
                    $"{purpose}: refused before anything was written, {count} target(s) cannot be written safely: {string.Join("; ", problems.ToArray())}" +
                    (count > problems.Count ? $" (and {count - problems.Count} more)" : string.Empty) +
                    ". Remove the link (or the extra hard link) inside the mod folder, then retry");
            }
        }

        /// <summary>
        /// open_test_mod pre-check. Loading a mod enumerates Assets with GetFiles(AllDirectories), which follows links, and writes a
        /// .pmeta for every asset file that has none (or an unreadable one). Refuses (LINK_REFUSED) when the mod root is a link, or when
        /// a file reached through a link inside Assets has no .pmeta next to it (the game would write it through the link). Files behind
        /// links that already have their .pmeta are only read; such a mod opens and its assets are listed as linked.
        /// </summary>
        public static void RequireOpenable(string modRoot)
        {
            string root = Path.GetFullPath(modRoot).TrimEnd('\\', '/');
            FileProbe r = Probe(root);
            if (r.Error != null || !r.Exists || !r.IsDirectory)
            {
                return; // the caller reports a missing folder itself
            }
            if (r.IsReparsePoint)
            {
                throw new RpcException(ErrorCodes.LinkRefused, $"the mod folder '{Path.GetFileName(root)}' is a link (junction/symlink); it is not opened");
            }
            string assets = Path.Combine(root, "Assets");
            List<string> problems = new List<string>();
            Stack<string> stack = new Stack<string>();
            if (Directory.Exists(assets))
            {
                stack.Push(assets);
            }
            int visited = 0;
            while (stack.Count > 0 && problems.Count < 8 && visited < 20000)
            {
                string dir = stack.Pop();
                visited++;
                FileProbe d = Probe(dir);
                if (d.IsReparsePoint)
                {
                    LinkedWithoutMeta(dir, root, problems);
                    continue;
                }
                string[] files, dirs;
                try
                {
                    files = Directory.GetFiles(dir);
                    dirs = Directory.GetDirectories(dir);
                }
                catch (Exception)
                {
                    continue;
                }
                foreach (string f in files)
                {
                    if (IsAssetFile(f) && Probe(f).IsReparsePoint && !File.Exists(f + AssetImportRules.MetaExtension))
                    {
                        problems.Add($"'{Rel(root + Path.DirectorySeparatorChar, f)}' is a symlink without a .pmeta");
                    }
                }
                foreach (string sub in dirs)
                {
                    stack.Push(sub);
                }
            }
            if (problems.Count > 0)
            {
                throw new RpcException(ErrorCodes.LinkRefused,
                    "opening this mod would make the game write .pmeta files through links inside it (it creates one for every asset file without one): " +
                    string.Join("; ", problems.ToArray()) + ". Remove the links inside the mod folder (or give those files their .pmeta), then retry; nothing was opened");
            }
        }

        private static void LinkedWithoutMeta(string linkDir, string root, List<string> problems)
        {
            try
            {
                foreach (string f in Directory.GetFiles(linkDir, "*", SearchOption.AllDirectories))
                {
                    if (problems.Count >= 8)
                    {
                        return;
                    }
                    if (IsAssetFile(f) && !File.Exists(f + AssetImportRules.MetaExtension))
                    {
                        problems.Add($"'{Rel(root + Path.DirectorySeparatorChar, f)}' (behind the link '{Rel(root + Path.DirectorySeparatorChar, linkDir)}') has no .pmeta");
                    }
                }
            }
            catch (Exception e)
            {
                problems.Add($"the link '{Rel(root + Path.DirectorySeparatorChar, linkDir)}' cannot be examined ({e.Message})");
            }
        }

        private static bool IsAssetFile(string f)
        {
            string ext = Path.GetExtension(f);
            return !string.Equals(ext, AssetImportRules.MetaExtension, StringComparison.OrdinalIgnoreCase) && !string.Equals(ext, ".meta", StringComparison.OrdinalIgnoreCase);
        }

        private static string Rel(string prefix, string p) => p.Length > prefix.Length ? p.Substring(prefix.Length) : p;

        /// <summary>"X:\..." or "X:/..." (null is not). The one copy of this check (AssetImportCore uses it too).</summary>
        internal static bool IsLocalDrivePath(string p) =>
            p != null && p.Length >= 3 &&((p[0] >= 'A' && p[0] <= 'Z') || (p[0] >= 'a' && p[0] <= 'z')) && p[1] == ':' && (p[2] == '\\' || p[2] == '/');

        private static string PathText(string p)
        {
            if (p.StartsWith("\\\\", StringComparison.Ordinal) || p.StartsWith("//", StringComparison.Ordinal))
            {
                return "network, device and \\\\?\\ paths are not written by the plugin";
            }
            if (!IsLocalDrivePath(p))
            {
                return "the path is not an absolute local drive path";
            }
            if (p.IndexOf(':', 2) >= 0)
            {
                return "the path contains ':' after the drive letter (alternate data streams are not written)";
            }
            foreach (char c in p)
            {
                if (c < ' ' || c == '*' || c == '?' || c == '<' || c == '>' || c == '|' || c == '"')
                {
                    return "the path contains a wildcard, control or invalid character";
                }
            }
            return null;
        }

        // ------------------------------------------------------------------ Win32 probe

        public static FileProbe Probe(string path)
        {
            Func<string, FileProbe> o = ProbeOverride;
            return o != null ? o(path) : Win32Probe(path);
        }

        private const uint InvalidFileAttributes = 0xFFFFFFFF;
        private const uint AttrDirectory = 0x10;
        private const uint AttrReparsePoint = 0x400;
        private const int ErrorFileNotFound = 2;
        private const int ErrorPathNotFound = 3;
        private const uint FileShareAll = 0x1 | 0x2 | 0x4;
        private const uint OpenExisting = 3;
        private const uint FlagBackupSemantics = 0x02000000;
        private const uint FlagOpenReparsePoint = 0x00200000;

        [StructLayout(LayoutKind.Sequential)]
        private struct ByHandleFileInformation
        {
            public uint FileAttributes;
            public uint CreationTimeLow;
            public uint CreationTimeHigh;
            public uint LastAccessTimeLow;
            public uint LastAccessTimeHigh;
            public uint LastWriteTimeLow;
            public uint LastWriteTimeHigh;
            public uint VolumeSerialNumber;
            public uint FileSizeHigh;
            public uint FileSizeLow;
            public uint NumberOfLinks;
            public uint FileIndexHigh;
            public uint FileIndexLow;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetFileAttributesW")]
        private static extern uint GetFileAttributes(string path);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
        private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out ByHandleFileInformation info);

        /// <summary>Attributes of the path itself (a link is not followed) and, for a file, its hard-link count.</summary>
        private static FileProbe Win32Probe(string path)
        {
            FileProbe p = new FileProbe();
            try
            {
                uint attr = GetFileAttributes(path);
                if (attr == InvalidFileAttributes)
                {
                    int err = Marshal.GetLastWin32Error();
                    if (err == ErrorFileNotFound || err == ErrorPathNotFound)
                    {
                        return p;
                    }
                    p.Error = "Win32 error " + err;
                    return p;
                }
                p.Exists = true;
                p.IsDirectory = (attr & AttrDirectory) != 0;
                p.IsReparsePoint = (attr & AttrReparsePoint) != 0;
                if (!p.IsDirectory && !p.IsReparsePoint)
                {
                    using (SafeFileHandle h = CreateFile(path, 0, FileShareAll, IntPtr.Zero, OpenExisting, FlagBackupSemantics | FlagOpenReparsePoint, IntPtr.Zero))
                    {
                        if (h.IsInvalid)
                        {
                            return p; // Links stays -1: refused by the caller
                        }
                        if (GetFileInformationByHandle(h, out ByHandleFileInformation info))
                        {
                            p.Links = (int)info.NumberOfLinks;
                            if ((info.FileAttributes & AttrReparsePoint) != 0)
                            {
                                p.IsReparsePoint = true;
                            }
                        }
                    }
                }
                return p;
            }
            catch (Exception e)
            {
                // no Win32 (EntryPointNotFound/DllNotFound) or anything else: the path cannot be examined and the caller refuses
                p.Error = e.GetType().Name + ": " + e.Message;
                return p;
            }
        }
    }
}
