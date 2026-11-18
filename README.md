# WOWStats

Statistiky postav a guild z **WoW Forever** (neoficiální Blizzard API). Žádný běžící server: je to knihovna a příkazová řádka, kterou si zavolá libovolná aplikace s parametry. Python verze nemá žádné závislosti, existuje i samostatná C# knihovna.

> **Upozornění**
> - Projekt **není spojený s Blizzard Entertainment ani s Wowheadem** a není jimi schválen. World of Warcraft je ochranná známka Blizzard Entertainment.
> - **WoW Forever API je neoficiální a nedokumentované.** Blizzard ho oficiálně nevydal, endpointy se mohou kdykoli změnit nebo zmizet. Popis vznikl zkoumáním komunity ([mhuijskens.com/WoWForever/Api](https://mhuijskens.com/WoWForever/Api/)).
> - Názvy itemů, které Blizzard nezná, se doplňují z **neoficiálního endpointu Wowheadu**, který může také zmizet. Dá se vypnout (viz níže). Nezatěžuj ho: dotazy jsou cachované a mají odstup.
> - Potřebuješ **vlastní klíče** (Client ID a Secret), zdarma na [develop.battle.net](https://develop.battle.net). Nikdy je necommituj, `.env` je v `.gitignore`.

Tři cesty, stejný výsledek (JSON / objekt s poli `name`, `surname`, `level`, `race`, `characterClass`, `faction`, `guild`, `ruleset`, `itemLevel`, `lastLogin`, `stats`, `gear[]`, `professions[]`, `lifetime`, `avatarUrl`, `renderUrl`, `fetchedAt`, `stale`; na vyžádání ještě `achievements`, `reputations`, `quests`, `pvp`, `hunterPets`, `specializations` a `gear[].iconUrl`, viz [další sekce](#další-sekce-na-vyžádání)). Přesně co jde z API stáhnout a co z toho knihovna umí, je v [přehledu API](#přehled-dostupných-api):

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
| sekce | výchozí: `stats, gear, professions, media, lifetime`; na vyžádání `achievements, reputations, quests, pvp, hunterPets, specializations, icons`. Lze zadat seznam, řetězec oddělený čárkami, `default` nebo `all`. Méně sekcí = méně HTTP dotazů (profil je vždy, každá sekce je aspoň jeden dotaz navíc) |
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

# statistiky za celou dobu: smrti, zabití, questy… (jeden dotaz navíc)
hl = get_character("jmeno-prijmeni", sections=["lifetime"])["lifetime"]["highlights"]
print(hl["deaths"], hl["deathsFalling"], hl["totalKills"])

# další sekce na vyžádání (každá je aspoň jeden dotaz navíc), "all" vezme všechno
c = get_character("jmeno-prijmeni", sections=["achievements", "reputations", "icons"])
print(c["achievements"]["completed"][-1], c["gear"][0]["iconUrl"])

g = get_guild("Nazev Guildy")
print(g["memberCount"], [m["name"] for m in g["members"][:3]])
```

Bez `.env`: `get_character(name, client_id="...", client_secret="...")` (totéž u `get_guild`).
Chyby jsou výjimky `WowStatsError`: `CharacterNotFound` / `GuildNotFound` (společný předek `NotFound`), `UpstreamError`, `ConfigError`; každá má `code` jako návratový kód CLI.

## CLI (pro jakýkoliv jazyk)

```
wowstats character jmeno-prijmeni --sections stats,professions --ttl 600 --pretty
wowstats character jmeno-prijmeni --sections all --pretty          # všechny sekce včetně ikon
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

// další sekce na vyžádání; All = všechno, Icons doplní gear[].IconUrl
Character full = await client.GetCharacterAsync("jmeno-prijmeni",
    CharacterSections.Default | CharacterSections.Achievements | CharacterSections.Icons);
Console.WriteLine($"{full.Achievements!.Completed.Count} úspěchů, ikona: {full.Gear![0].IconUrl}");

Guild g = await client.GetGuildAsync("Nazev Guildy");
Console.WriteLine($"{g.MemberCount} členů, první: {g.Members[0].Name}");
```

Podrobnosti a ukázkový projekt v [csharp/README.md](csharp/README.md).

## Statistiky za celou dobu (`lifetime`)

Sekce `lifetime` stahuje `achievements/statistics`: smrti, zabití, questy, battlegroundy a další (kolem 50–90 hodnot podle postavy, pole `all`). Z nich je vybraných pár do `highlights` pod stabilními klíči. Chybějící statistika znamená 0 (API vrací jen ty, které postava někdy měla).

| Klíč v `highlights` | Význam |
|---|---|
| `deaths` | smrtí celkem |
| `deathsFalling`, `deathsDrowning`, `deathsFireLava` | smrti pádem, utonutím, ohněm nebo lávou |
| `deathsFromPlayers`, `deathsFromOppositeFaction` | smrti od hráčů, od druhé frakce |
| `deathsInBattlegrounds`, `deathsDungeonsRaids` | smrti v battlegroundech (součet), v dungeonech a raidech |
| `totalKills`, `honorableKills`, `killingBlows` | zabití celkem, čestná zabití, smrtící rány |
| `questsCompleted`, `questsAbandoned` | splněné a opuštěné questy |
| `battlegroundsPlayed`, `battlegroundsWon`, `dungeonsEntered` | battlegroundy a dungeony |
| `fishCaught`, `flightPaths`, `timesHearthed`, `timesCamped` | ryby, letové trasy, hearthstone, odhlášení |

`all[]` obsahuje každou statistiku jako `{id, category, name, value}` (u některých i `description`), např. `{id: 114, category: "Deaths/World", name: "Deaths from falling", value: 1}`. Klíče v `highlights` se přiřazují podle `id`, protože ty jsou stabilní.
**Názvy a kategorie vrací API jen anglicky** (v jiném jazyce jsou `null`), proto knihovna u této sekce vždy žádá `en_GB`. Čísla jsou stejná jako v „Síni hanby“ na svetwarcraftu.cz (ověřeno na několika postavách).

## Další sekce na vyžádání

Kromě výchozích sekcí (`stats, gear, professions, media, lifetime`) umí `get_character` na požádání dalších šest sekcí a ikony itemů. **Nestahují se, dokud o ně nepožádáš**, protože každá znamená aspoň jeden HTTP dotaz navíc na postavu. `sections="all"` (CLI `--sections all`, C# `CharacterSections.All`) vezme všechno. Aplikace si tak vyžádá jen to, co potřebuje.

| Sekce | Pole v odpovědi | Dotazů | Co obsahuje |
|---|---|---|---|
| `achievements` | `achievements` | 1 | `total`, `points`, `completed[]` (`{id, name, completedAt}`, seřazeno podle času splnění), `recent[]` (`{id, name, at}`), `categories[]` (`{id, name, quantity, points}`). Rozpracované úspěchy se vynechávají |
| `reputations` | `reputations[]` | 1 | `{id, name, standing, tier, value, max, raw}`. `standing` je např. „Honored“, `tier` 3 Neutral až 7 Exalted, `value`/`max` je postup v aktuální úrovni, `raw` celkem. Seřazeno: tier sestupně, pak název |
| `quests` | `quests` | 2 | `completedCount`, `completed[]` (`{id, name}`), `inProgress[]`. `completed` a `completedCount` jsou `null`, když se seznam splněných nepodařilo získat |
| `pvp` | `pvp` | 1 | `honorableKills`, `rank`, `battlegrounds[]` (`{id, name, played, won, lost}`) |
| `hunterPets` | `hunterPets[]` | 1 | `{name, level, slot, active, summoned, creatureId, creature}`. U jiných povolání než lovec je **`null`** (API vrací 404) |
| `specializations` | `specializations[]` | 1 | skupiny `{active, specializations: [{id, name, points, talents: [{id, name, rank, spellId, description}]}]}`. Jen stromy talentů s utracenými body |
| `icons` | `gear[].iconUrl` | 0 až N | URL ikony itemu z Blizzardu. **Automaticky zahrne `gear`.** Jeden dotaz na každý dosud neznámý item, pak trvale v cache (`icons.json`, sdílená s C#). Nenalezený item (404) se zkusí znovu po 7 dnech, chyby sítě se neukládají. Bez `icons` pole `iconUrl` v `gear` není |

Společná pravidla:
- Názvy v těchto sekcích vrací API **jen anglicky**, proto knihovna žádá `en_GB` bez ohledu na nastavený jazyk.
- Pole sekce je `null`, když ji API nevrátilo (chyba, nebo u `hunterPets` jiné povolání).
- Pro nepožadované sekce klíč v odpovědi vůbec není (C#: vlastnost je `null`).
- Cena: první stažení všech sekcí jedné postavy trvalo kolem 7 až 9 s (asi 13 dotazů plus jednorázově názvy a ikony), další z cache zlomek sekundy. Pro desítky postav radši vyžádej jen potřebné sekce.

**Změna oproti 0.2.0:** výchozí sekce zůstaly stejné (`DEFAULT_SECTIONS` v Pythonu, `CharacterSections.Default` v C#), takže stávající kód se chová jako dřív. Ale `ALL_SECTIONS` (Python) a `CharacterSections.All` (C#) dřív znamenaly výchozí výběr a teď znamenají všechny sekce. Kód, který `All` předával výslovně, dostane víc dotazů, a tím i víc dat.

## Přehled dostupných API

Co přesně jde z Blizzard API (a Wowheadu) stáhnout. Ověřeno 18. 11. 2026 na regionu EU voláním každého endpointu. Sloupec **Knihovna** říká, jestli to `wowstats` už umí: ✅ ano (v jaké sekci/poli), ❌ ne (API to nabízí, ale knihovna to zatím nestahuje).

Všechny dotazy jdou na `https://{region}.api.blizzard.com`, s parametry `namespace=…` a `locale=…` a s tokenem z `oauth.battle.net/token` (client credentials, stačí Client ID + Secret). Postava se adresuje `jmeno-prijmeni` (bez realmu). Nic z toho **není oficiálně zdokumentované** ([zdroj](https://mhuijskens.com/WoWForever/Api/)).

### Postava: `/profile/wow/character/{jmeno-prijmeni}` (namespace `profile-forever-{region}`)

| Endpoint (za cestou postavy) | Co vrací | Knihovna |
|---|---|---|
| *(bez přípony)* | jméno, příjmení, level, zkušenosti, rasa, třída, frakce, pohlaví, guilda, ruleset, item level (průměrný i nasazený), poslední přihlášení | ✅ základ odpovědi |
| `/statistics` | atributy, crit, hit, útočná síla, brnění, dodge, parry, block, zdraví, zdroj… (~57 hodnot) | ✅ `stats` |
| `/equipment` | nasazené itemy: slot, id, kvalita, item level, brnění, bonusy, odolnost, požadavky, prodejní cena | ✅ `gear` (slot, id, název, kvalita, ilvl) |
| `/professions` | primární a vedlejší profese, skill, maximum, seznam známých receptů | ✅ `professions` (skill a maximum) |
| `/character-media` | obrázky postavy (`avatar`, `avatar-sd`, `main-raw`, `main-sd-raw`) | ✅ `avatarUrl`, `renderUrl` |
| `/achievements/statistics` | statistiky za celou dobu: smrti, zabití, questy, PvP, dungeony, profese… | ✅ `lifetime` |
| `/achievements` | splněné úspěchy s časem, celkové body, poslední události | ✅ `achievements` |
| `/specializations` | talenty (jméno, rank, popis), utracené body ve stromech | ✅ `specializations` |
| `/reputations` | reputace u frakcí (úroveň, postup) | ✅ `reputations` |
| `/quests/completed` | seznam splněných questů s názvy (u testované postavy 306) | ✅ `quests` |
| `/quests` | rozpracované questy | ✅ `quests` |
| `/appearance` | vzhled postavy (rasa, pohlaví, customizace, erb guildy, itemy) | ❌ |
| `/pvp-summary` | čestná zabití, PvP hodnost, statistiky battlegroundů | ✅ `pvp` |
| `/hunter-pets` | peti lovce (jméno, level, druh) | ✅ `hunterPets` (u ne-lovců 404, v knihovně `null`; na fóru psali, že vrací 404 všem, nám vrací data) |
| `/encounters`, `/encounters/dungeons`, `/encounters/raids` | postup dungeony a raidy | ❌ (u testované postavy prázdné) |
| `/collections`, `/collections/mounts`, `/pets`, `/toys` | mounty, mazlíčci, hračky, transmog | ❌ (u testované postavy prázdné) |
| `/titles`, `/mythic-keystone-profile` | tituly, mythic+ | ❌ (odpovídají, ale bez dat) |

Postava se objeví až od levelu 10 (níž je 404) a data se aktualizují po odhlášení ze hry.

### Guilda: `/data/wow/guild/{slug}` (namespace `profile-forever-{region}`)

| Endpoint | Co vrací | Knihovna |
|---|---|---|
| *(bez přípony)* | název, id, frakce, počet členů, datum založení, ruleset, achievementové body | ✅ `get_guild` |
| `/roster` | všichni členové: jméno, příjmení, slug, level, třída, rasa, frakce, rank (číslo) | ✅ `members[]` |
| `/activity`, `/achievements` | odpovídají, ale vracejí jen hlavičku guildy (žádná data) | ❌ nemá co vrátit |

API nevrací jména ranků, banku, zprávu dne ani online stav.

### Statická data: `/data/wow/…` (namespace `static-forever-{region}`)

Funguje bez ohledu na postavu. Používá se hlavně k překladu `id` na názvy (třída, rasa, item…). Namespace je alias na `static-1.60.1_70399-forever-{region}`.

| Endpoint | Co vrací | Knihovna |
|---|---|---|
| `playable-class/index`, `/{id}` | povolání (9), specializace, rasy, zdroj | ✅ jen názvy |
| `playable-race/index`, `/{id}` | rasy (10), frakce, povolání, rasové schopnosti | ✅ jen názvy |
| `playable-specialization/index`, `/{id}` | specializace (27) | ❌ |
| `talent-tree/index` | talentové stromy (27 specializací + 9 tříd) | ❌ |
| `item/{id}` | detail itemu | ✅ názvy, ale **část itemů vrací 404** (u testované postavy kolem poloviny hledaných) |
| `media/item/{id}` | **ikona itemu** (`render.worldofwarcraft.com/forever-{region}/icons/…`), funguje i pro itemy, které `item/{id}` nezná | ✅ `icons` (`gear[].iconUrl`) |
| `item-class/index`, `/{id}` | druhy itemů (13) a podtypy | ❌ |
| `profession/index`, `/{id}`, `recipe/{id}` | profese (13), recepty, suroviny | ✅ jen názvy profesí |
| `achievement/index`, `achievement-category/index` | úspěchy (176), kategorie | ❌ |
| `reputation-faction/index`, `/{id}` | frakce reputace (56) | ❌ |
| `mount/index`, `pet/index`, `title/index` | mounty (141), mazlíčci (110), tituly (41) | ❌ |
| `power-type/index`, `creature-family/index`, `spell/{id}` | typy zdroje, rodiny tvorů, kouzla | ❌ |
| `search/item?name.en_GB=sword&orderby=id` | vyhledávání itemů podle názvu, stránkované po 100 | ❌ (hledání podle `id` nic nevrátilo, na chybějící názvy nepomůže) |

### Dynamická data: `/data/wow/…` (namespace `dynamic-forever-{region}`)

| Endpoint | Co vrací | Knihovna |
|---|---|---|
| `ruleset/index`, `ruleset/{id}` | rulesety místo realmů (v EU 4; „Normal“ má id 2) | ✅ jen název |
| `ruleset/{id}/auctions/` | 15 aukčních domů a jejich nabídky (lomítko na konci je nutné; všechny rulesety vracejí tytéž domy) | ❌ |
| `realm/index`, `token/index` | **404**: realmy ani cena WoW tokenu ve Forever neexistují | – |

### Nezkoušeno

`/profile/user/wow` (seznam všech postav na účtu) vyžaduje přihlášení hráče přes OAuth (scope `wow.profile`), ne jen Client ID a Secret.

### Wowhead (neoficiální, jen názvy itemů)

`https://nether.wowhead.com/forever/tooltip/item/{id}` vrací `name`, `quality`, `icon` a text tooltipu. Knihovna z něj bere jen `name`, a to pro itemy, které Blizzard nezná (viz „Názvy itemů“). Podmínky použití Wowheadu jsem neověřil, endpoint může kdykoli zmizet.

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
