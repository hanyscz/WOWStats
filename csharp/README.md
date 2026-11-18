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
```

| Člen | Význam |
|---|---|
| `ForeverOptions.ClientId/ClientSecret` | klíče z develop.battle.net |
| `Region`, `Locale`, `Ttl`, `CacheDir` | region (`eu`), jazyk, platnost cache (15 min), složka cache |
| `UseWowhead` | doplnit názvy itemů, které Blizzard nezná (výchozí `true`) |
| `GetCharacterAsync(name, sections, ttl, refresh, ct)` | `name` je slug nebo `"Jmeno Prijmeni"`; nevyžádané sekce jsou `null` |

Výjimky (společný předek `WowStatsException` s vlastností `Code`): `CharacterNotFoundException` (2), `UpstreamException` (3), `ConfigException` (4).

`ForeverClient` je bezpečný pro souběžné použití. V aplikaci s DI ho registruj jako singleton a případně mu předej sdílený `HttpClient`.

## Ukázka

```
cd csharp
dotnet run --project WowStats.Sample -- jmeno-prijmeni [Stats,Gear,Professions,Media] [--refresh] [--no-wowhead]
```

Ukázka hledá `.env` v aktuální složce a nahoru. Cache sdílí s Python verzí (token, názvy, itemy).
