"""The multi-factor composite strategy.

This is the strategy the bot actually trades. It is deliberately conventional —
cross-sectional factor ranking with a macro overlay and volatility targeting —
because the conventional version is the one with decades of out-of-sample
evidence behind it. Novelty in a trading strategy is usually just overfitting
that has not been caught yet.

How a position gets chosen, in order:

1. **Score every symbol on each factor.** Trend, momentum and short-term reversal
   come from prices; value, quality and growth from fundamentals; sentiment from
   news. Each is standardised *across the universe on that date*, so the question
   is always "cheap compared to what else I could buy today", never "cheap
   compared to 2013".
2. **Blend the factors using regime-dependent weights.** Financial-conditions
   stress decides whether the book leans into trend or into quality. Factors
   without data are dropped and the remaining weights renormalised.
3. **Filter for tradability.** Enough history, traded recently, liquid enough.
4. **Rank and select** the top slice long (and optionally the bottom slice short).
5. **Size by conviction / volatility**, scale the book toward a target
   volatility, then apply hard limits.

Every step is causal: nothing on date *t* reads a value from after the close of
*t*. The execution lag is the engine's job.
"""

from __future__ import annotations

from typing import Dict, List, Optional

import numpy as np
import pandas as pd

from ..config import Config
from ..data.fundamentals import FundamentalData
from ..data.repository import MarketData
from ..features.crosssection import cross_sectional_zscore, neutralise
from ..features.fundamental import fundamental_scores
from ..features.macro_regime import RegimeSeries, classify_regime
from ..features.sentiment import LexiconScorer, SentimentScorer
from ..features.technical import compute_technicals
from ..risk.limits import apply_limits
from ..risk.sizing import inverse_vol_weights, portfolio_volatility, scale_to_target_vol
from ..utils.logging import get_logger
from .base import Strategy, StrategyDiagnostics

log = get_logger(__name__)

#: Factors sourced from prices — always available.
_PRICE_FACTORS = ("trend", "momentum", "reversal")
#: Factors that need a fundamentals feed.
_FUNDAMENTAL_FACTORS = ("value", "quality", "growth")


class CompositeStrategy(Strategy):
    name = "composite"

    def __init__(self, cfg: Config, sentiment_scorer: Optional[SentimentScorer] = None) -> None:
        super().__init__(cfg)
        self.sentiment_scorer = sentiment_scorer or LexiconScorer()
        self.scores: Dict[str, pd.DataFrame] = {}
        self.regime: Optional[RegimeSeries] = None
        self.tech = None
        self.unavailable: List[str] = []
        self._last_vol_scale: Optional[float] = None

    # ------------------------------------------------------------------ setup

    def prepare(self, data: MarketData) -> None:
        self.data = data
        cfg = self.cfg
        clip = cfg.features.zscore_clip

        self.tech = compute_technicals(data, cfg.features)

        raw: Dict[str, pd.DataFrame] = {
            name: self.tech.signals[name] for name in _PRICE_FACTORS
        }
        raw.update(self._fundamental_panels(data))
        sentiment = self._sentiment_panel(data)
        if sentiment is not None:
            raw["sentiment"] = sentiment

        # Sector-neutralise before standardising: otherwise "buy quality" quietly
        # becomes "buy consumer staples", and the portfolio's real exposure is a
        # sector bet nobody chose.
        sectors = cfg.universe.sectors
        self.scores = {}
        for name, panel in raw.items():
            panel = neutralise(panel, sectors) if sectors else panel
            self.scores[name] = cross_sectional_zscore(panel, clip)

        self.regime = classify_regime(
            data.macro, cfg.regime, benchmark=data.benchmark, index=data.close.index
        )

        configured = set().union(*(set(w) for w in cfg.regime.factor_weights.values()))
        self.unavailable = sorted(configured - set(self.scores))
        if self.unavailable:
            log.warning(
                "factors without data, weights will be redistributed: %s",
                ", ".join(self.unavailable),
            )
        log.info("strategy ready with factors: %s", ", ".join(sorted(self.scores)))
        self._prepared = True

    # ------------------------------------------------------- factor construction

    def _fundamental_panels(self, data: MarketData) -> Dict[str, pd.DataFrame]:
        """Value/quality/growth panels, point-in-time where possible."""
        index, symbols = data.close.index, list(data.close.columns)

        if not data.fundamentals_archive.empty:
            # Score at each reporting date, then carry forward. Because the archive
            # is indexed by publication date, a value only appears on the day the
            # market could have known it.
            dates = sorted(set(data.fundamentals_archive.index.get_level_values("date")))
            rows: Dict[str, Dict] = {name: {} for name in _FUNDAMENTAL_FACTORS}
            for date in dates:
                snapshot = FundamentalData.as_of(data.fundamentals_archive, date, symbols)
                if snapshot.empty:
                    continue
                for name, series in fundamental_scores(snapshot).items():
                    rows[name][date] = series.reindex(symbols)

            panels = {}
            for name, by_date in rows.items():
                if not by_date:
                    continue
                frame = pd.DataFrame(by_date).T.sort_index()
                panels[name] = frame.reindex(index, method="ffill")
            if panels:
                log.info("fundamental factors built from a point-in-time archive")
                return panels

        if not data.fundamentals.empty:
            # A live snapshot describes today and nothing else. Broadcasting it
            # across history would hand the strategy tomorrow's balance sheet, so
            # this path is only taken when the run is not a backtest.
            scores = fundamental_scores(data.fundamentals)
            panels = {}
            for name, series in scores.items():
                row = series.reindex(symbols).to_numpy(dtype=float)
                panels[name] = pd.DataFrame(
                    np.tile(row, (len(index), 1)), index=index, columns=symbols
                )
            log.info("fundamental factors built from a current snapshot (live use only)")
            return panels

        return {}

    def _sentiment_panel(self, data: MarketData) -> Optional[pd.DataFrame]:
        """Decayed per-symbol news sentiment as a date x symbol panel."""
        news = data.news
        if news is None or news.empty or "symbols" not in news.columns:
            return None

        index, symbols = data.close.index, list(data.close.columns)
        texts = (news["title"].fillna("") + ". " + news.get("summary", "").fillna("")).tolist()
        scored = self.sentiment_scorer.score_batch(texts)

        records = []
        for i, tagged in enumerate(news["symbols"].fillna("")):
            if not tagged or scored[i].confidence <= 0:
                continue
            day = pd.Timestamp(news["published"].iloc[i]).normalize()
            for symbol in str(tagged).split(","):
                symbol = symbol.strip()
                if symbol in symbols:
                    records.append(
                        {
                            "date": day,
                            "symbol": symbol,
                            "weighted": scored[i].score * scored[i].confidence,
                            "weight": scored[i].confidence,
                        }
                    )
        if not records:
            return None

        frame = pd.DataFrame(records)
        daily = frame.groupby(["date", "symbol"])[["weighted", "weight"]].sum().reset_index()
        daily["score"] = daily["weighted"] / daily["weight"].replace(0.0, np.nan)

        panel = daily.pivot(index="date", columns="symbol", values="score")
        panel = panel.reindex(index=index, columns=symbols)
        # Exponential decay across dates is exactly the time-weighting we want,
        # and ewm handles the gaps between news days for free.
        panel = panel.ewm(halflife=self.cfg.features.sentiment_halflife, ignore_na=True).mean()
        log.info("sentiment factor built from %d tagged headlines", len(records))
        return panel

    # ------------------------------------------------------------- per-date work

    @staticmethod
    def _row_at(panel: pd.DataFrame, date: pd.Timestamp) -> pd.Series:
        """The most recent row at or before *date* — never after it."""
        window = panel.loc[:date]
        if window.empty:
            return pd.Series(dtype=float)
        return window.iloc[-1]

    def _blend(self, date: pd.Timestamp, weights: Dict[str, float]) -> pd.Series:
        """Weighted composite score across factors, renormalised by coverage."""
        active = {f: w for f, w in weights.items() if w > 0 and f in self.scores}
        if not active:
            return pd.Series(dtype=float)

        total = sum(active.values())
        symbols = list(self.data.close.columns)
        accumulated = pd.Series(0.0, index=symbols)
        covered = pd.Series(0.0, index=symbols)

        for factor, weight in active.items():
            row = self._row_at(self.scores[factor], date).reindex(symbols)
            mask = row.notna()
            accumulated += row.fillna(0.0) * weight
            covered += mask.astype(float) * weight

        combined = accumulated / covered.replace(0.0, np.nan)
        # A symbol scored on less than 40% of the active weight is a guess, not a view.
        return combined.where(covered / total >= 0.40)

    def _eligible(self, date: pd.Timestamp) -> pd.Index:
        """Symbols that may be traded on *date*."""
        data = self.data
        cfg = self.cfg

        history = data.close.loc[:date]
        if history.empty:
            return pd.Index([])

        enough_history = history.notna().sum() >= cfg.data.min_history_bars
        has_price = history.iloc[-1].notna()
        # Forward-filled holiday prices are fine for valuation but not for a fresh
        # trade decision — require a genuine print in the last week.
        recently_traded = data.traded.loc[:date].tail(5).any()

        mask = enough_history & has_price & recently_traded

        if cfg.strategy.min_dollar_volume > 0 and self.tech is not None:
            liquidity = self._row_at(self.tech.dollar_volume, date)
            mask &= liquidity.reindex(mask.index).fillna(0.0) >= cfg.strategy.min_dollar_volume

        return mask[mask].index

    @staticmethod
    def _select_with_buffer(
        ranked_names: List[str], held: set, n_target: int, buffer: float
    ) -> List[str]:
        """Pick *n_target* names, preferring ones already held near the cutoff.

        Without this, a name that drifts from rank 12 to rank 13 is sold and
        immediately replaced by the name that drifted from 13 to 12 — two round
        trips of cost to express no change of opinion at all.
        """
        if n_target <= 0 or not ranked_names:
            return []
        if buffer <= 0 or not held:
            return ranked_names[:n_target]

        n_keep = min(len(ranked_names), int(round(n_target * (1.0 + buffer))))
        selected = [s for s in ranked_names[:n_keep] if s in held][:n_target]
        for symbol in ranked_names[:n_target]:
            if len(selected) >= n_target:
                break
            if symbol not in selected:
                selected.append(symbol)
        # Return in rank order so conviction sizing lines up with the ranking.
        order = {s: i for i, s in enumerate(ranked_names)}
        return sorted(selected, key=lambda s: order.get(s, len(order)))

    def target_weights(
        self, date: pd.Timestamp, current: Optional[pd.Series] = None
    ) -> pd.Series:
        self.require_prepared()
        cfg = self.cfg
        date = pd.Timestamp(date)
        symbols = list(self.data.close.columns)
        empty = pd.Series(0.0, index=symbols)

        regime_weights = self.regime.weights_at(date)
        diagnostics = StrategyDiagnostics(
            date=date,
            regime=self.regime.at(date).value,
            regime_score=float(self.regime.score.loc[:date].iloc[-1])
            if len(self.regime.score.loc[:date])
            else 0.0,
            gross_target=self.regime.gross_at(date),
            factor_weights=regime_weights,
            active_factors=sorted(set(regime_weights) & set(self.scores)),
        )

        eligible = self._eligible(date)
        diagnostics.eligible = len(eligible)
        if len(eligible) < cfg.strategy.min_positions:
            diagnostics.notes.append(
                f"only {len(eligible)} tradable symbols, need {cfg.strategy.min_positions}"
            )
            self.last_diagnostics = diagnostics
            return empty

        combined = self._blend(date, regime_weights).reindex(eligible).dropna()
        if combined.empty:
            diagnostics.notes.append("no symbol had enough factor coverage")
            self.last_diagnostics = diagnostics
            return empty

        ranked = combined.sort_values(ascending=False)
        names = list(ranked.index)
        n = len(ranked)
        buffer = cfg.strategy.selection_buffer

        held_long, held_short = set(), set()
        if current is not None and not current.empty:
            held_long = set(current[current > 1e-9].index)
            held_short = set(current[current < -1e-9].index)

        n_long = int(np.clip(round(cfg.strategy.long_quantile * n),
                             cfg.strategy.min_positions, cfg.strategy.max_positions))
        n_long = min(n_long, n)

        picked_long = self._select_with_buffer(names, held_long, n_long, buffer)
        longs = ranked.loc[picked_long]
        longs = longs[longs >= cfg.strategy.min_score]

        shorts = pd.Series(dtype=float)
        if not cfg.strategy.long_only and cfg.strategy.short_quantile > 0:
            n_short = int(np.clip(round(cfg.strategy.short_quantile * n),
                                  cfg.strategy.min_positions, cfg.strategy.max_positions))
            n_short = min(n_short, max(0, n - len(longs)))
            if n_short > 0:
                # Rank ascending for the short side: the worst name is the best short.
                worst_first = names[::-1]
                picked_short = self._select_with_buffer(
                    worst_first, held_short, n_short, buffer
                )
                shorts = ranked.loc[picked_short]
                shorts = shorts[shorts <= -cfg.strategy.min_score]

        if longs.empty and shorts.empty:
            diagnostics.notes.append("no symbol cleared the score threshold")
            self.last_diagnostics = diagnostics
            return empty

        vol = self._row_at(self.tech.vol, date)
        gross = self.regime.gross_at(date)
        # With shorts enabled the gross budget is split evenly, which keeps the
        # book close to dollar-neutral and makes the factor bet, not the market
        # bet, the thing being expressed.
        long_gross = gross * (0.5 if not shorts.empty else 1.0)
        short_gross = gross * 0.5 if not shorts.empty else 0.0

        weights = pd.Series(0.0, index=symbols)

        if not longs.empty:
            conviction = (longs - cfg.strategy.min_score).clip(lower=0.01)
            sized = (
                inverse_vol_weights(conviction, vol)
                if cfg.strategy.inverse_vol_weighting
                else conviction / conviction.sum()
            )
            weights.loc[sized.index] = sized * long_gross
            diagnostics.selected_long = list(sized.index)

        if not shorts.empty:
            conviction = (-shorts - cfg.strategy.min_score).clip(lower=0.01)
            sized = (
                inverse_vol_weights(conviction, vol)
                if cfg.strategy.inverse_vol_weighting
                else conviction / conviction.sum()
            )
            weights.loc[sized.index] = -sized * short_gross
            diagnostics.selected_short = list(sized.index)

        returns = self.data.close.loc[:date].pct_change(fill_method=None)
        weights, scale = scale_to_target_vol(
            weights,
            returns,
            cfg.risk.target_vol,
            cfg.risk.vol_lookback,
            cfg.risk.max_leverage,
            previous_scale=self._last_vol_scale,
            deadband=cfg.strategy.vol_scale_deadband,
        )
        self._last_vol_scale = scale
        diagnostics.vol_scale = scale
        diagnostics.estimated_vol = portfolio_volatility(
            weights, returns, cfg.risk.vol_lookback
        )

        weights = apply_limits(weights, cfg.risk, cfg.universe.sectors)
        self.last_diagnostics = diagnostics
        log.debug("%s", diagnostics.summary())
        return weights
