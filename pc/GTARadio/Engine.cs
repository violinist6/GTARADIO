using System.Diagnostics;

namespace GTARadio;

class Overlay
{
    public long At;          // samples from segment start
    public Track Line;
    public long Len;
}

class Segment
{
    public long Start;       // global sample time (48 kHz since program start)
    public long Len;
    public long SourceOffset; // samples into the audio where this segment begins
    public Track Track;
    public string Kind;      // music, ident, ad, news, solo, speech
    public List<Overlay> Overlays = new();
    public long End => Start + Len;
}

// Decides what each station plays. Every station keeps running on the shared clock whether
// or not anyone is listening; tuning in just reads the timeline at "now".
class StationRunner
{
    public const int Rate = 48000;
    public readonly Station St;
    readonly Manifest M;
    readonly Random Rng;
    readonly Settings Cfg;
    public readonly List<Segment> Timeline = new();
    readonly Queue<Segment> Pending = new();
    readonly Dictionary<string, Queue<string>> History = new();
    readonly Dictionary<string, long> LastPlayed = new();
    int SongsSinceBreak;
    int NextBreakAfter;
    readonly object Lock = new();

    public StationRunner(Station st, Manifest m, Settings cfg, int seed)
    {
        St = st; M = m; Cfg = cfg; Rng = new Random(seed);
        NextBreakAfter = Rng.Next(2, 5);
    }

    long MinGap(int cat) => M.MinGapMs.TryGetValue(cat, out var ms) ? ms * (long)Rate / 1000 : 0;

    public void Ensure(long upTo)
    {
        lock (Lock)
        {
            if (Timeline.Count == 0)
            {
                // Like the game, a station starts somewhere in the middle of something.
                var first = Next(0);
                long into = (long)(Rng.NextDouble() * first.Len * 0.9);
                first.Start = -into;
                Timeline.Add(first);
            }
            while (Timeline[^1].End < upTo)
            {
                var s = Next(Timeline[^1].End);
                s.Start = Timeline[^1].End;
                Timeline.Add(s);
            }
            // Keep a little history for the now-playing display, drop the rest.
            while (Timeline.Count > 8 && Timeline[1].End < upTo - 30L * 60 * Rate) Timeline.RemoveAt(0);
        }
    }

    public Segment At(long t)
    {
        Ensure(t + 5L * 60 * Rate);
        lock (Lock)
        {
            foreach (var s in Timeline) if (t >= s.Start && t < s.End) return s;
            return Timeline[^1];
        }
    }

    public List<Segment> Range(long from, long to)
    {
        Ensure(to + 60L * Rate);
        lock (Lock) return Timeline.Where(s => s.End > from && s.Start < to).ToList();
    }

    Track Pick(string key, List<Track> list, int keep = 10)
    {
        if (list == null || list.Count == 0) return null;
        if (!History.TryGetValue(key, out var h)) History[key] = h = new Queue<string>();
        keep = Math.Min(keep, list.Count - 1);
        var choices = list.Where(t => !h.Contains(t.Name)).ToList();
        if (choices.Count == 0) choices = list;
        var pick = choices[Rng.Next(choices.Count)];
        h.Enqueue(pick.Name);
        while (h.Count > keep) h.Dequeue();
        return pick;
    }

    static Segment Seg(Track t, string kind) => new() { Track = t, Kind = kind, Len = t.Audio.Samples * Rate / Math.Max(1, t.Audio.Rate) };

    long Since(string what, long now) => LastPlayed.TryGetValue(what, out var t) ? now - t : long.MaxValue / 4;

    Segment Next(long now)
    {
        if (Pending.Count > 0) return Pending.Dequeue();
        switch (St.Kind)
        {
            case "mix": return Seg(Pick("music", St.Music, St.Music.Count), "music");
            case "talk": return NextTalk(now);
            default: return NextMusic(now);
        }
    }

    Segment NextTalk(long now)
    {
        var show = Seg(Pick("music", St.Music), "music");
        QueueBreak(now + show.Len, newsAllowed: true);
        return show;
    }

    void QueueBreak(long at, bool newsAllowed)
    {
        if (newsAllowed && Cfg.News && M.News.Count > 0 && Since("news", at) >= MinGap((int)Cat.News) && Rng.NextDouble() < 0.4)
        {
            Pending.Enqueue(Seg(Pick("news", M.News, 20), "news"));
            LastPlayed["news"] = at;
        }
        else
        {
            int n = St.Ads.Count == 0 ? 0 : Rng.Next(2, 4);
            for (int i = 0; i < n; i++) Pending.Enqueue(Seg(Pick("ad", St.Ads, 20), "ad"));
            LastPlayed["ad"] = at;
        }
        var id = Pick("ident", St.Idents);
        if (id != null) Pending.Enqueue(Seg(id, "ident"));
    }

    Segment NextMusic(long now)
    {
        var song = Pick("music", St.Music);
        var seg = Seg(song, "music");
        TryIntro(seg, now);
        SongsSinceBreak++;

        long end = now + seg.Len;
        string follow = "none";
        if (SongsSinceBreak >= NextBreakAfter && St.Ads.Count > 0)
            follow = Cfg.News && M.News.Count > 0 && Since("news", end) >= MinGap((int)Cat.News) && Rng.NextDouble() < 0.4 ? "news" : "ad";
        else if (St.Solos.Count > 0 && Since("solo", end) >= MinGap((int)Cat.Solo) && Rng.NextDouble() < 0.12) follow = "solo";
        else if (St.Idents.Count > 0 && Rng.NextDouble() < 0.4) follow = "ident";

        if (follow is "ad" or "news")
        {
            SongsSinceBreak = 0; NextBreakAfter = Rng.Next(2, 5);
            // The DJ throws to the break over the song's outro, or straight after it.
            var lead = Pick("to_" + follow, St.Speech.GetValueOrDefault("to_" + (follow == "ad" ? "ad" : "news")), 4);
            if (lead != null && !TryOutro(seg, lead)) Pending.Enqueue(Seg(lead, "speech"));
            if (follow == "news")
            {
                Pending.Enqueue(Seg(Pick("news", M.News, 20), "news"));
                LastPlayed["news"] = end;
            }
            else
            {
                int n = Rng.Next(2, 4);
                for (int i = 0; i < n; i++) Pending.Enqueue(Seg(Pick("ad", St.Ads, 20), "ad"));
                LastPlayed["ad"] = end;
            }
            var id = Pick("ident", St.Idents);
            if (id != null) Pending.Enqueue(Seg(id, "ident"));
        }
        else if (follow == "solo")
        {
            Pending.Enqueue(Seg(Pick("solo", St.Solos), "solo"));
            LastPlayed["solo"] = end;
        }
        else if (follow == "ident")
        {
            Pending.Enqueue(Seg(Pick("ident", St.Idents), "ident"));
        }
        else if (Rng.NextDouble() < 0.25)
        {
            var g = Pick("general", St.Speech.GetValueOrDefault("general"), 8);
            if (g != null) TryOutro(seg, g);
        }
        return seg;
    }

    void TryIntro(Segment seg, long now)
    {
        var t = seg.Track;
        if (t.IntroEnd <= 0 || Rng.NextDouble() > 0.6) return;
        double window = t.IntroEnd - Math.Max(0, t.IntroStart);
        var hour = DateTime.Now.Hour;
        string tod = hour >= 5 && hour < 12 ? "morning" : hour >= 12 && hour < 17 ? "afternoon" : hour >= 17 && hour < 22 ? "evening" : "night";
        var options = new List<(string key, double w)>
        {
            ("intro:" + t.Name, 3), (tod, 1.5), ("general", 1)
        };
        // Weighted choice among boxes that have a line short enough for the window.
        var usable = options.Select(o => (o.key, o.w, lines: St.Speech.GetValueOrDefault(o.key)?.Where(l => l.Audio.Seconds <= window + 0.3).ToList()))
            .Where(o => o.lines != null && o.lines.Count > 0).ToList();
        if (usable.Count == 0) return;
        double r = Rng.NextDouble() * usable.Sum(o => o.w);
        var box = usable.First();
        foreach (var o in usable) { if ((r -= o.w) <= 0) { box = o; break; } }
        var line = Pick(box.key, box.lines, 6);
        long len = line.Audio.Samples * Rate / line.Audio.Rate;
        // Speech is timed to finish as the vocals come in.
        long at = (long)(t.IntroEnd * Rate) - len;
        at = Math.Max(at, (long)(Math.Max(0, t.IntroStart) * Rate));
        seg.Overlays.Add(new Overlay { At = at, Line = line, Len = len });
    }

    bool TryOutro(Segment seg, Track line)
    {
        var t = seg.Track;
        if (t.OutroStart <= 0) return false;
        long len = line.Audio.Samples * Rate / line.Audio.Rate;
        long at = (long)(t.OutroStart * Rate);
        if (at + len > seg.Len) return false;
        seg.Overlays.Add(new Overlay { At = at, Line = line, Len = len });
        return true;
    }
}

class Settings
{
    public bool News { get; set; } = false;
}

static class Clock
{
    static readonly Stopwatch Sw = Stopwatch.StartNew();
    public static long Now => (long)(Sw.Elapsed.TotalSeconds * StationRunner.Rate);
}
