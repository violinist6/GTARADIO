using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace GTARadio;

class Server
{
    readonly Manifest M;
    readonly Dictionary<string, StationRunner> Runners;
    readonly Settings Cfg;
    readonly string AudioRoot, IconDir;
    HttpListener L;
    public string Url { get; private set; }
    public PhoneExport Phone;

    public Server(Manifest m, Dictionary<string, StationRunner> runners, Settings cfg, string audioRoot, string iconDir)
    {
        M = m; Runners = runners; Cfg = cfg; AudioRoot = audioRoot; IconDir = iconDir;
    }

    public void Start()
    {
        for (int port = 7458; port < 7470; port++)
        {
            try
            {
                Url = $"http://localhost:{port}/";
                L = new HttpListener();
                L.Prefixes.Add(Url); L.Start();
                break;
            }
            catch (HttpListenerException) { try { L.Close(); } catch { } L = null; }
        }
        if (L == null) throw new Exception("Couldn't open a local port for the radio (7458-7469 are all in use). Is it already running?");
        _ = Task.Run(async () =>
        {
            while (L.IsListening)
            {
                var ctx = await L.GetContextAsync();
                _ = Task.Run(() => Handle(ctx));
            }
        });
    }

    async Task Handle(HttpListenerContext ctx)
    {
        var path = ctx.Request.Url.AbsolutePath;
        var res = ctx.Response;
        try
        {
            if (path.StartsWith("/live/")) { await Live(path.Substring(6), res); return; }
            if (path.StartsWith("/icons/"))
            {
                var id = Path.GetFileNameWithoutExtension(path.Substring(7));
                var f = M.Stations.Any(x => x.Id == id) ? IconPath(id) : null;
                if (f == null) { res.StatusCode = 404; res.Close(); return; }
                res.ContentType = "image/png";
                using (var fs = File.OpenRead(f)) await fs.CopyToAsync(res.OutputStream);
                res.Close(); return;
            }
            if (path == "/api/phone")
            {
                if (ctx.Request.QueryString["start"] == "1" && Phone.State != "running") _ = Task.Run(Phone.Run);
                Json(res, Phone.Status()); return;
            }
            if (path == "/api/stations") { Json(res, Stations()); return; }
            if (path == "/api/settings")
            {
                var q = ctx.Request.QueryString["news"];
                if (q != null) Cfg.News = q == "1";
                Json(res, Cfg); return;
            }
            var name = path == "/" ? "index.html" : path.TrimStart('/');
            using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("www/" + name);
            if (s == null) { res.StatusCode = 404; res.Close(); return; }
            res.ContentType = name.EndsWith(".js") ? "text/javascript" : name.EndsWith(".css") ? "text/css" : name.EndsWith(".svg") ? "image/svg+xml" : "text/html; charset=utf-8";
            res.Headers["Cache-Control"] = "no-store";
            await s.CopyToAsync(res.OutputStream);
            res.Close();
        }
        catch (Exception) { try { res.Abort(); } catch { } }
    }

    string IconPath(string id)
    {
        var f = Path.Combine(IconDir, id + ".png");
        return File.Exists(f) ? f : null;
    }

    void Json(HttpListenerResponse res, object o)
    {
        var b = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(o, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        res.ContentType = "application/json"; res.Headers["Cache-Control"] = "no-store";
        res.OutputStream.Write(b); res.Close();
    }

    object Stations()
    {
        long now = Clock.Now;
        return M.Stations.Select(st => new { id = st.Id, name = st.Name, kind = st.Kind, icon = IconPath(st.Id) != null, now = NowPlaying(Runners[st.Id], now) }).ToList();
    }

    public static object NowPlaying(StationRunner r, long now)
    {
        var seg = r.At(now);
        double pos = (double)(now - seg.Start) / StationRunner.Rate;
        string title = null, artist = null;
        switch (seg.Kind)
        {
            case "music":
                var cue = seg.Track.Cues.LastOrDefault(c => c.At <= pos + (double)seg.SourceOffset / StationRunner.Rate) ?? seg.Track.Cues.FirstOrDefault();
                title = cue?.Title; artist = cue?.Artist;
                if (title == null && r.St.Kind != "mix") title = Pretty(seg.Track.Name);
                if (r.St.Kind == "talk") { artist = null; title ??= Pretty(seg.Track.Name); }
                break;
            case "ad": title = "Advert"; break;
            case "news": title = "Weazel News"; break;
            case "ident": title = r.St.Name; break;
            default: title = "DJ"; break;
        }
        var dj = seg.Overlays.Any(o => pos * StationRunner.Rate >= o.At && pos * StationRunner.Rate < o.At + o.Len);
        return new { kind = seg.Kind, title, artist, dj, pos, len = (double)seg.Len / StationRunner.Rate };
    }

    static string Pretty(string n)
    {
        n = n.Replace("mono_", "").Replace('_', ' ').Trim();
        return System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(n);
    }

    async Task Live(string id, HttpListenerResponse res)
    {
        if (!Runners.TryGetValue(id, out var runner)) { res.StatusCode = 404; res.Close(); return; }
        res.ContentType = "application/octet-stream";
        res.Headers["Cache-Control"] = "no-store";
        res.SendChunked = true;
        const int frames = 4800; // 100 ms
        long lead = StationRunner.Rate * 3 / 2;
        long t = Clock.Now;
        var buf = new float[frames * 2];
        var bytes = new byte[frames * 4];
        using var mixer = new Mixer(AudioRoot);
        var outStream = res.OutputStream;
        try
        {
            while (true)
            {
                long wait = t - (Clock.Now + lead);
                if (wait > 0) await Task.Delay((int)(wait * 1000 / StationRunner.Rate));
                mixer.Render(runner, t, frames, buf);
                for (int i = 0; i < frames * 2; i++)
                {
                    float v = Math.Clamp(buf[i], -1f, 1f);
                    short s = (short)(v * 32767);
                    bytes[i * 2] = (byte)s; bytes[i * 2 + 1] = (byte)(s >> 8);
                }
                await outStream.WriteAsync(bytes);
                t += frames;
            }
        }
        catch (HttpListenerException) { }
        catch (IOException) { }
        catch (Exception ex) { Log.Warn("Stream error on " + id + ": " + ex); }
        finally { try { res.Abort(); } catch { } }
    }
}
