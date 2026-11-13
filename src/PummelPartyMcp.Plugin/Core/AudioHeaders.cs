using System;
using System.IO;

namespace PummelPartyMcp.Core
{
    /// <summary>Duration of an audio file as read from its header; Seconds null when the header does not tell it.</summary>
    public sealed class AudioDuration
    {
        public double? Seconds;

        /// <summary>MP3 without a frame count: the duration a constant bitrate would give (refuses above the limit; Seconds stays null).</summary>
        public double? CbrEstimateSeconds;

        /// <summary>How it was determined (or why not), for the reply's checks.</summary>
        public string Method;
    }

    /// <summary>
    /// Review M1: reads the duration of a WAV (fmt byte rate + data chunk size), OGG Vorbis/Opus (identification header sample rate +
    /// granule position of the last page) or MP3 (Xing/Info or VBRI frame count; otherwise only a constant-bitrate estimate) from the
    /// file headers, never decoding it. Pure .NET; tested with generated files.
    /// </summary>
    public static class AudioHeaders
    {
        public static AudioDuration Read(string path, string extensionLower)
        {
            try
            {
                switch (extensionLower)
                {
                    case ".wav": return Wav(path);
                    case ".ogg": return Ogg(path);
                    case ".mp3": return Mp3(path);
                    default: return new AudioDuration { Method = "not an audio type" };
                }
            }
            catch (Exception e)
            {
                return new AudioDuration { Method = "header unreadable (" + e.Message + ")" };
            }
        }

        private static uint U32(byte[] b, int o) => (uint)(b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24));

        private static uint U32Be(byte[] b, int o) => (uint)((b[o] << 24) | (b[o + 1] << 16) | (b[o + 2] << 8) | b[o + 3]);

        private static FileStream Open(string path) => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

        private static byte[] ReadAt(FileStream fs, long offset, int count)
        {
            fs.Seek(offset, SeekOrigin.Begin);
            byte[] b = new byte[Math.Max(0, count)];
            int got = 0;
            while (got < b.Length)
            {
                int n = fs.Read(b, got, b.Length - got);
                if (n <= 0)
                {
                    break;
                }
                got += n;
            }
            if (got != b.Length)
            {
                Array.Resize(ref b, got);
            }
            return b;
        }

        private static bool Ascii(byte[] b, int o, string s)
        {
            if (o < 0 || o + s.Length > b.Length)
            {
                return false;
            }
            for (int i = 0; i < s.Length; i++)
            {
                if (b[o + i] != (byte)s[i])
                {
                    return false;
                }
            }
            return true;
        }

        public static AudioDuration Wav(string path)
        {
            using (FileStream fs = Open(path))
            {
                long len = fs.Length;
                byte[] head = ReadAt(fs, 0, 12);
                if (!Ascii(head, 0, "RIFF") || !Ascii(head, 8, "WAVE"))
                {
                    return new AudioDuration { Method = "no RIFF/WAVE header" };
                }
                long pos = 12;
                uint byteRate = 0;
                long dataSize = -1;
                for (int guard = 0; guard < 1000 && pos + 8 <= len; guard++)
                {
                    byte[] ch = ReadAt(fs, pos, 8);
                    if (ch.Length < 8)
                    {
                        break;
                    }
                    long size = U32(ch, 4);
                    if (Ascii(ch, 0, "fmt "))
                    {
                        byte[] fmt = ReadAt(fs, pos + 8, 16);
                        if (fmt.Length >= 12)
                        {
                            byteRate = U32(fmt, 8);
                        }
                    }
                    else if (Ascii(ch, 0, "data"))
                    {
                        dataSize = Math.Min(size, len - (pos + 8)); // a streaming writer may leave 0xFFFFFFFF: then the rest of the file
                        break;
                    }
                    pos += 8 + size + (size & 1);
                }
                if (byteRate == 0 || dataSize < 0)
                {
                    return new AudioDuration { Method = "WAV without a readable fmt byte rate or data chunk" };
                }
                return new AudioDuration { Seconds = (double)dataSize / byteRate, Method = "WAV header (data size / byte rate)" };
            }
        }

        public static AudioDuration Ogg(string path)
        {
            using (FileStream fs = Open(path))
            {
                long len = fs.Length;
                byte[] first = ReadAt(fs, 0, (int)Math.Min(len, 4096));
                long rate = 0;
                for (int i = 0; i + 16 < first.Length; i++)
                {
                    if (first[i] == 1 && Ascii(first, i + 1, "vorbis"))
                    {
                        rate = U32(first, i + 12); // packet type 1, "vorbis", version (4), channels (1), sample rate (4)
                        break;
                    }
                    if (Ascii(first, i, "OpusHead"))
                    {
                        rate = 48000; // Opus granule positions always count 48 kHz samples
                        break;
                    }
                }
                if (rate <= 0)
                {
                    return new AudioDuration { Method = "no Vorbis/Opus identification header" };
                }
                int tailLen = (int)Math.Min(len, 65536);
                byte[] tail = ReadAt(fs, len - tailLen, tailLen);
                for (int i = tail.Length - 14; i >= 0; i--)
                {
                    if (Ascii(tail, i, "OggS"))
                    {
                        long granule = (long)(U32(tail, i + 6) | ((ulong)U32(tail, i + 10) << 32));
                        if (granule <= 0)
                        {
                            return new AudioDuration { Method = "the last Ogg page has no granule position" };
                        }
                        return new AudioDuration { Seconds = (double)granule / rate, Method = "Ogg header (last granule position / sample rate)" };
                    }
                }
                return new AudioDuration { Method = "no Ogg page found at the end of the file" };
            }
        }

        private static readonly int[] s_v1l3 = { 0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 0 };
        private static readonly int[] s_v2l23 = { 0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160, 0 };
        private static readonly int[] s_v1l2 = { 0, 32, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 384, 0 };
        private static readonly int[] s_v1l1 = { 0, 32, 64, 96, 128, 160, 192, 224, 256, 288, 320, 352, 384, 416, 448, 0 };
        private static readonly int[] s_v2l1 = { 0, 32, 48, 56, 64, 80, 96, 112, 128, 144, 160, 176, 192, 224, 256, 0 };
        private static readonly int[] s_rates = { 44100, 48000, 32000, 0 };

        public static AudioDuration Mp3(string path)
        {
            using (FileStream fs = Open(path))
            {
                long len = fs.Length;
                long start = 0;
                byte[] id3 = ReadAt(fs, 0, 10);
                if (id3.Length == 10 && Ascii(id3, 0, "ID3"))
                {
                    long size = ((id3[6] & 0x7F) << 21) | ((id3[7] & 0x7F) << 14) | ((id3[8] & 0x7F) << 7) | (id3[9] & 0x7F);
                    start = 10 + size + ((id3[5] & 0x10) != 0 ? 10 : 0);
                }
                if (start >= len)
                {
                    return new AudioDuration { Method = "no MPEG frame after the ID3 tag" };
                }
                byte[] b = ReadAt(fs, start, (int)Math.Min(len - start, 16384));
                int f = -1;
                for (int i = 0; i + 4 <= b.Length; i++)
                {
                    if (b[i] == 0xFF && (b[i + 1] & 0xE0) == 0xE0 && ((b[i + 1] >> 3) & 3) != 1 && ((b[i + 1] >> 1) & 3) != 0
                        && ((b[i + 2] >> 4) & 0xF) != 0xF && ((b[i + 2] >> 2) & 3) != 3)
                    {
                        f = i;
                        break;
                    }
                }
                if (f < 0)
                {
                    return new AudioDuration { Method = "no MPEG frame header found" };
                }
                int ver = (b[f + 1] >> 3) & 3;   // 3 = MPEG1, 2 = MPEG2, 0 = MPEG2.5
                int layer = (b[f + 1] >> 1) & 3; // 1 = Layer III, 2 = Layer II, 3 = Layer I
                int bri = (b[f + 2] >> 4) & 0xF;
                int sri = (b[f + 2] >> 2) & 3;
                int mode = (b[f + 3] >> 6) & 3;
                bool v1 = ver == 3;
                int rate = s_rates[sri] / (v1 ? 1 : ver == 2 ? 2 : 4);
                int spf = layer == 3 ? 384 : layer == 2 ? 1152 : (v1 ? 1152 : 576);
                int kbps = layer == 1 ? (v1 ? s_v1l3[bri] : s_v2l23[bri]) : layer == 2 ? (v1 ? s_v1l2[bri] : s_v2l23[bri]) : (v1 ? s_v1l1[bri] : s_v2l1[bri]);
                if (rate <= 0)
                {
                    return new AudioDuration { Method = "invalid MPEG sample rate" };
                }
                int side = v1 ? (mode == 3 ? 17 : 32) : (mode == 3 ? 9 : 17);
                int x = f + 4 + side;
                if ((Ascii(b, x, "Xing") || Ascii(b, x, "Info")) && x + 12 <= b.Length)
                {
                    uint flags = U32Be(b, x + 4);
                    if ((flags & 1) != 0)
                    {
                        uint frames = U32Be(b, x + 8);
                        return new AudioDuration { Seconds = (double)frames * spf / rate, Method = "MP3 Xing/Info header (frame count)" };
                    }
                }
                int v = f + 4 + 32;
                if (Ascii(b, v, "VBRI") && v + 18 <= b.Length)
                {
                    uint frames = U32Be(b, v + 14);
                    return new AudioDuration { Seconds = (double)frames * spf / rate, Method = "MP3 VBRI header (frame count)" };
                }
                if (kbps > 0)
                {
                    double estimate = (len - start - f) * 8.0 / (kbps * 1000.0);
                    return new AudioDuration { CbrEstimateSeconds = estimate, Method = $"no Xing/VBRI frame count; a constant bitrate of {kbps} kbps would give {estimate:0} s" };
                }
                return new AudioDuration { Method = "no Xing/VBRI frame count and a free-format bitrate" };
            }
        }
    }
}
