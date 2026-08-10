"""Baseline strategies.

These exist so the composite strategy has something honest to be compared
against. A backtest that reports "18% CAGR" without saying what equal-weighting
the same universe would have returned is not evidence of anything; most of the
time the answer is that the market did the work.

Both baselines run through the same engine, the same costs and the same
execution lag as the real strategy, so the comparison is like for like.
"""

from __future__ import annotations

from typing import Optional

import pandas as pd

from ..data.repository import MarketData
from ..features.technical import compute_technicals
from ..risk.limits import apply_limits
from ..utils.logging import get_logger
from .base import Strategy, StrategyDiagnostics

log = get_logger(__name__)


class BuyAndHoldStrategy(Strategy):
    """Equal-weight the tradable universe and rebalance on schedule.

    The benchmark that matters. If the clever strategy cannot beat this after
    costs, it is an expensive way to own the market.
    """

    name = "buy_and_hold"

    def prepare(self, data: MarketData) -> None:
        self.data = data
        self._prepared = True

    def target_weights(
        self, date: pd.Timestamp, current: Optional[pd.Series] = None
    ) -> pd.Series:
        self.require_prepared()
        date = pd.Timestamp(date)
        symbols = list(self.data.close.columns)

        history = self.data.close.loc[:date]
        if history.empty:
            return pd.Series(0.0, index=symbols)

        tradable = history.iloc[-1].notna() & self.data.traded.loc[:date].tail(5).any()
        names = tradable[tradable].index
        weights = pd.Series(0.0, index=symbols)
        if len(names) == 0:
            return weights

        weights.loc[names] = self.cfg.risk.max_gross / len(names)
        weights = apply_limits(weights, self.cfg.risk, self.cfg.universe.sectors)
        self.last_diagnostics = StrategyDiagnostics(
            date=date, gross_target=self.cfg.risk.max_gross,
            eligible=len(names), selected_long=list(names),
        )
        return weights


class TrendFollowingStrategy(Strategy):
    """Hold what is above its long moving average, sized by inverse volatility.

    A single-factor control. If the multi-factor composite does not beat plain
    trend following, the extra factors are decoration and should be dropped —
    every additional input is another way to overfit.
    """

    name = "trend"

    def prepare(self, data: MarketData) -> None:
        self.data = data
        self.tech = compute_technicals(data, self.cfg.features)
        slow = self.cfg.features.trend_slow
        moving_average = data.close.rolling(slow, min_periods=slow // 2).mean()
        self.above = data.close > moving_average
        self._prepared = True

    def target_weights(
        self, date: pd.Timestamp, current: Optional[pd.Series] = None
    ) -> pd.Series:
        self.require_prepared()
        date = pd.Timestamp(date)
        symbols = list(self.data.close.columns)
        weights = pd.Series(0.0, index=symbols)

        window = self.above.loc[:date]
        if window.empty:
            return weights

        signal = window.iloc[-1]
        history_ok = self.data.close.loc[:date].notna().sum() >= self.cfg.data.min_history_bars
        recently_traded = self.data.traded.loc[:date].tail(5).any()
        names = signal[signal & history_ok & recently_traded].index
        if len(names) == 0:
            return weights

        vol = self.tech.vol.loc[:date].iloc[-1].reindex(names)
        vol = vol.fillna(vol.median() if vol.notna().any() else 0.20).clip(0.05, 1.5)
        inverse = 1.0 / vol
        weights.loc[names] = (inverse / inverse.sum()) * self.cfg.risk.max_gross

        weights = apply_limits(weights, self.cfg.risk, self.cfg.universe.sectors)
        self.last_diagnostics = StrategyDiagnostics(
            date=date, eligible=int((history_ok & recently_traded).sum()),
            selected_long=list(names), gross_target=self.cfg.risk.max_gross,
        )
        return weights
