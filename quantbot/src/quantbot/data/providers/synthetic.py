"""Deterministic synthetic market data.

This is not a toy stub — it is what makes the rest of the bot testable. Free APIs
go down, rate-limit, or are blocked outright from CI and sandboxes; a backtest
engine you cannot exercise is a backtest engine you cannot trust.

The generator produces a market with the properties a factor strategy actually
has to cope with:

* one common market factor plus per-symbol betas, so cross-sectional ranking has
  something to rank and diversification behaves realistically;
* two-state Markov volatility, so calm periods and crises alternate and the
  regime overlay has real regimes to detect;
* Student-t innovations, so tails are fat and a Gaussian risk model underestimates
  drawdowns exactly the way it does in reality;
* slow-moving per-symbol drift, so momentum is present but decays — a strategy
  that only works here is not proven, but one that fails here is broken.

Everything derives from ``seed`` and the symbol name, so two runs produce
byte-identical data.
"""

from __future__ import annotations

import hashlib
from typing import Dict, Optional

import numpy as np
import pandas as pd

from ..base import PriceProvider

_TRADING_DAYS = 252


def _symbol_seed(symbol: str, seed: int) -> int:
    digest = hashlib.sha256(f"{symbol}:{seed}".encode("utf-8")).digest()
    return int.from_bytes(digest[:4], "big")


class SyntheticProvider(PriceProvider):
    name = "synthetic"
    adjusted = True

    def __init__(
        self,
        seed: int = 7,
        annual_drift: float = 0.06,
        annual_vol: float = 0.18,
        crisis_vol_multiplier: float = 2.6,
    ) -> None:
        self.seed = int(seed)
        self.annual_drift = annual_drift
        self.annual_vol = annual_vol
        self.crisis_vol_multiplier = crisis_vol_multiplier
        self._market_cache: Dict[tuple, tuple[pd.DatetimeIndex, np.ndarray, np.ndarray]] = {}

    # ------------------------------------------------------------ market factor

    def _market(self, start: str, end: Optional[str]):
        """Market returns and the volatility state path, shared by every symbol."""
        key = (start, end)
        if key in self._market_cache:
            return self._market_cache[key]

        dates = pd.bdate_range(start=start, end=end or pd.Timestamp.today().normalize())
        n = len(dates)
        rng = np.random.default_rng(self.seed)

        # Two-state Markov vol: calm and crisis, with sticky transitions.
        p_calm_to_crisis, p_crisis_to_calm = 0.004, 0.045
        state = np.zeros(n, dtype=int)
        for i in range(1, n):
            flip = rng.random()
            if state[i - 1] == 0:
                state[i] = 1 if flip < p_calm_to_crisis else 0
            else:
                state[i] = 0 if flip < p_crisis_to_calm else 1

        daily_vol = self.annual_vol / np.sqrt(_TRADING_DAYS)
        vol_path = daily_vol * np.where(state == 1, self.crisis_vol_multiplier, 1.0)
        # Crises fall, calm periods grind up — that asymmetry is what makes a
        # regime overlay worth having in the first place.
        drift_path = np.where(
            state == 1,
            -1.8 * self.annual_drift / _TRADING_DAYS,
            self.annual_drift / _TRADING_DAYS,
        )
        shocks = rng.standard_t(df=4, size=n) / np.sqrt(4 / 2)  # unit variance, fat tails
        market = drift_path + vol_path * shocks

        self._market_cache[key] = (dates, market, state)
        return self._market_cache[key]

    # ------------------------------------------------------------------ fetch

    def fetch(self, symbol: str, start: str, end: Optional[str]) -> Optional[pd.DataFrame]:
        dates, market, state = self._market(start, end)
        n = len(dates)
        if n < 2:
            return None

        rng = np.random.default_rng(_symbol_seed(symbol, self.seed))

        beta = float(np.clip(rng.normal(1.0, 0.35), 0.15, 2.2))
        idio_vol = float(np.clip(rng.normal(0.20, 0.07), 0.06, 0.55)) / np.sqrt(_TRADING_DAYS)
        # A small permanent skill spread — about +/-1.5% a year, one sigma.
        alpha = rng.normal(0.0, 0.015) / _TRADING_DAYS

        # A slowly mean-reverting drift component gives momentum a real but
        # decaying edge, instead of a permanent per-symbol constant that any
        # ranking model would trivially discover.
        #
        # drift_vol is set from the target *stationary* spread, not used raw: an
        # Ornstein-Uhlenbeck process with parameters (theta, sigma) settles at a
        # standard deviation of sigma/sqrt(2*theta), so passing the annual figure
        # in directly would produce a cross-sectional drift spread of roughly 85%
        # a year and make momentum a free lunch that no real market offers.
        theta = 1.0 / 180.0
        drift_spread_annual = 0.05
        drift_vol = (drift_spread_annual / _TRADING_DAYS) * np.sqrt(2.0 * theta)
        drift = np.empty(n)
        drift[0] = 0.0
        for i in range(1, n):
            drift[i] = drift[i - 1] * (1 - theta) + drift_vol * rng.standard_normal()

        idio = idio_vol * rng.standard_t(df=5, size=n) / np.sqrt(5 / 3)
        returns = alpha + drift + beta * market + idio
        returns = np.clip(returns, -0.35, 0.35)  # keep limit-down days survivable

        start_price = float(np.exp(rng.normal(np.log(60.0), 0.7)))
        close = start_price * np.exp(np.cumsum(returns))

        # Build a plausible bar around each close: range scales with that day's vol.
        bar_vol = np.abs(beta) * np.abs(market) + idio_vol
        prev_close = np.concatenate([[start_price], close[:-1]])
        open_ = prev_close * (1.0 + 0.35 * bar_vol * rng.standard_normal(n))
        span = close * bar_vol * (0.8 + 0.9 * rng.random(n))
        high = np.maximum(open_, close) + span * rng.random(n)
        low = np.minimum(open_, close) - span * rng.random(n)
        low = np.maximum(low, 0.01)

        base_volume = float(np.exp(rng.normal(13.8, 1.1)))
        # Volume spikes on big moves, the way it does in real tape.
        volume = base_volume * np.exp(rng.normal(0, 0.35, n)) * (1 + 6 * np.abs(returns))

        return pd.DataFrame(
            {
                "open": open_,
                "high": high,
                "low": low,
                "close": close,
                "volume": np.round(volume),
            },
            index=dates,
        )

    # --------------------------------------------------------------- extras

    def macro_state(self, start: str, end: Optional[str]) -> pd.Series:
        """The true regime path (0 calm, 1 crisis) — for validating the detector."""
        dates, _, state = self._market(start, end)
        return pd.Series(state, index=dates, name="true_state")
