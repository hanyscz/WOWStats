"""Offline testy: HTTP je nahrazeno atrapou, nic se neposila na Blizzard ani Wowhead."""
import contextlib
import io
import json
import os
import tempfile
import time
import unittest
from pathlib import Path
from unittest import mock

import wowstats.__main__ as cli
import wowstats.client as m
from wowstats import CharacterNotFound, ConfigError, ForeverClient, GuildNotFound, NotFound

PROFILE = {
    "name": "Test", "surname": "Char", "level": 35, "race": {"id": 2}, "character_class": {"id": 3},
    "faction": {"type": "HORDE"}, "guild": {"name": "Guild"}, "ruleset": {"id": 2},
    "equipped_item_level": 26, "last_login_timestamp": 1700000000000,
}
EQUIPMENT = {"equipped_items": [
    {"item": {"id": 1}, "slot": {"type": "HEAD"}, "quality": {"type": "RARE"}, "level": {"value": 30}, "name": None},
    {"item": {"id": 2}, "slot": {"type": "FINGER_1"}, "quality": {"type": "UNCOMMON"}, "level": {"value": 20}, "name": None},
]}
STATS = {"_links": {}, "character": {}, "attack_power": 262, "strength": {"base": 40, "effective": 47},
         "melee_crit": {"value": 3.5904002}}
PROFESSIONS = {"primaries": [{"profession": {"id": 165, "name": "Leatherworking"},
                              "tiers": [{"skill_points": 120, "max_skill_points": 150}]}], "secondaries": []}
LIFETIME = {"categories": [
    {"name": "Deaths", "statistics": [{"id": 60, "name": "Total deaths", "quantity": 33.0}], "sub_categories": [
        {"name": "World", "statistics": [{"id": 114, "name": "Deaths from falling", "quantity": 1.0}]},
        {"name": "Battlegrounds", "statistics": [{"id": 56, "name": "Deaths in WSG", "quantity": 2.0},
                                                 {"id": 59, "name": "Deaths in AB", "quantity": 3.0}]}]},
    {"name": "Kills", "statistics": [{"id": 1197, "name": "Total kills", "quantity": 3838.0},
                                     {"id": 1336, "name": "Creature type killed the most", "quantity": 20.5,
                                      "description": "Beasts"}]},
]}
MEDIA = {"assets": [{"key": "avatar", "value": "https://example.test/a.jpg"},
                    {"key": "main-sd-raw", "value": "https://example.test/m.png"}]}
GUILD = {"id": 7, "name": "Test Guild", "faction": {"type": "HORDE"}, "member_count": 3,
         "achievement_points": 0, "created_timestamp": 1700000000000, "ruleset": {"id": 2}}


def _member(name, level, rank):
    return {"character": {"name": name, "surname": "X", "slug": f"{name.lower()}-x", "level": level,
                          "playable_class": {"id": 2}, "playable_race": {"id": 5}, "faction": {"type": "HORDE"}},
            "rank": rank}


ROSTER = {"members": [_member("B", 10, 1), _member("A", 30, 0), _member("C", 20, 1)]}


class Fake:
    """Atrapa m._http. items = Blizzard static (id -> nazev), wowhead = id -> nazev | None (404) | 'err'."""

    def __init__(self, items=None, wowhead=None, profile_status=200, guild_status=200):
        self.items, self.wowhead, self.profile_status = items or {}, wowhead or {}, profile_status
        self.guild_status = guild_status
        self.lifetime_ok = True
        self.calls = []

    def count(self, part):
        return sum(1 for c in self.calls if part in c)

    def __call__(self, url, token=None, data=None, basic=None):
        self.calls.append(url)
        if "oauth.battle.net" in url:
            return 200, {"access_token": "t", "expires_in": 86400}
        if "wowhead.com" in url:
            v = self.wowhead.get(int(url.rsplit("/", 1)[1]))
            if v == "err":
                return -1, "boom"
            return (200, {"name": v}) if v else (404, None)
        path = url.split("blizzard.com", 1)[1].split("?")[0]
        static = {"/data/wow/playable-race/2": "Orc", "/data/wow/playable-class/3": "Hunter",
                  "/data/wow/ruleset/2": "Normal", "/data/wow/playable-class/2": "Paladin",
                  "/data/wow/playable-race/5": "Undead"}
        if path in static:
            return 200, {"name": static[path]}
        if path.startswith("/data/wow/item/"):
            n = self.items.get(int(path.rsplit("/", 1)[1]))
            return (200, {"name": n}) if n else (404, None)
        if path == "/data/wow/guild/test-guild":
            return (200, GUILD) if self.guild_status == 200 else (self.guild_status, None)
        if path == "/data/wow/guild/test-guild/roster":
            return 200, ROSTER
        base = "/profile/wow/character/test-char"
        if path == base:
            return (200, PROFILE) if self.profile_status == 200 else (self.profile_status, None)
        sub = {"/equipment": EQUIPMENT, "/statistics": STATS, "/professions": PROFESSIONS,
               "/character-media": MEDIA, "/achievements/statistics": LIFETIME}
        if path.endswith("/achievements/statistics") and not self.lifetime_ok:
            return 500, None
        if path.startswith(base) and path[len(base):] in sub:
            return 200, sub[path[len(base):]]
        return 404, None


class ClientTests(unittest.TestCase):
    def setUp(self):
        self._tmp = tempfile.TemporaryDirectory()
        self.tmp = Path(self._tmp.name)
        self._orig = (m._http, m._WOWHEAD_GAP)
        m._WOWHEAD_GAP = 0
        self.fake = Fake(items={1: "Helm"}, wowhead={2: "Ring"})
        m._http = self.fake

    def tearDown(self):
        m._http, m._WOWHEAD_GAP = self._orig
        self._tmp.cleanup()

    def client(self, **kw):  # novy objekt = novy proces (sdili jen disk)
        return ForeverClient("id123456789", "secret", cache_dir=self.tmp, **kw)

    def items_cache(self):
        return json.loads((self.tmp / "items.json").read_text(encoding="utf-8"))

    # --- obsah ---
    def test_builds_character(self):
        c = self.client().get_character("Test Char")
        self.assertEqual((c["name"], c["race"], c["characterClass"], c["ruleset"]),
                         ("Test", "Orc", "Hunter", "Normal"))
        self.assertEqual([g["name"] for g in c["gear"]], ["Helm", "Ring"])
        self.assertEqual(c["gear"][1]["slot"], "Finger 1")
        self.assertEqual(c["stats"], {"attack_power": 262, "strength": 47, "melee_crit": 3.59})
        self.assertEqual(c["professions"], [{"name": "Leatherworking", "primary": True,
                                             "skill": 120, "maxSkill": 150}])
        self.assertEqual(c["avatarUrl"], "https://example.test/a.jpg")
        self.assertFalse(c["stale"])

    def test_lifetime_statistics(self):
        c = self.client(locale="cs_CZ").get_character("test-char", sections=["lifetime"])
        hl = c["lifetime"]["highlights"]
        self.assertEqual((hl["deaths"], hl["deathsFalling"], hl["totalKills"]), (33, 1, 3838))
        self.assertEqual(hl["deathsInBattlegrounds"], 5)   # soucet kategorie Deaths/Battlegrounds
        self.assertEqual(hl["deathsDrowning"], 0)          # chybejici statistika = 0
        row = next(r for r in c["lifetime"]["all"] if r["id"] == 114)
        self.assertEqual((row["category"], row["name"], row["value"]), ("Deaths/World", "Deaths from falling", 1))
        self.assertEqual(next(r for r in c["lifetime"]["all"] if r["id"] == 1336)["description"], "Beasts")
        # nazvy statistik jsou jen anglicky, i kdyz klient pouziva cs_CZ
        call = next(u for u in self.fake.calls if "achievements/statistics" in u)
        self.assertIn("locale=en_GB", call)
        self.assertNotIn("stats", c)

    def test_lifetime_missing_when_api_fails(self):
        self.fake.lifetime_ok = False
        self.assertIsNone(self.client().get_character("test-char", sections=["lifetime"])["lifetime"])

    def test_sections_limit_requests(self):
        c = self.client().get_character("test-char", sections=["stats"])
        self.assertIn("stats", c)
        self.assertNotIn("gear", c)
        self.assertEqual(self.fake.count("/equipment"), 0)

    # --- itemy a cache ---
    def test_known_item_never_asked_again(self):
        self.client().get_character("test-char")
        self.assertEqual(self.fake.count("wowhead.com"), 1)  # jen item 2; item 1 zna Blizzard
        n_static = self.fake.count("/data/wow/item/")
        c = self.client().get_character("test-char", refresh=True)  # novy "proces"
        self.assertEqual(c["gear"][1]["name"], "Ring")
        self.assertEqual(self.fake.count("wowhead.com"), 1)
        self.assertEqual(self.fake.count("/data/wow/item/"), n_static)

    def test_negative_cache_and_expiry(self):
        self.fake.wowhead = {}
        c = self.client().get_character("test-char")
        self.assertIsNone(c["gear"][1]["name"])
        self.client().get_character("test-char", refresh=True)
        self.assertEqual(self.fake.count("wowhead.com"), 1)  # nenalezeny item se neptame znovu
        cache = self.items_cache()
        cache["2"]["t"] = time.time() - 8 * 86400
        (self.tmp / "items.json").write_text(json.dumps(cache), encoding="utf-8")
        self.client().get_character("test-char", refresh=True)
        self.assertEqual(self.fake.count("wowhead.com"), 2)  # po 7 dnech ano

    def test_network_error_is_not_cached(self):
        self.fake.wowhead = {2: "err"}
        self.assertIsNone(self.client().get_character("test-char")["gear"][1]["name"])
        self.assertNotIn("2", self.items_cache())
        self.client().get_character("test-char", refresh=True)
        self.assertEqual(self.fake.count("wowhead.com"), 2)

    def test_wowhead_disabled(self):
        c = self.client(use_wowhead=False).get_character("test-char")
        self.assertIsNone(c["gear"][1]["name"])
        self.assertEqual(self.fake.count("wowhead.com"), 0)
        self.assertNotIn("2", self.items_cache())

    # --- vysledky postav ---
    def test_ttl_and_stale_fallback(self):
        cl = self.client()
        cl.get_character("test-char")
        cl.get_character("test-char")
        self.assertEqual(self.fake.count("/profile/wow/character/test-char?"), 1)  # z cache
        self.fake.profile_status = 500
        c = self.client().get_character("test-char", ttl=0)
        self.assertTrue(c["stale"])

    # --- chyby ---
    def test_not_found(self):
        self.fake.profile_status = 404
        with self.assertRaises(CharacterNotFound):
            self.client().get_character("test-char")

    def test_invalid_name_and_section(self):
        with self.assertRaises(CharacterNotFound):
            self.client().get_character("../etc/passwd")
        with self.assertRaises(ConfigError):
            self.client().get_character("test-char", sections=["foo"])

    def test_missing_credentials(self):
        with self.assertRaises(ConfigError):
            ForeverClient(cache_dir=self.tmp).get_character("test-char")

    # --- guilda ---
    def test_guild_builds_and_sorts_members(self):
        g = self.client().get_guild("Test Guild")
        self.assertEqual((g["name"], g["slug"], g["id"], g["faction"], g["ruleset"]),
                         ("Test Guild", "test-guild", 7, "Horde", "Normal"))
        self.assertEqual((g["memberCount"], g["achievementPoints"]), (3, 0))
        self.assertTrue(g["createdAt"].startswith("2023-11-14"))
        # rank 0 prvni, uvnitr ranku level sestupne
        self.assertEqual([x["name"] for x in g["members"]], ["A", "C", "B"])
        self.assertEqual(g["members"][0], {"name": "A", "surname": "X", "slug": "a-x", "level": 30,
                                           "characterClass": "Paladin", "race": "Undead",
                                           "faction": "Horde", "rank": 0})
        self.assertFalse(g["stale"])

    def test_guild_is_cached_on_disk(self):
        self.client().get_guild("test-guild")
        n = self.fake.count("/data/wow/guild/")
        self.client().get_guild("test-guild")  # novy "proces"
        self.assertEqual(self.fake.count("/data/wow/guild/"), n)
        self.client().get_guild("test-guild", refresh=True)
        self.assertEqual(self.fake.count("/data/wow/guild/"), 2 * n)

    def test_guild_not_found_and_invalid(self):
        self.fake.guild_status = 404
        with self.assertRaises(GuildNotFound) as cm:
            self.client().get_guild("test-guild")
        self.assertIsInstance(cm.exception, NotFound)
        self.assertEqual(cm.exception.code, 2)
        with self.assertRaises(GuildNotFound):
            self.client().get_guild("../x")

    def test_guild_stale_fallback(self):
        self.client().get_guild("test-guild")
        self.fake.guild_status = 500
        self.assertTrue(self.client().get_guild("test-guild", ttl=0)["stale"])

    def test_cli_guild_and_errors(self):
        args = ["--env-file", "nonexist", "--client-id", "id123456789", "--client-secret", "s"]
        with mock.patch.dict(os.environ, {"WOWSTATS_CACHE_DIR": str(self.tmp)}):
            out = io.StringIO()
            with contextlib.redirect_stdout(out):
                self.assertEqual(cli.main(["guild", "Test Guild", *args]), 0)
            self.assertEqual(json.loads(out.getvalue())["members"][0]["name"], "A")
            self.fake.guild_status = 404
            err = io.StringIO()
            with contextlib.redirect_stderr(err):
                self.assertEqual(cli.main(["guild", "test-guild", "--refresh", *args]), 2)
            self.assertEqual(json.loads(err.getvalue())["code"], 2)


class HelperTests(unittest.TestCase):
    def test_slugify_and_num(self):
        self.assertEqual(m.slugify("  Jmeno Prijmeni "), "jmeno-prijmeni")
        self.assertEqual(m._num({"base": 1, "effective": 2}), 2)
        self.assertEqual(m._num({"value": 1.23456}), 1.23)
        self.assertIsNone(m._num("x"))

    def test_load_dotenv(self):
        with tempfile.TemporaryDirectory() as d:
            p = Path(d) / ".env"
            p.write_text('# komentar\nA=1\nB="two"\n\nC = three \n', encoding="utf-8")
            self.assertEqual(m.load_dotenv(p), {"A": "1", "B": "two", "C": "three"})
            self.assertEqual(m.load_dotenv(Path(d) / "nope"), {})


if __name__ == "__main__":
    unittest.main()
