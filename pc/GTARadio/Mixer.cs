using NLayer;

namespace GTARadio;

// Reads one mono stream (MP3 from the game, or WAV for older containers) at an arbitrary sample position.
class Source : IDisposable
{
    readonly string PathName;
    MpegFile Mp3;
    readonly short[] Pcm;
    readonly int SrcRate;
    readonly Mp3Index Index;
    long Pos = -1;
    float[] Scratch = new float[4096];

    public Source(string path)
    {
        PathName = path;
        if (path.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase)) { Index = Mp3Index.Get(path); SrcRate = Index.Rate; }
        else
        {
            var b = File.ReadAllBytes(path);
            SrcRate = BitConverter.ToInt32(b, 24);
            int data = 12;
            while (data + 8 <= b.Length && System.Text.Encoding.ASCII.GetString(b, data, 4) != "data") data += 8 + BitConverter.ToInt32(b, data + 4);
            int len = Math.Min(BitConverter.ToInt32(b, data + 4), b.Length - data - 8);
            Pcm = new short[len / 2];
            Buffer.BlockCopy(b, data + 8, Pcm, 0, Pcm.Length * 2);
        }
    }

    // Opens a fresh decoder a few frames before the target and decodes up to it, so the bit reservoir is primed.
    void SeekTo(long pos)
    {
        Mp3?.Dispose(); Mp3 = null;
        Pos = pos;
        long frame = Math.Max(0, pos / Index.SamplesPerFrame - 8);
        if (frame >= Index.Offsets.Length - 2) return; // past the end: silence
        var fs = new FileStream(PathName, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        try { Mp3 = new MpegFile(new SubStream(fs, Index.Offsets[frame])); }
        catch (Exception) { fs.Dispose(); return; }
        long skip = pos - frame * Index.SamplesPerFrame;
        while (skip > 0)
        {
            int n = SafeRead(Scratch, 0, (int)Math.Min(Scratch.Length, skip));
            if (n <= 0) break;
            skip -= n;
        }
        Pos = pos;
    }

    int SafeRead(float[] dst, int off, int count)
    {
        if (Mp3 == null) return 0;
        try { return Mp3.ReadSamples(dst, off, count); }
        catch (Exception) { return 0; }
    }

    // Fills dst[0..count) with samples starting at pos (source sample rate). Missing data is silence.
    void ReadRaw(long pos, float[] dst, int count)
    {
        Array.Clear(dst, 0, count);
        if (Pcm != null)
        {
            for (int i = 0; i < count; i++) { long p = pos + i; if (p >= 0 && p < Pcm.Length) dst[i] = Pcm[p] / 32768f; }
            return;
        }
        if (Pos != pos) SeekTo(pos);
        int got = 0;
        while (got < count)
        {
            int n = SafeRead(dst, got, count - got);
            if (n <= 0) break;
            got += n;
        }
        Pos += count;
    }

    // Reads at the 48 kHz output rate.
    public void Read(long pos, float[] dst, int count)
    {
        Array.Clear(dst, 0, count);
        if (pos < 0)
        {
            int skip = (int)Math.Min(count, -pos);
            if (skip >= count) return;
            var tmp = new float[count - skip]; Read(0, tmp, tmp.Length); Array.Copy(tmp, 0, dst, skip, tmp.Length); return;
        }
        if (SrcRate == StationRunner.Rate) { ReadRaw(pos, dst, count); return; }
        double ratio = (double)SrcRate / StationRunner.Rate;
        long s0 = (long)(pos * ratio);
        int need = (int)(count * ratio) + 2;
        var src = new float[need];
        ReadRaw(s0, src, need);
        Pos = s0 + need - 2; // keep reads contiguous for the next block
        for (int i = 0; i < count; i++)
        {
            double x = pos * ratio + i * ratio - s0; int a = (int)x; double f = x - a;
            dst[i] = a + 1 < need ? (float)(src[a] * (1 - f) + src[a + 1] * f) : 0;
        }
    }

    public void Dispose() => Mp3?.Dispose();
}

// Byte offset of every MPEG audio frame, so seeking never relies on scanning for sync words.
class Mp3Index
{
    public long[] Offsets;
    public int Rate;
    public int SamplesPerFrame;
    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Mp3Index> Cache = new();
    static readonly int[] Br1 = { 0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 0 };
    static readonly int[] Br2 = { 0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160, 0 };
    static readonly int[] Sr1 = { 44100, 48000, 32000, 0 };

    public static Mp3Index Get(string path) => Cache.GetOrAdd(path, Build);

    static Mp3Index Build(string path)
    {
        var b = File.ReadAllBytes(path);
        var list = new List<long>();
        int rate = 0, spf = 1152;
        long p = 0;
        while (p + 4 <= b.Length)
        {
            if (b[p] == 0xFF && (b[p + 1] & 0xE0) == 0xE0)
            {
                int ver = (b[p + 1] >> 3) & 3;      // 3 = MPEG1, 2 = MPEG2, 0 = MPEG2.5
                int layer = (b[p + 1] >> 1) & 3;    // 1 = Layer III
                int bri = (b[p + 2] >> 4) & 15, sri = (b[p + 2] >> 2) & 3, pad = (b[p + 2] >> 1) & 1;
                if (layer == 1 && ver != 1 && bri != 0 && bri != 15 && sri != 3)
                {
                    bool mpeg1 = ver == 3;
                    int sr = Sr1[sri] / (mpeg1 ? 1 : ver == 2 ? 2 : 4);
                    int br = (mpeg1 ? Br1 : Br2)[bri] * 1000;
                    int len = mpeg1 ? 144 * br / sr + pad : 72 * br / sr + pad;
                    if (len > 4)
                    {
                        if (rate == 0) { rate = sr; spf = mpeg1 ? 1152 : 576; }
                        list.Add(p); p += len; continue;
                    }
                }
            }
            p++;
        }
        return new Mp3Index { Offsets = list.ToArray(), Rate = rate == 0 ? 48000 : rate, SamplesPerFrame = spf };
    }
}

// A read-only view of a stream starting at an offset (lets the decoder start mid-file).
class SubStream : Stream
{
    readonly Stream S; readonly long Off;
    public SubStream(Stream s, long off) { S = s; Off = off; S.Position = off; }
    public override bool CanRead => true; public override bool CanSeek => true; public override bool CanWrite => false;
    public override long Length => S.Length - Off;
    public override long Position { get => S.Position - Off; set => S.Position = value + Off; }
    public override int Read(byte[] buffer, int offset, int count) => S.Read(buffer, offset, count);
    public override long Seek(long offset, SeekOrigin origin) => origin switch
    {
        SeekOrigin.Begin => S.Seek(offset + Off, SeekOrigin.Begin) - Off,
        SeekOrigin.Current => S.Seek(offset, SeekOrigin.Current) - Off,
        _ => S.Seek(offset, SeekOrigin.End) - Off,
    };
    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    protected override void Dispose(bool disposing) { if (disposing) S.Dispose(); base.Dispose(disposing); }
}

// Renders a station's timeline to stereo PCM: music, DJ speech over it with ducking, breaks.
class Mixer : IDisposable
{
    readonly string AudioRoot;
    readonly Dictionary<string, (Source src, long lastUse)> Sources = new();
    long Tick;
    const float Duck = 0.45f;          // music level under DJ speech
    const float SpeechGain = 1.0f;
    static readonly long Ramp = StationRunner.Rate / 4;

    public Mixer(string audioRoot) { AudioRoot = audioRoot; }

    Source Get(string file)
    {
        Tick++;
        if (!Sources.TryGetValue(file, out var e)) e = (new Source(Path.Combine(AudioRoot, file)), Tick);
        Sources[file] = (e.src, Tick);
        if (Sources.Count > 12)
            foreach (var old in Sources.Where(x => Tick - x.Value.lastUse > 200).Select(x => x.Key).ToList()) { Sources[old].src.Dispose(); Sources.Remove(old); }
        return e.src;
    }

    public void Render(StationRunner st, long t, int frames, float[] outLR)
    {
        Array.Clear(outLR, 0, frames * 2);
        var l = new float[frames]; var r = new float[frames]; var sp = new float[frames];
        foreach (var seg in st.Range(t, t + frames))
        {
            long a = Math.Max(t, seg.Start), b = Math.Min(t + frames, seg.End);
            if (b <= a) continue;
            int n = (int)(b - a), o = (int)(a - t);
            long srcPos = seg.SourceOffset + (a - seg.Start);
            var files = seg.Track.Audio.Files;
            Get(files[0]).Read(srcPos, l, n);
            if (files.Count > 1) Get(files[1]).Read(srcPos, r, n); else Array.Copy(l, r, n);

            for (int i = 0; i < n; i++)
            {
                long rel = a - seg.Start + i;
                float env = 0;
                foreach (var ov in seg.Overlays)
                {
                    float e = rel < ov.At - Ramp || rel >= ov.At + ov.Len + Ramp ? 0
                        : rel < ov.At ? (float)(rel - (ov.At - Ramp)) / Ramp
                        : rel < ov.At + ov.Len ? 1
                        : 1 - (float)(rel - (ov.At + ov.Len)) / Ramp;
                    env = Math.Max(env, e);
                }
                float g = 1 - (1 - Duck) * env;
                outLR[(o + i) * 2] += l[i] * g;
                outLR[(o + i) * 2 + 1] += r[i] * g;
            }

            foreach (var ov in seg.Overlays)
            {
                long os = seg.Start + ov.At, oe = os + ov.Len;
                long ca = Math.Max(a, os), cb = Math.Min(b, oe);
                if (cb <= ca) continue;
                int cn = (int)(cb - ca), co = (int)(ca - t);
                Get(ov.Line.Audio.Files[0]).Read(ca - os, sp, cn);
                for (int i = 0; i < cn; i++) { outLR[(co + i) * 2] += sp[i] * SpeechGain; outLR[(co + i) * 2 + 1] += sp[i] * SpeechGain; }
            }
        }
    }

    public void Dispose() { foreach (var s in Sources.Values) s.src.Dispose(); Sources.Clear(); }
}
