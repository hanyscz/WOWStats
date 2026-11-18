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

ALL_SECTIONS = ("stats", "gear", "professions", "media")
_SUBDOC = {"stats": "statistics", "gear": "equipment", "professions": "professions", "media": "character-media"}
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


class CharacterNotFound(WowStatsError):
    code = 2


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

    # --- verejne API ---
    def get_character(self, name: str, *, sections=ALL_SECTIONS, ttl: int | None = None,
                      refresh: bool = False) -> dict:
        """Vrati dict s postavou. `name` je slug nebo 'Jmeno Prijmeni'. `sections` omezi pocet dotazu."""
        slug = slugify(name)
        if not _SLUG_RE.match(slug):
            raise CharacterNotFound(f"Neplatne jmeno postavy: {name!r}")
        secs = tuple(s for s in ALL_SECTIONS if s in set(sections))
        bad = set(sections) - set(ALL_SECTIONS)
        if bad:
            raise ConfigError(f"Neznama sekce: {', '.join(sorted(bad))} (platne: {', '.join(ALL_SECTIONS)})")
        ttl = self.ttl if ttl is None else ttl
        cf = self.cache_dir / f"char-{self.region}-{slug}-{'+'.join(secs) or 'summary'}.json"
        cached = _read_json(cf)
        if cached and not refresh and time.time() - cached["_ts"] < ttl:
            return cached["data"]
        try:
            data = self._fetch(slug, secs)
        except UpstreamError:
            if cached:  # radsi stara data nez chyba
                return {**cached["data"], "stale": True}
            raise
        _write_json(cf, {"_ts": time.time(), "data": data})
        return data

    def _fetch(self, slug: str, secs: tuple[str, ...]) -> dict:
        base = f"/profile/wow/character/{urllib.parse.quote(slug)}"
        st, p = self._api(base)
        if st == 404:
            raise CharacterNotFound(f"Postava nenalezena: {slug}")
        if st != 200 or not isinstance(p, dict):
            raise UpstreamError(f"Profil: HTTP {st}")
        sub = {}
        for s in secs:
            st2, b = self._api(f"{base}/{_SUBDOC[s]}")
            sub[s] = b if st2 == 200 and isinstance(b, dict) else {}

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
        if "media" in sub:
            a = {x["key"]: x["value"] for x in sub["media"].get("assets", [])}
            out["avatarUrl"], out["renderUrl"] = a.get("avatar"), a.get("main-sd-raw")
        out["fetchedAt"] = datetime.now(timezone.utc).isoformat()
        out["stale"] = False
        return out
