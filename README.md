# WOWStats

Statistiky postavy z **WoW Forever** (neoficiální Blizzard API). Žádný běžící server: je to knihovna a příkazová řádka, kterou si zavolá libovolná aplikace s parametry. Python verze nemá žádné závislosti, existuje i samostatná C# knihovna.

> **Upozornění**
> - Projekt **není spojený s Blizzard Entertainment ani s Wowheadem** a není jimi schválen. World of Warcraft je ochranná známka Blizzard Entertainment.
> - **WoW Forever API je neoficiální a nedokumentované.** Blizzard ho oficiálně nevydal, endpointy se mohou kdykoli změnit nebo zmizet. Popis vznikl zkoumáním komunity ([mhuijskens.com/WoWForever/Api](https://mhuijskens.com/WoWForever/Api/)).
> - Názvy itemů, které Blizzard nezná, se doplňují z **neoficiálního endpointu Wowheadu**, který může také zmizet. Dá se vypnout (viz níže). Nezatěžuj ho: dotazy jsou cachované a mají odstup.
> - Potřebuješ **vlastní klíče** (Client ID a Secret), zdarma na [develop.battle.net](https://develop.battle.net). Nikdy je necommituj, `.env` je v `.gitignore`.

Tři cesty, stejný výsledek (JSON / objekt s poli `name`, `surname`, `level`, `race`, `characterClass`, `faction`, `guild`, `ruleset`, `itemLevel`, `lastLogin`, `stats`, `gear[]`, `professions[]`, `avatarUrl`, `renderUrl`, `fetchedAt`, `stale`):

| Použití | Jak |
|---|---|
| Python | `from wowstats import get_character` |
| Cokoliv jiného | `wowstats character <jmeno>` (nebo `python -m wowstats ...`) vypíše JSON na stdout |
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

## Python

```python
from wowstats import get_character

c = get_character("jmeno-prijmeni", sections=["stats", "gear"], ttl=600)
print(c["level"], c["stats"]["attack_power"])
```

Bez `.env`: `get_character(name, client_id="...", client_secret="...")`.

## CLI (pro jakýkoliv jazyk)

```
wowstats character jmeno-prijmeni --sections stats,professions --ttl 600 --pretty
```

Další volby: `--refresh --region --locale --env-file --no-wowhead --client-id --client-secret` (secret raději přes proměnnou prostředí, argumenty jsou vidět v seznamu procesů).
Výstup je JSON na stdout. Chyba jde jako JSON na stderr (`{"error": "...", "code": N}`) a návratový kód je: `2` postava nenalezena, `3` chyba Blizzard API, `4` chybná konfigurace.

## C#

```csharp
using WowStats;

using var client = new ForeverClient(ForeverOptions.FromEnvironment());
Character c = await client.GetCharacterAsync("jmeno-prijmeni",
    CharacterSections.Stats | CharacterSections.Gear, ttl: TimeSpan.FromMinutes(10));
Console.WriteLine($"{c.Name} {c.Level} {c.Stats!["attack_power"]}");
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
