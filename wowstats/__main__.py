"""CLI: wowstats character <jmeno> | guild <nazev> | rulesets | auction-houses | auctions <dum> | raw <cesta>
-> JSON na stdout.

Navratove kody: 0 ok, 2 postava/guilda/dum/cesta nenalezena, 3 chyba Blizzard API, 4 chybna konfigurace.
Chyby jdou jako JSON na stderr: {"error": "...", "code": N}.
"""
import argparse
import json
import sys

from . import ALL_SECTIONS, DEFAULT_SECTIONS, ConfigError, ForeverClient, WowStatsError


def _parser() -> argparse.ArgumentParser:
    common = argparse.ArgumentParser(add_help=False)
    common.add_argument("--ttl", type=int, help="cache v sekundach (vychozi 900)")
    common.add_argument("--refresh", action="store_true", help="obejit cache")
    common.add_argument("--region", help="eu|us|kr|tw (vychozi eu)")
    common.add_argument("--locale", help="napr. en_GB")
    common.add_argument("--env-file", default=".env", help="soubor s BLIZZARD_CLIENT_ID/SECRET (vychozi .env)")
    common.add_argument("--client-id", help="radsi pouzij promenne prostredi (argumenty jsou videt v seznamu procesu)")
    common.add_argument("--client-secret")
    common.add_argument("--pretty", action="store_true", help="formatovany JSON")

    ap = argparse.ArgumentParser(prog="wowstats", description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = ap.add_subparsers(dest="cmd", required=True)

    c = sub.add_parser("character", parents=[common], help="data postavy")
    c.add_argument("name", help="slug nebo 'Jmeno Prijmeni', napr. jmeno-prijmeni")
    c.add_argument("--sections", default="default",
                   help=f"'default' ({','.join(DEFAULT_SECTIONS)}), 'all', nebo carkou oddelene z: "
                        f"{','.join(ALL_SECTIONS)}. Kazda sekce je aspon jeden dotaz; 'icons' automaticky pridava 'gear'")
    c.add_argument("--no-wowhead", action="store_true", help="nedoplnovat nazvy itemu z Wowheadu")

    g = sub.add_parser("guild", parents=[common], help="guilda a seznam clenu")
    g.add_argument("name", help="slug nebo nazev guildy, napr. 'Nazev Guildy' / nazev-guildy")

    sub.add_parser("rulesets", parents=[common], help="rulesety (misto realmu): id a nazev")

    ah = sub.add_parser("auction-houses", parents=[common], help="aukcni domy")
    ah.add_argument("--ruleset", help="id nebo nazev (vychozi Normal; data aukci jsou u vsech rulesetu stejna)")

    au = sub.add_parser("auctions", parents=[common], help="nabidky v aukcnim dome (velke, cachuje se na disk)")
    au.add_argument("house", help="id aukcniho domu (viz auction-houses)")
    au.add_argument("--ruleset", help="id nebo nazev (vychozi Normal)")
    au.add_argument("--kind", default="auctions", choices=["auctions", "commodities"],
                    help="auctions = jednotlive nabidky, commodities = hromadne zbozi")
    au.add_argument("--item-id", type=int, action="append", dest="item_ids", metavar="ID",
                    help="jen tento item (lze opakovat)")
    au.add_argument("--limit", type=int, help="vratit nejvyse N nabidek")
    au.add_argument("--names", action="store_true", help="doplnit nazvy itemu (max. 500 ruznych)")

    rw = sub.add_parser("raw", parents=[common], help="libovolny endpoint Blizzard API (/data/wow/… nebo /profile/wow/…)")
    rw.add_argument("path", help="napr. /data/wow/achievement/855")
    rw.add_argument("--kind", default="static", choices=["static", "dynamic", "profile"], help="namespace (vychozi static)")
    rw.add_argument("--param", action="append", default=[], metavar="KLIC=HODNOTA", help="dalsi parametr dotazu (lze opakovat)")
    rw.add_argument("--clean", action="store_true", help="bez odkazu, klice v camelCase")
    return ap


def main(argv=None) -> int:
    for stream in (sys.stdout, sys.stderr):  # JSON vzdy v UTF-8, i kdyz Windows pouziva cp1250 pri presmerovani
        if hasattr(stream, "reconfigure"):
            stream.reconfigure(encoding="utf-8")
    a = _parser().parse_args(argv)
    try:
        client = ForeverClient.from_env(a.env_file, client_id=a.client_id, client_secret=a.client_secret,
                                        region=a.region, locale=a.locale,
                                        use_wowhead=False if getattr(a, "no_wowhead", False) else None)
        if a.cmd == "guild":
            data = client.get_guild(a.name, ttl=a.ttl, refresh=a.refresh)
        elif a.cmd == "rulesets":
            data = client.get_rulesets(ttl=a.ttl, refresh=a.refresh)
        elif a.cmd == "auction-houses":
            data = client.get_auction_houses(a.ruleset, ttl=a.ttl, refresh=a.refresh)
        elif a.cmd == "auctions":
            data = client.get_auctions(a.house, ruleset=a.ruleset, kind=a.kind, item_ids=a.item_ids, limit=a.limit,
                                       names=a.names, ttl=a.ttl, refresh=a.refresh)
        elif a.cmd == "raw":
            params = {}
            for p in a.param:
                if "=" not in p:
                    raise ConfigError(f"--param ocekava KLIC=HODNOTA, dostal jsem {p!r}")
                k, v = p.split("=", 1)
                params[k] = v
            data = client.raw(a.path, kind=a.kind, params=params or None, locale=a.locale, ttl=a.ttl,
                              refresh=a.refresh, clean=a.clean)
        else:
            data = client.get_character(a.name, sections=a.sections, ttl=a.ttl, refresh=a.refresh)
    except WowStatsError as e:
        print(json.dumps({"error": str(e), "code": e.code}, ensure_ascii=False), file=sys.stderr)
        return e.code
    print(json.dumps(data, ensure_ascii=False, indent=2 if a.pretty else None))
    return 0


if __name__ == "__main__":
    sys.exit(main())
