using System.IO.Compression;
using System.Text.RegularExpressions;
using CodeWalker.GameFiles;
using CodeWalker.Utils;

namespace GTARadio;

// Finds the radio wheel's station logos in the game's texture dictionaries and saves them as PNGs.
static class Icons
{
    static readonly Regex YtdFilter = new(@"(\\textures\\|scaleform|hud|radio|frontend|script_txd|minimap)", RegexOptions.IgnoreCase);
    // Clothing, vehicles and map models hold tens of thousands of texture dictionaries with no logos in them.
    static readonly Regex YtdSkip = new(@"\\(models|levels|vehicles|peds)", RegexOptions.IgnoreCase);

    public static void Extract(List<(string path, string rel)> sources, List<string> stationIds, string iconDir)
    {
        Directory.CreateDirectory(iconDir);
        var report = new List<string>();
        var best = new Dictionary<string, (int score, Texture tex, string where)>();
        int scanned = 0;
        var all = new List<string>();
        foreach (var (path, rel) in sources)
        {
            RpfFile rpf;
            try { rpf = new RpfFile(path, rel); rpf.ScanStructure(null, _ => { }); }
            catch (Exception ex) { report.Add($"couldn't open {rel}: {ex.Message}"); continue; }
            foreach (var f in Extractor.AllRpfs(rpf))
            foreach (var e in f.AllEntries)
            {
                if (e is not RpfFileEntry fe || !fe.NameLower.EndsWith(".ytd")) continue;
                if (!YtdFilter.IsMatch(fe.Path) || YtdSkip.IsMatch(fe.Path)) continue;
                YtdFile ytd;
                try { ytd = RpfFile.GetFile<YtdFile>(fe, f.ExtractFile(fe)); scanned++; }
                catch { continue; }
                var texs = ytd?.TextureDict?.Textures?.data_items;
                if (texs == null) continue;
                all.Add(fe.Path + ": " + string.Join(" ", texs.Select(t => t.Name)));
                bool radioYtd = fe.NameLower.Contains("radio");
                foreach (var t in texs)
                {
                    var tn = (t.Name ?? "").ToLowerInvariant();
                    if (!radioYtd && !tn.Contains("radio")) continue;
                    report.Add($"{fe.Path} : {tn} {t.Width}x{t.Height} {t.Format}");
                    foreach (var id in stationIds)
                    {
                        int score = tn == id.ToLowerInvariant() ? 3 : ManifestBuilder.NamedFor(id, tn) ? 2 : 0;
                        if (score == 0) continue;
                        score = score * 100000 + Math.Min(t.Width * t.Height, 99999);
                        if (!best.TryGetValue(id, out var b) || score > b.score) best[id] = (score, t, fe.Path + " : " + tn);
                    }
                }
            }
        }
        report.Insert(0, $"{scanned} texture dictionaries checked");
        foreach (var (id, b) in best)
        {
            try
            {
                var px = Pixels(b.tex);
                if (px == null) { report.Add($"{id}: {b.where} is in an unsupported format ({b.tex.Format})"); continue; }
                File.WriteAllBytes(Path.Combine(iconDir, id + ".png"), Png(px, b.tex.Width, b.tex.Height));
                report.Add($"{id}: {b.where}");
            }
            catch (Exception ex) { report.Add($"{id}: couldn't convert {b.where}: {ex.Message}"); }
        }
        File.WriteAllLines(Path.Combine(iconDir, "icons.txt"), report);
        // Every texture dictionary looked at, in case the logos turn out to be named differently.
        File.WriteAllLines(Path.Combine(iconDir, "textures.txt"), all);
        Log.Info($"  {best.Count} station logos found");
    }

    // BGRA pixels of the top mip level.
    static byte[] Pixels(Texture t)
    {
        if (t.Format == TextureFormat.D3DFMT_BC7) return Bc7.Decode(t.Data.FullData, t.Width, t.Height);
        return DDSIO.GetPixels(t, 0);
    }

    static byte[] Png(byte[] bgra, int w, int h)
    {
        using var raw = new MemoryStream();
        for (int y = 0; y < h; y++)
        {
            raw.WriteByte(0);
            for (int x = 0; x < w; x++) { int o = (y * w + x) * 4; raw.WriteByte(bgra[o + 2]); raw.WriteByte(bgra[o + 1]); raw.WriteByte(bgra[o]); raw.WriteByte(bgra[o + 3]); }
        }
        using var z = new MemoryStream();
        using (var zs = new ZLibStream(z, CompressionLevel.Optimal, true)) raw.WriteTo(zs);
        using var png = new MemoryStream();
        png.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        var ihdr = new byte[13];
        BE(ihdr, 0, w); BE(ihdr, 4, h); ihdr[8] = 8; ihdr[9] = 6;
        Chunk(png, "IHDR", ihdr); Chunk(png, "IDAT", z.ToArray()); Chunk(png, "IEND", Array.Empty<byte>());
        return png.ToArray();
    }

    static void BE(byte[] b, int o, int v) { b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v; }

    static void Chunk(Stream s, string type, byte[] data)
    {
        var len = new byte[4]; BE(len, 0, data.Length); s.Write(len);
        var td = System.Text.Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
        s.Write(td);
        var crc = new byte[4]; BE(crc, 0, (int)Crc(td)); s.Write(crc);
    }

    static uint[] table;
    static uint Crc(byte[] d)
    {
        if (table == null)
        {
            table = new uint[256];
            for (uint n = 0; n < 256; n++) { uint c = n; for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1; table[n] = c; }
        }
        uint r = 0xFFFFFFFF;
        foreach (var b in d) r = table[(r ^ b) & 0xFF] ^ (r >> 8);
        return r ^ 0xFFFFFFFF;
    }
}
