using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace GTARadio;

// Track categories as used by RadioStationTrackList.Category in game.dat151.
// 0 adverts, 1 idents, 2 music, 3 news, 4 weather, 5 DJ solo, 6 to-news, 7 to-ad,
// 8/9 user intro/outro, 10/11 morning/evening intro, 12-14 takeover variants.
enum Cat { Advert = 0, Ident = 1, Music = 2, News = 3, Weather = 4, Solo = 5 }

class AudioRef
{
    public List<string> Files { get; set; } = new(); // 1 = mono, 2 = left/right
    public long Samples { get; set; }
    public int Rate { get; set; }
    public double Seconds => Rate > 0 ? (double)Samples / Rate : 0;
}

class TextCue
{
    public double At { get; set; }
    public int Id { get; set; }
    public string Title { get; set; }
    public string Artist { get; set; }
}

class Track
{
    public string Name { get; set; }
    public AudioRef Audio { get; set; }
    public double IntroStart { get; set; } = -1;
    public double IntroEnd { get; set; } = -1;
    public double OutroStart { get; set; } = -1;
    public List<TextCue> Cues { get; set; } = new();
}

class Station
{
    public string Id { get; set; }
    public string Name { get; set; }
    public int Wheel { get; set; }
    public string Kind { get; set; } // music, talk, mix
    public List<Track> Music { get; set; } = new();
    public List<Track> Idents { get; set; } = new();
    public List<Track> Solos { get; set; } = new();
    public List<Track> Ads { get; set; } = new();
    public Dictionary<string, List<Track>> Speech { get; set; } = new();
}

class Manifest
{
    // Every audio file the stations and news can play.
    public IEnumerable<string> AllFiles() => Stations.SelectMany(s => s.Music.Concat(s.Idents).Concat(s.Solos).Concat(s.Ads).Concat(s.Speech.Values.SelectMany(v => v)))
        .Concat(News).SelectMany(t => t.Audio.Files).Distinct();

    public const int Current = 4;
    public int Version { get; set; } = Current;
    public List<Station> Stations { get; set; } = new();
    public List<Track> News { get; set; } = new();
    public Dictionary<int, int> MinGapMs { get; set; } = new();
}

static class ManifestBuilder
{
    static readonly Dictionary<string, string> DisplayNames = new()
    {
        ["RADIO_01_CLASS_ROCK"] = "Los Santos Rock Radio",
        ["RADIO_02_POP"] = "Non-Stop-Pop FM",
        ["RADIO_03_HIPHOP_NEW"] = "Radio Los Santos",
        ["RADIO_04_PUNK"] = "Channel X",
        ["RADIO_05_TALK_01"] = "West Coast Talk Radio",
        ["RADIO_06_COUNTRY"] = "Rebel Radio",
        ["RADIO_07_DANCE_01"] = "Soulwax FM",
        ["RADIO_08_MEXICAN"] = "East Los FM",
        ["RADIO_09_HIPHOP_OLD"] = "West Coast Classics",
        ["RADIO_11_TALK_02"] = "Blaine County Radio",
        ["RADIO_12_REGGAE"] = "The Blue Ark",
        ["RADIO_13_JAZZ"] = "Worldwide FM",
        ["RADIO_14_DANCE_02"] = "FlyLo FM",
        ["RADIO_15_MOTOWN"] = "The Lowdown 91.1",
        ["RADIO_16_SILVERLAKE"] = "Radio Mirror Park",
        ["RADIO_17_FUNK"] = "Space 103.2",
        ["RADIO_18_90S_ROCK"] = "Vinewood Boulevard Radio",
    };

    // Used only when the game's own text has no name for a DLC station.
    static readonly Dictionary<string, string> DlcNames = new()
    {
        ["RADIO_20_THELAB"] = "The Lab",
        ["RADIO_21_DLC_XM17"] = "Blonded Los Santos 97.8 FM",
        ["RADIO_22_DLC_BATTLE_MIX1_RADIO"] = "Los Santos Underground Radio",
        ["RADIO_23_DLC_XM19_RADIO"] = "iFruit Radio",
        ["RADIO_27_DLC_PRHEI4"] = "Still Slipping Los Santos",
        ["RADIO_34_DLC_HEI4_KULT"] = "Kult FM",
        ["RADIO_35_DLC_HEI4_MLR"] = "The Music Locker",
        ["RADIO_37_MOTOMAMI"] = "MOTOMAMI Los Santos",
    };

    static readonly string[] NotStations = { "RADIO_19_USER", "RADIO_36_AUDIOPLAYER" };

    static readonly string[] SpeechWords = { "general", "morning", "afternoon", "evening", "night", "to_ad", "to_news", "to_weather", "weather", "outro", "intro", "chatter" };

    class Container
    {
        public string Station; public string Awc; public Dictionary<uint, StreamInfo> Streams = new(); public AwcInfo Info;
    }

    // All audio game data from every source, merged in load order (later objects replace earlier ones
    // with the same name, which is how updates and DLC packs patch the base game).
    public class RelDb
    {
        public Dictionary<uint, XElement> Items = new();
        public Dictionary<uint, XElement> Sounds = new();
        public Dictionary<uint, string> Names;
        public List<(string Id, int Wheel, List<XElement> Lists)> Stations = new();
        public List<string> Report = new();
        public HashSet<XElement> AdLists = new();
        // Music lists no station claims (media player extras, unlockables). A station can still take one
        // later if its DJ has intros for those songs.
        public List<(string Name, XElement List)> Leftover = new();

        public RelDb(string relRoot, Dictionary<uint, string> names, Func<uint, string> folderOf)
        {
            Folder = folderOf;
            Names = names ?? new();
            var settings = new List<XElement>();
            foreach (var dir in Directory.GetDirectories(relRoot).OrderBy(d => d, StringComparer.Ordinal))
            {
                foreach (var f in Directory.GetFiles(dir, "*.dat151.rel.xml").OrderBy(x => x))
                {
                    int ns = 0, nl = 0;
                    foreach (var it in XDocument.Load(f).Root.Element("Items").Elements("Item"))
                    {
                        Items[Hv(it.Element("Name"))] = it;
                        var ty = (string)it.Attribute("type");
                        if (ty == "RadioStationSettings") { settings.Add(it); ns++; }
                        if (ty == "RadioStationTrackList") nl++;
                    }
                    if (ns + nl > 0) Report.Add($"{Path.GetFileName(dir)}/{Path.GetFileName(f)}: {ns} station definitions, {nl} track lists");
                }
                foreach (var f in Directory.GetFiles(dir, "*.dat54.rel.xml").OrderBy(x => x))
                    foreach (var it in XDocument.Load(f).Root.Element("Items").Elements("Item"))
                        Sounds[Hv(it.Element("Name"))] = it;
            }

            // A station can be defined more than once (base game, then patched by updates and DLC).
            // It gets every track list any of its definitions names, and the last wheel position.
            var lists = new Dictionary<string, List<uint>>();
            var wheel = new Dictionary<string, int>();
            var referenced = new HashSet<uint>();
            var offWheel = new HashSet<string>();
            foreach (var it in settings)
            {
                var rn = it.Element("RadioName")?.Value?.Trim();
                if (string.IsNullOrEmpty(rn)) continue;
                var refs = it.Element("TrackList")?.Elements("Item").Select(Hv).ToList() ?? new();
                referenced.UnionWith(refs);
                if (!rn.StartsWith("RADIO_") || NotStations.Contains(rn)) continue;
                // Rockstar Editor definitions (replay track lists only, wheel position 1000) aren't the live station.
                if (!refs.Any(r => Items.TryGetValue(r, out var x) && (string)x.Attribute("type") == "RadioStationTrackList")) continue;
                // Nightclub and background-music sets are flagged as not on the wheel.
                if (IsOffWheel(it)) { Report.Add($"{rn}: not on the wheel in the game, skipped"); offWheel.Add(rn); continue; }
                if (!lists.TryGetValue(rn, out var l)) lists[rn] = l = new();
                foreach (var r in refs) if (!l.Contains(r)) l.Add(r);
                wheel[rn] = (int?)it.Element("WheelPosition")?.Attribute("value") ?? 0;
            }

            // Track lists that DLC packs add for scripts to unlock on an existing station aren't named by
            // any station. Give each one to its station by its name, or else by where its audio lives.
            foreach (var (h, tl) in Items)
            {
                if ((string)tl.Attribute("type") != "RadioStationTrackList" || referenced.Contains(h)) continue;
                Names.TryGetValue(h, out var name);
                var desc = $"track list {name ?? h.ToString("X8")} (category {Category(tl)}, {TrackRefs(tl).Count()} tracks)";
                // Media player, Rockstar Editor and hidden lists aren't heard on the radio.
                if (name != null && Regex.IsMatch(name, "(^|_)(AP|EDITOR|REPLAY|HIDDEN|PROMOTER)(_|$)", RegexOptions.IgnoreCase))
                {
                    if (Category(tl) == (int)Cat.Music) Leftover.Add((name, tl));
                    Report.Add($"{desc} is for the media player or editor, skipped"); continue;
                }
                string owner = null;
                if (name != null)
                {
                    var nt = Tokens(name);
                    owner = lists.Keys.Concat(offWheel).Where(k => StationTokens(k).All(t => nt.Contains(t) || (t.StartsWith("MIX") && nt.Any(n => n.StartsWith("MIX"))))).OrderByDescending(k => StationTokens(k).Count).FirstOrDefault();
                }
                if (owner == null)
                {
                    // Otherwise by where its audio lives, if nearly all of it is in one station's folder.
                    var fs = TrackRefs(tl).SelectMany(ContainerHashes).Select(c => Folder(c)).Where(x => x != null).ToList();
                    var top = fs.GroupBy(x => x).OrderByDescending(g => g.Count()).FirstOrDefault();
                    if (top != null && top.Count() >= fs.Count * 0.9)
                    {
                        owner = lists.Keys.Where(k => top.Key.StartsWith(k, StringComparison.OrdinalIgnoreCase)).OrderByDescending(k => k.Length).FirstOrDefault();
                        // A DLC folder that only one station plays from (e.g. dlc_heist3 for iFruit Radio).
                        if (owner == null)
                        {
                            var users = lists.Where(kv => kv.Value.Any(r => Items.TryGetValue(r, out var x) && Category(x) == (int)Cat.Music
                                && TrackRefs(x).SelectMany(ContainerHashes).Any(c => Folder(c) == top.Key))).Select(kv => kv.Key).ToList();
                            if (users.Count == 1) owner = users[0];
                        }
                    }
                }
                if (owner != null && offWheel.Contains(owner)) { Report.Add($"{desc} belongs to {owner}, skipped"); continue; }
                if (owner != null && name != null && Category(tl) == (int)Cat.Music && Regex.IsMatch(name, "ADVERT", RegexOptions.IgnoreCase)) AdLists.Add(tl);
                if (owner != null) { lists[owner].Add(h); Report.Add($"{desc} added to {owner}" + (AdLists.Contains(tl) ? " as adverts" : "")); }
                else
                {
                    if (Category(tl) == (int)Cat.Music) Leftover.Add((name ?? h.ToString("X8"), tl));
                    Report.Add($"{desc} not used by any station");
                }
            }

            foreach (var (rn, l) in lists)
                Stations.Add((rn, wheel[rn], l.Select(x => Items.GetValueOrDefault(x)).Where(x => x != null).ToList()));
        }

        // Container hash -> its folder, so unclaimed track lists can be placed by where their audio lives.
        readonly Func<uint, string> Folder;

        static bool IsOffWheel(XElement settings)
        {
            var f = settings.Element("Flags")?.Attribute("value")?.Value;
            if (f == null || !f.StartsWith("0x")) return false;
            uint flags = Convert.ToUInt32(f.Substring(2), 16);
            return ((flags >> 14) & 3) == 1;
        }

        public static int Category(XElement tl) => (int?)tl.Element("Category")?.Attribute("value") ?? -1;
        public static IEnumerable<uint> TrackRefs(XElement tl) => tl.Element("Tracks")?.Elements("Item").Select(x => Hv(x.Element("SoundRef"))) ?? Enumerable.Empty<uint>();

        public IEnumerable<XElement> NewsLists => Items.Values.Where(i => (string)i.Attribute("type") == "RadioStationTrackList" && Category(i) == (int)Cat.News);

        // The wave containers a sound plays from.
        public IEnumerable<uint> ContainerHashes(uint soundRef)
        {
            if (!Sounds.TryGetValue(soundRef, out var s)) yield break;
            var kids = s.Element("ChildSounds")?.Elements("Item").Select(Hv).ToList() ?? new List<uint> { soundRef };
            foreach (var k in kids)
                if (Sounds.TryGetValue(k, out var ss) && ss.Element("ContainerName") is XElement cn) yield return Hv(cn);
        }
    }

    // The container folders a radio needs: everything its tracks play from, plus each station's own
    // folder (that's where the DJ lines are).
    public static HashSet<string> Plan(RelDb db, Func<uint, string> folderOf, IEnumerable<string> allFolders)
    {
        var need = new HashSet<string>();
        var lists = db.Stations.SelectMany(s => s.Lists).Concat(db.NewsLists);
        foreach (var tl in lists)
            foreach (var r in RelDb.TrackRefs(tl))
                foreach (var c in db.ContainerHashes(r))
                    if (folderOf(c) is string f) need.Add(f);
        var all = allFolders.ToHashSet();
        foreach (var s in db.Stations)
            foreach (var f in all)
                if (f == s.Id.ToLowerInvariant() || NamedFor(s.Id, f)) need.Add(f);
        return need;
    }

    public static Manifest Build(string dataDir, RelDb db, Dictionary<uint, string> text)
    {
        var indexRoot = Path.Combine(dataDir, "index");

        // Index every wave container by the hash the game data uses ("folder/awc").
        var containers = new Dictionary<uint, Container>();
        var byStation = new Dictionary<string, List<Container>>();
        foreach (var sdir in Directory.GetDirectories(indexRoot))
        {
            var dirName = Path.GetFileName(sdir);
            var st = Extractor.DirFolder(dirName);
            foreach (var j in Directory.GetFiles(sdir, "*.json"))
            {
                var info = JsonSerializer.Deserialize<AwcInfo>(File.ReadAllText(j));
                var c = new Container { Station = dirName, Awc = info.Awc, Info = info };
                foreach (var s in info.Streams) c.Streams[s.Hash] = s;
                containers[Jenk.Hash(st + "/" + info.Awc)] = c;
                if (!byStation.TryGetValue(st, out var l)) byStation[st] = l = new();
                l.Add(c);
            }
        }

        var sounds = db.Sounds;
        var m = new Manifest();
        var usedContainers = new HashSet<Container>();

        Track Resolve(uint soundRef)
        {
            if (!sounds.TryGetValue(soundRef, out var s)) return null;
            var kids = s.Element("ChildSounds")?.Elements("Item").Select(Hv).ToList() ?? new List<uint> { soundRef };
            var streams = new List<(StreamInfo si, Container c)>();
            foreach (var k in kids)
            {
                if (!sounds.TryGetValue(k, out var ss)) continue;
                var cn = ss.Element("ContainerName"); var fn = ss.Element("FileName");
                if (cn == null || fn == null) continue;
                if (!containers.TryGetValue(Hv(cn), out var c)) continue;
                if (!c.Streams.TryGetValue(Hv(fn) & 0x1FFFFFFF, out var si)) continue;
                streams.Add((si, c)); usedContainers.Add(c);
            }
            if (streams.Count == 0) return null;
            // Left channel first; markers live on the left stream.
            streams = streams.OrderBy(x => x.si.Name.EndsWith("_right") ? 1 : 0).ToList();
            var main = streams[0];
            var t = new Track
            {
                Name = main.c.Awc,
                Audio = new AudioRef { Files = streams.Select(x => x.c.Station + "/" + x.si.File).Distinct().ToList(), Samples = main.si.Samples, Rate = main.si.Rate }
            };
            ApplyMarkers(t, streams.SelectMany(x => x.si.Markers).ToList(), main.si.Rate, text);
            return t;
        }

        var stationFolders = new Dictionary<Station, List<string>>();
        foreach (var (rn, wheel, lists) in db.Stations)
        {
            var st = new Station { Id = rn, Name = StationName(rn, text), Wheel = wheel };
            var folders = new HashSet<string> { rn.ToLowerInvariant() };
            foreach (var tl in lists)
            {
                var tracks = RelDb.TrackRefs(tl).Select(Resolve).Where(x => x != null).ToList();
                switch (db.AdLists.Contains(tl) ? Cat.Advert : (Cat)RelDb.Category(tl))
                {
                    case Cat.Music: st.Music.AddRange(tracks); break;
                    case Cat.Ident: st.Idents.AddRange(tracks); break;
                    case Cat.Solo: st.Solos.AddRange(tracks); break;
                    case Cat.Advert: st.Ads.AddRange(tracks); break;
                }
            }
            if (st.Music.Count == 0) { Log.Info($"    {rn}: no music found, skipped"); continue; }
            foreach (var t in st.Music.Concat(st.Idents).Concat(st.Solos)) folders.Add(Extractor.DirFolder(t.Audio.Files[0].Split('/')[0]));
            foreach (var f in byStation.Keys) if (NamedFor(rn, f)) folders.Add(f);
            DedupeByName(st.Music); DedupeByName(st.Ads); DedupeByName(st.Idents); DedupeByName(st.Solos);
            stationFolders[st] = folders.ToList();
            m.Stations.Add(st);
        }

        // DJ speech: containers no track list uses (general, intro, time, to...). Base stations keep theirs in
        // their own folder; DLC stations share a folder and prefix them (motomami_intro, hiphop_new_dd_general),
        // or leave them unprefixed when one station owns the folder's speech (Kult FM's "intro" in dlc_hei4_music).
        var leftoverTracks = db.Leftover.Select(x => (x.Name, Tracks: RelDb.TrackRefs(x.List).Select(Resolve).Where(t => t != null).ToList())).Where(x => x.Tracks.Count > 0).ToList();
        var songOwner = new Dictionary<string, Station>();
        foreach (var st in m.Stations) foreach (var t in st.Music) songOwner.TryAdd(t.Name, st);
        var allSongs = songOwner.Keys.Concat(leftoverTracks.SelectMany(x => x.Tracks).Select(t => t.Name)).ToHashSet();
        var known = new Dictionary<uint, string>();
        foreach (var w in SpeechWords.Concat(new[] { "dd_general", "pl_general" }).Concat(allSongs))
            for (int n = 0; n <= 130; n++) { known.TryAdd(Jenk.Stream($"{w}_{n:00}"), $"{w}_{n:00}"); known.TryAdd(Jenk.Stream($"{w}_{n}"), $"{w}_{n:00}"); known.TryAdd(Jenk.Stream($"{w}_{n:000}"), $"{w}_{n:000}"); }

        var lines = new List<(Container c, StreamInfo s, string key, string name)>(); // key: song name or speech word
        var speechFolders = stationFolders.Values.SelectMany(x => x).ToHashSet();
        foreach (var folder in speechFolders)
        foreach (var c in byStation.GetValueOrDefault(folder) ?? new())
        {
            if (usedContainers.Contains(c)) continue;
            foreach (var s in c.Info.Streams)
            {
                var name = s.Name.StartsWith("0x") && known.TryGetValue(s.Hash, out var kn) ? kn : s.Name;
                var mm = Regex.Match(name, @"^(.*)_(\d+)$");
                if (!mm.Success) continue;
                var key = mm.Groups[1].Value;
                if (key.EndsWith("general")) key = "general";
                if (allSongs.Contains(key) || SpeechWords.Contains(key)) lines.Add((c, s, key, name));
            }
        }
        // Who a container belongs to: the station it's named for, else the station whose folder it's in,
        // else the station whose songs most of the folder's unprefixed intros are for.
        var containerOwner = new Dictionary<Container, Station>();
        foreach (var g in lines.GroupBy(x => x.c))
        {
            var c = g.Key; var folder = Extractor.DirFolder(c.Station);
            var owner = m.Stations.Where(st => NamedFor(st.Id, c.Awc)).OrderByDescending(st => StationTokens(st.Id).Count).FirstOrDefault()
                ?? m.Stations.FirstOrDefault(st => folder.StartsWith(st.Id.ToLowerInvariant()))
                ?? m.Stations.Where(st => NamedFor(st.Id, folder)).OrderByDescending(st => StationTokens(st.Id).Count).FirstOrDefault();
            if (owner == null)
                owner = lines.Where(x => Extractor.DirFolder(x.c.Station) == folder && !m.Stations.Any(st => NamedFor(st.Id, x.c.Awc)) && songOwner.ContainsKey(x.key))
                    .GroupBy(x => songOwner[x.key]).OrderByDescending(x => x.Count()).Select(x => x.Key).FirstOrDefault();
            if (owner != null) containerOwner[c] = owner;
        }

        // Unclaimed song lists join the station whose DJ introduces at least half of their songs
        // (e.g. Dr. Dre's Contract songs, which DJ Pooh introduces on Radio Los Santos).
        foreach (var (name, tracks) in leftoverTracks)
        {
            var owners = tracks.Select(t => lines.FirstOrDefault(x => x.key == t.Name && containerOwner.ContainsKey(x.c))).Where(x => x.c != null).Select(x => containerOwner[x.c]).ToList();
            var top = owners.GroupBy(x => x).OrderByDescending(x => x.Count()).FirstOrDefault();
            if (top == null || top.Count() < tracks.Count / 2.0) continue;
            top.Key.Music.AddRange(tracks); DedupeByName(top.Key.Music);
            foreach (var t in tracks) songOwner.TryAdd(t.Name, top.Key);
            Log.Info($"    {name}: {tracks.Count} songs added to {top.Key.Name} (its DJ introduces them)");
        }

        foreach (var (c, s, key, lineName) in lines)
        {
            if (!containerOwner.TryGetValue(c, out var st)) continue;
            string k = key;
            if (allSongs.Contains(key)) { if (!st.Music.Any(t => t.Name == key)) continue; k = "intro:" + key; }
            var tr = new Track { Name = lineName, Audio = new AudioRef { Files = { c.Station + "/" + s.File }, Samples = s.Samples, Rate = s.Rate } };
            if (!st.Speech.TryGetValue(k, out var l)) st.Speech[k] = l = new();
            l.Add(tr);
        }

        foreach (var st in m.Stations)
        {
            double avg = st.Music.Average(x => x.Audio.Seconds);
            bool noAds = st.Ads.Count == 0;
            st.Kind = st.Id.Contains("_TALK") ? "talk" : (noAds && st.Idents.Count == 0) || (noAds && avg > 600) ? "mix" : avg > 600 ? "talk" : "music";
        }
        m.Stations = m.Stations.OrderBy(s => s.Wheel).ThenBy(s => s.Id).ToList();

        // News: the biggest news list (the game swaps lists as the story progresses).
        var news = db.NewsLists.Select(tl => RelDb.TrackRefs(tl).Select(Resolve).Where(x => x != null).ToList())
            .OrderByDescending(l => l.Count).FirstOrDefault();
        if (news != null) m.News = news;

        // Minimum time between categories: the RadioTrackCategoryData table whose values are in milliseconds.
        foreach (var it in db.Items.Values.Where(i => (string)i.Attribute("type") == "RadioTrackCategoryData"))
        {
            var vals = it.Element("Items").Elements("Item").Select(x => ((int)x.Element("Track").Attribute("value"), (int)x.Element("PreDelay").Attribute("value"))).ToList();
            if (vals.Any(v => v.Item2 >= 10000)) { m.MinGapMs = vals.GroupBy(v => v.Item1).ToDictionary(g => g.Key, g => g.First().Item2); break; }
        }
        return m;
    }

    static HashSet<string> Tokens(string s) => s.ToUpperInvariant().Split(new[] { '_', '-', '/', '+' }, StringSplitOptions.RemoveEmptyEntries).ToHashSet();

    // The words that identify a station in other names: RADIO_34_DLC_HEI4_KULT -> HEI4, KULT.
    public static List<string> StationTokens(string rn)
    {
        var t = rn.ToUpperInvariant().Split('_').ToList();
        if (t.Count > 1 && t[0] == "RADIO" && t[1].All(char.IsDigit)) t.RemoveRange(0, 2);
        t.RemoveAll(x => x == "DLC" || x == "RADIO");
        return t.Count > 0 ? t : new List<string> { rn.ToUpperInvariant() };
    }

    // A folder or container whose name carries all of a station's words (e.g. dlc_hei4_music/kult_general for Kult FM).
    public static bool NamedFor(string rn, string name) { var nt = Tokens(name); return StationTokens(rn).All(nt.Contains); }

    static string StationName(string rn, Dictionary<uint, string> text)
    {
        if (DisplayNames.TryGetValue(rn, out var n)) return n;
        if (text != null && text.TryGetValue(Jenk.Hash(rn), out var t) && !string.IsNullOrWhiteSpace(t)) return Regex.Replace(t, "~[a-zA-Z]~", "").Trim();
        if (DlcNames.TryGetValue(rn, out n)) return n;
        var s = Regex.Replace(rn, @"^RADIO_\d+_(DLC_)?", "").Replace('_', ' ').ToLowerInvariant();
        return System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(s);
    }

    static void DedupeByName(List<Track> l)
    {
        var seen = new HashSet<string>();
        l.RemoveAll(t => !seen.Add(t.Name));
    }

    static readonly uint MkTrackId = 0xA6D93246, MkDj = 0x08DBA0F8;

    static void ApplyMarkers(Track t, List<MarkerInfo> markers, int rate, Dictionary<uint, string> text)
    {
        foreach (var mk in markers.OrderBy(x => x.Offset))
        {
            double at = (double)mk.Offset / rate;
            if (mk.Name == MkDj)
            {
                if (mk.Value == Jenk.Hash("intro_start")) t.IntroStart = at;
                else if (mk.Value == Jenk.Hash("intro_end")) t.IntroEnd = at;
                else if (mk.Value == Jenk.Hash("outro_start")) t.OutroStart = at;
            }
            else if (mk.Name == MkTrackId)
            {
                int id = (int)BitConverter.Int32BitsToSingle((int)mk.Value);
                if (t.Cues.Any(c => c.Id == id)) continue;
                var cue = new TextCue { At = at, Id = id };
                if (text != null)
                {
                    text.TryGetValue(Jenk.Hash(id + "S"), out var title);
                    text.TryGetValue(Jenk.Hash(id + "A"), out var artist);
                    cue.Title = title; cue.Artist = artist;
                }
                t.Cues.Add(cue);
            }
        }
        if (t.IntroEnd >= 0 && t.IntroStart < 0) t.IntroStart = 0;
    }

    static uint Hv(XElement e)
    {
        var s = e?.Value?.Trim() ?? "";
        if (s.StartsWith("hash_")) return Convert.ToUInt32(s.Substring(5), 16);
        if (s.StartsWith("0x")) return Convert.ToUInt32(s.Substring(2), 16);
        return Jenk.Hash(s);
    }
}
