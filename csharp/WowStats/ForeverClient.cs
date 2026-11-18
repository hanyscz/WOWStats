using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
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
    private Dictionary<string, IconEntry>? _icons;
    private bool _iconsDirty;
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

    [GeneratedRegex(@"^/(data|profile)/wow/[\w\-./]*$")]
    private static partial Regex RawPathRe();

    private const int AuctionNamesMax = 500; // nejvíc různých itemů, u kterých se u aukcí řeší názvy (každý neznámý = dotaz)

    // ---------- veřejné API ----------

    /// <param name="name">Slug nebo "Jmeno Prijmeni".</param>
    public async Task<Character> GetCharacterAsync(string name, CharacterSections sections = CharacterSections.Default,
        TimeSpan? ttl = null, bool refresh = false, CancellationToken ct = default)
    {
        var slug = name.Trim().ToLowerInvariant().Replace(' ', '-');
        if (!SlugRe().IsMatch(slug)) throw new CharacterNotFoundException($"Neplatné jméno postavy: '{name}'");
        // ikony a detaily se přidávají k vybavení, recepty k profesím
        if (sections.HasFlag(CharacterSections.Icons) || sections.HasFlag(CharacterSections.ItemDetails)) sections |= CharacterSections.Gear;
        if (sections.HasFlag(CharacterSections.Recipes)) sections |= CharacterSections.Professions;

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

    // ---------- libovolný endpoint, rulesety a aukce ----------

    private sealed record RawDoc(JsonElement Doc);
    private sealed record RulesetList(List<Ruleset> Items);
    private sealed record HouseList(List<AuctionHouse> Items);

    /// <summary>
    /// Libovolný endpoint Blizzard API (cesta začíná /data/wow/ nebo /profile/wow/). Vrací JSON tak, jak přišel
    /// (<paramref name="clean"/> = bez odkazů a s klíči v camelCase). <paramref name="kind"/>: "static" (výchozí),
    /// "dynamic" nebo "profile". 404 → <see cref="NotFoundException"/>, jiná chyba → <see cref="UpstreamException"/>.
    /// Výsledek se cachuje na disk.
    /// </summary>
    public async Task<JsonElement> RawAsync(string path, string kind = "static",
        IReadOnlyDictionary<string, string>? parameters = null, string? locale = null, TimeSpan? ttl = null,
        bool refresh = false, bool clean = false, CancellationToken ct = default)
    {
        if (kind is not ("profile" or "static" or "dynamic"))
            throw new ConfigException($"Neplatný kind '{kind}' (platné: profile, static, dynamic)");
        if (!RawPathRe().IsMatch(path) || path.Contains(".."))
            throw new ConfigException($"Neplatná cesta '{path}': musí začínat /data/wow/ nebo /profile/wow/");
        var qs = string.Join("&", (parameters ?? new Dictionary<string, string>()).OrderBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value)}"));
        var key = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(
            $"{_o.Region}|{kind}|{path}|{qs}|{locale ?? _o.Locale}"))).ToLowerInvariant()[..20];
        var doc = await CachedAsync(Path.Combine(_cache, $"cs-raw-{key}.json"), ttl, refresh, async () =>
        {
            var (st, body) = await ApiAsync(path, kind, locale, ct, qs.Length > 0 ? qs : null);
            if (st == 404) throw new NotFoundException($"Nenalezeno: {path}");
            if (st != 200 || body is not { } b) throw new UpstreamException($"{path}: HTTP {st}");
            return new RawDoc(b);
        }, d => d);
        return clean ? Tidy(doc.Doc) : doc.Doc;
    }

    /// <summary>Rulesety (ve Forever místo realmu): 1 PvP, 2 Normal, 3 Roleplay, 15 Roleplay PvP.</summary>
    public async Task<IReadOnlyList<Ruleset>> GetRulesetsAsync(TimeSpan? ttl = null, bool refresh = false,
        CancellationToken ct = default) =>
        (await CachedAsync(Path.Combine(_cache, $"cs-rulesets-{_o.Region}.json"), ttl, refresh, async () =>
        {
            var (st, b) = await ApiAsync("/data/wow/ruleset/index", "dynamic", "en_GB", ct);
            if (st != 200 || b is not { } doc) throw new UpstreamException($"Rulesety: HTTP {st}");
            var ids = Items(doc.Path("rulesets")).Select(r => I(r.Path("id"))).Where(i => i is not null)
                .Select(i => i!.Value).OrderBy(i => i).ToList();
            var list = new List<Ruleset>();
            foreach (var id in ids) list.Add(new Ruleset(id, await NameAsync($"/data/wow/ruleset/{id}", "dynamic", ct)));
            return new RulesetList(list);
        }, x => x)).Items;

    private async Task<Ruleset> ResolveRulesetAsync(string? ruleset, CancellationToken ct)
    {
        var rs = await GetRulesetsAsync(ct: ct);
        if (rs.Count == 0) throw new UpstreamException("API nevrátilo žádné rulesety");
        if (ruleset is null) // data aukcí jsou u všech rulesetů stejná, „Normal“ je rozumný výchozí
            return rs.FirstOrDefault(r => string.Equals(r.Name, "Normal", StringComparison.OrdinalIgnoreCase)) ?? rs[0];
        var t = ruleset.Trim();
        var hit = int.TryParse(t, out var id)
            ? rs.FirstOrDefault(r => r.Id == id)
            : rs.FirstOrDefault(r => string.Equals(r.Name, t, StringComparison.OrdinalIgnoreCase));
        return hit ?? throw new ConfigException(
            $"Neznámý ruleset '{ruleset}' (platné: {string.Join(", ", rs.Select(r => $"{r.Id}={r.Name}"))})");
    }

    /// <param name="ruleset">Id ("2") nebo název ("Normal"); výchozí Normal.</param>
    public async Task<IReadOnlyList<AuctionHouse>> GetAuctionHousesAsync(string? ruleset = null, TimeSpan? ttl = null,
        bool refresh = false, CancellationToken ct = default)
    {
        var rs = await ResolveRulesetAsync(ruleset, ct);
        return (await CachedAsync(Path.Combine(_cache, $"cs-auchouses-{_o.Region}-{rs.Id}.json"), ttl, refresh, async () =>
        {
            var (st, b) = await ApiAsync($"/data/wow/ruleset/{rs.Id}/auctions/", "dynamic", "en_GB", ct); // lomítko na konci je nutné
            if (st != 200 || b is not { } doc) throw new UpstreamException($"Aukční domy: HTTP {st}");
            return new HouseList(Items(doc.Path("auctions"))
                .Select(a => new AuctionHouse(I(a.Path("id")), a.Path("name")?.GetString())).ToList());
        }, x => x)).Items;
    }

    /// <summary>
    /// Nabídky v aukčním domě (<paramref name="house"/> = id, viz <see cref="GetAuctionHousesAsync"/>). Ceny jsou v copperu.
    /// Celý dům má i stovky tisíc nabídek (desítky MB), proto se cachuje na disk a <paramref name="itemIds"/> (jen tyto
    /// itemy) a <paramref name="limit"/> se aplikují až na cachovaná data. <paramref name="names"/> doplní ItemName
    /// (max. 500 různých itemů, každý neznámý je dotaz, pak je v cache). Pozor: data jsou u všech rulesetů stejná
    /// (zvláštnost API).
    /// </summary>
    public async Task<AuctionListing> GetAuctionsAsync(int house, string? ruleset = null,
        AuctionKind kind = AuctionKind.Auctions, IReadOnlyCollection<int>? itemIds = null, int? limit = null,
        bool names = false, TimeSpan? ttl = null, bool refresh = false, CancellationToken ct = default)
    {
        var rs = await ResolveRulesetAsync(ruleset, ct);
        var file = Path.Combine(_cache, $"cs-auc-{_o.Region}-{rs.Id}-{house}-{kind.ToString().ToLowerInvariant()}.json");
        var full = await CachedAsync(file, ttl, refresh, () => FetchAuctionsAsync(rs, house, kind, ct), l => l with { Stale = true });

        IEnumerable<Auction> rows = full.Auctions;
        if (itemIds is not null)
        {
            var want = itemIds.ToHashSet();
            rows = rows.Where(a => a.ItemId is { } i && want.Contains(i));
        }
        var matched = rows.ToList();
        var returned = limit is { } lim ? matched.Take(Math.Max(0, lim)).ToList() : matched;
        if (names)
        {
            var ids = returned.Select(a => a.ItemId).Where(i => i is not null).Select(i => i!.Value).Distinct().ToList();
            if (ids.Count > AuctionNamesMax)
                throw new ConfigException($"names: true u {ids.Count} různých itemů je moc (max {AuctionNamesMax}); zúž výběr přes itemIds nebo limit");
            var nm = new Dictionary<int, string?>();
            foreach (var id in ids) nm[id] = await ItemNameAsync(id, ct);
            FlushItems();
            returned = returned.Select(a => a with { ItemName = a.ItemId is { } i ? nm.GetValueOrDefault(i) : null }).ToList();
        }
        return full with { Matched = matched.Count, Returned = returned.Count, Auctions = returned };
    }

    private async Task<AuctionListing> FetchAuctionsAsync(Ruleset rs, int house, AuctionKind kind, CancellationToken ct)
    {
        var commodity = kind == AuctionKind.Commodities;
        var (st, b) = await ApiAsync($"/data/wow/ruleset/{rs.Id}/auctions/{house}" + (commodity ? "/commodities" : ""),
            "dynamic", "en_GB", ct);
        if (st == 404) throw new NotFoundException($"Aukční dům {house} nenalezen (ruleset {rs.Name})");
        if (st != 200 || b is not { } doc) throw new UpstreamException($"Aukce: HTTP {st}");
        var rows = Items(doc.Path("auctions")).Select(a => NormAuction(a, commodity)).ToList();
        var name = doc.Path("name")?.GetString()
            ?? (await GetAuctionHousesAsync(rs.Id.ToString(), ct: ct)).FirstOrDefault(h => h.Id == house)?.Name;
        return new AuctionListing(new AuctionHouse(house, name), rs, kind, rows.Count, rows.Count, rows.Count, rows,
            DateTimeOffset.UtcNow, false);
    }

    private static Auction NormAuction(JsonElement a, bool commodity)
    {
        static long? L(JsonElement? e) => e is { ValueKind: JsonValueKind.Number } v && v.TryGetInt64(out var l) ? l : null;
        var item = a.Path("item");
        var mods = commodity ? null : item?.Path("modifiers");
        var bonus = commodity ? null : item?.Path("bonus_lists");
        return new Auction(
            Id: L(a.Path("id")), ItemId: I(item?.Path("id")), Quantity: I(a.Path("quantity")),
            Buyout: commodity ? null : L(a.Path("buyout")), Bid: commodity ? null : L(a.Path("bid")),
            UnitPrice: commodity ? L(a.Path("unit_price")) : null, TimeLeft: a.Path("time_left")?.GetString(),
            Context: commodity ? null : I(item?.Path("context")),
            Modifiers: mods is null ? null : Items(mods).Select(m => new ItemModifier(I(m.Path("type")), I(m.Path("value")))).ToList(),
            BonusLists: bonus is null ? null : Items(bonus).Select(x => I(x)).Where(x => x is not null).Select(x => x!.Value).ToList());
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
        // texty detailů itemů a receptů vrací API jen anglicky, proto en_GB
        JsonElement? gear = secs.HasFlag(CharacterSections.Gear)
            ? secs.HasFlag(CharacterSections.ItemDetails) ? await SubEn("equipment") : await Sub("equipment") : null;
        JsonElement? profs = secs.HasFlag(CharacterSections.Professions)
            ? secs.HasFlag(CharacterSections.Recipes) ? await SubEn("professions") : await Sub("professions") : null;
        JsonElement? media = secs.HasFlag(CharacterSections.Media) ? await Sub("character-media") : null;
        // názvy statistik vrací API jen anglicky, jinak jsou null
        async Task<JsonElement?> SubEn(string path) =>
            (await ApiAsync($"{baseP}/{path}", "profile", "en_GB", ct)) is (200, { } b) ? b : null;
        JsonElement? lifetime = secs.HasFlag(CharacterSections.Lifetime) ? await SubEn("achievements/statistics") : null;
        // u dalších sekcí vrací API názvy také jen anglicky, proto en_GB
        JsonElement? ach = secs.HasFlag(CharacterSections.Achievements) ? await SubEn("achievements") : null;
        JsonElement? reps = secs.HasFlag(CharacterSections.Reputations) ? await SubEn("reputations") : null;
        JsonElement? questsIn = secs.HasFlag(CharacterSections.Quests) ? await SubEn("quests") : null;
        JsonElement? questsDone = secs.HasFlag(CharacterSections.Quests) ? await SubEn("quests/completed") : null;
        JsonElement? pvp = secs.HasFlag(CharacterSections.Pvp) ? await SubEn("pvp-summary") : null;
        JsonElement? pets = secs.HasFlag(CharacterSections.HunterPets) ? await SubEn("hunter-pets") : null; // ne-lovci: 404
        JsonElement? specs = secs.HasFlag(CharacterSections.Specializations) ? await SubEn("specializations") : null;
        // vracejí se "jak jsou" (bez odkazů, camelCase): null = dotaz selhal, {} = API nic nevrátilo
        JsonElement? appearance = secs.HasFlag(CharacterSections.Appearance) ? await SubEn("appearance") : null;
        JsonElement? titles = secs.HasFlag(CharacterSections.Titles) ? await SubEn("titles") : null;
        JsonElement? mythic = secs.HasFlag(CharacterSections.MythicKeystone) ? await SubEn("mythic-keystone-profile") : null;
        JsonElement? encDungeons = secs.HasFlag(CharacterSections.Encounters) ? await SubEn("encounters/dungeons") : null;
        JsonElement? encRaids = secs.HasFlag(CharacterSections.Encounters) ? await SubEn("encounters/raids") : null;
        var collections = secs.HasFlag(CharacterSections.Collections)
            ? new[] { await SubEn("collections/mounts"), await SubEn("collections/pets"), await SubEn("collections/toys"),
                      await SubEn("collections/heirlooms"), await SubEn("collections/transmogs") }
            : null;

        var rulesetId = prof.Path("ruleset", "id")?.GetInt32();
        long? login = prof.Path("last_login_timestamp")?.GetInt64();

        var gearList = gear is null ? null
            : await BuildGearAsync(gear.Value, secs.HasFlag(CharacterSections.Icons), secs.HasFlag(CharacterSections.ItemDetails), ct);
        var profList = profs is null ? null : await BuildProfessionsAsync(profs.Value, secs.HasFlag(CharacterSections.Recipes), ct);
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
            Experience: I(prof.Path("experience")),
            Gender: prof.Path("gender", "type")?.GetString() is { Length: > 0 } gd ? Title(gd) : null,
            AchievementPoints: I(prof.Path("achievement_points")),
            AverageItemLevel: I(prof.Path("average_item_level")),
            Stats: stats is null ? null : BuildStats(stats.Value),
            Gear: gearList,
            Professions: profList,
            Lifetime: lifetime is null ? null : BuildLifetime(lifetime.Value),
            Achievements: ach is null ? null : BuildAchievements(ach.Value),
            Reputations: reps is null ? null : BuildReputations(reps.Value),
            Quests: secs.HasFlag(CharacterSections.Quests) ? BuildQuests(questsIn, questsDone) : null,
            Pvp: pvp is null ? null : BuildPvp(pvp.Value),
            HunterPets: pets is null ? null : BuildHunterPets(pets.Value),
            Specializations: specs is null ? null : BuildSpecializations(specs.Value),
            Collections: collections is null ? null : BuildCollections(collections),
            Appearance: appearance is null ? null : Tidy(appearance.Value, "character"),
            Encounters: encDungeons is null && encRaids is null ? null
                : new EncountersInfo(encDungeons is null ? null : Tidy(encDungeons.Value, "character"),
                                     encRaids is null ? null : Tidy(encRaids.Value, "character")),
            Titles: titles is null ? null : Tidy(titles.Value, "character"),
            MythicKeystone: mythic is null ? null : Tidy(mythic.Value, "character"),
            AvatarUrl: assets?.GetValueOrDefault("avatar"),
            RenderUrl: assets?.GetValueOrDefault("main-sd-raw"),
            FetchedAt: DateTimeOffset.UtcNow,
            Stale: false);
    }

    // ---------- nové sekce (stejné tvary jako v Pythonu) ----------

    private static int? I(JsonElement? e) => e is { ValueKind: JsonValueKind.Number } v && v.TryGetInt32(out var i) ? i : null;
    private static bool B(JsonElement? e) => e is { ValueKind: JsonValueKind.True };
    private static DateTimeOffset? Ms(JsonElement? e) =>
        e is { ValueKind: JsonValueKind.Number } v && v.TryGetInt64(out var ms) && ms != 0 ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : null;
    private static IEnumerable<JsonElement> Items(JsonElement? e) =>
        e is { ValueKind: JsonValueKind.Array } a ? a.EnumerateArray() : [];

    // ---------- úprava odpovědí API: bez odkazů, klíče v camelCase ----------

    private static readonly Regex SnakeRe = new("_([a-z0-9])", RegexOptions.Compiled);

    private static string CamelKey(string k) => SnakeRe.Replace(k, m => m.Groups[1].Value.ToUpperInvariant());

    /// <summary>
    /// Odpověď API bez odkazů (<c>_links</c>, <c>key: {href}</c>, <c>{href}</c>) a s klíči v camelCase. Hodnoty zůstávají
    /// beze změny, nic se neztrácí. <paramref name="dropTop"/> = další klíče na nejvyšší úrovni, které se vynechají.
    /// </summary>
    internal static JsonElement Tidy(JsonElement e, params string[] dropTop)
    {
        System.Text.Json.Nodes.JsonNode? Rec(JsonElement x, bool top)
        {
            switch (x.ValueKind)
            {
                case JsonValueKind.Object:
                    var o = new System.Text.Json.Nodes.JsonObject();
                    foreach (var p in x.EnumerateObject())
                    {
                        if (p.Name == "_links" || (top && dropTop.Contains(p.Name))) continue;
                        if (p.Name == "key" && p.Value.ValueKind == JsonValueKind.Object && p.Value.TryGetProperty("href", out _)) continue;
                        if (p.Value.ValueKind == JsonValueKind.Object && p.Value.EnumerateObject().Count() == 1
                            && p.Value.TryGetProperty("href", out _)) continue;
                        o[CamelKey(p.Name)] = Rec(p.Value, false);
                    }
                    return o;
                case JsonValueKind.Array:
                    var a = new System.Text.Json.Nodes.JsonArray();
                    foreach (var v in x.EnumerateArray()) a.Add(Rec(v, false));
                    return a;
                default:
                    return System.Text.Json.Nodes.JsonNode.Parse(x.GetRawText());
            }
        }
        return JsonSerializer.SerializeToElement(Rec(e, true));
    }

    private static CollectionsInfo? BuildCollections(JsonElement?[] docs)
    {
        if (docs.All(d => d is null)) return null;
        JsonElement? mountsD = docs[0], petsD = docs[1], toysD = docs[2], heirD = docs[3], tmD = docs[4];
        (int? Id, string? Name) Ref(JsonElement x, string k) => (I(x.Path(k, "id")), x.Path(k, "name")?.GetString());
        return new CollectionsInfo(
            Mounts: mountsD is null ? null : Items(mountsD.Value.Path("mounts")).Select(m =>
                { var (id, nm) = Ref(m, "mount"); return new MountRef(id, nm, B(m.Path("is_useable"))); }).ToList(),
            Pets: petsD is null ? null : Items(petsD.Value.Path("pets")).Select(p => Tidy(p)).ToList(),
            UnlockedBattlePetSlots: petsD is null ? null : I(petsD.Value.Path("unlocked_battle_pet_slots")),
            Toys: toysD is null ? null : Items(toysD.Value.Path("toys")).Select(t =>
                { var (id, nm) = Ref(t, "toy"); return new ToyRef(id, nm); }).ToList(),
            Heirlooms: heirD is null ? null
                : heirD.Value.Path("heirlooms") is { } h ? Tidy(h) : JsonSerializer.SerializeToElement(Array.Empty<int>()),
            Transmogs: tmD is null ? null : Items(tmD.Value.Path("slots")).Select(s => new TransmogSlot(
                s.Path("slot", "type")?.GetString(),
                Items(s.Path("appearances")).Select(a => I(a.Path("id"))).Where(i => i is not null).Select(i => i!.Value).ToList())).ToList());
    }

    private static AchievementsInfo? BuildAchievements(JsonElement doc)
    {
        if (doc.Path("achievements") is null && doc.Path("total_quantity") is null) return null;
        var completed = new List<AchievementEntry>();
        var inProgress = new List<InProgressAchievement>();
        foreach (var a in Items(doc.Path("achievements")))
        {
            var id = I(a.Path("achievement", "id")) ?? I(a.Path("id"));
            var nm = a.Path("achievement", "name")?.GetString();
            if (Ms(a.Path("completed_timestamp")) is { } at) completed.Add(new AchievementEntry(id, nm, at));
            else inProgress.Add(new InProgressAchievement(id, nm, a.Path("criteria") is { } cr ? Tidy(cr) : null));
        }
        completed = completed.OrderBy(x => x.CompletedAt).ToList();
        var recent = Items(doc.Path("recent_events")).Select(e => new RecentAchievement(
            I(e.Path("achievement", "id")), e.Path("achievement", "name")?.GetString(), Ms(e.Path("timestamp")))).ToList();
        var cats = Items(doc.Path("category_progress")).Select(c => new AchievementCategory(
            I(c.Path("category", "id")), c.Path("category", "name")?.GetString(), I(c.Path("quantity")), I(c.Path("points")))).ToList();
        return new AchievementsInfo(I(doc.Path("total_quantity")) ?? completed.Count, I(doc.Path("total_points")) ?? 0,
            completed, recent, cats, inProgress);
    }

    private static List<Reputation>? BuildReputations(JsonElement doc)
    {
        if (doc.Path("reputations") is not { ValueKind: JsonValueKind.Array } arr) return null;
        return arr.EnumerateArray().Select(r => new Reputation(
                I(r.Path("faction", "id")), r.Path("faction", "name")?.GetString(), r.Path("standing", "name")?.GetString(),
                I(r.Path("standing", "tier")), I(r.Path("standing", "value")), I(r.Path("standing", "max")), I(r.Path("standing", "raw"))))
            .OrderByDescending(x => x.Tier ?? -1).ThenBy(x => x.Name ?? "", StringComparer.Ordinal).ToList(); // tier sestupně, pak název
    }

    private static QuestsInfo? BuildQuests(JsonElement? inProgress, JsonElement? done)
    {
        var ip = inProgress?.Path("in_progress");
        var dn = done?.Path("quests");
        if (ip is not { ValueKind: JsonValueKind.Array } && dn is not { ValueKind: JsonValueKind.Array }) return null;
        List<QuestRef> Refs(JsonElement? a) => Items(a).Select(q => new QuestRef(I(q.Path("id")), q.Path("name")?.GetString())).ToList();
        var completed = dn is { ValueKind: JsonValueKind.Array } ? Refs(dn) : null; // null = seznam se nepodařilo získat
        return new QuestsInfo(completed?.Count, completed, Refs(ip));
    }

    private static PvpInfo? BuildPvp(JsonElement doc)
    {
        if (doc.Path("honorable_kills") is null && doc.Path("pvp_map_statistics") is null) return null;
        var bgs = Items(doc.Path("pvp_map_statistics")).Select(m => new BattlegroundStat(
            I(m.Path("world_map", "id")), m.Path("world_map", "name")?.GetString(), I(m.Path("match_statistics", "played")) ?? 0,
            I(m.Path("match_statistics", "won")) ?? 0, I(m.Path("match_statistics", "lost")) ?? 0)).ToList();
        return new PvpInfo(I(doc.Path("honorable_kills")) ?? 0, I(doc.Path("pvp_rank")) ?? 0, bgs);
    }

    private static List<HunterPet>? BuildHunterPets(JsonElement doc)
    {
        if (doc.Path("hunter_pets") is not { ValueKind: JsonValueKind.Array } arr) return null;
        return arr.EnumerateArray().Select(p => new HunterPet(
            p.Path("name")?.GetString(), I(p.Path("level")), I(p.Path("slot")), B(p.Path("is_active")), B(p.Path("is_summoned")),
            I(p.Path("creature", "id")), p.Path("creature", "name")?.GetString())).ToList();
    }

    private static List<SpecializationGroup>? BuildSpecializations(JsonElement doc)
    {
        if (doc.Path("specialization_groups") is not { ValueKind: JsonValueKind.Array } groups) return null;
        var result = new List<SpecializationGroup>();
        foreach (var g in groups.EnumerateArray())
        {
            var specs = new List<Specialization>();
            foreach (var s in Items(g.Path("specializations")))
            {
                var talents = Items(s.Path("talents")).Select(t => new Talent(
                    I(t.Path("talent", "id")), t.Path("talent", "name")?.GetString(), I(t.Path("talent_rank")),
                    I(t.Path("spell_tooltip", "spell", "id")), t.Path("spell_tooltip", "description")?.GetString())).ToList();
                specs.Add(new Specialization(I(s.Path("specialization", "id")),
                    s.Path("specialization", "name")?.GetString() ?? s.Path("specialization_name")?.GetString(),
                    I(s.Path("spent_points")) ?? talents.Sum(t => t.Rank ?? 0), talents));
            }
            result.Add(new SpecializationGroup(B(g.Path("is_active")), specs));
        }
        return result;
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

    private async Task<List<GearItem>> BuildGearAsync(JsonElement eq, bool icons, bool details, CancellationToken ct)
    {
        var list = new List<GearItem>();
        if (eq.Path("equipped_items") is not { ValueKind: JsonValueKind.Array } arr) return list;
        foreach (var it in arr.EnumerateArray())
        {
            var id = it.Path("item", "id")!.Value.GetInt32();
            var nm = it.Path("name")?.GetString() ?? await ItemNameAsync(id, ct);
            list.Add(new GearItem(
                Title((it.Path("slot", "type")?.GetString() ?? "").Replace('_', ' ')), id, nm,
                Title(it.Path("quality", "type")?.GetString() ?? ""), it.Path("level", "value")?.GetInt32(),
                icons ? await ItemIconAsync(id, ct) : null,
                details ? Tidy(it) : null)); // celý záznam itemu z API, nic se nezahazuje
        }
        FlushItems();
        FlushIcons();
        return list;
    }

    // ---------- ikony itemů: Blizzard /data/wow/media/item/{id}, trvale cachované v icons.json (sdílí se s Pythonem) ----------

    private sealed record IconEntry(string? Url, double T);

    private async Task<string?> ItemIconAsync(int id, CancellationToken ct)
    {
        var key = id.ToString();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        lock (_lock)
        {
            _icons ??= ReadJson<Dictionary<string, IconEntry>>(Path.Combine(_cache, "icons.json")) ?? [];
            // známá ikona se nikdy nepřeptává; nenalezený item až po NegTtl
            if (_icons.TryGetValue(key, out var e) && (!string.IsNullOrEmpty(e.Url) || now - e.T < NegTtl.TotalSeconds))
                return e.Url;
        }

        var (st, body) = await ApiAsync($"/data/wow/media/item/{id}", "static", "en_GB", ct);
        string? url = null;
        if (st == 200)
            foreach (var a in Items(body?.Path("assets")))
                if (a.Path("key")?.GetString() == "icon") { url = a.Path("value")?.GetString(); break; }
        if (!string.IsNullOrEmpty(url) || st == 404) // dočasné chyby se necachují
            lock (_lock) { _icons![key] = new IconEntry(url, now); _iconsDirty = true; }
        return url;
    }

    private void FlushIcons()
    {
        lock (_lock)
        {
            if (!_iconsDirty) return;
            WriteJson(Path.Combine(_cache, "icons.json"), _icons);
            _iconsDirty = false;
        }
    }

    private async Task<List<Profession>> BuildProfessionsAsync(JsonElement pr, bool recipes, CancellationToken ct)
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
                List<ProfessionTier>? tierList = null;
                List<Recipe>? recipeList = null;
                if (recipes)
                {
                    tierList = [];
                    recipeList = [];
                    foreach (var t in Items(x.Path("tiers")))
                    {
                        var tn = t.Path("tier", "name")?.GetString();
                        tierList.Add(new ProfessionTier(I(t.Path("tier", "id")), tn, I(t.Path("skill_points")) ?? 0, I(t.Path("max_skill_points")) ?? 0));
                        foreach (var r in Items(t.Path("known_recipes")))
                            recipeList.Add(new Recipe(I(r.Path("id")), r.Path("name")?.GetString(), tn));
                    }
                }
                list.Add(new Profession(nm, grp == "primaries", skill, max, tierList, recipeList));
            }
        }
        return list;
    }

    private static string Title(string s) =>
        System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(s.ToLowerInvariant());

    // ---------- HTTP ----------

    private async Task<(int Status, JsonElement? Body)> ApiAsync(string path, string kind, string? locale,
        CancellationToken ct, string? query = null)
    {
        var token = await TokenAsync(ct);
        var url = $"https://{_o.Region}.api.blizzard.com{path}?namespace={kind}-forever-{_o.Region}&locale={locale ?? _o.Locale}"
            + (query is null ? "" : "&" + query);
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
