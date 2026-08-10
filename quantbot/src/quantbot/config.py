"""Typed configuration for the whole bot.

One YAML file drives the data layer, the strategy, the risk limits, the
backtester and the live runner. That is deliberate: a backtest whose parameters
can drift away from the live loop's parameters is worse than no backtest at all.

Loading is tolerant on structure (unknown keys raise, missing keys fall back to
the dataclass defaults) so a short YAML file can override two parameters without
restating the other eighty.
"""

from __future__ import annotations

import dataclasses
import os
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any, Dict, List, Optional, get_type_hints

import yaml

# --------------------------------------------------------------------------- #
# Sections
# --------------------------------------------------------------------------- #


@dataclass
class RunConfig:
    name: str = "default"
    cache_dir: str = ".cache"
    output_dir: str = "output"
    log_level: str = "INFO"
    random_seed: int = 7


@dataclass
class UniverseConfig:
    """What we are allowed to trade.

    ``symbols`` is the tradable universe; ``benchmark`` is only ever used for
    reporting and never enters a signal.
    """

    name: str = "demo"
    symbols: List[str] = field(default_factory=list)
    benchmark: str = "SPY"
    #: Optional per-symbol sector labels, used by the sector exposure limit.
    sectors: Dict[str, str] = field(default_factory=dict)
    #: Asset class per symbol ("equity", "etf", "crypto", "fx", "commodity").
    #: Used to pick a cost model and a trading calendar.
    asset_classes: Dict[str, str] = field(default_factory=dict)


@dataclass
class DataConfig:
    #: Tried in order; the first provider that returns usable bars wins.
    price_providers: List[str] = field(
        default_factory=lambda: ["yahoo", "stooq", "alphavantage", "csv", "synthetic"]
    )
    start: str = "2015-01-01"
    end: Optional[str] = None
    cache_ttl_hours: float = 12.0
    #: Directory scanned by the ``csv`` provider, one ``<SYMBOL>.csv`` per symbol.
    csv_dir: str = "data/prices"
    alphavantage_key_env: str = "ALPHAVANTAGE_API_KEY"
    #: Fundamentals and news are optional: without them the strategy simply
    #: renormalises the remaining factor weights.
    use_fundamentals: bool = True
    use_news: bool = True
    use_macro: bool = True
    #: FRED series id -> friendly name. No API key needed for the CSV endpoint.
    macro_series: Dict[str, str] = field(
        default_factory=lambda: {
            "DGS10": "yield_10y",
            "DGS2": "yield_2y",
            "T10Y2Y": "curve_10y2y",
            "VIXCLS": "vix",
            "BAMLH0A0HYM2": "hy_oas",
            "DTWEXBGS": "usd_index",
            "UNRATE": "unemployment",
            "T10YIE": "breakeven_10y",
            "DCOILWTICO": "wti",
        }
    )
    news_feeds: List[str] = field(
        default_factory=lambda: [
            "https://feeds.content.dowjones.io/public/rss/mw_topstories",
            "https://feeds.content.dowjones.io/public/rss/mw_marketpulse",
            "https://www.ecb.europa.eu/rss/press.html",
            "https://www.federalreserve.gov/feeds/press_all.xml",
            "https://rss.politico.com/economy.xml",
        ]
    )
    news_lookback_days: int = 7
    #: Minimum number of bars a symbol needs before it may be traded at all.
    min_history_bars: int = 260


@dataclass
class FeatureConfig:
    trend_fast: int = 50
    trend_slow: int = 200
    momentum_lookback: int = 252
    #: Skip the most recent month — classic 12-1 momentum, avoids short-term reversal.
    momentum_skip: int = 21
    reversal_lookback: int = 5
    rsi_period: int = 14
    atr_period: int = 14
    vol_lookback: int = 63
    donchian_period: int = 55
    #: Half-life in days for decaying news sentiment.
    sentiment_halflife: float = 3.0
    #: Winsorise cross-sectional z-scores at +/- this many sigma.
    zscore_clip: float = 3.0


@dataclass
class RegimeConfig:
    """Macro regime classification thresholds.

    The regime is a composite z-score of macro stress indicators. Positive means
    stress (risk-off). The two thresholds carve the score into three states.
    """

    lookback_days: int = 756  # ~3y of daily macro history for the z-scores
    risk_off_threshold: float = 0.75
    risk_on_threshold: float = -0.35
    #: How far past a threshold the score must go before the regime is allowed to
    #: change back. Entering uses the threshold; leaving needs this much margin.
    hysteresis_band: float = 0.35
    #: Minimum bars to stay in a regime once entered. Financial conditions do not
    #: genuinely flip every week, and a label that does is measuring noise — and
    #: charging the portfolio real turnover for the privilege.
    min_dwell_bars: int = 21
    #: How much gross exposure each regime is allowed to run.
    gross_exposure: Dict[str, float] = field(
        default_factory=lambda: {"risk_on": 1.00, "neutral": 0.70, "risk_off": 0.35}
    )
    #: Factor weights per regime. Renormalised over the factors actually available.
    factor_weights: Dict[str, Dict[str, float]] = field(
        default_factory=lambda: {
            # In a calm tape, ride the trend. Under stress, pay for quality and
            # fade the panic. Weights are renormalised over whichever factors
            # actually have data, so a missing feed shifts emphasis rather than
            # silently shrinking the book.
            "risk_on": {
                "trend": 0.35,
                "momentum": 0.18,
                "value": 0.12,
                "quality": 0.08,
                "growth": 0.12,
                "sentiment": 0.10,
                "reversal": 0.05,
            },
            "neutral": {
                "trend": 0.25,
                "momentum": 0.15,
                "value": 0.18,
                "quality": 0.18,
                "growth": 0.07,
                "sentiment": 0.07,
                "reversal": 0.10,
            },
            "risk_off": {
                "trend": 0.12,
                "momentum": 0.05,
                "value": 0.10,
                "quality": 0.35,
                "growth": 0.03,
                "sentiment": 0.10,
                "reversal": 0.25,
            },
        }
    )


@dataclass
class StrategyConfig:
    name: str = "composite"
    long_only: bool = True
    #: Fraction of the ranked universe held long / shorted.
    long_quantile: float = 0.30
    short_quantile: float = 0.30
    max_positions: int = 25
    min_positions: int = 5
    #: Weight positions by 1/vol instead of by score alone.
    inverse_vol_weighting: bool = True
    #: A symbol must clear this composite z-score to be bought at all.
    min_score: float = 0.0
    #: Skip rebalancing a name whose target moved less than this share of equity.
    no_trade_band: float = 0.005
    #: ...or less than this share of its own target position. Without the relative
    #: term, every volatility-target rescale nudges every position past the
    #: absolute floor and the whole book churns for no change in view.
    no_trade_band_relative: float = 0.25
    #: Ignore volatility-target rescales smaller than this. The vol estimate moves
    #: a little every day; acting on each move is pure turnover.
    vol_scale_deadband: float = 0.10
    #: Hysteresis on selection: an existing holding is kept while it stays inside
    #: the top ``(1 + buffer) * n`` names, instead of being sold the moment it slips
    #: to rank n+1. Names near the cutoff otherwise flip in and out every
    #: rebalance, which is most of a factor strategy's turnover and none of its
    #: return. 0 disables buffering.
    selection_buffer: float = 0.5
    #: Minimum 21-day average traded notional for a symbol to be eligible.
    #: 0 disables the filter; raise it for small-cap universes.
    min_dollar_volume: float = 0.0
    #: Cap a position at this share of the symbol's average daily volume, so the
    #: backtest cannot fill a size the real market would never absorb.
    max_adv_participation: float = 0.10


@dataclass
class RiskConfig:
    #: Annualised volatility the portfolio is scaled towards.
    target_vol: float = 0.12
    vol_lookback: int = 63
    #: Never scale the book up by more than this, whatever the vol estimate says.
    max_leverage: float = 1.0
    max_weight: float = 0.10
    max_sector_weight: float = 0.35
    max_gross: float = 1.0
    max_net: float = 1.0
    #: Flatten everything if the equity curve falls this far below its high-water mark.
    drawdown_killswitch: float = 0.25
    #: Days to stay flat after the kill switch fires.
    killswitch_cooldown_days: int = 10
    #: Per-position trailing stop, in ATR multiples. 0 disables it.
    atr_stop_mult: float = 4.0


@dataclass
class CostConfig:
    commission_bps: float = 1.0
    #: Half the bid/ask spread, paid on every trade.
    spread_bps: float = 2.0
    #: Market impact = coeff * sqrt(trade_value / adv). Set 0 for a naive model.
    impact_coeff_bps: float = 10.0
    #: Borrow cost charged on short positions, annualised.
    short_borrow_bps: float = 50.0


@dataclass
class BacktestConfig:
    initial_capital: float = 100_000.0
    #: pandas offset alias: "B" daily, "W-WED" weekly, "BME" monthly.
    rebalance: str = "W-WED"
    #: Bars between signal computation and fill. 1 = decide on close, fill next open.
    execution_lag_bars: int = 1
    costs: CostConfig = field(default_factory=CostConfig)
    #: Annualised risk-free rate used for Sharpe. Overridden by FRED DGS3MO when available.
    risk_free_rate: float = 0.02
    #: Walk-forward window sizes in trading days.
    wf_train_days: int = 756
    wf_test_days: int = 189
    wf_step_days: int = 189


@dataclass
class ExecutionConfig:
    broker: str = "paper"  # paper | alpaca
    #: dry_run logs the orders it *would* send and stops there.
    mode: str = "dry_run"  # dry_run | live
    alpaca_key_env: str = "ALPACA_API_KEY_ID"
    alpaca_secret_env: str = "ALPACA_API_SECRET_KEY"
    alpaca_paper: bool = True
    #: Local state file for the paper broker.
    state_file: str = "state/paper_account.json"
    #: Refuse to place a single order bigger than this share of equity.
    max_order_pct_equity: float = 0.15
    #: Refuse to trade at all if the data is staler than this.
    max_data_age_hours: float = 48.0
    #: Orders below this notional are dropped as noise.
    min_order_notional: float = 50.0
    order_type: str = "market"
    limit_offset_bps: float = 10.0


@dataclass
class Config:
    run: RunConfig = field(default_factory=RunConfig)
    universe: UniverseConfig = field(default_factory=UniverseConfig)
    data: DataConfig = field(default_factory=DataConfig)
    features: FeatureConfig = field(default_factory=FeatureConfig)
    regime: RegimeConfig = field(default_factory=RegimeConfig)
    strategy: StrategyConfig = field(default_factory=StrategyConfig)
    risk: RiskConfig = field(default_factory=RiskConfig)
    backtest: BacktestConfig = field(default_factory=BacktestConfig)
    execution: ExecutionConfig = field(default_factory=ExecutionConfig)

    #: Directory the config was loaded from; relative paths resolve against it.
    base_dir: str = "."

    # ---------------------------------------------------------------- loading

    @classmethod
    def load(cls, path: str | os.PathLike[str] | None) -> "Config":
        """Read a YAML config, falling back to pure defaults when *path* is None."""
        if path is None:
            return cls()
        p = Path(path).expanduser().resolve()
        if not p.is_file():
            raise FileNotFoundError(f"config file not found: {p}")
        raw = yaml.safe_load(p.read_text(encoding="utf-8")) or {}
        if not isinstance(raw, dict):
            raise ValueError(f"config root must be a mapping, got {type(raw).__name__}")
        cfg = _from_dict(cls, raw, path=("<root>",))
        cfg.base_dir = str(p.parent)
        return cfg

    @classmethod
    def from_dict(cls, raw: Dict[str, Any]) -> "Config":
        return _from_dict(cls, raw, path=("<root>",))

    def to_dict(self) -> Dict[str, Any]:
        return dataclasses.asdict(self)

    # ------------------------------------------------------------ path helper

    def resolve(self, relative: str) -> Path:
        """Resolve a config-relative path into an absolute one."""
        p = Path(relative).expanduser()
        return p if p.is_absolute() else (Path(self.base_dir) / p).resolve()

    # -------------------------------------------------------------- validation

    def validate(self) -> List[str]:
        """Return a list of human-readable problems; empty means the config is sane."""
        problems: List[str] = []
        s, r, b = self.strategy, self.risk, self.backtest

        if not self.universe.symbols:
            problems.append("universe.symbols is empty — nothing to trade")
        if not 0 < s.long_quantile <= 1:
            problems.append("strategy.long_quantile must be in (0, 1]")
        if not 0 <= s.short_quantile <= 1:
            problems.append("strategy.short_quantile must be in [0, 1]")
        if s.max_positions < s.min_positions:
            problems.append("strategy.max_positions < strategy.min_positions")
        if r.max_weight <= 0 or r.max_weight > 1:
            problems.append("risk.max_weight must be in (0, 1]")
        if r.max_weight * s.min_positions < 1.0 and r.max_gross > r.max_weight * s.max_positions:
            problems.append(
                f"risk.max_weight ({r.max_weight}) x strategy.max_positions "
                f"({s.max_positions}) = {r.max_weight * s.max_positions:.2f} cannot reach "
                f"risk.max_gross ({r.max_gross}) — the book can never be fully invested"
            )
        if r.target_vol <= 0:
            problems.append("risk.target_vol must be positive")
        if not 0 < r.drawdown_killswitch < 1:
            problems.append("risk.drawdown_killswitch must be in (0, 1)")
        if b.execution_lag_bars < 1:
            problems.append(
                "backtest.execution_lag_bars < 1 fills on the bar the signal was "
                "computed from — that is look-ahead bias, not a fast bot"
            )
        if b.initial_capital <= 0:
            problems.append("backtest.initial_capital must be positive")
        if self.execution.mode not in {"dry_run", "live"}:
            problems.append("execution.mode must be 'dry_run' or 'live'")
        if self.execution.broker not in {"paper", "alpaca"}:
            problems.append("execution.broker must be 'paper' or 'alpaca'")
        if s.long_only and s.short_quantile > 0:
            # Not fatal, but it silently does nothing — worth saying out loud.
            problems.append(
                "strategy.long_only is true but short_quantile > 0 — shorts will be ignored"
            )
        for regime, weights in self.regime.factor_weights.items():
            if regime not in {"risk_on", "neutral", "risk_off"}:
                problems.append(f"regime.factor_weights has unknown regime '{regime}'")
            if any(w < 0 for w in weights.values()):
                problems.append(f"regime.factor_weights['{regime}'] has a negative weight")
        return problems


# --------------------------------------------------------------------------- #
# Recursive dataclass construction
# --------------------------------------------------------------------------- #


def _from_dict(cls: type, raw: Dict[str, Any], path: tuple[str, ...]) -> Any:
    """Build a (possibly nested) dataclass from a mapping, rejecting unknown keys."""
    known = {f.name: f for f in dataclasses.fields(cls)}
    unknown = set(raw) - set(known)
    if unknown:
        where = ".".join(path)
        raise ValueError(
            f"unknown config key(s) at {where}: {', '.join(sorted(unknown))}. "
            f"Valid keys: {', '.join(sorted(known))}"
        )
    # `from __future__ import annotations` turns field types into strings, so the
    # nested-dataclass check has to go through the resolved hints instead of f.type.
    hints = get_type_hints(cls)
    kwargs: Dict[str, Any] = {}
    for name, value in raw.items():
        hint = hints.get(name)
        if dataclasses.is_dataclass(hint) and isinstance(value, dict):
            kwargs[name] = _from_dict(hint, value, path + (name,))
        else:
            kwargs[name] = value
    return cls(**kwargs)
