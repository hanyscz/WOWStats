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
    All = Stats | Gear | Professions | Media,
}

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
    string? AvatarUrl,
    string? RenderUrl,
    DateTimeOffset FetchedAt,
    bool Stale);

public class WowStatsException : Exception
{
    public int Code { get; }
    public WowStatsException(string message, int code = 1) : base(message) => Code = code;
}

public sealed class ConfigException(string message) : WowStatsException(message, 4);
public sealed class CharacterNotFoundException(string message) : WowStatsException(message, 2);
public sealed class UpstreamException(string message) : WowStatsException(message, 3);
