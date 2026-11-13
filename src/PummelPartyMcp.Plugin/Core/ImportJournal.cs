using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using Newtonsoft.Json.Linq;

namespace PummelPartyMcp.Core
{
    /// <summary>
    /// Review M1: the game writes the .pmeta and then decodes the file synchronously (audio while the main thread busy-waits). If the
    /// game dies during that decode, the copy and its .pmeta stay registered on disk and every reopen of the sandbox mod decodes them
    /// again. import_asset therefore writes &lt;mod&gt;\McpRecovery\import-pending.json right before ImportFile and removes it right after
    /// (success or handled failure). A journal still present later means that import was interrupted: <see cref="Recover"/> (run at
    /// plugin start for every sandbox mod, and before open_test_mod) renames the copy to &lt;name&gt;.mcp-import-failed and, when the
    /// .pmeta provably came from that import, its .pmeta to &lt;name&gt;.mcp-import-failed.pmeta, so the game no longer decodes them. Nothing
    /// is deleted except the journal itself; every path passes the link check; only files whose SHA-256 equals the journal's are moved.
    /// </summary>
    public static class ImportJournal
    {
        public const string FileName = "import-pending.json";
        public const string FailedSuffix = ".mcp-import-failed";

        public static string PathOf(string modRoot) => Path.Combine(modRoot, PrefabWriteGuard.RecoveryFolder, FileName);

        /// <summary>Writes the journal (CreateNew semantics are not needed: one import at a time per mod; an old journal is recovered first).</summary>
        public static void Write(string modRoot, ImportDestination d, string sha256, bool metaExistedBefore)
        {
            string dir = Path.Combine(modRoot, PrefabWriteGuard.RecoveryFolder);
            SandboxPath.RequireFolderInsideMod(modRoot, dir, "import_asset (journal)");
            Directory.CreateDirectory(dir);
            string path = PathOf(modRoot);
            SandboxPath.RequireInsideMod(modRoot, path, "import_asset (journal)");
            string rel = d.FullPath.Substring(Path.GetFullPath(modRoot).TrimEnd('\\', '/').Length + 1);
            JObject j = new JObject
            {
                ["relativePath"] = rel,
                ["sha256"] = sha256,
                ["metaExistedBefore"] = metaExistedBefore,
                ["startedUtc"] = DateTime.UtcNow.ToString("o", System.Globalization.CultureInfo.InvariantCulture),
            };
            File.WriteAllText(path, j.ToString());
        }

        /// <summary>Removes the journal after the import step returned (never throws).</summary>
        public static void Clear(string modRoot)
        {
            try
            {
                string path = PathOf(modRoot);
                if (File.Exists(path) && SandboxPath.Check(modRoot, path, out string _, out string _) == null)
                {
                    File.Delete(path);
                }
            }
            catch (Exception)
            {
                // a leftover journal only makes the next start quarantine a file whose hash matches
            }
        }

        private static string Sha(string path)
        {
            using (SHA256 sha = SHA256.Create())
            using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                return BitConverter.ToString(sha.ComputeHash(fs)).Replace("-", string.Empty).ToLowerInvariant();
            }
        }

        private static string FreeName(string basePath)
        {
            if (!File.Exists(basePath) && !Directory.Exists(basePath))
            {
                return basePath;
            }
            return basePath + "." + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff", System.Globalization.CultureInfo.InvariantCulture);
        }

        /// <summary>Recovers one sandbox mod; returns what was done (empty when there was no journal). Never throws.</summary>
        public static List<string> Recover(string modRoot)
        {
            List<string> done = new List<string>();
            try
            {
                string path = PathOf(modRoot);
                if (!File.Exists(path))
                {
                    return done;
                }
                if (TestModName.Validate(Path.GetFileName(Path.GetFullPath(modRoot).TrimEnd('\\', '/'))) != null)
                {
                    return done; // never acts outside sandbox mods
                }
                JObject j = JObject.Parse(File.ReadAllText(path));
                string rel = (string)j["relativePath"];
                string sha = (string)j["sha256"];
                bool metaExisted = j["metaExistedBefore"] == null || (bool)j["metaExistedBefore"];
                string file = rel == null ? null : Path.GetFullPath(Path.Combine(modRoot, rel));
                bool shapeOk = file != null && AssetImportRules.IsStrictlyUnder(Path.Combine(modRoot, "Assets"), file)
                               && AssetImportRules.TryKind(Path.GetExtension(file).ToLowerInvariant(), out AssetImportKind _);
                if (shapeOk && File.Exists(file) && sha != null && string.Equals(Sha(file), sha, StringComparison.OrdinalIgnoreCase))
                {
                    string target = FreeName(file + FailedSuffix);
                    SandboxPath.RequireAllInsideMod(modRoot, new[] { file, target }, "import recovery");
                    File.Move(file, target);
                    done.Add($"moved the interrupted import {rel} to {Path.GetFileName(target)} (the game no longer decodes it)");
                    string meta = file + AssetImportRules.MetaExtension;
                    if (!metaExisted && File.Exists(meta))
                    {
                        string metaTarget = FreeName(file + FailedSuffix + AssetImportRules.MetaExtension);
                        SandboxPath.RequireAllInsideMod(modRoot, new[] { meta, metaTarget }, "import recovery");
                        File.Move(meta, metaTarget);
                        done.Add($"moved its .pmeta to {Path.GetFileName(metaTarget)}");
                    }
                }
                else
                {
                    done.Add($"journal for {rel ?? "?"} found, but the file is gone or changed: nothing moved");
                }
                SandboxPath.RequireInsideMod(modRoot, path, "import recovery");
                File.Delete(path);
            }
            catch (Exception e)
            {
                done.Add("import recovery problem: " + e.Message);
            }
            return done;
        }

        /// <summary>Recovers every sandbox mod (MCP_ folder name, not a link) directly below <paramref name="modsFolder"/>. Never throws.</summary>
        public static List<string> RecoverAll(string modsFolder)
        {
            List<string> done = new List<string>();
            try
            {
                if (string.IsNullOrEmpty(modsFolder) || !Directory.Exists(modsFolder))
                {
                    return done;
                }
                foreach (string dir in Directory.GetDirectories(modsFolder))
                {
                    if (!TestModName.IsValid(Path.GetFileName(dir)) || SandboxPath.Probe(dir).IsReparsePoint)
                    {
                        continue;
                    }
                    foreach (string line in Recover(dir))
                    {
                        done.Add(Path.GetFileName(dir) + ": " + line);
                    }
                }
            }
            catch (Exception e)
            {
                done.Add("import recovery problem: " + e.Message);
            }
            return done;
        }
    }
}
