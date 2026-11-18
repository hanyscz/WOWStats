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
| `GetCharacterAsync(name, sections, ttl, refresh, ct)` | `name` je slug nebo `"Jmeno Prijmeni"`; nevyžádané sekce jsou `null` |
| `GetGuildAsync(name, ttl, refresh, ct)` | `name` je název guildy nebo slug; vrací `Guild` se seznamem `Members` |

`CharacterSections.Lifetime` stáhne statistiky za celou dobu (`Character.Lifetime`): `Highlights` (např. `deaths`, `deathsFalling`, `totalKills`, `questsCompleted`; chybějící statistika = 0) a `All` se všemi statistikami (`Id`, `Category`, `Name`, `Value`). Názvy a kategorie jsou jen anglicky. Přehled klíčů je v [../README.md](../README.md#statistiky-za-celou-dobu-lifetime).

`Guild`: `Name`, `Slug`, `Id`, `Faction`, `MemberCount`, `AchievementPoints`, `CreatedAt`, `Ruleset`, `Members`, `FetchedAt`, `Stale`.
`GuildMember`: `Name`, `Surname`, `Slug`, `Level`, `CharacterClass`, `Race`, `Faction`, `Rank` (číslo, 0 bývá vedoucí; názvy ranků API nevrací). `Slug` člena jde použít v `GetCharacterAsync`. Aktivita, guildové achievementy a banka API neposkytuje.

Výjimky (společný předek `WowStatsException` s vlastností `Code`): `NotFoundException` (2) s potomky `CharacterNotFoundException` a `GuildNotFoundException`, `UpstreamException` (3), `ConfigException` (4).

`ForeverClient` je bezpečný pro souběžné použití. V aplikaci s DI ho registruj jako singleton a případně mu předej sdílený `HttpClient`.

## Ukázka

```
cd csharp
dotnet run --project WowStats.Sample -- character jmeno-prijmeni [Stats,Gear,Professions,Media] [--refresh] [--no-wowhead]
dotnet run --project WowStats.Sample -- guild "Nazev Guildy" [--refresh] [--top 5]
```

Ukázka hledá `.env` v aktuální složce a nahoru. Cache sdílí s Python verzí (token, názvy, itemy).
