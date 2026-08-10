"""Backtest engine behaviour, metrics, and the live execution path."""

from __future__ import annotations

import copy

import numpy as np
import pandas as pd
import pytest

from quantbot.backtest.engine import BacktestEngine
from quantbot.backtest.metrics import compute_metrics, max_drawdown_duration
from quantbot.execution.broker import Order, orders_from_weights
from quantbot.execution.paper import PaperBroker
from quantbot.strategy import build_strategy


# --------------------------------------------------------------------------- #
# Engine
# --------------------------------------------------------------------------- #


def test_engine_produces_a_coherent_result(small_cfg, small_market):
    engine = BacktestEngine(small_cfg)
    result = engine.run(build_strategy("composite", small_cfg), small_market, label="t")

    assert len(result.equity) > 100
    assert result.equity.notna().all()
    assert (result.equity > 0).all()
    assert result.returns.index.equals(result.equity.index)
    assert result.metrics.trades == len(result.trades)
    assert result.meta["execution_lag_bars"] >= 1


def test_gross_exposure_never_exceeds_the_configured_cap(small_cfg, small_market):
    cfg = copy.deepcopy(small_cfg)
    cfg.risk.max_gross = 0.75
    engine = BacktestEngine(cfg)
    result = engine.run(build_strategy("composite", cfg), small_market, label="t")

    # Allow a little slack: weights are set at the open and drift with prices
    # until the next rebalance, which is the real behaviour, not a limit breach.
    assert result.exposure["gross"].max() <= 0.75 * 1.35


def test_long_only_config_never_shorts(small_cfg, small_market):
    cfg = copy.deepcopy(small_cfg)
    cfg.strategy.long_only = True
    cfg.strategy.short_quantile = 0.0
    engine = BacktestEngine(cfg)
    result = engine.run(build_strategy("composite", cfg), small_market, label="t")
    assert (result.weights.min(axis=1) >= -1e-9).all()


def test_costs_reduce_returns(small_cfg, small_market):
    """A strategy that trades must do strictly worse once costs are charged."""
    free = copy.deepcopy(small_cfg)
    free.backtest.costs.commission_bps = 0.0
    free.backtest.costs.spread_bps = 0.0
    free.backtest.costs.impact_coeff_bps = 0.0

    expensive = copy.deepcopy(small_cfg)
    expensive.backtest.costs.commission_bps = 20.0
    expensive.backtest.costs.spread_bps = 20.0
    expensive.backtest.costs.impact_coeff_bps = 50.0

    cheap_result = BacktestEngine(free).run(build_strategy("composite", free), small_market)
    dear_result = BacktestEngine(expensive).run(
        build_strategy("composite", expensive), small_market
    )

    assert dear_result.trades.shape[0] > 0
    assert dear_result.equity.iloc[-1] < cheap_result.equity.iloc[-1]
    assert dear_result.metrics.cost_drag > cheap_result.metrics.cost_drag


def test_selection_buffer_reduces_turnover(small_cfg, small_market):
    """Hysteresis must actually cut trading, not just exist in the config."""
    without = copy.deepcopy(small_cfg)
    without.strategy.selection_buffer = 0.0
    without.strategy.no_trade_band_relative = 0.0

    with_buffer = copy.deepcopy(small_cfg)
    with_buffer.strategy.selection_buffer = 0.5
    with_buffer.strategy.no_trade_band_relative = 0.25

    churny = BacktestEngine(without).run(build_strategy("composite", without), small_market)
    calmer = BacktestEngine(with_buffer).run(
        build_strategy("composite", with_buffer), small_market
    )
    assert calmer.metrics.turnover < churny.metrics.turnover


def test_orders_are_capped_by_available_volume(small_cfg, small_market):
    cfg = copy.deepcopy(small_cfg)
    cfg.strategy.max_adv_participation = 0.01
    engine = BacktestEngine(cfg)
    result = engine.run(build_strategy("composite", cfg), small_market, label="t")

    adv = small_market.volume.rolling(21, min_periods=5).mean()
    for _, trade in result.trades.iterrows():
        limit = adv.loc[pd.Timestamp(trade["date"]), trade["symbol"]]
        if np.isfinite(limit) and limit > 0:
            assert abs(trade["quantity"]) <= limit * 0.01 * 1.0001


def test_drawdown_guard_flattens_the_book(small_cfg, small_market):
    cfg = copy.deepcopy(small_cfg)
    cfg.risk.drawdown_killswitch = 0.02   # trip almost immediately
    cfg.risk.killswitch_cooldown_days = 5
    engine = BacktestEngine(cfg)
    result = engine.run(build_strategy("composite", cfg), small_market, label="t")

    assert result.guard_events, "a 2% kill switch should have fired at least once"
    assert (result.trades["reason"] == "drawdown_guard").any()


def test_benchmark_metrics_are_reported(small_cfg, small_market):
    result = BacktestEngine(small_cfg).run(
        build_strategy("buy_and_hold", small_cfg), small_market
    )
    assert result.benchmark_metrics is not None
    assert result.metrics.beta is not None


# --------------------------------------------------------------------------- #
# Metrics
# --------------------------------------------------------------------------- #


def test_metrics_on_a_known_series():
    index = pd.bdate_range("2020-01-01", periods=252)
    returns = pd.Series(np.full(252, 0.001), index=index)
    metrics = compute_metrics(returns)

    assert metrics.total_return == pytest.approx((1.001**252) - 1, rel=1e-6)
    assert metrics.volatility == pytest.approx(0.0, abs=1e-12)
    assert metrics.max_drawdown == pytest.approx(0.0)
    assert metrics.win_rate == pytest.approx(1.0)


def test_max_drawdown_is_measured_from_the_peak():
    equity = pd.Series(
        [100, 120, 90, 95, 130],
        index=pd.bdate_range("2024-01-01", periods=5),
    )
    returns = equity.pct_change().fillna(0.0)
    metrics = compute_metrics(returns, equity=equity)
    assert metrics.max_drawdown == pytest.approx(90 / 120 - 1)
    # Two bars sit strictly below the 120 high-water mark before it is regained.
    assert max_drawdown_duration(equity) == 2
    assert metrics.time_in_drawdown == pytest.approx(2 / 5)


def test_sortino_ignores_upside_volatility():
    """Big up days must not be penalised the way big down days are."""
    index = pd.bdate_range("2020-01-01", periods=200)
    rng = np.random.default_rng(7)
    base = rng.normal(0.0005, 0.004, 200)
    skewed = base.copy()
    skewed[::20] += 0.05  # occasional large gains, same small losses

    symmetric = compute_metrics(pd.Series(base, index=index))
    upside_heavy = compute_metrics(pd.Series(skewed, index=index))

    # The upside spikes inflate volatility, so Sharpe barely improves — but they
    # add nothing to downside deviation, so Sortino improves a lot.
    assert upside_heavy.sortino / upside_heavy.sharpe > symmetric.sortino / symmetric.sharpe


def test_sortino_is_infinite_when_nothing_ever_lost_money():
    index = pd.bdate_range("2020-01-01", periods=100)
    returns = pd.Series(np.full(100, 0.001), index=index)
    metrics = compute_metrics(returns)
    assert metrics.sortino == float("inf"), (
        "a series with no losing days has no downside deviation; reporting 0 "
        "would make a flawless record look like the worst possible one"
    )


def test_deflated_sharpe_falls_as_more_variants_are_tried():
    rng = np.random.default_rng(11)
    index = pd.bdate_range("2020-01-01", periods=500)
    returns = pd.Series(rng.normal(0.0004, 0.01, 500), index=index)

    one = compute_metrics(returns, n_trials=1).deflated_sharpe
    many = compute_metrics(returns, n_trials=500).deflated_sharpe
    assert one is not None and many is not None
    assert many < one, "searching more variants must discount the headline Sharpe"


def test_turnover_uses_contemporaneous_equity():
    """Turnover must not be inflated by growth in the equity curve."""
    index = pd.bdate_range("2020-01-01", periods=500)
    equity = pd.Series(np.linspace(100_000, 1_000_000, 500), index=index)
    returns = equity.pct_change().fillna(0.0)
    # One trade a day, each worth exactly 10% of that day's equity.
    trades = pd.DataFrame(
        {
            "date": index,
            "symbol": ["A"] * 500,
            "quantity": 1.0,
            "price": 1.0,
            "cost": 0.0,
            "notional": equity.to_numpy() * 0.10,
            "reason": "rebalance",
        }
    )
    metrics = compute_metrics(returns, trades=trades, equity=equity)
    years = 500 / 252
    assert metrics.turnover == pytest.approx(500 * 0.10 / years, rel=1e-6)


# --------------------------------------------------------------------------- #
# Order generation
# --------------------------------------------------------------------------- #


def test_orders_move_the_book_toward_the_target():
    targets = pd.Series({"A": 0.50, "B": 0.25})
    current = pd.Series({"A": 10.0})
    prices = pd.Series({"A": 100.0, "B": 50.0})

    orders = orders_from_weights(targets, current, prices, equity=10_000.0)
    by_symbol = {o.symbol: o for o in orders}
    assert by_symbol["A"].quantity == pytest.approx(40.0)   # want 50, hold 10
    assert by_symbol["B"].quantity == pytest.approx(50.0)   # want 2500 / 50


def test_orders_close_positions_dropped_from_the_target():
    targets = pd.Series({"A": 0.0})
    current = pd.Series({"A": 25.0})
    prices = pd.Series({"A": 40.0})
    orders = orders_from_weights(targets, current, prices, equity=10_000.0)
    assert len(orders) == 1
    assert orders[0].quantity == pytest.approx(-25.0)
    assert orders[0].reason == "exit"


def test_no_trade_band_suppresses_small_adjustments():
    targets = pd.Series({"A": 0.101})
    current = pd.Series({"A": 100.0})   # 0.10 of a 100k book at price 100
    prices = pd.Series({"A": 100.0})
    orders = orders_from_weights(
        targets, current, prices, equity=100_000.0,
        no_trade_band=0.005, no_trade_band_relative=0.25,
    )
    assert orders == []


def test_exits_ignore_the_no_trade_band():
    targets = pd.Series({"A": 0.0})
    current = pd.Series({"A": 1.0})
    prices = pd.Series({"A": 10.0})
    orders = orders_from_weights(
        targets, current, prices, equity=100_000.0,
        no_trade_band=0.05, no_trade_band_relative=0.5, min_notional=1000.0,
    )
    assert len(orders) == 1, "a position must always be closable"


def test_single_order_is_capped_at_a_share_of_equity():
    targets = pd.Series({"A": 5.0})   # absurd weight, e.g. from a bad price feed
    current = pd.Series(dtype=float)
    prices = pd.Series({"A": 100.0})
    orders = orders_from_weights(
        targets, current, prices, equity=100_000.0, max_order_pct_equity=0.15
    )
    assert orders[0].notional(100.0) == pytest.approx(15_000.0)


# --------------------------------------------------------------------------- #
# Paper broker
# --------------------------------------------------------------------------- #


def test_paper_broker_round_trip_and_persistence(tmp_path):
    state = tmp_path / "account.json"
    broker = PaperBroker(state, initial_cash=50_000.0)

    assert broker.account().equity == pytest.approx(50_000.0)
    assert broker.positions().empty

    result = broker.submit(Order("AAA", 100), reference_price=100.0)
    assert result.accepted
    assert broker.positions()["AAA"] == pytest.approx(100.0)
    assert broker.account().cash < 40_000.0

    # A fresh instance must see the same book — state is on disk, not in memory.
    reloaded = PaperBroker(state, initial_cash=50_000.0)
    assert reloaded.positions()["AAA"] == pytest.approx(100.0)
    assert reloaded.account().cash == pytest.approx(broker.account().cash)


def test_paper_broker_refuses_to_overspend(tmp_path):
    broker = PaperBroker(tmp_path / "a.json", initial_cash=1_000.0)
    result = broker.submit(Order("AAA", 100), reference_price=100.0)
    assert not result.accepted
    assert "insufficient cash" in result.message
    assert broker.positions().empty


def test_paper_broker_rejects_orders_without_a_price(tmp_path):
    broker = PaperBroker(tmp_path / "a.json", initial_cash=10_000.0)
    result = broker.submit(Order("AAA", 10), reference_price=None)
    assert not result.accepted


def test_paper_broker_slippage_moves_against_the_trader(tmp_path):
    from quantbot.backtest.costs import CostModel

    broker = PaperBroker(
        tmp_path / "a.json", initial_cash=100_000.0,
        cost_model=CostModel(commission_bps=0.0, spread_bps=10.0, impact_coeff_bps=0.0),
    )
    buy = broker.submit(Order("AAA", 10), reference_price=100.0)
    sell = broker.submit(Order("AAA", -10), reference_price=100.0)
    assert buy.filled_price > 100.0
    assert sell.filled_price < 100.0


def test_corrupt_paper_state_is_not_silently_overwritten(tmp_path):
    state = tmp_path / "a.json"
    state.write_text("{not valid json", encoding="utf-8")
    with pytest.raises(Exception):
        PaperBroker(state, initial_cash=10_000.0)
    # The damaged file must still be there for the operator to inspect.
    assert state.read_text(encoding="utf-8").startswith("{not valid")


# --------------------------------------------------------------------------- #
# Walk-forward
# --------------------------------------------------------------------------- #


def test_sharing_prepared_panels_matches_full_repreparation(small_cfg, small_market):
    """The walk-forward speedup must not change a single number."""
    other = copy.deepcopy(small_cfg)
    other.risk.target_vol = 0.20

    fresh = build_strategy("composite", other)
    fresh.prepare(small_market)

    template = build_strategy("composite", small_cfg)
    template.prepare(small_market)
    shared = template.with_config(other)

    for date in small_market.close.index[300::60]:
        pd.testing.assert_series_equal(
            fresh.target_weights(date),
            shared.target_weights(date),
            check_exact=False, rtol=1e-12,
            obj=f"shared panels changed the weights on {date.date()}",
        )


def test_with_config_does_not_leak_state_between_trials(small_cfg, small_market):
    template = build_strategy("composite", small_cfg)
    template.prepare(small_market)

    first = template.with_config(small_cfg)
    first.target_weights(small_market.close.index[400])
    assert first._last_vol_scale is not None

    second = template.with_config(small_cfg)
    assert second._last_vol_scale is None, "per-run state leaked into the next trial"
    assert template._last_vol_scale is None


def test_walk_forward_reports_out_of_sample_results(small_cfg, small_market):
    from quantbot.backtest.walkforward import run_walk_forward

    cfg = copy.deepcopy(small_cfg)
    cfg.backtest.rebalance = "BME"
    cfg.backtest.wf_train_days = 400
    cfg.backtest.wf_test_days = 150
    cfg.backtest.wf_step_days = 150

    result = run_walk_forward(
        cfg, small_market, grid={"risk.target_vol": (0.10, 0.15)}, also_run_in_sample=True
    )

    assert len(result.folds) >= 2
    assert not result.oos_returns.empty
    assert result.n_trials == 2
    assert result.in_sample_metrics is not None
    # Every fold must have traded a window strictly after the one it was fitted on.
    for fold in result.folds:
        assert fold.test_start > fold.train_end
    # The stitched curve must not reuse a bar twice.
    assert not result.oos_returns.index.duplicated().any()


def test_walk_forward_rejects_too_little_history(small_cfg, small_market):
    from quantbot.backtest.walkforward import run_walk_forward

    cfg = copy.deepcopy(small_cfg)
    cfg.backtest.wf_train_days = 100_000
    with pytest.raises(ValueError, match="not enough history"):
        run_walk_forward(cfg, small_market, grid={})


def test_walk_forward_aggregates_trading_statistics(small_cfg, small_market):
    """The out-of-sample summary must not claim a cost-free, trade-free strategy."""
    from quantbot.backtest.walkforward import run_walk_forward

    cfg = copy.deepcopy(small_cfg)
    cfg.backtest.rebalance = "BME"
    cfg.backtest.wf_train_days = 400
    cfg.backtest.wf_test_days = 150
    cfg.backtest.wf_step_days = 150

    result = run_walk_forward(cfg, small_market, grid={}, also_run_in_sample=False)

    assert result.oos_metrics.trades > 0
    assert result.oos_metrics.trades == sum(f.test_metrics.trades for f in result.folds)
    assert result.oos_metrics.turnover > 0
    # The aggregate must sit inside the range of the folds it came from.
    fold_turnovers = [f.test_metrics.turnover for f in result.folds]
    assert min(fold_turnovers) <= result.oos_metrics.turnover <= max(fold_turnovers)
