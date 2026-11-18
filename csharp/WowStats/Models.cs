using System.Text.Json;

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
    /// <summary>Doplní GearItem.Details: celý záznam itemu z API (bez odkazů, camelCase). Automaticky zahrne Gear.</summary>
    ItemDetails = 4096,
    /// <summary>Doplní Profession.Tiers a Profession.Recipes (známé recepty). Automaticky zahrne Professions.</summary>
    Recipes = 8192,
    /// <summary>Mounty, mazlíčci, hračky, dědictví a transmog (pět dotazů).</summary>
    Collections = 16384,
    Appearance = 32768,
    /// <summary>Dungeony a raidy (dva dotazy). Ve Forever zatím bez dat.</summary>
    Encounters = 65536,
    Titles = 131072,
    MythicKeystone = 262144,

    /// <summary>Výchozí sekce: to, co vrací GetCharacterAsync bez dalšího nastavení.</summary>
    Default = Stats | Gear | Professions | Media | Lifetime,
    /// <summary>Všechny dostupné sekce. Každá je aspoň jeden HTTP dotaz na postavu navíc.</summary>
    All = Default | Achievements | Reputations | Quests | Pvp | HunterPets | Specializations | Icons | ItemDetails
        | Recipes | Collections | Appearance | Encounters | Titles | MythicKeystone,
}

/// <summary>Splněný úspěch (CompletedAt je čas splnění, UTC). Názvy jsou jen anglicky.</summary>
public sealed record AchievementEntry(int? Id, string? Name, DateTimeOffset CompletedAt);

public sealed record RecentAchievement(int? Id, string? Name, DateTimeOffset? At);

public sealed record AchievementCategory(int? Id, string? Name, int? Quantity, int? Points);

/// <summary>Total = počet splněných, Completed je seřazeno podle času splnění (rozpracované se vynechávají).</summary>
public sealed record AchievementsInfo(int Total, int Points, IReadOnlyList<AchievementEntry> Completed,
    IReadOnlyList<RecentAchievement> Recent, IReadOnlyList<AchievementCategory> Categories,
    IReadOnlyList<InProgressAchievement> InProgress);

/// <summary>Rozpracovaný úspěch. Criteria je záznam z API (camelCase): id, isCompleted, childCriteria[{id, amount, isCompleted}].</summary>
public sealed record InProgressAchievement(int? Id, string? Name, JsonElement? Criteria);

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

/// <summary>
/// IconUrl je vyplněná jen při CharacterSections.Icons. Details jen při CharacterSections.ItemDetails: celý záznam itemu
/// z API (binding, stats, armor, weapon, durability, requirements, sellPrice, spells…), bez odkazů a s klíči v camelCase.
/// </summary>
public sealed record GearItem(string Slot, int ItemId, string? Name, string Quality, int? ItemLevel,
    string? IconUrl = null, JsonElement? Details = null);

public sealed record ProfessionTier(int? Id, string? Name, int Skill, int MaxSkill);

public sealed record Recipe(int? Id, string? Name, string? Tier);

/// <summary>Tiers a Recipes jsou vyplněné jen při CharacterSections.Recipes.</summary>
public sealed record Profession(string? Name, bool Primary, int Skill, int MaxSkill,
    IReadOnlyList<ProfessionTier>? Tiers = null, IReadOnlyList<Recipe>? Recipes = null);

public sealed record MountRef(int? Id, string? Name, bool Usable);

public sealed record ToyRef(int? Id, string? Name);

public sealed record TransmogSlot(string? Slot, IReadOnlyList<int> AppearanceIds);

/// <summary>Část sbírky je null, když se její dotaz nepodařil. Pets jsou celé záznamy z API (druh, level, kvalita, staty).</summary>
public sealed record CollectionsInfo(IReadOnlyList<MountRef>? Mounts, IReadOnlyList<JsonElement>? Pets,
    int? UnlockedBattlePetSlots, IReadOnlyList<ToyRef>? Toys, JsonElement? Heirlooms, IReadOnlyList<TransmogSlot>? Transmogs);

/// <summary>Dokumenty z API (camelCase). Prázdný objekt = API nic nevrátilo, null = dotaz selhal.</summary>
public sealed record EncountersInfo(JsonElement? Dungeons, JsonElement? Raids);

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
    int? Experience,
    string? Gender,
    int? AchievementPoints,
    int? AverageItemLevel,
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
    CollectionsInfo? Collections,
    JsonElement? Appearance,
    EncountersInfo? Encounters,
    JsonElement? Titles,
    JsonElement? MythicKeystone,
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

/// <summary>Ruleset (ve Forever místo realmu): 1 PvP, 2 Normal, 3 Roleplay, 15 Roleplay PvP.</summary>
public sealed record Ruleset(int Id, string? Name);

public sealed record AuctionHouse(int? Id, string? Name);

public sealed record ItemModifier(int? Type, int? Value);

public enum AuctionKind
{
    /// <summary>Jednotlivé nabídky (Buyout, případně Bid).</summary>
    Auctions,
    /// <summary>Hromadné zboží (UnitPrice).</summary>
    Commodities,
}

/// <summary>
/// Jedna nabídka. Ceny jsou v copperu. U Auctions je vyplněné Buyout (a někdy Bid, Context, Modifiers, BonusLists),
/// u Commodities UnitPrice. ItemName jen při names: true.
/// </summary>
public sealed record Auction(long? Id, int? ItemId, int? Quantity, long? Buyout, long? Bid, long? UnitPrice,
    string? TimeLeft, int? Context, IReadOnlyList<ItemModifier>? Modifiers, IReadOnlyList<int>? BonusLists,
    string? ItemName = null);

/// <summary>Total = všechny nabídky domu, Matched = po filtru podle itemů, Returned = po omezení limitem.</summary>
public sealed record AuctionListing(AuctionHouse House, Ruleset Ruleset, AuctionKind Kind, int Total, int Matched,
    int Returned, IReadOnlyList<Auction> Auctions, DateTimeOffset FetchedAt, bool Stale);

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
