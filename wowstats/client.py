"""Klient pro neoficialni WoW Forever API (Blizzard). Jen standardni knihovna, zadny bezici server.

Vsechno se predava parametry; cache (token, jmena, vysledky) je na disku, aby opakovana volani
z roznych procesu nestala zbytecne pozadavky.
"""
from __future__ import annotations

import base64
import json
import os
import re
import sys
import time
import urllib.error
import urllib.parse
import urllib.request
from datetime import datetime, timezone
from pathlib import Path

# Vychozi sekce (to, co vraci get_character bez dalsiho nastaveni) a vsechny dostupne sekce.
# Dalsi sekce jsou "na vyzadani": kazda znamena aspon jeden HTTP dotaz navic na postavu.
DEFAULT_SECTIONS = ("stats", "gear", "professions", "media", "lifetime")
ALL_SECTIONS = DEFAULT_SECTIONS + ("achievements", "reputations", "quests", "pvp", "hunterPets", "specializations",
                                   "icons")
_SUBDOC = {"stats": "statistics", "gear": "equipment", "professions": "professions", "media": "character-media",
           "lifetime": "achievements/statistics", "achievements": "achievements", "reputations": "reputations",
           "quests": "quests", "pvp": "pvp-summary", "hunterPets": "hunter-pets",
           "specializations": "specializations"}  # "icons" nema vlastni dokument, doplnuje gear[].iconUrl
# Sekce, kde API vraci nazvy jen anglicky (v jinem jazyce jsou null), proto se vzdy zada en_GB
_EN_SECTIONS = {"lifetime", "achievements", "reputations", "quests", "pvp", "hunterPets", "specializations"}
# Statistiky za celou dobu: id statistiky -> klic ve `lifetime.highlights` (id jsou stabilni, nazvy jen anglicky)
_LIFETIME_KEYS = {
    60: "deaths", 1501: "deathsFromPlayers", 318: "deathsFromOppositeFaction", 114: "deathsFalling",
    112: "deathsDrowning", 115: "deathsFireLava", 321: "deathsDungeonsRaids",
    1197: "totalKills", 588: "honorableKills", 1487: "killingBlows", 98: "questsCompleted",
    94: "questsAbandoned", 64222: "timesCamped", 839: "battlegroundsPlayed", 840: "battlegroundsWon",
    932: "dungeonsEntered", 349: "flightPaths", 353: "timesHearthed", 1456: "fishCaught",
}
_SLUG_RE = re.compile(r"^[\w-]{1,60}$")
_STAT_SKIP = {"_links", "character", "power_type"}
_UA = "wowstats/0.1"
_WOWHEAD_URL = "https://nether.wowhead.com/forever/tooltip/item/{}"  # neoficialni, muze se zmenit
_WOWHEAD_GAP = 0.4        # s mezi dotazy na Wowhead
_NEG_TTL = 7 * 86400      # nenalezeny item se znovu zkusi nejdriv po 7 dnech


class WowStatsError(Exception):
    """Zaklad vsech chyb. `code` odpovida navratovemu kodu CLI."""
    code = 1


class ConfigError(WowStatsError):
    code = 4


class NotFound(WowStatsError):
    code = 2


class CharacterNotFound(NotFound):
    pass


class GuildNotFound(NotFound):
    pass


class UpstreamError(WowStatsError):
    code = 3


def default_cache_dir() -> Path:
    if os.environ.get("WOWSTATS_CACHE_DIR"):
        return Path(os.environ["WOWSTATS_CACHE_DIR"])
    if sys.platform == "win32":
        return Path(os.environ.get("LOCALAPPDATA", Path.home() / "AppData" / "Local")) / "wowstats"
    return Path(os.environ.get("XDG_CACHE_HOME", Path.home() / ".cache")) / "wowstats"


def load_dotenv(path: str | os.PathLike) -> dict[str, str]:
    """Nacte KEY=VALUE soubor (nic nevklada do os.environ)."""
    out: dict[str, str] = {}
    p = Path(path)
    if not p.is_file():
        return out
    for line in p.read_text(encoding="utf-8").splitlines():
        line = line.strip()
        if line and not line.startswith("#") and "=" in line:
            k, v = line.split("=", 1)
            out[k.strip()] = v.strip().strip('"').strip("'")
    return out


def slugify(name: str) -> str:
    return name.strip().lower().replace(" ", "-")


def _pretty(t) -> str:
    return re.sub(r"_+", " ", str(t)).title()


def _num(v):
    if isinstance(v, dict):
        v = v.get("effective", v.get("value", v.get("base")))
    if isinstance(v, bool) or not isinstance(v, (int, float)):
        return None
    return round(v, 2) if isinstance(v, float) else v


def _build_lifetime(doc: dict) -> dict | None:
    """Z `achievements/statistics` udela {"highlights": {...}, "all": [...]}. Chybejici statistika = 0."""
    if "categories" not in doc:
        return None
    rows: list[dict] = []

    def walk(cats, path=""):
        for x in cats:
            p = f"{path}/{x.get('name')}" if path else str(x.get("name"))
            for s in x.get("statistics") or []:
                v = s.get("quantity")
                if isinstance(v, (int, float)) and not isinstance(v, bool):
                    row = {"id": s.get("id"), "category": p, "name": s.get("name"),
                           "value": int(v) if float(v).is_integer() else round(v, 2)}
                    if s.get("description"):
                        row["description"] = s["description"]
                    rows.append(row)
            walk(x.get("sub_categories") or [], p)

    walk(doc.get("categories") or [])
    by_id = {r["id"]: r["value"] for r in rows}
    hl = {key: by_id.get(i, 0) for i, key in _LIFETIME_KEYS.items()}
    hl["deathsInBattlegrounds"] = sum(r["value"] for r in rows if r["category"] == "Deaths/Battlegrounds")
    return {"highlights": hl, "all": rows}


def _iso_ms(ts) -> str | None:
    if isinstance(ts, (int, float)) and not isinstance(ts, bool) and ts:
        return datetime.fromtimestamp(ts / 1000, timezone.utc).isoformat()
    return None


def _build_achievements(doc: dict) -> dict | None:
    """Splnene uspechy s casem splneni (rozpracovane se vynechavaji), serazene podle casu."""
    if "achievements" not in doc and "total_quantity" not in doc:
        return None
    completed = []
    for a in doc.get("achievements") or []:
        at = _iso_ms(a.get("completed_timestamp"))
        if at:
            ach = a.get("achievement") or {}
            completed.append({"id": ach.get("id", a.get("id")), "name": ach.get("name"), "completedAt": at})
    completed.sort(key=lambda x: x["completedAt"])
    recent = [{"id": (e.get("achievement") or {}).get("id"), "name": (e.get("achievement") or {}).get("name"),
               "at": _iso_ms(e.get("timestamp"))} for e in doc.get("recent_events") or []]
    cats = [{"id": (c.get("category") or {}).get("id"), "name": (c.get("category") or {}).get("name"),
             "quantity": c.get("quantity"), "points": c.get("points")} for c in doc.get("category_progress") or []]
    return {"total": doc.get("total_quantity", len(completed)), "points": doc.get("total_points", 0),
            "completed": completed, "recent": recent, "categories": cats}


def _build_reputations(doc: dict) -> list | None:
    """Reputace u frakci, serazene: nejvyssi uroven (tier) prvni, pak nazev."""
    if "reputations" not in doc:
        return None
    out = []
    for r in doc["reputations"] or []:
        f, s = r.get("faction") or {}, r.get("standing") or {}
        out.append({"id": f.get("id"), "name": f.get("name"), "standing": s.get("name"), "tier": s.get("tier"),
                    "value": s.get("value"), "max": s.get("max"), "raw": s.get("raw")})
    out.sort(key=lambda x: (-(x["tier"] if x["tier"] is not None else -1), x["name"] or ""))
    return out


def _build_quests(doc: dict, done: dict) -> dict | None:
    if "in_progress" not in doc and "quests" not in done:
        return None
    simple = lambda xs: [{"id": q.get("id"), "name": q.get("name")} for q in xs or []]
    completed = simple(done["quests"]) if "quests" in done else None  # None = seznam se nepodarilo ziskat
    return {"completedCount": len(completed) if completed is not None else None,
            "completed": completed, "inProgress": simple(doc.get("in_progress"))}


def _build_pvp(doc: dict) -> dict | None:
    if "honorable_kills" not in doc and "pvp_map_statistics" not in doc:
        return None
    bgs = []
    for m in doc.get("pvp_map_statistics") or []:
        wm, ms = m.get("world_map") or {}, m.get("match_statistics") or {}
        bgs.append({"id": wm.get("id"), "name": wm.get("name"), "played": ms.get("played", 0),
                    "won": ms.get("won", 0), "lost": ms.get("lost", 0)})
    return {"honorableKills": doc.get("honorable_kills", 0), "rank": doc.get("pvp_rank", 0), "battlegrounds": bgs}


def _build_hunter_pets(doc: dict) -> list | None:
    if "hunter_pets" not in doc:  # ne-lovci: API vraci 404
        return None
    return [{"name": p.get("name"), "level": p.get("level"), "slot": p.get("slot"),
             "active": bool(p.get("is_active")), "summoned": bool(p.get("is_summoned")),
             "creatureId": (p.get("creature") or {}).get("id"), "creature": (p.get("creature") or {}).get("name")}
            for p in doc["hunter_pets"] or []]


def _build_specializations(doc: dict) -> list | None:
    """[{active, specializations: [{id, name, points, talents: [{id, name, rank, spellId, description}]}]}]"""
    if "specialization_groups" not in doc:
        return None
    groups = []
    for g in doc["specialization_groups"] or []:
        specs = []
        for s in g.get("specializations") or []:
            sp = s.get("specialization") or {}
            talents = []
            for t in s.get("talents") or []:
                tip = t.get("spell_tooltip") or {}
                talents.append({"id": (t.get("talent") or {}).get("id"), "name": (t.get("talent") or {}).get("name"),
                                "rank": t.get("talent_rank"), "spellId": (tip.get("spell") or {}).get("id"),
                                "description": tip.get("description")})
            specs.append({"id": sp.get("id"), "name": sp.get("name") or s.get("specialization_name"),
                          "points": s.get("spent_points", sum(t["rank"] or 0 for t in talents)), "talents": talents})
        groups.append({"active": bool(g.get("is_active")), "specializations": specs})
    return groups


def _http(url: str, token: str | None = None, data: bytes | None = None, basic: str | None = None):
    req = urllib.request.Request(url, data=data, headers={"User-Agent": _UA})
    if token:
        req.add_header("Authorization", f"Bearer {token}")
    if basic:
        req.add_header("Authorization", "Basic " + base64.b64encode(basic.encode()).decode())
    try:
        with urllib.request.urlopen(req, timeout=20) as r:
            return r.status, json.loads(r.read().decode("utf-8"))
    except urllib.error.HTTPError as e:
        return e.code, None
    except Exception as e:  # sit, DNS, timeout
        return -1, str(e)


def _read_json(p: Path):
    try:
        return json.loads(p.read_text(encoding="utf-8"))
    except (OSError, ValueError):
        return None


def _write_json(p: Path, obj) -> None:
    try:
        p.parent.mkdir(parents=True, exist_ok=True)
        tmp = p.with_suffix(p.suffix + ".tmp")
        tmp.write_text(json.dumps(obj, ensure_ascii=False), encoding="utf-8")
        os.replace(tmp, p)
    except OSError:
        pass  # cache je jen optimalizace


class ForeverClient:
    def __init__(self, client_id: str | None = None, client_secret: str | None = None, *,
                 region: str = "eu", locale: str = "en_GB", cache_dir: str | os.PathLike | None = None,
                 ttl: int = 900, use_wowhead: bool = True):
        self.client_id = client_id or ""
        self.client_secret = client_secret or ""
        self.region, self.locale, self.ttl = region, locale, ttl
        self.use_wowhead = use_wowhead
        self.cache_dir = Path(cache_dir) if cache_dir else default_cache_dir()
        self._names: dict | None = None
        self._items: dict | None = None
        self._items_dirty = False
        self._icons: dict | None = None
        self._icons_dirty = False
        self._wh_last = 0.0

    @classmethod
    def from_env(cls, env_file: str | os.PathLike | None = ".env", **overrides) -> "ForeverClient":
        """Poradi: argumenty > promenne prostredi > .env soubor."""
        f = load_dotenv(env_file) if env_file else {}
        get = lambda k, d=None: os.environ.get(k) or f.get(k) or d
        kw = dict(
            client_id=get("BLIZZARD_CLIENT_ID"), client_secret=get("BLIZZARD_CLIENT_SECRET"),
            region=get("BLIZZARD_REGION", "eu"), locale=get("BLIZZARD_LOCALE", "en_GB"),
            ttl=int(get("CACHE_TTL_SECONDS", "900")),
            use_wowhead=str(get("WOWSTATS_WOWHEAD", "1")).lower() not in ("0", "false", "no", "off"),
        )
        kw.update({k: v for k, v in overrides.items() if v is not None})
        return cls(**kw)

    # --- auth (token se cachuje na disk, plati ~24 h) ---
    def _token(self) -> str:
        if not self.client_id or not self.client_secret:
            raise ConfigError("Chybi client id / secret (BLIZZARD_CLIENT_ID, BLIZZARD_CLIENT_SECRET).")
        tf = self.cache_dir / f"token-{self.client_id[:8]}.json"
        c = _read_json(tf)
        if c and c.get("exp", 0) > time.time() + 60:
            return c["token"]
        st, j = _http("https://oauth.battle.net/token",
                      data=b"grant_type=client_credentials", basic=f"{self.client_id}:{self.client_secret}")
        if st != 200 or not isinstance(j, dict):
            raise UpstreamError(f"Ziskani tokenu selhalo (HTTP {st}); zkontroluj client id/secret.")
        _write_json(tf, {"token": j["access_token"], "exp": time.time() + j.get("expires_in", 3600)})
        return j["access_token"]

    def _api(self, path: str, kind: str = "profile", locale: str | None = None):
        url = (f"https://{self.region}.api.blizzard.com{path}"
               f"?namespace={kind}-forever-{self.region}&locale={locale or self.locale}")
        return _http(url, self._token())

    # --- jmena ze static dat (cs_CZ vraci 200 s prazdnym jmenem -> fallback en_GB) ---
    def _name(self, path: str, kind: str = "static"):
        return self._name_ex(path, kind)[0]

    def _name_ex(self, path: str, kind: str = "static"):
        """(jmeno, jisty): jisty=False znamena docasnou chybu, nic se necachuje."""
        if self._names is None:
            self._names = _read_json(self.cache_dir / "names.json") or {}
        if path in self._names:
            return self._names[path], True
        name, definitive = None, False
        for loc in dict.fromkeys([self.locale, "en_GB"]):
            st, body = self._api(path, kind, loc)
            if st == 200 and isinstance(body, dict):
                n = body.get("name")
                if isinstance(n, dict):
                    n = next((v for v in (n.get(loc), *n.values()) if v), None)
                if n:
                    name, definitive = n, True
                    break
            elif st == 404:
                definitive = True
        if definitive:  # docasne chyby site necachuj
            self._names[path] = name
            _write_json(self.cache_dir / "names.json", self._names)
        return name, definitive

    # --- itemy: Blizzard static -> zaloha Wowhead; vysledek se cachuje v items.json ---
    def _item_name(self, item_id: int):
        if self._items is None:
            self._items = _read_json(self.cache_dir / "items.json") or {}
        key = str(int(item_id))
        e = self._items.get(key)
        if e and (e.get("name") or time.time() - e.get("t", 0) < _NEG_TTL):
            return e.get("name")  # zname -> nikdy se neptame znovu; nenalezene az po _NEG_TTL
        name, definitive = self._name_ex(f"/data/wow/item/{key}")
        src = "blizzard"
        if not name and self.use_wowhead:
            src = "wowhead"
            name, wh_definitive = self._wowhead(key)
            definitive = definitive and wh_definitive
        elif not name:
            definitive = False  # Wowhead vypnuty: nic neukladej, az se zapne, zkusi se
        if definitive or name:
            self._items[key] = {"name": name, "t": time.time(), "src": src}
            self._items_dirty = True
        return name

    def _wowhead(self, item_id: str):
        wait = self._wh_last + _WOWHEAD_GAP - time.time()
        if wait > 0:
            time.sleep(wait)
        st, body = _http(_WOWHEAD_URL.format(item_id))
        self._wh_last = time.time()
        if st == 200 and isinstance(body, dict) and body.get("name"):
            return body["name"], True
        return None, st == 404

    def _flush_items(self) -> None:
        if self._items_dirty:
            _write_json(self.cache_dir / "items.json", self._items)
            self._items_dirty = False

    # --- ikony itemu: Blizzard /data/wow/media/item/{id}, trvale cachovano v icons.json ---
    def _item_icon(self, item_id: int):
        if self._icons is None:
            self._icons = _read_json(self.cache_dir / "icons.json") or {}
        key = str(int(item_id))
        e = self._icons.get(key)
        if e and (e.get("url") or time.time() - e.get("t", 0) < _NEG_TTL):
            return e.get("url")  # zname se nikdy nepreptava; nenalezene az po _NEG_TTL
        st, body = self._api(f"/data/wow/media/item/{key}", "static", "en_GB")
        url = None
        if st == 200 and isinstance(body, dict):
            url = next((a.get("value") for a in body.get("assets") or [] if a.get("key") == "icon"), None)
        if url or st == 404:  # docasne chyby se necachuji
            self._icons[key] = {"url": url, "t": time.time()}
            self._icons_dirty = True
        return url

    def _flush_icons(self) -> None:
        if self._icons_dirty:
            _write_json(self.cache_dir / "icons.json", self._icons)
            self._icons_dirty = False

    # --- verejne API ---
    def get_character(self, name: str, *, sections=DEFAULT_SECTIONS, ttl: int | None = None,
                      refresh: bool = False) -> dict:
        """Vrati dict s postavou. `name` je slug nebo 'Jmeno Prijmeni'.

        `sections`: seznam, retezec oddeleny carkami, "default" nebo "all". Kazda sekce je aspon jeden HTTP dotaz.
        Sekce `icons` doplni `gear[].iconUrl` (a proto automaticky zahrne `gear`)."""
        slug = slugify(name)
        if not _SLUG_RE.match(slug):
            raise CharacterNotFound(f"Neplatne jmeno postavy: {name!r}")
        if isinstance(sections, str):
            sections = (ALL_SECTIONS if sections == "all" else DEFAULT_SECTIONS if sections == "default"
                        else [x.strip() for x in sections.split(",") if x.strip()])
        want = set(sections)
        bad = want - set(ALL_SECTIONS)
        if bad:
            raise ConfigError(f"Neznama sekce: {', '.join(sorted(bad))} (platne: {', '.join(ALL_SECTIONS)})")
        if "icons" in want:
            want.add("gear")
        secs = tuple(s for s in ALL_SECTIONS if s in want)
        cf = self.cache_dir / f"char-{self.region}-{slug}-{'+'.join(secs) or 'summary'}.json"
        return self._with_cache(cf, ttl, refresh, lambda: self._fetch(slug, secs))

    def get_guild(self, name: str, *, ttl: int | None = None, refresh: bool = False) -> dict:
        """Vrati dict s guildou a seznamem clenu (serazeno: rank, level sestupne, jmeno).
        `name` je slug nebo nazev guildy, napr. 'Nazev Guildy'."""
        slug = slugify(name)
        if not _SLUG_RE.match(slug):
            raise GuildNotFound(f"Neplatny nazev guildy: {name!r}")
        cf = self.cache_dir / f"guild-{self.region}-{slug}.json"
        return self._with_cache(cf, ttl, refresh, lambda: self._fetch_guild(slug))

    def _with_cache(self, cf: Path, ttl: int | None, refresh: bool, fetch) -> dict:
        ttl = self.ttl if ttl is None else ttl
        cached = _read_json(cf)
        if cached and not refresh and time.time() - cached["_ts"] < ttl:
            return cached["data"]
        try:
            data = fetch()
        except UpstreamError:
            if cached:  # radsi stara data nez chyba
                return {**cached["data"], "stale": True}
            raise
        _write_json(cf, {"_ts": time.time(), "data": data})
        return data

    def _fetch_guild(self, slug: str) -> dict:
        base = f"/data/wow/guild/{urllib.parse.quote(slug)}"
        st, g = self._api(base)
        if st == 404:
            raise GuildNotFound(f"Guilda nenalezena: {slug}")
        if st != 200 or not isinstance(g, dict):
            raise UpstreamError(f"Guilda: HTTP {st}")
        st, r = self._api(f"{base}/roster")
        if st != 200 or not isinstance(r, dict):
            raise UpstreamError(f"Roster: HTTP {st}")

        members = []
        for x in r.get("members", []):
            ch = x.get("character") or {}
            cid, rid = (ch.get("playable_class") or {}).get("id"), (ch.get("playable_race") or {}).get("id")
            members.append({
                "name": ch.get("name"), "surname": ch.get("surname"), "slug": ch.get("slug"),
                "level": ch.get("level"),
                "characterClass": self._name(f"/data/wow/playable-class/{cid}") if cid is not None else None,
                "race": self._name(f"/data/wow/playable-race/{rid}") if rid is not None else None,
                "faction": ((ch.get("faction") or {}).get("type") or "").title() or None,
                "rank": x.get("rank"),
            })
        members.sort(key=lambda m: (99 if m["rank"] is None else m["rank"], -(m["level"] or 0), m["name"] or ""))

        created = g.get("created_timestamp")
        return {
            "name": g.get("name") or slug, "slug": slug, "id": g.get("id"),
            "faction": ((g.get("faction") or {}).get("type") or "").title() or None,
            "memberCount": g.get("member_count", len(members)),
            "achievementPoints": g.get("achievement_points"),
            "createdAt": datetime.fromtimestamp(created / 1000, timezone.utc).isoformat() if created else None,
            "ruleset": self._name(f"/data/wow/ruleset/{g['ruleset']['id']}", "dynamic") if g.get("ruleset") else None,
            "members": members,
            "fetchedAt": datetime.now(timezone.utc).isoformat(), "stale": False,
        }

    def _fetch(self, slug: str, secs: tuple[str, ...]) -> dict:
        base = f"/profile/wow/character/{urllib.parse.quote(slug)}"
        st, p = self._api(base)
        if st == 404:
            raise CharacterNotFound(f"Postava nenalezena: {slug}")
        if st != 200 or not isinstance(p, dict):
            raise UpstreamError(f"Profil: HTTP {st}")
        sub = {}
        for s in secs:
            if s not in _SUBDOC:  # "icons" nema vlastni dokument
                continue
            # u techto sekci vraci API nazvy jen anglicky, jinak jsou null
            st2, b = self._api(f"{base}/{_SUBDOC[s]}", locale="en_GB" if s in _EN_SECTIONS else None)
            sub[s] = b if st2 == 200 and isinstance(b, dict) else {}
        if "quests" in sub:
            st3, done = self._api(f"{base}/quests/completed", locale="en_GB")
            sub["quests_completed"] = done if st3 == 200 and isinstance(done, dict) else {}

        login = p.get("last_login_timestamp")
        out = {
            "name": p["name"], "surname": p.get("surname"), "slug": slug, "level": p["level"],
            "race": self._name(f"/data/wow/playable-race/{p['race']['id']}"),
            "characterClass": self._name(f"/data/wow/playable-class/{p['character_class']['id']}"),
            "faction": p["faction"]["type"].title(),
            "guild": (p.get("guild") or {}).get("name"),
            "ruleset": self._name(f"/data/wow/ruleset/{p['ruleset']['id']}", "dynamic") if p.get("ruleset") else None,
            "itemLevel": p.get("equipped_item_level"),
            "lastLogin": datetime.fromtimestamp(login / 1000, timezone.utc).isoformat() if login else None,
        }
        if "stats" in sub:
            out["stats"] = {k: n for k, v in sub["stats"].items()
                            if k not in _STAT_SKIP and (n := _num(v)) is not None}
        if "gear" in sub:
            out["gear"] = [{
                "slot": _pretty(it["slot"]["type"]), "itemId": it["item"]["id"],
                "name": it.get("name") or self._item_name(it["item"]["id"]),
                "quality": it["quality"]["type"].title(),
                "itemLevel": (it.get("level") or {}).get("value"),
            } for it in sub["gear"].get("equipped_items", [])]
            if "icons" in secs:
                for g in out["gear"]:
                    g["iconUrl"] = self._item_icon(g["itemId"])
                self._flush_icons()
            self._flush_items()
        if "professions" in sub:
            out["professions"] = []
            for grp in ("primaries", "secondaries"):
                for pr in sub["professions"].get(grp, []):
                    tiers = pr.get("tiers", [])
                    out["professions"].append({
                        "name": pr["profession"].get("name") or self._name(f"/data/wow/profession/{pr['profession']['id']}"),
                        "primary": grp == "primaries",
                        "skill": sum(t.get("skill_points", 0) for t in tiers),
                        "maxSkill": sum(t.get("max_skill_points", 0) for t in tiers),
                    })
        if "lifetime" in sub:
            out["lifetime"] = _build_lifetime(sub["lifetime"])
        if "achievements" in sub:
            out["achievements"] = _build_achievements(sub["achievements"])
        if "reputations" in sub:
            out["reputations"] = _build_reputations(sub["reputations"])
        if "quests" in sub:
            out["quests"] = _build_quests(sub["quests"], sub["quests_completed"])
        if "pvp" in sub:
            out["pvp"] = _build_pvp(sub["pvp"])
        if "hunterPets" in sub:
            out["hunterPets"] = _build_hunter_pets(sub["hunterPets"])
        if "specializations" in sub:
            out["specializations"] = _build_specializations(sub["specializations"])
        if "media" in sub:
            a = {x["key"]: x["value"] for x in sub["media"].get("assets", [])}
            out["avatarUrl"], out["renderUrl"] = a.get("avatar"), a.get("main-sd-raw")
        out["fetchedAt"] = datetime.now(timezone.utc).isoformat()
        out["stale"] = False
        return out
