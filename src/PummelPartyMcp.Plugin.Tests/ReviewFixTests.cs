using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Newtonsoft.Json.Linq;
using PummelPartyMcp.Core;

namespace PummelPartyMcp.Tests
{
    /// <summary>
    /// Fixes of the Phase 4 review: C1 (links inside a sandbox mod are never written or deleted through) on real temp folders with
    /// real junctions (mklink /J, no privilege needed), hard links (CreateHardLink) and file symlinks (where the machine allows them);
    /// C2 (start_playtest's save gets the prefab protection) with the fake editor; and the later findings. Every link created here
    /// points at another folder this test created under the system temp folder; all of it is removed in finally.
    /// </summary>
    internal static class ReviewFixTests
    {
        private static Action<string, bool> s_check;
        private static string s_root;
        private static readonly List<string> s_links = new List<string>();

        public static void Run(Action<string, bool> check)
        {
            s_check = check;
            s_root = Path.Combine(Path.GetTempPath(), "mcp-review-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(s_root);
            try
            {
                SandboxBasics();
                JunctionCases();
                HardLinkCases();
                SymlinkCase();
                OpenPreCheck();
                PlaytestSaveProtection();
                Extra();
            }
            finally
            {
                Cleanup();
            }
        }

        private static void Check(string name, bool ok) => s_check(name, ok);

        private static void Cleanup()
        {
            // remove every link first (non-recursive: only the link itself), then the tree
            foreach (string l in s_links.AsEnumerable().Reverse())
            {
                try
                {
                    if (Directory.Exists(l))
                    {
                        Directory.Delete(l, false);
                    }
                    else if (File.Exists(l))
                    {
                        File.Delete(l);
                    }
                }
                catch (Exception)
                {
                    // reported by the leftover check below
                }
            }
            try
            {
                Directory.Delete(s_root, true);
            }
            catch (Exception)
            {
                // temp
            }
            Check("the review-fix tests left no temp directory behind", !Directory.Exists(s_root));
        }

        private static string CodeOf(Action a)
        {
            try
            {
                a();
                return null;
            }
            catch (RpcException e)
            {
                return e.Code;
            }
            catch (Exception e)
            {
                return "OTHER:" + e.GetType().Name;
            }
        }

        private static string MessageOf(Action a)
        {
            try
            {
                a();
                return null;
            }
            catch (Exception e)
            {
                return e.Message;
            }
        }

        private static string Dir(params string[] parts)
        {
            string d = Path.Combine(new[] { s_root }.Concat(parts).ToArray());
            Directory.CreateDirectory(d);
            return d;
        }

        private static string Put(string dir, string name, byte[] bytes)
        {
            Directory.CreateDirectory(dir);
            string p = Path.Combine(dir, name);
            File.WriteAllBytes(p, bytes);
            return p;
        }

        private static string Hash(string p)
        {
            using (SHA256 sha = SHA256.Create())
            {
                return Convert.ToBase64String(sha.ComputeHash(File.ReadAllBytes(p)));
            }
        }

        /// <summary>Hash of every file below a folder (relative path + content): proves the link target is byte-identical afterwards.</summary>
        private static string TreeHash(string dir)
        {
            return string.Join("|", Directory.GetFiles(dir, "*", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .Select(f => f.Substring(dir.Length) + "=" + Hash(f)).ToArray());
        }

        private static bool Junction(string link, string target)
        {
            if (!target.StartsWith(s_root, StringComparison.OrdinalIgnoreCase) || !link.StartsWith(s_root, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("test links must stay inside the test's temp folder");
            }
            ProcessStartInfo psi = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using (Process p = Process.Start(psi))
            {
                p.StandardOutput.ReadToEnd();
                p.StandardError.ReadToEnd();
                p.WaitForExit(15000);
            }
            bool ok = Directory.Exists(link) && (new DirectoryInfo(link).Attributes & FileAttributes.ReparsePoint) != 0;
            if (ok)
            {
                s_links.Add(link);
            }
            return ok;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateHardLinkW")]
        private static extern bool CreateHardLink(string newFile, string existingFile, IntPtr security);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateSymbolicLinkW")]
        private static extern bool CreateSymbolicLink(string link, string target, int flags);

        private static bool HardLink(string link, string existing)
        {
            if (!existing.StartsWith(s_root, StringComparison.OrdinalIgnoreCase) || !link.StartsWith(s_root, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("test links must stay inside the test's temp folder");
            }
            bool ok = CreateHardLink(link, existing, IntPtr.Zero);
            if (ok)
            {
                s_links.Add(link);
            }
            return ok;
        }

        private static byte[] Png() => new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R', 0, 0, 0, 4, 0, 0, 0, 4, 8, 6, 0, 0, 0, 1, 2, 3 };

        private static byte[] Pfab(string name, int extra)
        {
            using (MemoryStream ms = new MemoryStream())
            using (BinaryWriter w = new BinaryWriter(ms))
            {
                w.Write("PMH");
                w.Write((ushort)1);
                w.Write((ushort)0);
                w.Write(name);
                w.Write(true);
                w.Write(0);
                w.Write("Untagged");
                w.Write((ushort)0);
                for (int i = 0; i < extra; i++)
                {
                    w.Write((byte)(i % 251));
                }
                w.Flush();
                return ms.ToArray();
            }
        }

        /// <summary>A "real mod" outside the sandbox (the link target): textures, a prefab, a material, with their .pmeta.</summary>
        private static string RealModTarget(string tag)
        {
            string real = Dir("targets", tag, "Real Mod");
            string tex = Path.Combine(real, "Assets", "Textures");
            Put(tex, "rock.png", Png());
            Put(tex, "rock.png.pmeta", System.Text.Encoding.UTF8.GetBytes("{\"real\":true}"));
            Put(Path.Combine(tex, "Sub"), "deep.png", Png());
            Put(Path.Combine(tex, "Sub"), "deep.png.pmeta", System.Text.Encoding.UTF8.GetBytes("{\"real\":2}"));
            Put(Path.Combine(real, "Assets", "Prefabs"), "Crate.pfab", Pfab("Crate", 40));
            Put(Path.Combine(real, "Assets", "Prefabs"), "Crate.pfab.pmeta", System.Text.Encoding.UTF8.GetBytes("{\"p\":1}"));
            Put(Path.Combine(real, "Assets", "Materials"), "Mat.pmat", System.Text.Encoding.UTF8.GetBytes("PMAT"));
            Put(Path.Combine(real, "Assets", "Materials"), "Mat.pmat.pmeta", System.Text.Encoding.UTF8.GetBytes("{\"m\":1}"));
            Put(Path.Combine(real, "Data"), "Meta.json", System.Text.Encoding.UTF8.GetBytes("{\"Name\":\"Real Mod\"}"));
            return real;
        }

        // ------------------------------------------------------------------ basics

        private static void SandboxBasics()
        {
            Console.WriteLine("-- unit: SandboxPath basics (C1)");
            string mod = Dir("basic", "MCP_Basic");
            Put(Path.Combine(mod, "Assets", "Textures"), "a.png", Png());
            Check("a plain file inside the mod may be written", CodeOf(() => SandboxPath.RequireInsideMod(mod, Path.Combine(mod, "Assets", "Textures", "a.png"), "t")) == null);
            Check("a not-yet-existing file in a not-yet-existing folder may be created", CodeOf(() => SandboxPath.RequireInsideMod(mod, Path.Combine(mod, "Assets", "New", "Deeper", "x.png"), "t")) == null);
            Check("the mod root itself is not a file target", CodeOf(() => SandboxPath.RequireInsideMod(mod, mod, "t")) == ErrorCodes.InvalidState);
            Check("a path outside the mod is refused", CodeOf(() => SandboxPath.RequireInsideMod(mod, Path.Combine(s_root, "basic", "x.png"), "t")) == ErrorCodes.InvalidState);
            Check("a traversing path is normalised and refused", CodeOf(() => SandboxPath.RequireInsideMod(mod, Path.Combine(mod, "Assets", "..", "..", "x.png"), "t")) == ErrorCodes.InvalidState);
            Check("a sibling folder with the mod name as prefix is refused", CodeOf(() => SandboxPath.RequireInsideMod(mod, mod + "2\\x.png", "t")) == ErrorCodes.InvalidState);
            string real = Dir("basic", "Real Mod");
            Check("a non-sandbox mod root is refused (GUARD_NOT_TEST_MOD)", CodeOf(() => SandboxPath.RequireInsideMod(real, Path.Combine(real, "x.png"), "t")) == ErrorCodes.GuardNotTestMod);
            Check("UNC, device and ADS paths are refused",
                CodeOf(() => SandboxPath.RequireInsideMod(mod, "\\\\server\\share\\MCP_Basic\\x.png", "t")) != null
                && CodeOf(() => SandboxPath.RequireInsideMod("\\\\?\\" + mod, "\\\\?\\" + mod + "\\x.png", "t")) != null
                && CodeOf(() => SandboxPath.RequireInsideMod(mod, Path.Combine(mod, "x.png:stream"), "t")) != null);
            Check("a folder existing at a file target is refused", CodeOf(() => SandboxPath.RequireInsideMod(mod, Path.Combine(mod, "Assets", "Textures"), "t")) == ErrorCodes.InvalidState);
            Put(mod, "Blocker", new byte[1]);
            Check("a file in the place of a folder on the way is refused", CodeOf(() => SandboxPath.RequireInsideMod(mod, Path.Combine(mod, "Blocker", "x.png"), "t")) == ErrorCodes.InvalidState);
            Check("a missing mod root is refused", CodeOf(() => SandboxPath.RequireInsideMod(Path.Combine(s_root, "basic", "MCP_Missing"), Path.Combine(s_root, "basic", "MCP_Missing", "x"), "t")) == ErrorCodes.InvalidState);
            Check("FindLink: no link in a plain mod", SandboxPath.FindLink(mod, Path.Combine(mod, "Assets", "Textures", "a.png")) == null);
            string msg = MessageOf(() => SandboxPath.RequireInsideMod(real, Path.Combine(real, "x.png"), "remove_asset"));
            Check("the message names the operation and says nothing was written", msg != null && msg.StartsWith("remove_asset:") && msg.Contains("nothing was written or deleted"));

            // the probe seam: an undeterminable link count or an unreadable path is refused
            string file = Path.Combine(mod, "Assets", "Textures", "a.png");
            try
            {
                SandboxPath.ProbeOverride = p => string.Equals(p, file, StringComparison.OrdinalIgnoreCase)
                    ? new FileProbe { Exists = true, Links = -1 }
                    : new FileProbe { Exists = Directory.Exists(p), IsDirectory = Directory.Exists(p) };
                Check("a hard-link count that cannot be determined is refused (LINK_REFUSED)", CodeOf(() => SandboxPath.RequireInsideMod(mod, file, "t")) == ErrorCodes.LinkRefused);
                SandboxPath.ProbeOverride = p => string.Equals(p, Path.Combine(mod, "Assets"), StringComparison.OrdinalIgnoreCase)
                    ? new FileProbe { Error = "Win32 error 5" }
                    : new FileProbe { Exists = Directory.Exists(p) || File.Exists(p), IsDirectory = Directory.Exists(p), Links = 1 };
                Check("a folder on the way that cannot be examined is refused", CodeOf(() => SandboxPath.RequireInsideMod(mod, file, "t")) == ErrorCodes.InvalidState);
            }
            finally
            {
                SandboxPath.ProbeOverride = null;
            }
            Check("the real Win32 probe reports one link for a plain file", SandboxPath.Probe(file).Links == 1 && SandboxPath.Probe(file).Exists && !SandboxPath.Probe(file).IsReparsePoint);
        }

        // ------------------------------------------------------------------ junctions

        /// <summary>
        /// Every operation kind against a mod with a junction at <paramref name="where"/>: import target, rollback delete, remove (the
        /// paths RemoveAsset deletes), restore, material/prefab write pre-check, save pre-check, folder check, listing flag.
        /// </summary>
        private static void JunctionCases()
        {
            Console.WriteLine("-- unit: junctions inside a sandbox mod are never written through (C1)");
            foreach (string where in new[] { "Assets", "Assets\\Textures", "Assets\\Textures\\Sub", "Assets\\Prefabs", "Assets\\Materials" })
            {
                string tag = where.Replace('\\', '_');
                string real = RealModTarget(tag);
                string before = TreeHash(real);
                string mod = Dir("j-" + tag, "MCP_Linked");
                string linkPath = Path.Combine(mod, where);
                Directory.CreateDirectory(Path.GetDirectoryName(linkPath));
                if (!Junction(linkPath, Path.Combine(real, where)))
                {
                    Check($"junction at {where}: mklink /J works on this machine", false);
                    continue;
                }
                string assets = Path.Combine(mod, "Assets");
                string tex = Path.Combine(assets, "Textures");
                string victim = where.EndsWith("Sub") ? Path.Combine(tex, "Sub", "deep.png")
                    : where.EndsWith("Prefabs") ? Path.Combine(assets, "Prefabs", "Crate.pfab")
                    : where.EndsWith("Materials") ? Path.Combine(assets, "Materials", "Mat.pmat")
                    : Path.Combine(tex, "rock.png");
                Check($"junction at {where}: the file behind it is visible through the mod (precondition)", File.Exists(victim));

                Check($"junction at {where}: write/delete of a file behind it -> LINK_REFUSED",
                    CodeOf(() => SandboxPath.RequireInsideMod(mod, victim, "t")) == ErrorCodes.LinkRefused
                    && CodeOf(() => SandboxPath.RequireInsideMod(mod, victim + ".pmeta", "t")) == ErrorCodes.LinkRefused);
                Check($"junction at {where}: FindLink names it (list_assets/get_asset 'linked')", SandboxPath.FindLink(mod, victim) != null);

                // import target (copy) behind the link
                if (where == "Assets" || where == "Assets\\Textures")
                {
                    string src = Put(Dir("src-" + tag), "new.png", Png());
                    Check($"junction at {where}: the Assets folder check refuses (import, create_material, create_prefab)",
                        CodeOf(() => AssetImportRules.ValidateAssetsFolder(mod, assets)) == (where == "Assets" ? ErrorCodes.LinkRefused : null));
                    ImportSource s = AssetImportRules.CheckSource(src, null, AssetLimits.Default);
                    Check($"junction at {where}: import copy target refused", CodeOf(() => AssetImportRules.CopyIntoMod(s, assets, "new")) == ErrorCodes.LinkRefused);
                    Check($"junction at {where}: import destination folder refused", CodeOf(() => AssetImportRules.EnsureSubFolder(assets, AssetImportKind.Texture)) == ErrorCodes.LinkRefused);
                }

                // rollback delete
                ImportDestination d = new ImportDestination
                {
                    AssetsFolder = assets, SubFolder = Path.GetFileName(Path.GetDirectoryName(victim)), FullPath = victim, MetaPath = victim + ".pmeta",
                    Stem = Path.GetFileNameWithoutExtension(victim), Extension = Path.GetExtension(victim),
                };
                AssetImportRules.Created fake = new AssetImportRules.Created { Destination = d, FileCreated = true, MetaStepReached = true, MetaExistedBefore = false };
                AssetImportRules.CleanupReport rep = where.EndsWith("Sub") ? null : AssetImportRules.Rollback(fake);
                Check($"junction at {where}: rollback deletes nothing behind it", rep == null || (rep.Deleted.Count == 0 && rep.Problems.Count > 0));

                // remove: the two (or four for a prop) paths the editor's RemoveAsset deletes
                Check($"junction at {where}: remove (file + .pmeta) refused",
                    CodeOf(() => SandboxPath.RequireAllInsideMod(mod, new[] { victim, victim + ".pmeta" }, "remove_asset")) == ErrorCodes.LinkRefused);

                // material / prefab write pre-check
                if (victim.EndsWith(".pfab"))
                {
                    Check($"junction at {where}: prefab write pre-check (close_prefab/save) refused",
                        CodeOf(() => PrefabWriteGuard.Prepare("Crate", victim, assets, () => Pfab("Crate", 1))) == ErrorCodes.LinkRefused);
                    Check($"junction at {where}: new prefab pre-check (create_prefab) refused",
                        CodeOf(() => PrefabWriteGuard.PrepareNew("New", Path.Combine(assets, "Prefabs", "New.pfab"), assets)) == ErrorCodes.LinkRefused);
                }
                if (victim.EndsWith(".pmat"))
                {
                    Check($"junction at {where}: material write pre-check (create_material/set_material) refused",
                        CodeOf(() => SandboxPath.RequireFolderInsideMod(mod, Path.Combine(assets, "Materials"), "create_material")) == ErrorCodes.LinkRefused
                        && CodeOf(() => SandboxPath.RequireAllInsideMod(mod, new[] { victim, victim + ".pmeta" }, "set_material")) == ErrorCodes.LinkRefused);
                }

                // restore of a safety copy behind the link
                PrefabFileJob job = new PrefabFileJob
                {
                    Name = "X", PfabPath = victim, MetaPath = victim + ".pmeta", AssetsFolder = assets,
                    Expected = new byte[3], // "bad" result: the length check fails, Repair runs
                    Pfab = new FileSnapshot { Path = victim, Existed = true, Bytes = new byte[] { 1, 2, 3, 4 } },
                    Meta = new FileSnapshot { Path = victim + ".pmeta", Existed = true, Bytes = new byte[] { 5 } },
                };
                PrefabFileOutcome o = PrefabWriteGuard.Finish(job);
                Check($"junction at {where}: restore through it refused (reported, nothing written)", !o.Ok && !o.Restored && o.RestoreProblem != null && o.RestoreProblem.Contains("link"));
                Check($"junction at {where}: the safety-copy writer refuses too", CodeOf(() => PrefabWriteGuard.AtomicWrite(victim, new byte[] { 9 }, mod)) == ErrorCodes.LinkRefused);

                // save pre-check: every target the save writes; one link refuses the whole save and names it
                List<string> targets = ModSaveTargets.Build(mod, new[]
                {
                    new SavedAssetFile { Name = "victim", FilePath = victim, RewritesFile = victim.EndsWith(".pfab") || victim.EndsWith(".pmat") },
                }, includePreview: true);
                string saveMsg = MessageOf(() => SandboxPath.RequireAllWritable(mod, targets, "save_mod"));
                Check($"junction at {where}: save pre-check refuses the whole save and names the link",
                    CodeOf(() => SandboxPath.RequireAllWritable(mod, targets, "save_mod")) == ErrorCodes.LinkRefused && saveMsg.Contains("refused before anything was written") && saveMsg.Contains(where.Split('\\').Last()));

                Check($"junction at {where}: the link target is byte-identical afterwards", TreeHash(real) == before);
            }

            // junction at the mod root itself
            string realRoot = RealModTarget("root");
            string beforeRoot = TreeHash(realRoot);
            string parent = Dir("j-root");
            string linkedRoot = Path.Combine(parent, "MCP_RootLink");
            if (Junction(linkedRoot, realRoot))
            {
                string v = Path.Combine(linkedRoot, "Assets", "Textures", "rock.png");
                Check("junction AS the mod root: any write refused (LINK_REFUSED)", CodeOf(() => SandboxPath.RequireInsideMod(linkedRoot, v, "t")) == ErrorCodes.LinkRefused);
                Check("junction AS the mod root: Assets folder check refused", CodeOf(() => AssetImportRules.ValidateAssetsFolder(linkedRoot, Path.Combine(linkedRoot, "Assets"))) == ErrorCodes.LinkRefused);
                Check("junction AS the mod root: save pre-check refused", CodeOf(() => SandboxPath.RequireAllWritable(linkedRoot, ModSaveTargets.Build(linkedRoot, new SavedAssetFile[0], true), "save_mod")) == ErrorCodes.LinkRefused);
                Check("junction AS the mod root: open refused", CodeOf(() => SandboxPath.RequireOpenable(linkedRoot)) == ErrorCodes.LinkRefused);
                Check("junction AS the mod root: FindLink names it", SandboxPath.FindLink(linkedRoot, v) != null);
                Check("junction AS the mod root: the target is byte-identical afterwards", TreeHash(realRoot) == beforeRoot);
            }
            else
            {
                Check("junction at the mod root: mklink /J works on this machine", false);
            }
        }

        // ------------------------------------------------------------------ hard links

        private static void HardLinkCases()
        {
            Console.WriteLine("-- unit: hard-linked files inside a sandbox mod are never written or deleted (C1)");
            string real = RealModTarget("hard");
            string before = TreeHash(real);
            string mod = Dir("hard", "MCP_Hard");
            string tex = Dir("hard", "MCP_Hard", "Assets", "Textures");
            string prefabs = Dir("hard", "MCP_Hard", "Assets", "Prefabs");
            string h = Path.Combine(tex, "rock.png");
            string hm = Path.Combine(tex, "rock.png.pmeta");
            string hp = Path.Combine(prefabs, "Crate.pfab");
            bool made = HardLink(h, Path.Combine(real, "Assets", "Textures", "rock.png"))
                        && HardLink(hm, Path.Combine(real, "Assets", "Textures", "rock.png.pmeta"))
                        && HardLink(hp, Path.Combine(real, "Assets", "Prefabs", "Crate.pfab"));
            Check("CreateHardLink works in the temp folder (precondition)", made);
            if (!made)
            {
                return;
            }
            string assets = Path.Combine(mod, "Assets");
            Check("hard link: the probe counts 2 links", SandboxPath.Probe(h).Links == 2);
            Check("hard link: write/delete refused (LINK_REFUSED)", CodeOf(() => SandboxPath.RequireInsideMod(mod, h, "t")) == ErrorCodes.LinkRefused
                && MessageOf(() => SandboxPath.RequireInsideMod(mod, h, "t")).Contains("hard-linked"));
            Check("hard link: remove refused", CodeOf(() => SandboxPath.RequireAllInsideMod(mod, new[] { h, hm }, "remove_asset")) == ErrorCodes.LinkRefused);
            ImportDestination d = new ImportDestination { AssetsFolder = assets, SubFolder = "Textures", FullPath = h, MetaPath = hm, Stem = "rock", Extension = ".png" };
            AssetImportRules.CleanupReport rep = AssetImportRules.Rollback(new AssetImportRules.Created { Destination = d, FileCreated = true, MetaStepReached = true, MetaExistedBefore = false });
            Check("hard link: rollback deletes neither the file nor the .pmeta", rep.Deleted.Count == 0 && File.Exists(h) && File.Exists(hm));
            Check("hard link: prefab write pre-check refused", CodeOf(() => PrefabWriteGuard.Prepare("Crate", hp, assets, () => Pfab("Crate", 1))) == ErrorCodes.LinkRefused);
            PrefabFileJob job = new PrefabFileJob
            {
                Name = "Crate", PfabPath = hp, MetaPath = hp + ".pmeta", AssetsFolder = assets, Expected = new byte[3],
                Pfab = new FileSnapshot { Path = hp, Existed = true, Bytes = new byte[] { 1, 2 } },
                Meta = new FileSnapshot { Path = hp + ".pmeta", Existed = false },
            };
            PrefabFileOutcome o = PrefabWriteGuard.Finish(job);
            Check("hard link: restore refused and reported", !o.Restored && o.RestoreProblem != null);
            List<string> targets = ModSaveTargets.Build(mod, new[]
            {
                new SavedAssetFile { Name = "rock", FilePath = h, RewritesFile = false }, // the save rewrites rock.png.pmeta (hard-linked)
                new SavedAssetFile { Name = "Crate", FilePath = hp, RewritesFile = true },
            }, includePreview: false);
            string msg = MessageOf(() => SandboxPath.RequireAllWritable(mod, targets, "save_mod"));
            Check("hard link: save pre-check refuses and names both files", CodeOf(() => SandboxPath.RequireAllWritable(mod, targets, "save_mod")) == ErrorCodes.LinkRefused
                && msg.Contains("rock.png.pmeta") && msg.Contains("Crate.pfab") && msg.Contains("2 target(s)"));
            Check("hard link: FindLink reports the extra links", SandboxPath.FindLink(mod, h) != null);
            Check("hard link: the other copies are byte-identical afterwards", TreeHash(real) == before);
        }

        // ------------------------------------------------------------------ file symlink

        private static void SymlinkCase()
        {
            Console.WriteLine("-- unit: a file symlink inside a sandbox mod (C1)");
            string real = RealModTarget("sym");
            string before = TreeHash(real);
            string mod = Dir("sym", "MCP_Sym");
            string tex = Dir("sym", "MCP_Sym", "Assets", "Textures");
            string link = Path.Combine(tex, "rock.png");
            string target = Path.Combine(real, "Assets", "Textures", "rock.png");
            bool made = CreateSymbolicLink(link, target, 0x2 /* ALLOW_UNPRIVILEGED_CREATE (developer mode) */);
            if (!made || !File.Exists(link))
            {
                Console.WriteLine("  SKIP  file symlink cases: this machine does not allow creating symbolic links without elevation (developer mode off); junction and hard-link cases cover the rest");
                return;
            }
            s_links.Add(link);
            Check("file symlink: probe sees the reparse point", SandboxPath.Probe(link).IsReparsePoint);
            Check("file symlink: write/delete refused (LINK_REFUSED)", CodeOf(() => SandboxPath.RequireInsideMod(mod, link, "t")) == ErrorCodes.LinkRefused);
            AssetImportRules.CleanupReport rep = AssetImportRules.Rollback(new AssetImportRules.Created
            {
                Destination = new ImportDestination { AssetsFolder = Path.Combine(mod, "Assets"), SubFolder = "Textures", FullPath = link, MetaPath = link + ".pmeta", Stem = "rock", Extension = ".png" },
                FileCreated = true,
            });
            Check("file symlink: rollback does not delete it", rep.Deleted.Count == 0 && File.Exists(link));
            Check("file symlink: save pre-check refused", CodeOf(() => SandboxPath.RequireAllWritable(mod, new[] { link }, "save_mod")) == ErrorCodes.LinkRefused);
            Check("file symlink: open refused while it has no .pmeta", CodeOf(() => SandboxPath.RequireOpenable(mod)) == ErrorCodes.LinkRefused);
            Check("file symlink: the target is byte-identical afterwards", TreeHash(real) == before);
        }

        // ------------------------------------------------------------------ open

        private static void OpenPreCheck()
        {
            Console.WriteLine("-- unit: open_test_mod pre-check (the game writes missing .pmeta files when loading)");
            string real = RealModTarget("open");
            string mod = Dir("open", "MCP_Open");
            Dir("open", "MCP_Open", "Assets");
            Check("a mod without links opens", CodeOf(() => SandboxPath.RequireOpenable(mod)) == null);
            if (!Junction(Path.Combine(mod, "Assets", "Textures"), Path.Combine(real, "Assets", "Textures")))
            {
                Check("open pre-check: mklink /J works", false);
                return;
            }
            Check("a junction whose files all have their .pmeta opens (they are only read; listed as linked)", CodeOf(() => SandboxPath.RequireOpenable(mod)) == null);
            Put(Path.Combine(real, "Assets", "Textures"), "bare.png", Png());
            string before = TreeHash(real);
            string msg = MessageOf(() => SandboxPath.RequireOpenable(mod));
            Check("a file behind a junction without a .pmeta refuses the open and names it", CodeOf(() => SandboxPath.RequireOpenable(mod)) == ErrorCodes.LinkRefused && msg.Contains("bare.png"));
            Check("the open pre-check wrote nothing", TreeHash(real) == before);
        }

        // ------------------------------------------------------------------ C2

        private sealed class Completion : IWorkCompletion
        {
            public bool Committed;
            public object Result;
            public Exception Error;
            public bool IsCancelled => false;
            public bool TryCommit() { Committed = true; return true; }
            public TimeSpan Remaining => TimeSpan.FromSeconds(60);
            public void SetResult(object result) => Result = result;
            public void SetException(Exception error) => Error = error;
        }

        private sealed class Host : IPlaytestHost
        {
            public PlayState State { get; set; } = PlayState.Editing;
            public int Enters;
            public Action OnEnter;
            public RpcException CheckSameMod() => null;
            public void EnterPlayMode() { Enters++; OnEnter?.Invoke(); }
            public void PressStop() { }
            public bool EditSceneRestored() => true;
        }

        private static void PlaytestSaveProtection()
        {
            Console.WriteLine("-- unit: start_playtest save protection (C2, fake editor, real temp files)");
            string mod = Dir("c2", "MCP_Play");
            string assets = Path.Combine(mod, "Assets");
            string prefabs = Dir("c2", "MCP_Play", "Assets", "Prefabs");
            byte[] oldA = Pfab("A", 30), oldB = Pfab("B", 10);
            string a = Put(prefabs, "A.pfab", oldA);
            Put(prefabs, "A.pfab.pmeta", System.Text.Encoding.UTF8.GetBytes("{\"a\":1}"));
            string b = Put(prefabs, "B.pfab", oldB);
            Put(prefabs, "B.pfab.pmeta", System.Text.Encoding.UTF8.GetBytes("{\"b\":1}"));
            byte[] newA = Pfab("A", 50), newB = Pfab("B", 20);
            Func<List<PrefabFileJob>> jobs = () => new List<PrefabFileJob>
            {
                PrefabWriteGuard.Prepare("A", a, assets, () => newA),
                PrefabWriteGuard.Prepare("B", b, assets, () => newB),
            };

            // 1. a good save: verified, result carries prefabsVerified
            Host h1 = new Host { OnEnter = null };
            h1.OnEnter = () => { File.WriteAllBytes(a, newA); File.WriteAllBytes(b, newB); h1.State = PlayState.PlayMode; };
            Completion c1 = new Completion();
            PlaytestFlow f1 = PlaytestFlow.ForStart(h1, c1, o => new JObject { ["prefabsVerified"] = o.SaveReport?.Verified });
            f1.SaveProtection = new PrefabSaveProtection(jobs);
            f1.Start();
            Check("C2: a clean playtest save verifies every prefab (prefabsVerified 2)", c1.Error == null && (int?)((JObject)c1.Result)["prefabsVerified"] == 2);

            // 2. a dry run that throws refuses the playtest before anything changes
            File.WriteAllBytes(a, oldA);
            File.WriteAllBytes(b, oldB);
            Host h2 = new Host();
            Completion c2 = new Completion();
            PlaytestFlow f2 = PlaytestFlow.ForStart(h2, c2, o => new JObject());
            f2.SaveProtection = new PrefabSaveProtection(() => new List<PrefabFileJob> { PrefabWriteGuard.Prepare("A", a, assets, () => throw new InvalidOperationException("component throws")) });
            f2.Start();
            Check("C2: a prefab that cannot be serialised refuses the playtest: Play not pressed, not committed, files unchanged",
                (c2.Error as RpcException)?.Code == ErrorCodes.InvalidState && h2.Enters == 0 && !c2.Committed && File.ReadAllBytes(a).SequenceEqual(oldA));

            // 3. the save truncates one prefab and the playtest starts: restored, and the error says it started
            Host h3 = new Host();
            h3.OnEnter = () => { File.WriteAllBytes(a, newA.Take(9).ToArray()); File.WriteAllBytes(b, newB); h3.State = PlayState.EnteringPlayMode; };
            Completion c3 = new Completion();
            PlaytestFlow f3 = PlaytestFlow.ForStart(h3, c3, o => new JObject());
            f3.SaveProtection = new PrefabSaveProtection(jobs);
            f3.Start();
            RpcException e3 = c3.Error as RpcException;
            JObject d3 = e3?.ErrorData as JObject;
            Check("C2: a truncated prefab after the playtest save is restored to its previous content", File.ReadAllBytes(a).SequenceEqual(oldA));
            Check("C2: the error says the playtest WAS started and names the restored prefab file",
                e3 != null && e3.Message.Contains("WAS started") && e3.Message.Contains("A") && d3 != null && (bool)d3["playtestStarted"] && ((JArray)d3["restoredPrefabs"]).Any(t => (string)t == "A"));
            Check("C2: the good prefab B keeps the new save", File.ReadAllBytes(b).SequenceEqual(newB));

            // 4. the editor stays in edit mode after a bad save: the error says it was NOT started
            File.WriteAllBytes(a, oldA);
            File.WriteAllBytes(b, oldB);
            Host h4 = new Host();
            h4.OnEnter = () => File.WriteAllBytes(b, new byte[0]);
            Completion c4 = new Completion();
            PlaytestFlow f4 = PlaytestFlow.ForStart(h4, c4, o => new JObject());
            f4.SaveProtection = new PrefabSaveProtection(jobs);
            f4.Start();
            RpcException e4 = c4.Error as RpcException;
            Check("C2: a bad save with the editor still in edit mode reports NOT started and restores the file",
                e4 != null && e4.Message.Contains("NOT started") && File.ReadAllBytes(b).SequenceEqual(oldB) && !(bool)((JObject)e4.ErrorData)["playtestStarted"]);

            // 5. the Play call throws after the save wrote a broken prefab: still verified and restored
            Host h5 = new Host();
            h5.OnEnter = () => { File.WriteAllBytes(a, new byte[] { 3 }); throw new InvalidOperationException("boom"); };
            Completion c5 = new Completion();
            PlaytestFlow f5 = PlaytestFlow.ForStart(h5, c5, o => new JObject());
            f5.SaveProtection = new PrefabSaveProtection(jobs);
            f5.Start();
            Check("C2: a Play call that throws still gets the prefab verification and restore", c5.Error != null && c5.Error.Message.Contains("boom") && File.ReadAllBytes(a).SequenceEqual(oldA));

            // 6. a link on the way refuses the playtest (C1 pre-check inside Prepare)
            string real = RealModTarget("c2");
            string mod6 = Dir("c2b", "MCP_PlayLinked");
            Directory.CreateDirectory(Path.Combine(mod6, "Assets"));
            if (Junction(Path.Combine(mod6, "Assets", "Prefabs"), Path.Combine(real, "Assets", "Prefabs")))
            {
                string before = TreeHash(real);
                string linkedPfab = Path.Combine(mod6, "Assets", "Prefabs", "Crate.pfab");
                Host h6 = new Host();
                Completion c6 = new Completion();
                PlaytestFlow f6 = PlaytestFlow.ForStart(h6, c6, o => new JObject());
                f6.SaveProtection = new PrefabSaveProtection(() =>
                {
                    SandboxPath.RequireAllWritable(mod6, ModSaveTargets.Build(mod6, new[] { new SavedAssetFile { Name = "Crate", FilePath = linkedPfab, RewritesFile = true } }, false), "start_playtest");
                    return new List<PrefabFileJob>();
                });
                f6.Start();
                Check("C2+C1: a link on the way refuses the playtest before Play (LINK_REFUSED, nothing written)",
                    (c6.Error as RpcException)?.Code == ErrorCodes.LinkRefused && h6.Enters == 0 && TreeHash(real) == before);
            }
        }

        // ------------------------------------------------------------------ later findings (filled in per finding)

        private static string FindPluginDir()
        {
            string dir = AppContext.BaseDirectory;
            for (int i = 0; i < 10 && dir != null; i++)
            {
                string p = Path.Combine(dir, "src", "PummelPartyMcp.Plugin");
                if (Directory.Exists(p))
                {
                    return p;
                }
                dir = Path.GetDirectoryName(dir);
            }
            return null;
        }

        private static readonly string[] s_guards = { "SandboxPath.", "RequireRemovable(", "RequireSavable(", "RequireModSavable(", "PrefabWriteGuard.Recheck(", "LinkProblem(", "RenameGuard.Before(" };

        /// <summary>Every occurrence of <paramref name="call"/> in <paramref name="text"/> has a link check in the preceding <paramref name="window"/> characters.</summary>
        private static bool GuardedBefore(string text, string call, int window, out int count)
        {
            count = 0;
            int at = 0;
            while ((at = text.IndexOf(call, at, StringComparison.Ordinal)) >= 0)
            {
                count++;
                string before = text.Substring(Math.Max(0, at - window), Math.Min(window, at));
                if (!s_guards.Any(g => before.Contains(g)))
                {
                    return false;
                }
                at += call.Length;
            }
            return true;
        }

        private static void C1SourceScan()
        {
            Console.WriteLine("-- unit: C1 source scan (every file-touching game call is preceded by the central link check)");
            string plugin = FindPluginDir();
            Check("plugin sources located", plugin != null);
            if (plugin == null)
            {
                return;
            }
            (string file, string call)[] sites =
            {
                ("AssetImportTools.cs", "ImportFile("), ("AssetImportTools.cs", "RemoveAsset("), ("AssetImportTools.cs", "ReimportAsset("),
                ("AssetImportTools.cs", "CreateNewMaterial("), ("AssetImportTools.cs", ".Rename("), ("AssetImportTools.cs", "pm.Save()"), ("AssetImportTools.cs", "asset.Save()"),
                ("PrefabTools.cs", "CreateNewPrefab("), ("PrefabTools.cs", "RemoveAsset("), ("PrefabTools.cs", ".Rename("), ("PrefabTools.cs", "CurrentPrefab = null;"),
                ("ModTools.cs", "ed.SaveCurrentMod()"), ("ModTools.cs", "ed.OpenMod(folder)"),
                ("SettingsTools.cs", "ed.TakeScreenGrab()"),
            };
            foreach ((string file, string call) in sites)
            {
                string text = File.ReadAllText(Path.Combine(plugin, "Tools", file));
                bool ok = GuardedBefore(text, call, 4000, out int n);
                Check($"C1 scan: every {call} in {file} ({n}x) follows a SandboxPath check", ok && n > 0);
            }
            string play = File.ReadAllText(Path.Combine(plugin, "Tools", "PlaytestTools.cs"));
            Check("C1/C2 scan: start_playtest sets the save protection (link pre-check + prefab dry runs) on the flow before Start",
                play.Contains("flow.SaveProtection = new PrefabSaveProtection") && play.Contains("RequireModSavable(")
                && play.IndexOf("flow.SaveProtection", StringComparison.Ordinal) < play.IndexOf("if (!flow.Start())", play.IndexOf("flow.SaveProtection", StringComparison.Ordinal), StringComparison.Ordinal));
            string core = File.ReadAllText(Path.Combine(plugin, "Core", "AssetImportCore.cs"));
            Check("C1 scan: the import core checks before CreateNew and before each rollback delete",
                GuardedBefore(core, "FileMode.CreateNew", 1500, out int _) && GuardedBefore(core, "File.Delete(", 1500, out int _));
            string prefabCore = File.ReadAllText(Path.Combine(plugin, "Core", "PrefabCore.cs"));
            Check("C1 scan: the prefab core checks before restoring, deleting and writing the temp file",
                GuardedBefore(prefabCore, "File.Delete(p)", 1500, out int _) && GuardedBefore(prefabCore, "FileMode.CreateNew", 1500, out int _)
                && prefabCore.Contains("LinkProblem(job, s.Path)"));
        }

        private static void Extra()
        {
            C1SourceScan();
            ReferenceLocations();
            PrefabScanAndFolder();
            RestoreFallback();
            PartialRename();
            ImportOnTheCopy();
            AudioDurations();
            InterruptedImport();
            Budgets();
        }

        // ------------------------------------------------------------------ L7

        private static void Budgets()
        {
            Console.WriteLine("-- unit: commit budgets sized for the work after the commit, server timeouts above them (L7)");
            foreach ((string tool, TimeSpan limit, TimeSpan budget) in CommitBudgets.All)
            {
                Check($"L7: {tool}: the commit budget ({budget.TotalSeconds:0} s) is below its limit ({limit.TotalSeconds:0} s) and at least 10 s",
                    budget < limit && budget >= TimeSpan.FromSeconds(10));
            }
            Check("L7: capture keeps its whole poll window after the commit", CommitBudgets.Capture >= CommitBudgets.CaptureWait + TimeSpan.FromSeconds(1));
            Check("L7: import has room for the decode of the longest allowed clip (budget >= 60 s)", CommitBudgets.Import >= TimeSpan.FromSeconds(60));
            string plugin = FindPluginDir();
            if (plugin == null)
            {
                return;
            }
            string client = File.ReadAllText(Path.Combine(plugin, "..", "PummelPartyMcp.Server", "src", "client.ts"));
            foreach ((string tool, TimeSpan limit, TimeSpan _) in CommitBudgets.All)
            {
                System.Text.RegularExpressions.Match m = System.Text.RegularExpressions.Regex.Match(client, "\\b" + tool + ":\\s*(\\d+)");
                double ms = m.Success ? double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : 0;
                Check($"L7: the server waits for {tool} at least the plugin limit + {CommitBudgets.Grace.TotalSeconds:0} s grace + 1 s ({ms} ms)",
                    ms >= (limit + CommitBudgets.Grace + TimeSpan.FromSeconds(1)).TotalMilliseconds);
            }
            foreach (string f in new[] { "AssetImportTools.cs", "PrefabTools.cs" })
            {
                string t = File.ReadAllText(Path.Combine(plugin, "Tools", f));
                Check($"L7: {f} commits only with the documented budgets", !t.Contains("inv.Commit(TimeSpan.FromSeconds(") && t.Contains("inv.Commit(CommitBudgets."));
            }
            string st = File.ReadAllText(Path.Combine(plugin, "Tools", "SettingsTools.cs"));
            Check("L7: capture_preview_image commits with CommitBudgets.Capture", st.Contains("inv.Commit(CommitBudgets.Capture)"));
            Check("L6: capture_preview_image assigns only a screenshot of the mod that was open at the start (same mod object, listed in it, in its Textures folder)",
                st.Contains("!ReferenceEquals(ed.CurrentMod, mod) || !AssetAccess.InModList(mod, created)") && st.Contains("nothing was assigned"));
            Check("L7: save_mod uses its documented limit and budget", File.ReadAllText(Path.Combine(plugin, "Tools", "ModTools.cs")).Contains("CommitBudgets.SaveTimeout"));
        }

        // ------------------------------------------------------------------ M1

        private static byte[] WavBytes(int byteRate, int dataBytes)
        {
            using (MemoryStream ms = new MemoryStream())
            using (BinaryWriter w = new BinaryWriter(ms))
            {
                w.Write(System.Text.Encoding.ASCII.GetBytes("RIFF")); w.Write(36 + dataBytes); w.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));
                w.Write(System.Text.Encoding.ASCII.GetBytes("LIST")); w.Write(4); w.Write(System.Text.Encoding.ASCII.GetBytes("INFO")); // a chunk before fmt
                w.Write(System.Text.Encoding.ASCII.GetBytes("fmt ")); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(byteRate); w.Write(byteRate); w.Write((short)1); w.Write((short)8);
                w.Write(System.Text.Encoding.ASCII.GetBytes("data")); w.Write(dataBytes); w.Write(new byte[dataBytes]);
                w.Flush();
                return ms.ToArray();
            }
        }

        private static byte[] OggPage(long granule, byte[] payload)
        {
            using (MemoryStream ms = new MemoryStream())
            using (BinaryWriter w = new BinaryWriter(ms))
            {
                w.Write(System.Text.Encoding.ASCII.GetBytes("OggS")); w.Write((byte)0); w.Write((byte)0); w.Write(granule); w.Write(1); w.Write(0); w.Write(0);
                w.Write((byte)1); w.Write((byte)payload.Length); w.Write(payload);
                w.Flush();
                return ms.ToArray();
            }
        }

        private static byte[] OggBytes(int rate, long lastGranule)
        {
            List<byte> id = new List<byte> { 1 };
            id.AddRange(System.Text.Encoding.ASCII.GetBytes("vorbis"));
            id.AddRange(BitConverter.GetBytes(0)); id.Add(2); id.AddRange(BitConverter.GetBytes(rate)); id.AddRange(new byte[15]);
            return OggPage(0, id.ToArray()).Concat(new byte[500]).Concat(OggPage(lastGranule, new byte[10])).ToArray();
        }

        /// <summary>MPEG1 Layer III 44.1 kHz stereo frame header (128 kbps or the given bitrate index) + optional Xing frame count.</summary>
        private static byte[] Mp3Bytes(int bitrateIndex, uint? xingFrames, int padTo)
        {
            List<byte> b = new List<byte> { 0xFF, 0xFB, (byte)((bitrateIndex << 4) | 0x00), 0x00 };
            b.AddRange(new byte[32]); // side information (MPEG1 stereo)
            if (xingFrames.HasValue)
            {
                b.AddRange(System.Text.Encoding.ASCII.GetBytes("Xing"));
                b.AddRange(new byte[] { 0, 0, 0, 1 });
                uint f = xingFrames.Value;
                b.AddRange(new[] { (byte)(f >> 24), (byte)(f >> 16), (byte)(f >> 8), (byte)f });
            }
            while (b.Count < padTo)
            {
                b.Add(0);
            }
            return b.ToArray();
        }

        private static void ImportOnTheCopy()
        {
            Console.WriteLine("-- unit: import checks run on the copy inside the mod; staging name (M1)");
            string mod = Dir("m1", "MCP_Import");
            string assets = Path.Combine(mod, "Assets");
            Directory.CreateDirectory(assets);
            string src = Dir("m1-src");

            // a good PNG: staging copy renamed, nothing left under the staging name
            byte[] png = Png();
            string good = Put(src, "good.png", png);
            ImportSource s = AssetImportRules.CheckSource(good, assets, AssetLimits.Default);
            AssetImportRules.Created c = AssetImportRules.CopyIntoMod(s, assets, "good", null, AssetLimits.Default);
            Check("M1: the copy lands under its final name and no staging copy remains", File.Exists(c.Destination.FullPath) && !File.Exists(c.Destination.FullPath + AssetImportRules.StagingSuffix)
                && Directory.GetFiles(Path.Combine(assets, "Textures"), "*" + AssetImportRules.StagingSuffix).Length == 0);
            Check("M1: the reply's checks include the checks run on the copy", s.Checks.Any(x => x.StartsWith("copy: PNG")) && s.Checks.Any(x => x.StartsWith("copy: image size")));
            Check("M1: the staging extension has no importer in the game (a leftover is never decoded)", !AssetImportRules.TryKind(AssetImportRules.StagingSuffix, out AssetImportKind _)
                && !AssetImportRules.TryKind(ImportJournal.FailedSuffix, out AssetImportKind _));

            // the source is swapped after the pre-checks (same length, not a PNG any more): the check on the copy refuses
            string swap = Put(src, "swap.png", png);
            ImportSource s2 = AssetImportRules.CheckSource(swap, assets, AssetLimits.Default);
            File.WriteAllBytes(swap, new byte[png.Length]);
            string before = string.Join("|", Directory.GetFiles(assets, "*", SearchOption.AllDirectories).OrderBy(x => x).ToArray());
            string msg = MessageOf(() => AssetImportRules.CopyIntoMod(s2, assets, "swap", null, AssetLimits.Default));
            Check("M1: a source swapped after the pre-checks is refused by the check on the copy (INVALID_PARAMS) and only the copy is removed",
                CodeOf(() => AssetImportRules.CopyIntoMod(AssetImportRules.CheckSource(good, assets, AssetLimits.Default) is ImportSource q ? Swapped(q, swap) : null, assets, "swap2", null, AssetLimits.Default)) == ErrorCodes.InvalidParams
                && msg != null && msg.Contains("checked on the copy") && msg.Contains("source file was not touched")
                && string.Join("|", Directory.GetFiles(assets, "*", SearchOption.AllDirectories).OrderBy(x => x).ToArray()) == before);

            // OBJ: face lines are capped on the copy
            System.Text.StringBuilder obj = new System.Text.StringBuilder("v 0 0 0\nv 1 0 0\nv 0 1 0\n");
            for (int i = 0; i <= AssetImportRules.MaxObjFaceLines; i++)
            {
                obj.Append("f 1 2 3\n");
            }
            string bigObj = Put(src, "faces.obj", System.Text.Encoding.ASCII.GetBytes(obj.ToString()));
            ImportSource so = AssetImportRules.CheckSource(bigObj, assets, AssetLimits.Default);
            Check("M1: an OBJ with more face lines than the cap is refused on the copy and nothing stays in Assets\\Props",
                CodeOf(() => AssetImportRules.CopyIntoMod(so, assets, "faces", null, AssetLimits.Default)) == ErrorCodes.InvalidParams
                && (!Directory.Exists(Path.Combine(assets, "Props")) || Directory.GetFiles(Path.Combine(assets, "Props")).Length == 0));
            AssetImportRules.ObjCounts oc = AssetImportRules.CountObjLines(Put(src, "small.obj", System.Text.Encoding.ASCII.GetBytes("v 0 0 0\r\nv\t1 0 0\nvn 0 0 1\nf 1 2 3\nfo x\n")));
            Check("M1: OBJ counting: 'v '/'v\\t' are vertices, 'vn' is not, 'f ' is a face, 'fo' is not", oc.Vertices == 2 && oc.Faces == 1);
        }

        /// <summary>Overwrites the source after its pre-checks (a TOCTOU swap) and returns the stale check result.</summary>
        private static ImportSource Swapped(ImportSource checkedSource, string swapWith)
        {
            File.WriteAllBytes(checkedSource.FullPath + ".tmp-swap", File.ReadAllBytes(swapWith));
            File.Copy(checkedSource.FullPath + ".tmp-swap", checkedSource.FullPath, true);
            File.Delete(checkedSource.FullPath + ".tmp-swap");
            return checkedSource;
        }

        private static void AudioDurations()
        {
            Console.WriteLine("-- unit: audio duration from the headers, caps on the copy (M1)");
            string src = Dir("m1-audio");
            string mod = Dir("m1a", "MCP_Audio");
            string assets = Path.Combine(mod, "Assets");
            Directory.CreateDirectory(assets);
            AssetLimits limits = new AssetLimits(50, 500, 600, 1);

            AudioDuration w = AudioHeaders.Wav(Put(src, "a.wav", WavBytes(8000, 16000)));
            Check("M1: WAV duration = data size / byte rate (a LIST chunk before fmt is skipped)", w.Seconds.HasValue && Math.Abs(w.Seconds.Value - 2.0) < 1e-9);
            string longWav = Put(src, "long.wav", WavBytes(10, 7000)); // 700 s
            Check("M1: a WAV longer than MaxAudioSeconds is refused on the copy",
                CodeOf(() => AssetImportRules.CopyIntoMod(AssetImportRules.CheckSource(longWav, assets, limits), assets, "long", null, limits)) == ErrorCodes.InvalidParams);
            string brokenWav = Put(src, "broken.wav", System.Text.Encoding.ASCII.GetBytes("RIFF\0\0\0\0WAVEjunk"));
            Check("M1: a WAV whose header gives no duration is refused", CodeOf(() => AssetImportRules.CopyIntoMod(AssetImportRules.CheckSource(brokenWav, assets, limits), assets, "broken", null, limits)) == ErrorCodes.InvalidParams);

            AudioDuration o = AudioHeaders.Ogg(Put(src, "a.ogg", OggBytes(44100, 44100L * 30)));
            Check("M1: OGG duration = last granule / Vorbis sample rate", o.Seconds.HasValue && Math.Abs(o.Seconds.Value - 30) < 1e-6);
            string longOgg = Put(src, "long.ogg", OggBytes(8000, 8000L * 3600));
            Check("M1: an OGG of an hour is refused (MaxAudioSeconds)", CodeOf(() => AssetImportRules.CopyIntoMod(AssetImportRules.CheckSource(longOgg, assets, limits), assets, "longogg", null, limits)) == ErrorCodes.InvalidParams);
            string okOgg = Put(src, "ok.ogg", OggBytes(44100, 44100L * 5));
            AssetImportRules.Created co = AssetImportRules.CopyIntoMod(AssetImportRules.CheckSource(okOgg, assets, limits), assets, "okogg", null, limits);
            Check("M1: a short OGG is imported", File.Exists(co.Destination.FullPath));

            AudioDuration x = AudioHeaders.Mp3(Put(src, "x.mp3", Mp3Bytes(9, 1000, 2000)));
            Check("M1: MP3 duration from the Xing frame count (1000 x 1152 / 44100)", x.Seconds.HasValue && Math.Abs(x.Seconds.Value - 1000 * 1152.0 / 44100) < 1e-6);
            string longMp3 = Put(src, "longx.mp3", Mp3Bytes(9, 100000, 2000)); // ~2612 s
            Check("M1: an MP3 whose Xing header says it is too long is refused", CodeOf(() => AssetImportRules.CopyIntoMod(AssetImportRules.CheckSource(longMp3, assets, limits), assets, "longx", null, limits)) == ErrorCodes.InvalidParams);
            string cbr = Put(src, "cbr.mp3", Mp3Bytes(1, null, 700 * 1024)); // 32 kbps, ~180 s estimate
            AudioDuration ce = AudioHeaders.Mp3(cbr);
            Check("M1: an MP3 without a frame count has no duration but a constant-bitrate estimate", !ce.Seconds.HasValue && ce.CbrEstimateSeconds.HasValue && ce.CbrEstimateSeconds.Value > 100);
            string lowCbr = Put(src, "lowcbr.mp3", Mp3Bytes(1, null, 900 * 1024)); // 32 kbps: ~230 s
            Check("M1: an MP3 without a frame count within the compressed cap (1 MB here) is accepted",
                CodeOf(() => AssetImportRules.CopyIntoMod(AssetImportRules.CheckSource(lowCbr, assets, limits), assets, "lowcbr", null, limits)) == null);
            string bigNoCount = Put(src, "big.mp3", Mp3Bytes(14, null, 2 * 1024 * 1024)); // 320 kbps, ~52 s estimate, 2 MB
            string capMsg = MessageOf(() => AssetImportRules.CopyIntoMod(AssetImportRules.CheckSource(bigNoCount, assets, limits), assets, "big", null, limits));
            Check("M1: an MP3 without a frame count above MaxCompressedAudioMB is refused (duration not readable)", capMsg != null && capMsg.Contains("MaxCompressedAudioMB"));
            string slow = Put(src, "slow.mp3", Mp3Bytes(1, null, 900 * 1024));
            AssetLimits shortLimits = new AssetLimits(50, 500, 60, 15);
            Check("M1: a constant-bitrate estimate above MaxAudioSeconds refuses", MessageOf(() => AssetImportRules.CopyIntoMod(AssetImportRules.CheckSource(slow, assets, shortLimits), assets, "slow", null, shortLimits))?.Contains("MaxAudioSeconds") == true);
            Check("M1: no staging copies are left after refused audio imports", Directory.GetFiles(assets, "*" + AssetImportRules.StagingSuffix, SearchOption.AllDirectories).Length == 0);
            Check("M1: the default audio caps are 600 s and 15 MB", AssetLimits.DefaultMaxAudioSeconds == 600 && AssetLimits.DefaultMaxCompressedAudioMB == 15 && AssetLimits.Default.MaxCompressedAudioMB == 15);
        }

        private static void InterruptedImport()
        {
            Console.WriteLine("-- unit: an import interrupted during the decode is quarantined, not decoded again (M1)");
            string mods = Dir("m1j");
            string mod = Dir("m1j", "MCP_Crash");
            string audio = Dir("m1j", "MCP_Crash", "Assets", "Audio");
            byte[] wav = WavBytes(8000, 800);
            string file = Put(audio, "boom.wav", wav);
            File.WriteAllText(file + ".pmeta", "{}"); // written by the importer before the decode
            ImportDestination d = new ImportDestination { AssetsFolder = Path.Combine(mod, "Assets"), SubFolder = "Audio", FullPath = file, MetaPath = file + ".pmeta", Stem = "boom", Extension = ".wav" };
            string sha;
            using (SHA256 h = SHA256.Create())
            {
                sha = BitConverter.ToString(h.ComputeHash(wav)).Replace("-", string.Empty).ToLowerInvariant();
            }
            ImportJournal.Write(mod, d, sha, metaExistedBefore: false);
            Check("M1: the journal is written into McpRecovery (outside Assets)", File.Exists(ImportJournal.PathOf(mod)) && ImportJournal.PathOf(mod).Contains(PrefabWriteGuard.RecoveryFolder));

            // a real mod with a journal next to it is never touched
            string real = Dir("m1j", "Real Mod");
            string realAudio = Dir("m1j", "Real Mod", "Assets", "Audio");
            string realFile = Put(realAudio, "boom.wav", wav);
            Directory.CreateDirectory(Path.Combine(real, PrefabWriteGuard.RecoveryFolder));
            File.Copy(ImportJournal.PathOf(mod), ImportJournal.PathOf(real));

            List<string> done = ImportJournal.RecoverAll(mods);
            Check("M1: recovery renames the interrupted copy and its own .pmeta (the game no longer decodes them) and removes the journal",
                !File.Exists(file) && File.Exists(file + ImportJournal.FailedSuffix) && !File.Exists(file + ".pmeta") && File.Exists(file + ImportJournal.FailedSuffix + ".pmeta")
                && !File.Exists(ImportJournal.PathOf(mod)) && done.Count >= 1 && done.All(l => l.StartsWith("MCP_Crash")));
            Check("M1: recovery never acts in a non-sandbox mod folder", File.Exists(realFile) && File.Exists(ImportJournal.PathOf(real)));

            // a journal whose file changed (or whose .pmeta pre-existed) moves nothing it cannot prove
            string file2 = Put(audio, "keep.wav", wav);
            File.WriteAllText(file2 + ".pmeta", "{\"theirs\":1}");
            ImportJournal.Write(mod, new ImportDestination { FullPath = file2, MetaPath = file2 + ".pmeta" }, sha, metaExistedBefore: true);
            ImportJournal.Recover(mod);
            Check("M1: a .pmeta that existed before the import is left in place", File.Exists(file2 + ".pmeta") && File.Exists(file2 + ImportJournal.FailedSuffix));
            string file3 = Put(audio, "changed.wav", wav);
            ImportJournal.Write(mod, new ImportDestination { FullPath = file3, MetaPath = file3 + ".pmeta" }, "00", metaExistedBefore: false);
            List<string> d3 = ImportJournal.Recover(mod);
            Check("M1: a file whose hash differs from the journal is not moved", File.Exists(file3) && d3.Any(l => l.Contains("nothing moved")) && !File.Exists(ImportJournal.PathOf(mod)));
            Check("M1: Clear removes the journal and never throws", CodeOf(() => ImportJournal.Clear(mod)) == null);
        }

        // ------------------------------------------------------------------ L8

        private static void PartialRename()
        {
            Console.WriteLine("-- unit: partial renames are detected and never reported as success (L8)");
            string mod = Dir("l8", "MCP_Rename");
            string mats = Dir("l8", "MCP_Rename", "Assets", "Materials");
            byte[] pmat = System.Text.Encoding.ASCII.GetBytes("PMAT-content");
            string Old(string n) => Path.Combine(mats, n + ".pmat");
            void Make(string n) { File.WriteAllBytes(Old(n), pmat); File.WriteAllText(Old(n) + ".pmeta", "{}"); }

            // full success
            Make("A");
            RenameSnapshot s1 = RenameGuard.Before(mod, Old("A"), Old("A2"), false);
            File.Move(Old("A"), Old("A2"));
            File.Move(Old("A") + ".pmeta", Old("A2") + ".pmeta");
            Check("L8: both files moved, asset path updated, no dialog -> success", RenameGuard.After(s1, Old("A2"), false).Ok);

            // success on disk but a dialog appeared -> failure
            Make("B");
            RenameSnapshot s2 = RenameGuard.Before(mod, Old("B"), Old("B2"), false);
            File.Move(Old("B"), Old("B2"));
            File.Move(Old("B") + ".pmeta", Old("B2") + ".pmeta");
            RenameResult r2 = RenameGuard.After(s2, Old("B2"), true);
            Check("L8: a dialog raised by the rename is a failure", !r2.Ok && r2.Problem.Contains("dialog"));

            // partial: file moved, .pmeta not, asset still at the old path -> moved back
            Make("C");
            RenameSnapshot s3 = RenameGuard.Before(mod, Old("C"), Old("C2"), false);
            File.Move(Old("C"), Old("C2"));
            RenameResult r3 = RenameGuard.After(s3, Old("C"), true);
            Check("L8: file moved without its .pmeta -> failure, moved back (provably safe), content intact",
                !r3.Ok && r3.MovedBack && r3.Orphan == null && File.Exists(Old("C")) && !File.Exists(Old("C2")) && File.ReadAllBytes(Old("C")).SequenceEqual(pmat) && r3.Problem.Contains("partial"));

            // partial, but the moved file's bytes changed -> not moved back, orphan reported
            Make("D");
            RenameSnapshot s4 = RenameGuard.Before(mod, Old("D"), Old("D2"), false);
            File.Move(Old("D"), Old("D2"));
            File.WriteAllText(Old("D2"), "changed by someone else");
            RenameResult r4 = RenameGuard.After(s4, Old("D"), false);
            Check("L8: a partial rename whose bytes changed is not moved back; the orphan is named", !r4.Ok && !r4.MovedBack && r4.Orphan == "D2.pmat" && File.Exists(Old("D2")));

            // nothing moved (the editor refused) -> failure
            Make("E");
            RenameSnapshot s5 = RenameGuard.Before(mod, Old("E"), Old("E2"), false);
            RenameResult r5 = RenameGuard.After(s5, Old("E"), true);
            Check("L8: nothing moved -> failure (the asset keeps its old name)", !r5.Ok && r5.Problem.Contains("nothing was moved"));

            // the new name taken / a dialog already open -> refused before the call
            Make("F");
            Make("G");
            Check("L8: a taken new name is refused before renaming", CodeOf(() => RenameGuard.Before(mod, Old("F"), Old("G"), false)) == ErrorCodes.AlreadyExists);
            Check("L8: an open dialog refuses the rename", CodeOf(() => RenameGuard.Before(mod, Old("F"), Old("F9"), true)) == ErrorCodes.InvalidState);

            string plugin = FindPluginDir();
            if (plugin != null)
            {
                string a = File.ReadAllText(Path.Combine(plugin, "Tools", "AssetImportTools.cs"));
                string p = File.ReadAllText(Path.Combine(plugin, "Tools", "PrefabTools.cs"));
                Check("L8: create_material and create_prefab judge the rename with RenameGuard and fail instead of warning",
                    a.Contains("RenameGuard.After(") && p.Contains("RenameGuard.After(") && !a.Contains("the editor raised a dialog while renaming") && !p.Contains("the editor raised a dialog while renaming")
                    && p.Contains("but renaming it to"));
            }
        }

        // ------------------------------------------------------------------ L5

        private static void RestoreFallback()
        {
            Console.WriteLine("-- unit: restore without File.Replace, and a failed restore keeps the safety copy (L5)");
            string mod = Dir("l5", "MCP_Restore");
            string assets = Path.Combine(mod, "Assets");
            string prefabs = Dir("l5", "MCP_Restore", "Assets", "Prefabs");
            byte[] old = Pfab("R", 30);
            string p = Put(prefabs, "R.pfab", old);
            Put(prefabs, "R.pfab.pmeta", System.Text.Encoding.UTF8.GetBytes("{\"r\":1}"));
            PrefabFileJob Job() => PrefabWriteGuard.Prepare("R", p, assets, () => Pfab("R", 60));
            string recovery = Path.Combine(mod, PrefabWriteGuard.RecoveryFolder);
            try
            {
                foreach (Exception missing in new Exception[] { new PlatformNotSupportedException("no Replace"), new NotImplementedException("no Replace") })
                {
                    PrefabFileJob j = Job();
                    File.WriteAllBytes(p, old.Take(5).ToArray()); // the editor's write broke the file
                    PrefabWriteGuard.ReplaceOp = (a, b) => throw missing;
                    PrefabFileOutcome o = PrefabWriteGuard.Finish(j);
                    Check($"L5: without File.Replace ({missing.GetType().Name}) the restore falls back to copy and succeeds",
                        o.Restored && o.RestoreProblem == null && PrefabWriteGuard.LastSwap == "copy-fallback" && File.ReadAllBytes(p).SequenceEqual(old));
                }
                PrefabWriteGuard.ResetFileOps();
                PrefabFileJob jr = Job();
                File.WriteAllBytes(p, old.Take(5).ToArray());
                PrefabFileOutcome ok = PrefabWriteGuard.Finish(jr);
                Check("L5: with File.Replace available the normal replace is used", ok.Restored && PrefabWriteGuard.LastSwap == "replace" && File.ReadAllBytes(p).SequenceEqual(old));
                Check("L5: no safety copy is kept on disk when the restore works", !Directory.Exists(recovery));
                Check("L5: no temp file is left after the fallback restores", Directory.GetFiles(prefabs, "*" + PrefabWriteGuard.RestoreTempSuffix).Length == 0);

                PrefabFileJob jf = Job();
                File.WriteAllBytes(p, old.Take(5).ToArray());
                PrefabWriteGuard.ReplaceOp = (a, b) => throw new IOException("sharing violation");
                PrefabFileOutcome bad = PrefabWriteGuard.Finish(jf);
                string[] kept = Directory.Exists(recovery) ? Directory.GetFiles(recovery) : new string[0];
                Check("L5: a failed restore is reported loudly with the path of the kept safety copy",
                    !bad.Ok && !bad.Restored && bad.RestoreProblem != null && bad.RestoreProblem.Contains("RESTORE FAILED") && bad.RestoreProblem.Contains(PrefabWriteGuard.RecoveryFolder + "/R.pfab.")
                    && bad.PreservedSnapshots.Count == 1 && ((JArray)bad.ToJson()["preservedSnapshots"]).Count == 1);
                Check("L5: the kept safety copy holds the previous bytes, outside Assets (the game does not read it)",
                    kept.Length == 1 && File.ReadAllBytes(kept[0]).SequenceEqual(old) && kept[0].EndsWith(PrefabWriteGuard.SnapshotSuffix) && !kept[0].StartsWith(assets, StringComparison.OrdinalIgnoreCase));
                Check("L5: the error text of a failed restore names the kept copy", PrefabWriteGuard.Describe(new[] { bad }).Contains("McpRecovery"));
            }
            finally
            {
                PrefabWriteGuard.ResetFileOps();
            }
        }

        // ------------------------------------------------------------------ M2, L4

        private static void PrefabScanAndFolder()
        {
            Console.WriteLine("-- unit: create_prefab refuses an incomplete subtree scan (M2) and checks FullPrefabsFolder (L4)");
            Check("M2: an incomplete subtree scan is a refusal that says nothing was created", PrefabSourceRules.RefusalForIncompleteScan(true)?.Contains("nothing was created") == true
                && PrefabSourceRules.RefusalForIncompleteScan(false) == null);
            string plugin = FindPluginDir();
            if (plugin == null)
            {
                return;
            }
            string tools = File.ReadAllText(Path.Combine(plugin, "Tools", "PrefabTools.cs"));
            int refusal = tools.IndexOf("RefusalForIncompleteScan(incomplete)", StringComparison.Ordinal);
            Check("M2: create_prefab throws on the incomplete scan before its commit point (no warning-only path left)",
                refusal > 0 && refusal < tools.IndexOf("inv.Commit(", refusal, StringComparison.Ordinal) && !tools.Contains("references inside them could not be checked"));
            int folder = tools.IndexOf("r.Mod.FullPrefabsFolder", StringComparison.Ordinal);
            Check("L4: create_prefab compares the game's FullPrefabsFolder with <mod>\\Assets\\Prefabs before CreateNewPrefab",
                folder > 0 && folder < tools.IndexOf("CreateNewPrefab(", StringComparison.Ordinal) && tools.Contains("is not <mod>\\\\Assets\\\\Prefabs"));
            Check("L4: FullPrefabsFolder is in the central registry (self_test and the drift test)",
                RequiredMembers.All.Any(m => m.Type == "PummelMod" && m.Name == "FullPrefabsFolder" && m.Kind == "property"));
        }

        // ------------------------------------------------------------------ H1, L1, L2, L3

        private static void ReferenceLocations()
        {
            Console.WriteLine("-- unit: reference scan covers every location (H1, L1, L2)");
            const string g = "11111111-2222-3333-4444-555555555555";
            const string other = "99999999-2222-3333-4444-555555555555";
            JObject Ref(string guid) => new JObject { ["guid"] = guid, ["name"] = "x", ["assetType"] = "AudioClip" };

            // component property
            ReferenceSource comp = new ReferenceSource { Kind = "component", Owner = "Box / Renderer", OwnerGuid = "o1", Data = new JObject { ["materials"] = new JArray(Ref(g)) } };
            // action parameter on a component trigger
            JObject trig = new JObject
            {
                ["triggerType"] = "OnEnter",
                ["actions"] = new JArray(new JObject { ["type"] = "Play Sound", ["parameters"] = new JObject { ["m_clip"] = Ref(g) }, ["targets"] = new JArray() }),
            };
            ReferenceSource action = new ReferenceSource { Kind = "component", Owner = "Zone / Trigger", Data = new JObject { ["OnEnterActions"] = trig } };
            // nested popup choice list
            JObject popup = new JObject
            {
                ["actions"] = new JArray(new JObject
                {
                    ["type"] = "Display Popup",
                    ["parameters"] = new JObject
                    {
                        ["choices"] = new JArray(new JObject(), new JObject { ["onSelect"] = new JObject { ["actions"] = new JArray(new JObject { ["parameters"] = new JObject { ["prefab"] = Ref(g) } }) } }),
                    },
                }),
            };
            ReferenceSource nested = new ReferenceSource { Kind = "component", Owner = "Npc / Logic", Data = new JObject { ["Triggers"] = new JArray(popup) } };
            // the six player-settings triggers + hitTrigger (each its own source, as the game layer reads them)
            List<ReferenceSource> player = new[] { "weaponHitTrigger", "tickTrigger", "onDeathTrigger", "onRespawnTrigger", "onFirstSpawnTrigger", "onRoundEndTrigger", "hitTrigger" }
                .Select(n => new ReferenceSource { Kind = "playerTrigger", Owner = "playerSettings." + n, Data = trig.DeepClone() }).ToList();
            ReferenceSource material = new ReferenceSource { Kind = "material", Owner = "Mat", Data = new JValue(g), FixedWhere = "_MainTex" };
            ReferenceSource music = new ReferenceSource { Kind = "setting", Owner = "minigameSettings.Music", Data = new JValue(g), FixedWhere = "get_mod_settings minigameSettings.Music" };
            ReferenceSource preview = new ReferenceSource { Kind = "setting", Owner = "details.previewImage", Data = new JValue(g), IsPreviewImage = true };
            ReferenceSource spaces = new ReferenceSource { Kind = "setting", Owner = "customSpaces.CustomSpacePrefabs", Data = new JArray(other, g) };
            ReferenceSource item = new ReferenceSource { Kind = "setting", Owner = "itemDetails.prefab", Data = new JValue(g) };
            ReferenceSource workshop = new ReferenceSource { Kind = "workshop", Owner = "workshopItem.previewImage", Data = new JValue(g) };

            foreach (ReferenceSource s in new[] { comp, action, nested, material, music, spaces, item, workshop }.Concat(player))
            {
                ReferenceCollector.Result r = ReferenceCollector.Collect(new[] { s }, g);
                Check($"H1: a reference in {s.Kind} '{s.Owner}' is found (scan complete)", r.Items.Count == 1 && !r.Incomplete && r.Items[0].Owner == s.Owner);
                Check($"H1: {s.Kind} '{s.Owner}' does not report another guid", ReferenceCollector.Collect(new[] { s }, other.Replace('9', '8')).Items.Count == 0);
            }
            Check("H1: a hit inside a component's actions is reported as an action", ReferenceCollector.Collect(new[] { action }, g).Items[0].Kind == "action"
                && ReferenceCollector.Collect(new[] { nested }, g).Items[0].Kind == "action" && ReferenceCollector.Collect(new[] { comp }, g).Items[0].Kind == "component");
            Check("H1: player-settings trigger hits keep their own kind and owner", ReferenceCollector.Collect(player, g).Items.Select(i => i.Kind).Distinct().Single() == "playerTrigger"
                && ReferenceCollector.Collect(player, g).Items.Count == 7);
            Check("H1: the preview image is flagged (never removed)", ReferenceCollector.Collect(new[] { preview }, g).PreviewImage && !ReferenceCollector.Collect(new[] { music }, g).PreviewImage);
            Check("H1: fixed 'where' texts are used for settings and materials", ReferenceCollector.Collect(new[] { material }, g).Items[0].Where == "_MainTex");
            Check("H1: the location list names player-settings triggers, item details and the Workshop item",
                ReferenceCollector.Locations.Any(l => l.Contains("onRoundEndTrigger") && l.Contains("hitTrigger")) && ReferenceCollector.Locations.Any(l => l.Contains("Item mods"))
                && ReferenceCollector.Locations.Any(l => l.Contains("Workshop")));

            // L1: truncated arrays and unreadable places make the scan incomplete
            JObject truncated = new JObject { ["actions"] = new JArray(new JObject(), new JObject { ["truncatedItems"] = 12 }) };
            Check("L1: {truncatedItems} marks the scan incomplete", ReferenceCollector.Collect(new[] { new ReferenceSource { Kind = "component", Owner = "o", Data = truncated } }, g).Incomplete);
            bool inc = false;
            PrefabSourceRules.FindObjectRefs(truncated, string.Empty, "o", new string[0], new List<SubtreeReference>(), ref inc);
            Check("L1: the prefab subtree scan treats {truncatedItems} as incomplete too", inc);
            Check("H1/L3: a place that could not be read marks the scan incomplete", ReferenceCollector.Collect(new[] { new ReferenceSource { Kind = "setting", Owner = "s", ReadFailed = true } }, g).Incomplete);

            // the settings rules type the player triggers as triggers, not strings
            List<ModSettingField> trigFields = SettingsRules.ForModType("Minigame").SelectMany(s => s.Fields).Where(f => CallSites.PlayerSettingTriggers.Contains(f.Name)).ToList();
            Check("H1: the six player-settings triggers are typed Trigger (read-only, type 'trigger') in the settings rules",
                trigFields.Count == 6 && trigFields.All(f => f.Kind == SettingKind.Trigger && SettingsRules.TypeName(f) == "trigger"));

            // the game layer feeds every location through the collector
            string plugin = FindPluginDir();
            if (plugin != null)
            {
                string access = File.ReadAllText(Path.Combine(plugin, "Game", "AssetAccess.cs"));
                Check("H1 scan: FindReferences reads the player triggers, item details and the Workshop item and treats a failed settings read as unremovable",
                    access.Contains("ScanPlayerTriggers(mod, conv, guid, result)") && access.Contains("ScanItemDetails(") && access.Contains("ScanWorkshopItem(")
                    && access.Contains("typeof(ModLogicTrigger).IsAssignableFrom(f.FieldType)") && access.Contains("SettingsScanFailed = true"));
                string tools = File.ReadAllText(Path.Combine(plugin, "Tools", "AssetImportTools.cs"));
                Check("L3: remove_asset refuses even with force when the settings scan failed", tools.Contains("if (refs.SettingsScanFailed)") && tools.IndexOf("if (refs.SettingsScanFailed)", StringComparison.Ordinal) < tools.IndexOf("bool blocked = refs.Items.Count > 0", StringComparison.Ordinal));
            }
        }
    }
}
