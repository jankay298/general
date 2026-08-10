"""Price providers and the registry that builds them by name."""

from __future__ import annotations

from typing import Callable, Dict, List, Optional

from ...config import Config
from ..base import PriceProvider
from .alphavantage import AlphaVantageProvider
from .csvfile import CsvProvider
from .stooq import StooqProvider
from .synthetic import SyntheticProvider
from .yahoo import YahooProvider

_BUILDERS: Dict[str, Callable[[Config], PriceProvider]] = {
    "yahoo": lambda cfg: YahooProvider(),
    "stooq": lambda cfg: StooqProvider(),
    "alphavantage": lambda cfg: AlphaVantageProvider(key_env=cfg.data.alphavantage_key_env),
    "csv": lambda cfg: CsvProvider(directory=cfg.resolve(cfg.data.csv_dir)),
    "synthetic": lambda cfg: SyntheticProvider(seed=cfg.run.random_seed),
}

KNOWN_PROVIDERS = tuple(_BUILDERS)


def build_providers(cfg: Config, names: Optional[List[str]] = None) -> List[PriceProvider]:
    """Instantiate the configured provider chain, skipping unavailable ones."""
    wanted = names if names is not None else cfg.data.price_providers
    providers: List[PriceProvider] = []
    for name in wanted:
        key = str(name).strip().lower()
        if key not in _BUILDERS:
            raise ValueError(
                f"unknown price provider {name!r}; known: {', '.join(KNOWN_PROVIDERS)}"
            )
        providers.append(_BUILDERS[key](cfg))
    return providers


__all__ = [
    "AlphaVantageProvider",
    "CsvProvider",
    "StooqProvider",
    "SyntheticProvider",
    "YahooProvider",
    "build_providers",
    "KNOWN_PROVIDERS",
]
