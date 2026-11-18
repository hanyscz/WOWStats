"""Offline testy: HTTP je nahrazeno atrapou, nic se neposila na Blizzard ani Wowhead."""
import json
import tempfile
import time
import unittest
from pathlib import Path

import wowstats.client as m
from wowstats import CharacterNotFound, ConfigError, ForeverClient

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
MEDIA = {"assets": [{"key": "avatar", "value": "https://example.test/a.jpg"},
                    {"key": "main-sd-raw", "value": "https://example.test/m.png"}]}


class Fake:
    """Atrapa m._http. items = Blizzard static (id -> nazev), wowhead = id -> nazev | None (404) | 'err'."""

    def __init__(self, items=None, wowhead=None, profile_status=200):
        self.items, self.wowhead, self.profile_status = items or {}, wowhead or {}, profile_status
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
                  "/data/wow/ruleset/2": "Normal"}
        if path in static:
            return 200, {"name": static[path]}
        if path.startswith("/data/wow/item/"):
            n = self.items.get(int(path.rsplit("/", 1)[1]))
            return (200, {"name": n}) if n else (404, None)
        base = "/profile/wow/character/test-char"
        if path == base:
            return (200, PROFILE) if self.profile_status == 200 else (self.profile_status, None)
        sub = {"/equipment": EQUIPMENT, "/statistics": STATS, "/professions": PROFESSIONS,
               "/character-media": MEDIA}
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
