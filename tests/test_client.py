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
    "equipped_item_level": 26, "last_login_timestamp": 1700000000000, "experience": 60503,
    "gender": {"type": "MALE", "name": "Male"}, "achievement_points": 10, "average_item_level": 25,
}
EQUIPMENT = {"equipped_items": [
    {"item": {"id": 1, "key": {"href": "http://x/item/1"}}, "slot": {"type": "HEAD"}, "quality": {"type": "RARE"},
     "level": {"value": 30}, "name": None, "media": {"key": {"href": "http://x/media/1"}, "id": 1},
     "binding": {"type": "ON_ACQUIRE", "name": "Binds when picked up"},
     "stats": [{"type": {"type": "AGILITY", "name": "Agility"}, "value": 3, "is_hidden": False,
                "display": {"display_string": "+3 Agility"}}]},
    {"item": {"id": 2}, "slot": {"type": "FINGER_1"}, "quality": {"type": "UNCOMMON"}, "level": {"value": 20}, "name": None},
]}
STATS = {"_links": {}, "character": {}, "attack_power": 262, "strength": {"base": 40, "effective": 47},
         "melee_crit": {"value": 3.5904002}}
PROFESSIONS = {"primaries": [{"profession": {"id": 165, "name": "Leatherworking"},
                              "tiers": [{"tier": {"id": 2945, "name": "Leatherworking"}, "skill_points": 120,
                                         "max_skill_points": 150,
                                         "known_recipes": [{"key": {"href": "x"}, "id": 1353, "name": "Boots"},
                                                           {"id": 4419, "name": "Belt"}]}]}], "secondaries": []}
COLL_MOUNTS = {"_links": {}, "mounts": [{"mount": {"key": {"href": "x"}, "name": "Horse", "id": 3111}, "is_useable": True}]}
COLL_PETS = {"pets": [{"species": {"name": "Zergling", "id": 5160}, "level": 1,
                       "quality": {"type": "UNCOMMON", "name": "Uncommon"}, "stats": {"breed_id": 0, "health": 100},
                       "creature_display": {"id": 9}, "id": 77}], "unlocked_battle_pet_slots": 0}
COLL_TOYS = {"toys": [{"toy": {"name": "Windwell", "id": 1761}}]}
COLL_HEIRLOOMS = {"_links": {}}
COLL_TRANSMOGS = {"slots": [{"slot": {"type": "HEAD", "name": "Head"},
                             "appearances": [{"key": {"href": "x"}, "id": 29124}, {"id": 64412}]}]}
APPEARANCE = {"_links": {}, "character": {"name": "X"}, "playable_race": {"key": {"href": "x"}, "name": "Orc", "id": 2},
              "customizations": [{"option": {"name": "Skin Color", "id": 63}, "choice": {"id": 984, "display_order": 0}}]}
TITLES = {"_links": {}, "character": {"name": "X"}}
MYTHIC = {"current_period": {"period": {"key": {"href": "x"}, "id": 1090}}, "character": {"name": "X"}}
ENC_DUNGEONS = {"_links": {}}
ENC_RAIDS = {"_links": {}, "character": {"name": "X"}, "some_field": {"inner_value": 1}}
AUCTIONS = [
    {"id": 1, "item": {"id": 10}, "buyout": 100, "quantity": 1, "time_left": "LONG"},
    {"id": 2, "item": {"id": 11, "context": 1, "modifiers": [{"type": 28, "value": 5}], "bonus_lists": [1, 2]},
     "buyout": 200, "bid": 150, "quantity": 2, "time_left": "SHORT"},
    {"id": 3, "item": {"id": 10}, "buyout": 90, "quantity": 5, "time_left": "MEDIUM"},
]
COMMODITIES = [{"id": 5, "item": {"id": 20}, "quantity": 17, "unit_price": 20, "time_left": "LONG"}]
LIFETIME = {"categories": [
    {"name": "Deaths", "statistics": [{"id": 60, "name": "Total deaths", "quantity": 33.0}], "sub_categories": [
        {"name": "World", "statistics": [{"id": 114, "name": "Deaths from falling", "quantity": 1.0}]},
        {"name": "Battlegrounds", "statistics": [{"id": 56, "name": "Deaths in WSG", "quantity": 2.0},
                                                 {"id": 59, "name": "Deaths in AB", "quantity": 3.0}]}]},
    {"name": "Kills", "statistics": [{"id": 1197, "name": "Total kills", "quantity": 3838.0},
                                     {"id": 1336, "name": "Creature type killed the most", "quantity": 20.5,
                                      "description": "Beasts"}]},
]}
ACHIEVEMENTS = {
    "total_quantity": 2, "total_points": 10,
    "achievements": [
        {"id": 2, "achievement": {"id": 2, "name": "Second"}, "completed_timestamp": 1700000200000},
        {"id": 1, "achievement": {"id": 1, "name": "First"}, "completed_timestamp": 1700000100000},
        {"id": 3, "achievement": {"id": 3, "name": "In progress"}, "criteria": {"id": 9, "is_completed": False}},
    ],
    "recent_events": [{"achievement": {"id": 2, "name": "Second"}, "timestamp": 1700000200000}],
    "category_progress": [{"category": {"id": 7, "name": "Adventure"}, "quantity": 2, "points": 10}],
}
REPUTATIONS = {"reputations": [
    {"faction": {"id": 1, "name": "Zeta"}, "standing": {"raw": 100, "value": 100, "max": 3000, "tier": 3, "name": "Neutral"}},
    {"faction": {"id": 2, "name": "Alpha"}, "standing": {"raw": 20000, "value": 8000, "max": 12000, "tier": 5, "name": "Honored"}},
    {"faction": {"id": 3, "name": "Beta"}, "standing": {"raw": 0, "value": 0, "max": 3000, "tier": 3, "name": "Neutral"}},
]}
QUESTS_IN = {"in_progress": [{"id": 5, "name": "Open quest"}], "completed": {"href": "x"}}
QUESTS_DONE = {"quests": [{"id": 1, "name": "Done A"}, {"id": 2, "name": "Done B"}]}
PVP = {"honorable_kills": 834, "pvp_rank": 1, "pvp_map_statistics": [
    {"world_map": {"id": 489, "name": "Warsong Gulch"}, "match_statistics": {"played": 11, "won": 3, "lost": 8}}]}
PETS = {"hunter_pets": [{"name": "Rex", "level": 35, "slot": 0, "is_active": True, "is_summoned": False,
                         "creature": {"id": 3241, "name": "Savannah Patriarch"}}]}
SPECS = {"specialization_groups": [{"is_active": True, "specializations": [{
    "specialization": {"id": 50, "name": "Beast Mastery"}, "spent_points": 26,
    "talents": [{"talent": {"id": 10, "name": "Deadly Aspects"}, "talent_rank": 5,
                 "spell_tooltip": {"spell": {"id": 19552, "name": "Deadly Aspects"}, "description": "Passive"}}]}]}]}
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
        self.hunter_ok = True
        self.fail = set()                                 # koncovky cest, u kterych se simuluje HTTP 500
        self.icons = {1: "https://example.test/i1.jpg"}  # id itemu -> url ikony (ostatni = 404)
        self.icon_errors = set()                          # id itemu, u kterych se simuluje sitova chyba
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
        if any(path.endswith(s) for s in self.fail):
            return 500, None
        static = {"/data/wow/playable-race/2": "Orc", "/data/wow/playable-class/3": "Hunter",
                  "/data/wow/ruleset/2": "Normal", "/data/wow/ruleset/1": "PvP", "/data/wow/playable-class/2": "Paladin",
                  "/data/wow/playable-race/5": "Undead"}
        if path in static:
            return 200, {"name": static[path]}
        if path.startswith("/data/wow/media/item/"):
            iid = int(path.rsplit("/", 1)[1])
            if iid in self.icon_errors:
                return -1, "boom"
            url = self.icons.get(iid)
            return (200, {"assets": [{"key": "icon", "value": url}]}) if url else (404, None)
        if path.startswith("/data/wow/item/"):
            n = self.items.get(int(path.rsplit("/", 1)[1]))
            return (200, {"name": n}) if n else (404, None)
        if path == "/data/wow/ruleset/index":
            return 200, {"rulesets": [{"id": 2}, {"id": 1}]}
        if path == "/data/wow/ruleset/2/auctions/":
            return 200, {"auctions": [{"key": {"href": "x"}, "name": "Alliance Auction House", "id": 110},
                                      {"name": "Horde Auction House", "id": 113}]}
        if path == "/data/wow/ruleset/2/auctions/110":
            return 200, {"id": 110, "name": "Alliance Auction House", "auctions": AUCTIONS, "commodities": {"href": "x"}}
        if path == "/data/wow/ruleset/2/auctions/110/commodities":
            return 200, {"auctions": COMMODITIES}
        if path == "/data/wow/achievement/855":
            return 200, {"_links": {"self": {"href": "x"}}, "id": 855, "name": "Explore", "reward_description": "r",
                         "category": {"key": {"href": "y"}, "name": "Exploration", "id": 3}}
        if path == "/data/wow/guild/test-guild":
            return (200, GUILD) if self.guild_status == 200 else (self.guild_status, None)
        if path == "/data/wow/guild/test-guild/roster":
            return 200, ROSTER
        base = "/profile/wow/character/test-char"
        if path == base:
            return (200, PROFILE) if self.profile_status == 200 else (self.profile_status, None)
        sub = {"/equipment": EQUIPMENT, "/statistics": STATS, "/professions": PROFESSIONS,
               "/character-media": MEDIA, "/achievements/statistics": LIFETIME, "/achievements": ACHIEVEMENTS,
               "/reputations": REPUTATIONS, "/quests": QUESTS_IN, "/quests/completed": QUESTS_DONE,
               "/pvp-summary": PVP, "/hunter-pets": PETS, "/specializations": SPECS,
               "/collections/mounts": COLL_MOUNTS, "/collections/pets": COLL_PETS, "/collections/toys": COLL_TOYS,
               "/collections/heirlooms": COLL_HEIRLOOMS, "/collections/transmogs": COLL_TRANSMOGS,
               "/appearance": APPEARANCE, "/titles": TITLES, "/mythic-keystone-profile": MYTHIC,
               "/encounters/dungeons": ENC_DUNGEONS, "/encounters/raids": ENC_RAIDS}
        if path.endswith("/hunter-pets") and not self.hunter_ok:
            return 404, None
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

    # --- dalsi sekce (na vyzadani) ---
    NEW_KEYS = ("achievements", "reputations", "quests", "pvp", "hunterPets", "specializations")

    def test_default_sections_do_not_include_new_ones(self):
        c = self.client().get_character("test-char")
        for k in self.NEW_KEYS:
            self.assertNotIn(k, c)
        self.assertNotIn("iconUrl", c["gear"][0])
        self.assertEqual(self.fake.count("/pvp-summary"), 0)
        self.assertEqual(self.fake.count("/media/item/"), 0)

    def test_all_new_sections(self):
        c = self.client(locale="cs_CZ").get_character("test-char", sections="all")
        a = c["achievements"]
        self.assertEqual((a["total"], a["points"]), (2, 10))
        self.assertEqual([x["name"] for x in a["completed"]], ["First", "Second"])  # podle casu, bez rozpracovanych
        self.assertTrue(a["completed"][0]["completedAt"].startswith("2023-11-14T22:15"))
        self.assertEqual((a["recent"][0]["name"], a["categories"][0]["name"]), ("Second", "Adventure"))
        self.assertEqual([r["name"] for r in c["reputations"]], ["Alpha", "Beta", "Zeta"])  # tier sestupne, pak nazev
        self.assertEqual((c["reputations"][0]["standing"], c["reputations"][0]["value"], c["reputations"][0]["max"]),
                         ("Honored", 8000, 12000))
        q = c["quests"]
        self.assertEqual((q["completedCount"], [x["name"] for x in q["completed"]], q["inProgress"]),
                         (2, ["Done A", "Done B"], [{"id": 5, "name": "Open quest"}]))
        self.assertEqual(c["pvp"], {"honorableKills": 834, "rank": 1, "battlegrounds": [
            {"id": 489, "name": "Warsong Gulch", "played": 11, "won": 3, "lost": 8}]})
        self.assertEqual(c["hunterPets"], [{"name": "Rex", "level": 35, "slot": 0, "active": True, "summoned": False,
                                            "creatureId": 3241, "creature": "Savannah Patriarch"}])
        spec = c["specializations"][0]["specializations"][0]
        self.assertTrue(c["specializations"][0]["active"])
        self.assertEqual((spec["name"], spec["points"], spec["talents"][0]["spellId"], spec["talents"][0]["rank"]),
                         ("Beast Mastery", 26, 19552, 5))
        self.assertTrue(all("iconUrl" in g for g in c["gear"]))  # "all" zahrnuje i ikony
        # nazvy jsou jen anglicky, i kdyz klient pouziva cs_CZ
        for part in ("/achievements?", "/reputations?", "/quests?", "/quests/completed?", "/pvp-summary?",
                     "/hunter-pets?", "/specializations?"):
            self.assertIn("locale=en_GB", next(u for u in self.fake.calls if part in u), part)

    def test_sections_as_comma_string(self):
        c = self.client().get_character("test-char", sections="pvp, reputations")
        self.assertEqual(set(c) & set(self.NEW_KEYS), {"pvp", "reputations"})
        self.assertNotIn("stats", c)

    def test_hunter_pets_none_for_non_hunters(self):
        self.fake.hunter_ok = False
        self.assertIsNone(self.client().get_character("test-char", sections=["hunterPets"])["hunterPets"])

    def test_quests_list_failure_gives_none_not_zero(self):
        orig = self.fake.__call__

        def broken(url, *a, **k):
            return (500, None) if "/quests/completed" in url else orig(url, *a, **k)
        m._http = broken
        q = self.client().get_character("test-char", sections=["quests"])["quests"]
        self.assertIsNone(q["completed"])
        self.assertIsNone(q["completedCount"])
        self.assertEqual(len(q["inProgress"]), 1)

    def test_icons_imply_gear_and_are_cached(self):
        c = self.client().get_character("test-char", sections=["icons"])  # bez "gear"
        self.assertEqual([g["iconUrl"] for g in c["gear"]], ["https://example.test/i1.jpg", None])
        self.assertEqual(self.fake.count("/media/item/"), 2)
        self.client().get_character("test-char", sections=["icons"], refresh=True)  # novy "proces"
        self.assertEqual(self.fake.count("/media/item/"), 2)  # znama ikona i nenalezeny item (404) se nepreptavaji

    def test_icon_negative_cache_expires_and_network_error_is_not_cached(self):
        self.client().get_character("test-char", sections=["icons"])
        cache = json.loads((self.tmp / "icons.json").read_text(encoding="utf-8"))
        self.assertEqual(cache["2"]["url"], None)
        cache["2"]["t"] = time.time() - 8 * 86400
        (self.tmp / "icons.json").write_text(json.dumps(cache), encoding="utf-8")
        self.client().get_character("test-char", sections=["icons"], refresh=True)
        self.assertEqual(self.fake.count("/media/item/2"), 2)  # po 7 dnech se zkusi znovu
        self.fake.icon_errors = {1}
        (self.tmp / "icons.json").unlink()
        self.client().get_character("test-char", sections=["icons"], refresh=True)
        self.assertNotIn("1", json.loads((self.tmp / "icons.json").read_text(encoding="utf-8")))  # chyba site se necachuje

    def test_unknown_section_lists_valid_ones(self):
        with self.assertRaises(ConfigError) as cm:
            self.client().get_character("test-char", sections="stats,bogus")
        self.assertIn("bogus", str(cm.exception))
        self.assertIn("achievements", str(cm.exception))

    def test_cli_sections_all(self):
        args = ["--env-file", "nonexist", "--client-id", "id123456789", "--client-secret", "s"]
        with mock.patch.dict(os.environ, {"WOWSTATS_CACHE_DIR": str(self.tmp)}):
            out = io.StringIO()
            with contextlib.redirect_stdout(out):
                self.assertEqual(cli.main(["character", "test-char", "--sections", "all", *args]), 0)
        self.assertIn("achievements", json.loads(out.getvalue()))

    # --- profil, detaily itemu, recepty, sbirky, vzhled a dalsi ---
    def test_profile_extras_in_default_output(self):
        c = self.client().get_character("test-char")
        self.assertEqual((c["experience"], c["gender"], c["achievementPoints"], c["averageItemLevel"]),
                         (60503, "Male", 10, 25))

    def test_item_details_are_complete_and_tidy(self):
        c = self.client(locale="cs_CZ").get_character("test-char", sections=["itemDetails"])  # automaticky zahrne gear
        self.assertIn("locale=en_GB", next(u for u in self.fake.calls if "/equipment?" in u))  # texty jen anglicky
        d = c["gear"][0]["details"]
        self.assertEqual(d["binding"], {"type": "ON_ACQUIRE", "name": "Binds when picked up"})
        self.assertEqual(d["stats"][0], {"type": {"type": "AGILITY", "name": "Agility"}, "value": 3, "isHidden": False,
                                         "display": {"displayString": "+3 Agility"}})
        self.assertEqual((d["item"], d["media"]), ({"id": 1}, {"id": 1}))  # odkazy odstraneny
        self.assertEqual(d["slot"], {"type": "HEAD"})  # nic se nezahazuje, ani to, co je uz v gear
        self.assertNotIn("iconUrl", c["gear"][0])

    def test_recipes_imply_professions_and_use_english(self):
        c = self.client(locale="cs_CZ").get_character("test-char", sections=["recipes"])
        p = c["professions"][0]
        self.assertEqual(p["recipes"], [{"id": 1353, "name": "Boots", "tier": "Leatherworking"},
                                        {"id": 4419, "name": "Belt", "tier": "Leatherworking"}])
        self.assertEqual(p["tiers"], [{"id": 2945, "name": "Leatherworking", "skill": 120, "maxSkill": 150}])
        self.assertIn("locale=en_GB", next(u for u in self.fake.calls if "/professions?" in u))
        self.assertNotIn("recipes", self.client().get_character("test-char", sections=["professions"],
                                                                refresh=True)["professions"][0])

    def test_collections(self):
        c = self.client().get_character("test-char", sections=["collections"])["collections"]
        self.assertEqual(c["mounts"], [{"id": 3111, "name": "Horse", "usable": True}])
        self.assertEqual(c["pets"], [{"species": {"name": "Zergling", "id": 5160}, "level": 1,
                                      "quality": {"type": "UNCOMMON", "name": "Uncommon"},
                                      "stats": {"breedId": 0, "health": 100}, "creatureDisplay": {"id": 9}, "id": 77}])
        self.assertEqual((c["unlockedBattlePetSlots"], c["toys"], c["heirlooms"]), (0, [{"id": 1761, "name": "Windwell"}], []))
        self.assertEqual(c["transmogs"], [{"slot": "HEAD", "appearanceIds": [29124, 64412]}])

    def test_collections_partial_failure_keeps_the_rest(self):
        self.fake.fail = {"/collections/pets"}
        c = self.client().get_character("test-char", sections=["collections"])["collections"]
        self.assertIsNone(c["pets"])
        self.assertEqual(len(c["mounts"]), 1)
        self.fake.fail = {"/collections/mounts", "/collections/pets", "/collections/toys",
                          "/collections/heirlooms", "/collections/transmogs"}
        self.assertIsNone(self.client().get_character("test-char", sections=["collections"], refresh=True)["collections"])

    def test_passthrough_sections(self):
        c = self.client().get_character("test-char", sections=["appearance", "titles", "mythicKeystone", "encounters"])
        a = c["appearance"]
        self.assertNotIn("character", a)
        self.assertNotIn("_links", a)
        self.assertEqual(a["playableRace"], {"name": "Orc", "id": 2})
        self.assertEqual(a["customizations"][0]["choice"], {"id": 984, "displayOrder": 0})
        self.assertEqual(c["titles"], {})  # {} = API nic nevratilo
        self.assertEqual(c["mythicKeystone"], {"currentPeriod": {"period": {"id": 1090}}})
        self.assertEqual(c["encounters"], {"dungeons": {}, "raids": {"someField": {"innerValue": 1}}})

    def test_passthrough_none_when_request_fails(self):
        self.fake.fail = {"/appearance", "/titles", "/encounters/dungeons", "/encounters/raids"}
        c = self.client().get_character("test-char", sections=["appearance", "titles", "encounters"])
        self.assertEqual((c["appearance"], c["titles"], c["encounters"]), (None, None, None))  # None = selhalo

    def test_achievements_in_progress(self):
        a = self.client().get_character("test-char", sections=["achievements"])["achievements"]
        self.assertEqual(a["inProgress"], [{"id": 3, "name": "In progress", "criteria": {"id": 9, "isCompleted": False}}])

    # --- libovolny endpoint ---
    def test_raw_untouched_clean_cached_and_validated(self):
        doc = self.client().raw("/data/wow/achievement/855")
        self.assertIn("_links", doc)
        self.assertEqual(doc["category"]["key"], {"href": "y"})  # bez uprav
        clean = self.client().raw("/data/wow/achievement/855", clean=True)  # novy "proces": z cache
        self.assertEqual((clean["rewardDescription"], clean["category"]), ("r", {"name": "Exploration", "id": 3}))
        self.assertEqual(self.fake.count("/data/wow/achievement/855"), 1)
        with self.assertRaises(NotFound):
            self.client().raw("/data/wow/achievement/999")
        for bad in ("/etc/passwd", "/data/wow/../x", "https://evil.test/data/wow/x", "/data/wow/x?y=1"):
            with self.assertRaises(ConfigError, msg=bad):
                self.client().raw(bad)
        with self.assertRaises(ConfigError):
            self.client().raw("/data/wow/achievement/855", kind="bogus")

    def test_raw_params_are_sorted_in_url(self):
        self.client().raw("/data/wow/achievement/855", params={"b": 2, "a": 1}, refresh=True)
        self.assertTrue(any("&a=1&b=2" in u for u in self.fake.calls))

    # --- rulesety a aukce ---
    def test_rulesets_and_auction_houses(self):
        cl = self.client()
        self.assertEqual(cl.get_rulesets(), [{"id": 1, "name": "PvP"}, {"id": 2, "name": "Normal"}])
        houses = [{"id": 110, "name": "Alliance Auction House"}, {"id": 113, "name": "Horde Auction House"}]
        self.assertEqual(cl.get_auction_houses(), houses)          # vychozi Normal
        self.assertEqual(cl.get_auction_houses("normal"), houses)  # podle nazvu
        self.assertEqual(cl.get_auction_houses(2), houses)         # podle id
        with self.assertRaises(ConfigError) as cm:
            cl.get_auction_houses("bogus")
        self.assertIn("2=Normal", str(cm.exception))
        with self.assertRaises(ConfigError):
            cl.get_auction_houses(99)

    def test_auctions_normalized_filtered_and_cached(self):
        a = self.client().get_auctions(110)
        self.assertEqual((a["house"], a["ruleset"], a["kind"], (a["total"], a["matched"], a["returned"])),
                         ({"id": 110, "name": "Alliance Auction House"}, {"id": 2, "name": "Normal"}, "auctions", (3, 3, 3)))
        self.assertEqual(a["auctions"][1], {"id": 2, "itemId": 11, "quantity": 2, "buyout": 200, "timeLeft": "SHORT",
                                            "bid": 150, "context": 1, "modifiers": [{"type": 28, "value": 5}],
                                            "bonusLists": [1, 2]})
        self.assertNotIn("bid", a["auctions"][0])
        n = self.fake.count("/auctions/110")
        b = self.client().get_auctions("110", item_ids=[10], limit=1)  # novy "proces": filtr az nad cache na disku
        self.assertEqual((b["total"], b["matched"], b["returned"], [x["id"] for x in b["auctions"]]), (3, 2, 1, [1]))
        self.assertEqual(self.fake.count("/auctions/110"), n)

    def test_commodities(self):
        a = self.client().get_auctions(110, kind="commodities")
        self.assertEqual(a["auctions"], [{"id": 5, "itemId": 20, "quantity": 17, "unitPrice": 20, "timeLeft": "LONG"}])
        self.assertEqual(a["house"]["name"], "Alliance Auction House")  # nazev z seznamu domu (dokument ho nema)

    def test_auction_item_names(self):
        self.fake.items = {10: "Sword", 11: "Axe"}
        a = self.client().get_auctions(110, names=True, item_ids=[10])
        self.assertEqual({x["itemName"] for x in a["auctions"]}, {"Sword"})
        with mock.patch.object(m, "_AUCTION_NAMES_MAX", 1):
            with self.assertRaises(ConfigError):
                self.client().get_auctions(110, names=True)  # 2 ruzne itemy > limit

    def test_auction_errors(self):
        cl = self.client()
        with self.assertRaises(NotFound):
            cl.get_auctions(999)
        with self.assertRaises(ConfigError):
            cl.get_auctions("abc")
        with self.assertRaises(ConfigError):
            cl.get_auctions(110, kind="bogus")

    def test_cli_new_commands(self):
        base = ["--env-file", "nonexist", "--client-id", "id123456789", "--client-secret", "s"]

        def run(*args):
            out = io.StringIO()
            with mock.patch.dict(os.environ, {"WOWSTATS_CACHE_DIR": str(self.tmp)}), contextlib.redirect_stdout(out):
                code = cli.main([*args, *base])
            return code, json.loads(out.getvalue()) if out.getvalue() else None

        self.assertEqual(run("rulesets")[1][1], {"id": 2, "name": "Normal"})
        self.assertEqual(run("auction-houses", "--ruleset", "2")[1][0]["id"], 110)
        code, data = run("auctions", "110", "--item-id", "10", "--limit", "5")
        self.assertEqual((code, data["matched"]), (0, 2))
        code, data = run("raw", "/data/wow/achievement/855", "--clean")
        self.assertEqual(data["rewardDescription"], "r")
        err = io.StringIO()
        with mock.patch.dict(os.environ, {"WOWSTATS_CACHE_DIR": str(self.tmp)}), contextlib.redirect_stderr(err):
            self.assertEqual(cli.main(["raw", "/data/wow/x", "--param", "chybi-rovnitko", *base]), 4)

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
