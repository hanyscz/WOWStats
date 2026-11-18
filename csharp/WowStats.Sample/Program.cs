using WowStats;

// dotnet run -- character jmeno-prijmeni [Default|All|Stats,Gear,...] [--refresh] [--no-wowhead]
// dotnet run -- guild "Nazev Guildy" [--refresh] [--top N]
// dotnet run -- rulesets
// dotnet run -- auctions <dum> [--kind commodities] [--item ID] [--limit N] [--names] [--ruleset R]
// dotnet run -- raw /data/wow/achievement/855 [--kind static|dynamic|profile] [--clean]
Console.OutputEncoding = System.Text.Encoding.UTF8;
string[] commands = ["character", "guild", "rulesets", "auctions", "raw"];
if (args.Length < 1 || !commands.Contains(args[0]) || (args[0] != "rulesets" && args.Length < 2))
{
    Console.Error.WriteLine("Použití: character <jmeno> [sekce] | guild <nazev> | rulesets | auctions <dum> | raw <cesta>  (volby: --refresh --no-wowhead --top N --kind --item --limit --names --ruleset --clean)");
    return 4;
}

string? Opt(string name) => Array.IndexOf(args, name) is var i and >= 0 && i + 1 < args.Length ? args[i + 1] : null;

try
{
    // hledá .env v aktuální složce a nahoru; jinak proměnné prostředí BLIZZARD_CLIENT_ID / BLIZZARD_CLIENT_SECRET
    var opts = ForeverOptions.FromEnvironment(FindEnv());
    if (args.Contains("--no-wowhead")) opts.UseWowhead = false;
    using var client = new ForeverClient(opts);
    var refresh = args.Contains("--refresh");

    if (args[0] == "rulesets")
    {
        Console.WriteLine(string.Join(", ", (await client.GetRulesetsAsync(refresh: refresh)).Select(r => $"{r.Id}={r.Name}")));
        return 0;
    }

    if (args[0] == "auctions")
    {
        var kind = Opt("--kind")?.ToLowerInvariant() == "commodities" ? AuctionKind.Commodities : AuctionKind.Auctions;
        int[]? items = Opt("--item") is { } it ? [int.Parse(it)] : null;
        var listing = await client.GetAuctionsAsync(int.Parse(args[1]), Opt("--ruleset"), kind, items,
            Opt("--limit") is { } lim ? int.Parse(lim) : null, args.Contains("--names"), refresh: refresh);
        Console.WriteLine($"{listing.House.Name} ({listing.House.Id}) | ruleset {listing.Ruleset.Name} | {listing.Kind} | celkem {listing.Total}, po filtru {listing.Matched}, vráceno {listing.Returned} | stale: {listing.Stale}");
        foreach (var a in listing.Auctions.Take(5))
            Console.WriteLine($"  #{a.Id} item {a.ItemId}{(a.ItemName is null ? "" : $" ({a.ItemName})")} x{a.Quantity} | " +
                              (kind == AuctionKind.Commodities ? $"unitPrice {a.UnitPrice}" : $"buyout {a.Buyout}") + $" copper | {a.TimeLeft}");
        return 0;
    }

    if (args[0] == "raw")
    {
        var json = await client.RawAsync(args[1], Opt("--kind") ?? "static", refresh: refresh, clean: args.Contains("--clean"));
        var text = json.GetRawText();
        Console.WriteLine(text.Length > 1200 ? text[..1200] + $" … [{text.Length} znaků]" : text);
        return 0;
    }

    if (args[0] == "guild")
    {
        var top = Opt("--top") is { } t ? int.Parse(t) : 5;
        var gd = await client.GetGuildAsync(args[1], refresh: refresh);
        Console.WriteLine($"{gd.Name} | {gd.Faction} | {gd.Ruleset} | členů {gd.MemberCount} | založena {gd.CreatedAt:yyyy-MM-dd} | stale: {gd.Stale}");
        foreach (var m in gd.Members.Take(top))
            Console.WriteLine($"  rank {m.Rank} | {m.Name} {m.Surname} | lvl {m.Level} {m.Race} {m.CharacterClass}");
        Console.WriteLine("třídy: " + string.Join(", ", gd.Members.GroupBy(m => m.CharacterClass)
            .OrderByDescending(x => x.Count()).Take(4).Select(x => $"{x.Key} {x.Count()}")));
        return 0;
    }

    var sections = CharacterSections.Default;
    if (args.Length > 2 && !args[2].StartsWith("--")) sections = Enum.Parse<CharacterSections>(args[2], ignoreCase: true);
    var c = await client.GetCharacterAsync(args[1], sections, refresh: refresh);

    Console.WriteLine($"{c.Name} {c.Surname} | lvl {c.Level} {c.Race} {c.CharacterClass} | {c.Gender} {c.Faction} <{c.Guild}> | {c.Ruleset} | ilvl {c.ItemLevel} (průměr {c.AverageItemLevel}) | zkušenosti {c.Experience}");
    if (c.Stats is { } s) Console.WriteLine($"stats: {s.Count}, attack power {s.GetValueOrDefault("attack_power")}");
    if (c.Gear is { } g) Console.WriteLine($"gear: {g.Count} itemů, bez názvu {g.Count(i => i.Name is null)}");
    if (c.Gear is { } gd2 && gd2.Any(i => i.Details is not null))
        Console.WriteLine($"detaily itemů: {gd2.Count(i => i.Details is not null)}, např. {gd2[0].Name}: {gd2[0].Details!.Value.GetProperty("binding").GetProperty("name").GetString()}");
    if (c.Professions is { } p) Console.WriteLine("profese: " + string.Join(", ", p.Select(x => $"{x.Name} {x.Skill}/{x.MaxSkill}" + (x.Recipes is null ? "" : $" ({x.Recipes.Count} receptů)"))));
    if (c.Lifetime is { } lt)
        Console.WriteLine($"za celou dobu: smrtí {lt.Highlights["deaths"]} (pády {lt.Highlights["deathsFalling"]}, od hráčů {lt.Highlights["deathsFromPlayers"]}), zabití {lt.Highlights["totalKills"]}, questů {lt.Highlights["questsCompleted"]} [{lt.All.Count} statistik]");
    if (c.Achievements is { } ac)
        Console.WriteLine($"úspěchy: {ac.Total} splněných, {ac.InProgress.Count} rozpracovaných" + (ac.Completed.Count > 0 ? $", poslední „{ac.Completed[^1].Name}“ {ac.Completed[^1].CompletedAt:yyyy-MM-dd}" : ""));
    if (c.Reputations is { } rp)
        Console.WriteLine($"reputace: {rp.Count} frakcí" + (rp.Count > 0 ? $", nejvyšší {rp[0].Name} ({rp[0].Standing})" : ""));
    if (c.Quests is { } qs)
        Console.WriteLine($"questy: splněno {qs.CompletedCount?.ToString() ?? "?"}, rozpracováno {qs.InProgress.Count}");
    if (c.Pvp is { } pv)
        Console.WriteLine($"PvP: {pv.HonorableKills} čestných zabití, hodnost {pv.Rank}, battlegroundů {pv.Battlegrounds.Count}");
    if (c.HunterPets is { } hp)
        Console.WriteLine("peti: " + (hp.Count == 0 ? "žádní" : string.Join(", ", hp.Select(x => $"{x.Name} ({x.Creature}, lvl {x.Level})"))));
    if (c.Specializations is { } sp)
        Console.WriteLine("talenty: " + string.Join("; ", sp.SelectMany(g => g.Specializations).Select(x => $"{x.Name} {x.Points} bodů ({x.Talents.Count} talentů)")));
    if (c.Collections is { } co)
        Console.WriteLine($"sbírky: mounty {co.Mounts?.Count}, mazlíčci {co.Pets?.Count}, hračky {co.Toys?.Count}, transmog slotů {co.Transmogs?.Count}");
    if (c.Appearance is { } ap) Console.WriteLine($"vzhled: {ap.GetProperty("customizations").GetArrayLength()} customizací, {ap.GetProperty("items").GetArrayLength()} itemů");
    if (c.Encounters is not null || c.Titles is not null || c.MythicKeystone is not null)
        Console.WriteLine($"encounters: {c.Encounters?.Raids?.GetRawText()}, titles: {c.Titles?.GetRawText()}, mythic: {c.MythicKeystone?.GetRawText()}");
    if (c.Gear is { } gi && gi.Any(i => i.IconUrl is not null))
        Console.WriteLine($"ikony: {gi.Count(i => i.IconUrl is not null)} z {gi.Count}, např. {gi.First(i => i.IconUrl is not null).IconUrl}");
    Console.WriteLine($"avatar: {c.AvatarUrl ?? "-"} | stale: {c.Stale}");
    return 0;
}
catch (WowStatsException e)
{
    Console.Error.WriteLine($"{{\"error\": \"{e.Message}\", \"code\": {e.Code}}}");
    return e.Code;
}

static string? FindEnv()
{
    for (var d = new DirectoryInfo(Directory.GetCurrentDirectory()); d is not null; d = d.Parent)
        if (File.Exists(Path.Combine(d.FullName, ".env"))) return Path.Combine(d.FullName, ".env");
    return null;
}
