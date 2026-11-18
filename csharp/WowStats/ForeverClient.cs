using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WowStats;

/// <summary>Klient pro neoficiální WoW Forever API. Žádný server, cache (token, jména, výsledky) je na disku.</summary>
public sealed partial class ForeverClient : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = false };
    private static readonly HashSet<string> StatSkip = ["_links", "character", "power_type"];

    // id statistiky -> klíč v Lifetime.Highlights (id jsou stabilní, názvy jen anglicky)
    private static readonly Dictionary<int, string> LifetimeKeys = new()
    {
        [60] = "deaths", [1501] = "deathsFromPlayers", [318] = "deathsFromOppositeFaction", [114] = "deathsFalling",
        [112] = "deathsDrowning", [115] = "deathsFireLava", [321] = "deathsDungeonsRaids",
        [1197] = "totalKills", [588] = "honorableKills", [1487] = "killingBlows", [98] = "questsCompleted",
        [94] = "questsAbandoned", [64222] = "timesCamped", [839] = "battlegroundsPlayed", [840] = "battlegroundsWon",
        [932] = "dungeonsEntered", [349] = "flightPaths", [353] = "timesHearthed", [1456] = "fishCaught",
    };

    private readonly ForeverOptions _o;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly string _cache;
    private readonly object _lock = new();
    private Dictionary<string, string?>? _names;
    private Dictionary<string, ItemEntry>? _items;
    private bool _itemsDirty;
    private readonly SemaphoreSlim _whGate = new(1, 1);
    private DateTimeOffset _whLast = DateTimeOffset.MinValue;

    private const string WowheadUrl = "https://nether.wowhead.com/forever/tooltip/item/{0}"; // neoficiální
    private static readonly TimeSpan WowheadGap = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan NegTtl = TimeSpan.FromDays(7); // nenalezený item se zkusí znovu až po 7 dnech

    public ForeverClient(ForeverOptions options, HttpClient? http = null)
    {
        _o = options;
        _ownsHttp = http is null;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        _cache = options.ResolveCacheDir();
    }

    public void Dispose() { if (_ownsHttp) _http.Dispose(); }

    [GeneratedRegex(@"^[\w-]{1,60}$")]
    private static partial Regex SlugRe();

    // ---------- veřejné API ----------

    /// <param name="name">Slug nebo "Jmeno Prijmeni".</param>
    public async Task<Character> GetCharacterAsync(string name, CharacterSections sections = CharacterSections.All,
        TimeSpan? ttl = null, bool refresh = false, CancellationToken ct = default)
    {
        var slug = name.Trim().ToLowerInvariant().Replace(' ', '-');
        if (!SlugRe().IsMatch(slug)) throw new CharacterNotFoundException($"Neplatné jméno postavy: '{name}'");

        var file = Path.Combine(_cache, $"cs-char-{_o.Region}-{slug}-{(int)sections}.json");
        return await CachedAsync(file, ttl, refresh, () => FetchAsync(slug, sections, ct), c => c with { Stale = true });
    }

    /// <param name="name">Slug nebo název guildy, např. "Nazev Guildy".</param>
    public async Task<Guild> GetGuildAsync(string name, TimeSpan? ttl = null, bool refresh = false,
        CancellationToken ct = default)
    {
        var slug = name.Trim().ToLowerInvariant().Replace(' ', '-');
        if (!SlugRe().IsMatch(slug)) throw new GuildNotFoundException($"Neplatný název guildy: '{name}'");
        var file = Path.Combine(_cache, $"cs-guild-{_o.Region}-{slug}.json");
        return await CachedAsync(file, ttl, refresh, () => FetchGuildAsync(slug, ct), g => g with { Stale = true });
    }

    // ---------- interní ----------

    private sealed record CacheEntry<T>(double Ts, T Data);

    private async Task<T> CachedAsync<T>(string file, TimeSpan? ttl, bool refresh, Func<Task<T>> fetch,
        Func<T, T> markStale) where T : class
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var cached = ReadJson<CacheEntry<T>>(file);
        if (cached is not null && !refresh && now - cached.Ts < (ttl ?? _o.Ttl).TotalSeconds)
            return cached.Data;

        T data;
        try { data = await fetch(); }
        catch (UpstreamException) when (cached is not null)
        {
            return markStale(cached.Data); // radši stará data než chyba
        }
        WriteJson(file, new CacheEntry<T>(now, data));
        return data;
    }

    private async Task<Guild> FetchGuildAsync(string slug, CancellationToken ct)
    {
        var baseP = $"/data/wow/guild/{Uri.EscapeDataString(slug)}";
        var (st, g) = await ApiAsync(baseP, "profile", null, ct);
        if (st == 404) throw new GuildNotFoundException($"Guilda nenalezena: {slug}");
        if (st != 200 || g is not { } guild) throw new UpstreamException($"Guilda: HTTP {st}");
        var (st2, r) = await ApiAsync($"{baseP}/roster", "profile", null, ct);
        if (st2 != 200 || r is not { } roster) throw new UpstreamException($"Roster: HTTP {st2}");

        var members = new List<GuildMember>();
        if (roster.Path("members") is { ValueKind: JsonValueKind.Array } arr)
            foreach (var x in arr.EnumerateArray())
            {
                if (x.Path("character") is not { } c) continue;
                var cid = c.Path("playable_class", "id")?.GetInt32();
                var rid = c.Path("playable_race", "id")?.GetInt32();
                members.Add(new GuildMember(
                    c.Path("name")?.GetString(), c.Path("surname")?.GetString(), c.Path("slug")?.GetString(),
                    c.Path("level")?.GetInt32(),
                    cid is { } a ? await NameAsync($"/data/wow/playable-class/{a}", "static", ct) : null,
                    rid is { } b ? await NameAsync($"/data/wow/playable-race/{b}", "static", ct) : null,
                    c.Path("faction", "type")?.GetString() is { Length: > 0 } f ? Title(f) : null,
                    x.Path("rank")?.GetInt32()));
            }
        var sorted = members.OrderBy(m => m.Rank ?? 99).ThenByDescending(m => m.Level ?? 0)
            .ThenBy(m => m.Name, StringComparer.Ordinal).ToList();

        long? created = guild.Path("created_timestamp")?.GetInt64();
        var rulesetId = guild.Path("ruleset", "id")?.GetInt32();
        return new Guild(
            Name: guild.Path("name")?.GetString() ?? slug,
            Slug: slug,
            Id: guild.Path("id")?.GetInt32() ?? 0,
            Faction: guild.Path("faction", "type")?.GetString() is { Length: > 0 } gf ? Title(gf) : null,
            MemberCount: guild.Path("member_count")?.GetInt32() ?? sorted.Count,
            AchievementPoints: guild.Path("achievement_points")?.GetInt32() ?? 0,
            CreatedAt: created is { } cr ? DateTimeOffset.FromUnixTimeMilliseconds(cr) : null,
            Ruleset: rulesetId is { } rid2 ? await NameAsync($"/data/wow/ruleset/{rid2}", "dynamic", ct) : null,
            Members: sorted,
            FetchedAt: DateTimeOffset.UtcNow,
            Stale: false);
    }

    private async Task<Character> FetchAsync(string slug, CharacterSections secs, CancellationToken ct)
    {
        var baseP = $"/profile/wow/character/{Uri.EscapeDataString(slug)}";
        var (st, p) = await ApiAsync(baseP, "profile", null, ct);
        if (st == 404) throw new CharacterNotFoundException($"Postava nenalezena: {slug}");
        if (st != 200 || p is not { } prof) throw new UpstreamException($"Profil: HTTP {st}");

        async Task<JsonElement?> Sub(string path) =>
            (await ApiAsync($"{baseP}/{path}", "profile", null, ct)) is (200, { } b) ? b : null;

        JsonElement? stats = secs.HasFlag(CharacterSections.Stats) ? await Sub("statistics") : null;
        JsonElement? gear = secs.HasFlag(CharacterSections.Gear) ? await Sub("equipment") : null;
        JsonElement? profs = secs.HasFlag(CharacterSections.Professions) ? await Sub("professions") : null;
        JsonElement? media = secs.HasFlag(CharacterSections.Media) ? await Sub("character-media") : null;
        // názvy statistik vrací API jen anglicky, jinak jsou null
        JsonElement? lifetime = secs.HasFlag(CharacterSections.Lifetime)
            ? (await ApiAsync($"{baseP}/achievements/statistics", "profile", "en_GB", ct)) is (200, { } lt) ? lt : null
            : null;

        var rulesetId = prof.Path("ruleset", "id")?.GetInt32();
        long? login = prof.Path("last_login_timestamp")?.GetInt64();

        var gearList = gear is null ? null : await BuildGearAsync(gear.Value, ct);
        var profList = profs is null ? null : await BuildProfessionsAsync(profs.Value, ct);
        var assets = media?.Path("assets")?.EnumerateArray()
            .ToDictionary(a => a.Path("key")!.Value.GetString()!, a => a.Path("value")?.GetString());

        return new Character(
            Name: prof.Path("name")!.Value.GetString()!,
            Surname: prof.Path("surname")?.GetString(),
            Slug: slug,
            Level: prof.Path("level")!.Value.GetInt32(),
            Race: await NameAsync($"/data/wow/playable-race/{prof.Path("race", "id")!.Value.GetInt32()}", "static", ct),
            CharacterClass: await NameAsync($"/data/wow/playable-class/{prof.Path("character_class", "id")!.Value.GetInt32()}", "static", ct),
            Faction: Title(prof.Path("faction", "type")?.GetString() ?? ""),
            Guild: prof.Path("guild", "name")?.GetString(),
            Ruleset: rulesetId is { } rid ? await NameAsync($"/data/wow/ruleset/{rid}", "dynamic", ct) : null,
            ItemLevel: prof.Path("equipped_item_level")?.GetInt32(),
            LastLogin: login is { } l ? DateTimeOffset.FromUnixTimeMilliseconds(l) : null,
            Stats: stats is null ? null : BuildStats(stats.Value),
            Gear: gearList,
            Professions: profList,
            Lifetime: lifetime is null ? null : BuildLifetime(lifetime.Value),
            AvatarUrl: assets?.GetValueOrDefault("avatar"),
            RenderUrl: assets?.GetValueOrDefault("main-sd-raw"),
            FetchedAt: DateTimeOffset.UtcNow,
            Stale: false);
    }

    private static LifetimeStats? BuildLifetime(JsonElement doc)
    {
        if (doc.Path("categories") is not { ValueKind: JsonValueKind.Array } cats) return null;
        var rows = new List<LifetimeStat>();
        void Walk(JsonElement list, string path)
        {
            foreach (var x in list.EnumerateArray())
            {
                var name = x.Path("name")?.GetString();
                var p = path.Length == 0 ? name ?? "" : $"{path}/{name}";
                if (x.Path("statistics") is { ValueKind: JsonValueKind.Array } stats)
                    foreach (var s in stats.EnumerateArray())
                        if (s.Path("quantity") is { ValueKind: JsonValueKind.Number } q && s.Path("id") is { } id)
                            rows.Add(new LifetimeStat(id.GetInt32(), p, s.Path("name")?.GetString(),
                                Math.Round(q.GetDouble(), 2), s.Path("description")?.GetString()));
                if (x.Path("sub_categories") is { ValueKind: JsonValueKind.Array } subs) Walk(subs, p);
            }
        }
        Walk(cats, "");

        var byId = rows.GroupBy(r => r.Id).ToDictionary(g => g.Key, g => g.First().Value);
        var hl = LifetimeKeys.ToDictionary(kv => kv.Value, kv => byId.GetValueOrDefault(kv.Key));
        hl["deathsInBattlegrounds"] = rows.Where(r => r.Category == "Deaths/Battlegrounds").Sum(r => r.Value);
        return new LifetimeStats(hl, rows);
    }

    private static Dictionary<string, double> BuildStats(JsonElement stats)
    {
        var d = new Dictionary<string, double>();
        foreach (var pr in stats.EnumerateObject())
            if (!StatSkip.Contains(pr.Name) && Num(pr.Value) is { } n) d[pr.Name] = Math.Round(n, 2);
        return d;
    }

    private static double? Num(JsonElement v)
    {
        if (v.ValueKind == JsonValueKind.Object)
        {
            foreach (var k in new[] { "effective", "value", "base" })
                if (v.TryGetProperty(k, out var x) && x.ValueKind == JsonValueKind.Number) return x.GetDouble();
            return null;
        }
        return v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
    }

    private async Task<List<GearItem>> BuildGearAsync(JsonElement eq, CancellationToken ct)
    {
        var list = new List<GearItem>();
        if (eq.Path("equipped_items") is not { ValueKind: JsonValueKind.Array } arr) return list;
        foreach (var it in arr.EnumerateArray())
        {
            var id = it.Path("item", "id")!.Value.GetInt32();
            var nm = it.Path("name")?.GetString() ?? await ItemNameAsync(id, ct);
            list.Add(new GearItem(
                Title((it.Path("slot", "type")?.GetString() ?? "").Replace('_', ' ')), id, nm,
                Title(it.Path("quality", "type")?.GetString() ?? ""), it.Path("level", "value")?.GetInt32()));
        }
        FlushItems();
        return list;
    }

    private async Task<List<Profession>> BuildProfessionsAsync(JsonElement pr, CancellationToken ct)
    {
        var list = new List<Profession>();
        foreach (var grp in new[] { "primaries", "secondaries" })
        {
            if (pr.Path(grp) is not { ValueKind: JsonValueKind.Array } arr) continue;
            foreach (var x in arr.EnumerateArray())
            {
                int skill = 0, max = 0;
                if (x.Path("tiers") is { ValueKind: JsonValueKind.Array } tiers)
                    foreach (var t in tiers.EnumerateArray())
                    {
                        skill += t.Path("skill_points")?.GetInt32() ?? 0;
                        max += t.Path("max_skill_points")?.GetInt32() ?? 0;
                    }
                var pid = x.Path("profession", "id")!.Value.GetInt32();
                var nm = x.Path("profession", "name")?.GetString() ?? await NameAsync($"/data/wow/profession/{pid}", "static", ct);
                list.Add(new Profession(nm, grp == "primaries", skill, max));
            }
        }
        return list;
    }

    private static string Title(string s) =>
        System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(s.ToLowerInvariant());

    // ---------- HTTP ----------

    private async Task<(int Status, JsonElement? Body)> ApiAsync(string path, string kind, string? locale, CancellationToken ct)
    {
        var token = await TokenAsync(ct);
        var url = $"https://{_o.Region}.api.blizzard.com{path}?namespace={kind}-forever-{_o.Region}&locale={locale ?? _o.Locale}";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        try
        {
            using var res = await _http.SendAsync(req, ct);
            if (!res.IsSuccessStatusCode) return ((int)res.StatusCode, null);
            await using var s = await res.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(s, cancellationToken: ct);
            return (200, doc.RootElement.Clone());
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return (-1, null);
        }
    }

    private sealed record TokenCache(string Token, double Exp);

    private async Task<string> TokenAsync(CancellationToken ct)
    {
        if (_o.ClientId.Length == 0 || _o.ClientSecret.Length == 0)
            throw new ConfigException("Chybí client id / secret (BLIZZARD_CLIENT_ID, BLIZZARD_CLIENT_SECRET).");

        var tf = Path.Combine(_cache, $"token-{_o.ClientId[..Math.Min(8, _o.ClientId.Length)]}.json");
        var c = ReadJson<TokenCache>(tf);
        if (c is not null && c.Exp > DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 60) return c.Token;

        using var req = new HttpRequestMessage(HttpMethod.Post, "https://oauth.battle.net/token")
        {
            Content = new FormUrlEncodedContent([new("grant_type", "client_credentials")]),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_o.ClientId}:{_o.ClientSecret}")));
        try
        {
            using var res = await _http.SendAsync(req, ct);
            if (!res.IsSuccessStatusCode)
                throw new UpstreamException($"Získání tokenu selhalo (HTTP {(int)res.StatusCode}); zkontroluj client id/secret.");
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
            var token = doc.RootElement.GetProperty("access_token").GetString()!;
            var exp = doc.RootElement.TryGetProperty("expires_in", out var e) ? e.GetInt32() : 3600;
            WriteJson(tf, new TokenCache(token, DateTimeOffset.UtcNow.ToUnixTimeSeconds() + exp));
            return token;
        }
        catch (HttpRequestException ex) { throw new UpstreamException($"Token: {ex.Message}"); }
    }

    // ---------- jména ze static dat (cs_CZ vrací 200 s prázdným jménem -> fallback en_GB) ----------

    private async Task<string?> NameAsync(string path, string kind, CancellationToken ct) =>
        (await NameExAsync(path, kind, ct)).Name;

    /// <summary>Definitive=false znamená dočasnou chybu, nic se necachuje.</summary>
    private async Task<(string? Name, bool Definitive)> NameExAsync(string path, string kind, CancellationToken ct)
    {
        lock (_lock)
        {
            _names ??= ReadJson<Dictionary<string, string?>>(Path.Combine(_cache, "names.json")) ?? [];
            if (_names.TryGetValue(path, out var hit)) return (hit, true);
        }

        string? name = null;
        var definitive = false;
        foreach (var loc in new[] { _o.Locale, "en_GB" }.Distinct())
        {
            var (st, body) = await ApiAsync(path, kind, loc, ct);
            if (st == 200 && body?.Path("name") is { } n)
            {
                name = n.ValueKind == JsonValueKind.String ? n.GetString()
                    : n.ValueKind == JsonValueKind.Object
                        ? (n.TryGetProperty(loc, out var l) && l.ValueKind == JsonValueKind.String ? l.GetString() : null)
                          ?? n.EnumerateObject().Select(x => x.Value.GetString()).FirstOrDefault(x => !string.IsNullOrEmpty(x))
                        : null;
                if (!string.IsNullOrEmpty(name)) { definitive = true; break; }
            }
            else if (st == 404) definitive = true;
        }
        if (definitive)
            lock (_lock)
            {
                _names![path] = name;
                WriteJson(Path.Combine(_cache, "names.json"), _names);
            }
        return (name, definitive);
    }

    // ---------- itemy: Blizzard static -> záloha Wowhead; výsledek se cachuje v items.json ----------

    private sealed record ItemEntry(string? Name, double T, string Src);

    private async Task<string?> ItemNameAsync(int id, CancellationToken ct)
    {
        var key = id.ToString();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        lock (_lock)
        {
            _items ??= ReadJson<Dictionary<string, ItemEntry>>(Path.Combine(_cache, "items.json")) ?? [];
            // známý item se nikdy nepřeptává; nenalezený až po NegTtl
            if (_items.TryGetValue(key, out var e) && (!string.IsNullOrEmpty(e.Name) || now - e.T < NegTtl.TotalSeconds))
                return e.Name;
        }

        var (name, definitive) = await NameExAsync($"/data/wow/item/{id}", "static", ct);
        var src = "blizzard";
        if (string.IsNullOrEmpty(name) && _o.UseWowhead)
        {
            src = "wowhead";
            var (wn, wd) = await WowheadAsync(id, ct);
            name = wn;
            definitive &= wd;
        }
        else if (string.IsNullOrEmpty(name)) definitive = false; // Wowhead vypnutý: nic neukládej

        if (definitive || !string.IsNullOrEmpty(name))
            lock (_lock) { _items![key] = new ItemEntry(name, now, src); _itemsDirty = true; }
        return name;
    }

    private async Task<(string? Name, bool Definitive)> WowheadAsync(int id, CancellationToken ct)
    {
        await _whGate.WaitAsync(ct);
        try
        {
            var wait = _whLast + WowheadGap - DateTimeOffset.UtcNow;
            if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
            using var req = new HttpRequestMessage(HttpMethod.Get, string.Format(WowheadUrl, id));
            req.Headers.UserAgent.ParseAdd("wowstats/0.1");
            try
            {
                using var res = await _http.SendAsync(req, ct);
                _whLast = DateTimeOffset.UtcNow;
                if (res.StatusCode == HttpStatusCode.NotFound) return (null, true);
                if (!res.IsSuccessStatusCode) return (null, false);
                await using var s = await res.Content.ReadAsStreamAsync(ct);
                using var doc = await JsonDocument.ParseAsync(s, cancellationToken: ct);
                var n = doc.RootElement.Path("name")?.GetString();
                return string.IsNullOrEmpty(n) ? (null, false) : (n, true);
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException && !ct.IsCancellationRequested)
            {
                return (null, false);
            }
        }
        finally { _whGate.Release(); }
    }

    private void FlushItems()
    {
        lock (_lock)
        {
            if (!_itemsDirty) return;
            WriteJson(Path.Combine(_cache, "items.json"), _items);
            _itemsDirty = false;
        }
    }

    // ---------- disk ----------

    private static T? ReadJson<T>(string path) where T : class
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json) : null; }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }

    private static void WriteJson<T>(string path, T value)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(value, Json));
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* cache je jen optimalizace */ }
    }
}

internal static class JsonExt
{
    /// <summary>Bezpečné procházení: vrací null, pokud cesta neexistuje nebo je hodnota null.</summary>
    public static JsonElement? Path(this JsonElement e, params string[] keys)
    {
        var cur = e;
        foreach (var k in keys)
        {
            if (cur.ValueKind != JsonValueKind.Object || !cur.TryGetProperty(k, out cur)) return null;
        }
        return cur.ValueKind == JsonValueKind.Null ? null : cur;
    }
}
