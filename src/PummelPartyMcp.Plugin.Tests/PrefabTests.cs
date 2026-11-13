using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using PummelPartyMcp.Core;

namespace PummelPartyMcp.Tests
{
    /// <summary>
    /// Prefab tools (plugin 0.8.0): the .pfab write protection (dry run, verify, restore/remove) on real temp directories, name choice,
    /// reference rules, removal guard, batch refusal, context rules, guard declarations and the source scan of the game-side code.
    /// </summary>
    internal static class PrefabTests
    {
        private static Action<string, bool> s_check;
        private static string s_root;

        public static void Run(Action<string, bool> check)
        {
            s_check = check;
            s_root = Path.Combine(Path.GetTempPath(), "mcp-prefab-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(s_root);
            try
            {
                Format();
                Prepare();
                FinishCases();
                NewPrefab();
                SaveModSimulation();
                Naming();
                ReferencesAndSources();
                RegistryBatchGuardsDocs();
            }
            finally
            {
                try
                {
                    Directory.Delete(s_root, true);
                }
                catch (Exception)
                {
                    // temp
                }
                Check("the test left no temp directory behind", !Directory.Exists(s_root));
            }
        }

        private static void Check(string name, bool ok) => s_check(name, ok);

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

        // ---- fake .pfab files: the real layout of the start of a ModHierarchySerializer file (header, root name, active, layer, tag, child count)

        private static byte[] Pfab(string name, bool active, int extraBytes)
        {
            using (MemoryStream ms = new MemoryStream())
            using (BinaryWriter w = new BinaryWriter(ms))
            {
                w.Write("PMH");
                w.Write((ushort)1);
                w.Write((ushort)0);
                w.Write(name);
                w.Write(active);
                w.Write(0);
                w.Write("Untagged");
                w.Write((ushort)0);
                for (int i = 0; i < extraBytes; i++)
                {
                    w.Write((byte)(i % 251));
                }
                w.Flush();
                return ms.ToArray();
            }
        }

        private static string NewAssets()
        {
            string assets = Path.Combine(s_root, Guid.NewGuid().ToString("N").Substring(0, 8), "MCP_Test", "Assets");
            Directory.CreateDirectory(Path.Combine(assets, "Prefabs"));
            return assets;
        }

        private static string Put(string assets, string name, byte[] pfab, string meta = "{\"guid\":{\"serializedGuid\":\"x\"}}")
        {
            string p = Path.Combine(assets, "Prefabs", name + ".pfab");
            File.WriteAllBytes(p, pfab);
            File.WriteAllText(p + ".pmeta", meta);
            return p;
        }

        private static string[] TempLeftovers(string assets) =>
            Directory.GetFiles(assets, "*" + PrefabWriteGuard.RestoreTempSuffix, SearchOption.AllDirectories);

        private static string Hash(string path) => Convert.ToBase64String(System.Security.Cryptography.SHA256.Create().ComputeHash(File.ReadAllBytes(path)));

        // ------------------------------------------------------------------ format

        private static void Format()
        {
            Console.WriteLine("-- unit: .pfab format checks");
            byte[] good = Pfab("Crate", true, 5);
            Check("a file written like the game's serializer passes the header check", PfabFormat.CheckBytes(good) == null);
            Check("the header constant equals BinaryWriter.Write(\"PMH\") + 1.0", good.Take(8).SequenceEqual(PfabFormat.Header));
            Check("empty, short, wrong-header and header-only files are refused",
                PfabFormat.CheckBytes(new byte[0]) != null && PfabFormat.CheckBytes(new byte[3]) != null
                && PfabFormat.CheckBytes(new byte[] { 3, (byte)'P', (byte)'M', (byte)'X', 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }) != null
                && PfabFormat.CheckBytes(PfabFormat.Header) != null);
            byte[] v2 = (byte[])good.Clone();
            v2[4] = 2;
            Check("another version is refused", PfabFormat.CheckBytes(v2) != null);
            int off = PfabFormat.RootActiveOffset(good);
            Check("the root active flag offset points at the flag", off == 8 + 1 + "Crate".Length && good[off] == 1 && PfabFormat.RootActiveOffset(Pfab("Crate", false, 5)) == off);
            Check("two files differing only in the root active flag compare as the same", PfabFormat.SameApartFromRootActive(good, Pfab("Crate", false, 5)));
            byte[] other = (byte[])good.Clone();
            other[other.Length - 1] ^= 0xFF;
            Check("a different byte elsewhere, or another length, is not the same", !PfabFormat.SameApartFromRootActive(good, other) && !PfabFormat.SameApartFromRootActive(good, Pfab("Crate", true, 6)));
            Check("a long root name (2-byte length prefix) still finds the flag", PfabFormat.RootActiveOffset(Pfab(new string('n', 200), true, 3)) == 8 + 2 + 200);
        }

        // ------------------------------------------------------------------ prepare

        private static void Prepare()
        {
            Console.WriteLine("-- unit: dry run and safety copy");
            string assets = NewAssets();
            byte[] cur = Pfab("A", true, 10);
            string path = Put(assets, "A", cur);
            string before = Hash(path);

            Check("a dry run that throws refuses with INVALID_STATE, names the prefab and touches nothing",
                CodeOf(() => PrefabWriteGuard.Prepare("A", path, assets, () => throw new InvalidOperationException("boom"))) == ErrorCodes.InvalidState
                && Hash(path) == before && TempLeftovers(assets).Length == 0);
            try
            {
                PrefabWriteGuard.Prepare("A", path, assets, () => throw new InvalidOperationException("boom"));
            }
            catch (RpcException e)
            {
                Check("the refusal message says what would happen and names the cause", e.Message.Contains("'A'") && e.Message.Contains("truncated") && e.Message.Contains("boom"));
            }
            Check("a dry run that returns garbage refuses", CodeOf(() => PrefabWriteGuard.Prepare("A", path, assets, () => new byte[] { 1, 2, 3 })) == ErrorCodes.InvalidState);
            PrefabFileJob job = PrefabWriteGuard.Prepare("A", path, assets, () => Pfab("A", true, 20));
            Check("a good dry run keeps the expected bytes and a safety copy of the file and its .pmeta",
                job.Pfab.Existed && job.Pfab.Bytes.SequenceEqual(cur) && job.Meta.Existed && job.Expected.Length == Pfab("A", true, 20).Length && !job.IsNew);
            Check("a path outside the Assets folder, a wrong extension and a folder are refused",
                CodeOf(() => PrefabWriteGuard.Prepare("A", Path.Combine(s_root, "elsewhere", "A.pfab"), assets, () => Pfab("A", true, 1))) == ErrorCodes.InvalidState
                && CodeOf(() => PrefabWriteGuard.Prepare("A", Path.Combine(assets, "Prefabs", "A.png"), assets, () => Pfab("A", true, 1))) == ErrorCodes.InvalidState
                && CodeOf(() => PrefabWriteGuard.Prepare("A", Path.Combine(assets, ".."), assets, () => Pfab("A", true, 1))) == ErrorCodes.InvalidState);
            Directory.CreateDirectory(Path.Combine(assets, "Prefabs", "D.pfab"));
            Check("a prefab path that is a directory is refused", CodeOf(() => PrefabWriteGuard.Prepare("D", Path.Combine(assets, "Prefabs", "D.pfab"), assets, () => Pfab("D", true, 1))) == ErrorCodes.InvalidState);
            Check("a file larger than the safety-copy limit is refused", CodeOf(() => FileSnapshot.Take(path, 4)) == ErrorCodes.InvalidState);
            Check("a prefab in another folder below Assets is protected too (the editor allows it)", PrefabWriteGuard.Prepare("S", Path.Combine(assets, "Props", "S.pfab"), assets, () => Pfab("S", true, 1)) != null);
        }

        // ------------------------------------------------------------------ finish: existing prefabs

        private static void FinishCases()
        {
            Console.WriteLine("-- unit: verify and restore (existing prefab)");
            string assets = NewAssets();
            byte[] oldA = Pfab("A", true, 30);
            byte[] oldB = Pfab("B", true, 40);
            string a = Put(assets, "A", oldA);
            string b = Put(assets, "B", oldB);
            string hashB = Hash(b);
            string hashBMeta = Hash(b + ".pmeta");

            // good write
            byte[] newA = Pfab("A", true, 60);
            PrefabFileJob job = PrefabWriteGuard.Prepare("A", a, assets, () => newA);
            File.WriteAllBytes(a, newA);
            File.WriteAllText(a + ".pmeta", "{\"x\":1}");
            PrefabFileOutcome ok = PrefabWriteGuard.Finish(job);
            Check("a complete write verifies, is identical to the dry run and keeps the new content", ok.Ok && ok.Identical && ok.Length == newA.Length && File.ReadAllBytes(a).SequenceEqual(newA) && !ok.Restored);

            // same length, other content: accepted with a warning
            byte[] same = (byte[])newA.Clone();
            same[same.Length - 2] ^= 0x55;
            PrefabFileJob jobSame = PrefabWriteGuard.Prepare("A", a, assets, () => newA);
            File.WriteAllBytes(a, same);
            PrefabFileOutcome w = PrefabWriteGuard.Finish(jobSame);
            Check("same length but different content is kept with a warning (only truncation is a failure)", w.Ok && !w.Identical && w.Warnings.Count == 1);

            // truncated write (what FileMode.Create + an exception leaves)
            PrefabFileJob jt = PrefabWriteGuard.Prepare("A", a, assets, () => Pfab("A", true, 200));
            byte[] snapshot = File.ReadAllBytes(a);
            File.WriteAllBytes(a, Pfab("A", true, 200).Take(20).ToArray());
            PrefabFileOutcome t = PrefabWriteGuard.Finish(jt);
            Check("a truncated file is detected and the previous content is put back byte for byte",
                !t.Ok && t.Restored && t.Problem.Contains("truncated") && File.ReadAllBytes(a).SequenceEqual(snapshot) && t.RestoreProblem == null);
            Check("no temp file is left behind", TempLeftovers(assets).Length == 0);
            Check("the neighbour prefab is untouched", Hash(b) == hashB && Hash(b + ".pmeta") == hashBMeta);

            // empty file
            PrefabFileJob je = PrefabWriteGuard.Prepare("A", a, assets, () => Pfab("A", true, 300));
            byte[] snapE = File.ReadAllBytes(a);
            File.WriteAllBytes(a, new byte[0]);
            PrefabFileOutcome e0 = PrefabWriteGuard.Finish(je);
            Check("a zero-length file is detected and restored", !e0.Ok && e0.Restored && File.ReadAllBytes(a).SequenceEqual(snapE));

            // header garbage of the right length
            PrefabFileJob jh = PrefabWriteGuard.Prepare("A", a, assets, () => Pfab("A", true, 100));
            byte[] snapH = File.ReadAllBytes(a);
            File.WriteAllBytes(a, new byte[Pfab("A", true, 100).Length]);
            PrefabFileOutcome h = PrefabWriteGuard.Finish(jh);
            Check("a file of the right length with a wrong header is restored", !h.Ok && h.Restored && File.ReadAllBytes(a).SequenceEqual(snapH));

            // deleted file
            PrefabFileJob jd = PrefabWriteGuard.Prepare("A", a, assets, () => Pfab("A", true, 100));
            byte[] snapD = File.ReadAllBytes(a);
            File.Delete(a);
            PrefabFileOutcome d = PrefabWriteGuard.Finish(jd);
            Check("a file that vanished is restored", !d.Ok && d.Restored && File.ReadAllBytes(a).SequenceEqual(snapD));

            // not written at all (the editor call threw before touching the file)
            PrefabFileJob jn = PrefabWriteGuard.Prepare("A", a, assets, () => Pfab("A", true, 500));
            PrefabFileOutcome n = PrefabWriteGuard.Finish(jn);
            Check("an unchanged file whose new state was never written is reported as not written, nothing is 'restored'", !n.Ok && n.NotWritten && !n.Restored && n.Problem.Contains("not rewritten"));

            // .pmeta truncated
            PrefabFileJob jm = PrefabWriteGuard.Prepare("A", a, assets, () => File.ReadAllBytes(a));
            string metaBefore = File.ReadAllText(a + ".pmeta");
            File.WriteAllText(a + ".pmeta", "{\"guid\":{\"seri");
            PrefabFileOutcome m = PrefabWriteGuard.Finish(jm);
            Check("a truncated .pmeta is detected and restored", !m.Ok && m.Restored && File.ReadAllText(a + ".pmeta") == metaBefore && m.Problem.Contains("pmeta"));
            File.Delete(a + ".pmeta");
            PrefabFileJob jm2 = PrefabWriteGuard.Prepare("A", a, assets, () => File.ReadAllBytes(a));
            File.WriteAllBytes(a + ".pmeta", new byte[0]);
            Check("an empty .pmeta is detected", !PrefabWriteGuard.Finish(jm2).Ok);

            // atomic write helper
            string target = Path.Combine(assets, "Prefabs", "T.pfab");
            PrefabWriteGuard.AtomicWrite(target, new byte[] { 1, 2, 3 });
            PrefabWriteGuard.AtomicWrite(target, new byte[] { 4, 5 });
            Check("the atomic writer creates and replaces files and leaves no temp file", File.ReadAllBytes(target).SequenceEqual(new byte[] { 4, 5 }) && TempLeftovers(assets).Length == 0);
            File.WriteAllBytes(target + PrefabWriteGuard.RestoreTempSuffix, new byte[] { 9 });
            PrefabWriteGuard.AtomicWrite(target, new byte[] { 6 });
            Check("a stale temp file of an earlier crash is not overwritten or used", File.ReadAllBytes(target + PrefabWriteGuard.RestoreTempSuffix).SequenceEqual(new byte[] { 9 }) && File.ReadAllBytes(target).SequenceEqual(new byte[] { 6 }));
            File.Delete(target + PrefabWriteGuard.RestoreTempSuffix);
        }

        // ------------------------------------------------------------------ new prefab

        private static void NewPrefab()
        {
            Console.WriteLine("-- unit: verify and remove (new prefab)");
            string assets = NewAssets();
            string neighbour = Put(assets, "Other", Pfab("Other", true, 10));
            string hashN = Hash(neighbour);
            string path = Path.Combine(assets, "Prefabs", "Prefab_0.pfab");

            Check("PrepareNew refuses an existing file, an orphan .pmeta and a path outside Assets\\Prefabs",
                CodeOf(() => PrefabWriteGuard.PrepareNew("Other", neighbour, assets)) == ErrorCodes.AlreadyExists
                && CodeOf(() => { File.WriteAllText(path + ".pmeta", "{}"); try { PrefabWriteGuard.PrepareNew("Prefab_0", path, assets); } finally { File.Delete(path + ".pmeta"); } }) == ErrorCodes.AlreadyExists
                && CodeOf(() => PrefabWriteGuard.PrepareNew("x", Path.Combine(assets, "Props", "x.pfab"), assets)) == ErrorCodes.InvalidState
                && CodeOf(() => PrefabWriteGuard.PrepareNew("x", Path.Combine(s_root, "x.pfab"), assets)) == ErrorCodes.InvalidState);

            // good creation: the editor deactivates the new prefab after saving, so the file has active=true, the in-memory dry run active=false
            PrefabFileJob job = PrefabWriteGuard.PrepareNew("Prefab_0", path, assets);
            File.WriteAllBytes(path, Pfab("Prefab_0", true, 25));
            File.WriteAllText(path + ".pmeta", "{\"a\":1}");
            job.Expected = Pfab("Prefab_0", false, 25);
            PrefabFileOutcome ok = PrefabWriteGuard.Finish(job);
            Check("a new prefab verifies against the later dry run despite the deactivated root", ok.Ok && ok.Identical && File.Exists(path) && File.Exists(path + ".pmeta"));
            File.Delete(path);
            File.Delete(path + ".pmeta");

            // truncated creation
            PrefabFileJob jt = PrefabWriteGuard.PrepareNew("Prefab_0", path, assets);
            File.WriteAllBytes(path, Pfab("Prefab_0", true, 25).Take(14).ToArray());
            File.WriteAllText(path + ".pmeta", "{\"a\":1}");
            jt.Expected = Pfab("Prefab_0", false, 25);
            PrefabFileOutcome t = PrefabWriteGuard.Finish(jt);
            Check("a truncated new prefab is removed with its .pmeta, nothing else", !t.Ok && t.Removed && !File.Exists(path) && !File.Exists(path + ".pmeta") && Hash(neighbour) == hashN && File.Exists(neighbour + ".pmeta"));

            // header-only check without an expectation
            PrefabFileJob jh = PrefabWriteGuard.PrepareNew("Prefab_0", path, assets);
            File.WriteAllBytes(path, new byte[] { 0, 1, 2 });
            File.WriteAllText(path + ".pmeta", "{}");
            Check("without a dry run the header check still removes a bad new file", !PrefabWriteGuard.Finish(jh).Ok && !File.Exists(path));

            // Discard after an editor exception: removes only the files that exist, strict paths
            PrefabFileJob jd = PrefabWriteGuard.PrepareNew("Prefab_0", path, assets);
            File.WriteAllBytes(path, new byte[] { 1 });
            PrefabFileOutcome d = PrefabWriteGuard.Discard(jd, "editor call failed");
            Check("Discard removes the half-written file and leaves the neighbour", d.Removed && !File.Exists(path) && Hash(neighbour) == hashN);
            PrefabFileJob outside = new PrefabFileJob { Name = "x", PfabPath = Path.Combine(s_root, "keep.pfab"), MetaPath = Path.Combine(s_root, "keep.pfab.pmeta"), AssetsFolder = assets, IsNew = true, Pfab = new FileSnapshot(), Meta = new FileSnapshot() };
            File.WriteAllBytes(outside.PfabPath, new byte[] { 7 });
            PrefabFileOutcome o2 = PrefabWriteGuard.Discard(outside, "x");
            Check("Discard never deletes a file outside the Assets folder", File.Exists(outside.PfabPath) && !o2.Removed && o2.RestoreProblem != null);
            File.Delete(outside.PfabPath);
            Check("no temp files", TempLeftovers(assets).Length == 0);
        }

        // ------------------------------------------------------------------ save_mod with several prefabs

        private static void SaveModSimulation()
        {
            Console.WriteLine("-- unit: save_mod protection with several prefabs, one write truncated");
            string assets = NewAssets();
            string[] names = { "P1", "P2", "P3", "P4" };
            Dictionary<string, byte[]> old = new Dictionary<string, byte[]>();
            Dictionary<string, byte[]> next = new Dictionary<string, byte[]>();
            foreach (string n in names)
            {
                old[n] = Pfab(n, true, 20);
                next[n] = Pfab(n, true, 70);
                Put(assets, n, old[n]);
            }
            List<PrefabFileJob> jobs = names.Select(n => PrefabWriteGuard.Prepare(n, Path.Combine(assets, "Prefabs", n + ".pfab"), assets, () => next[n])).ToList();

            // "SaveCurrentMod": P1 ok, P2 truncated mid-write, P3 never reached (the exception aborted the loop), P4 ok
            File.WriteAllBytes(jobs[0].PfabPath, next["P1"]);
            File.WriteAllBytes(jobs[1].PfabPath, next["P2"].Take(11).ToArray());
            File.WriteAllBytes(jobs[3].PfabPath, next["P4"]);
            List<PrefabFileOutcome> outcomes = jobs.Select(PrefabWriteGuard.Finish).ToList();
            List<PrefabFileOutcome> failed = outcomes.Where(o => !o.Ok).ToList();
            Check("exactly the truncated prefab and the one never written fail", failed.Select(f => f.Name).OrderBy(x => x).SequenceEqual(new[] { "P2", "P3" }));
            Check("the truncated one is restored byte for byte, the unreached one is untouched", File.ReadAllBytes(jobs[1].PfabPath).SequenceEqual(old["P2"]) && File.ReadAllBytes(jobs[2].PfabPath).SequenceEqual(old["P3"]) && outcomes[1].Restored && outcomes[2].NotWritten);
            Check("the prefabs that were written correctly keep their new content", File.ReadAllBytes(jobs[0].PfabPath).SequenceEqual(next["P1"]) && File.ReadAllBytes(jobs[3].PfabPath).SequenceEqual(next["P4"]) && outcomes[0].Ok && outcomes[3].Ok);
            string message = PrefabWriteGuard.Describe(failed);
            Check("the error text names every failed prefab and says what happened", message.Contains("'P2'") && message.Contains("restored") && message.Contains("'P3'"));
            Check("every file of the mod parses as a complete header afterwards", names.All(n => PfabFormat.CheckBytes(File.ReadAllBytes(Path.Combine(assets, "Prefabs", n + ".pfab"))) == null));
            Check("no temp files and the outcome JSON carries the problem", TempLeftovers(assets).Length == 0 && failed[0].ToJson()["problem"] != null && (bool)failed[0].ToJson()["ok"] == false);

            // a prefab that cannot be serialised refuses before anything is written
            string hash1 = Hash(jobs[0].PfabPath);
            Check("one prefab failing its dry run refuses the whole save before any file is touched",
                CodeOf(() => { foreach (string n in names) { string nn = n; PrefabWriteGuard.Prepare(nn, Path.Combine(assets, "Prefabs", nn + ".pfab"), assets, () => { if (nn == "P3") throw new Exception("component throws"); return next[nn]; }); } }) == ErrorCodes.InvalidState
                && Hash(jobs[0].PfabPath) == hash1);
        }

        // ------------------------------------------------------------------ naming

        private static void Naming()
        {
            Console.WriteLine("-- unit: prefab names");
            string folder = Path.Combine(NewAssets(), "Prefabs");
            Func<string, bool> free = n => AssetImportRules.IsFree(Path.Combine(folder, n + ".pfab"));
            PrefabNamePlan p0 = PrefabNaming.Plan(null, "Prefab_0", new string[0], free);
            Check("no request: the editor's name is used as is", p0.Name == "Prefab_0" && !p0.NeedsRename && !p0.Adjusted);
            PrefabNamePlan p1 = PrefabNaming.Plan("Crate", "Prefab_0", new string[0], free);
            Check("a requested name needs a rename from the editor's name and is not 'adjusted'", p1.Name == "Crate" && p1.NeedsRename && !p1.Adjusted && p1.EditorName == "Prefab_0");
            PrefabNamePlan p2 = PrefabNaming.Plan("Crate", "Prefab_0", new[] { "Crate", "crate_1" }, free);
            Check("a requested name that exists as an asset (case-insensitively) gets the next free suffix", p2.Name == "Crate_2" && p2.Adjusted);
            File.WriteAllBytes(Path.Combine(folder, "Wall.pfab"), new byte[] { 1 });
            PrefabNamePlan p3 = PrefabNaming.Plan("wall", "Prefab_0", new string[0], free);
            Check("a requested name that exists as a FILE (NTFS is case-insensitive) is not reused", p3.Name == "wall_1" && p3.Adjusted);
            File.WriteAllBytes(Path.Combine(folder, "Pm.pfab.pmeta"), new byte[] { 1 });
            Check("an orphan .pmeta blocks a name too", PrefabNaming.Plan("Pm", "Prefab_0", new string[0], free).Name == "Pm_1");
            Check("the editor's name taken on disk by an orphan file refuses (the editor would overwrite it)", CodeOf(() => PrefabNaming.Plan(null, "Wall", new string[0], free)) == ErrorCodes.AlreadyExists
                && CodeOf(() => PrefabNaming.Plan("Other", "WALL", new string[0], free)) == ErrorCodes.AlreadyExists);
            Check("an editor name with characters the plugin does not allow refuses (rename the source object)", CodeOf(() => PrefabNaming.Plan(null, "Bad/Name", new string[0], free)) == ErrorCodes.InvalidParams
                && CodeOf(() => PrefabNaming.Plan(null, "Cube:1", new string[0], free)) == ErrorCodes.InvalidParams && CodeOf(() => PrefabNaming.Plan(null, "ends.", new string[0], free)) == ErrorCodes.InvalidParams
                && CodeOf(() => PrefabNaming.Plan(null, "CON", new string[0], free)) == ErrorCodes.InvalidParams);
            string[] taken = new[] { "Q" }.Concat(Enumerable.Range(1, PrefabNaming.MaxSuffix).Select(i => "Q_" + i)).ToArray();
            Check("all suffixes taken refuses instead of overwriting", CodeOf(() => PrefabNaming.Plan("Q", "Prefab_0", taken, free)) == ErrorCodes.AlreadyExists);
            Check("a source object named like an existing prefab asset gets the editor's _1 name and passes", PrefabNaming.Plan(null, "Cube_1", new[] { "Cube" }, free).Name == "Cube_1");
            Check("the editor name is never changed by a request equal to it", !PrefabNaming.Plan("Prefab_0", "Prefab_0", new string[0], free).NeedsRename);
        }

        // ------------------------------------------------------------------ references and sources

        private static void ReferencesAndSources()
        {
            Console.WriteLine("-- unit: source subtree rules, reference scan, removal guard");
            Check("a subtree with a prefab instance or a board space is refused (the editor's own rule)", PrefabSourceRules.Check(1, 0) != null && PrefabSourceRules.Check(0, 2) != null && PrefabSourceRules.Check(0, 0) == null);
            JObject props = JObject.Parse(@"{
              ""Triggers"": { ""value"": [ { ""actions"": [ { ""targets"": [ { ""guid"": null, ""objectGuid"": ""in-1"", ""objectName"": ""Door"" }, { ""guid"": null, ""objectGuid"": ""out-9"" } ], ""parameters"": { ""m_x"": 1 } } ] } ] },
              ""Mat"": { ""value"": { ""guid"": ""asset-1"", ""name"": ""Red"" } } }");
            List<SubtreeReference> found = new List<SubtreeReference>();
            bool incomplete = false;
            PrefabSourceRules.FindObjectRefs(props, string.Empty, "Door / Logic", new HashSet<string> { "in-1" }, found, ref incomplete);
            Check("action targets pointing at objects are found, inside and outside told apart, plain asset references are not",
                found.Count == 2 && found[0].PointsInside && !found[1].PointsInside && found[0].Where.Contains("targets[0]") && !incomplete);
            string refusal = PrefabSourceRules.RefusalForReferences(found);
            Check("the refusal explains the silent breakage and what to do", refusal != null && refusal.Contains("2 object/action reference(s)") && refusal.Contains("1 to objects inside") && refusal.Contains("empty prefab"));
            Check("no references -> no refusal", PrefabSourceRules.RefusalForReferences(new List<SubtreeReference>()) == null && PrefabSourceRules.RefusalForReferences(null) == null);
            bool inc2 = false;
            PrefabSourceRules.FindObjectRefs(JObject.Parse(@"{ ""a"": { ""unavailable"": ""x"" } }"), string.Empty, "o", null, new List<SubtreeReference>(), ref inc2);
            Check("unreadable parts mark the scan incomplete (reported as a warning)", inc2);

            // reference scan for a PREFAB asset on fake component JSON: instance, spawner, item, Spawn Prefab action, set-player-visual
            string guid = "11111111-2222-3333-4444-555555555555";
            JObject instance = JObject.Parse($"{{ \"PrefabReference\": {{ \"value\": {{ \"guid\": \"{guid}\", \"name\": \"Crate\", \"assetType\": \"Prefab\" }} }} }}");
            JObject spawner = JObject.Parse($"{{ \"Prefabs\": {{ \"value\": [ {{ \"guid\": \"{guid}\", \"name\": \"Crate\" }} ] }} }}");
            JObject item = JObject.Parse($"{{ \"ItemPrefab\": {{ \"value\": {{ \"guid\": \"{guid}\" }} }} }}");
            JObject action = JObject.Parse($"{{ \"Triggers\": {{ \"value\": [ {{ \"actions\": [ {{ \"className\": \"SpawnPrefabAction\", \"parameters\": {{ \"Prefabs\": [ {{ \"guid\": \"{guid}\" }} ] }} }}, {{ \"className\": \"SetPlayerVisualAction\", \"parameters\": {{ \"Prefab\": {{ \"guid\": \"{guid}\" }} }} }} ] }} ] }} }}");
            JObject instanceBoth = JObject.Parse($"{{ \"PrefabReference\": {{ \"value\": {{ \"guid\": \"{guid}\", \"name\": \"Crate\" }} }}, \"PrefabGUID\": {{ \"value\": \"{guid}\" }} }}");
            ReferenceCollector.Result both = ReferenceCollector.Collect(new[] { new ReferenceSource { Kind = "component", Owner = "Crate1 / Prefab Instance", OwnerGuid = "o1", Data = instanceBoth } }, guid);
            Check("a Prefab Instance is one reference (PrefabReference and PrefabGUID are the same)", both.Items.Count == 1 && both.Items[0].Where.StartsWith("PrefabReference"));
            ReferenceCollector.Result onlyGuid = ReferenceCollector.Collect(new[] { new ReferenceSource { Kind = "component", Owner = "x", Data = JObject.Parse($"{{ \"PrefabGUID\": {{ \"value\": \"{guid}\" }} }}") } }, guid);
            Check("a Prefab Instance whose asset did not resolve is still found by its guid", onlyGuid.Items.Count == 1);
            ReferenceCollector.Result twoInst = ReferenceCollector.Collect(new[]
            {
                new ReferenceSource { Kind = "component", Owner = "A", Data = instanceBoth }, new ReferenceSource { Kind = "component", Owner = "B", Data = instanceBoth },
            }, guid);
            Check("two instances count as two references", twoInst.Items.Count == 2);
            string g2 = "99999999-2222-3333-4444-555555555555";
            JObject spawnerBoth = JObject.Parse($"{{ \"Prefabs\": {{ \"value\": [ {{ \"guid\": \"{guid}\" }}, {{ \"guid\": \"{g2}\" }}, {{ \"guid\": \"{guid}\" }} ] }}, \"PrefabGUIDs\": {{ \"value\": [ \"{guid}\", \"{g2}\", \"{guid}\" ] }} }}");
            Check("a spawner's prefab list shows each entry twice (Prefabs and PrefabGUIDs): counted once per entry",
                ReferenceCollector.Collect(new[] { new ReferenceSource { Kind = "component", Owner = "S", Data = spawnerBoth } }, guid).Items.Count == 2
                && ReferenceCollector.Collect(new[] { new ReferenceSource { Kind = "component", Owner = "S", Data = spawnerBoth } }, g2).Items.Count == 1);
            JObject spawnerGuidOnly = JObject.Parse($"{{ \"Prefabs\": {{ \"value\": [ {{ \"guid\": \"{g2}\", \"missing\": true }} ] }}, \"PrefabGUIDs\": {{ \"value\": [ \"{guid}\" ] }} }}");
            Check("a spawner entry that only the guid side holds is still found", ReferenceCollector.Collect(new[] { new ReferenceSource { Kind = "component", Owner = "S", Data = spawnerGuidOnly } }, guid).Items.Count == 1);
            JObject itemBoth = JObject.Parse($"{{ \"ItemPrefab\": {{ \"value\": {{ \"guid\": \"{guid}\" }} }}, \"ItemPrefabGUID\": {{ \"value\": \"{guid}\" }}, \"DroppedItemPrefab\": {{ \"value\": {{ \"guid\": \"{guid}\" }} }}, \"DroppedItemPrefabGUID\": {{ \"value\": \"{guid}\" }} }}");
            ReferenceCollector.Result itemRes = ReferenceCollector.Collect(new[] { new ReferenceSource { Kind = "component", Owner = "I", Data = itemBoth } }, guid);
            Check("an item's prefab and dropped prefab count once each", itemRes.Items.Count == 2 && itemRes.Items.Any(i => i.Where.StartsWith("ItemPrefab.")) && itemRes.Items.Any(i => i.Where.StartsWith("DroppedItemPrefab.")));
            foreach ((string what, JObject j, int expected) in new[] { ("a prefab instance", instance, 1), ("a spawner", spawner, 1), ("an item", item, 1), ("Spawn Prefab and Set Player Visual actions", action, 2) })
            {
                List<string> hits = new List<string>();
                bool inc = false;
                ReferenceScan.FindGuid(j, guid, string.Empty, hits, ref inc);
                Check($"the reference scan finds {what} that uses the prefab", hits.Count == expected && !inc);
            }
            List<string> none = new List<string>();
            bool incN = false;
            ReferenceScan.FindGuid(spawner, "99999999-2222-3333-4444-555555555555", string.Empty, none, ref incN);
            Check("another prefab's guid is not found", none.Count == 0);
        }

        // ------------------------------------------------------------------ registry, batch, guards, docs, source scan

        private static string FindUp(string relative)
        {
            for (DirectoryInfo d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            {
                string p = Path.Combine(d.FullName, relative);
                if (File.Exists(p) || Directory.Exists(p))
                {
                    return p;
                }
            }
            return null;
        }

        private static readonly string[] PrefabTools = { "create_prefab", "open_prefab", "close_prefab" };

        private sealed class NullHost : IEditorGuardHost
        {
            public EditorSnapshot Capture() => null;

            public void EnforceDefaultWorkshopId()
            {
            }
        }

        private static void RegistryBatchGuardsDocs()
        {
            Console.WriteLine("-- unit: prefab registry, batch, guards, context rules, docs, source scan");
            string plugin = FindUp(Path.Combine("src", "PummelPartyMcp.Plugin"));
            Check("plugin sources located", plugin != null);
            if (plugin == null)
            {
                return;
            }
            string tools = File.ReadAllText(Path.Combine(plugin, "Tools", "PrefabTools.cs"));
            foreach (string tool in PrefabTools)
            {
                string why = BatchRules.Refusal(tool, true);
                Check($"{tool} is refused inside run_batch with its own reason", why != null && why.Contains("outside the batch"));
                Check($"{tool} is registered as a write tool behind WriteGuard.OpenMod()", tools.Contains($"ToolDefinition.Write(\"{tool}\", WriteGuard.OpenMod()") && !tools.Contains($"ToolDefinition.Read(\"{tool}\""));
            }
            Check("edits inside an open prefab stay batchable like any other edit", BatchRules.Refusal("create_object", true) == null && BatchRules.Refusal("set_property", true) == null && BatchRules.Refusal("undo", true) == null);

            WriteSafety safety = new WriteSafety(() => true, () => true, () => GameVersionGate.Evaluate("1.14.1k", GameVersionGate.TestedVersions, string.Empty, false), new NullHost());
            EditorSnapshot real = new EditorSnapshot { EditorOpen = true, ModOpen = true, ModLoaded = true, ModName = "My Real Mod", FolderName = "My Real Mod", IsEditing = true, StateName = "Editing" };
            EditorSnapshot sandbox = new EditorSnapshot { EditorOpen = true, ModOpen = true, ModLoaded = true, ModName = "MCP_Prefabs", FolderName = "MCP_Prefabs", IsEditing = true, StateName = "Editing" };
            EditorSnapshot playing = new EditorSnapshot { EditorOpen = true, ModOpen = true, ModLoaded = true, ModName = "MCP_Prefabs", FolderName = "MCP_Prefabs", IsEditing = false, StateName = "Playing" };
            Check("the OpenMod guard refuses a real mod for the prefab tools, accepts the sandbox mod, refuses during a playtest",
                WriteGuardEvaluator.Evaluate(safety, WriteGuard.OpenMod(), null, real)?.Code == ErrorCodes.GuardNotTestMod
                && WriteGuardEvaluator.Evaluate(safety, WriteGuard.OpenMod(), null, sandbox) == null
                && WriteGuardEvaluator.Evaluate(safety, WriteGuard.OpenMod(), null, playing)?.Code == ErrorCodes.InvalidState);
            ToolRegistry reg = new ToolRegistry(safety);
            bool ran = false;
            reg.Register(ToolDefinition.Write("create_prefab_probe", WriteGuard.OpenMod(), null, inv => { ran = true; inv.Complete(1); }));
            Check("a write tool declared like the prefab tools is registered as a guarded write tool", reg.TryGet("create_prefab_probe", out RegisteredTool rt) && rt.Definition.IsWrite && rt.Definition.Guard.Kind == WriteGuardKind.OpenMod && !ran);

            // context state rules, checked against the sources of the tools
            string assetTools = File.ReadAllText(Path.Combine(plugin, "Tools", "AssetImportTools.cs"));
            string modTools = File.ReadAllText(Path.Combine(plugin, "Tools", "ModTools.cs"));
            string playTools = File.ReadAllText(Path.Combine(plugin, "Tools", "PlaytestTools.cs"));
            Check("the context table lists exactly the tools that refuse while a prefab is open", PrefabContextRules.RefusedWhilePrefabOpen.OrderBy(x => x)
                .SequenceEqual(new[] { "create_prefab", "create_test_mod", "import_asset", "open_prefab", "open_test_mod", "remove_asset", "start_playtest" }));
            Check("create_prefab and open_prefab refuse while a prefab is open, with the table's message", tools.Contains("PrefabContextRules.Message(\"create_prefab\"") && tools.Contains("PrefabContextRules.Message(\"open_prefab\"")
                && PrefabContextRules.Message("open_prefab", "P").Contains("close_prefab") && PrefabContextRules.Message("create_prefab", "P").Contains("close_prefab"));
            Check("import_asset, set_import_settings and remove_asset require no open prefab; set_material and create_material do not",
                assetTools.Split(new[] { "RequireReady(noPrefab: true)" }, StringSplitOptions.None).Length == 4 && assetTools.Split(new[] { "RequireReady(noPrefab: false)" }, StringSplitOptions.None).Length == 3);
            Check("the lifecycle tools and the playtest tools still refuse while a prefab is open", modTools.Contains("ed.CurrentPrefab != null") && playTools.Contains("ed.CurrentPrefab != null") && playTools.Contains("Call close_prefab first"));
            Check("close_prefab refuses when no prefab is open", tools.Contains("no prefab is being edited") && PrefabContextRules.OnlyWhilePrefabOpen.Contains("close_prefab"));
            string objectTools = File.ReadAllText(Path.Combine(plugin, "Core", "EditRules.cs"));
            Check("create_object kind prefabInstance is disabled inside a prefab (NotInPrefab)", objectTools.Contains("Kind = \"prefabInstance\"") && objectTools.Contains("NotInPrefab = true") && objectTools.Contains("is disabled while a prefab is being edited"));

            // the write protection is wired into every path that makes the editor write a .pfab
            Check("save_mod prepares every prefab before its commit point and verifies after the save", modTools.IndexOf("PrefabAccess.Prepare(", StringComparison.Ordinal) > 0
                && modTools.IndexOf("PrefabAccess.Prepare(", StringComparison.Ordinal) < modTools.IndexOf("inv.Commit(SaveBudget)", StringComparison.Ordinal)
                && modTools.IndexOf("inv.Commit(SaveBudget)", StringComparison.Ordinal) < modTools.IndexOf("ed.SaveCurrentMod()", StringComparison.Ordinal)
                && modTools.IndexOf("ed.SaveCurrentMod()", StringComparison.Ordinal) < modTools.IndexOf("PrefabWriteGuard.Finish", StringComparison.Ordinal));
            int close = tools.IndexOf("private static void Close(", StringComparison.Ordinal);
            string closeBody = tools.Substring(close);
            Check("close_prefab dry-runs before its commit, forces the instance refresh before leaving, verifies after, and never reimports",
                closeBody.IndexOf("PrefabAccess.Prepare(", StringComparison.Ordinal) < closeBody.IndexOf("inv.Commit(", StringComparison.Ordinal)
                && closeBody.IndexOf("ForceUpdate(", StringComparison.Ordinal) < closeBody.IndexOf("CurrentPrefab = null", StringComparison.Ordinal)
                && closeBody.IndexOf("CurrentPrefab = null", StringComparison.Ordinal) < closeBody.IndexOf("PrefabWriteGuard.Finish", StringComparison.Ordinal)
                && !tools.Contains("ReimportAsset("));
            Check("create_prefab verifies the files after CreateNewPrefab and removes what it created on failure", tools.IndexOf("CreateNewPrefab(", StringComparison.Ordinal) < tools.IndexOf("PrefabWriteGuard.Finish", StringComparison.Ordinal)
                && tools.Contains("PrefabWriteGuard.Discard") && tools.Contains("PrepareNew("));
            Check("the prefab tools and game helpers have no direct file deletes, moves or writes (only the guarded core does)",
                new[] { Path.Combine(plugin, "Tools", "PrefabTools.cs"), Path.Combine(plugin, "Game", "PrefabAccess.cs") }
                    .All(f => { string t = File.ReadAllText(f); return !t.Contains("File.Delete") && !t.Contains("File.Move") && !t.Contains("File.Copy") && !t.Contains("File.WriteAll") && !t.Contains("FileMode.Create") && !t.Contains("Directory.Delete"); }));
            string core = File.ReadAllText(Path.Combine(plugin, "Core", "PrefabCore.cs"));
            Check("the core writes only through the temp-and-replace helper (CreateNew + Replace/Move) and deletes only inside Repair/AtomicWrite",
                core.Contains("FileMode.CreateNew") && !core.Contains("FileMode.Create,") && !core.Contains("File.WriteAllBytes") && !core.Contains("Directory.Delete") && core.Contains("RemovablePath"));
            Check("the capability \"prefabs\" is reported", File.ReadAllText(Path.Combine(plugin, "Tools", "StatusTools.cs")).Contains("\"prefabs\""));

            // registry
            IReadOnlyList<RequiredMember> all = RequiredMembers.All;
            string[] needed = { "CreateNewPrefab", "CurrentPrefab", "m_prefabsRoot", "DeselectAllObjectsAndGizmos", "ForceUpdate", "PrefabReference", "SaveTransform", "Identifier", "MajorVersion", "MinorVersion", "HideAndDontSave", "targetPrefab", "isEnabled" };
            Check("the registry names every game member the prefab tools call by name", needed.All(n => all.Any(m => m.Name == n)));

            // docs
            string docs = FindUp(Path.Combine("docs", "plugin-api.md"));
            string text = docs == null ? string.Empty : File.ReadAllText(docs);
            Check("docs/plugin-api.md describes every prefab tool, the capability and the write protection", new[] { "create_prefab", "open_prefab", "close_prefab", "\"prefabs\"", "truncated", "dry run" }.All(text.Contains));
        }
    }
}
