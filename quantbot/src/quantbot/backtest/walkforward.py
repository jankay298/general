"""Walk-forward validation.

A single backtest over the full history tells you how a strategy would have done
*if you had known in 2015 which parameters to use*. You did not. Walk-forward
answers the question that actually matters: fit on what was knowable, trade the
next stretch blind, roll forward, repeat — and report only the blind stretches.

The out-of-sample curve produced here is stitched from windows the parameter
search never saw. It is normally much worse than the in-sample result, and that
gap is the single most useful number in this package: it is the size of the lie
a naive backtest would have told.

Parameter counting is tracked and fed into the deflated Sharpe ratio, because a
search over 200 combinations finds an impressive-looking one whether or not any
edge exists.
"""

from __future__ import annotations

import copy
import itertools
from dataclasses import dataclass, field
from typing import Any, Dict, List, Optional, Sequence

import numpy as np
import pandas as pd

from ..config import Config
from ..data.repository import MarketData
from ..strategy import build_strategy
from ..utils.logging import get_logger
from .engine import BacktestEngine
from .metrics import Metrics, compute_metrics

log = get_logger(__name__)

#: A deliberately small default grid. Every extra axis multiplies the number of
#: trials and therefore the amount of luck that can masquerade as skill.
DEFAULT_GRID: Dict[str, Sequence[Any]] = {
    "risk.target_vol": (0.08, 0.12, 0.16),
    "strategy.long_quantile": (0.20, 0.30, 0.40),
}


@dataclass
class Fold:
    index: int
    train_start: pd.Timestamp
    train_end: pd.Timestamp
    test_start: pd.Timestamp
    test_end: pd.Timestamp
    chosen: Dict[str, Any] = field(default_factory=dict)
    train_metrics: Optional[Metrics] = None
    test_metrics: Optional[Metrics] = None
    returns: pd.Series = field(default_factory=lambda: pd.Series(dtype=float))


@dataclass
class WalkForwardResult:
    folds: List[Fold]
    oos_returns: pd.Series
    oos_equity: pd.Series
    oos_metrics: Metrics
    in_sample_metrics: Optional[Metrics] = None
    n_trials: int = 1
    label: str = "walk-forward"

    def fold_table(self) -> pd.DataFrame:
        rows = []
        for fold in self.folds:
            row = {
                "fold": fold.index,
                "train": f"{fold.train_start.date()}..{fold.train_end.date()}",
                "test": f"{fold.test_start.date()}..{fold.test_end.date()}",
                "train_sharpe": fold.train_metrics.sharpe if fold.train_metrics else np.nan,
                "test_sharpe": fold.test_metrics.sharpe if fold.test_metrics else np.nan,
                "test_return": fold.test_metrics.total_return if fold.test_metrics else np.nan,
                "test_max_dd": fold.test_metrics.max_drawdown if fold.test_metrics else np.nan,
            }
            row.update({k.split(".")[-1]: v for k, v in fold.chosen.items()})
            rows.append(row)
        return pd.DataFrame(rows)

    def summary(self) -> str:
        lines = [
            f"=== {self.label}: {len(self.folds)} out-of-sample folds "
            f"({self.n_trials} parameter combinations searched per fold) ===",
            self.oos_metrics.summary(),
        ]
        if self.in_sample_metrics is not None:
            gap = self.in_sample_metrics.sharpe - self.oos_metrics.sharpe
            lines += [
                "",
                f"in-sample Sharpe    : {self.in_sample_metrics.sharpe:>10.2f}",
                f"out-of-sample Sharpe: {self.oos_metrics.sharpe:>10.2f}",
                f"overfitting gap     : {gap:>10.2f}"
                + ("   <-- large; treat the in-sample result as fiction" if gap > 0.5 else ""),
            ]
        positive = sum(
            1 for f in self.folds if f.test_metrics and f.test_metrics.total_return > 0
        )
        lines.append(f"profitable folds    : {positive:>10d} / {len(self.folds)}")
        return "\n".join(lines)


def _set_by_path(cfg: Config, path: str, value: Any) -> None:
    """Set ``cfg.section.field = value`` from a dotted string."""
    target = cfg
    parts = path.split(".")
    for part in parts[:-1]:
        target = getattr(target, part)
    if not hasattr(target, parts[-1]):
        raise ValueError(f"unknown config path: {path}")
    setattr(target, parts[-1], value)


def _grid_combinations(grid: Dict[str, Sequence[Any]]) -> List[Dict[str, Any]]:
    if not grid:
        return [{}]
    keys = list(grid)
    return [dict(zip(keys, values)) for values in itertools.product(*(grid[k] for k in keys))]


def make_folds(
    index: pd.DatetimeIndex, train_days: int, test_days: int, step_days: int
) -> List[tuple]:
    """Rolling (train, test) index windows."""
    folds = []
    start = 0
    while start + train_days + test_days <= len(index):
        train = index[start : start + train_days]
        test = index[start + train_days : start + train_days + test_days]
        folds.append((train, test))
        start += max(1, step_days)
    return folds


def run_walk_forward(
    cfg: Config,
    data: MarketData,
    strategy_name: str = "composite",
    grid: Optional[Dict[str, Sequence[Any]]] = None,
    selection_metric: str = "sharpe",
    also_run_in_sample: bool = True,
) -> WalkForwardResult:
    """Fit on each training window, trade the next window blind, stitch the results."""
    grid = DEFAULT_GRID if grid is None else grid
    combinations = _grid_combinations(grid)
    windows = make_folds(
        data.close.index,
        cfg.backtest.wf_train_days,
        cfg.backtest.wf_test_days,
        cfg.backtest.wf_step_days,
    )
    if not windows:
        raise ValueError(
            f"not enough history for walk-forward: need at least "
            f"{cfg.backtest.wf_train_days + cfg.backtest.wf_test_days} bars, "
            f"have {len(data.close.index)}"
        )

    # Indicator panels only depend on these config sections. When the grid leaves
    # them alone — the common case — the panels can be computed once and shared
    # across every trial instead of being rebuilt folds x combinations times.
    prepare_sensitive = {"features", "regime", "universe", "data"}
    reusable = not any(path.split(".")[0] in prepare_sensitive for path in grid)

    template: Optional[object] = None
    if reusable:
        template = build_strategy(strategy_name, cfg)
        template.prepare(data)
        log.info("feature panels prepared once and shared across all trials")

    def make_strategy(trial_cfg: Config):
        return template.with_config(trial_cfg) if template is not None else build_strategy(
            strategy_name, trial_cfg
        )

    log.info(
        "walk-forward: %d folds x %d parameter combinations", len(windows), len(combinations)
    )

    folds: List[Fold] = []
    oos_chunks: List[pd.Series] = []

    for i, (train_index, test_index) in enumerate(windows):
        best_score = -np.inf
        best_params: Dict[str, Any] = {}
        best_train: Optional[Metrics] = None

        for params in combinations:
            trial_cfg = copy.deepcopy(cfg)
            for path, value in params.items():
                _set_by_path(trial_cfg, path, value)
            try:
                engine = BacktestEngine(trial_cfg)
                strategy = make_strategy(trial_cfg)
                result = engine.run(
                    strategy, data,
                    start=train_index[0], end=train_index[-1],
                    label=f"fold{i}-train",
                    prepare=template is None,
                )
            except Exception as exc:
                log.warning("fold %d, params %s failed on the training window: %s", i, params, exc)
                continue

            score = getattr(result.metrics, selection_metric, -np.inf)
            # A parameter set that only looks good because it barely traded is not
            # a parameter set worth carrying into live money.
            if result.metrics.trades < 5:
                score = -np.inf
            if score > best_score:
                best_score, best_params, best_train = score, params, result.metrics

        if best_train is None:
            log.warning("fold %d: no parameter set produced a usable training result", i)
            continue

        test_cfg = copy.deepcopy(cfg)
        for path, value in best_params.items():
            _set_by_path(test_cfg, path, value)
        engine = BacktestEngine(test_cfg)
        strategy = make_strategy(test_cfg)
        test_result = engine.run(
            strategy, data,
            start=test_index[0], end=test_index[-1],
            label=f"fold{i}-test",
            prepare=template is None,
        )

        folds.append(
            Fold(
                index=i,
                train_start=train_index[0], train_end=train_index[-1],
                test_start=test_index[0], test_end=test_index[-1],
                chosen=best_params,
                train_metrics=best_train,
                test_metrics=test_result.metrics,
                returns=test_result.returns,
            )
        )
        oos_chunks.append(test_result.returns)
        log.info(
            "fold %d: chose %s | train %s %.2f -> test %.2f",
            i, best_params or "defaults", selection_metric, best_score,
            test_result.metrics.sharpe,
        )

    if not oos_chunks:
        raise RuntimeError("walk-forward produced no out-of-sample results")

    oos_returns = pd.concat(oos_chunks).sort_index()
    oos_returns = oos_returns[~oos_returns.index.duplicated(keep="first")]
    oos_equity = cfg.backtest.initial_capital * (1.0 + oos_returns).cumprod()

    oos_metrics = compute_metrics(
        oos_returns,
        risk_free=cfg.backtest.risk_free_rate,
        equity=oos_equity,
        n_trials=len(combinations) * len(folds),
    )

    # Trading statistics have to be aggregated from the folds. Each fold ran on
    # its own capital base, so the stitched equity curve cannot be used to
    # normalise their notionals — but each fold's turnover is already annualised,
    # so a length-weighted average of those is the honest figure. Leaving them at
    # zero would advertise a cost-free strategy that traded 13 times over.
    total_days = sum(len(f.returns) for f in folds) or 1
    oos_metrics.turnover = sum(
        (f.test_metrics.turnover if f.test_metrics else 0.0) * len(f.returns) for f in folds
    ) / total_days
    oos_metrics.cost_drag = sum(
        (f.test_metrics.cost_drag if f.test_metrics else 0.0) * len(f.returns) for f in folds
    ) / total_days
    oos_metrics.trades = sum(f.test_metrics.trades if f.test_metrics else 0 for f in folds)

    in_sample_metrics = None
    if also_run_in_sample:
        engine = BacktestEngine(cfg)
        full = engine.run(
            make_strategy(cfg), data, label="in-sample", prepare=template is None
        )
        in_sample_metrics = full.metrics

    return WalkForwardResult(
        folds=folds,
        oos_returns=oos_returns,
        oos_equity=oos_equity,
        oos_metrics=oos_metrics,
        in_sample_metrics=in_sample_metrics,
        n_trials=len(combinations),
        label=f"walk-forward ({strategy_name})",
    )
