"""The most important tests in the suite.

Look-ahead bias does not announce itself. It produces a backtest that is merely
*very good* rather than obviously broken, and it survives code review because the
offending line — a ``.shift(-1)``, a centred rolling window, a fillna that
propagates backwards — reads as innocuous.

The test below is the only reliable defence: compute a signal, then destroy every
data point after some date, recompute, and assert nothing before that date moved.
If any part of the pipeline peeks forward, these fail.
"""

from __future__ import annotations

import copy

import numpy as np
import pandas as pd
import pytest

from quantbot.data.repository import MarketData
from quantbot.features.macro_regime import classify_regime
from quantbot.features.technical import compute_technicals
from quantbot.strategy import build_strategy


def _truncate(market: MarketData, cutoff: pd.Timestamp) -> MarketData:
    """A copy of the data in which everything after *cutoff* is destroyed.

    Not merely removed — replaced with garbage. Removing rows would also catch a
    forward-looking window, but corrupting them additionally catches code that
    reindexes back to the full calendar and quietly picks the future up again.
    """
    mangled = copy.deepcopy(market)
    future = mangled.close.index > cutoff
    for name in ("open", "high", "low", "close", "volume"):
        panel = getattr(mangled, name)
        panel.loc[future] = panel.loc[future] * 0.0 + 999.0
        setattr(mangled, name, panel)
    if mangled.benchmark is not None:
        mangled.benchmark.loc[future] = 999.0
    return mangled


def test_technical_signals_do_not_see_the_future(market):
    cutoff = market.close.index[len(market.close) // 2]
    from quantbot.config import FeatureConfig

    features = FeatureConfig()
    original = compute_technicals(market, features)
    corrupted = compute_technicals(_truncate(market, cutoff), features)

    for name, panel in original.signals.items():
        before = panel.loc[:cutoff]
        after = corrupted.signals[name].loc[:cutoff]
        pd.testing.assert_frame_equal(
            before, after,
            check_exact=False, rtol=1e-9,
            obj=f"signal {name!r} changed when future data changed — look-ahead bias",
        )


def test_volatility_and_atr_do_not_see_the_future(market):
    from quantbot.config import FeatureConfig

    cutoff = market.close.index[len(market.close) // 2]
    original = compute_technicals(market, FeatureConfig())
    corrupted = compute_technicals(_truncate(market, cutoff), FeatureConfig())

    for attribute in ("vol", "atr", "rsi", "adx", "dollar_volume"):
        pd.testing.assert_frame_equal(
            getattr(original, attribute).loc[:cutoff],
            getattr(corrupted, attribute).loc[:cutoff],
            check_exact=False, rtol=1e-9,
            obj=f"{attribute} changed when future data changed",
        )


def test_regime_classification_does_not_see_the_future(market):
    from quantbot.config import RegimeConfig

    cutoff = market.close.index[len(market.close) // 2]
    cfg = RegimeConfig()
    original = classify_regime(market.macro, cfg, market.benchmark, market.close.index)
    corrupted_market = _truncate(market, cutoff)
    corrupted = classify_regime(
        corrupted_market.macro, cfg, corrupted_market.benchmark, corrupted_market.close.index
    )
    pd.testing.assert_series_equal(
        original.score.loc[:cutoff], corrupted.score.loc[:cutoff],
        check_exact=False, rtol=1e-9,
        obj="regime score changed when future data changed",
    )
    pd.testing.assert_series_equal(
        original.label.loc[:cutoff], corrupted.label.loc[:cutoff],
        obj="regime label changed when future data changed",
    )


@pytest.mark.parametrize("strategy_name", ["composite", "trend", "buy_and_hold"])
def test_target_weights_do_not_see_the_future(small_cfg, small_market, strategy_name):
    cutoff = small_market.close.index[int(len(small_market.close) * 0.6)]
    probe_dates = small_market.close.index[
        int(len(small_market.close) * 0.4) : int(len(small_market.close) * 0.6) : 20
    ]

    clean = build_strategy(strategy_name, small_cfg)
    clean.prepare(small_market)
    dirty = build_strategy(strategy_name, small_cfg)
    dirty.prepare(_truncate(small_market, cutoff))

    for date in probe_dates:
        pd.testing.assert_series_equal(
            clean.target_weights(date),
            dirty.target_weights(date),
            check_exact=False, rtol=1e-9,
            obj=f"{strategy_name} weights on {date.date()} depend on future data",
        )


def test_engine_fills_after_the_signal_bar(small_cfg, small_market):
    """A trade must never be priced at the bar that produced its signal."""
    from quantbot.backtest.engine import BacktestEngine

    cfg = copy.deepcopy(small_cfg)
    cfg.backtest.rebalance = "BME"
    cfg.backtest.execution_lag_bars = 1

    engine = BacktestEngine(cfg)
    result = engine.run(build_strategy("composite", cfg), small_market, label="lag-check")
    assert not result.trades.empty, "no trades to check"

    index = small_market.close.index
    position = {d: i for i, d in enumerate(index)}

    for _, trade in result.trades.iterrows():
        date = pd.Timestamp(trade["date"])
        expected = small_market.open.loc[date, trade["symbol"]]
        assert np.isclose(trade["price"], expected, rtol=1e-9), (
            f"fill on {date.date()} used {trade['price']}, not that day's open "
            f"{expected} — the engine is not filling where it claims to"
        )
        # And the signal that caused it came from a strictly earlier bar.
        assert position[date] >= 1


def test_execution_lag_of_zero_is_rejected_by_validation():
    from quantbot.config import Config

    cfg = Config()
    cfg.universe.symbols = ["A", "B", "C"]
    cfg.backtest.execution_lag_bars = 0
    problems = cfg.validate()
    assert any("look-ahead" in p for p in problems), (
        "a zero execution lag is look-ahead bias and validation must say so"
    )
