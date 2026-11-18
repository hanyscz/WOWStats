namespace WowStats;

public sealed class ForeverOptions
{
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
    public string Region { get; set; } = "eu";
    public string Locale { get; set; } = "en_GB";
    /// <summary>Jak dlouho se výsledek bere z disku bez nového dotazu.</summary>
    public TimeSpan Ttl { get; set; } = TimeSpan.FromMinutes(15);
    /// <summary>Výchozí: %LOCALAPPDATA%\wowstats (Windows) nebo ~/.cache/wowstats, stejně jako Python verze.</summary>
    public string? CacheDir { get; set; }
    /// <summary>Doplnit názvy itemů, které Blizzard nezná, z Wowheadu (neoficiální endpoint). Výsledky se cachují.</summary>
    public bool UseWowhead { get; set; } = true;

    /// <summary>Pořadí: proměnné prostředí, pak .env soubor. Pro override nastav vlastnosti po zavolání.</summary>
    public static ForeverOptions FromEnvironment(string? envFile = ".env")
    {
        var file = LoadDotEnv(envFile);
        string? Get(string k) =>
            Environment.GetEnvironmentVariable(k) is { Length: > 0 } v ? v : file.GetValueOrDefault(k);

        var o = new ForeverOptions
        {
            ClientId = Get("BLIZZARD_CLIENT_ID") ?? "",
            ClientSecret = Get("BLIZZARD_CLIENT_SECRET") ?? "",
            Region = Get("BLIZZARD_REGION") ?? "eu",
            Locale = Get("BLIZZARD_LOCALE") ?? "en_GB",
            UseWowhead = Get("WOWSTATS_WOWHEAD")?.ToLowerInvariant() is not ("0" or "false" or "no" or "off"),
        };
        if (int.TryParse(Get("CACHE_TTL_SECONDS"), out var s)) o.Ttl = TimeSpan.FromSeconds(s);
        return o;
    }

    internal string ResolveCacheDir()
    {
        if (!string.IsNullOrEmpty(CacheDir)) return CacheDir;
        if (Environment.GetEnvironmentVariable("WOWSTATS_CACHE_DIR") is { Length: > 0 } e) return e;
        if (OperatingSystem.IsWindows())
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "wowstats");
        var xdg = Environment.GetEnvironmentVariable("XDG_CACHE_HOME");
        return Path.Combine(string.IsNullOrEmpty(xdg)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache") : xdg, "wowstats");
    }

    private static Dictionary<string, string> LoadDotEnv(string? path)
    {
        var d = new Dictionary<string, string>();
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return d;
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            var i = line.IndexOf('=');
            if (line.Length == 0 || line[0] == '#' || i < 1) continue;
            d[line[..i].Trim()] = line[(i + 1)..].Trim().Trim('"', '\'');
        }
        return d;
    }
}
