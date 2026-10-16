using System.Text.RegularExpressions;

namespace GTARadio;

// Finds the GTA V install (for the base game's x64 folder) and its update folder (updates and DLC packs).
static class GameFolders
{
    static bool HasBase(string d) => Directory.Exists(Path.Combine(d, "x64", "audio", "sfx"));
    static bool HasUpdate(string d) => File.Exists(Path.Combine(d, "update", "update.rpf")) || Directory.Exists(Path.Combine(d, "update", "x64", "dlcpacks"));

    public static (string game, string update) Find(string baseDir, string gameArg)
    {
        var cands = new List<string>();
        if (gameArg != null) cands.Add(gameArg.Trim('"'));
        cands.Add(baseDir);
        cands.AddRange(Installs());
        cands = cands.Where(Directory.Exists).Select(d => Path.GetFullPath(d)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        string game = cands.FirstOrDefault(d => HasBase(d) && HasUpdate(d)) ?? cands.FirstOrDefault(HasBase);
        string upDir = new[] { game }.Concat(cands).Where(d => d != null && HasUpdate(d)).Select(d => Path.Combine(d, "update")).FirstOrDefault();
        return (game, upDir);
    }

    static IEnumerable<string> Installs()
    {
        var names = new[] { "Grand Theft Auto V Enhanced", "Grand Theft Auto V" };
        var libs = new List<string>();
        foreach (var steam in new[] { @"C:\Program Files (x86)\Steam", @"C:\Program Files\Steam" })
        {
            libs.Add(steam);
            var vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
            try
            {
                if (File.Exists(vdf))
                    foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\""))
                        libs.Add(m.Groups[1].Value.Replace(@"\\", @"\"));
            }
            catch { }
        }
        foreach (var lib in libs.Distinct(StringComparer.OrdinalIgnoreCase))
            foreach (var n in names) yield return Path.Combine(lib, "steamapps", "common", n);
        foreach (var root in new[] { @"C:\Program Files\Epic Games", @"D:\Epic Games", @"C:\Program Files\Rockstar Games", @"D:\Rockstar Games" })
            foreach (var n in new[] { "GTAVEnhanced", "GTAV", "Grand Theft Auto V Enhanced", "Grand Theft Auto V" })
                yield return Path.Combine(root, n);
    }
}
