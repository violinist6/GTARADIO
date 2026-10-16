using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CodeWalker.GameFiles;

namespace GTARadio;

// Unlocks the player's own game archives with CodeWalker and writes the radio audio
// (as the original MP3 streams) plus the audio game data to the data folder.
static class Extractor
{
    public static void LoadKeys(string baseDir)
    {
        bool gen9 = File.Exists(Path.Combine(baseDir, "GTA5_Enhanced.exe"));
        bool legacy = File.Exists(Path.Combine(baseDir, "GTA5.exe"));
        if (!gen9 && !legacy)
            throw new Exception("Couldn't find GTA5_Enhanced.exe or GTA5.exe next to this program. Copy it from your GTA V install folder.");
        Log.Info("Reading unlock keys from " + (gen9 ? "GTA5_Enhanced.exe" : "GTA5.exe") + "...");
        var keyPath = baseDir.TrimEnd('\\', '/');
        if (!OperatingSystem.IsWindows())
        {
            // CodeWalker builds the path with a backslash; mirror that on non-Windows test machines.
            var link = keyPath + "\\" + (gen9 ? "gta5_enhanced.exe" : "gta5.exe");
            if (!File.Exists(link)) File.Copy(Path.Combine(baseDir, gen9 ? "GTA5_Enhanced.exe" : "GTA5.exe"), link);
        }
        GTA5Keys.LoadFromPath(keyPath, gen9);
    }

    // Where one wave container lives. Folder is the game's container path without the file name,
    // relative to audio\sfx (e.g. "radio_01_class_rock" or "dlc_hei4_music"), and Hash is jenk("folder/awc").
    public class AwcLoc { public RpfFile Rpf; public RpfFileEntry Entry; public string Folder; public string Awc; public uint Hash; }

    public class GameScan
    {
        public Dictionary<uint, AwcLoc> Awcs = new();
        public Dictionary<uint, string> Names = new();
        public Dictionary<uint, string> Text = new();
        public IEnumerable<string> Folders => Awcs.Values.Select(a => a.Folder).Distinct();
    }

    // Reads every source archive in order (base game, then updates, then DLC packs). Later sources
    // override earlier ones, as the game does. Writes the audio game data as XML to relRoot/NNN_source,
    // and indexes every wave container without extracting it yet.
    public static GameScan Scan(List<(string path, string rel)> sources, string relRoot)
    {
        var scan = new GameScan();
        Directory.CreateDirectory(relRoot);
        int idx = 0;
        foreach (var (path, relPath) in sources)
        {
            idx++;
            var tag = $"{idx:000}_" + Regex.Replace(relPath.ToLowerInvariant(), @"[^a-z0-9]+", "_").Trim('_');
            Console.WriteLine($"  [{idx}/{sources.Count}] {relPath}");
            RpfFile rpf;
            try
            {
                rpf = new RpfFile(path, relPath);
                rpf.ScanStructure(null, e => Log.Warn(e));
            }
            catch (Exception ex) { Log.Warn($"Couldn't open {relPath}: {ex.Message}"); continue; }
            foreach (var f in AllRpfs(rpf))
            {
                bool american = f.NameLower.StartsWith("american");
                foreach (var e in f.AllEntries)
                {
                    if (e is not RpfFileEntry fe) continue;
                    var p = fe.Path.Replace('\\', '/');
                    try
                    {
                        if (fe.NameLower.EndsWith(".awc"))
                        {
                            int si = p.LastIndexOf("/sfx/");
                            string container = si >= 0 ? p.Substring(si + 5) : string.Join("/", p.Split('/').TakeLast(2));
                            container = container.Replace(".rpf/", "/");
                            container = container.Substring(0, container.Length - 4);
                            int slash = container.LastIndexOf('/');
                            if (slash < 0) continue;
                            var loc = new AwcLoc { Rpf = f, Entry = fe, Folder = container.Substring(0, slash), Awc = container.Substring(slash + 1), Hash = Jenk.Hash(container) };
                            scan.Awcs[loc.Hash] = loc;
                        }
                        else if (fe.NameLower.EndsWith(".dat151.rel") || fe.NameLower.EndsWith(".dat54.rel"))
                        {
                            var data = f.ExtractFile(fe);
                            var dir = Path.Combine(relRoot, tag);
                            Directory.CreateDirectory(dir);
                            var xml = MetaXml.GetXml(fe, data, out string fn, dir);
                            if (!string.IsNullOrEmpty(xml)) File.WriteAllText(Path.Combine(dir, fn), xml);
                        }
                        else if (fe.NameLower.EndsWith(".nametable"))
                        {
                            var data = f.ExtractFile(fe);
                            foreach (var n in Encoding.ASCII.GetString(data).Split('\0', StringSplitOptions.RemoveEmptyEntries))
                                scan.Names[Jenk.Hash(n)] = n;
                        }
                        else if (american && fe.NameLower.EndsWith(".gxt2"))
                        {
                            var g = RpfFile.GetFile<Gxt2File>(fe, f.ExtractFile(fe));
                            foreach (var t in g.TextEntries ?? Array.Empty<Gxt2Entry>()) scan.Text[t.Hash] = t.Text;
                        }
                    }
                    catch (Exception ex) { Log.Warn("Couldn't read " + fe.Path + ": " + ex.Message); }
                }
            }
        }
        return scan;
    }

    // Extracts every wave container in one folder to audioRoot/<folder>.
    // Pass 1: read every wave container in a folder and save only its index (stream names, lengths,
    // markers) to indexRoot. No audio is written; the station list decides what gets extracted.
    public static void IndexFolder(GameScan scan, string folder, string indexRoot)
    {
        var outDir = Path.Combine(indexRoot, FolderDir(folder));
        Directory.CreateDirectory(outDir);
        int n = 0;
        foreach (var loc in scan.Awcs.Values.Where(a => a.Folder == folder))
        {
            try { ExportAwc(loc.Entry, loc.Rpf.ExtractFile(loc.Entry), outDir, null, null); n++; }
            catch (Exception ex) { Log.Warn("Couldn't read " + loc.Entry.Path + ": " + ex.Message); }
        }
        Log.Info($"    {n} files");
    }

    // Pass 2: extract only the audio the stations play. files are "folderdir/awc/stream.mp3" paths.
    public static int ExtractAudio(GameScan scan, IEnumerable<string> files, string audioRoot)
    {
        int n = 0;
        foreach (var g in files.Where(f => !File.Exists(Path.Combine(audioRoot, f))).GroupBy(f => string.Join('/', f.Split('/').Take(2))))
        {
            var parts = g.Key.Split('/');
            var hash = Jenk.Hash(DirFolder(parts[0]) + "/" + parts[1]);
            if (!scan.Awcs.TryGetValue(hash, out var loc)) { Log.Warn("Couldn't find " + g.Key + " in the game files"); continue; }
            var want = g.Select(f => f.Substring(g.Key.Length + 1)).ToHashSet();
            try { n += ExportAwc(loc.Entry, loc.Rpf.ExtractFile(loc.Entry), null, Path.Combine(audioRoot, parts[0]), want); }
            catch (Exception ex) { Log.Warn("Couldn't extract " + loc.Entry.Path + ": " + ex.Message); }
        }
        return n;
    }

    // Container folders can be nested ("a/b"); keep them one level deep on disk.
    public static string FolderDir(string folder) => folder.Replace('/', '+');
    public static string DirFolder(string dir) => dir.Replace('+', '/');

    // Writes the container's index to indexDir (when set) and the streams named in want to audioDir/awc/.
    static int ExportAwc(RpfFileEntry fe, byte[] data, string indexDir, string audioDir, HashSet<string> want)
    {
        var awc = RpfFile.GetFile<AwcFile>(fe, data);
        var baseName = Path.GetFileNameWithoutExtension(fe.Name).ToLowerInvariant();
        string adir = audioDir == null ? null : Path.Combine(audioDir, baseName);
        int written = 0;
        var info = new AwcInfo { Awc = baseName };
        foreach (var s in awc.Streams)
        {
            int codec; uint samples; int rate; byte[] audio;
            if (s.StreamSource != null && s.StreamFormat != null)
            {
                codec = (int)s.StreamFormat.Codec; samples = s.StreamFormat.Samples; rate = s.StreamFormat.SamplesPerSecond;
                using var ms = new MemoryStream();
                foreach (var blk in s.StreamSource.StreamBlocks)
                {
                    if (s.StreamChannelIndex >= (blk?.Channels?.Length ?? 0)) continue;
                    var ch = blk.Channels[s.StreamChannelIndex];
                    if (codec != 7) { ms.Write(ch.Data, 0, ch.Data.Length); continue; }
                    // MP3 blocks: each channel's frames are packed back to back (16-byte aligned), and
                    // Unused3 holds each channel's real byte count. CodeWalker splits the block assuming
                    // fixed-size channel regions, so rebuild the region and cut it up properly.
                    var region = blk.Channels.SelectMany(c => c.Data).ToArray();
                    int off = 0;
                    for (int c = 0; c < s.StreamChannelIndex; c++) off += (blk.Channels[c].Unused3 + 15) & ~15;
                    int len = Math.Min(ch.Unused3, region.Length - off);
                    if (len > 0) ms.Write(region, off, len);
                }
                audio = ms.ToArray();
            }
            else if (s.FormatChunk != null && s.DataChunk?.Data != null)
            {
                codec = (int)s.FormatChunk.Codec; samples = s.FormatChunk.Samples; rate = s.FormatChunk.SamplesPerSecond;
                audio = s.DataChunk.Data;
            }
            else continue;

            var sname = s.Name.ToLowerInvariant();
            string file = sname + (codec == 7 ? ".mp3" : ".wav");
            if (adir != null && want.Contains(file))
            {
                Directory.CreateDirectory(adir);
                File.WriteAllBytes(Path.Combine(adir, file), codec == 7 ? audio : s.GetWavFile());
                written++;
            }

            var si = new StreamInfo { Name = sname, Hash = (uint)s.Hash & 0x1FFFFFFF, Codec = codec, Samples = samples, Rate = rate, File = baseName + "/" + file };
            var mk = s.MarkersChunk?.Markers;
            if (mk != null)
                foreach (var m in mk)
                    si.Markers.Add(new MarkerInfo { Name = (uint)m.Name, Value = (uint)m.Value, Offset = m.SampleOffset });
            info.Streams.Add(si);
        }
        if (indexDir != null) File.WriteAllText(Path.Combine(indexDir, baseName + ".json"), JsonSerializer.Serialize(info));
        return written;
    }

    public static IEnumerable<RpfFile> AllRpfs(RpfFile r)
    {
        yield return r;
        if (r.Children != null) foreach (var c in r.Children) foreach (var x in AllRpfs(c)) yield return x;
    }
}

class AwcInfo
{
    public string Awc { get; set; }
    public List<StreamInfo> Streams { get; set; } = new();
}

class StreamInfo
{
    public string Name { get; set; }
    public uint Hash { get; set; }
    public int Codec { get; set; }
    public uint Samples { get; set; }
    public int Rate { get; set; }
    public string File { get; set; }
    public List<MarkerInfo> Markers { get; set; } = new();
}

class MarkerInfo
{
    public uint Name { get; set; }
    public uint Value { get; set; }
    public uint Offset { get; set; }
}

static class Log
{
    public static void Info(string s) => Console.WriteLine(s);
    public static void Warn(string s) { Console.ForegroundColor = ConsoleColor.Yellow; Console.WriteLine(s); Console.ResetColor(); }
}
