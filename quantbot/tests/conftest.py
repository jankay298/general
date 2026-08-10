"""Shared fixtures.

Everything here runs on the synthetic provider, so the whole suite works with no
network and no API keys — which is the only way a test suite for a trading bot
stays useful.
"""

from __future__ import annotations

import pytest

from quantbot.config import Config
from quantbot.data.repository import MarketData, load_market_data
from quantbot.utils.cache import Cache


@pytest.fixture(scope="session")
def cfg(tmp_path_factory) -> Config:
    cache_dir = tmp_path_factory.mktemp("cache")
    config = Config()
    config.base_dir = str(tmp_path_factory.mktemp("base"))
    config.run.cache_dir = str(cache_dir)
    config.universe.symbols = [f"T{i:02d}" for i in range(24)]
    config.universe.benchmark = "TMKT"
    config.universe.sectors = {
        f"T{i:02d}": ["tech", "financials", "energy", "health"][i % 4] for i in range(24)
    }
    config.data.price_providers = ["synthetic"]
    config.data.start = "2016-01-01"
    config.data.end = "2023-12-31"
    config.data.use_macro = False       # no network in tests
    config.data.use_news = False
    config.data.use_fundamentals = False
    config.backtest.initial_capital = 100_000.0
    return config


@pytest.fixture(scope="session")
def market(cfg: Config) -> MarketData:
    cache = Cache(cfg.resolve(cfg.run.cache_dir), ttl_hours=999, enabled=False)
    return load_market_data(cfg, cache, for_backtest=True)


@pytest.fixture
def small_cfg(cfg: Config) -> Config:
    """A faster config for tests that only need a few symbols."""
    import copy

    small = copy.deepcopy(cfg)
    small.universe.symbols = cfg.universe.symbols[:12]
    small.universe.sectors = {s: cfg.universe.sectors[s] for s in small.universe.symbols}
    small.data.start = "2018-01-01"
    small.strategy.min_positions = 3
    small.strategy.max_positions = 6
    return small


@pytest.fixture
def small_market(small_cfg: Config) -> MarketData:
    cache = Cache(small_cfg.resolve(small_cfg.run.cache_dir), ttl_hours=999, enabled=False)
    return load_market_data(small_cfg, cache, for_backtest=True)
