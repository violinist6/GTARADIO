using System.Text;
using System.Text.Json;
using Concentus;
using Concentus.Enums;
using NLayer;

namespace GTARadio;

// Builds GTARadio-phone.pack: every track the stations play, compressed to Opus, plus the station list,
// in one file the iPhone player copies into its own storage. Layout:
//   "GTARPK01", u64 index offset, u64 index length, then one blob per audio clip, then the index (JSON).
//   Blob: u32 frames, u16 channels, u16 lookahead, u32 samples, u16 packet sizes[frames], packets.
// Opus frames are 20 ms (960 samples at 48 kHz).
class PhoneExport
{
    public const int Frame = 960;
    readonly Manifest M;
    readonly string AudioRoot, OutPath;
    public double TotalSeconds, DoneSeconds;
    public string State = "idle", Error;
    long doneSamples;

    public PhoneExport(Manifest m, string audioRoot, string outPath) { M = m; AudioRoot = audioRoot; OutPath = outPath; }

    public object Status() => new { state = State, error = Error, done = DoneSeconds, total = TotalSeconds, path = OutPath, bytes = File.Exists(OutPath) ? new FileInfo(OutPath).Length : 0 };

    public void Run()
    {
        try
        {
            State = "running"; Error = null;
            // One blob per distinct clip (the same advert plays on many stations).
            var blobs = new List<List<string>>();
            var blobOf = new Dictionary<string, int>();
            var lengths = new List<long>();
            int Blob(Track t)
            {
                var key = string.Join("|", t.Audio.Files);
                if (!blobOf.TryGetValue(key, out var id)) { id = blobs.Count; blobOf[key] = id; blobs.Add(t.Audio.Files); lengths.Add(t.Audio.Samples * StationRunner.Rate / Math.Max(1, t.Audio.Rate)); }
                return id;
            }
            object T(Track t) => new
            {
                n = t.Name, b = Blob(t), s = t.Audio.Samples * StationRunner.Rate / Math.Max(1, t.Audio.Rate),
                @is = t.IntroStart, ie = t.IntroEnd, os = t.OutroStart,
                c = t.Cues.Select(c => new object[] { c.At, c.Title, c.Artist }).ToList(),
            };
            List<object> L(List<Track> l) => l.Select(T).ToList();
            var stations = M.Stations.Select(st => new
            {
                id = st.Id, name = st.Name, kind = st.Kind, wheel = st.Wheel,
                music = L(st.Music), idents = L(st.Idents), solos = L(st.Solos), ads = L(st.Ads),
                speech = st.Speech.ToDictionary(k => k.Key, k => L(k.Value)),
            }).ToList();
            var news = L(M.News);
            TotalSeconds = lengths.Sum(x => (double)x) / StationRunner.Rate;

            var part = OutPath + ".part";
            var offsets = new (long off, long len)[blobs.Count];
            using (var fs = new FileStream(part, FileMode.Create, FileAccess.ReadWrite, FileShare.Read, 1 << 20))
            {
                fs.Write(new byte[24]);
                var gate = new object();
                Parallel.For(0, blobs.Count, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1) }, i =>
                {
                    var data = Encode(blobs[i], lengths[i]);
                    lock (gate) { offsets[i] = (fs.Position, data.Length); fs.Write(data); }
                });
                var index = JsonSerializer.SerializeToUtf8Bytes(new { v = 1, stations, news, minGap = M.MinGapMs, blobs = offsets.Select(o => new[] { o.off, o.len }).ToList() });
                long at = fs.Position;
                fs.Write(index);
                fs.Position = 0;
                fs.Write(Encoding.ASCII.GetBytes("GTARPK01"));
                fs.Write(BitConverter.GetBytes(at));
                fs.Write(BitConverter.GetBytes((long)index.Length));
            }
            File.Move(part, OutPath, true);
            DoneSeconds = TotalSeconds;
            State = "done";
        }
        catch (Exception ex) { Error = ex.Message; State = "failed"; Log.Warn("iPhone copy failed: " + ex); }
    }

    byte[] Encode(List<string> files, long samples)
    {
        int ch = files.Count;
        var enc = OpusCodecFactory.CreateEncoder(48000, ch, OpusApplication.OPUS_APPLICATION_AUDIO);
        enc.Bitrate = ch == 2 ? 96000 : 48000;
        enc.Complexity = 6;
        enc.UseVBR = true;
        int la = enc.Lookahead;
        var readers = files.Select(f => new PcmReader(Path.Combine(AudioRoot, f))).ToArray();
        try
        {
            // Encode lookahead extra samples so the decoder's delay never cuts off the end.
            long total = samples + la;
            int frames = (int)((total + Frame - 1) / Frame);
            var sizes = new ushort[frames];
            using var packets = new MemoryStream();
            var chan = new float[ch][];
            for (int c = 0; c < ch; c++) chan[c] = new float[Frame];
            var inter = new float[Frame * ch];
            var outBuf = new byte[4000];
            for (int f = 0; f < frames; f++)
            {
                for (int c = 0; c < ch; c++) readers[c].Read(chan[c], Frame);
                for (int i = 0; i < Frame; i++) for (int c = 0; c < ch; c++) inter[i * ch + c] = Math.Clamp(chan[c][i], -1f, 1f);
                int n = enc.Encode(inter, Frame, outBuf, outBuf.Length);
                sizes[f] = (ushort)n;
                packets.Write(outBuf, 0, n);
                if ((f & 255) == 255) Progress(256L * Frame);
            }
            Progress((frames & 255) * (long)Frame);
            using var ms = new MemoryStream();
            var bw = new BinaryWriter(ms);
            bw.Write(frames); bw.Write((ushort)ch); bw.Write((ushort)la); bw.Write((uint)samples);
            foreach (var s in sizes) bw.Write(s);
            packets.WriteTo(ms);
            return ms.ToArray();
        }
        finally { foreach (var r in readers) r.Dispose(); }
    }

    void Progress(long n)
    {
        var d = Interlocked.Add(ref doneSamples, n);
        DoneSeconds = Math.Min(TotalSeconds, (double)d / StationRunner.Rate);
    }
}

// Reads a mono WAV (16-bit) or MP3 stream front to back at 48 kHz without loading it all into memory.
class PcmReader : IDisposable
{
    readonly FileStream Fs;
    readonly MpegFile Mp3;
    readonly int Rate;
    long wavLeft;
    readonly byte[] raw = new byte[8192];
    // Resampling state (only used when the source isn't 48 kHz).
    double pos; float prev, cur; bool primed;

    public PcmReader(string path)
    {
        if (path.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase)) { Mp3 = new MpegFile(path); Rate = Mp3.SampleRate; return; }
        Fs = File.OpenRead(path);
        var h = new byte[12]; Fs.ReadExactly(h);
        var ch = new byte[8];
        Rate = 48000;
        while (Fs.Read(ch, 0, 8) == 8)
        {
            var id = Encoding.ASCII.GetString(ch, 0, 4); int len = BitConverter.ToInt32(ch, 4);
            if (id == "fmt ") { var f = new byte[len]; Fs.ReadExactly(f); Rate = BitConverter.ToInt32(f, 4); continue; }
            if (id == "data") { wavLeft = Math.Min(len, Fs.Length - Fs.Position); break; }
            Fs.Seek(len + (len & 1), SeekOrigin.Current);
        }
    }

    // Next sample at the source rate, or 0 past the end.
    float[] one = new float[1];
    readonly float[] mp3Buf = new float[4096]; int mp3N, mp3P;
    float NextRaw()
    {
        if (Mp3 != null)
        {
            if (mp3P >= mp3N)
            {
                mp3P = 0;
                try { mp3N = Mp3.ReadSamples(mp3Buf, 0, mp3Buf.Length); } catch { mp3N = 0; }
                if (mp3N <= 0) { mp3N = 0; return 0; }
            }
            return mp3Buf[mp3P++];
        }
        if (wavLeft < 2) return 0;
        if (rawP >= rawN)
        {
            rawN = Fs.Read(raw, 0, (int)Math.Min(raw.Length, wavLeft)) & ~1; rawP = 0;
            if (rawN <= 0) { wavLeft = 0; return 0; }
        }
        short s = (short)(raw[rawP] | raw[rawP + 1] << 8); rawP += 2; wavLeft -= 2;
        return s / 32768f;
    }
    int rawN, rawP;

    public void Read(float[] dst, int count)
    {
        if (Rate == 48000) { for (int i = 0; i < count; i++) dst[i] = NextRaw(); return; }
        double step = (double)Rate / 48000;
        if (!primed) { prev = NextRaw(); cur = NextRaw(); primed = true; }
        for (int i = 0; i < count; i++)
        {
            while (pos >= 1) { prev = cur; cur = NextRaw(); pos -= 1; }
            dst[i] = (float)(prev + (cur - prev) * pos);
            pos += step;
        }
    }

    public void Dispose() { Fs?.Dispose(); Mp3?.Dispose(); }
}
