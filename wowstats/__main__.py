"""CLI: wowstats character <jmeno> | wowstats guild <nazev>  -> JSON na stdout.

Navratove kody: 0 ok, 2 postava/guilda nenalezena, 3 chyba Blizzard API, 4 chybna konfigurace.
Chyby jdou jako JSON na stderr: {"error": "...", "code": N}.
"""
import argparse
import json
import sys

from . import ALL_SECTIONS, ForeverClient, WowStatsError


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
    c.add_argument("--sections", default=",".join(ALL_SECTIONS),
                   help=f"carkou oddelene: {','.join(ALL_SECTIONS)} (mene sekci = mene dotazu)")
    c.add_argument("--no-wowhead", action="store_true", help="nedoplnovat nazvy itemu z Wowheadu")

    g = sub.add_parser("guild", parents=[common], help="guilda a seznam clenu")
    g.add_argument("name", help="slug nebo nazev guildy, napr. 'Nazev Guildy' / nazev-guildy")
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
        else:
            secs = [s.strip() for s in a.sections.split(",") if s.strip()]
            data = client.get_character(a.name, sections=secs, ttl=a.ttl, refresh=a.refresh)
    except WowStatsError as e:
        print(json.dumps({"error": str(e), "code": e.code}, ensure_ascii=False), file=sys.stderr)
        return e.code
    print(json.dumps(data, ensure_ascii=False, indent=2 if a.pretty else None))
    return 0


if __name__ == "__main__":
    sys.exit(main())
