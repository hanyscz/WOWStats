# WOWStats

Statistiky postav a guild z **WoW Forever** (neoficiální Blizzard API). Žádný běžící server: je to knihovna a příkazová řádka, kterou si zavolá libovolná aplikace s parametry. Python verze nemá žádné závislosti, existuje i samostatná C# knihovna.

> **Upozornění**
> - Projekt **není spojený s Blizzard Entertainment ani s Wowheadem** a není jimi schválen. World of Warcraft je ochranná známka Blizzard Entertainment.
> - **WoW Forever API je neoficiální a nedokumentované.** Blizzard ho oficiálně nevydal, endpointy se mohou kdykoli změnit nebo zmizet. Popis vznikl zkoumáním komunity ([mhuijskens.com/WoWForever/Api](https://mhuijskens.com/WoWForever/Api/)).
> - Názvy itemů, které Blizzard nezná, se doplňují z **neoficiálního endpointu Wowheadu**, který může také zmizet. Dá se vypnout (viz níže). Nezatěžuj ho: dotazy jsou cachované a mají odstup.
> - Potřebuješ **vlastní klíče** (Client ID a Secret), zdarma na [develop.battle.net](https://develop.battle.net). Nikdy je necommituj, `.env` je v `.gitignore`.

Tři cesty, stejný výsledek (JSON / objekt s poli `name`, `surname`, `level`, `race`, `characterClass`, `faction`, `guild`, `ruleset`, `itemLevel`, `lastLogin`, `stats`, `gear[]`, `professions[]`, `avatarUrl`, `renderUrl`, `fetchedAt`, `stale`):

| Použití | Jak |
|---|---|
| Python | `from wowstats import get_character` |
| Cokoliv jiného | `wowstats character <jmeno>` / `wowstats guild <nazev>` (nebo `python -m wowstats ...`) vypíše JSON na stdout |
| C# | knihovna [`csharp/WowStats`](csharp/README.md), nepotřebuje Python |

## Instalace

Python 3.10+, bez závislostí:

```
git clone <url-repozitare> && cd WOWStats
pip install .
```

Bez instalace stačí spouštět `python -m wowstats ...` ze složky repozitáře.

## Konfigurace

Pořadí: argumenty > proměnné prostředí > `.env`. Zkopíruj `.env.example` na `.env` a vyplň klíče.
`BLIZZARD_CLIENT_ID`, `BLIZZARD_CLIENT_SECRET`, volitelně `BLIZZARD_REGION` (eu), `BLIZZARD_LOCALE` (en_GB), `CACHE_TTL_SECONDS` (900), `WOWSTATS_WOWHEAD` (1), `WOWSTATS_CACHE_DIR`.

## Parametry

| Parametr | Význam |
|---|---|
| jméno | slug `jmeno-prijmeni` nebo `"Jmeno Prijmeni"` (ve Forever nejsou realmy, postava je jedinečná v rámci regionu) |
| sekce | `stats, gear, professions, media`; méně sekcí = méně HTTP dotazů (profil je vždy) |
| ttl | jak dlouho se bere výsledek z disku (výchozí 900 s) |
| refresh | obejde cache |
| wowhead | doplnit názvy itemů z Wowheadu (výchozí zapnuto): Python `use_wowhead=False`, CLI `--no-wowhead`, C# `UseWowhead = false`, env `WOWSTATS_WOWHEAD=0` |
| region, locale | `eu/us/kr/tw`, jazyk (pro Forever fakticky `en_GB`, jména se při jiném jazyce doplní z angličtiny) |

Cache je na disku (`%LOCALAPPDATA%\wowstats`, jinak `~/.cache/wowstats`, přepíše `WOWSTATS_CACHE_DIR`): token (~24 h), názvy z static dat, názvy itemů a výsledky. Opakované volání z libovolného procesu je tak okamžité a šetří limit API. Pokud Blizzard selže a stará data existují, vrátí se s `stale: true`.
Token (`token-*.json`), názvy (`names.json`) a itemy (`items.json`) sdílí Python i C#; výsledky postav (`char-*` / `cs-char-*`) má každý jazyk zvlášť.

### Názvy itemů

Blizzard static data zhruba čtvrtinu itemů nezná. Pro ně se jednou zeptá Wowheadu (`nether.wowhead.com/forever/tooltip/item/{id}`). Pořadí: `items.json` → Blizzard → Wowhead. Nalezený název se ukládá trvale a už se nikdy nepřeptává. Nenalezený item (404) se zkusí znovu nejdřív po 7 dnech; při síťové chybě se nic neukládá. Dotazy na Wowhead mají odstup 400 ms a User-Agent `wowstats/0.1`. Při vypnutém Wowheadu (nebo pokud item neexistuje nikde) zůstává `name: null`, vždy je ale k dispozici `itemId`.

## Guilda

Kromě postav jde stáhnout guildu a seznam jejích členů: `get_guild` (Python), `wowstats guild` (CLI), `GetGuildAsync` (C#). Jméno je název guildy nebo slug (`"Nazev Guildy"` / `nazev-guildy`). Parametry `ttl`, `refresh`, region, locale a klíče fungují stejně jako u postav (sekce a Wowhead se u guild nepoužívají).

Odpověď: `name`, `slug`, `id`, `faction`, `memberCount`, `achievementPoints`, `createdAt`, `ruleset`, `members[]`, `fetchedAt`, `stale`.
Člen: `name`, `surname`, `slug`, `level`, `characterClass`, `race`, `faction`, `rank`. Členové jsou seřazeni podle ranku, pak levelu sestupně a jména. `slug` člena jde rovnou použít pro stažení celé postavy.

Co API neposkytuje (ověřeno, příslušné endpointy vrací jen hlavičku guildy): aktivitu a guildové achievementy, banku, zprávu dne a online stav členů. `rank` je jen číslo (0 bývá vedoucí), názvy ranků API nevrací. Seznam obsahuje jen základ členů, výbavu a statistiky je potřeba stáhnout zvlášť přes `slug`.
Guilda se stahuje dvěma dotazy plus jednorázovými názvy tříd a ras (cachují se), první běh u guildy o ~90 členech trvá kolem 4 s, další jsou z cache okamžité.

## Python

```python
from wowstats import get_character, get_guild

c = get_character("jmeno-prijmeni", sections=["stats", "gear"], ttl=600)
print(c["level"], c["stats"]["attack_power"])

g = get_guild("Nazev Guildy")
print(g["memberCount"], [m["name"] for m in g["members"][:3]])
```

Bez `.env`: `get_character(name, client_id="...", client_secret="...")` (totéž u `get_guild`).
Chyby jsou výjimky `WowStatsError`: `CharacterNotFound` / `GuildNotFound` (společný předek `NotFound`), `UpstreamError`, `ConfigError`; každá má `code` jako návratový kód CLI.

## CLI (pro jakýkoliv jazyk)

```
wowstats character jmeno-prijmeni --sections stats,professions --ttl 600 --pretty
wowstats guild "Nazev Guildy" --pretty
```

Další volby: `--ttl --refresh --region --locale --env-file --client-id --client-secret` (secret raději přes proměnnou prostředí, argumenty jsou vidět v seznamu procesů), u `character` navíc `--sections` a `--no-wowhead`.
Výstup je JSON v UTF-8 na stdout. Chyba jde jako JSON na stderr (`{"error": "...", "code": N}`) a návratový kód je: `2` postava nebo guilda nenalezena, `3` chyba Blizzard API, `4` chybná konfigurace.

## C#

```csharp
using WowStats;

using var client = new ForeverClient(ForeverOptions.FromEnvironment());
Character c = await client.GetCharacterAsync("jmeno-prijmeni",
    CharacterSections.Stats | CharacterSections.Gear, ttl: TimeSpan.FromMinutes(10));
Console.WriteLine($"{c.Name} {c.Level} {c.Stats!["attack_power"]}");

Guild g = await client.GetGuildAsync("Nazev Guildy");
Console.WriteLine($"{g.MemberCount} členů, první: {g.Members[0].Name}");
```

Podrobnosti a ukázkový projekt v [csharp/README.md](csharp/README.md).

## Vývoj

```
python -m unittest discover -s tests -v     # offline testy, HTTP je nahrazeno atrapou
dotnet build csharp/WowStats.Sample         # sestavení C# knihovny
```

Při každém pushi a pull requestu to samé spouští GitHub Actions ([.github/workflows/ci.yml](.github/workflows/ci.yml)).

## Limity API

Podle dokumentace Blizzardu: 100 požadavků/s a 36 000/h na klienta (pro Forever nepotvrzeno). Data postavy se aktualizují až po odhlášení ze hry, účet s mírným zpožděním. Postavy pod level 10 API nevrací.

## Licence

[MIT](LICENSE)
