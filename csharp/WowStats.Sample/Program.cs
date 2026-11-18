using WowStats;

// dotnet run -- character jmeno-prijmeni [Stats,Gear,Professions,Media] [--refresh] [--no-wowhead]
// dotnet run -- guild "Nazev Guildy" [--refresh] [--top N]
Console.OutputEncoding = System.Text.Encoding.UTF8;
if (args.Length < 2 || args[0] is not ("character" or "guild"))
{
    Console.Error.WriteLine("Použití: character <jmeno> [sekce] [--refresh] [--no-wowhead] | guild <nazev> [--refresh] [--top N]");
    return 4;
}

try
{
    // hledá .env v aktuální složce a nahoru; jinak proměnné prostředí BLIZZARD_CLIENT_ID / BLIZZARD_CLIENT_SECRET
    var opts = ForeverOptions.FromEnvironment(FindEnv());
    if (args.Contains("--no-wowhead")) opts.UseWowhead = false;
    using var client = new ForeverClient(opts);
    var refresh = args.Contains("--refresh");

    if (args[0] == "guild")
    {
        var top = args.Contains("--top") ? int.Parse(args[Array.IndexOf(args, "--top") + 1]) : 5;
        var gd = await client.GetGuildAsync(args[1], refresh: refresh);
        Console.WriteLine($"{gd.Name} | {gd.Faction} | {gd.Ruleset} | členů {gd.MemberCount} | založena {gd.CreatedAt:yyyy-MM-dd} | stale: {gd.Stale}");
        foreach (var m in gd.Members.Take(top))
            Console.WriteLine($"  rank {m.Rank} | {m.Name} {m.Surname} | lvl {m.Level} {m.Race} {m.CharacterClass}");
        Console.WriteLine("třídy: " + string.Join(", ", gd.Members.GroupBy(m => m.CharacterClass)
            .OrderByDescending(x => x.Count()).Take(4).Select(x => $"{x.Key} {x.Count()}")));
        return 0;
    }

    var sections = CharacterSections.All;
    if (args.Length > 2 && !args[2].StartsWith("--")) sections = Enum.Parse<CharacterSections>(args[2], ignoreCase: true);
    var c = await client.GetCharacterAsync(args[1], sections, refresh: refresh);

    Console.WriteLine($"{c.Name} {c.Surname} | lvl {c.Level} {c.Race} {c.CharacterClass} | {c.Faction} <{c.Guild}> | {c.Ruleset} | ilvl {c.ItemLevel}");
    if (c.Stats is { } s) Console.WriteLine($"stats: {s.Count}, attack power {s.GetValueOrDefault("attack_power")}");
    if (c.Gear is { } g) Console.WriteLine($"gear: {g.Count} itemů, bez názvu {g.Count(i => i.Name is null)}");
    if (c.Professions is { } p) Console.WriteLine("profese: " + string.Join(", ", p.Select(x => $"{x.Name} {x.Skill}/{x.MaxSkill}")));
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
