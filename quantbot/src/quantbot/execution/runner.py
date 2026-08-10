"""The live trading loop.

One pass does this: refresh data, ask the strategy what the book should look
like, read what the book *actually* looks like from the broker, diff the two,
run the preflight checks, and only then send orders.

The order of those steps is the important part. Reading positions from the broker
rather than from local state means the bot recovers from a partial fill, a manual
trade, or a crash halfway through yesterday's rebalance — it simply sees the true
book and trades toward the target from there. A bot that trusts its own memory of
what it owns will eventually double a position and not notice.

Three safety properties, in order of how much money they save:

* **Dry run is the default.** Sending real orders requires ``mode: live`` in the
  config *and* an explicit confirmation at the call site. Neither alone is enough.
* **Preflight checks are blocking, not advisory.** Stale data, a blocked account,
  an oversized order, or a tripped drawdown guard stops the run. There is no
  "warn and continue" path, because the whole point is the times you are not
  watching.
* **Equity history persists.** The drawdown guard needs to know about the loss
  that started three weeks ago, which means surviving restarts.
"""

from __future__ import annotations

import json
from dataclasses import dataclass, field
from datetime import datetime, timezone
from typing import List, Optional, Tuple

import numpy as np
import pandas as pd

from ..config import Config
from ..data.repository import MarketData, load_market_data
from ..risk.limits import DrawdownGuard
from ..strategy import build_strategy
from ..strategy.base import Strategy
from ..utils.logging import get_logger
from .broker import Account, Broker, Order, OrderResult, orders_from_weights

log = get_logger(__name__)


@dataclass
class Check:
    name: str
    passed: bool
    message: str
    blocking: bool = True

    def render(self) -> str:
        mark = "PASS" if self.passed else ("BLOCK" if self.blocking else "WARN")
        return f"[{mark:5}] {self.name}: {self.message}"


@dataclass
class TradePlan:
    """What the bot intends to do, and whether it is allowed to."""

    as_of: pd.Timestamp
    account: Optional[Account]
    targets: pd.Series
    current_weights: pd.Series
    current_positions: pd.Series
    prices: pd.Series
    orders: List[Order] = field(default_factory=list)
    checks: List[Check] = field(default_factory=list)
    diagnostics: Optional[object] = None
    mode: str = "dry_run"
    broker_name: str = "paper"

    @property
    def blocked(self) -> bool:
        return any(c.blocking and not c.passed for c in self.checks)

    @property
    def block_reasons(self) -> List[str]:
        return [c.message for c in self.checks if c.blocking and not c.passed]

    def total_notional(self) -> float:
        return float(
            sum(o.notional(self.prices.get(o.symbol, 0.0) or 0.0) for o in self.orders)
        )

    def render(self) -> str:
        lines = [
            f"=== trade plan {pd.Timestamp(self.as_of).date()} "
            f"({self.broker_name}, mode={self.mode}) ===",
        ]
        if self.account:
            lines.append(
                f"account: equity {self.account.equity:,.2f} {self.account.currency} | "
                f"cash {self.account.cash:,.2f}"
            )
        if self.diagnostics is not None and hasattr(self.diagnostics, "summary"):
            lines.append(self.diagnostics.summary())

        lines.append("")
        lines.append("checks:")
        lines.extend("  " + c.render() for c in self.checks)

        lines.append("")
        if not self.orders:
            lines.append("orders: none — the book already matches the target")
        else:
            lines.append(f"orders ({len(self.orders)}, {self.total_notional():,.2f} notional):")
            for order in self.orders:
                lines.append("  " + order.describe(self.prices.get(order.symbol)))

        held = self.current_weights[self.current_weights.abs() > 1e-6]
        wanted = self.targets[self.targets.abs() > 1e-6]
        if not wanted.empty:
            lines.append("")
            lines.append("target book:")
            for symbol, weight in wanted.sort_values(ascending=False).items():
                now = held.get(symbol, 0.0)
                lines.append(f"  {symbol:<8} {now:>7.2%} -> {weight:>7.2%}")
        return "\n".join(lines)


class LiveRunner:
    """Drives one live (or dry-run) trading pass."""

    def __init__(
        self,
        cfg: Config,
        broker: Optional[Broker] = None,
        strategy: Optional[Strategy] = None,
    ) -> None:
        self.cfg = cfg
        self.broker = broker or self._default_broker(cfg)
        self.strategy = strategy or build_strategy(cfg.strategy.name, cfg)
        self.state_dir = cfg.resolve(cfg.execution.state_file).parent
        self.equity_file = self.state_dir / "equity_history.json"
        self.guard = DrawdownGuard(
            cfg.risk.drawdown_killswitch, cfg.risk.killswitch_cooldown_days
        )
        self._load_guard_state()

    @staticmethod
    def _default_broker(cfg: Config) -> Broker:
        from . import build_broker

        return build_broker(cfg.execution.broker, cfg)

    # -------------------------------------------------------------- persistence

    def _load_guard_state(self) -> None:
        if not self.equity_file.is_file():
            return
        try:
            state = json.loads(self.equity_file.read_text(encoding="utf-8"))
        except Exception as exc:
            log.warning("could not read equity history (%s) — starting fresh", exc)
            return
        self.guard.high_water_mark = float(state.get("high_water_mark", 0.0))
        self.guard.bars_since_trigger = int(state.get("bars_since_trigger", 0))
        triggered = state.get("triggered_at")
        self.guard.triggered_at = pd.Timestamp(triggered) if triggered else None
        log.debug(
            "drawdown guard restored: high-water %.2f, active=%s",
            self.guard.high_water_mark, self.guard.active,
        )

    def _save_guard_state(self, equity: float) -> None:
        self.state_dir.mkdir(parents=True, exist_ok=True)
        history = []
        if self.equity_file.is_file():
            try:
                history = json.loads(self.equity_file.read_text(encoding="utf-8")).get("history", [])
            except Exception:
                history = []
        history.append({"timestamp": datetime.now(timezone.utc).isoformat(), "equity": equity})
        history = history[-2000:]  # a few years of daily points is plenty

        payload = {
            "high_water_mark": self.guard.high_water_mark,
            "triggered_at": str(self.guard.triggered_at) if self.guard.triggered_at else None,
            "bars_since_trigger": self.guard.bars_since_trigger,
            "history": history,
        }
        temp = self.equity_file.with_suffix(".json.tmp")
        temp.write_text(json.dumps(payload, indent=2), encoding="utf-8")
        temp.replace(self.equity_file)

    # -------------------------------------------------------------------- plan

    def build_plan(self, data: Optional[MarketData] = None) -> TradePlan:
        """Work out what to trade, and check whether it is safe to."""
        cfg = self.cfg
        checks: List[Check] = []

        if data is None:
            # Live mode: snapshot fundamentals and current headlines are correct
            # here, which is exactly why the backtest path refuses to load them.
            data = load_market_data(cfg, for_backtest=False)

        as_of = data.close.index[-1]
        prices = data.close.iloc[-1]

        age_hours = (pd.Timestamp.utcnow().tz_localize(None) - as_of).total_seconds() / 3600.0
        checks.append(
            Check(
                "data freshness",
                age_hours <= cfg.execution.max_data_age_hours,
                f"latest bar {as_of.date()} is {age_hours:.1f}h old "
                f"(limit {cfg.execution.max_data_age_hours:.0f}h)",
            )
        )

        healthy, message = self.broker.health_check()
        checks.append(Check("broker", healthy, message))

        account = None
        current_positions = pd.Series(dtype=float)
        try:
            account = self.broker.account()
            current_positions = self.broker.positions()
        except Exception as exc:
            checks.append(Check("account", False, f"could not read the account: {exc}"))

        market_open = self.broker.is_market_open()
        checks.append(
            Check("market hours", market_open,
                  "market is open" if market_open else "market is closed",
                  blocking=False)
        )

        equity = account.equity if account else 0.0
        current_weights = pd.Series(dtype=float)
        if equity > 0 and not current_positions.empty:
            values = current_positions * prices.reindex(current_positions.index)
            current_weights = (values / equity).dropna()

        blocked_by_drawdown = False
        if equity > 0:
            if self.guard.high_water_mark <= 0:
                self.guard.high_water_mark = equity
            blocked_by_drawdown = self.guard.update(equity, as_of)
            self._save_guard_state(equity)
        checks.append(
            Check(
                "drawdown guard",
                not blocked_by_drawdown,
                f"flat until the cooldown expires "
                f"({self.guard.cooldown_bars - self.guard.bars_since_trigger} runs left)"
                if blocked_by_drawdown
                else f"equity {equity:,.2f} vs high-water {self.guard.high_water_mark:,.2f}",
            )
        )

        self.strategy.prepare(data)
        if blocked_by_drawdown:
            targets = pd.Series(0.0, index=data.close.columns)
        else:
            targets = self.strategy.target_weights(as_of, current=current_weights)

        orders = orders_from_weights(
            target_weights=targets,
            current_positions=current_positions,
            prices=prices,
            equity=equity,
            min_notional=cfg.execution.min_order_notional,
            no_trade_band=cfg.strategy.no_trade_band,
            no_trade_band_relative=cfg.strategy.no_trade_band_relative,
            max_order_pct_equity=cfg.execution.max_order_pct_equity,
        )

        # Sanity on the aggregate, not just each order: a broken price feed
        # produces a set of individually-plausible orders that together are absurd.
        total = sum(o.notional(prices.get(o.symbol, 0.0) or 0.0) for o in orders)
        sane_total = equity <= 0 or total <= 2.0 * equity
        checks.append(
            Check(
                "order size",
                sane_total,
                f"{len(orders)} order(s), {total:,.2f} notional "
                f"({total / equity:.0%} of equity)" if equity > 0 else "no equity to size against",
            )
        )

        missing_prices = [o.symbol for o in orders if not np.isfinite(prices.get(o.symbol, np.nan))]
        checks.append(
            Check("prices", not missing_prices,
                  "all order symbols priced" if not missing_prices
                  else f"no price for {', '.join(missing_prices)}")
        )

        plan = TradePlan(
            as_of=as_of,
            account=account,
            targets=targets,
            current_weights=current_weights,
            current_positions=current_positions,
            prices=prices,
            orders=orders,
            checks=checks,
            diagnostics=self.strategy.last_diagnostics,
            mode=cfg.execution.mode,
            broker_name=self.broker.name,
        )
        return plan

    # ----------------------------------------------------------------- execute

    def execute(self, plan: TradePlan, confirm: bool = False) -> List[OrderResult]:
        """Send the plan's orders. Requires live mode *and* an explicit confirm."""
        cfg = self.cfg

        if plan.blocked:
            log.error("refusing to trade — %s", "; ".join(plan.block_reasons))
            return []

        if cfg.execution.mode != "live":
            log.info(
                "dry run: %d order(s) not sent. Set execution.mode: live and pass "
                "confirm=True to trade for real.",
                len(plan.orders),
            )
            return []

        if not confirm:
            log.error(
                "execution.mode is 'live' but the caller did not confirm. Both are "
                "required — this is the last guard between a config typo and real orders."
            )
            return []

        if not plan.orders:
            log.info("nothing to do — the book already matches the target")
            return []

        if self.broker.is_live:
            log.warning(
                "sending %d order(s) to a LIVE account (%s), %.2f notional",
                len(plan.orders), self.broker.name, plan.total_notional(),
            )

        # Mark the paper account with current prices so its equity is right.
        if hasattr(self.broker, "mark"):
            self.broker.mark(plan.prices)

        results = self.broker.submit_all(plan.orders, plan.prices)
        for result in results:
            level = log.info if result.accepted else log.error
            level("%s -> %s (%s)", result.order.describe(), result.status, result.message)

        rejected = sum(1 for r in results if not r.accepted)
        if rejected:
            log.warning("%d/%d order(s) were rejected", rejected, len(results))
        return results

    def run_once(self, confirm: bool = False) -> Tuple[TradePlan, List[OrderResult]]:
        """Build a plan, log it, and execute it if allowed."""
        plan = self.build_plan()
        print(plan.render())
        results = self.execute(plan, confirm=confirm)
        return plan, results
