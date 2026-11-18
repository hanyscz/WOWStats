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
    /// <summary>Splněné úspěchy s datem splnění.</summary>
    Achievements = 32,
    Reputations = 64,
    /// <summary>Splněné a rozpracované questy (dva dotazy).</summary>
    Quests = 128,
    Pvp = 256,
    /// <summary>Peti lovce (u ostatních povolání je výsledek null).</summary>
    HunterPets = 512,
    Specializations = 1024,
    /// <summary>Doplní GearItem.IconUrl (a proto automaticky zahrne Gear). Ikony se cachují na disku.</summary>
    Icons = 2048,

    /// <summary>Výchozí sekce: to, co vrací GetCharacterAsync bez dalšího nastavení.</summary>
    Default = Stats | Gear | Professions | Media | Lifetime,
    /// <summary>Všechny dostupné sekce. Každá je aspoň jeden HTTP dotaz na postavu navíc.</summary>
    All = Default | Achievements | Reputations | Quests | Pvp | HunterPets | Specializations | Icons,
}

/// <summary>Splněný úspěch (CompletedAt je čas splnění, UTC). Názvy jsou jen anglicky.</summary>
public sealed record AchievementEntry(int? Id, string? Name, DateTimeOffset CompletedAt);

public sealed record RecentAchievement(int? Id, string? Name, DateTimeOffset? At);

public sealed record AchievementCategory(int? Id, string? Name, int? Quantity, int? Points);

/// <summary>Total = počet splněných, Completed je seřazeno podle času splnění (rozpracované se vynechávají).</summary>
public sealed record AchievementsInfo(int Total, int Points, IReadOnlyList<AchievementEntry> Completed,
    IReadOnlyList<RecentAchievement> Recent, IReadOnlyList<AchievementCategory> Categories);

/// <summary>Tier: 3 Neutral, 4 Friendly, 5 Honored, 6 Revered, 7 Exalted. Value/Max = postup v rámci aktuální úrovně, Raw = celkem.</summary>
public sealed record Reputation(int? Id, string? Name, string? Standing, int? Tier, int? Value, int? Max, int? Raw);

public sealed record QuestRef(int? Id, string? Name);

/// <summary>Completed i CompletedCount jsou null, když se seznam splněných questů nepodařilo získat.</summary>
public sealed record QuestsInfo(int? CompletedCount, IReadOnlyList<QuestRef>? Completed, IReadOnlyList<QuestRef> InProgress);

public sealed record BattlegroundStat(int? Id, string? Name, int Played, int Won, int Lost);

public sealed record PvpInfo(int HonorableKills, int Rank, IReadOnlyList<BattlegroundStat> Battlegrounds);

public sealed record HunterPet(string? Name, int? Level, int? Slot, bool Active, bool Summoned, int? CreatureId, string? Creature);

public sealed record Talent(int? Id, string? Name, int? Rank, int? SpellId, string? Description);

/// <summary>Strom talentů, ve kterém má postava utracené body (Points).</summary>
public sealed record Specialization(int? Id, string? Name, int Points, IReadOnlyList<Talent> Talents);

public sealed record SpecializationGroup(bool Active, IReadOnlyList<Specialization> Specializations);

/// <summary>Jedna statistika za celou dobu. Názvy a kategorie jsou jen anglicky.</summary>
public sealed record LifetimeStat(int Id, string Category, string? Name, double Value, string? Description);

/// <summary>
/// Highlights: vybrané statistiky pod stabilními klíči (deaths, deathsFalling, totalKills, questsCompleted…);
/// chybějící statistika = 0. All: všechny statistiky, které API vrátilo.
/// </summary>
public sealed record LifetimeStats(IReadOnlyDictionary<string, double> Highlights, IReadOnlyList<LifetimeStat> All);

/// <summary>IconUrl je vyplněná jen při CharacterSections.Icons.</summary>
public sealed record GearItem(string Slot, int ItemId, string? Name, string Quality, int? ItemLevel, string? IconUrl = null);

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
    AchievementsInfo? Achievements,
    IReadOnlyList<Reputation>? Reputations,
    QuestsInfo? Quests,
    PvpInfo? Pvp,
    IReadOnlyList<HunterPet>? HunterPets,
    IReadOnlyList<SpecializationGroup>? Specializations,
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
