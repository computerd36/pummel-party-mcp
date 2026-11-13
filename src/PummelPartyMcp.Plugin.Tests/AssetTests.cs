using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json.Linq;
using PummelPartyMcp.Core;

namespace PummelPartyMcp.Tests
{
    /// <summary>
    /// Asset import / removal / materials (plugin 0.8.0): destination path, name rules, source checks, content sniffing, copy and rollback on
    /// real temp directories, removal guard, reference scan, rule engines, docs, registry, batch and guard declarations.
    /// </summary>
    internal static class AssetTests
    {
        private static Action<string, bool> s_check;
        private static string s_root;

        public static void Run(Action<string, bool> check)
        {
            s_check = check;
            s_root = Path.Combine(Path.GetTempPath(), "mcp-asset-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(s_root);
            try
            {
                Names();
                DestinationPath();
                SourceText();
                Sniffing();
                SourceChecks();
                FreeNames();
                CopyAndRollback();
                Limits();
                RemovalRules();
                References();
                ImportSettings();
                Materials();
                Listing();
                UsingImportedAssets();
                DocsRegistryBatchGuards();
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
        }

        private static string MessageOf(Action a)
        {
            try
            {
                a();
                return null;
            }
            catch (RpcException e)
            {
                return e.Message;
            }
        }

        // ------------------------------------------------------------------ samples

        private static byte[] Png(int w, int h, int pad = 0)
        {
            List<byte> b = new List<byte> { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R' };
            b.AddRange(new[] { (byte)(w >> 24), (byte)(w >> 16), (byte)(w >> 8), (byte)w, (byte)(h >> 24), (byte)(h >> 16), (byte)(h >> 8), (byte)h });
            b.AddRange(new byte[] { 8, 6, 0, 0, 0, 0, 0, 0, 0 });
            b.AddRange(new byte[pad]);
            return b.ToArray();
        }

        private static byte[] Jpeg(int w, int h)
        {
            List<byte> b = new List<byte> { 0xFF, 0xD8, 0xFF, 0xE0, 0, 16, (byte)'J', (byte)'F', (byte)'I', (byte)'F', 0, 1, 1, 0, 0, 1, 0, 1, 0, 0 };
            b.AddRange(new byte[] { 0xFF, 0xC0, 0, 17, 8, (byte)(h >> 8), (byte)h, (byte)(w >> 8), (byte)w, 3, 1, 0x22, 0, 2, 0x11, 1, 3, 0x11, 1 });
            b.AddRange(new byte[] { 0xFF, 0xD9 });
            return b.ToArray();
        }

        /// <summary>A minimal valid PCM WAV (fmt: 8 kHz mono 8-bit, byte rate 8000; data: 800 bytes = 0.1 s).</summary>
        private static byte[] Wav()
        {
            using (MemoryStream ms = new MemoryStream())
            using (BinaryWriter w = new BinaryWriter(ms))
            {
                w.Write(Encoding.ASCII.GetBytes("RIFF")); w.Write(36 + 800); w.Write(Encoding.ASCII.GetBytes("WAVE"));
                w.Write(Encoding.ASCII.GetBytes("fmt ")); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(8000); w.Write(8000); w.Write((short)1); w.Write((short)8);
                w.Write(Encoding.ASCII.GetBytes("data")); w.Write(800); w.Write(new byte[800]);
                w.Flush();
                return ms.ToArray();
            }
        }

        private static byte[] Ogg() => Encoding.ASCII.GetBytes("OggS").Concat(new byte[64]).ToArray();

        private static byte[] Mp3Id3() => Encoding.ASCII.GetBytes("ID3").Concat(new byte[64]).ToArray();

        private static byte[] Mp3Sync() => new byte[] { 0xFF, 0xFB, 0x90, 0x00, 0, 0, 0, 0 };

        private static byte[] Obj() => Encoding.ASCII.GetBytes("# cube\nv 0 0 0\nv 1 0 0\nv 0 1 0\nf 1 2 3\n");

        private static string Write(string dir, string name, byte[] bytes)
        {
            Directory.CreateDirectory(dir);
            string p = Path.Combine(dir, name);
            File.WriteAllBytes(p, bytes);
            return p;
        }

        private static string Hash(string p)
        {
            using (SHA256 sha = SHA256.Create())
            using (FileStream fs = File.OpenRead(p))
            {
                return BitConverter.ToString(sha.ComputeHash(fs));
            }
        }

        private static string NewDir(string name)
        {
            string d = Path.Combine(s_root, name + "-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            Directory.CreateDirectory(d);
            return d;
        }

        /// <summary>A fake sandbox mod folder: root\Assets, folder name MCP_Test.</summary>
        private static string FakeAssets(out string modRoot)
        {
            modRoot = Path.Combine(NewDir("mods"), "MCP_Test");
            string assets = Path.Combine(modRoot, "Assets");
            Directory.CreateDirectory(assets);
            return AssetImportRules.ValidateAssetsFolder(modRoot, assets);
        }

        // ------------------------------------------------------------------ names

        private static void Names()
        {
            Console.WriteLine("-- unit: asset names");
            string[] ok = { "rock", "Rock 2", "a_b-c", "a.b", "x (1)", "A1", new string('a', AssetImportRules.MaxStemLength) };
            foreach (string n in ok)
            {
                Check($"valid name '{n}'", AssetImportRules.ValidateName(n) == null);
            }
            string[] bad =
            {
                null, "", " ", "a/b", "a\\b", "..", "a..b", ".", "a.", ".a", " a", "a ", "C:x", "C:", "CON", "con", "NUL", "Com1", "LPT9", "aux.txt", "nul.png", "CONIN$",
                "a\tb", "a\nb", "a\0b", "é", "ü", "a:b", "a*b", "a?b", "a\"b", "a<b", "a|b", "a;b", "a,b", "a&b", "a%b", "a\u200Bb", new string('a', AssetImportRules.MaxStemLength + 1),
                "../x", "..\\x", "/x", "\\x", "x/", "a\u00A0b",
            };
            foreach (string n in bad)
            {
                string shown = n == null ? "<null>" : n.Replace("\0", "\\0").Replace("\t", "\\t").Replace("\n", "\\n");
                Check($"invalid name '{shown}'", AssetImportRules.ValidateName(n) != null);
            }
            Check("DeriveStem keeps good names", AssetImportRules.DeriveStem("My Rock (2)") == "My Rock (2)");
            Check("DeriveStem replaces bad characters", AssetImportRules.DeriveStem("Stein é#1") == "Stein __1");
            Check("DeriveStem never returns a reserved or empty name", AssetImportRules.DeriveStem("CON") == "asset" && AssetImportRules.DeriveStem("") == "asset" && AssetImportRules.DeriveStem(" . ") == "asset");
            Check("DeriveStem output always passes ValidateName", new[] { "a..b", "é", "x\\y", new string('z', 200), " . ", "NUL", "名前" }.All(s => AssetImportRules.ValidateName(AssetImportRules.DeriveStem(s)) == null));
            Check("reserved device names", new[] { "CON", "prn", "Aux", "NUL", "COM1", "com9", "LPT1", "con.txt", "NUL .x" }.All(AssetImportRules.IsReservedDeviceName) && !AssetImportRules.IsReservedDeviceName("console"));
        }

        // ------------------------------------------------------------------ destination

        private static void DestinationPath()
        {
            Console.WriteLine("-- unit: destination path");
            string assets = FakeAssets(out string modRoot);
            string tex = AssetImportRules.BuildDestinationPath(assets, AssetImportKind.Texture, "rock", ".png");
            string mesh = AssetImportRules.BuildDestinationPath(assets, AssetImportKind.Mesh, "rock", ".obj");
            string snd = AssetImportRules.BuildDestinationPath(assets, AssetImportKind.Audio, "boom", ".ogg");
            Check("texture goes to Assets\\Textures", tex == Path.Combine(assets, "Textures", "rock.png"));
            Check("mesh goes to Assets\\Props", mesh == Path.Combine(assets, "Props", "rock.obj"));
            Check("audio goes to Assets\\Audio", snd == Path.Combine(assets, "Audio", "boom.ogg"));
            Check("destinations are strictly inside the Assets folder", new[] { tex, mesh, snd }.All(p => AssetImportRules.IsStrictlyUnder(assets, p)));
            Check("traversal names are refused", new[] { "..", "../x", "..\\..\\x", "a/../b", "C:\\x", "\\\\server\\share\\x" }.All(n => CodeOf(() => AssetImportRules.BuildDestinationPath(assets, AssetImportKind.Texture, n, ".png")) == ErrorCodes.InvalidParams));
            Check("reserved names are refused", new[] { "CON", "nul", "COM3", "lpt1" }.All(n => CodeOf(() => AssetImportRules.BuildDestinationPath(assets, AssetImportKind.Texture, n, ".png")) == ErrorCodes.InvalidParams));
            Check("a long name is refused", CodeOf(() => AssetImportRules.BuildDestinationPath(assets, AssetImportKind.Texture, new string('a', 65), ".png")) == ErrorCodes.InvalidParams);
            Check("a disallowed extension is refused", new[] { ".bmp", ".pmat", ".pfab", ".pgf", ".txt", ".exe", "", ".png.exe", ".PNG" }.All(e => CodeOf(() => AssetImportRules.BuildDestinationPath(assets, AssetImportKind.Texture, "x", e)) == ErrorCodes.InvalidParams));
            Check("an extension of another type is refused", CodeOf(() => AssetImportRules.BuildDestinationPath(assets, AssetImportKind.Texture, "x", ".obj")) == ErrorCodes.InvalidParams);
            Check("the extension is always lower case in the destination", Path.GetExtension(tex) == ".png");
            Check("IsStrictlyUnder: the folder itself, siblings and traversal are not under it",
                !AssetImportRules.IsStrictlyUnder(assets, assets) && !AssetImportRules.IsStrictlyUnder(assets, assets + "2\\x.png")
                && !AssetImportRules.IsStrictlyUnder(assets, Path.Combine(assets, "..", "Data", "x.png")) && AssetImportRules.IsStrictlyUnder(assets, Path.Combine(assets, "Textures", "..", "Textures", "a.png"))
                && !AssetImportRules.IsStrictlyUnder(assets, Path.Combine(assets, "..", "Assets2", "x.png")));
            Check("IsStrictlyUnder is case-insensitive like the file system", AssetImportRules.IsStrictlyUnder(assets.ToUpperInvariant(), tex));

            // the mod root is re-checked against the sandbox rule
            Check("a real mod folder is refused", CodeOf(() => AssetImportRules.ValidateAssetsFolder(Path.Combine(s_root, "My Real Mod"), Path.Combine(s_root, "My Real Mod", "Assets"))) == ErrorCodes.GuardNotTestMod);
            Check("Assets folder must be <root>\\Assets", CodeOf(() => AssetImportRules.ValidateAssetsFolder(modRoot, Path.Combine(modRoot, "Other"))) == ErrorCodes.Internal
                && CodeOf(() => AssetImportRules.ValidateAssetsFolder(modRoot, Path.Combine(modRoot, "Assets", "Textures"))) == ErrorCodes.Internal
                && CodeOf(() => AssetImportRules.ValidateAssetsFolder(modRoot, Path.Combine(s_root, "Assets"))) == ErrorCodes.Internal);
            Check("an empty mod folder is an internal error", CodeOf(() => AssetImportRules.ValidateAssetsFolder("", "")) == ErrorCodes.Internal);
            Check("a relative mod folder is refused", CodeOf(() => AssetImportRules.ValidateAssetsFolder("MCP_x", "MCP_x\\Assets")) != null);
            Check("the old PB_TEST_ prefix is not a sandbox folder", CodeOf(() => AssetImportRules.ValidateAssetsFolder(Path.Combine(s_root, "PB_TEST_x"), Path.Combine(s_root, "PB_TEST_x", "Assets"))) == ErrorCodes.GuardNotTestMod);
        }

        // ------------------------------------------------------------------ source path text

        private static void SourceText()
        {
            Console.WriteLine("-- unit: source path text");
            string[] refused =
            {
                null, "", "   ", "\\\\server\\share\\a.png", "//server/share/a.png", "\\\\.\\C:\\a.png", "\\\\?\\C:\\a.png", "\\\\?\\UNC\\s\\a.png", "\\\\.\\PhysicalDrive0",
                "a.png", "..\\a.png", "C:a.png", "C:", "\\a.png", "/a.png", "file:///C:/a.png", "http://x/a.png", "C:\\a.png:stream", "C:\\x\\a.png::$DATA", "C:\\a*.png", "C:\\a?.png",
                "C:\\a<b.png", "C:\\a|b.png", "C:\\a\"b.png", "C:\\a\tb.png", "C:\\a\0.png", "C:\\" + new string('a', 300) + ".png", "1:\\a.png",
            };
            for (int ri = 0; ri < refused.Length; ri++)
            {
                Check($"source text refused #{ri}", AssetImportRules.CheckSourcePathText(refused[ri]) != null);
            }
            Check("UNC refusal names the limitation", AssetImportRules.CheckSourcePathText("\\\\server\\share\\a.png").Contains("not supported in this version"));
            Check("local absolute paths pass the text check", new[] { "C:\\a.png", "d:/x/y.obj", "E:\\Folder With Spaces\\a b.wav", "C:\\ü\\é.png" }.All(p => AssetImportRules.CheckSourcePathText(p) == null));
        }

        // ------------------------------------------------------------------ sniffing

        private static ImportSource Check1(string path, string ownAssets, AssetLimits limits = null) => AssetImportRules.CheckSource(path, ownAssets, limits ?? AssetLimits.Default);

        private static void Sniffing()
        {
            Console.WriteLine("-- unit: content sniffing");
            string own;
            string assets = FakeAssets(out own);
            string src = NewDir("src");
            (string ext, byte[] data, AssetImportKind kind)[] good =
            {
                (".png", Png(4, 4), AssetImportKind.Texture), (".jpg", Jpeg(8, 8), AssetImportKind.Texture), (".jpeg", Jpeg(8, 8), AssetImportKind.Texture),
                (".obj", Obj(), AssetImportKind.Mesh), (".wav", Wav(), AssetImportKind.Audio), (".ogg", Ogg(), AssetImportKind.Audio),
                (".mp3", Mp3Id3(), AssetImportKind.Audio),
            };
            foreach (var g in good)
            {
                string p = Write(src, "ok" + g.ext, g.data);
                ImportSource s = null;
                string err = MessageOf(() => s = Check1(p, assets));
                Check($"valid {g.ext} sample accepted ({err})", s != null && s.Kind == g.kind && s.Extension == g.ext && s.Length == g.data.Length && s.Checks.Count >= 3);
            }
            Check("mp3 with a frame sync and no tag is accepted", Check1(Write(src, "sync.mp3", Mp3Sync()), assets).Kind == AssetImportKind.Audio);
            Check("PNG size is read from the header", Check1(Write(src, "size.png", Png(640, 360)), assets) is ImportSource ps && ps.Width == 640 && ps.Height == 360);
            Check("JPEG size is read from the SOF marker", Check1(Write(src, "size.jpg", Jpeg(1280, 720)), assets) is ImportSource js && js.Width == 1280 && js.Height == 720);
            Check("OBJ vertex lines are counted", Check1(Write(src, "count.obj", Obj()), assets).VertexLines == 3);

            // every valid sample renamed to every wrong extension is refused, nothing is copied
            Dictionary<string, byte[]> samples = good.GroupBy(x => x.ext).ToDictionary(x => x.Key, x => x.First().data);
            int renamed = 0, refusedRenamed = 0;
            foreach (var sample in samples)
            {
                foreach (string ext in AssetImportRules.AllowedExtensions)
                {
                    bool compatible = ext == sample.Key || (ext == ".jpg" && sample.Key == ".jpeg") || (ext == ".jpeg" && sample.Key == ".jpg");
                    if (compatible)
                    {
                        continue;
                    }
                    renamed++;
                    string p = Write(src, "renamed-" + sample.Key.TrimStart('.') + "-as" + ext, sample.Value);
                    if (CodeOf(() => Check1(p, assets)) == ErrorCodes.InvalidParams)
                    {
                        refusedRenamed++;
                    }
                }
            }
            // an Ogg-looking OBJ is plain text without NUL? only OBJ accepts text; binary samples renamed to .obj contain NULs in the first block
            Check($"every sample renamed to a wrong extension is refused ({refusedRenamed} of {renamed})", refusedRenamed == renamed);
            Check("a text file renamed to .png/.wav/.ogg/.mp3/.jpg is refused", new[] { ".png", ".wav", ".ogg", ".mp3", ".jpg" }.All(e => CodeOf(() => Check1(Write(src, "text" + e, Encoding.ASCII.GetBytes("hello world, this is text")), assets)) == ErrorCodes.InvalidParams));
            Check("an exe renamed to .png is refused", CodeOf(() => Check1(Write(src, "mz.png", Encoding.ASCII.GetBytes("MZ").Concat(new byte[200]).ToArray()), assets)) == ErrorCodes.InvalidParams);
            Check("a PNG with a truncated header is refused", CodeOf(() => Check1(Write(src, "short.png", Png(4, 4).Take(20).ToArray()), assets)) == ErrorCodes.InvalidParams);
            Check("an OBJ with NUL bytes in the first block is refused", CodeOf(() => Check1(Write(src, "nul.obj", Obj().Concat(new byte[] { 0 }).ToArray()), assets)) == ErrorCodes.InvalidParams);
            Check("an OBJ without vertex lines is refused", CodeOf(() => Check1(Write(src, "novert.obj", Encoding.ASCII.GetBytes("# nothing\nvn 0 0 1\nf 1 2 3\n")), assets)) == ErrorCodes.InvalidParams);
            {
                object prop = new object();
                object[] matsOld = new object[1];
                Check("OBJ re-import: same Prop, same Mesh, new materials array = took effect (the reused-object case)", AssetImportRules.ObjReimportTookEffect(prop, prop, new object[1], matsOld));
                Check("OBJ re-import: a new Prop object = took effect", AssetImportRules.ObjReimportTookEffect(new object(), prop, matsOld, matsOld));
                Check("OBJ re-import: nothing replaced = failed (error logged, previous state kept)", !AssetImportRules.ObjReimportTookEffect(prop, prop, matsOld, matsOld));
                Check("OBJ re-import: no Prop object = failed", !AssetImportRules.ObjReimportTookEffect(null, prop, null, matsOld));
            }
            Check("an OBJ without face lines is refused (the game would import an empty mesh)", CodeOf(() => Check1(Write(src, "noface.obj", Encoding.ASCII.GetBytes("v 0 0 0\nv 1 0 0\nv 0 1 0\n")), assets)) == ErrorCodes.InvalidParams);
            Check("the no-face refusal names the face lines", MessageOf(() => Check1(Path.Combine(src, "noface.obj"), assets)).Contains("no face ('f ')"));
            Check("an OBJ with tab-separated face lines passes", Check1(Write(src, "tabface.obj", Encoding.ASCII.GetBytes("v 0 0 0\nv 1 0 0\nv 0 1 0\nf\t1 2 3\n")), assets).VertexLines == 3);
            Check("an indented f line is a face line (not refused as faceless)", Check1(Write(src, "indf.obj", Encoding.ASCII.GetBytes("v 0 0 0\nv 1 0 0\nv 0 1 0\n  f 1 2 3\n")), assets).VertexLines == 3);
            Check("indented v lines (spaces and tabs) count as vertex lines", AssetImportRules.CountObjLines(Write(src, "indv.obj", Encoding.ASCII.GetBytes("  v 0 0 0\n\tv 1 0 0\nv 0 1 0\nf 1 2 3\n"))).Vertices == 3);
            Check("a UTF-8 BOM before the first line does not hide its keyword", AssetImportRules.CountObjLines(Write(src, "bom.obj", new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.ASCII.GetBytes("v 0 0 0\nv 1 0 0\nv 0 1 0\nf 1 2 3\n")).ToArray())) is AssetImportRules.ObjCounts oc && oc.Vertices == 3 && oc.Faces == 1);
            Check("an indented-and-BOM OBJ passes the source check", Check1(Write(src, "bomf.obj", new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.ASCII.GetBytes("v 0 0 0\nv 1 0 0\nv 0 1 0\n\tf 1 2 3\n")).ToArray()), assets).VertexLines == 3);
            Check("only whitespace and a BOM are skipped: other prefixes still do not count", AssetImportRules.CountObjLines(Write(src, "x.obj", Encoding.ASCII.GetBytes("x v 0 0 0\n#f 1 2 3\n"))).Vertices == 0);
            Check("CountObjLines counts face lines", AssetImportRules.CountObjLines(Path.Combine(src, "tabface.obj")).Faces == 1 && AssetImportRules.CountObjLines(Path.Combine(src, "noface.obj")).Faces == 0);
            Check("type mismatch message echoes the caller's word and names both types",
                AssetImportRules.TypeMismatchMessage("Prop", AssetImportKind.Mesh, ".png", AssetImportKind.Texture) is string tm && tm.Contains("'type' Prop ") && tm.Contains("a .png file is a Texture") && !tm.Contains("Mesh"));
            Check("type mismatch message without the original word falls back to the public name", AssetImportRules.TypeMismatchMessage(null, AssetImportKind.Audio, ".obj", AssetImportKind.Mesh).Contains("'type' AudioClip "));
            Check("'vt' and 'vn' lines are not vertex lines", Check1(Write(src, "vt.obj", Encoding.ASCII.GetBytes("vt 0 0\nvn 0 0 1\nv 1 2 3\nf 1 1 1\n")), assets).VertexLines == 1);
            Check("zero-byte files are refused for every type", AssetImportRules.AllowedExtensions.All(e => CodeOf(() => Check1(Write(src, "empty" + e, new byte[0]), assets)) == ErrorCodes.InvalidParams));
            Check("zero-byte refusal says so", MessageOf(() => Check1(Path.Combine(src, "empty.png"), assets)).Contains("empty"));

            Check("a PNG wider than the cap is refused", MessageOf(() => Check1(Write(src, "wide.png", Png(AssetImportRules.MaxTextureSide + 1, 8)), assets)).Contains(AssetImportRules.MaxTextureSide.ToString()));
            Check("a PNG at the cap is accepted", Check1(Write(src, "cap.png", Png(AssetImportRules.MaxTextureSide, AssetImportRules.MaxTextureSide)), assets).Width == AssetImportRules.MaxTextureSide);
            Check("a tall JPEG over the cap is refused", CodeOf(() => Check1(Write(src, "tall.jpg", Jpeg(8, 20000)), assets)) == ErrorCodes.InvalidParams);
            Check("a 0x0 PNG is refused", CodeOf(() => Check1(Write(src, "zero.png", Png(0, 0)), assets)) == ErrorCodes.InvalidParams);

            StringBuilder big = new StringBuilder();
            for (int i = 0; i < AssetImportRules.MaxObjVertexLines + 1; i++)
            {
                big.Append("v 0 0 0\n");
            }
            string bigObj = Write(src, "big.obj", Encoding.ASCII.GetBytes(big.ToString()));
            Check("an OBJ over the vertex cap is refused", MessageOf(() => Check1(bigObj, assets, new AssetLimits(50, 500))).Contains("vertex"));
            Check("the vertex counter stops at the cap (no full scan of huge files)", AssetImportRules.CountVertexLines(bigObj) == AssetImportRules.MaxObjVertexLines + 1);
        }

        // ------------------------------------------------------------------ source checks (file system)

        private static void SourceChecks()
        {
            Console.WriteLine("-- unit: source checks");
            string assets = FakeAssets(out string modRoot);
            string src = NewDir("src2");
            string png = Write(src, "a.png", Png(4, 4));
            Check("a normal PNG passes", Check1(png, assets).Kind == AssetImportKind.Texture);
            Check("upper-case extension on input is accepted and normalised", Check1(Write(src, "UP.PNG", Png(4, 4)), assets).Extension == ".png");
            Check("a missing file is NOT_FOUND", CodeOf(() => Check1(Path.Combine(src, "none.png"), assets)) == ErrorCodes.NotFound);
            Check("a folder is refused", CodeOf(() => Check1(Directory.CreateDirectory(Path.Combine(src, "dir.png")).FullName, assets)) == ErrorCodes.InvalidParams);
            Check(".bmp is refused with a hint", MessageOf(() => Check1(Write(src, "a.bmp", new byte[] { (byte)'B', (byte)'M', 0, 0, 0 }), assets)).Contains(".bmp"));
            Check("other extensions are refused", new[] { "a.txt", "a.pmat", "a.pfab", "a.pgf", "a.exe", "a.dll", "a", "a.png.exe", "a.gif", "a.webp", "a.fbx" }.All(n => CodeOf(() => Check1(Write(src, n, Png(4, 4)), assets)) == ErrorCodes.InvalidParams));
            Check("a reserved device name is refused", CodeOf(() => Check1("C:\\Windows\\NUL.png", assets)) == ErrorCodes.InvalidParams && CodeOf(() => Check1("C:\\x\\CON", assets)) == ErrorCodes.InvalidParams);
            Check("a name with a trailing dot or space is refused", CodeOf(() => Check1("C:\\x\\a.png.", assets)) == ErrorCodes.InvalidParams && CodeOf(() => Check1("C:\\x\\a.png ", assets)) == ErrorCodes.InvalidParams);

            // size limits from the config object
            string big = Write(src, "big.png", Png(4, 4, 2 * 1024 * 1024));
            Check("a file over MaxImportFileMB is refused and the message names the setting", MessageOf(() => Check1(big, assets, new AssetLimits(1, 500))).Contains("MaxImportFileMB"));
            Check("the same file passes with a higher limit", Check1(big, assets, new AssetLimits(5, 500)).Length > 2 * 1024 * 1024);
            Check("the limit is the configured one (1 MB edge)", CodeOf(() => Check1(Write(src, "edge.png", Png(4, 4, 1024 * 1024 - Png(4, 4).Length)), assets, new AssetLimits(1, 500))) == null
                && CodeOf(() => Check1(Write(src, "edge2.png", Png(4, 4, 1024 * 1024 - Png(4, 4).Length + 1)), assets, new AssetLimits(1, 500))) == ErrorCodes.InvalidParams);

            // inside the mod's own assets -> refused; inside another mod's folder -> allowed (read-only)
            string inOwn = Write(Path.Combine(assets, "Textures"), "own.png", Png(4, 4));
            Check("a source inside the open mod's own Assets folder is refused", MessageOf(() => Check1(inOwn, assets)).Contains("already inside"));
            string other = Write(Path.Combine(NewDir("mods2"), "Real Mod", "Assets", "Textures"), "theirs.png", Png(4, 4));
            string otherHash = Hash(other);
            DateTime otherStamp = File.GetLastWriteTimeUtc(other);
            Check("a source inside ANOTHER mod's folder is allowed as a read-only source", Check1(other, assets).Kind == AssetImportKind.Texture);
            Check("...and is left untouched by the checks", Hash(other) == otherHash && File.GetLastWriteTimeUtc(other) == otherStamp && !File.Exists(other + ".pmeta"));
            Check("a source in the mod root outside Assets (e.g. Data) is allowed", Check1(Write(Path.Combine(modRoot, "Data"), "Preview.png", Png(4, 4)), assets).Kind == AssetImportKind.Texture);

            // an own .pmeta next to the source is ignored
            Write(src, "a.png.pmeta", Encoding.ASCII.GetBytes("{\"guid\":\"x\"}"));
            Check("a .pmeta next to the source is ignored", Check1(png, assets).Length == Png(4, 4).Length);

            // links are refused (needs a privilege on Windows: skipped when it cannot be created)
            string link = Path.Combine(src, "link.png");
            bool made = false;
            try
            {
                made = CreateLink(link, png);
            }
            catch (Exception)
            {
                made = false;
            }
            if (made)
            {
                Check("a symbolic link as the source is refused", CodeOf(() => Check1(link, assets)) == ErrorCodes.InvalidParams);
            }
            else
            {
                Console.WriteLine("  SKIP  symbolic link source (no privilege to create one)");
            }
        }

        private static bool CreateLink(string link, string target)
        {
            System.Diagnostics.ProcessStartInfo psi = new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink \"{link}\" \"{target}\"") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            using (System.Diagnostics.Process p = System.Diagnostics.Process.Start(psi))
            {
                p.WaitForExit(5000);
                return File.Exists(link);
            }
        }

        // ------------------------------------------------------------------ free names

        private static void FreeNames()
        {
            Console.WriteLine("-- unit: free names");
            string assets = FakeAssets(out _);
            string tex = Path.Combine(assets, "Textures");
            Directory.CreateDirectory(tex);
            ImportDestination d0 = AssetImportRules.ChooseFree(assets, AssetImportKind.Texture, "rock", ".png");
            Check("a free name is used as is", d0.Suffix == 0 && d0.FullPath == Path.Combine(tex, "rock.png") && d0.MetaPath == d0.FullPath + ".pmeta");
            File.WriteAllBytes(Path.Combine(tex, "rock.png"), new byte[1]);
            ImportDestination d1 = AssetImportRules.ChooseFree(assets, AssetImportKind.Texture, "rock", ".png");
            Check("an existing file gives ' (1)'", d1.Suffix == 1 && d1.Stem == "rock (1)" && Path.GetFileName(d1.FullPath) == "rock (1).png");
            File.WriteAllBytes(Path.Combine(tex, "rock (1).png.pmeta"), new byte[1]); // orphan sidecar only
            ImportDestination d2 = AssetImportRules.ChooseFree(assets, AssetImportKind.Texture, "rock", ".png");
            Check("a name whose .pmeta exists (without the file) is NOT free (the game would adopt the old sidecar)", d2.Suffix == 2);
            Check("the check is case-insensitive", AssetImportRules.ChooseFree(assets, AssetImportKind.Texture, "ROCK", ".png").Suffix == 2);
            Directory.CreateDirectory(Path.Combine(tex, "rock (2).png"));
            Check("a directory of that name is not free either", AssetImportRules.ChooseFree(assets, AssetImportKind.Texture, "rock", ".png").Suffix == 3);
            for (int i = 3; i <= AssetImportRules.MaxDuplicateSuffix; i++)
            {
                File.WriteAllBytes(Path.Combine(tex, $"rock ({i}).png"), new byte[1]);
            }
            string msg = MessageOf(() => AssetImportRules.ChooseFree(assets, AssetImportKind.Texture, "rock", ".png"));
            Check("all numbered names taken -> ALREADY_EXISTS (bounded, well below the editor's 100)", CodeOf(() => AssetImportRules.ChooseFree(assets, AssetImportKind.Texture, "rock", ".png")) == ErrorCodes.AlreadyExists && msg.Contains("nothing was copied"));
            Check("the bound is below the editor's 100", AssetImportRules.MaxDuplicateSuffix < 100 && AssetImportRules.MaxDuplicateSuffix >= 5);
            Check("other types are independent", AssetImportRules.ChooseFree(assets, AssetImportKind.Mesh, "rock", ".obj").Suffix == 0);
        }

        // ------------------------------------------------------------------ copy and rollback

        private static void CopyAndRollback()
        {
            Console.WriteLine("-- unit: copy, race, rollback");
            string assets = FakeAssets(out string modRoot);
            string src = NewDir("src3");
            byte[] data = Png(32, 16, 5000);
            string source = Write(src, "Photo.PNG", data);
            File.SetLastWriteTimeUtc(source, new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc));
            string hashBefore = Hash(source);
            DateTime writeBefore = File.GetLastWriteTimeUtc(source);
            DateTime accessBefore = File.GetCreationTimeUtc(source);
            FileAttributes attrBefore = File.GetAttributes(source);
            ImportSource s = Check1(source, assets);
            AssetImportRules.Created c = AssetImportRules.CopyIntoMod(s, assets, "photo");
            Check("the copy lands in Assets\\Textures with a lower-case extension", c.Destination.FullPath == Path.Combine(assets, "Textures", "photo.png") && File.Exists(c.Destination.FullPath));
            Check("the copy is byte-identical and the reported hash is right", Hash(c.Destination.FullPath) == hashBefore && c.Sha256.ToUpperInvariant() == hashBefore.Replace("-", string.Empty) && c.Bytes == data.Length);
            Check("the source is untouched (hash, write time, creation time, attributes)", Hash(source) == hashBefore && File.GetLastWriteTimeUtc(source) == writeBefore && File.GetCreationTimeUtc(source) == accessBefore && File.GetAttributes(source) == attrBefore);
            Check("no .pmeta or other file appears next to the source", Directory.GetFiles(src).Length == 1 && Path.GetFileName(Directory.GetFiles(src)[0]) == "Photo.PNG");
            bool unlocked;
            try
            {
                using (new FileStream(source, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    unlocked = true;
                }
            }
            catch (IOException)
            {
                unlocked = false;
            }
            Check("the source is not locked after the copy", unlocked);
            Check("the copy has no read-only attribute", (File.GetAttributes(c.Destination.FullPath) & FileAttributes.ReadOnly) == 0);
            Check("only the copy was created in the mod", Directory.GetFiles(assets, "*", SearchOption.AllDirectories).Length == 1);

            // a read-only source can be read and copied; the copy is writable
            string ro = Write(src, "ro.png", Png(4, 4));
            File.SetAttributes(ro, FileAttributes.ReadOnly);
            AssetImportRules.Created cro = AssetImportRules.CopyIntoMod(Check1(ro, assets), assets, "ro");
            Check("a read-only source works and the copy is not read-only", File.Exists(cro.Destination.FullPath) && (File.GetAttributes(cro.Destination.FullPath) & FileAttributes.ReadOnly) == 0);
            File.SetAttributes(ro, FileAttributes.Normal);

            // never overwrites: a second copy gets (1) and the first is unchanged
            AssetImportRules.Created c2 = AssetImportRules.CopyIntoMod(s, assets, "photo");
            Check("a second import never overwrites: ' (1)', first copy unchanged", c2.Destination.Suffix == 1 && Path.GetFileName(c2.Destination.FullPath) == "photo (1).png" && Hash(c.Destination.FullPath) == hashBefore);

            // a file appearing between the free-name check and the create is not overwritten
            string racerPath = null;
            byte[] racerBytes = Encoding.ASCII.GetBytes("someone else's file");
            AssetImportRules.Created c3 = AssetImportRules.CopyIntoMod(s, assets, "race", target =>
            {
                if (racerPath == null)
                {
                    racerPath = target;
                    File.WriteAllBytes(target, racerBytes); // appears after the check
                }
            });
            Check("a simulated race: the racer's file is untouched and the copy took the next name",
                racerPath == Path.Combine(assets, "Textures", "race.png") && File.ReadAllText(racerPath) == "someone else's file" && c3.Destination.Suffix == 1 && Hash(c3.Destination.FullPath) == hashBefore);
            // the racer also holding the pmeta
            string racerMeta = null;
            AssetImportRules.Created c4 = AssetImportRules.CopyIntoMod(s, assets, "race2", target =>
            {
                if (racerMeta == null)
                {
                    racerMeta = target + ".pmeta";
                    File.WriteAllBytes(racerMeta, new byte[3]);
                }
            });
            Check("a .pmeta appearing in between makes the name unusable (next name taken, orphan untouched)", c4.Destination.Suffix == 1 && File.ReadAllBytes(racerMeta).Length == 3);

            // CreateNew semantics directly
            string cn = Path.Combine(assets, "Textures", "cn.png");
            File.WriteAllBytes(cn, new byte[1]);
            Check("CreateNew fails on an existing file", Throws<IOException>(() => new FileStream(cn, FileMode.CreateNew, FileAccess.Write, FileShare.None).Dispose()));

            // rollback removes exactly what the call created and leaves neighbours alone
            AssetImportRules.Created c5 = AssetImportRules.CopyIntoMod(s, assets, "keepme");
            c5.BeforeMetaStep(); // the import step is reached and the .pmeta does not exist yet
            File.WriteAllBytes(c5.Destination.MetaPath, Encoding.ASCII.GetBytes("{}")); // the importer's sidecar
            string neighbour = Write(Path.Combine(assets, "Textures"), "keepme-neighbour.png", Png(4, 4));
            string neighbourMeta = Write(Path.Combine(assets, "Textures"), "keepme-neighbour.png.pmeta", new byte[2]);
            string similar = Write(Path.Combine(assets, "Textures"), "keepme (9).png", Png(4, 4));
            string otherFolder = Write(Path.Combine(assets, "Audio"), "keepme.png", Png(4, 4));
            AssetImportRules.CleanupReport rep = AssetImportRules.Rollback(c5);
            Check("rollback deletes the copy and its .pmeta", !File.Exists(c5.Destination.FullPath) && !File.Exists(c5.Destination.MetaPath) && rep.Deleted.Count == 2 && rep.Problems.Count == 0);
            Check("rollback leaves every neighbour (pre-existing files, similar names, other folders) alone", File.Exists(neighbour) && File.Exists(neighbourMeta) && File.Exists(similar) && File.Exists(otherFolder)
                && File.Exists(c.Destination.FullPath) && File.Exists(c2.Destination.FullPath) && File.Exists(cn));
            Check("rolling back twice is harmless", AssetImportRules.Rollback(c5).Deleted.Count == 0);

            // L11: the .pmeta is deleted only when this call can prove it created it
            AssetImportRules.Created c6 = AssetImportRules.CopyIntoMod(s, assets, "proof1");
            File.WriteAllBytes(c6.Destination.MetaPath, Encoding.ASCII.GetBytes("{\"other\":1}")); // appeared before the import step (someone else's)
            AssetImportRules.CleanupReport rep6 = AssetImportRules.Rollback(c6);
            Check("L11: rollback before the import step keeps a .pmeta it did not create (and says so)", !File.Exists(c6.Destination.FullPath) && File.Exists(c6.Destination.MetaPath)
                && rep6.Problems.Any(p => p.Contains("cannot prove")));
            AssetImportRules.Created c7 = AssetImportRules.CopyIntoMod(s, assets, "proof2");
            File.WriteAllBytes(c7.Destination.MetaPath, Encoding.ASCII.GetBytes("{\"other\":2}"));
            c7.BeforeMetaStep(); // the .pmeta already existed right before the import step
            AssetImportRules.Rollback(c7);
            Check("L11: rollback keeps a .pmeta that existed right before the import step", File.Exists(c7.Destination.MetaPath) && !File.Exists(c7.Destination.FullPath));
            Check("rollback of a call that created nothing deletes nothing (e.g. the racer's file)",
                AssetImportRules.Rollback(new AssetImportRules.Created { Destination = c3.Destination, FileCreated = false }).Deleted.Count == 0 && File.Exists(c3.Destination.FullPath));

            // rollback refuses anything not strictly inside the Assets\Sub folder
            string outside = Write(NewDir("outside"), "victim.png", Png(4, 4));
            AssetImportRules.CleanupReport bad = AssetImportRules.Rollback(new AssetImportRules.Created
            {
                FileCreated = true,
                Destination = new ImportDestination { AssetsFolder = assets, SubFolder = "Textures", FullPath = outside, MetaPath = outside + ".pmeta" },
            });
            Check("rollback refuses a path outside the Assets folder", File.Exists(outside) && bad.Deleted.Count == 0 && bad.Problems.Count >= 1);
            string wrongSub = Path.Combine(assets, "Audio", "keepme.png");
            AssetImportRules.CleanupReport bad2 = AssetImportRules.Rollback(new AssetImportRules.Created
            {
                FileCreated = true,
                Destination = new ImportDestination { AssetsFolder = assets, SubFolder = "Textures", FullPath = wrongSub, MetaPath = wrongSub + ".pmeta" },
            });
            Check("rollback refuses a file in another sub-folder", File.Exists(wrongSub) && bad2.Deleted.Count == 0);
            string traverse = Path.Combine(assets, "Textures", "..", "..", "..", "outside-x.png");
            Write(Path.GetDirectoryName(Path.GetFullPath(traverse)), "outside-x.png", Png(4, 4));
            AssetImportRules.Rollback(new AssetImportRules.Created
            {
                FileCreated = true,
                Destination = new ImportDestination { AssetsFolder = assets, SubFolder = "Textures", FullPath = traverse, MetaPath = traverse + ".pmeta" },
            });
            Check("rollback refuses a traversing path", File.Exists(Path.GetFullPath(traverse)));

            // a copy that fails half way removes its own file only (source length changed under it)
            ImportSource lie = Check1(Write(src, "lie.png", Png(4, 4, 100)), assets);
            lie.Length += 7;
            string beforeFiles = string.Join("|", Directory.GetFiles(assets, "*", SearchOption.AllDirectories).OrderBy(x => x).ToArray());
            string failMsg = MessageOf(() => AssetImportRules.CopyIntoMod(lie, assets, "lie"));
            string afterFiles = string.Join("|", Directory.GetFiles(assets, "*", SearchOption.AllDirectories).OrderBy(x => x).ToArray());
            Check("a failing copy removes what it created and reports it", failMsg != null && failMsg.Contains("removed") && failMsg.Contains("source file was not touched") && beforeFiles == afterFiles);
            Check("...and the source is still intact", File.Exists(Path.Combine(src, "lie.png")) && Hash(Path.Combine(src, "lie.png")) == Hash(Path.Combine(src, "lie.png")));

            // sub-folder problems
            string assets2 = FakeAssets(out _);
            File.WriteAllBytes(Path.Combine(assets2, "Textures"), new byte[1]); // a FILE named Textures
            Check("Assets\\Textures existing as a file is refused before anything is written", CodeOf(() => AssetImportRules.CopyIntoMod(Check1(source, assets2), assets2, "x")) == ErrorCodes.InvalidState && File.ReadAllBytes(Path.Combine(assets2, "Textures")).Length == 1);

            // all kinds
            string assets3 = FakeAssets(out _);
            foreach ((string name, byte[] bytes, string sub) t in new[] { ("m.obj", Obj(), "Props"), ("a.wav", Wav(), "Audio"), ("b.ogg", Ogg(), "Audio"), ("c.mp3", Mp3Id3(), "Audio"), ("d.jpg", Jpeg(4, 4), "Textures") })
            {
                AssetImportRules.Created k = AssetImportRules.CopyIntoMod(Check1(Write(src, t.name, t.bytes), assets3), assets3, Path.GetFileNameWithoutExtension(t.name));
                Check($"{t.name} lands in Assets\\{t.sub}", Path.GetDirectoryName(k.Destination.FullPath) == Path.Combine(assets3, t.sub) && File.Exists(k.Destination.FullPath));
            }
        }

        private static bool Throws<T>(Action a) where T : Exception
        {
            try
            {
                a();
                return false;
            }
            catch (T)
            {
                return true;
            }
        }

        // ------------------------------------------------------------------ limits

        private static void Limits()
        {
            Console.WriteLine("-- unit: limits");
            string assets = FakeAssets(out _);
            Write(Path.Combine(assets, "Textures"), "x.bin", new byte[300 * 1024]);
            Write(Path.Combine(assets, "Audio"), "y.bin", new byte[300 * 1024]);
            AssetLimits l = new AssetLimits(50, 1);
            Check("folder size counts every file below Assets", AssetImportRules.FolderSize(assets) == 600 * 1024);
            Check("the per-mod total passes while within the limit", AssetImportRules.CheckModTotal(assets, 400 * 1024, l) == null);
            string why = AssetImportRules.CheckModTotal(assets, 500 * 1024, l);
            Check("the per-mod total refuses past the limit and names the setting", why != null && why.Contains("MaxModAssetsMB"));
            Check("limits default to 50 and 500 MB", AssetLimits.Default.MaxImportFileMB == 50 && AssetLimits.Default.MaxModAssetsMB == 500 && AssetLimits.Default.MaxImportFileBytes == 50L * 1024 * 1024);
            Check("limits are re-evaluated per object (a changed config gives other bounds)", new AssetLimits(10, 20).MaxImportFileBytes == 10L * 1024 * 1024 && new AssetLimits(10, 20).MaxModAssetsMB == 20);
            Check("an empty Assets folder has size 0 and a missing one too", AssetImportRules.FolderSize(Path.Combine(s_root, "nope")) == 0);
        }

        // ------------------------------------------------------------------ removal guard

        private static RemovalFacts Facts(string assets, string cls = "PummelTexture", string file = null, bool external = true, bool builtIn = false)
        {
            return new RemovalFacts
            {
                IsExternal = external, IsBuiltIn = builtIn, ClassName = cls, AssetsFolder = assets,
                FilePath = file ?? Path.Combine(assets, "Textures", "a.png"),
            };
        }

        private static void RemovalRules()
        {
            Console.WriteLine("-- unit: removal guard");
            string assets = FakeAssets(out _);
            Check("an own texture passes the guard", AssetRemovalRules.Check(Facts(assets)) == null);
            Check("props, audio clips and materials pass", AssetRemovalRules.Check(Facts(assets, "PummelProp", Path.Combine(assets, "Props", "a.obj"))) == null
                && AssetRemovalRules.Check(Facts(assets, "PummelAudioClip", Path.Combine(assets, "Audio", "a.wav"))) == null
                && AssetRemovalRules.Check(Facts(assets, "PummelMaterial", Path.Combine(assets, "Materials", "Material_0.pmat"))) == null);
            Check("external but its file is OUTSIDE the assets folder -> refused", AssetRemovalRules.Check(Facts(assets, file: Path.Combine(s_root, "Elsewhere", "a.png")))?.Contains("not strictly inside") == true);
            Check("a file outside via traversal -> refused", AssetRemovalRules.Check(Facts(assets, file: Path.Combine(assets, "..", "Data", "a.png"))) != null);
            Check("the Assets folder itself -> refused", AssetRemovalRules.Check(Facts(assets, file: assets)) != null);
            Check("no file path -> refused", AssetRemovalRules.Check(Facts(assets, file: "")) != null);
            Check("a built-in asset -> refused", AssetRemovalRules.Check(Facts(assets, external: false, builtIn: true))?.Contains("built-in") == true);
            Check("a non-external asset -> refused", AssetRemovalRules.Check(Facts(assets, external: false)) != null);
            Check("an own prefab (.pfab) passes the class/path guard; the open prefab, a wrong extension and a prefab outside Assets do not",
                AssetRemovalRules.Check(Facts(assets, "PummelPrefab", Path.Combine(assets, "Prefabs", "p.pfab"))) == null
                && AssetRemovalRules.Check(new RemovalFacts { IsExternal = true, ClassName = "PummelPrefab", AssetsFolder = assets, FilePath = Path.Combine(assets, "Prefabs", "p.pfab"), IsOpenPrefab = true }) != null
                && AssetRemovalRules.Check(Facts(assets, "PummelPrefab", Path.Combine(assets, "Prefabs", "p.png"))) != null
                && AssetRemovalRules.Check(Facts(assets, "PummelTexture", Path.Combine(assets, "Prefabs", "p.pfab"))) != null
                && AssetRemovalRules.Check(Facts(assets, "PummelPrefab", Path.Combine(s_root, "Elsewhere", "p.pfab"))) != null);
            Check("visual scripts and unknown classes -> refused", AssetRemovalRules.Check(Facts(assets, "PummelVisualScript", Path.Combine(assets, "Scripts", "s.pgf"))) != null && AssetRemovalRules.Check(Facts(assets, "ModAsset")) != null);
            Check("the open prefab -> refused", AssetRemovalRules.Check(new RemovalFacts { IsExternal = true, ClassName = "PummelTexture", AssetsFolder = assets, FilePath = Path.Combine(assets, "Textures", "a.png"), IsOpenPrefab = true }) != null);
            Check("selected with unapplied import settings -> refused", AssetRemovalRules.Check(new RemovalFacts { IsExternal = true, ClassName = "PummelTexture", AssetsFolder = assets, FilePath = Path.Combine(assets, "Textures", "a.png"), IsSelectedAndDirty = true }) != null);
            Check("a file with an unhandled extension -> refused", AssetRemovalRules.Check(Facts(assets, file: Path.Combine(assets, "Textures", "a.exe"))) != null);
            string prop = Path.Combine(assets, "Props", "a.obj");
            Check("a prop whose nested mesh points at another file -> refused (the game would delete that path)", AssetRemovalRules.Check(new RemovalFacts
            {
                IsExternal = true, ClassName = "PummelProp", AssetsFolder = assets, FilePath = prop, HasNested = true, NestedPath = Path.Combine(assets, "Props", "other.obj"),
            }) != null);
            Check("a prop whose nested mesh is the same file passes", AssetRemovalRules.Check(new RemovalFacts
            {
                IsExternal = true, ClassName = "PummelProp", AssetsFolder = assets, FilePath = prop, HasNested = true, NestedPath = prop.ToUpperInvariant(),
            }) == null);
            Check("the allow-list is exactly texture, prop, audio clip, material, prefab", AssetRemovalRules.AllowedClasses.OrderBy(x => x).SequenceEqual(new[] { "PummelAudioClip", "PummelMaterial", "PummelPrefab", "PummelProp", "PummelTexture" }));
        }

        // ------------------------------------------------------------------ reference scan

        private static void References()
        {
            Console.WriteLine("-- unit: reference scan");
            string g = "11111111-2222-3333-4444-555555555555";
            JObject comp = JObject.Parse($@"{{
                'Prop': {{ 'type': 'Prop', 'value': {{ 'guid': '{g}', 'name': 'rock' }} }},
                'materials': {{ 'value': [ {{ 'guid': 'aaaaaaaa-0000-0000-0000-000000000000' }}, {{ 'guid': '{g.ToUpperInvariant()}', 'name': 'x' }} ] }},
                'PrefabGUIDs': {{ 'value': [ '{g}', 'other' ] }},
                'Clip': {{ 'value': null }},
                'OnEnterActions': {{ 'value': {{ 'actions': [ {{ 'type': 'PlaySound', 'parameters': {{ 'm_clip': {{ 'guid': '{g}' }} }} }} ] }} }},
                'Name': {{ 'value': 'not {g} exactly' }}
            }}");
            List<string> found = new List<string>();
            bool incomplete = false;
            ReferenceScan.FindGuid(comp, g, string.Empty, found, ref incomplete);
            Check("finds {guid} objects, plain strings and nested action parameters", found.Count == 4 && found.Contains("Prop.value") && found.Contains("materials.value[1]") && found.Contains("PrefabGUIDs.value[0]")
                && found.Contains("OnEnterActions.value.actions[0].parameters.m_clip"));
            Check("the guid comparison ignores case", found.Any(f => f.StartsWith("materials")));
            Check("a clean scan is complete", !incomplete);
            incomplete = false;
            ReferenceScan.FindGuid(JObject.Parse("{'a': {'truncated': 'X'}}"), g, string.Empty, new List<string>(), ref incomplete);
            Check("truncated / unsupported / unavailable markers make the scan incomplete", incomplete);
            incomplete = false;
            ReferenceScan.FindGuid(JObject.Parse("{'a': {'unavailable': 'not ready'}}"), g, string.Empty, new List<string>(), ref incomplete);
            Check("'unavailable' components make it incomplete", incomplete);
            Check("an unrelated guid finds nothing", Count(comp, "99999999-0000-0000-0000-000000000000") == 0);
            JArray many = new JArray(Enumerable.Range(0, 1000).Select(i => (JToken)new JObject { ["guid"] = g }).ToArray());
            Check("findings are capped", Count(new JObject { ["x"] = many }, g) == ReferenceScan.MaxFindings);
            AssetReference r = new AssetReference { Kind = "action", Owner = "Door / Logic", OwnerGuid = g, Where = "OnEnterActions.value.actions[0]" };
            Check("references describe themselves for error messages", ReferenceScan.Describe(r).Contains("Door / Logic") && (string)ReferenceScan.ToJson(r)["kind"] == "action" && (string)ReferenceScan.ToJson(r)["ownerGuid"] == g);
        }

        private static int Count(JToken t, string guid)
        {
            List<string> f = new List<string>();
            bool inc = false;
            ReferenceScan.FindGuid(t, guid, string.Empty, f, ref inc);
            return f.Count;
        }

        // ------------------------------------------------------------------ import settings

        private static void ImportSettings()
        {
            Console.WriteLine("-- unit: import settings");
            Func<ImportSettingField, string> cur = f => f.Default;
            ImportSettingsPlan p = ImportSettingRules.Plan("Texture", JObject.Parse("{'compression':'highquality','filterMode':'Point','wrapMode':3}"), cur);
            Check("texture: names are case-insensitive and integers map to names", p.Changes.Count == 3 && p.Changes.First(c => c.Field.Name == "compression").NewValue == "HighQuality"
                && p.Changes.First(c => c.Field.Name == "filterMode").NewValue == "Point" && p.Changes.First(c => c.Field.Name == "wrapMode").NewValue == "MirrorOnce");
            Check("values equal to the current ones are reported unchanged", ImportSettingRules.Plan("Texture", JObject.Parse("{'compression':'Normal'}"), cur).Unchanged.SequenceEqual(new[] { "compression" }));
            Check("unknown field refused", CodeOf(() => ImportSettingRules.Plan("Texture", JObject.Parse("{'mipmaps':true}"), cur)) == ErrorCodes.InvalidParams);
            Check("undefined enum value refused", new[] { "'Ultra'", "9", "true", "null", "1.5" }.All(v => CodeOf(() => ImportSettingRules.Plan("Texture", JObject.Parse("{'compression':" + v + "}"), cur)) == ErrorCodes.InvalidParams));
            Check("filter Trilinear and wrap Mirror are choices the inspector offers", ImportSettingRules.Plan("Texture", JObject.Parse("{'filterMode':'Trilinear','wrapMode':'Mirror'}"), cur).Changes.Count == 2);
            Check("prop: recalculateNormals is a bool", ImportSettingRules.Plan("Prop", JObject.Parse("{'recalculateNormals':true}"), cur).Changes[0].NewValue == "true"
                && CodeOf(() => ImportSettingRules.Plan("Prop", JObject.Parse("{'recalculateNormals':'yes'}"), cur)) == ErrorCodes.InvalidParams);
            Check("audio, material and prefab have no import settings", new[] { "AudioClip", "Material", "Prefab", "Shader" }.All(t => CodeOf(() => ImportSettingRules.Plan(t, JObject.Parse("{'x':1}"), cur)) == ErrorCodes.InvalidParams));
            Check("empty values refused", CodeOf(() => ImportSettingRules.Plan("Texture", new JObject(), cur)) == ErrorCodes.InvalidParams);
            Check("a failing field refuses the whole call (nothing partial in the plan)", CodeOf(() => ImportSettingRules.Plan("Texture", JObject.Parse("{'compression':'None','wrapMode':'Nope'}"), cur)) == ErrorCodes.InvalidParams);
            Check("the table has the four fields of the inspectors", ImportSettingRules.All.Count == 4 && ImportSettingRules.AssetTypes.SequenceEqual(new[] { "Texture", "Prop" }));
            Check("enum tables are consistent (names/values/defaults)", ImportSettingRules.All.Where(f => !f.IsBool).All(f => f.EnumNames.Length == f.EnumValues.Length && f.EnumNames.Contains(f.Default)));
            Check("schema JSON lists choices", ((JArray)ImportSettingRules.ToJson("Texture")["wrapMode"]["choices"]).Count == 4);
        }

        // ------------------------------------------------------------------ materials

        private sealed class FakeMat : IMaterialState
        {
            public readonly Dictionary<string, object> Values = new Dictionary<string, object>();
            public readonly HashSet<string> Textures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public readonly Dictionary<string, double[]> Ranges = new Dictionary<string, double[]>();

            public object Get(string key) => Values.TryGetValue(key, out object v) ? v : null;

            public bool TryRange(string key, out double min, out double max)
            {
                min = max = 0;
                if (!Ranges.TryGetValue(key, out double[] r))
                {
                    return false;
                }
                min = r[0];
                max = r[1];
                return true;
            }

            public string CheckTexture(string guid) => Textures.Contains(guid) ? null : "no texture asset with that guid";
        }

        private static void Materials()
        {
            Console.WriteLine("-- unit: material rules");
            ShaderRule std = MaterialRules.ForShader("Standard");
            ShaderRule decal = MaterialRules.ForShader("Standard Decal Receiver");
            string tex = "11111111-1111-1111-1111-111111111111";
            FakeMat m = new FakeMat();
            m.Textures.Add(tex);
            m.Ranges["_Metallic"] = new[] { 0.0, 1.0 };
            m.Ranges["_Glossiness"] = new[] { 0.0, 1.0 };
            m.Ranges["_Parallax"] = new[] { 0.005, 0.08 };
            m.Values["_Mode"] = "Opaque";
            m.Values["Emission"] = false;

            Check("five shader inspectors are covered (Standard + Decal Receiver share one)", MaterialRules.Shaders.Count == 4 && std == decal && MaterialRules.ForShader("Custom/ModBoardSpaceShader") != null
                && MaterialRules.ForShader("Particles/Additive Intensify Mod") != null && MaterialRules.ForShader("Legacy Shaders/Particles/Alpha Blended Mod") != null && MaterialRules.ForShader("Unlit/Texture") == null);
            MaterialPlan p = MaterialRules.Plan(std, JObject.Parse($"{{'_Color':{{'r':1,'g':0.5,'b':0}},'_MainTex':'{tex.ToUpperInvariant()}','_Metallic':0.25,'Tiling':[2,3],'_Mode':'transparent'}}"), m);
            Check("plan normalises colour (alpha defaults to 1), texture guid, vectors and mode",
                ((double[])p.Assignments[0].Value).SequenceEqual(new[] { 1, 0.5, 0, 1 }) && (string)p.Assignments[1].Value == tex && ((double[])p.Assignments[3].Value).SequenceEqual(new double[] { 2, 3 }) && (string)p.Assignments[4].Value == "Transparent");
            Check("a colour as an array works", ((double[])MaterialRules.Plan(std, JObject.Parse("{'_Color':[0,1,0.5,0.25]}"), m).Assignments[0].Value).SequenceEqual(new[] { 0, 1, 0.5, 0.25 }));
            MaterialPlan clamp = MaterialRules.Plan(std, JObject.Parse("{'_Color':{'r':2,'g':-1,'b':0},'_Metallic':5,'_Glossiness':-3,'Tiling':{'x':5000,'y':0}}"), m);
            Check("colours, ranges (the shader's own) and vectors are clamped and the clamps reported", clamp.Clamped.Count == 5 && clamp.Clamped.Any(c => c.Key == "_Metallic" && c.Applied == "1") && clamp.Clamped.Any(c => c.Key == "_Glossiness" && c.Applied == "0"));
            Check("HDR emission colour allows values above 1", MaterialRules.Plan(std, JObject.Parse("{'Emission':true,'_EmissionColor':{'r':5,'g':1,'b':1}}"), m).Clamped.Count == 0);
            Check("HDR colour is capped", MaterialRules.Plan(std, JObject.Parse("{'Emission':true,'_EmissionColor':{'r':500,'g':1,'b':1}}"), m).Clamped.Count == 1);
            Check("unknown property refused (and the allowed keys are listed)", MessageOf(() => MaterialRules.Plan(std, JObject.Parse("{'_Foo':1}"), m)).Contains("_Color"));
            Check("keys are matched case-insensitively", MaterialRules.Plan(std, JObject.Parse("{'_color':[1,1,1]}"), m).Assignments.Count == 1);
            Check("wrong types are refused", new[] { "{'_Metallic':'x'}", "{'_Color':5}", "{'_Color':[1,2]}", "{'_Color':{'r':1,'g':1}}", "{'_Color':{'r':1,'g':1,'b':1,'q':1}}", "{'Emission':1}", "{'_Mode':'Glass'}", "{'_Mode':7}", "{'Tiling':[1]}", "{'_MainTex':5}", "{'_MainTex':'nope'}" }
                .All(js => CodeOf(() => MaterialRules.Plan(std, JObject.Parse(js), m)) == ErrorCodes.InvalidParams));
            Check("a texture must be a texture asset guid (null clears)", MaterialRules.Plan(std, JObject.Parse("{'_MainTex':null}"), m).Assignments[0].Value == null && CodeOf(() => MaterialRules.Plan(std, JObject.Parse("{'_MainTex':'22222222-2222-2222-2222-222222222222'}"), m)) == ErrorCodes.InvalidParams);
            Check("{guid} is accepted for a texture", (string)MaterialRules.Plan(std, JObject.Parse($"{{'_MainTex':{{'guid':'{tex}'}}}}"), m).Assignments[0].Value == tex);

            // conditions
            Check("_Cutoff is refused unless the mode is Cutout", CodeOf(() => MaterialRules.Plan(std, JObject.Parse("{'_Cutoff':0.5}"), m)) == ErrorCodes.InvalidParams);
            Check("_Cutoff works when _Mode: Cutout is given in the same call", MaterialRules.Plan(std, JObject.Parse("{'_Cutoff':0.5,'_Mode':'Cutout'}"), m).Assignments.Count == 2);
            Check("_Cutoff works when the material is already Cutout", MaterialRules.Plan(std, JObject.Parse("{'_Cutoff':0.5}"), new FakeMat { Values = { ["_Mode"] = "Cutout" } }).Assignments.Count == 1);
            Check("_Cutoff is refused when the same call sets another mode", CodeOf(() => MaterialRules.Plan(std, JObject.Parse("{'_Cutoff':0.5,'_Mode':'Fade'}"), new FakeMat { Values = { ["_Mode"] = "Cutout" } })) == ErrorCodes.InvalidParams);
            Check("_EmissionColor needs Emission on", CodeOf(() => MaterialRules.Plan(std, JObject.Parse("{'_EmissionColor':[1,1,1]}"), m)) == ErrorCodes.InvalidParams
                && MaterialRules.Plan(std, JObject.Parse("{'_EmissionColor':[1,1,1],'Emission':true}"), m).Assignments.Count == 2);
            Check("_Parallax needs a height map (in the call or already set)", CodeOf(() => MaterialRules.Plan(std, JObject.Parse("{'_Parallax':0.02}"), m)) == ErrorCodes.InvalidParams
                && MaterialRules.Plan(std, JObject.Parse($"{{'_Parallax':0.02,'_ParallaxMap':'{tex}'}}"), m).Assignments.Count == 2
                && MaterialRules.Plan(std, JObject.Parse("{'_Parallax':0.02}"), new FakeMat { Values = { ["_ParallaxMap"] = tex }, Ranges = { ["_Parallax"] = new[] { 0.005, 0.08 } } }).Assignments.Count == 1
                && CodeOf(() => MaterialRules.Plan(std, JObject.Parse("{'_Parallax':0.02,'_ParallaxMap':null}"), new FakeMat { Values = { ["_ParallaxMap"] = tex }, Ranges = { ["_Parallax"] = new[] { 0.005, 0.08 } } })) == ErrorCodes.InvalidParams);
            MaterialPlan par = MaterialRules.Plan(std, JObject.Parse($"{{'_Parallax':1,'_ParallaxMap':'{tex}'}}"), m);
            Check("_Parallax is clamped to the shader's range", par.Clamped.Count == 1 && par.Clamped[0].Applied == "0.08");
            Check("a range without a shader range uses the documented fallback", MaterialRules.Plan(std, JObject.Parse("{'_Metallic':3}"), new FakeMat()).Clamped.Count == 1);
            ShaderRule board = MaterialRules.ForShader("Custom/ModBoardSpaceShader");
            Check("a range with neither shader range nor fallback is refused (not set blindly)", CodeOf(() => MaterialRules.Plan(board, JObject.Parse("{'_IconScale':1}"), new FakeMat())) == ErrorCodes.InvalidParams
                && MaterialRules.Plan(board, JObject.Parse("{'_IconScale':1}"), new FakeMat { Ranges = { ["_IconScale"] = new[] { 0.0, 2.0 } } }).Assignments.Count == 1);
            Check("the board-space, particle and alpha-blended shaders expose their inspector's properties",
                new[] { "_MainTex", "Tiling", "Offset", "_EdgeColor", "_InsideColor", "_IconColor", "_Metallic", "_Glossiness", "_IconScale" }.SequenceEqual(board.Props.Select(x => x.Key))
                && MaterialRules.ForShader("Particles/Additive Intensify Mod").Props.Select(x => x.Key).SequenceEqual(new[] { "_TintColor", "_MainTex", "Tiling", "Offset", "_InvFade", "_Glow" })
                && MaterialRules.ForShader("Legacy Shaders/Particles/Alpha Blended Mod").Props.Select(x => x.Key).SequenceEqual(new[] { "_TintColor", "_MainTex", "Tiling", "Offset", "_InvFade" }));
            Check("a property of another shader is refused", CodeOf(() => MaterialRules.Plan(board, JObject.Parse("{'_Color':[1,1,1]}"), new FakeMat())) == ErrorCodes.InvalidParams);
            Check("empty and oversized property objects are refused", CodeOf(() => MaterialRules.Plan(std, new JObject(), m)) == ErrorCodes.InvalidParams
                && CodeOf(() => MaterialRules.Plan(std, new JObject(Enumerable.Range(0, 40).Select(i => new JProperty("_k" + i, 1))), m)) == ErrorCodes.InvalidParams);
            Check("only the standard shaders use the standard keyword logic", MaterialRules.UsesStandardKeywords("Standard") && MaterialRules.UsesStandardKeywords("Standard Decal Receiver") && !MaterialRules.UsesStandardKeywords("Custom/ModBoardSpaceShader"));
            Check("JSON shapes round-trip", (string)MaterialRules.ToJson(std.Props.First(x => x.Key == "_Mode"), "Cutout") == "Cutout" && (double)MaterialRules.ToJson(std.Props.First(x => x.Key == "_Color"), new[] { 1, 0.5, 0, 1 })["g"] == 0.5
                && MaterialRules.ToJson(std.Props.First(x => x.Key == "_MainTex"), null).Type == JTokenType.Null);
            Check("rules JSON lists every shader", MaterialRules.RulesJson().Properties().Count() == 4);
            Check("shaders without an inspector are not in the table (their properties are refused)", MaterialRules.ForShader("Unlit/Color") == null && MaterialRules.KnownShaderNames.Contains("Standard"));
        }

        // ------------------------------------------------------------------ list_assets

        private static void Listing()
        {
            Console.WriteLine("-- unit: list_assets location filter");
            List<AssetEntry> all = new List<AssetEntry>
            {
                new AssetEntry { Guid = "g1", Name = "Rock", Type = "Texture", TypeAliases = new string[0], Row = new JObject { ["guid"] = "g1", ["location"] = "external" } },
                new AssetEntry { Guid = "g2", Name = "Rock", Type = "Texture2D", TypeAliases = new string[0], Row = new JObject { ["guid"] = "g2", ["location"] = "internal" } },
            };
            Check("location external keeps the mod's own assets", (int)AssetListing.Query(all, null, null, 10, 0, "external")["total"] == 1);
            Check("location internal keeps the built-in ones", (string)AssetListing.Query(all, null, null, 10, 0, "internal")["assets"][0]["guid"] == "g2");
            Check("no location filter keeps both (shape unchanged)", (int)AssetListing.Query(all, null, null, 10, 0)["total"] == 2);
            Check("an unknown location is refused", CodeOf(() => AssetListing.Query(all, null, null, 10, 0, "runtime")) == ErrorCodes.InvalidParams);
        }


        // ------------------------------------------------------------------ using imported assets (examples in the docs)

        private sealed class PreviewState : ISettingsState
        {
            public readonly HashSet<string> Known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            public object Current(ModSettingField f) => f.Kind == SettingKind.Asset ? new AssetValue() : null;

            public bool? Derived(string name) => null;

            public IList<string> PresetNames(ModSettingField f) => null;

            public string CheckAsset(ModSettingField f, string guid) => Known.Contains(guid) ? null : "no asset with that guid";

            public bool TryGetLimits(ModSettingField f, out double min, out double max)
            {
                min = max = 0;
                return false;
            }
        }

        private static void UsingImportedAssets()
        {
            Console.WriteLine("-- unit: using imported assets (docs examples)");
            string mat = "aaaaaaaa-1111-2222-3333-444444444444";
            ValueTypeDesc materials = ValueTypeDesc.ArrayOf(ValueTypeDesc.Reference("UnityEngine.Material"));
            PArray arr = (PArray)ValueParser.Parse(JToken.Parse($"[\"{mat}\", {{\"guid\": \"{mat.ToUpperInvariant()}\"}}, null]"), materials, "materials");
            Check("set_property materials/customMaterials: a material guid, {guid} and null are the accepted element shapes", arr.Items.Count == 3
                && ((PRef)arr.Items[0]).Guid == mat && ((PRef)arr.Items[1]).Guid == mat && ((PRef)arr.Items[2]).Guid == null);
            Check("a non-guid string is refused as a material reference", CodeOf(() => ValueParser.Parse(JToken.Parse("[\"my material\"]"), materials, "materials")) == ErrorCodes.InvalidParams);

            // preview image from an imported texture: set_mod_settings details previewImage = the texture's guid
            string tex = "bbbbbbbb-1111-2222-3333-444444444444";
            SettingsSection details = SettingsRules.Find("Minigame", "details");
            PreviewState state = new PreviewState();
            state.Known.Add(tex);
            SettingsPlan plan = SettingsEngine.Plan(details, JObject.Parse($"{{\"previewImage\": \"{tex}\"}}"), state);
            Check("details previewImage accepts the guid of an imported texture", plan.Assignments.Count == 1 && ((AssetValue)plan.Assignments[0].Value).Guid == tex);
            Check("details previewImage refuses a guid that is not an asset", CodeOf(() => SettingsEngine.Plan(details, JObject.Parse("{\"previewImage\": \"cccccccc-1111-2222-3333-444444444444\"}"), state)) == ErrorCodes.InvalidParams);
            Check("the music field takes an imported audio clip the same way", SettingsRules.Find("Minigame", "music") != null && SettingsEngine.Plan(SettingsRules.Find("Minigame", "music"), JObject.Parse($"{{\"Music\": \"{tex}\"}}"), state).Assignments.Count >= 1);
        }

        // ------------------------------------------------------------------ docs, registry, batch, guards, source scan

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

        private static readonly string[] WriteTools = { "import_asset", "set_import_settings", "create_material", "set_material", "remove_asset" };

        private static void DocsRegistryBatchGuards()
        {
            Console.WriteLine("-- unit: asset docs, registry, batch, guards, source scan");
            string docs = FindUp(Path.Combine("docs", "plugin-api.md"));
            Check("docs/plugin-api.md located", docs != null);
            if (docs != null)
            {
                string text = File.ReadAllText(docs).Replace("\r\n", "\n");
                int b = text.IndexOf(ImportSettingRules.DocsBegin, StringComparison.Ordinal);
                int e = text.IndexOf(ImportSettingRules.DocsEnd, StringComparison.Ordinal);
                Check("docs/plugin-api.md has the generated asset-rules block", b >= 0 && e > b);
                if (b >= 0 && e > b)
                {
                    Check("the asset-rules block equals AssetRules.ToMarkdown() (regenerate it when the tables change)", text.Substring(b, e + ImportSettingRules.DocsEnd.Length - b) == AssetRules.ToMarkdown());
                }
                Check("docs describe every asset tool, the capability and the new error codes", new[] { "get_asset", "list_asset_rules", "import_asset", "set_import_settings", "create_material", "set_material", "remove_asset", "ASSET_IN_USE", "IMPORT_FAILED", "MaxImportFileMB", "MaxModAssetsMB", "\"assets\"" }.All(text.Contains));
            }

            IReadOnlyList<RequiredMember> all = RequiredMembers.All;
            string[] needed = { "ImportFile", "RemoveAsset", "ReimportAsset", "CreateNewMaterial", "GetUniqueAssetName", "defaultShaderGuid", "m_compression", "m_filter", "m_wrapMode", "SetShader", "UpdateKeywords", "FullAssetsFolder", "GetPreviewImageGuid", "SerializeSelectedObject", "IsSelectedAssetDirty" };
            Check("the registry names every game member the asset tools call", needed.All(n => all.Any(m => m.Name == n)));
            Check("the registry checks the enums the import-setting table uses", new[] { "PummelTextureCompression", "UnityEngine.FilterMode", "UnityEngine.TextureWrapMode", "MaterialRenderingMode" }.All(t => all.Any(m => m.Kind == "enumType" && m.Type == t)));

            // batch refusal
            foreach (string tool in WriteTools)
            {
                string why = BatchRules.Refusal(tool, true);
                Check($"{tool} is refused inside run_batch with its own reason", why != null && why.Contains("outside the batch"));
            }
            Check("get_asset and list_asset_rules are not batch steps either", BatchRules.Refusal("get_asset", true) != null && BatchRules.Refusal("list_asset_rules", true) != null);

            // guard behaviour: every new write tool is declared with OpenMod (source scan) and the guard refuses a real mod
            string tools = FindUp(Path.Combine("src", "PummelPartyMcp.Plugin", "Tools", "AssetImportTools.cs"));
            string src = tools == null ? string.Empty : File.ReadAllText(tools);
            Check("AssetImportTools.cs located", tools != null);
            foreach (string tool in WriteTools)
            {
                Check($"{tool} is registered as a write tool behind WriteGuard.OpenMod()", src.Contains($"ToolDefinition.Write(\"{tool}\", WriteGuard.OpenMod()"));
            }
            Check("no asset tool is registered as a read tool by mistake", WriteTools.All(t => !src.Contains($"ToolDefinition.Read(\"{t}\"")));
            WriteSafety safety = new WriteSafety(() => true, () => true, () => GameVersionGate.Evaluate("1.14.1k", GameVersionGate.TestedVersions, string.Empty, false), new NullHost());
            EditorSnapshot real = new EditorSnapshot { EditorOpen = true, ModOpen = true, ModLoaded = true, ModName = "My Real Mod", FolderName = "My Real Mod", IsEditing = true, StateName = "Editing" };
            EditorSnapshot sandbox = new EditorSnapshot { EditorOpen = true, ModOpen = true, ModLoaded = true, ModName = "MCP_Assets", FolderName = "MCP_Assets", IsEditing = true, StateName = "Editing" };
            Check("the OpenMod guard refuses a real mod for the asset tools", WriteGuardEvaluator.Evaluate(safety, WriteGuard.OpenMod(), null, real)?.Code == ErrorCodes.GuardNotTestMod);
            Check("the OpenMod guard refuses a sandbox name with a different folder", WriteGuardEvaluator.Evaluate(safety, WriteGuard.OpenMod(), null, new EditorSnapshot { EditorOpen = true, ModOpen = true, ModLoaded = true, ModName = "MCP_Assets", FolderName = "Other", IsEditing = true, StateName = "Editing" })?.Code == ErrorCodes.GuardNotTestMod);
            Check("the OpenMod guard accepts the sandbox mod in edit mode and refuses during a playtest", WriteGuardEvaluator.Evaluate(safety, WriteGuard.OpenMod(), null, sandbox) == null
                && WriteGuardEvaluator.Evaluate(safety, WriteGuard.OpenMod(), null, new EditorSnapshot { EditorOpen = true, ModOpen = true, ModLoaded = true, ModName = "MCP_Assets", FolderName = "MCP_Assets", IsEditing = false, StateName = "Playing" })?.Code == ErrorCodes.InvalidState);
            Check("writes disabled refuses first", WriteGuardEvaluator.Evaluate(new WriteSafety(() => false, () => true, safety.GameVersion, new NullHost()), WriteGuard.OpenMod(), null, sandbox)?.Code == ErrorCodes.WritesDisabled);
            Check("an incomplete publish block refuses", WriteGuardEvaluator.Evaluate(new WriteSafety(() => true, () => false, safety.GameVersion, new NullHost()), WriteGuard.OpenMod(), null, sandbox)?.Code == ErrorCodes.PublishBlockIncomplete);
            Check("an untested game version refuses", WriteGuardEvaluator.Evaluate(new WriteSafety(() => true, () => true, () => GameVersionGate.Evaluate("9.9.9", GameVersionGate.TestedVersions, string.Empty, false), new NullHost()), WriteGuard.OpenMod(), null, sandbox)?.Code == ErrorCodes.GameVersionUntested);

            // the guard really runs through the registry for a tool declared like the asset tools
            ToolRegistry reg = new ToolRegistry(safety);
            bool ran = false;
            reg.Register(ToolDefinition.Write("import_asset_probe", WriteGuard.OpenMod(), null, inv => { ran = true; inv.Complete(1); }));
            Check("a write tool declared like the asset tools is registered as write", reg.TryGet("import_asset_probe", out RegisteredTool rt) && rt.Definition.IsWrite && rt.Definition.Guard.Kind == WriteGuardKind.OpenMod && !ran);

            // forbidden calls in the asset code: never the editor's AddAsset/copy paths, deletes only through the core's rollback or RemoveAsset
            string plugin = FindUp(Path.Combine("src", "PummelPartyMcp.Plugin"));
            string[] files = { "Tools/AssetImportTools.cs", "Game/AssetAccess.cs" };
            foreach (string f in files)
            {
                string t = File.ReadAllText(Path.Combine(plugin, f.Replace('/', Path.DirectorySeparatorChar)));
                Check($"{f} never calls AddAsset / AddDuplicatedAsset / ImportFileDialogCallback", !t.Contains(".AddAsset(") && !t.Contains("AddDuplicatedAsset") && !t.Contains("ImportFileDialogCallback"));
                Check($"{f} has no direct File.Delete / File.Move / File.Copy / Directory.Delete", !t.Contains("File.Delete") && !t.Contains("File.Move") && !t.Contains("File.Copy") && !t.Contains("Directory.Delete") && !t.Contains("File.WriteAll"));
            }
            string tt = File.ReadAllText(Path.Combine(plugin, "Tools", "AssetImportTools.cs"));
            Check("ImportFile is called exactly once (on the file inside the mod)", tt.Split(new[] { ".ImportFile(" }, StringSplitOptions.None).Length == 2);
            Check("RemoveAsset is only called after the path checks (3 call sites: remove, import cleanup, material cleanup)", tt.Split(new[] { ".RemoveAsset(" }, StringSplitOptions.None).Length == 4);
            string core = File.ReadAllText(Path.Combine(plugin, "Core", "AssetImportCore.cs"));
            Check("the core copies with CreateNew and never with an overwriting mode", core.Contains("FileMode.CreateNew") && !core.Contains("FileMode.Create,") && !core.Contains("FileMode.Truncate") && !core.Contains("File.Copy") && !core.Contains("File.Replace")
                && core.Split(new[] { "File.Move(" }, StringSplitOptions.None).Length == 2 && core.Contains("File.Move(staging, dest.FullPath); // fails if")); // M1: the one rename of the checked staging copy (non-overwriting)
            Check("the core opens the source read-only with sharing", core.Contains("FileAccess.Read, FileShare.ReadWrite | FileShare.Delete"));
            Check("the core deletes only in Rollback", core.Split(new[] { "File.Delete(" }, StringSplitOptions.None).Length == 2 && !core.Contains("Directory.Delete"));

            // untouched safety code
            Check("TestModName prefix is still MCP_", TestModName.Prefix == "MCP_");
        }

        private sealed class NullHost : IEditorGuardHost
        {
            public EditorSnapshot Capture() => null;

            public void EnforceDefaultWorkshopId()
            {
            }
        }
    }
}
