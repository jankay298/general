"""The backtest engine.

Event-driven over daily bars. The loop is short; the value is in what it refuses
to do.

**Signals are computed on the close of day t and filled at the open of day t+lag.**
That single rule is the difference between a backtest and a fantasy. Filling at
the same close the signal was computed from is the most common bug in
backtesting code, it is invisible in the output, and it inflates results enough
to make a random strategy look excellent.

**Costs are charged on every fill**, including the market-impact term, so a
strategy that only works at zero size shows it here rather than in production.

**Orders are capped at a share of average daily volume**, because a simulation
that buys 40% of a day's volume at the open has simulated a price that would not
have existed.

**The drawdown guard can flatten the book**, and the equity curve shows it. A
backtest that never exercises its own risk controls has not tested them.
"""

from __future__ import annotations

from dataclasses import dataclass, field
from typing import Dict, List, Optional, Set

import numpy as np
import pandas as pd

from ..config import Config
from ..data.repository import MarketData
from ..features.technical import atr
from ..portfolio import Portfolio
from ..risk.limits import DrawdownGuard, TrailingStops
from ..strategy.base import Strategy
from ..utils.logging import get_logger
from .costs import CostModel
from .metrics import Metrics, compute_metrics

log = get_logger(__name__)


@dataclass
class BacktestResult:
    """Everything a backtest produces. Serialisable, inspectable, comparable."""

    equity: pd.Series
    returns: pd.Series
    weights: pd.DataFrame
    targets: pd.DataFrame
    exposure: pd.DataFrame
    trades: pd.DataFrame
    metrics: Metrics
    benchmark: Optional[pd.Series] = None
    benchmark_metrics: Optional[Metrics] = None
    diagnostics: pd.DataFrame = field(default_factory=pd.DataFrame)
    guard_events: List[tuple] = field(default_factory=list)
    label: str = "strategy"
    meta: Dict = field(default_factory=dict)

    def summary(self) -> str:
        lines = [f"=== {self.label} ===", self.metrics.summary()]
        if self.benchmark_metrics is not None:
            lines.append(f"--- benchmark ({self.meta.get('benchmark_symbol', 'bench')}) ---")
            lines.append(self.benchmark_metrics.summary())
            excess = self.metrics.cagr - self.benchmark_metrics.cagr
            lines.append(f"excess CAGR vs benchmark : {excess:+.2%}")
        if self.guard_events:
            lines.append(f"drawdown guard fired {len(self.guard_events)}x")
        return "\n".join(lines)


class BacktestEngine:
    """Runs a strategy over historical bars with realistic frictions."""

    def __init__(self, cfg: Config, cost_model: Optional[CostModel] = None) -> None:
        self.cfg = cfg
        self.costs = cost_model or CostModel.from_config(cfg.backtest.costs)

    # ------------------------------------------------------------------- run

    def run(
        self,
        strategy: Strategy,
        data: MarketData,
        start: Optional[str] = None,
        end: Optional[str] = None,
        label: Optional[str] = None,
        prepare: bool = True,
    ) -> BacktestResult:
        cfg = self.cfg
        if prepare:
            strategy.prepare(data)

        index = data.close.index
        if start is not None:
            index = index[index >= pd.Timestamp(start)]
        if end is not None:
            index = index[index <= pd.Timestamp(end)]
        if len(index) < 2:
            raise ValueError("backtest window is too short (fewer than 2 bars)")

        portfolio = Portfolio(cfg.backtest.initial_capital)
        guard = DrawdownGuard(cfg.risk.drawdown_killswitch, cfg.risk.killswitch_cooldown_days)
        guard.high_water_mark = cfg.backtest.initial_capital
        stops = TrailingStops(cfg.risk.atr_stop_mult)

        # Liquidity and volatility context, precomputed once.
        adv_notional = (data.close * data.volume).rolling(21, min_periods=5).mean()
        adv_shares = data.volume.rolling(21, min_periods=5).mean()
        atr_panel = atr(data.high, data.low, data.close, cfg.features.atr_period)

        rebalance_days = set(strategy.rebalance_dates(index))
        lag = max(1, cfg.backtest.execution_lag_bars)

        pending: Dict[pd.Timestamp, Dict] = {}
        equity_curve: Dict[pd.Timestamp, float] = {}
        weight_rows: Dict[pd.Timestamp, pd.Series] = {}
        target_rows: Dict[pd.Timestamp, pd.Series] = {}
        exposure_rows: Dict[pd.Timestamp, Dict] = {}
        diagnostic_rows: List[Dict] = []

        for i, date in enumerate(index):
            open_prices = data.open.loc[date]
            close_prices = data.close.loc[date]

            # 1. Fill anything scheduled for this bar, at today's open.
            order = pending.pop(date, None)
            if order is not None:
                self._execute(
                    portfolio=portfolio,
                    targets=order.get("targets"),
                    exits=order.get("exits", set()),
                    prices=open_prices,
                    adv_notional=adv_notional.loc[date],
                    adv_shares=adv_shares.loc[date],
                    date=date,
                    reason=order.get("reason", "rebalance"),
                    tradable=data.traded.loc[date],
                )

            # 2. Mark to market and charge financing on any short exposure.
            equity = portfolio.equity(close_prices)
            weights_now = portfolio.weights(close_prices)
            short_notional = float((weights_now[weights_now < 0].sum()) * equity) if not weights_now.empty else 0.0
            if short_notional < 0:
                portfolio.charge(self.costs.borrow_cost(short_notional, days=1.0))
                equity = portfolio.equity(close_prices)

            equity_curve[date] = equity
            weight_rows[date] = weights_now
            exposure_rows[date] = portfolio.exposure(close_prices)

            if equity <= 0:
                log.error("account wiped out on %s — stopping the backtest", date.date())
                break

            # 3. Risk controls, evaluated on the close.
            blocked = guard.update(equity, date)
            stopped_out: Set[str] = set()
            if cfg.risk.atr_stop_mult > 0:
                stopped_out = set(
                    stops.update(portfolio.quantities, close_prices, atr_panel.loc[date], date)
                )

            if i + lag >= len(index):
                continue  # no future bar left to execute on
            execution_date = index[i + lag]

            # 4. Decide what to send for the next execution bar.
            if blocked:
                slot = pending.setdefault(execution_date, {})
                slot["targets"] = pd.Series(0.0, index=data.close.columns)
                slot["reason"] = "drawdown_guard"
            elif date in rebalance_days:
                # Hand the strategy the book as it stands so it can hold a name
                # that has slipped a rank instead of churning it.
                targets = strategy.target_weights(date, current=weights_now)
                target_rows[date] = targets
                slot = pending.setdefault(execution_date, {})
                slot["targets"] = targets
                slot.setdefault("reason", "rebalance")
                if strategy.last_diagnostics is not None:
                    diagnostic = strategy.last_diagnostics
                    diagnostic_rows.append(
                        {
                            "date": diagnostic.date,
                            "regime": diagnostic.regime,
                            "regime_score": diagnostic.regime_score,
                            "gross_target": diagnostic.gross_target,
                            "eligible": diagnostic.eligible,
                            "n_long": len(diagnostic.selected_long),
                            "n_short": len(diagnostic.selected_short),
                            "vol_scale": diagnostic.vol_scale,
                            "estimated_vol": diagnostic.estimated_vol,
                            "notes": "; ".join(diagnostic.notes),
                        }
                    )

            if stopped_out:
                slot = pending.setdefault(execution_date, {})
                slot.setdefault("exits", set()).update(stopped_out)
                slot.setdefault("reason", "stop_loss")

        equity = pd.Series(equity_curve).sort_index()
        returns = equity.pct_change(fill_method=None).fillna(0.0)

        benchmark = None
        benchmark_metrics = None
        if data.benchmark is not None and not data.benchmark.empty:
            benchmark = data.benchmark.reindex(equity.index).ffill()
            bench_returns = benchmark.pct_change(fill_method=None).fillna(0.0)
            benchmark_metrics = compute_metrics(bench_returns, risk_free=cfg.backtest.risk_free_rate)

        blotter = portfolio.blotter()
        metrics = compute_metrics(
            returns,
            risk_free=cfg.backtest.risk_free_rate,
            benchmark_returns=None if benchmark is None else benchmark.pct_change(fill_method=None).fillna(0.0),
            trades=blotter,
            equity=equity,
        )

        result = BacktestResult(
            equity=equity,
            returns=returns,
            weights=pd.DataFrame(weight_rows).T.reindex(equity.index).fillna(0.0),
            targets=pd.DataFrame(target_rows).T if target_rows else pd.DataFrame(),
            exposure=pd.DataFrame(exposure_rows).T,
            trades=blotter,
            metrics=metrics,
            benchmark=benchmark,
            benchmark_metrics=benchmark_metrics,
            diagnostics=pd.DataFrame(diagnostic_rows),
            guard_events=list(guard.events),
            label=label or strategy.name,
            meta={
                "symbols": list(data.close.columns),
                "start": str(equity.index.min().date()),
                "end": str(equity.index.max().date()),
                "benchmark_symbol": cfg.universe.benchmark,
                "initial_capital": cfg.backtest.initial_capital,
                "rebalance": cfg.backtest.rebalance,
                "execution_lag_bars": lag,
                "total_costs": float(portfolio.total_costs()),
            },
        )
        log.info(
            "%s: %s -> %s | final equity %s | %d trades | costs %s",
            result.label,
            result.meta["start"],
            result.meta["end"],
            f"{equity.iloc[-1]:,.0f}",
            len(blotter),
            f"{portfolio.total_costs():,.0f}",
        )
        return result

    # ------------------------------------------------------------- execution

    def _execute(
        self,
        portfolio: Portfolio,
        targets: Optional[pd.Series],
        exits: Set[str],
        prices: pd.Series,
        adv_notional: pd.Series,
        adv_shares: pd.Series,
        date: pd.Timestamp,
        reason: str,
        tradable: pd.Series,
    ) -> None:
        """Move the book toward *targets* at *prices*, respecting frictions."""
        cfg = self.cfg
        equity = portfolio.equity(prices)
        if equity <= 0:
            return

        current = portfolio.quantities
        desired: Dict[str, float] = {}

        if targets is not None:
            for symbol, weight in targets.items():
                price = prices.get(symbol)
                if price is None or not np.isfinite(price) or price <= 0:
                    continue
                desired[symbol] = (weight * equity) / price
            # Anything held but no longer in the target set goes to zero.
            for symbol in current.index:
                desired.setdefault(symbol, 0.0)
        else:
            desired = {s: current.get(s, 0.0) for s in current.index}

        for symbol in exits:
            desired[symbol] = 0.0

        for symbol, target_quantity in desired.items():
            price = prices.get(symbol)
            if price is None or not np.isfinite(price) or price <= 0:
                continue

            held = float(current.get(symbol, 0.0))
            delta = target_quantity - held
            if delta == 0:
                continue

            # A market that did not trade today cannot fill an order today. Closing
            # an existing position is allowed through, since a stop-out that has to
            # wait for a holiday to end is a worse model of reality than one that
            # fills at the carried price.
            is_exit = target_quantity == 0.0
            if not bool(tradable.get(symbol, False)) and not is_exit:
                continue

            # No-trade band. Two terms, and the relative one does the real work:
            # an absolute floor alone lets every volatility rescale nudge every
            # position just past it, so the book churns without the view changing.
            if not is_exit:
                band = max(
                    cfg.strategy.no_trade_band * equity,
                    cfg.strategy.no_trade_band_relative * abs(target_quantity * price),
                )
                if abs(delta * price) < band:
                    continue

            # Participation cap — never simulate a fill the market could not absorb.
            cap_shares = adv_shares.get(symbol)
            if (
                cfg.strategy.max_adv_participation > 0
                and cap_shares is not None
                and np.isfinite(cap_shares)
                and cap_shares > 0
            ):
                limit = cfg.strategy.max_adv_participation * float(cap_shares)
                if abs(delta) > limit:
                    delta = float(np.sign(delta) * limit)

            notional = abs(delta) * price
            if notional < cfg.execution.min_order_notional and not is_exit:
                continue

            adv = adv_notional.get(symbol)
            cost = self.costs.trade_cost(notional, float(adv) if adv and np.isfinite(adv) else None)
            portfolio.fill(date, symbol, delta, price, cost, reason=reason)
