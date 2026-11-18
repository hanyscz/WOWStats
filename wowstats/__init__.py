"""WoW Forever statistiky postav a guild (neoficialni Blizzard API)."""
from .client import (ALL_SECTIONS, CharacterNotFound, ConfigError, ForeverClient, GuildNotFound, NotFound,
                     UpstreamError, WowStatsError)


def _client(client_id, client_secret, region, locale, env_file, use_wowhead=None) -> ForeverClient:
    return ForeverClient.from_env(env_file, client_id=client_id, client_secret=client_secret,
                                  region=region, locale=locale, use_wowhead=use_wowhead)


def get_character(name: str, *, sections=ALL_SECTIONS, ttl: int | None = None, refresh: bool = False,
                  client_id: str | None = None, client_secret: str | None = None,
                  region: str | None = None, locale: str | None = None, env_file: str | None = ".env",
                  use_wowhead: bool | None = None) -> dict:
    """Zkratka: klic a nastaveni z argumentu > promennych prostredi > .env.
    use_wowhead=False vypne doplnovani nazvu itemu z Wowheadu."""
    c = _client(client_id, client_secret, region, locale, env_file, use_wowhead)
    return c.get_character(name, sections=sections, ttl=ttl, refresh=refresh)


def get_guild(name: str, *, ttl: int | None = None, refresh: bool = False,
              client_id: str | None = None, client_secret: str | None = None,
              region: str | None = None, locale: str | None = None, env_file: str | None = ".env") -> dict:
    """Guilda a seznam clenu (nazev nebo slug). Konfigurace stejne jako u get_character."""
    return _client(client_id, client_secret, region, locale, env_file).get_guild(name, ttl=ttl, refresh=refresh)


__all__ = ["get_character", "get_guild", "ForeverClient", "ALL_SECTIONS", "WowStatsError", "ConfigError",
           "NotFound", "CharacterNotFound", "GuildNotFound", "UpstreamError"]
