# WowStats (C#)

C# knihovna se stejnou logikou jako Python balíček `wowstats` v kořeni repozitáře: statistiky postavy z **WoW Forever** (neoficiální Blizzard API). Cílí na `net8.0`, nemá žádné NuGet závislosti a nepotřebuje Python.

Hlavní dokumentace (konfigurace, parametry, cache, názvy itemů, upozornění) je v [../README.md](../README.md).

## Přidání do projektu

```xml
<ProjectReference Include="..\cesta\k\WOWStats\csharp\WowStats\WowStats.csproj" />
```

## Použití

```csharp
using WowStats;

var options = ForeverOptions.FromEnvironment();     // proměnné prostředí, pak .env
// nebo: new ForeverOptions { ClientId = "...", ClientSecret = "..." }

using var client = new ForeverClient(options);
Character c = await client.GetCharacterAsync("jmeno-prijmeni",
    CharacterSections.Stats | CharacterSections.Gear,
    ttl: TimeSpan.FromMinutes(10));

Console.WriteLine($"{c.Name} {c.Level} {c.Stats!["attack_power"]}");

Guild g = await client.GetGuildAsync("Nazev Guildy");   // název nebo slug
foreach (var m in g.Members.Take(5))                    // seřazeno: rank, level sestupně, jméno
    Console.WriteLine($"rank {m.Rank} | {m.Name} {m.Surname} | lvl {m.Level} {m.Race} {m.CharacterClass}");
```

| Člen | Význam |
|---|---|
| `ForeverOptions.ClientId/ClientSecret` | klíče z develop.battle.net |
| `Region`, `Locale`, `Ttl`, `CacheDir` | region (`eu`), jazyk, platnost cache (15 min), složka cache |
| `UseWowhead` | doplnit názvy itemů, které Blizzard nezná (výchozí `true`) |
| `GetCharacterAsync(name, sections, ttl, refresh, ct)` | `name` je slug nebo `"Jmeno Prijmeni"`; `sections` výchozí `CharacterSections.Default`; nevyžádané sekce jsou `null` |
| `GetGuildAsync(name, ttl, refresh, ct)` | `name` je název guildy nebo slug; vrací `Guild` se seznamem `Members` |

`CharacterSections.Lifetime` stáhne statistiky za celou dobu (`Character.Lifetime`): `Highlights` (např. `deaths`, `deathsFalling`, `totalKills`, `questsCompleted`; chybějící statistika = 0) a `All` se všemi statistikami (`Id`, `Category`, `Name`, `Value`). Názvy a kategorie jsou jen anglicky. Přehled klíčů je v [../README.md](../README.md#statistiky-za-celou-dobu-lifetime).

### Sekce (`CharacterSections`)

`Default` = `Stats | Gear | Professions | Media | Lifetime` (to dostaneš bez parametru). Další sekce jsou **na vyžádání**, každá je aspoň jeden dotaz navíc, a `All` vezme všechno:

| Příznak | Vlastnost `Character` | Obsah |
|---|---|---|
| `Achievements` | `Achievements` (`AchievementsInfo`) | `Total`, `Points`, `Completed` (`Id`, `Name`, `CompletedAt`, seřazeno podle času), `Recent`, `Categories` |
| `Reputations` | `Reputations` | `Id`, `Name`, `Standing`, `Tier` (3 Neutral až 7 Exalted), `Value`, `Max`, `Raw`; tier sestupně |
| `Quests` | `Quests` (`QuestsInfo`) | `CompletedCount`, `Completed`, `InProgress` (`Id`, `Name`); `Completed` je `null`, když se seznam nepodařilo získat |
| `Pvp` | `Pvp` (`PvpInfo`) | `HonorableKills`, `Rank`, `Battlegrounds` (`Played`, `Won`, `Lost`) |
| `HunterPets` | `HunterPets` | `Name`, `Level`, `Slot`, `Active`, `Summoned`, `Creature`; `null` u jiných povolání než lovec |
| `Specializations` | `Specializations` | skupiny `Active` + `Specializations` (`Name`, `Points`, `Talents` s `Rank`, `SpellId`, `Description`) |
| `Icons` | `Gear[].IconUrl` | URL ikony z Blizzardu; **automaticky zahrne `Gear`**; cache `icons.json` sdílená s Pythonem |
| `ItemDetails` | `Gear[].Details` (`JsonElement?`) | **celý záznam itemu z API** (bez odkazů, camelCase): `binding`, `stats`, `armor`, `weapon`, `durability`, `requirements`, `sellPrice`… Automaticky zahrne `Gear` |
| `Recipes` | `Professions[].Tiers`, `Professions[].Recipes` | všechny známé recepty (`Id`, `Name`, `Tier`). Automaticky zahrne `Professions` |
| `Collections` | `Collections` (`CollectionsInfo`) | `Mounts`, `Pets` (celé záznamy jako `JsonElement`), `UnlockedBattlePetSlots`, `Toys`, `Heirlooms`, `Transmogs`; část je `null`, když její dotaz selhal |
| `Appearance` | `Appearance` (`JsonElement?`) | celý záznam: `playableRace`, `gender`, `guildCrest`, `items`, `customizations` |
| `Encounters` | `Encounters` (`EncountersInfo`) | `Dungeons`, `Raids` jako `JsonElement` (zatím prázdné objekty) |
| `Titles`, `MythicKeystone` | `Titles`, `MythicKeystone` (`JsonElement?`) | celé záznamy z API (zatím skoro prázdné) |

Sekce vracené jako `JsonElement` jsou **celé záznamy z API** bez odkazů a s klíči v camelCase (`display_string` → `displayString`). Prázdný objekt `{}` znamená „API nic nevrátilo“, `null` „dotaz selhal“ nebo „sekce nebyla vyžádána“. V základu mají `Character` i nové vlastnosti `Experience`, `Gender`, `AchievementPoints`, `AverageItemLevel` a `AchievementsInfo.InProgress` (rozpracované úspěchy s kritérii).

```csharp
Character c = await client.GetCharacterAsync("jmeno-prijmeni",
    CharacterSections.Default | CharacterSections.Achievements | CharacterSections.Icons);
Console.WriteLine($"{c.Achievements!.Completed.Count} úspěchů, ikona: {c.Gear![0].IconUrl}");
```

Názvy v těchto sekcích vrací API jen anglicky (knihovna žádá `en_GB`). **Změna oproti 0.2.0:** `All` dřív znamenalo výchozí výběr, teď znamená všechny sekce; výchozí hodnota parametru `sections` je `Default`, takže stávající volání bez parametru se chovají stejně. Podrobnosti v [../README.md](../README.md#další-sekce-na-vyžádání).

### Aukce, rulesety a libovolný endpoint

```csharp
IReadOnlyList<Ruleset> rulesets = await client.GetRulesetsAsync();            // 1 PvP, 2 Normal, 3 Roleplay, 15 Roleplay PvP
IReadOnlyList<AuctionHouse> houses = await client.GetAuctionHousesAsync();    // 15 domů (výchozí ruleset Normal)

AuctionListing l = await client.GetAuctionsAsync(110,
    kind: AuctionKind.Commodities, itemIds: [2589], limit: 100, names: true);
Console.WriteLine($"{l.Matched} nabídek z {l.Total}, nejlevnější {l.Auctions.Min(a => a.UnitPrice)} copper");

JsonElement achievement = await client.RawAsync("/data/wow/achievement/855", locale: "en_GB", clean: true);
```

- `GetAuctionsAsync(house, ruleset, kind, itemIds, limit, names, ttl, refresh)`: `house` je **id** domu (110, 111…), `ruleset` id nebo název (`"2"`, `"Normal"`). `Auction`: `Id`, `ItemId`, `Quantity`, `Buyout`/`Bid` (u `Auctions`), `UnitPrice` (u `Commodities`), `TimeLeft`, `Context`, `Modifiers`, `BonusLists`, `ItemName`. **Ceny jsou v copperu.** `AuctionListing`: `House`, `Ruleset`, `Kind`, `Total` (všechny nabídky domu), `Matched` (po filtru), `Returned` (po limitu).
- Jeden dům má kolem 142 tisíc nabídek (15 MB). Cachuje se celý na disk a `itemIds` a `limit` se aplikují až nad cache. Data aukcí jsou u všech rulesetů totožná.
- `RawAsync(path, kind = "static", parameters, locale, ttl, refresh, clean)` vrací libovolný endpoint (`/data/wow/…`, `/profile/wow/…`) jako `JsonElement`, s cache a ošetřením chyb (`NotFoundException`, `UpstreamException`, `ConfigException`). Jazyk bere z klienta; při `cs_CZ` jsou u Forever názvy `null`, proto předej `locale: "en_GB"`.
- Podrobnosti a velikosti v [../README.md](../README.md#aukce-rulesety-a-libovolný-endpoint).

`Guild`: `Name`, `Slug`, `Id`, `Faction`, `MemberCount`, `AchievementPoints`, `CreatedAt`, `Ruleset`, `Members`, `FetchedAt`, `Stale`.
`GuildMember`: `Name`, `Surname`, `Slug`, `Level`, `CharacterClass`, `Race`, `Faction`, `Rank` (číslo, 0 bývá vedoucí; názvy ranků API nevrací). `Slug` člena jde použít v `GetCharacterAsync`. Aktivita, guildové achievementy a banka API neposkytuje.

Výjimky (společný předek `WowStatsException` s vlastností `Code`): `NotFoundException` (2) s potomky `CharacterNotFoundException` a `GuildNotFoundException`, `UpstreamException` (3), `ConfigException` (4).

`ForeverClient` je bezpečný pro souběžné použití. V aplikaci s DI ho registruj jako singleton a případně mu předej sdílený `HttpClient`.

## Ukázka

```
cd csharp
dotnet run --project WowStats.Sample -- character jmeno-prijmeni [Stats,Gear,Professions,Media] [--refresh] [--no-wowhead]
dotnet run --project WowStats.Sample -- guild "Nazev Guildy" [--refresh] [--top 5]
dotnet run --project WowStats.Sample -- rulesets
dotnet run --project WowStats.Sample -- auctions 110 [--kind commodities] [--item 2589] [--limit 5] [--names]
dotnet run --project WowStats.Sample -- raw /data/wow/achievement/855 [--kind static] [--clean]
```

Ukázka hledá `.env` v aktuální složce a nahoru. Cache sdílí s Python verzí (token, názvy, itemy).
