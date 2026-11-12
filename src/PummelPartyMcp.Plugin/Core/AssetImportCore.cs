using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace PummelPartyMcp.Core
{
    public enum AssetImportKind
    {
        Texture,
        Mesh,
        Audio,
    }

    /// <summary>Size limits from the plugin config ([Assets] MaxImportFileMB, MaxModAssetsMB), re-read on every call.</summary>
    public sealed class AssetLimits
    {
        public const int DefaultMaxImportFileMB = 50;
        public const int DefaultMaxModAssetsMB = 500;

        public long MaxImportFileBytes { get; }
        public long MaxModAssetsBytes { get; }

        public AssetLimits(int maxImportFileMB, int maxModAssetsMB)
        {
            MaxImportFileBytes = Math.Max(1, maxImportFileMB) * 1024L * 1024L;
            MaxModAssetsBytes = Math.Max(1, maxModAssetsMB) * 1024L * 1024L;
        }

        public static AssetLimits Default => new AssetLimits(DefaultMaxImportFileMB, DefaultMaxModAssetsMB);

        public int MaxImportFileMB => (int)(MaxImportFileBytes / (1024L * 1024L));
        public int MaxModAssetsMB => (int)(MaxModAssetsBytes / (1024L * 1024L));
    }

    /// <summary>What the cheap pre-copy checks found out about a source file.</summary>
    public sealed class ImportSource
    {
        public string FullPath;
        public string Extension; // lower case, with dot
        public AssetImportKind Kind;
        public long Length;
        public string StemRaw;
        public int Width;
        public int Height;
        public int VertexLines;
        public List<string> Checks = new List<string>();
    }

    /// <summary>The destination inside the open sandbox mod: always Assets\{Textures|Props|Audio}\name.ext.</summary>
    public sealed class ImportDestination
    {
        public string AssetsFolder;   // full path, no trailing separator
        public string SubFolder;      // Textures / Props / Audio
        public string Stem;
        public string Extension;      // lower case, with dot
        public string FullPath;
        public string MetaPath;
        public int Suffix;            // 0 = the plain name, n = " (n)"
    }

    /// <summary>
    /// Everything the import tool decides without touching the game: allow-list, names, destination path, source checks,
    /// content sniffing, copy and rollback. Pure .NET (no Unity); unit tested against real temp directories.
    /// </summary>
    public static class AssetImportRules
    {
        public const int MaxStemLength = 64;
        public const int MaxSourcePathLength = 259;
        public const int MaxTextureSide = 8192;
        public const int MaxObjVertexLines = 500000;
        public const int MaxDuplicateSuffix = 20;
        public const string MetaExtension = ".pmeta";

        public static readonly string[] AllowedExtensions = { ".png", ".jpg", ".jpeg", ".obj", ".wav", ".mp3", ".ogg" };

        private static readonly string[] s_reserved =
        {
            "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9", "CONIN$", "CONOUT$", "CLOCK$",
        };

        public static string ExtensionsText => string.Join(", ", AllowedExtensions);

        public static bool TryKind(string extensionLower, out AssetImportKind kind)
        {
            switch (extensionLower)
            {
                case ".png":
                case ".jpg":
                case ".jpeg":
                    kind = AssetImportKind.Texture;
                    return true;
                case ".obj":
                    kind = AssetImportKind.Mesh;
                    return true;
                case ".wav":
                case ".mp3":
                case ".ogg":
                    kind = AssetImportKind.Audio;
                    return true;
                default:
                    kind = AssetImportKind.Texture;
                    return false;
            }
        }

        /// <summary>The fixed sub-folder of the mod's Assets folder the editor's Import Asset uses for the type.</summary>
        public static string SubFolderOf(AssetImportKind kind)
        {
            switch (kind)
            {
                case AssetImportKind.Texture: return "Textures";
                case AssetImportKind.Mesh: return "Props";
                default: return "Audio";
            }
        }

        public static string TypeNameOf(AssetImportKind kind)
        {
            switch (kind)
            {
                case AssetImportKind.Texture: return "Texture";
                case AssetImportKind.Mesh: return "Prop";
                default: return "AudioClip";
            }
        }

        // ------------------------------------------------------------------ names

        public static bool IsReservedDeviceName(string nameOrFile)
        {
            if (nameOrFile == null)
            {
                return false;
            }
            int dot = nameOrFile.IndexOf('.');
            string stem = (dot >= 0 ? nameOrFile.Substring(0, dot) : nameOrFile).TrimEnd(' ');
            return s_reserved.Any(r => string.Equals(r, stem, StringComparison.OrdinalIgnoreCase));
        }

        private static bool NameChar(char c) =>
            (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == ' ' || c == '_' || c == '.' || c == '(' || c == ')' || c == '-';

        /// <summary>Null when <paramref name="name"/> is an acceptable asset name (the file stem); otherwise why not.</summary>
        public static string ValidateName(string name)
        {
            if (name == null || name.Length == 0)
            {
                return "is empty";
            }
            if (name.Length > MaxStemLength)
            {
                return $"is longer than {MaxStemLength} characters";
            }
            for (int i = 0; i < name.Length; i++)
            {
                char c = name[i];
                if (c < ' ' || c == 127)
                {
                    return "contains a control character";
                }
                if (c == '/' || c == '\\' || c == ':')
                {
                    return "contains a path separator or drive letter (the destination folder is fixed; pass a plain name)";
                }
                if (!NameChar(c))
                {
                    return "may only contain A-Z, a-z, 0-9, space, '_', '.', '(', ')' and '-'";
                }
            }
            if (name.Contains(".."))
            {
                return "contains '..'";
            }
            if (name[0] == ' ' || name[0] == '.' || name[name.Length - 1] == ' ' || name[name.Length - 1] == '.')
            {
                return "starts or ends with a space or a dot";
            }
            if (IsReservedDeviceName(name))
            {
                return "is a reserved Windows device name";
            }
            return null;
        }

        /// <summary>A usable stem from an arbitrary source file name (disallowed characters become '_'), never empty.</summary>
        public static string DeriveStem(string sourceStem)
        {
            StringBuilder sb = new StringBuilder();
            foreach (char c in sourceStem ?? string.Empty)
            {
                sb.Append(NameChar(c) ? c : '_');
            }
            string s = sb.ToString().Replace("..", "_").Trim(' ', '.');
            if (s.Length > MaxStemLength)
            {
                s = s.Substring(0, MaxStemLength).Trim(' ', '.');
            }
            if (s.Length == 0 || IsReservedDeviceName(s))
            {
                s = "asset";
            }
            return s;
        }

        // ------------------------------------------------------------------ mod folder and destination

        private static string WithSeparator(string full) => full.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;

        public static bool IsStrictlyUnder(string folder, string path)
        {
            if (string.IsNullOrEmpty(folder) || string.IsNullOrEmpty(path))
            {
                return false;
            }
            try
            {
                string f = WithSeparator(Path.GetFullPath(folder));
                string p = Path.GetFullPath(path);
                return p.Length > f.Length && p.StartsWith(f, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static bool IsLocalDrivePath(string full) =>
            full != null && full.Length >= 3 && ((full[0] >= 'A' && full[0] <= 'Z') || (full[0] >= 'a' && full[0] <= 'z')) && full[1] == ':' && (full[2] == '\\' || full[2] == '/');

        /// <summary>
        /// Takes the open mod's root folder and its Assets folder (both from the game object) and re-checks them: local drive path,
        /// root folder name passes the sandbox rule, Assets folder is exactly root\Assets and not a link. Returns the full Assets folder.
        /// </summary>
        public static string ValidateAssetsFolder(string modRoot, string assetsFolder)
        {
            if (string.IsNullOrEmpty(modRoot) || string.IsNullOrEmpty(assetsFolder))
            {
                throw RpcException.Internal("the open mod's folder is not known");
            }
            if (!IsLocalDrivePath(modRoot) || !IsLocalDrivePath(assetsFolder))
            {
                throw new RpcException(ErrorCodes.InvalidState, "the open mod's folder is not an absolute local drive path; assets are not imported there");
            }
            string rootFull, assetsFull;
            try
            {
                rootFull = Path.GetFullPath(modRoot).TrimEnd('\\', '/');
                assetsFull = Path.GetFullPath(assetsFolder).TrimEnd('\\', '/');
            }
            catch (Exception e)
            {
                throw RpcException.Internal("the open mod's folder path is invalid: " + e.Message);
            }
            if (!IsLocalDrivePath(rootFull + "\\"))
            {
                throw new RpcException(ErrorCodes.InvalidState, "the open mod's folder is not on a local drive; assets are not imported there");
            }
            string leaf = Path.GetFileName(rootFull);
            string why = TestModName.Validate(leaf);
            if (why != null)
            {
                throw new RpcException(ErrorCodes.GuardNotTestMod, $"the open mod's folder '{leaf}' is not a sandbox mod folder: it {why}");
            }
            if (!string.Equals(assetsFull, rootFull + "\\Assets", StringComparison.OrdinalIgnoreCase))
            {
                throw RpcException.Internal("the mod's Assets folder is not <mod folder>\\Assets; refusing to write there");
            }
            if (Directory.Exists(assetsFull) && (new DirectoryInfo(assetsFull).Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new RpcException(ErrorCodes.InvalidState, "the mod's Assets folder is a link (junction/symlink); refusing to write through it");
            }
            return assetsFull;
        }

        /// <summary>
        /// The destination path for (kind, stem, extension): Assets\Sub\stem.ext, verified after normalisation to be exactly one folder
        /// below Assets. Pure string work (no file system access).
        /// </summary>
        public static string BuildDestinationPath(string assetsFull, AssetImportKind kind, string stem, string extensionLower)
        {
            string why = ValidateName(stem);
            if (why != null)
            {
                throw RpcException.InvalidParams($"asset name '{Truncate(stem)}' {why}");
            }
            if (!AllowedExtensions.Contains(extensionLower) || !TryKind(extensionLower, out AssetImportKind k) || k != kind)
            {
                throw RpcException.InvalidParams($"extension '{Truncate(extensionLower)}' is not an importable {TypeNameOf(kind)} type (allowed: {ExtensionsText})");
            }
            string sub = SubFolderOf(kind);
            string combined;
            try
            {
                combined = Path.GetFullPath(Path.Combine(assetsFull, sub, stem + extensionLower));
            }
            catch (Exception e)
            {
                throw RpcException.InvalidParams("the destination path is invalid: " + e.Message);
            }
            string expectedFolder = WithSeparator(Path.GetFullPath(assetsFull)) + sub + Path.DirectorySeparatorChar;
            if (!combined.StartsWith(expectedFolder, StringComparison.OrdinalIgnoreCase))
            {
                throw RpcException.InvalidParams("the destination would not be inside the mod's Assets\\" + sub + " folder");
            }
            string rest = combined.Substring(expectedFolder.Length);
            if (rest.Length == 0 || rest.IndexOfAny(new[] { '\\', '/', ':' }) >= 0 || !string.Equals(rest, stem + extensionLower, StringComparison.OrdinalIgnoreCase))
            {
                throw RpcException.InvalidParams("the destination would not be a plain file name directly in Assets\\" + sub);
            }
            return combined;
        }

        private static string Truncate(string s) => s == null ? "<null>" : s.Length <= 40 ? s : s.Substring(0, 40) + "...";

        /// <summary>Neither the file nor its .pmeta (nor anything else of that name) exists.</summary>
        public static bool IsFree(string path) =>
            !File.Exists(path) && !Directory.Exists(path) && !File.Exists(path + MetaExtension) && !Directory.Exists(path + MetaExtension);

        /// <summary>First free name: stem, then "stem (1)" ... "stem (MaxDuplicateSuffix)". Refuses (ALREADY_EXISTS) when all are taken.</summary>
        public static ImportDestination ChooseFree(string assetsFull, AssetImportKind kind, string stem, string extensionLower, int firstSuffix = 0, Func<string, bool> isFree = null)
        {
            isFree = isFree ?? IsFree;
            for (int n = firstSuffix; n <= MaxDuplicateSuffix; n++)
            {
                string candidate = n == 0 ? stem : stem + " (" + n + ")";
                string path = BuildDestinationPath(assetsFull, kind, candidate, extensionLower);
                if (isFree(path))
                {
                    return new ImportDestination
                    {
                        AssetsFolder = assetsFull,
                        SubFolder = SubFolderOf(kind),
                        Stem = candidate,
                        Extension = extensionLower,
                        FullPath = path,
                        MetaPath = path + MetaExtension,
                        Suffix = n,
                    };
                }
            }
            throw new RpcException(ErrorCodes.AlreadyExists,
                $"'{stem}{extensionLower}' and its numbered variants up to ({MaxDuplicateSuffix}) already exist in Assets\\{SubFolderOf(kind)}; pass a different 'name' (nothing was copied)");
        }

        // ------------------------------------------------------------------ source

        /// <summary>
        /// Validates the source path text (no file system): absolute local drive path only. UNC (\\server\share), device (\\.\, \\?\) and
        /// alternate-data-stream paths are refused in this version (the game's audio and OBJ loaders are unverified on them).
        /// </summary>
        public static string CheckSourcePathText(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return "'path' is empty";
            }
            if (raw.Length > MaxSourcePathLength)
            {
                return $"the path is longer than {MaxSourcePathLength} characters (not supported)";
            }
            foreach (char c in raw)
            {
                if (c < ' ')
                {
                    return "the path contains a control character";
                }
            }
            if (raw.StartsWith("\\\\", StringComparison.Ordinal) || raw.StartsWith("//", StringComparison.Ordinal))
            {
                return "network (UNC) and device paths (\\\\server\\share, \\\\.\\, \\\\?\\) are not supported in this version: copy the file to a local drive first";
            }
            if (!IsLocalDrivePath(raw))
            {
                return "the path must be an absolute local path such as C:\\folder\\file.png";
            }
            if (raw.EndsWith(".", StringComparison.Ordinal) || raw.EndsWith(" ", StringComparison.Ordinal))
            {
                return "the path ends with a dot or a space (Windows would change it)";
            }
            if (raw.IndexOf(':', 2) >= 0)
            {
                return "the path contains ':' after the drive letter (alternate data streams are not supported)";
            }
            if (raw.IndexOfAny(new[] { '*', '?', '<', '>', '|', '"' }) >= 0)
            {
                return "the path contains a wildcard or an invalid character";
            }
            return null;
        }

        public static RpcException Refuse(string message) => RpcException.InvalidParams(message);

        /// <summary>
        /// All checks that run before anything is copied: path text, regular local file, allow-listed extension, size, not inside the
        /// mod's own assets, content sniffing, texture/OBJ sanity caps. The file is opened read-only with full sharing and closed again.
        /// </summary>
        public static ImportSource CheckSource(string raw, string ownAssetsFolder, AssetLimits limits)
        {
            string text = CheckSourcePathText(raw);
            if (text != null)
            {
                throw Refuse(text);
            }
            string full;
            try
            {
                full = Path.GetFullPath(raw);
            }
            catch (Exception e)
            {
                throw Refuse("the path is invalid: " + e.Message);
            }
            if (!IsLocalDrivePath(full) || full.StartsWith("\\\\", StringComparison.Ordinal))
            {
                throw Refuse("the path does not resolve to a local drive path");
            }
            string leaf = Path.GetFileName(full);
            if (string.IsNullOrEmpty(leaf) || IsReservedDeviceName(leaf) || leaf.EndsWith(".", StringComparison.Ordinal) || leaf.EndsWith(" ", StringComparison.Ordinal))
            {
                throw Refuse("the file name is empty, a reserved device name, or ends with a dot or space");
            }
            try
            {
                if (new DriveInfo(full.Substring(0, 1)).DriveType == DriveType.Network)
                {
                    throw Refuse("the file is on a network drive; network paths are not supported in this version: copy the file to a local drive first");
                }
            }
            catch (RpcException)
            {
                throw;
            }
            catch (Exception)
            {
                throw Refuse("the drive of the path cannot be identified; use a file on a local drive");
            }

            string ext = Path.GetExtension(full).ToLowerInvariant();
            if (!TryKind(ext, out AssetImportKind kind))
            {
                string hint = ext == ".bmp" ? " (.bmp: the editor offers it in a menu but has no importer for it; convert to .png)" : string.Empty;
                throw Refuse($"'{ext}' files cannot be imported (allowed: {ExtensionsText}){hint}");
            }

            FileInfo fi = new FileInfo(full);
            if (Directory.Exists(full))
            {
                throw Refuse("the path is a folder, not a file");
            }
            if (!fi.Exists)
            {
                throw RpcException.NotFound("the file does not exist or is not readable");
            }
            if ((fi.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Device | FileAttributes.Directory)) != 0)
            {
                throw Refuse("the path is not a regular file (links, junctions and devices are not supported)");
            }
            if (ownAssetsFolder != null && IsStrictlyUnder(ownAssetsFolder, full))
            {
                throw Refuse("the file is already inside the open mod's Assets folder: nothing to import");
            }
            long len = fi.Length;
            if (len == 0)
            {
                throw Refuse("the file is empty (0 bytes)");
            }
            if (len > limits.MaxImportFileBytes)
            {
                throw Refuse($"the file is {Mb(len)} MB; the limit is {limits.MaxImportFileMB} MB ([Assets] MaxImportFileMB in the plugin config)");
            }

            ImportSource s = new ImportSource { FullPath = full, Extension = ext, Kind = kind, Length = len, StemRaw = Path.GetFileNameWithoutExtension(full) };
            s.Checks.Add("regular local file");
            s.Checks.Add($"extension {ext} is on the allow-list ({ExtensionsText})");
            s.Checks.Add($"size {len} bytes <= {limits.MaxImportFileMB} MB");
            Sniff(s);
            return s;
        }

        private static string Mb(long bytes) => (bytes / (1024.0 * 1024.0)).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);

        // ------------------------------------------------------------------ content sniffing

        private const int PrefixBytes = 1024 * 1024;

        private static void Sniff(ImportSource s)
        {
            byte[] prefix;
            try
            {
                using (FileStream fs = new FileStream(s.FullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    int want = (int)Math.Min(PrefixBytes, fs.Length);
                    prefix = new byte[want];
                    int got = 0;
                    while (got < want)
                    {
                        int n = fs.Read(prefix, got, want - got);
                        if (n <= 0)
                        {
                            break;
                        }
                        got += n;
                    }
                    if (got != want)
                    {
                        Array.Resize(ref prefix, got);
                    }
                }
            }
            catch (Exception e) when (!(e is RpcException))
            {
                throw Refuse("the file cannot be read: " + e.Message);
            }

            switch (s.Extension)
            {
                case ".png":
                    if (!TryPngSize(prefix, out int pw, out int ph))
                    {
                        throw Refuse("the file does not look like a PNG image (wrong signature); it was not copied");
                    }
                    CheckDims(s, pw, ph);
                    s.Checks.Add("PNG signature and header");
                    break;
                case ".jpg":
                case ".jpeg":
                    if (!IsJpeg(prefix))
                    {
                        throw Refuse("the file does not look like a JPEG image (wrong signature); it was not copied");
                    }
                    if (!TryJpegSize(prefix, out int jw, out int jh))
                    {
                        throw Refuse("the JPEG header could not be read (size unknown); it was not copied");
                    }
                    CheckDims(s, jw, jh);
                    s.Checks.Add("JPEG signature and header");
                    break;
                case ".wav":
                    if (!IsWav(prefix))
                    {
                        throw Refuse("the file does not look like a WAV file (no RIFF/WAVE header); it was not copied");
                    }
                    s.Checks.Add("RIFF/WAVE header");
                    break;
                case ".ogg":
                    if (!IsOgg(prefix))
                    {
                        throw Refuse("the file does not look like an Ogg file (no OggS header); it was not copied");
                    }
                    s.Checks.Add("OggS header");
                    break;
                case ".mp3":
                    if (!IsMp3(prefix))
                    {
                        throw Refuse("the file does not look like an MP3 file (no ID3 tag or frame sync); it was not copied");
                    }
                    s.Checks.Add("MP3 ID3 tag or frame sync");
                    break;
                case ".obj":
                    if (!IsTextWithoutNul(prefix, 8192))
                    {
                        throw Refuse("the file does not look like a text OBJ file (binary content in the first block); it was not copied");
                    }
                    s.VertexLines = CountVertexLines(s.FullPath);
                    if (s.VertexLines == 0)
                    {
                        throw Refuse("the OBJ file has no vertex ('v ') lines; it was not copied");
                    }
                    if (s.VertexLines > MaxObjVertexLines)
                    {
                        throw Refuse($"the OBJ file has more than {MaxObjVertexLines} vertex lines (limit of this plugin; the editor has none)");
                    }
                    s.Checks.Add($"text OBJ with {s.VertexLines} vertex lines (<= {MaxObjVertexLines})");
                    break;
            }
            if (s.Kind == AssetImportKind.Audio)
            {
                s.Checks.Add("audio file size within the limit (the game decodes audio on the main thread and waits for it)");
            }
        }

        private static void CheckDims(ImportSource s, int w, int h)
        {
            if (w < 1 || h < 1)
            {
                throw Refuse("the image has no size");
            }
            if (w > MaxTextureSide || h > MaxTextureSide)
            {
                throw Refuse($"the image is {w}x{h}; the limit of this plugin is {MaxTextureSide} per side (the editor has none)");
            }
            s.Width = w;
            s.Height = h;
            s.Checks.Add($"image size {w}x{h} <= {MaxTextureSide} per side");
        }

        public static bool TryPngSize(byte[] b, out int w, out int h)
        {
            w = h = 0;
            if (b == null || b.Length < 24)
            {
                return false;
            }
            byte[] sig = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
            for (int i = 0; i < 8; i++)
            {
                if (b[i] != sig[i])
                {
                    return false;
                }
            }
            if (b[12] != (byte)'I' || b[13] != (byte)'H' || b[14] != (byte)'D' || b[15] != (byte)'R')
            {
                return false;
            }
            w = (b[16] << 24) | (b[17] << 16) | (b[18] << 8) | b[19];
            h = (b[20] << 24) | (b[21] << 16) | (b[22] << 8) | b[23];
            return w > 0 && h > 0;
        }

        public static bool IsJpeg(byte[] b) => b != null && b.Length >= 4 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF;

        public static bool TryJpegSize(byte[] b, out int w, out int h)
        {
            w = h = 0;
            if (!IsJpeg(b))
            {
                return false;
            }
            int i = 2;
            while (i + 3 < b.Length)
            {
                if (b[i] != 0xFF)
                {
                    i++;
                    continue;
                }
                int marker = b[i + 1];
                if (marker == 0xFF)
                {
                    i++;
                    continue;
                }
                if (marker == 0xD8 || marker == 0x01 || (marker >= 0xD0 && marker <= 0xD7))
                {
                    i += 2;
                    continue;
                }
                int len = (b[i + 2] << 8) | b[i + 3];
                if (len < 2)
                {
                    return false;
                }
                bool sof = marker >= 0xC0 && marker <= 0xCF && marker != 0xC4 && marker != 0xC8 && marker != 0xCC;
                if (sof)
                {
                    if (i + 8 >= b.Length)
                    {
                        return false;
                    }
                    h = (b[i + 5] << 8) | b[i + 6];
                    w = (b[i + 7] << 8) | b[i + 8];
                    return w > 0 && h > 0;
                }
                i += 2 + len;
            }
            return false;
        }

        public static bool IsWav(byte[] b) =>
            b != null && b.Length >= 12 && b[0] == 'R' && b[1] == 'I' && b[2] == 'F' && b[3] == 'F' && b[8] == 'W' && b[9] == 'A' && b[10] == 'V' && b[11] == 'E';

        public static bool IsOgg(byte[] b) => b != null && b.Length >= 4 && b[0] == 'O' && b[1] == 'g' && b[2] == 'g' && b[3] == 'S';

        public static bool IsMp3(byte[] b)
        {
            if (b == null || b.Length < 3)
            {
                return false;
            }
            if (b[0] == 'I' && b[1] == 'D' && b[2] == '3')
            {
                return true;
            }
            return b[0] == 0xFF && (b[1] & 0xE0) == 0xE0;
        }

        public static bool IsTextWithoutNul(byte[] b, int block)
        {
            if (b == null || b.Length == 0)
            {
                return false;
            }
            int n = Math.Min(block, b.Length);
            for (int i = 0; i < n; i++)
            {
                if (b[i] == 0)
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>Counts lines starting with "v " (or "v" + tab) in the whole file, stopping early past the cap.</summary>
        public static int CountVertexLines(string path)
        {
            int count = 0;
            int state = 0; // 0 line start, 1 saw 'v' at line start, 2 elsewhere in a line
            byte[] buf = new byte[64 * 1024];
            using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                int n;
                while ((n = fs.Read(buf, 0, buf.Length)) > 0)
                {
                    for (int i = 0; i < n; i++)
                    {
                        byte c = buf[i];
                        if (c == (byte)'\n' || c == (byte)'\r')
                        {
                            state = 0;
                            continue;
                        }
                        if (state == 0)
                        {
                            state = c == (byte)'v' ? 1 : 2;
                        }
                        else if (state == 1)
                        {
                            if (c == (byte)' ' || c == (byte)'\t')
                            {
                                count++;
                                if (count > MaxObjVertexLines)
                                {
                                    return count;
                                }
                            }
                            state = 2;
                        }
                    }
                }
            }
            return count;
        }

        // ------------------------------------------------------------------ size of the mod's assets

        public static long FolderSize(string folder)
        {
            long total = 0;
            if (!Directory.Exists(folder))
            {
                return 0;
            }
            Stack<string> stack = new Stack<string>();
            stack.Push(folder);
            while (stack.Count > 0)
            {
                string dir = stack.Pop();
                try
                {
                    foreach (string f in Directory.GetFiles(dir))
                    {
                        try
                        {
                            total += new FileInfo(f).Length;
                        }
                        catch (Exception)
                        {
                            // vanished while counting
                        }
                    }
                    foreach (string d in Directory.GetDirectories(dir))
                    {
                        if ((new DirectoryInfo(d).Attributes & FileAttributes.ReparsePoint) == 0)
                        {
                            stack.Push(d);
                        }
                    }
                }
                catch (Exception)
                {
                    // unreadable folder: counted as empty
                }
            }
            return total;
        }

        /// <summary>Null when adding <paramref name="addBytes"/> keeps the mod's Assets folder within the limit.</summary>
        public static string CheckModTotal(string assetsFull, long addBytes, AssetLimits limits)
        {
            long current = FolderSize(assetsFull);
            if (current + addBytes > limits.MaxModAssetsBytes)
            {
                return $"the mod's Assets folder would be {Mb(current + addBytes)} MB after the import; the limit is {limits.MaxModAssetsMB} MB ([Assets] MaxModAssetsMB in the plugin config)";
            }
            return null;
        }

        // ------------------------------------------------------------------ copy and rollback

        /// <summary>Creates the sub-folder (refusing a file or a link at that place) and returns its path.</summary>
        public static string EnsureSubFolder(string assetsFull, AssetImportKind kind)
        {
            if (!Directory.Exists(assetsFull))
            {
                Directory.CreateDirectory(assetsFull);
            }
            string sub = Path.Combine(assetsFull, SubFolderOf(kind));
            if (File.Exists(sub))
            {
                throw new RpcException(ErrorCodes.InvalidState, $"'Assets\\{SubFolderOf(kind)}' exists as a file; nothing was copied");
            }
            if (Directory.Exists(sub))
            {
                if ((new DirectoryInfo(sub).Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new RpcException(ErrorCodes.InvalidState, $"'Assets\\{SubFolderOf(kind)}' is a link (junction/symlink); refusing to write through it");
                }
            }
            else
            {
                Directory.CreateDirectory(sub);
            }
            return sub;
        }

        /// <summary>What this call created, so a failure can remove exactly that and nothing else.</summary>
        public sealed class Created
        {
            public ImportDestination Destination;
            public bool FileCreated;
            public long Bytes;
            public string Sha256;
        }

        public sealed class CleanupReport
        {
            public List<string> Deleted = new List<string>();
            public List<string> Problems = new List<string>();
        }

        /// <summary>
        /// Copies the source into the mod: free name chosen, file created with CreateNew (fails if it appears in between: the next
        /// free name is tried, bounded), source read-only with full sharing, never modified. <paramref name="beforeCreate"/> is a test
        /// hook called right before each create attempt with the target path.
        /// </summary>
        public static Created CopyIntoMod(ImportSource source, string assetsFull, string stem, Action<string> beforeCreate = null)
        {
            EnsureSubFolder(assetsFull, source.Kind);
            int from = 0;
            for (int attempt = 0; attempt <= MaxDuplicateSuffix + 1; attempt++)
            {
                ImportDestination dest = ChooseFree(assetsFull, source.Kind, stem, source.Extension, from);
                beforeCreate?.Invoke(dest.FullPath);
                if (!IsFree(dest.FullPath))
                {
                    from = dest.Suffix + 1; // appeared between the check and now
                    continue;
                }
                Created created = new Created { Destination = dest };
                FileStream outStream = null;
                try
                {
                    try
                    {
                        outStream = new FileStream(dest.FullPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    }
                    catch (IOException) when (File.Exists(dest.FullPath) || Directory.Exists(dest.FullPath))
                    {
                        from = dest.Suffix + 1; // lost the race: not ours, nothing to clean
                        continue;
                    }
                    created.FileCreated = true;
                    using (SHA256 sha = SHA256.Create())
                    using (FileStream inStream = new FileStream(source.FullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    {
                        byte[] buf = new byte[81920];
                        long total = 0;
                        int n;
                        while ((n = inStream.Read(buf, 0, buf.Length)) > 0)
                        {
                            outStream.Write(buf, 0, n);
                            sha.TransformBlock(buf, 0, n, null, 0);
                            total += n;
                            if (total > source.Length)
                            {
                                throw new IOException("the source file grew while it was being copied");
                            }
                        }
                        sha.TransformFinalBlock(new byte[0], 0, 0);
                        if (total != source.Length)
                        {
                            throw new IOException("the source file changed size while it was being copied");
                        }
                        created.Bytes = total;
                        created.Sha256 = BitConverter.ToString(sha.Hash).Replace("-", string.Empty).ToLowerInvariant();
                    }
                    outStream.Flush();
                    outStream.Dispose();
                    outStream = null;
                    return created;
                }
                catch (Exception e)
                {
                    outStream?.Dispose();
                    outStream = null;
                    CleanupReport report = Rollback(created);
                    throw new RpcException(ErrorCodes.Internal,
                        $"copying failed ({e.GetType().Name}: {e.Message}); removed {(report.Deleted.Count == 0 ? "nothing" : string.Join(", ", report.Deleted.ToArray()))}; the source file was not touched");
                }
            }
            throw new RpcException(ErrorCodes.AlreadyExists, "could not find a free file name (files kept appearing); nothing was copied");
        }

        /// <summary>
        /// Deletes only what this call created: the copied file (if the call created it) and the .pmeta next to it (it did not exist
        /// before the copy). Each path is re-checked to be exactly Assets\Sub\name and strictly inside the Assets folder first.
        /// </summary>
        public static CleanupReport Rollback(Created created)
        {
            CleanupReport report = new CleanupReport();
            if (created == null || created.Destination == null || !created.FileCreated)
            {
                return report;
            }
            ImportDestination d = created.Destination;
            foreach (string p in new[] { d.FullPath, d.MetaPath })
            {
                try
                {
                    string expectedDir = Path.Combine(d.AssetsFolder, d.SubFolder);
                    string full = Path.GetFullPath(p);
                    bool inside = IsStrictlyUnder(d.AssetsFolder, full)
                                  && string.Equals(Path.GetDirectoryName(full), Path.GetFullPath(expectedDir), StringComparison.OrdinalIgnoreCase);
                    if (!inside)
                    {
                        report.Problems.Add("not deleted (not inside the mod's Assets folder): " + p);
                        continue;
                    }
                    if (File.Exists(full))
                    {
                        File.SetAttributes(full, FileAttributes.Normal);
                        File.Delete(full);
                        report.Deleted.Add(Path.GetFileName(full));
                    }
                }
                catch (Exception e)
                {
                    report.Problems.Add($"could not delete {Path.GetFileName(p)}: {e.Message}");
                }
            }
            return report;
        }
    }
}
