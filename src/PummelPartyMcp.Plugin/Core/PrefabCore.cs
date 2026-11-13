using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace PummelPartyMcp.Core
{
    /// <summary>The .pfab container format as far as the plugin checks it (a hierarchy file written by the game's ModHierarchySerializer).</summary>
    public static class PfabFormat
    {
        /// <summary>BinaryWriter.Write("PMH") (length prefix 3), then ushort major 1 and ushort minor 0, little endian.</summary>
        public static readonly byte[] Header = { 3, (byte)'P', (byte)'M', (byte)'H', 1, 0, 0, 0 };

        public const string Identifier = "PMH";

        /// <summary>The smallest plausible file: header, root name (length byte), active flag, layer (4), tag (length byte), child count (2) ...</summary>
        public const int MinLength = 8 + 1 + 1 + 4 + 1 + 2;

        /// <summary>Null when the bytes look like a complete, current-version .pfab start; otherwise why not.</summary>
        public static string CheckBytes(byte[] b)
        {
            if (b == null || b.Length == 0)
            {
                return "the file is empty";
            }
            if (b.Length < Header.Length)
            {
                return $"the file is only {b.Length} byte(s) long (shorter than the header)";
            }
            for (int i = 0; i < Header.Length; i++)
            {
                if (b[i] != Header[i])
                {
                    return "the file does not start with the PMH 1.0 header";
                }
            }
            if (b.Length < MinLength)
            {
                return $"the file is only {b.Length} byte(s) long (a prefab hierarchy needs at least {MinLength})";
            }
            return null;
        }

        /// <summary>Offset of the root object's active flag (after the header and the root name), or -1 when the bytes are too short to tell.</summary>
        public static int RootActiveOffset(byte[] b)
        {
            if (b == null || b.Length < Header.Length + 1)
            {
                return -1;
            }
            int pos = Header.Length;
            int len = 0, shift = 0;
            for (int i = 0; i < 5; i++)
            {
                if (pos >= b.Length)
                {
                    return -1;
                }
                byte cur = b[pos++];
                len |= (cur & 0x7F) << shift;
                if ((cur & 0x80) == 0)
                {
                    int off = pos + len;
                    return off < b.Length ? off : -1;
                }
                shift += 7;
            }
            return -1;
        }

        /// <summary>True when the two byte arrays are identical, or differ only in the root object's active flag (the editor deactivates a new prefab after saving it).</summary>
        public static bool SameApartFromRootActive(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length)
            {
                return false;
            }
            int skip = RootActiveOffset(a);
            for (int i = 0; i < a.Length; i++)
            {
                if (a[i] != b[i] && i != skip)
                {
                    return false;
                }
            }
            return true;
        }
    }

    /// <summary>The content of a file before the editor was allowed to write it.</summary>
    public sealed class FileSnapshot
    {
        public string Path;
        public bool Existed;
        public byte[] Bytes;

        public static FileSnapshot Take(string path, long maxBytes)
        {
            FileSnapshot s = new FileSnapshot { Path = path };
            if (Directory.Exists(path))
            {
                throw new RpcException(ErrorCodes.InvalidState, $"'{System.IO.Path.GetFileName(path)}' is a folder, not a file; nothing was changed");
            }
            if (!File.Exists(path))
            {
                return s;
            }
            long len = new FileInfo(path).Length;
            if (len > maxBytes)
            {
                throw new RpcException(ErrorCodes.InvalidState,
                    $"'{System.IO.Path.GetFileName(path)}' is {len} bytes, more than the {maxBytes} bytes the plugin keeps as a safety copy; nothing was changed");
            }
            s.Existed = true;
            s.Bytes = File.ReadAllBytes(path);
            return s;
        }
    }

    /// <summary>A prefab file the plugin protects around one editor write: what it was before, what the dry run says it will be.</summary>
    public sealed class PrefabFileJob
    {
        public string Name;
        public string PfabPath;
        public string MetaPath;
        public FileSnapshot Pfab;
        public FileSnapshot Meta;

        /// <summary>The bytes the editor's serializer produced in memory (dry run); null for a prefab that does not exist yet.</summary>
        public byte[] Expected;

        /// <summary>The prefab did not exist before the write: on failure its files are removed instead of restored.</summary>
        public bool IsNew;

        /// <summary>Folder the files must be strictly inside for restore/remove (the mod's Assets folder).</summary>
        public string AssetsFolder;
    }

    public sealed class PrefabFileOutcome
    {
        public string Name;
        public bool Ok;
        public long Length;

        /// <summary>The written file is byte-identical to the dry run (false with Ok: same length, differs elsewhere than the root active flag).</summary>
        public bool Identical;

        public string Problem;
        public bool Restored;
        public bool Removed;

        /// <summary>The file still has exactly its previous content (the write never happened); nothing needed restoring.</summary>
        public bool NotWritten;
        public string RestoreProblem;
        public List<string> Warnings = new List<string>();

        /// <summary>L5: a restore failed and the safety copy was kept in this file (path relative to the mod folder; only in that case).</summary>
        public List<string> PreservedSnapshots = new List<string>();

        public JObject ToJson()
        {
            JObject o = new JObject { ["name"] = Name, ["ok"] = Ok, ["sizeBytes"] = Length };
            if (PreservedSnapshots.Count > 0)
            {
                o["preservedSnapshots"] = new JArray(PreservedSnapshots.ToArray());
            }
            if (Problem != null)
            {
                o["problem"] = Problem;
            }
            if (Restored)
            {
                o["restoredPreviousContent"] = true;
            }
            if (Removed)
            {
                o["removedFiles"] = true;
            }
            if (RestoreProblem != null)
            {
                o["restoreProblem"] = RestoreProblem;
            }
            if (Warnings.Count > 0)
            {
                o["warnings"] = new JArray(Warnings.ToArray());
            }
            return o;
        }
    }

    /// <summary>
    /// The write protection around every editor call that rewrites a .pfab (PummelPrefab.Save opens the file with FileMode.Create and
    /// serialises straight into it, so a failure leaves a truncated file and a truncated prefab makes the whole mod fail to load):
    /// <see cref="Prepare"/> runs the dry run and takes the safety copy BEFORE the call; <see cref="Finish"/> verifies the file AFTER it and
    /// restores (existing prefab) or removes (new prefab) on any failure. Pure .NET; tested on real temp directories.
    /// </summary>
    public static class PrefabWriteGuard
    {
        /// <summary>No prefab file larger than this is protected (the safety copy is held in memory).</summary>
        public const long MaxSnapshotBytes = 64L * 1024 * 1024;

        public const string RestoreTempSuffix = ".mcp-restore.tmp";

        /// <summary>
        /// Dry run + safety copy for an existing prefab. <paramref name="dryRun"/> serialises the in-memory prefab to bytes without touching
        /// any file; it throws when the editor's serializer cannot write the prefab, which refuses the whole operation (nothing changed).
        /// </summary>
        public static PrefabFileJob Prepare(string name, string pfabPath, string assetsFolder, Func<byte[]> dryRun)
        {
            RequirePath(assetsFolder, pfabPath, name, false);
            byte[] expected;
            try
            {
                expected = dryRun();
            }
            catch (Exception e)
            {
                throw new RpcException(ErrorCodes.InvalidState,
                    $"the prefab '{name}' cannot be serialised ({e.GetType().Name}: {e.Message}); writing it would leave a truncated .pfab that makes the whole mod fail to load, so nothing was written. Fix or remove the object that fails (a component value that throws when saved), then retry");
            }
            string why = PfabFormat.CheckBytes(expected);
            if (why != null)
            {
                throw new RpcException(ErrorCodes.InvalidState, $"the dry run of the prefab '{name}' produced unusable data ({why}); nothing was written");
            }
            return new PrefabFileJob
            {
                Name = name,
                PfabPath = pfabPath,
                MetaPath = pfabPath + AssetImportRules.MetaExtension,
                AssetsFolder = assetsFolder,
                Expected = expected,
                Pfab = FileSnapshot.Take(pfabPath, MaxSnapshotBytes),
                Meta = FileSnapshot.Take(pfabPath + AssetImportRules.MetaExtension, MaxSnapshotBytes),
            };
        }

        /// <summary>Job for a prefab the editor is about to create (the files must not exist). Expected may be filled in after the call.</summary>
        public static PrefabFileJob PrepareNew(string name, string pfabPath, string assetsFolder)
        {
            RequirePath(assetsFolder, pfabPath, name, true);
            if (!AssetImportRules.IsFree(pfabPath))
            {
                throw new RpcException(ErrorCodes.AlreadyExists, $"'{System.IO.Path.GetFileName(pfabPath)}' (or its .pmeta) already exists; nothing was created");
            }
            return new PrefabFileJob
            {
                Name = name,
                PfabPath = pfabPath,
                MetaPath = pfabPath + AssetImportRules.MetaExtension,
                AssetsFolder = assetsFolder,
                IsNew = true,
                Pfab = new FileSnapshot { Path = pfabPath },
                Meta = new FileSnapshot { Path = pfabPath + AssetImportRules.MetaExtension },
            };
        }

        private static void RequirePath(string assetsFolder, string pfabPath, string name, bool directlyInPrefabsFolder)
        {
            string ext = pfabPath == null ? null : System.IO.Path.GetExtension(pfabPath);
            bool ok = string.Equals(ext, ".pfab", StringComparison.OrdinalIgnoreCase) && AssetImportRules.IsStrictlyUnder(assetsFolder, pfabPath);
            if (ok && directlyInPrefabsFolder)
            {
                ok = string.Equals(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(pfabPath)),
                    System.IO.Path.GetFullPath(System.IO.Path.Combine(assetsFolder, "Prefabs")), StringComparison.OrdinalIgnoreCase);
            }
            if (!ok)
            {
                throw new RpcException(ErrorCodes.InvalidState, "the file of the prefab '" + name + "' is not a .pfab inside the mod's Assets folder" + (directlyInPrefabsFolder ? "\\Prefabs" : string.Empty) + "; the plugin does not protect or write it");
            }
            string root = AssetImportRules.ModRootOf(System.IO.Path.GetFullPath(assetsFolder));
            SandboxPath.RequireInsideMod(root, pfabPath, $"the prefab '{name}' (.pfab)");
            SandboxPath.RequireInsideMod(root, pfabPath + AssetImportRules.MetaExtension, $"the prefab '{name}' (.pmeta)");
        }

        /// <summary>Re-runs the link check on the job's two files; called right before the editor call that writes them.</summary>
        public static void Recheck(PrefabFileJob job)
        {
            string root = AssetImportRules.ModRootOf(System.IO.Path.GetFullPath(job.AssetsFolder));
            SandboxPath.RequireInsideMod(root, job.PfabPath, $"the prefab '{job.Name}' (.pfab)");
            SandboxPath.RequireInsideMod(root, job.MetaPath, $"the prefab '{job.Name}' (.pmeta)");
        }

        /// <summary>Verifies the files after the editor wrote them; restores or removes them when they are bad. Never throws.</summary>
        public static PrefabFileOutcome Finish(PrefabFileJob job)
        {
            PrefabFileOutcome o = new PrefabFileOutcome { Name = job.Name };
            try
            {
                o.Problem = VerifyPfab(job, o);
                string metaProblem = VerifyMeta(job);
                if (o.Problem == null && metaProblem != null)
                {
                    o.Problem = metaProblem;
                }
            }
            catch (Exception e)
            {
                o.Problem = "could not verify the files: " + e.Message;
            }
            if (o.Problem == null)
            {
                o.Ok = true;
                return o;
            }
            Repair(job, o);
            return o;
        }

        private static string VerifyPfab(PrefabFileJob job, PrefabFileOutcome o)
        {
            if (!File.Exists(job.PfabPath))
            {
                return $"'{System.IO.Path.GetFileName(job.PfabPath)}' does not exist after the write";
            }
            byte[] now = File.ReadAllBytes(job.PfabPath);
            o.Length = now.Length;
            string why = PfabFormat.CheckBytes(now);
            if (why != null)
            {
                return why;
            }
            if (job.Expected != null)
            {
                if (!job.IsNew && job.Pfab.Existed && job.Pfab.Bytes != null && job.Pfab.Bytes.Length == now.Length && now.SequenceEqual(job.Pfab.Bytes)
                    && !now.SequenceEqual(job.Expected))
                {
                    o.NotWritten = true;
                    return "the file was not rewritten (it still has its previous content, the editor did not write the new state)";
                }
                if (now.Length != job.Expected.Length)
                {
                    return $"the file is {now.Length} bytes but the editor's serializer produced {job.Expected.Length} bytes in memory (truncated or incomplete write)";
                }
                o.Identical = PfabFormat.SameApartFromRootActive(now, job.Expected);
                if (!o.Identical)
                {
                    o.Warnings.Add("the file has the expected length but its content differs from the in-memory serialisation (not only the root active flag); the file was kept");
                }
            }
            return null;
        }

        private static string VerifyMeta(PrefabFileJob job)
        {
            if (!File.Exists(job.MetaPath))
            {
                return $"'{System.IO.Path.GetFileName(job.MetaPath)}' does not exist after the write";
            }
            byte[] b = File.ReadAllBytes(job.MetaPath);
            if (b.Length == 0)
            {
                return "the .pmeta is empty";
            }
            try
            {
                JToken.Parse(System.Text.Encoding.UTF8.GetString(b));
            }
            catch (Exception e)
            {
                return "the .pmeta is not valid JSON (" + e.Message + ")";
            }
            return null;
        }

        private static void Repair(PrefabFileJob job, PrefabFileOutcome o)
        {
            List<string> problems = new List<string>();
            if (job.IsNew)
            {
                foreach (string p in new[] { job.PfabPath, job.MetaPath })
                {
                    try
                    {
                        if (!RemovablePath(job, p))
                        {
                            problems.Add("not deleted (not a prefab file inside the mod's Assets\\Prefabs folder): " + p);
                            continue;
                        }
                        string link = LinkProblem(job, p);
                        if (link != null)
                        {
                            problems.Add($"not deleted ({link})");
                            continue;
                        }
                        if (File.Exists(p))
                        {
                            File.SetAttributes(p, FileAttributes.Normal);
                            File.Delete(p);
                            o.Removed = true;
                        }
                    }
                    catch (Exception e)
                    {
                        problems.Add($"could not delete {System.IO.Path.GetFileName(p)}: {e.Message}");
                    }
                }
            }
            else
            {
                foreach (FileSnapshot s in new[] { job.Pfab, job.Meta })
                {
                    try
                    {
                        string link = LinkProblem(job, s.Path);
                        if (s.Existed)
                        {
                            if (File.Exists(s.Path) && File.ReadAllBytes(s.Path).SequenceEqual(s.Bytes))
                            {
                                continue; // already the previous content
                            }
                            if (link != null)
                            {
                                problems.Add($"not restored ({link})");
                                continue;
                            }
                            AtomicWrite(s.Path, s.Bytes, AssetImportRules.ModRootOf(System.IO.Path.GetFullPath(job.AssetsFolder)));
                            o.Restored = true;
                        }
                        else if (File.Exists(s.Path) && RemovablePath(job, s.Path))
                        {
                            if (link != null)
                            {
                                problems.Add($"not deleted ({link})");
                                continue;
                            }
                            File.SetAttributes(s.Path, FileAttributes.Normal);
                            File.Delete(s.Path);
                            o.Removed = true;
                        }
                    }
                    catch (Exception e)
                    {
                        string kept = s.Existed && s.Bytes != null ? PreserveSnapshot(job, s, o) : null;
                        problems.Add($"RESTORE FAILED for {System.IO.Path.GetFileName(s.Path)} ({e.GetType().Name}: {e.Message}); " +
                                     (kept != null
                                         ? $"its content from before the write was kept in '{kept}' inside the mod folder (a folder the game does not read): copy it back over '{System.IO.Path.GetFileName(s.Path)}' by hand before reopening the mod"
                                         : "its safety copy could not be kept on disk either; do not reopen the mod before checking the file"));
                    }
                }
            }
            if (problems.Count > 0)
            {
                o.RestoreProblem = string.Join("; ", problems.ToArray());
            }
        }

        /// <summary>Folder (below the mod root, outside Assets and Data: the game never reads it) for safety copies of failed restores.</summary>
        public const string RecoveryFolder = "McpRecovery";

        public const string SnapshotSuffix = ".mcp-snapshot";

        /// <summary>
        /// L5: keeps the safety copy of a file whose restore failed as &lt;mod&gt;\McpRecovery\&lt;file&gt;.&lt;time&gt;.mcp-snapshot (CreateNew, link
        /// check). Only called in that failure case. Returns the path relative to the mod folder, or null when it could not be written.
        /// </summary>
        private static string PreserveSnapshot(PrefabFileJob job, FileSnapshot s, PrefabFileOutcome o)
        {
            try
            {
                string root = AssetImportRules.ModRootOf(System.IO.Path.GetFullPath(job.AssetsFolder));
                string dir = System.IO.Path.Combine(root, RecoveryFolder);
                SandboxPath.RequireFolderInsideMod(root, dir, "keeping the safety copy");
                Directory.CreateDirectory(dir);
                string name = System.IO.Path.GetFileName(s.Path) + "." + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff", System.Globalization.CultureInfo.InvariantCulture) + SnapshotSuffix;
                string target = System.IO.Path.Combine(dir, name);
                SandboxPath.RequireInsideMod(root, target, "keeping the safety copy");
                using (FileStream fs = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    fs.Write(s.Bytes, 0, s.Bytes.Length);
                    fs.Flush(true);
                }
                string rel = RecoveryFolder + "/" + name;
                o.PreservedSnapshots.Add(rel);
                return rel;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// L5 test seam for the swap step of <see cref="AtomicWrite"/>: File.Replace may be missing in the game's runtime (the same
        /// assumption as Core/TokenAuth.cs), so a PlatformNotSupportedException/NotImplementedException falls back to Copy(overwrite).
        /// </summary>
        public static Action<string, string> ReplaceOp = (temp, path) => File.Replace(temp, path, null);

        public static Action<string, string, bool> CopyOp = File.Copy;

        public static Action<string, string> MoveOp = File.Move;

        /// <summary>Restores the seams to the real file operations (tests).</summary>
        public static void ResetFileOps()
        {
            ReplaceOp = (temp, path) => File.Replace(temp, path, null);
            CopyOp = File.Copy;
            MoveOp = File.Move;
        }

        /// <summary>Which swap strategy the last <see cref="AtomicWrite"/> used ("replace", "copy-fallback", "move"); for tests and logs.</summary>
        public static string LastSwap;

        private static void Swap(string temp, string path)
        {
            if (!File.Exists(path))
            {
                MoveOp(temp, path);
                LastSwap = "move";
                return;
            }
            try
            {
                ReplaceOp(temp, path);
                LastSwap = "replace";
            }
            catch (PlatformNotSupportedException)
            {
                CopyOp(temp, path, true); // runtime without File.Replace: not atomic, but a complete file (verified below)
                LastSwap = "copy-fallback";
            }
            catch (NotImplementedException)
            {
                CopyOp(temp, path, true);
                LastSwap = "copy-fallback";
            }
        }

        private static string LinkProblem(PrefabFileJob job, string p)
        {
            try
            {
                return SandboxPath.Check(AssetImportRules.ModRootOf(System.IO.Path.GetFullPath(job.AssetsFolder)), p, out string _, out string _);
            }
            catch (Exception e)
            {
                return "the path cannot be checked: " + e.Message;
            }
        }

        private static bool RemovablePath(PrefabFileJob job, string p)
        {
            string full = System.IO.Path.GetFullPath(p);
            string ext = System.IO.Path.GetExtension(full).ToLowerInvariant();
            string basePfab = ext == ".pmeta" ? full.Substring(0, full.Length - ".pmeta".Length) : full;
            return AssetImportRules.IsStrictlyUnder(job.AssetsFolder, full)
                   && string.Equals(System.IO.Path.GetExtension(basePfab), ".pfab", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Writes the bytes next to the target and swaps them in, so a failure never truncates the target. The temp file is always removed.</summary>
        public static void AtomicWrite(string path, byte[] bytes, string modRoot = null)
        {
            string temp = path + RestoreTempSuffix;
            if (File.Exists(temp))
            {
                temp = path + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + RestoreTempSuffix;
            }
            if (modRoot != null)
            {
                SandboxPath.RequireInsideMod(modRoot, path, "restoring the safety copy");
                SandboxPath.RequireInsideMod(modRoot, temp, "restoring the safety copy (temporary file)");
            }
            try
            {
                using (FileStream fs = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    fs.Write(bytes, 0, bytes.Length);
                    fs.Flush(true);
                }
                Swap(temp, path);
                byte[] check = File.ReadAllBytes(path);
                if (check.Length != bytes.Length || !check.SequenceEqual(bytes))
                {
                    throw new IOException("the restored file does not match the safety copy");
                }
            }
            finally
            {
                try
                {
                    if (File.Exists(temp))
                    {
                        File.Delete(temp);
                    }
                }
                catch (Exception)
                {
                    // best effort; the name is recognisable
                }
            }
        }

        /// <summary>Removes the files of a prefab this call created (strict path checks), e.g. after the editor call threw. Never throws.</summary>
        public static PrefabFileOutcome Discard(PrefabFileJob job, string reason)
        {
            PrefabFileOutcome o = new PrefabFileOutcome { Name = job.Name, Problem = reason };
            Repair(job, o);
            return o;
        }

        /// <summary>One line per failed outcome, for an error message.</summary>
        public static string Describe(IEnumerable<PrefabFileOutcome> failed) =>
            string.Join("; ", failed.Select(f =>
                $"'{f.Name}': {f.Problem}" + (f.Restored ? " (previous file content restored)" : f.Removed ? " (the broken file was removed)" : string.Empty)
                + (f.RestoreProblem != null ? " [" + f.RestoreProblem + "]" : string.Empty)).ToArray());
    }

    /// <summary>State recorded right before ModAssetExternal.Rename (review L8).</summary>
    public sealed class RenameSnapshot
    {
        public string ModRoot;
        public string OldFile;
        public string NewFile;
        public long OldLength;
        public string OldSha256;
        public bool PopupBefore;
    }

    public sealed class RenameResult
    {
        public bool Ok;
        public string Problem;

        /// <summary>The file had been moved without its .pmeta and was provably safe to move back; it was.</summary>
        public bool MovedBack;

        /// <summary>A file left at the new name (not moved back because that was not provably safe); relative to the mod folder.</summary>
        public string Orphan;
    }

    /// <summary>
    /// Review L8: ModAssetExternal.Rename moves the file, then the .pmeta, and on any exception raises a modal popup and returns; a
    /// failure between the two moves leaves the file at the new name, the .pmeta at the old one and the asset pointing at the old
    /// path. <see cref="Before"/> checks and records; <see cref="After"/> decides success only when both files are at the new name,
    /// nothing is left at the old name and no popup appeared; a half-done move is undone only when provably safe (the new name was
    /// free before, the old name is free now, the moved bytes equal the recorded ones, both paths pass the link check).
    /// </summary>
    public static class RenameGuard
    {
        public static RenameSnapshot Before(string modRoot, string oldFile, string newFile, bool popupOpen)
        {
            SandboxPath.RequireAllInsideMod(modRoot, new[] { oldFile, oldFile + AssetImportRules.MetaExtension, newFile, newFile + AssetImportRules.MetaExtension }, "rename");
            if (!AssetImportRules.IsFree(newFile))
            {
                throw new RpcException(ErrorCodes.AlreadyExists, $"'{System.IO.Path.GetFileName(newFile)}' (or its .pmeta) already exists; not renamed");
            }
            if (popupOpen)
            {
                throw new RpcException(ErrorCodes.InvalidState, "an editor dialog is open; not renamed");
            }
            byte[] bytes = File.ReadAllBytes(oldFile);
            return new RenameSnapshot { ModRoot = modRoot, OldFile = oldFile, NewFile = newFile, OldLength = bytes.Length, OldSha256 = Sha(bytes), PopupBefore = popupOpen };
        }

        private static string Sha(byte[] b)
        {
            using (System.Security.Cryptography.SHA256 sha = System.Security.Cryptography.SHA256.Create())
            {
                return Convert.ToBase64String(sha.ComputeHash(b));
            }
        }

        private static bool Same(string a, string b)
        {
            try
            {
                return string.Equals(System.IO.Path.GetFullPath(a), System.IO.Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary><paramref name="assetPathNow"/>: the asset's filePath after the call; <paramref name="popupNow"/>: a dialog is open now. Never throws.</summary>
        public static RenameResult After(RenameSnapshot s, string assetPathNow, bool popupNow)
        {
            RenameResult r = new RenameResult();
            try
            {
                string oldMeta = s.OldFile + AssetImportRules.MetaExtension, newMeta = s.NewFile + AssetImportRules.MetaExtension;
                bool newFile = File.Exists(s.NewFile), newM = File.Exists(newMeta), oldFile = File.Exists(s.OldFile), oldM = File.Exists(oldMeta);
                bool popup = popupNow && !s.PopupBefore;
                if (Same(assetPathNow, s.NewFile) && newFile && newM && !oldFile && !oldM && !popup)
                {
                    r.Ok = true;
                    return r;
                }
                List<string> why = new List<string>();
                if (popup)
                {
                    why.Add("the editor raised an error dialog (close it by hand)");
                }
                if (newFile && !oldFile && oldM && !newM)
                {
                    why.Add("the file was moved to the new name but its .pmeta was not (partial rename)");
                    byte[] moved = File.ReadAllBytes(s.NewFile);
                    bool content = moved.Length == s.OldLength && Sha(moved) == s.OldSha256;
                    bool links = SandboxPath.Check(s.ModRoot, s.NewFile, out string _, out string _) == null && SandboxPath.Check(s.ModRoot, s.OldFile, out string _, out string _) == null;
                    if (content && links && Same(assetPathNow, s.OldFile))
                    {
                        File.Move(s.NewFile, s.OldFile);
                        r.MovedBack = File.Exists(s.OldFile) && !File.Exists(s.NewFile);
                    }
                    if (!r.MovedBack)
                    {
                        r.Orphan = System.IO.Path.GetFileName(s.NewFile);
                        why.Add($"'{r.Orphan}' was left at the new name (moving it back was not provably safe)");
                    }
                    else
                    {
                        why.Add("the file was moved back to its old name");
                    }
                }
                else if (oldFile && oldM && !newFile && !newM)
                {
                    why.Add("nothing was moved: the asset keeps its old name");
                }
                else if (!Same(assetPathNow, s.NewFile))
                {
                    why.Add($"the asset still points at '{System.IO.Path.GetFileName(assetPathNow)}'");
                }
                else
                {
                    why.Add($"unexpected files afterwards (new: {newFile}/{newM}, old: {oldFile}/{oldM})");
                }
                r.Problem = string.Join("; ", why.ToArray());
            }
            catch (Exception e)
            {
                r.Problem = "the rename could not be verified: " + e.Message;
            }
            return r;
        }
    }

    /// <summary>Name choice of create_prefab: the editor names the file itself and checks only asset names, so the plugin checks the rest.</summary>
    public sealed class PrefabNamePlan
    {
        /// <summary>The name the prefab ends up with.</summary>
        public string Name;

        /// <summary>The name the editor will give the file (asset-name unique, file free on disk).</summary>
        public string EditorName;

        public bool NeedsRename => !string.Equals(Name, EditorName, StringComparison.Ordinal);

        /// <summary>A name was requested and could not be used as given.</summary>
        public bool Adjusted;
    }

    public static class PrefabNaming
    {
        public const int MaxSuffix = 99;

        /// <summary>
        /// <paramref name="requested"/> = the caller's name (or null), <paramref name="editorName"/> = what EditorSystem.GetUniqueAssetName would return,
        /// <paramref name="assetNames"/> = names of the mod's prefab assets, <paramref name="fileFree"/> = neither .pfab nor .pmeta exists for a name.
        /// Never returns a name that exists as an asset (case-insensitive) or as a file; refuses instead.
        /// </summary>
        public static PrefabNamePlan Plan(string requested, string editorName, IEnumerable<string> assetNames, Func<string, bool> fileFree)
        {
            string bad = AssetImportRules.ValidateName(editorName);
            if (bad != null)
            {
                throw RpcException.InvalidParams($"the editor would name the prefab '{Trunc(editorName)}', which {bad}; rename the source object first (set_object) or use another one");
            }
            if (!fileFree(editorName))
            {
                throw new RpcException(ErrorCodes.AlreadyExists,
                    $"the editor would create 'Assets\\Prefabs\\{editorName}.pfab' but that file (or its .pmeta) already exists on disk (case-insensitively) without a loaded prefab of that name; nothing was created, because the editor would overwrite it");
            }
            HashSet<string> names = new HashSet<string>(assetNames ?? new string[0], StringComparer.OrdinalIgnoreCase);
            string baseName = requested ?? editorName;
            for (int n = 0; n <= MaxSuffix; n++)
            {
                string candidate = n == 0 ? baseName : baseName + "_" + n;
                if (AssetImportRules.ValidateName(candidate) != null)
                {
                    break;
                }
                bool free = !names.Contains(candidate) && (string.Equals(candidate, editorName, StringComparison.Ordinal) || fileFree(candidate));
                if (free)
                {
                    return new PrefabNamePlan { Name = candidate, EditorName = editorName, Adjusted = requested != null && n > 0 };
                }
            }
            throw new RpcException(ErrorCodes.AlreadyExists, $"no free prefab name found for '{Trunc(baseName)}' (tried up to _{MaxSuffix}); pass a different 'name'");
        }

        private static string Trunc(string s) => s == null ? "<null>" : s.Length <= 40 ? s : s.Substring(0, 40) + "...";
    }

    /// <summary>One component or action reference found inside a subtree that is about to become a prefab.</summary>
    public sealed class SubtreeReference
    {
        public string Owner;
        public string Where;
        public bool PointsInside;
    }

    public static class PrefabSourceRules
    {
        /// <summary>The editor's own rule (FileBrowserWidget drag and drop): no prefab instance and no board space inside the subtree.</summary>
        public static string Check(int prefabInstances, int boardSpaces)
        {
            if (prefabInstances > 0 || boardSpaces > 0)
            {
                return "the editor refuses to create a prefab from a subtree that contains a Prefab Instance or Board Space component";
            }
            return null;
        }

        /// <summary>
        /// Collects every reference to a mod OBJECT (a JSON object with "objectGuid", the shape the plugin's value converter gives a scene
        /// object/component reference, including action targets) in a component's property tree. <paramref name="insideGuids"/> = guids of
        /// the subtree's own objects. Sets <paramref name="incomplete"/> when a part could not be read.
        /// </summary>
        public static void FindObjectRefs(JToken token, string path, string owner, ICollection<string> insideGuids, List<SubtreeReference> found, ref bool incomplete)
        {
            if (token == null || found.Count >= 200)
            {
                return;
            }
            if (token is JObject o)
            {
                if (o["truncated"] != null || o["unsupported"] != null || o["unavailable"] != null || o["truncatedItems"] != null)
                {
                    incomplete = true;
                }
                JToken og = o["objectGuid"];
                if (og != null && og.Type == JTokenType.String)
                {
                    string g = (string)og;
                    found.Add(new SubtreeReference { Owner = owner, Where = path, PointsInside = insideGuids != null && insideGuids.Contains(g) });
                    return;
                }
                foreach (JProperty p in o.Properties())
                {
                    FindObjectRefs(p.Value, path.Length == 0 ? p.Name : path + "." + p.Name, owner, insideGuids, found, ref incomplete);
                }
            }
            else if (token is JArray a)
            {
                for (int i = 0; i < a.Count; i++)
                {
                    FindObjectRefs(a[i], path + "[" + i + "]", owner, insideGuids, found, ref incomplete);
                }
            }
        }

        /// <summary>
        /// Action targets and object references inside the copy keep the SOURCE objects' guids (the copy gets fresh component guids, but the
        /// stored references are not remapped when a prefab is made), so they would silently keep pointing at the scene objects. The plugin
        /// refuses instead; returns null when there are none.
        /// </summary>
        /// <summary>
        /// Review M2: a subtree whose component values could not all be read may hold references the scan did not see; consistent with
        /// "incomplete means in use" elsewhere, create_prefab refuses (nothing created). Null when the scan was complete.
        /// </summary>
        public static string RefusalForIncompleteScan(bool incomplete) => incomplete
            ? "some component values of the subtree could not be read (or were cut off), so object/action references inside them cannot be ruled out; nothing was created. " +
              "Check the objects with get_object (values shown as unavailable/truncated), simplify them, or create an empty prefab and build its contents inside it"
            : null;

        public static string RefusalForReferences(IList<SubtreeReference> refs)
        {
            if (refs == null || refs.Count == 0)
            {
                return null;
            }
            int inside = refs.Count(r => r.PointsInside);
            return $"the subtree has {refs.Count} object/action reference(s) ({inside} to objects inside it, {refs.Count - inside} to scene objects outside it): " +
                   string.Join("; ", refs.Take(8).Select(r => r.Owner + " " + r.Where + (r.PointsInside ? " (inside)" : " (outside)")).ToArray()) +
                   ". The editor copies them unchanged, so the prefab's actions would keep pointing at the scene objects instead of the copies (and instances would not remap them). " +
                   "Remove those references first, or create an empty prefab and build its contents inside it.";
        }
    }

    /// <summary>Which tools may run while a prefab is open (docs/plugin-api.md). Pure table; tools enforce it themselves.</summary>
    public static class PrefabContextRules
    {
        public static readonly string[] RefusedWhilePrefabOpen =
        {
            "create_prefab", "open_prefab", "import_asset", "remove_asset", "create_test_mod", "open_test_mod", "start_playtest",
        };

        public static readonly string[] OnlyWhilePrefabOpen = { "close_prefab" };

        public static string Message(string tool, string prefabName)
        {
            if (tool == "open_prefab")
            {
                return $"the prefab '{prefabName}' is already open; call close_prefab first (opening another prefab from the editor would save the open one without the instance refresh)";
            }
            return $"a prefab ('{prefabName}') is being edited; call close_prefab first";
        }
    }
}
