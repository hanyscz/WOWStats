namespace WowStats;

/// <summary>Které části postavy se mají stáhnout. Méně sekcí = méně HTTP dotazů.</summary>
[Flags]
public enum CharacterSections
{
    None = 0,
    Stats = 1,
    Gear = 2,
    Professions = 4,
    Media = 8,
    /// <summary>Statistiky za celou dobu (smrti, zabití, questy…) z achievements/statistics.</summary>
    Lifetime = 16,
    All = Stats | Gear | Professions | Media | Lifetime,
}

/// <summary>Jedna statistika za celou dobu. Názvy a kategorie jsou jen anglicky.</summary>
public sealed record LifetimeStat(int Id, string Category, string? Name, double Value, string? Description);

/// <summary>
/// Highlights: vybrané statistiky pod stabilními klíči (deaths, deathsFalling, totalKills, questsCompleted…);
/// chybějící statistika = 0. All: všechny statistiky, které API vrátilo.
/// </summary>
public sealed record LifetimeStats(IReadOnlyDictionary<string, double> Highlights, IReadOnlyList<LifetimeStat> All);

public sealed record GearItem(string Slot, int ItemId, string? Name, string Quality, int? ItemLevel);

public sealed record Profession(string? Name, bool Primary, int Skill, int MaxSkill);

/// <summary>Sekce, které nebyly vyžádané, jsou null.</summary>
public sealed record Character(
    string Name,
    string? Surname,
    string Slug,
    int Level,
    string? Race,
    string? CharacterClass,
    string Faction,
    string? Guild,
    string? Ruleset,
    int? ItemLevel,
    DateTimeOffset? LastLogin,
    IReadOnlyDictionary<string, double>? Stats,
    IReadOnlyList<GearItem>? Gear,
    IReadOnlyList<Profession>? Professions,
    LifetimeStats? Lifetime,
    string? AvatarUrl,
    string? RenderUrl,
    DateTimeOffset FetchedAt,
    bool Stale);

public sealed record GuildMember(
    string? Name,
    string? Surname,
    string? Slug,
    int? Level,
    string? CharacterClass,
    string? Race,
    string? Faction,
    int? Rank);

/// <summary>Členové jsou seřazení: rank, level sestupně, jméno. Rank je číslo (0 = vedoucí), jména ranků API nevrací.</summary>
public sealed record Guild(
    string Name,
    string Slug,
    int Id,
    string? Faction,
    int MemberCount,
    int AchievementPoints,
    DateTimeOffset? CreatedAt,
    string? Ruleset,
    IReadOnlyList<GuildMember> Members,
    DateTimeOffset FetchedAt,
    bool Stale);

public class WowStatsException : Exception
{
    public int Code { get; }
    public WowStatsException(string message, int code = 1) : base(message) => Code = code;
}

public sealed class ConfigException(string message) : WowStatsException(message, 4);
public class NotFoundException(string message) : WowStatsException(message, 2);
public sealed class CharacterNotFoundException(string message) : NotFoundException(message);
public sealed class GuildNotFoundException(string message) : NotFoundException(message);
public sealed class UpstreamException(string message) : WowStatsException(message, 3);
