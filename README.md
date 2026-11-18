# WOWStats

Statistiky postav a guild z **WoW Forever** (neoficiální Blizzard API). Žádný běžící server: je to knihovna a příkazová řádka, kterou si zavolá libovolná aplikace s parametry. Python verze nemá žádné závislosti, existuje i samostatná C# knihovna.

> **Upozornění**
> - Projekt **není spojený s Blizzard Entertainment ani s Wowheadem** a není jimi schválen. World of Warcraft je ochranná známka Blizzard Entertainment.
> - **WoW Forever API je neoficiální a nedokumentované.** Blizzard ho oficiálně nevydal, endpointy se mohou kdykoli změnit nebo zmizet. Popis vznikl zkoumáním komunity ([mhuijskens.com/WoWForever/Api](https://mhuijskens.com/WoWForever/Api/)).
> - Názvy itemů, které Blizzard nezná, se doplňují z **neoficiálního endpointu Wowheadu**, který může také zmizet. Dá se vypnout (viz níže). Nezatěžuj ho: dotazy jsou cachované a mají odstup.
> - Potřebuješ **vlastní klíče** (Client ID a Secret), zdarma na [develop.battle.net](https://develop.battle.net). Nikdy je necommituj, `.env` je v `.gitignore`.

Tři cesty, stejný výsledek (JSON / objekt s poli `name`, `surname`, `level`, `race`, `characterClass`, `faction`, `guild`, `ruleset`, `itemLevel`, `lastLogin`, `stats`, `gear[]`, `professions[]`, `lifetime`, `avatarUrl`, `renderUrl`, `fetchedAt`, `stale`; na vyžádání ještě `achievements`, `reputations`, `quests`, `pvp`, `hunterPets`, `specializations`, `collections`, `appearance`, `encounters`, `titles`, `mythicKeystone`, `gear[].iconUrl`, `gear[].details` a `professions[].recipes`, viz [další sekce](#další-sekce-na-vyžádání); mimo postavu ještě [aukce a rulesety](#aukce-rulesety-a-libovolný-endpoint) a obecné volání `raw`). Přesně co jde z API stáhnout a co z toho knihovna umí, je v [přehledu API](#přehled-dostupných-api):

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
| sekce | výchozí: `stats, gear, professions, media, lifetime`; na vyžádání `achievements, reputations, quests, pvp, hunterPets, specializations, icons, itemDetails, recipes, collections, appearance, encounters, titles, mythicKeystone`. Lze zadat seznam, řetězec oddělený čárkami, `default` nebo `all`. Méně sekcí = méně HTTP dotazů (profil je vždy, každá sekce je aspoň jeden dotaz navíc) |
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

# celé detaily itemů, všechny recepty a sbírky
c = get_character("jmeno-prijmeni", sections=["itemDetails", "recipes", "collections"])
print(c["gear"][0]["details"]["binding"]["name"], len(c["professions"][0]["recipes"]), len(c["collections"]["mounts"]))

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

Kromě výchozích sekcí (`stats, gear, professions, media, lifetime`) umí `get_character` na požádání dalších čtrnáct. **Nestahují se, dokud o ně nepožádáš**, protože každá znamená aspoň jeden HTTP dotaz navíc na postavu. `sections="all"` (CLI `--sections all`, C# `CharacterSections.All`) vezme všechno. Aplikace si tak vyžádá jen to, co potřebuje. Filozofie je „vrať všechno, co API dává“: kde tvar dat není nijak zvlášť zpracovaný, dostaneš celý záznam z API.

| Sekce | Pole v odpovědi | Dotazů | Co obsahuje |
|---|---|---|---|
| `achievements` | `achievements` | 1 | `total`, `points`, `completed[]` (`{id, name, completedAt}`, seřazeno podle času splnění), `recent[]` (`{id, name, at}`), `categories[]` (`{id, name, quantity, points}`), `inProgress[]` (`{id, name, criteria}`, kde `criteria` je záznam z API: `id`, `isCompleted`, `childCriteria[{id, amount, isCompleted}]`) |
| `reputations` | `reputations[]` | 1 | `{id, name, standing, tier, value, max, raw}`. `standing` je např. „Honored“, `tier` 3 Neutral až 7 Exalted, `value`/`max` je postup v aktuální úrovni, `raw` celkem. Seřazeno: tier sestupně, pak název |
| `quests` | `quests` | 2 | `completedCount`, `completed[]` (`{id, name}`), `inProgress[]`. `completed` a `completedCount` jsou `null`, když se seznam splněných nepodařilo získat |
| `pvp` | `pvp` | 1 | `honorableKills`, `rank`, `battlegrounds[]` (`{id, name, played, won, lost}`) |
| `hunterPets` | `hunterPets[]` | 1 | `{name, level, slot, active, summoned, creatureId, creature}`. U jiných povolání než lovec je **`null`** (API vrací 404) |
| `specializations` | `specializations[]` | 1 | skupiny `{active, specializations: [{id, name, points, talents: [{id, name, rank, spellId, description}]}]}`. Jen stromy talentů s utracenými body |
| `icons` | `gear[].iconUrl` | 0 až N | URL ikony itemu z Blizzardu. **Automaticky zahrne `gear`.** Jeden dotaz na každý dosud neznámý item, pak trvale v cache (`icons.json`, sdílená s C#). Nenalezený item (404) se zkusí znovu po 7 dnech, chyby sítě se neukládají. Bez `icons` pole `iconUrl` v `gear` není |
| `itemDetails` | `gear[].details` | 0 | **Celý záznam itemu z API** (bez odkazů, klíče v camelCase): `binding`, `stats[]` (`type`, `value`, `display.displayString`), `armor`, `weapon`, `durability`, `requirements`, `sellPrice`, `spells`, `inventoryType`, `itemClass`, `itemSubclass`, `uniqueEquipped`, `quantity`, `context`, `media`… Nic se nezahazuje. **Automaticky zahrne `gear`**, žádný další dotaz (jen se vyžádá `equipment` v angličtině) |
| `recipes` | `professions[].tiers`, `professions[].recipes` | 0 | `tiers[]` (`{id, name, skill, maxSkill}`) a `recipes[]` (`{id, name, tier}`), všechny známé recepty. **Automaticky zahrne `professions`**, žádný další dotaz. Detail receptu (suroviny, vyráběný item) je statický dokument, viz `raw("/data/wow/recipe/{id}")` |
| `collections` | `collections` | 5 | `mounts[]` (`{id, name, usable}`), `pets[]` (celý záznam: `species`, `level`, `quality`, `stats`, `creatureDisplay`, `id`), `unlockedBattlePetSlots`, `toys[]` (`{id, name}`), `heirlooms[]`, `transmogs[]` (`{slot, appearanceIds[]}`). Část je `null`, když její dotaz selhal; prázdná sbírka je `[]`. U tvé postavy bývají prázdné, u aktivnějších hráčů ne |
| `appearance` | `appearance` | 1 | celý záznam: `playableRace`, `playableClass`, `gender`, `faction`, `guildCrest`, `items[]` (vzhledové itemy se slotem a enchantem), `customizations[]` (`option`, `choice`) |
| `encounters` | `encounters` | 2 | `{dungeons, raids}`: celé dokumenty z API. **Ve Forever zatím vracejí prázdné objekty**, zabití bossů jsou v `lifetime.all` (kategorie „Dungeons & Raids/Boss Kills“) |
| `titles` | `titles` | 1 | celý záznam z API (zatím prázdný objekt) |
| `mythicKeystone` | `mythicKeystone` | 1 | celý záznam z API (zatím jen `currentPeriod`) |

Společná pravidla:
- Názvy v těchto sekcích vrací API **jen anglicky**, proto knihovna žádá `en_GB` bez ohledu na nastavený jazyk.
- Pole sekce je `null`, když ji API nevrátilo (chyba, nebo u `hunterPets` jiné povolání). U sekcí, které se vracejí „jak jsou“ (`appearance`, `titles`, `mythicKeystone`, `encounters`), znamená **`{}` „API nic nevrátilo“** a `null` „dotaz selhal“.
- Pro nepožadované sekce klíč v odpovědi vůbec není (C#: vlastnost je `null`).
- „Celý záznam z API“ znamená odpověď beze ztrát, jen bez odkazů (`_links`, `key: {href}`) a s klíči v camelCase (`display_string` → `displayString`). Hodnoty a vnoření zůstávají. Struktura odpovídá Blizzard API, takže se hodí i jejich dokumentace.
- Cena: první stažení **všech** sekcí jedné postavy (kolem 23 dotazů plus jednorázově názvy a ikony) trvalo kolem 5 až 9 s, další z cache zlomek sekundy. Pro desítky postav radši vyžádej jen potřebné sekce.
- **U každé postavy se v základu vracejí i `experience`, `gender`, `achievementPoints` a `averageItemLevel`** (jsou v profilu, žádný dotaz navíc).

**Změny a kompatibilita.** Všechny úpravy jsou přidání nových polí a sekcí, výchozí chování se nemění (`DEFAULT_SECTIONS` v Pythonu, `CharacterSections.Default` v C#). Výjimka z 0.3.0: `ALL_SECTIONS` (Python) a `CharacterSections.All` (C#) dřív znamenaly výchozí výběr a teď znamenají všechny sekce, takže kód, který `All` předával výslovně, dostane víc dotazů i dat. C# `Character`, `GearItem`, `Profession` a `AchievementsInfo` mají nové vlastnosti. Kdo je vytváří ručně pozičním konstruktorem (typicky v testech), musí je doplnit.

## Aukce, rulesety a libovolný endpoint

### Rulesety a aukce

Ve Forever nejsou realmy, ale **rulesety**: `1` PvP, `2` Normal, `3` Roleplay, `15` Roleplay PvP. **Data aukcí jsou u všech rulesetů stejná** (ověřeno: stejných 142 397 nabídek se stejným prvním id), ruleset aukce nefiltruje. Výchozí je proto `Normal`.

| Python | C# | CLI |
|---|---|---|
| `get_rulesets()` → `[{id, name}]` | `GetRulesetsAsync()` | `wowstats rulesets` |
| `get_auction_houses(ruleset=None)` → `[{id, name}]` (15 domů) | `GetAuctionHousesAsync(ruleset)` | `wowstats auction-houses` |
| `get_auctions(house, ...)` | `GetAuctionsAsync(house, ...)` | `wowstats auctions <dum>` |

`ruleset` je id nebo název (`2`, `"Normal"`). Domy se jmenují opakovaně (3× Alliance, 3× Horde, Blackwater…), proto se `house` zadává **id** (110, 111…).

`get_auctions(house, *, ruleset=None, kind="auctions", item_ids=None, limit=None, names=False, ttl=None, refresh=False)`:

| Parametr | Význam |
|---|---|
| `kind` | `"auctions"` = jednotlivé nabídky, `"commodities"` = hromadné zboží (látka, byliny, lektvary) |
| `item_ids` | jen tyto itemy (filtr se aplikuje na cachovaná data) |
| `limit` | nejvýš N nabídek |
| `names` | doplní `itemName` (nejvýš 500 různých itemů; každý neznámý je dotaz, pak je v cache) |

Odpověď: `house`, `ruleset`, `kind`, `total` (všechny nabídky domu), `matched` (po filtru), `returned` (po limitu), `auctions[]`, `fetchedAt`, `stale`.
Nabídka: `kind="auctions"`: `{id, itemId, quantity, buyout, timeLeft}` plus `bid`, `context`, `modifiers[]`, `bonusLists[]`, když je API má. `kind="commodities"`: `{id, itemId, quantity, unitPrice, timeLeft}`. **Ceny jsou v copperu** (100 copper = 1 silver, 10 000 = 1 gold). `timeLeft` nabývá hodnot `SHORT`, `MEDIUM`, `LONG` (jiné jsem v datech neviděl).

**Velikost:** jeden dům má kolem 142 tisíc nabídek a 131 tisíc komodit (15 MB a 12 MB JSON). Stažení trvá zhruba 2 s, výsledek se **uloží celý na disk** (cache) a `item_ids` a `limit` se aplikují až nad ním, takže další volání z libovolného procesu je v Pythonu kolem 0,2 s (v C# kolem 1,5 s včetně spuštění procesu .NET). Hodnotu `ttl` nastav podle toho, jak čerstvá data potřebuješ (výchozí 900 s).

```python
from wowstats import ForeverClient
c = ForeverClient.from_env()
cheap = c.get_auctions(110, kind="commodities", item_ids=[2589], names=True)   # Linen Cloth
print(min(a["unitPrice"] for a in cheap["auctions"]), "copper")
```

```
wowstats auctions 110 --kind commodities --item-id 2589 --limit 5 --names
```

```csharp
var l = await client.GetAuctionsAsync(110, kind: AuctionKind.Commodities, itemIds: [2589], names: true);
Console.WriteLine($"{l.Matched} nabídek, nejlevnější {l.Auctions.Min(a => a.UnitPrice)} copper");
```

### Libovolný endpoint (`raw`)

Pro všechno, co knihovna nezabaluje (statická data, vyhledávání, nové endpointy, které Blizzard přidá), je obecné volání. Vrací JSON tak, jak přišel, **cachuje ho na disk** a řeší token, namespace a chyby:

| | |
|---|---|
| Python | `client.raw(path, kind="static", params=None, locale=None, ttl=None, refresh=False, clean=False)` |
| C# | `await client.RawAsync(path, kind, parameters, locale, ttl, refresh, clean)` → `JsonElement` |
| CLI | `wowstats raw <cesta> [--kind static\|dynamic\|profile] [--param klic=hodnota] [--clean]` |

`path` musí začínat `/data/wow/` nebo `/profile/wow/`. `kind` volí namespace (`static-forever-…`, `dynamic-forever-…`, `profile-forever-…`). `clean=True` odstraní odkazy a přepíše klíče do camelCase, stejně jako u „celých záznamů“. 404 je `NotFound` (kód 2), jiná chyba `UpstreamError` (3), špatná cesta `ConfigError` (4). Jazyk bere z klienta (`locale`), takže při `cs_CZ` jsou u Forever názvy `null`: pro čitelné texty předej `locale="en_GB"`.

```python
client.raw("/data/wow/achievement/855", locale="en_GB", clean=True)          # definice úspěchu
client.raw("/data/wow/recipe/1353", locale="en_GB")                          # recept: suroviny, vyráběný item
client.raw("/data/wow/item/9807")                                            # detail itemu (část itemů je 404)
client.raw("/data/wow/search/item", params={"name.en_GB": "Linen Cloth", "orderby": "id"})   # hledání itemů
client.raw("/data/wow/ruleset/2", kind="dynamic")
```

## Přehled dostupných API

Co přesně jde z Blizzard API (a Wowheadu) stáhnout. Ověřeno 18. 11. 2026 na regionu EU voláním každého endpointu. Sloupec **Knihovna** říká, jak se k tomu dostaneš: ✅ hotová sekce nebo funkce (v jaké), 🔧 přes obecné volání [`raw`](#libovolný-endpoint-raw) (knihovna řeší token, cache a chyby, tvar dat je přímo z API), ❌ nic z toho (zatím žádný takový endpoint, vyjma OAuth níže).

Všechny dotazy jdou na `https://{region}.api.blizzard.com`, s parametry `namespace=…` a `locale=…` a s tokenem z `oauth.battle.net/token` (client credentials, stačí Client ID + Secret). Postava se adresuje `jmeno-prijmeni` (bez realmu). Nic z toho **není oficiálně zdokumentované** ([zdroj](https://mhuijskens.com/WoWForever/Api/)).

### Postava: `/profile/wow/character/{jmeno-prijmeni}` (namespace `profile-forever-{region}`)

| Endpoint (za cestou postavy) | Co vrací | Knihovna |
|---|---|---|
| *(bez přípony)* | jméno, příjmení, level, zkušenosti, rasa, třída, frakce, pohlaví, guilda, ruleset, item level (průměrný i nasazený), body za úspěchy, poslední přihlášení | ✅ základ odpovědi (včetně `experience`, `gender`, `achievementPoints`, `averageItemLevel`) |
| `/statistics` | atributy, crit, hit, útočná síla, brnění, dodge, parry, block, zdraví, zdroj… (~57 hodnot) | ✅ `stats` |
| `/equipment` | nasazené itemy: slot, id, kvalita, item level, brnění, bonusy, odolnost, požadavky, prodejní cena | ✅ `gear` (slot, id, název, kvalita, ilvl) a **`itemDetails`** (celý záznam) |
| `/professions` | primární a vedlejší profese, skill, maximum, seznam známých receptů | ✅ `professions` (skill a maximum) a **`recipes`** (všechny recepty) |
| `/character-media` | obrázky postavy (`avatar`, `avatar-sd`, `main-raw`, `main-sd-raw`) | ✅ `avatarUrl`, `renderUrl` |
| `/achievements/statistics` | statistiky za celou dobu: smrti, zabití, questy, PvP, dungeony, profese… | ✅ `lifetime` |
| `/achievements` | splněné úspěchy s časem, celkové body, poslední události | ✅ `achievements` |
| `/specializations` | talenty (jméno, rank, popis), utracené body ve stromech | ✅ `specializations` |
| `/reputations` | reputace u frakcí (úroveň, postup) | ✅ `reputations` |
| `/quests/completed` | seznam splněných questů s názvy (u testované postavy 306) | ✅ `quests` |
| `/quests` | rozpracované questy | ✅ `quests` |
| `/appearance` | vzhled postavy (rasa, pohlaví, customizace, erb guildy, itemy) | ✅ `appearance` |
| `/pvp-summary` | čestná zabití, PvP hodnost, statistiky battlegroundů | ✅ `pvp` |
| `/hunter-pets` | peti lovce (jméno, level, druh) | ✅ `hunterPets` (u ne-lovců 404, v knihovně `null`; na fóru psali, že vrací 404 všem, nám vrací data) |
| `/encounters`, `/encounters/dungeons`, `/encounters/raids` | postup dungeony a raidy | ✅ `encounters` (u všech otestovaných postav zatím prázdné) |
| `/collections` + `/mounts`, `/pets`, `/toys`, `/heirlooms`, `/transmogs` | mounty, mazlíčci, hračky, dědictví, transmog (21 slotů) | ✅ `collections` (data mají aktivnější hráči, `heirlooms` zatím prázdné) |
| `/titles`, `/mythic-keystone-profile` | tituly, mythic+ | ✅ `titles`, `mythicKeystone` (odpovídají, ale zatím skoro bez dat) |

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
| `playable-specialization/index`, `/{id}` | specializace (27) | 🔧 `raw` |
| `talent-tree/index` | talentové stromy (27 specializací + 9 tříd) | 🔧 `raw` |
| `item/{id}` | detail itemu | ✅ názvy (a 🔧 `raw` pro celý záznam), ale **část itemů vrací 404** (u testované postavy kolem poloviny hledaných) |
| `media/item/{id}` | **ikona itemu** (`render.worldofwarcraft.com/forever-{region}/icons/…`), funguje i pro itemy, které `item/{id}` nezná | ✅ `icons` (`gear[].iconUrl`) |
| `item-class/index`, `/{id}` | druhy itemů (13) a podtypy | 🔧 `raw` |
| `profession/index`, `/{id}`, `recipe/{id}` | profese (13), recepty, suroviny | ✅ názvy profesí; recepty postavy v `recipes`; detail receptu 🔧 `raw` |
| `achievement/index`, `/{id}`, `achievement-category/index` | úspěchy (176) a jejich definice, kategorie | 🔧 `raw` |
| `reputation-faction/index`, `/{id}` | frakce reputace (56) | 🔧 `raw` |
| `mount/index`, `pet/index`, `title/index` | mounty (141), mazlíčci (110), tituly (41) | 🔧 `raw` |
| `power-type/index`, `creature-family/index`, `spell/{id}` | typy zdroje, rodiny tvorů, kouzla | 🔧 `raw` |
| `search/item?name.en_GB=sword&orderby=id` | vyhledávání itemů podle názvu, stránkované po 100, názvy ve všech jazycích | 🔧 `raw` s `params` (hledání podle `id` nic nevrátilo, na chybějící názvy nepomůže) |

### Dynamická data: `/data/wow/…` (namespace `dynamic-forever-{region}`)

| Endpoint | Co vrací | Knihovna |
|---|---|---|
| `ruleset/index`, `ruleset/{id}` | rulesety místo realmů (v EU 4: 1 PvP, 2 Normal, 3 Roleplay, 15 Roleplay PvP) | ✅ `get_rulesets` |
| `ruleset/{id}/auctions/` | 15 aukčních domů (lomítko na konci je nutné) | ✅ `get_auction_houses` |
| `ruleset/{id}/auctions/{dum}` | všechny nabídky domu (kolem 142 tisíc, 15 MB) | ✅ `get_auctions` (`kind="auctions"`) |
| `ruleset/{id}/auctions/{dum}/commodities` | hromadné zboží (kolem 131 tisíc, 12 MB) | ✅ `get_auctions` (`kind="commodities"`) |

Data aukcí jsou u všech čtyř rulesetů **totožná** (zvláštnost API), ruleset je nefiltruje.
| `realm/index`, `token/index` | **404**: realmy ani cena WoW tokenu ve Forever neexistují | – |

### Co API nenabízí vůbec

V odpovědích jsem nic z toho nenašel, takže to nejde získat ani přes `raw`:
- **Jména ranků v guildě**, guildová banka, zpráva dne, online stav členů. Rank je jen číslo.
- **Historie v čase.** API dává jen aktuální stav. Změny (nové levely, nové smrti) musí aplikace skládat z vlastních snímků.
- **Záznam smrtí** s časem a příčinou. Jsou jen počítadla (`lifetime`).
- **Hledání postav a guild podle části jména** a seznam všech guild. Musí se znát přesné jméno.
- **Názvy v češtině a dalších jazycích** pro Forever (jen angličtina), realmy a cena WoW tokenu (404).

### Nezkoušeno

`/profile/user/wow` (seznam všech postav na účtu) vyžaduje přihlášení hráče přes OAuth (scope `wow.profile`), ne jen Client ID a Secret. Přes `raw` jde zavolat jen s uživatelským tokenem, který knihovna neumí získat.

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
