using WowStats;

// dotnet run -- jmeno-prijmeni [Stats,Gear,Professions,Media] [--refresh]
if (args.Length == 0) { Console.Error.WriteLine("Použití: <jmeno-postavy> [sekce] [--refresh]"); return 4; }

var sections = CharacterSections.All;
if (args.Length > 1 && !args[1].StartsWith("--")) sections = Enum.Parse<CharacterSections>(args[1], ignoreCase: true);

try
{
    // hledá .env v aktuální složce; jinak proměnné prostředí BLIZZARD_CLIENT_ID / BLIZZARD_CLIENT_SECRET
    var opts = ForeverOptions.FromEnvironment(FindEnv());
    if (args.Contains("--no-wowhead")) opts.UseWowhead = false;
    using var client = new ForeverClient(opts);
    var c = await client.GetCharacterAsync(args[0], sections, refresh: args.Contains("--refresh"));

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
