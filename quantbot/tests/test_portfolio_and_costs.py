"""Accounting and cost model.

Bookkeeping bugs are silent: the equity curve stays plausible while cash quietly
appears or vanishes. These tests assert conservation, which is the only property
that catches that class of bug.
"""

from __future__ import annotations

import pandas as pd
import pytest

from quantbot.backtest.costs import CostModel
from quantbot.portfolio import Portfolio


def test_cash_is_conserved_across_a_round_trip():
    portfolio = Portfolio(10_000.0)
    prices = pd.Series({"AAA": 100.0})

    portfolio.fill(pd.Timestamp("2024-01-02"), "AAA", 50, 100.0, cost=5.0)
    assert portfolio.cash == pytest.approx(10_000.0 - 5_000.0 - 5.0)
    assert portfolio.equity(prices) == pytest.approx(9_995.0)

    portfolio.fill(pd.Timestamp("2024-01-03"), "AAA", -50, 110.0, cost=5.5)
    assert portfolio.positions["AAA"].quantity == 0
    # Sold 50 at 110 = 5500 in, minus 5.50 costs.
    assert portfolio.cash == pytest.approx(10_000.0 - 5_000.0 - 5.0 + 5_500.0 - 5.5)
    assert portfolio.realised_pnl() == pytest.approx(500.0)
    assert portfolio.total_costs() == pytest.approx(10.5)


def test_average_price_tracks_additions_not_reductions():
    portfolio = Portfolio(100_000.0)
    portfolio.fill(pd.Timestamp("2024-01-02"), "AAA", 100, 10.0)
    portfolio.fill(pd.Timestamp("2024-01-03"), "AAA", 100, 20.0)
    assert portfolio.positions["AAA"].average_price == pytest.approx(15.0)

    # Trimming must not move the cost basis of what remains.
    portfolio.fill(pd.Timestamp("2024-01-04"), "AAA", -50, 30.0)
    assert portfolio.positions["AAA"].average_price == pytest.approx(15.0)
    assert portfolio.positions["AAA"].quantity == pytest.approx(150.0)
    assert portfolio.realised_pnl() == pytest.approx(50 * (30.0 - 15.0))


def test_flipping_through_zero_resets_the_basis():
    portfolio = Portfolio(100_000.0)
    portfolio.fill(pd.Timestamp("2024-01-02"), "AAA", 100, 10.0)
    portfolio.fill(pd.Timestamp("2024-01-03"), "AAA", -150, 12.0)
    position = portfolio.positions["AAA"]
    assert position.quantity == pytest.approx(-50.0)
    assert position.average_price == pytest.approx(12.0)
    assert position.realised == pytest.approx(100 * (12.0 - 10.0))


def test_short_position_values_negatively():
    portfolio = Portfolio(10_000.0)
    portfolio.fill(pd.Timestamp("2024-01-02"), "AAA", -20, 50.0)
    prices = pd.Series({"AAA": 50.0})
    assert portfolio.cash == pytest.approx(11_000.0)
    assert portfolio.market_value(prices) == pytest.approx(-1_000.0)
    assert portfolio.equity(prices) == pytest.approx(10_000.0)

    weights = portfolio.weights(prices)
    assert weights["AAA"] == pytest.approx(-0.1)
    assert portfolio.exposure(prices)["gross"] == pytest.approx(0.1)
    assert portfolio.exposure(prices)["net"] == pytest.approx(-0.1)


def test_zero_and_invalid_fills_are_ignored():
    portfolio = Portfolio(1_000.0)
    assert portfolio.fill(pd.Timestamp("2024-01-02"), "AAA", 0, 10.0) is None
    assert portfolio.fill(pd.Timestamp("2024-01-02"), "AAA", 10, float("nan")) is None
    assert portfolio.fill(pd.Timestamp("2024-01-02"), "AAA", 10, -5.0) is None
    assert portfolio.cash == 1_000.0
    assert portfolio.trades == []


def test_whole_share_mode_truncates():
    portfolio = Portfolio(1_000.0, allow_fractional=False)
    portfolio.fill(pd.Timestamp("2024-01-02"), "AAA", 10.9, 10.0)
    assert portfolio.positions["AAA"].quantity == 10.0
    assert portfolio.fill(pd.Timestamp("2024-01-02"), "BBB", 0.5, 10.0) is None


# --------------------------------------------------------------------------- #
# Costs
# --------------------------------------------------------------------------- #


def test_commission_and_spread_are_linear_in_notional():
    model = CostModel(commission_bps=1.0, spread_bps=2.0, impact_coeff_bps=0.0)
    assert model.trade_cost(10_000.0) == pytest.approx(10_000.0 * 3e-4)
    assert model.trade_cost(20_000.0) == pytest.approx(2 * model.trade_cost(10_000.0))


def test_market_impact_grows_with_participation_but_sublinearly():
    model = CostModel(commission_bps=0.0, spread_bps=0.0, impact_coeff_bps=10.0)
    adv = 1_000_000.0
    small = model.trade_cost(10_000.0, adv)
    large = model.trade_cost(40_000.0, adv)
    # 4x the size at 4x participation costs 8x, not 4x (linear) and not 16x.
    assert large == pytest.approx(8 * small, rel=1e-6)
    # Cost per unit traded rises with size — this is what caps capacity.
    assert large / 40_000.0 > small / 10_000.0


def test_impact_is_skipped_without_a_volume_estimate():
    model = CostModel(commission_bps=1.0, spread_bps=0.0, impact_coeff_bps=50.0)
    assert model.trade_cost(10_000.0, None) == pytest.approx(1.0)
    assert model.trade_cost(10_000.0, 0.0) == pytest.approx(1.0)


def test_effective_price_moves_against_the_trader():
    model = CostModel(commission_bps=1.0, spread_bps=5.0, impact_coeff_bps=0.0)
    buy = model.effective_price(100.0, quantity=100)
    sell = model.effective_price(100.0, quantity=-100)
    assert buy > 100.0 > sell
    assert buy - 100.0 == pytest.approx(100.0 - sell)


def test_borrow_cost_only_applies_to_shorts():
    model = CostModel(short_borrow_bps=100.0)
    assert model.borrow_cost(50_000.0) == 0.0
    charge = model.borrow_cost(-50_000.0, days=1.0)
    assert charge == pytest.approx(50_000.0 * 1e-2 / 252.0)
    assert model.borrow_cost(-50_000.0, days=5.0) == pytest.approx(5 * charge)
