using System.Diagnostics;
using System.Text.Json;
using GTARadio;

Console.Title = "GTA V Radio";
if (args.Length > 0 && args[0] == "--seektest")
{
    // Debug: compare a seek against continuous decoding.
    var src = new Source(args[1]); long at = long.Parse(args[2]);
    var a = new float[48000]; var b2 = new float[48000];
    src.Read(at, a, a.Length);
    var src2 = new Source(args[1]); var tmp = new float[48000]; long p0 = 0;
    while (p0 + 48000 <= at) { src2.Read(p0, tmp, 48000); p0 += 48000; }
    if (at > p0) { var t2 = new float[at - p0]; src2.Read(p0, t2, t2.Length); }
    src2.Read(at, b2, b2.Length);
    double d = 0; for (int i = 0; i < a.Length; i++) d += Math.Abs(a[i] - b2[i]);
    Console.WriteLine($"mean abs diff {d / a.Length:0.000000}");
    return;
}
if (args.Length > 0 && args[0] == "--bc7test")
{
    File.WriteAllBytes(args[4], Bc7.Decode(File.ReadAllBytes(args[1]), int.Parse(args[2]), int.Parse(args[3])));
    return;
}
if (args.Length > 0 && args[0] == "--reltest")
{
    // Debug: work out stations from an exported rel folder, with names from a text file.
    var names = File.ReadAllLines(args[2]).Select(x => x.Trim()).Where(x => x.Length > 0).ToDictionary(x => Jenk.Hash(x), x => x);
    var db = new ManifestBuilder.RelDb(args[1], names, _ => null);
    foreach (var r in db.Report) Console.WriteLine(r);
    foreach (var st in db.Stations) Console.WriteLine($"{st.Id} wheel {st.Wheel}: {st.Lists.Count} lists");
    return;
}
Console.WriteLine("GTA V Radio\n");
try
{
    var positional = new List<string>();
    string gameArg = null;
    for (int ai = 0; ai < args.Length; ai++)
    {
        if (args[ai] == "--game" && ai + 1 < args.Length) { gameArg = args[++ai]; continue; }
        if (args[ai] == "--render") { ai += 3; continue; }
        if (!args[ai].StartsWith("--")) positional.Add(args[ai]);
    }
    var baseDir = positional.FirstOrDefault() ?? AppContext.BaseDirectory;
    var dataDir = Path.Combine(baseDir, "GTARadio-data");
    var audioRoot = Path.Combine(dataDir, "audio");
    var relRoot = Path.Combine(dataDir, "rel");
    var manifestPath = Path.Combine(dataDir, "manifest.json");
    bool rebuild = args.Contains("--rebuild");
    // Bump when extraction changes so older extracted data gets redone.
    const string DataVersion = "4";
    var versionPath = Path.Combine(dataDir, "version.txt");
    var indexRoot = Path.Combine(dataDir, "index");
    var oldVersion = File.Exists(versionPath) ? File.ReadAllText(versionPath).Trim() : null;
    if (Directory.Exists(dataDir) && oldVersion == "3")
    {
        // Version 3 kept every container's index next to its audio. Move the indexes out; the audio
        // nothing plays gets removed once the station list is rebuilt below.
        Console.WriteLine("Tidying the extracted radio files...\n");
        foreach (var dir in Directory.Exists(audioRoot) ? Directory.GetDirectories(audioRoot) : Array.Empty<string>())
        {
            var to = Path.Combine(indexRoot, Path.GetFileName(dir));
            Directory.CreateDirectory(to);
            foreach (var f in Directory.GetFiles(dir, "*.json").Append(Path.Combine(dir, ".done")).Where(File.Exists))
                File.Move(f, Path.Combine(to, Path.GetFileName(f)), true);
        }
        if (File.Exists(manifestPath)) File.Delete(manifestPath);
        File.WriteAllText(versionPath, DataVersion);
    }
    else if (Directory.Exists(dataDir) && oldVersion != DataVersion)
    {
        Console.WriteLine("Updating the extracted radio files from an older version...\n");
        Directory.Delete(dataDir, true);
    }

    Manifest manifest = null;
    if (File.Exists(manifestPath) && !rebuild)
    {
        manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(manifestPath));
        // A newer program works out the stations differently; redo that part (already-extracted audio is kept).
        if (manifest.Version != Manifest.Current) { Console.WriteLine("Updating the station list for this version...\n"); manifest = null; }
    }
    string gameDir = null, updateDir = null;
    void OpenGame()
    {
        if (gameDir != null) return;
        (gameDir, updateDir) = GameFolders.Find(baseDir, gameArg);
        if (gameDir == null) throw new Exception("Couldn't find your GTA V files. Put this program in your GTA V folder (or next to a copy of its x64 folder), or run it with --game \"<your GTA V folder>\".");
        Console.WriteLine("Game files: " + gameDir);
        Console.WriteLine("Updates and DLC: " + (updateDir ?? "not found, so only the original stations will be available"));
        var keyDir = new[] { gameDir, baseDir }.FirstOrDefault(d => File.Exists(Path.Combine(d, "GTA5_Enhanced.exe")) || File.Exists(Path.Combine(d, "GTA5.exe"))) ?? baseDir;
        Extractor.LoadKeys(keyDir);
    }
    // Update and DLC archives in load order.
    List<(string path, string rel)> UpdateSources()
    {
        var list = new List<(string path, string rel)>();
        void Add(string path, string root) { if (File.Exists(path)) list.Add((path, Path.GetRelativePath(root, path))); }
        var dlcRoots = new List<string> { Path.Combine(gameDir, "x64", "dlcpacks") };
        if (updateDir != null)
        {
            var root = Path.GetDirectoryName(updateDir.TrimEnd('\\', '/'));
            Add(Path.Combine(updateDir, "update.rpf"), root);
            Add(Path.Combine(updateDir, "update2.rpf"), root);
            dlcRoots.Add(Path.Combine(updateDir, "x64", "dlcpacks"));
        }
        foreach (var dr in dlcRoots.Where(Directory.Exists))
            foreach (var pack in Directory.GetDirectories(dr).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                foreach (var f in Directory.GetFiles(pack, "*.rpf").OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                    Add(f, Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(dr))));
        return list;
    }
    if (manifest == null)
    {
        OpenGame();

        // Sources in the order the game loads them; later ones patch earlier ones.
        var sources = new List<(string path, string rel)>();
        void Add(string path, string root) { if (File.Exists(path)) sources.Add((path, Path.GetRelativePath(root, path))); }
        Add(Path.Combine(gameDir, "x64", "audio", "audio_rel.rpf"), gameDir);
        foreach (var f in Directory.GetFiles(Path.Combine(gameDir, "x64", "audio", "sfx"), "RADIO_*.rpf").OrderBy(x => x, StringComparer.OrdinalIgnoreCase)) Add(f, gameDir);
        Add(Path.Combine(baseDir, "americanrel.rpf"), baseDir);
        sources.AddRange(UpdateSources());

        Console.WriteLine($"\nFirst run: reading the radio from your game files ({sources.Count} archives). This takes a while.\n");
        if (Directory.Exists(relRoot)) Directory.Delete(relRoot, true);
        var scan = Extractor.Scan(sources, relRoot);
        string FolderOf(uint h) => scan.Awcs.TryGetValue(h, out var a) ? a.Folder : null;
        Console.WriteLine("\nWorking out the stations...");
        var db = new ManifestBuilder.RelDb(relRoot, scan.Names, FolderOf);
        var folders = ManifestBuilder.Plan(db, FolderOf, scan.Folders).OrderBy(x => x).ToList();
        Console.WriteLine($"\nReading the radio's wave files ({folders.Count} folders)...\n");
        Directory.CreateDirectory(indexRoot);
        int i = 0;
        foreach (var folder in folders)
        {
            i++;
            var done = Path.Combine(indexRoot, Extractor.FolderDir(folder), ".done");
            Console.WriteLine($"  [{i}/{folders.Count}] {folder}");
            if (File.Exists(done)) continue;
            Extractor.IndexFolder(scan, folder, indexRoot);
            File.WriteAllText(done, "ok");
        }
        manifest = ManifestBuilder.Build(dataDir, db, scan.Text);
        // Only the audio the stations play is extracted; anything else left over from older versions is removed.
        var used = manifest.AllFiles().ToList();
        Console.WriteLine($"\nExtracting the radio audio ({used.Count} files)...");
        Directory.CreateDirectory(audioRoot);
        Extractor.ExtractAudio(scan, used, audioRoot);
        int removed = Prune(audioRoot, used);
        if (removed > 0) Console.WriteLine($"  Removed {removed} files no station plays");
        var report = new List<string> { $"Game files: {gameDir}", $"Updates and DLC: {updateDir}", $"{sources.Count} archives, {scan.Awcs.Count} wave containers, {scan.Names.Count} names, {scan.Text.Count} text entries", "" };
        report.AddRange(db.Report);
        report.Add("");
        foreach (var st in manifest.Stations)
            report.Add($"{st.Id} = {st.Name} ({st.Kind}, wheel {st.Wheel}): {st.Music.Count} tracks, {st.Ads.Count} ads, {st.Idents.Count} IDs, {st.Solos.Count} solos, speech: " + string.Join(", ", st.Speech.GroupBy(k => k.Key.StartsWith("intro:") ? "song intros" : k.Key).Select(g => $"{g.Key} {g.Sum(x => x.Value.Count)}")));
        report.Add($"News: {manifest.News.Count}");
        File.WriteAllLines(Path.Combine(dataDir, "report.txt"), report);
        // Every wave container found, by folder, to help place DJ lines for new stations.
        File.WriteAllLines(Path.Combine(dataDir, "containers.txt"), scan.Awcs.Values.GroupBy(x => x.Folder).OrderBy(g => g.Key)
            .Select(g => $"{g.Key} ({g.Count()}): " + string.Join(" ", g.Select(x => x.Awc).OrderBy(x => x))));
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest));
        File.WriteAllText(versionPath, DataVersion);
    }

    // Station logos for the wheel, read once from the game's texture archives.
    var iconDir = Path.Combine(dataDir, "icons");
    if (!File.Exists(Path.Combine(iconDir, ".done")) && !args.Contains("--render"))
    {
        try
        {
            OpenGame();
            Console.WriteLine("\nLooking for the station logos in your game files...");
            var isrc = Directory.GetFiles(gameDir, "*.rpf").OrderBy(x => x, StringComparer.OrdinalIgnoreCase).Select(f => (f, Path.GetFileName(f))).ToList();
            isrc.AddRange(UpdateSources());
            Icons.Extract(isrc, manifest.Stations.Select(s => s.Id).ToList(), iconDir);
            File.WriteAllText(Path.Combine(iconDir, ".done"), "ok");
        }
        catch (Exception ex) { Log.Warn("Couldn't read the station logos, so the wheel will show names instead: " + ex.Message); }
    }

    var cfg = new Settings();
    var seed = Environment.TickCount;
    var runners = manifest.Stations.ToDictionary(s => s.Id, s => new StationRunner(s, manifest, cfg, seed ^ (int)Jenk.Hash(s.Id)));
    foreach (var r in runners.Values) r.Ensure(Clock.Now + 60L * StationRunner.Rate);

    var phone = new PhoneExport(manifest, audioRoot, Path.Combine(baseDir, "GTARadio-phone.pack"));
    if (args.Contains("--phone"))
    {
        // Make the iPhone copy and exit.
        var job = Task.Run(phone.Run);
        while (!job.IsCompleted) { await Task.Delay(2000); Console.Write($"\r  iPhone copy: {phone.DoneSeconds / Math.Max(1, phone.TotalSeconds):P0}   "); }
        Console.WriteLine($"\n{phone.State}: {phone.Status()}");
        return;
    }
    var ri = Array.IndexOf(args, "--render");
    if (ri >= 0)
    {
        // Debug: render a station offline from its timeline start, and print what was scheduled.
        var r = runners[args[ri + 1]]; int secs = int.Parse(args[ri + 2]); var outPath = args[ri + 3];
        long t0 = r.At(0).Start; long total = (long)secs * StationRunner.Rate;
        using var mix = new Mixer(audioRoot);
        using var fs = File.Create(outPath); var bw = new BinaryWriter(fs);
        bw.Write("RIFF"u8); bw.Write((int)(36 + total * 4)); bw.Write("WAVEfmt "u8); bw.Write(16); bw.Write((short)1); bw.Write((short)2); bw.Write(48000); bw.Write(48000 * 4); bw.Write((short)4); bw.Write((short)16); bw.Write("data"u8); bw.Write((int)(total * 4));
        var buf = new float[4800 * 2];
        for (long t = t0; t < t0 + total; t += 4800) { mix.Render(r, t, 4800, buf); foreach (var v in buf) bw.Write((short)(Math.Clamp(v, -1f, 1f) * 32767)); }
        foreach (var sg in r.Range(t0, t0 + total))
            Console.WriteLine($"{(sg.Start - t0) / 48000.0,8:0.0}s {sg.Kind,-6} {sg.Track.Name} ({sg.Len / 48000.0:0.0}s)" + string.Concat(sg.Overlays.Select(o => $"\n           + {o.Line.Name} at {o.At / 48000.0:0.0}s for {o.Len / 48000.0:0.0}s")));
        return;
    }
    Console.WriteLine();
    foreach (var st in manifest.Stations)
    {
        int intros = st.Speech.Where(k => k.Key.StartsWith("intro:")).Sum(k => k.Value.Count);
        int weather = st.Speech.Where(k => k.Key.Contains("weather")).Sum(k => k.Value.Count);
        Console.WriteLine($"  {st.Name,-26} {st.Music.Count,3} tracks  {st.Ads.Count,3} ads  {st.Idents.Count,2} IDs  {intros,3} song intros  {st.Speech.Values.Sum(v => v.Count) - intros,3} other DJ lines  {weather} weather lines");
    }
    Console.WriteLine($"\n{manifest.Stations.Count} stations ready.");
    var server = new Server(manifest, runners, cfg, audioRoot, iconDir);
    server.Phone = phone;
    server.Start();
    Console.WriteLine($"Radio is on at {server.Url}  (keep this window open; close it to turn the radio off)");
    if (!args.Contains("--no-browser"))
        try { Process.Start(new ProcessStartInfo(server.Url) { UseShellExecute = true }); } catch { }
    await Task.Delay(Timeout.Infinite);
}
catch (Exception ex)
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine("\nSomething went wrong: " + ex.Message);
    Console.ResetColor();
    Console.WriteLine(ex);
    Console.WriteLine("\nPress Enter to close.");
    Console.ReadLine();
}


// Deletes every file under audioRoot that isn't in keep (paths relative to audioRoot), then empty folders.
static int Prune(string audioRoot, List<string> keep)
{
    var set = keep.Select(k => Path.GetFullPath(Path.Combine(audioRoot, k))).ToHashSet(StringComparer.OrdinalIgnoreCase);
    int n = 0;
    foreach (var f in Directory.GetFiles(audioRoot, "*", SearchOption.AllDirectories))
        if (!set.Contains(Path.GetFullPath(f))) { File.Delete(f); n++; }
    foreach (var d in Directory.GetDirectories(audioRoot, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length))
        if (!Directory.EnumerateFileSystemEntries(d).Any()) Directory.Delete(d);
    return n;
}
