"""Performance and risk metrics.

Which numbers get reported shapes which strategies get built, so this module is
opinionated about showing the uncomfortable ones:

* **Sortino alongside Sharpe**, because Sharpe punishes upside volatility as if
  it were risk.
* **Maximum drawdown and its duration**, because the depth of a loss determines
  whether a strategy survives contact with a human, and the *length* of it
  determines whether that human keeps holding.
* **Turnover and cost drag**, because a Sharpe of 1.5 that requires 800% annual
  turnover is a Sharpe of 0.3 after fees.
* **A deflated Sharpe ratio**, which discounts the headline figure for how many
  variants were tried. Test enough strategies and one will look excellent purely
  by chance; this is the correction for that.

None of these are hard to compute. The discipline is in reporting them even when
the headline number is the flattering one.
"""

from __future__ import annotations

from dataclasses import dataclass, field
from typing import Dict, Optional

import numpy as np
import pandas as pd

try:  # SciPy is a convenience here, not a dependency — pure-Python fallbacks below.
    from scipy import stats as _scipy_stats
except ImportError:  # pragma: no cover - exercised only on installs without SciPy
    _scipy_stats = None

TRADING_DAYS = 252


@dataclass
class Metrics:
    total_return: float = 0.0
    cagr: float = 0.0
    volatility: float = 0.0
    sharpe: float = 0.0
    sortino: float = 0.0
    calmar: float = 0.0
    max_drawdown: float = 0.0
    max_drawdown_days: int = 0
    time_in_drawdown: float = 0.0
    var_95: float = 0.0
    cvar_95: float = 0.0
    skew: float = 0.0
    kurtosis: float = 0.0
    best_day: float = 0.0
    worst_day: float = 0.0
    win_rate: float = 0.0
    profit_factor: float = 0.0
    exposure: float = 0.0
    turnover: float = 0.0
    cost_drag: float = 0.0
    trades: int = 0
    beta: Optional[float] = None
    alpha: Optional[float] = None
    information_ratio: Optional[float] = None
    deflated_sharpe: Optional[float] = None
    extra: Dict[str, float] = field(default_factory=dict)

    def summary(self) -> str:
        lines = [
            f"total return        : {self.total_return:>10.2%}",
            f"CAGR                : {self.cagr:>10.2%}",
            f"volatility (ann.)   : {self.volatility:>10.2%}",
            f"Sharpe              : {self.sharpe:>10.2f}",
            f"Sortino             : {self.sortino:>10.2f}",
            f"Calmar              : {self.calmar:>10.2f}",
            f"max drawdown        : {self.max_drawdown:>10.2%}"
            f"  ({self.max_drawdown_days} days)",
            f"time in drawdown    : {self.time_in_drawdown:>10.1%}",
            f"daily VaR 95%       : {self.var_95:>10.2%}",
            f"daily CVaR 95%      : {self.cvar_95:>10.2%}",
            f"skew / kurtosis     : {self.skew:>10.2f} / {self.kurtosis:.2f}",
            f"win rate (days)     : {self.win_rate:>10.1%}",
            f"profit factor       : {self.profit_factor:>10.2f}",
            f"annual turnover     : {self.turnover:>10.1%}",
            f"cost drag (ann.)    : {self.cost_drag:>10.2%}",
            f"trades              : {self.trades:>10d}",
        ]
        if self.beta is not None:
            lines.append(f"beta / alpha        : {self.beta:>10.2f} / {self.alpha:.2%}")
        if self.information_ratio is not None:
            lines.append(f"information ratio   : {self.information_ratio:>10.2f}")
        if self.deflated_sharpe is not None:
            lines.append(f"deflated Sharpe     : {self.deflated_sharpe:>10.2f}")
        return "\n".join(lines)

    def to_dict(self) -> Dict[str, float]:
        out = {k: v for k, v in self.__dict__.items() if k != "extra"}
        out.update(self.extra)
        return out


def drawdown_series(equity: pd.Series) -> pd.Series:
    return equity / equity.cummax() - 1.0


def max_drawdown_duration(equity: pd.Series) -> int:
    """Longest stretch, in bars, spent below a previous high-water mark."""
    if equity.empty:
        return 0
    peaks = equity.cummax()
    underwater = equity < peaks
    longest = current = 0
    for flag in underwater:
        current = current + 1 if flag else 0
        longest = max(longest, current)
    return int(longest)


def compute_metrics(
    returns: pd.Series,
    risk_free: float = 0.0,
    benchmark_returns: Optional[pd.Series] = None,
    trades: Optional[pd.DataFrame] = None,
    equity: Optional[pd.Series] = None,
    weights: Optional[pd.DataFrame] = None,
    n_trials: int = 1,
) -> Metrics:
    """Compute the full metric set from a daily return series."""
    m = Metrics()
    returns = returns.dropna()
    if returns.empty:
        return m

    # An equity curve derived from returns already has the first period's return
    # baked into its first point, so dividing by equity[0] would silently drop it.
    # A supplied curve starts at the opening capital and does not have that problem.
    if equity is None:
        equity = (1.0 + returns).cumprod()
        base = 1.0
    else:
        base = float(equity.iloc[0])

    years = max(len(returns) / TRADING_DAYS, 1e-9)
    m.total_return = float(equity.iloc[-1] / base - 1.0) if base else 0.0
    m.cagr = float((1.0 + m.total_return) ** (1.0 / years) - 1.0) if m.total_return > -1 else -1.0
    m.volatility = float(returns.std(ddof=0) * np.sqrt(TRADING_DAYS))

    daily_rf = risk_free / TRADING_DAYS
    excess = returns - daily_rf
    if returns.std(ddof=0) > 0:
        m.sharpe = float(excess.mean() / returns.std(ddof=0) * np.sqrt(TRADING_DAYS))

    downside = excess[excess < 0]
    if len(downside) > 1 and downside.std(ddof=0) > 0:
        m.sortino = float(excess.mean() / downside.std(ddof=0) * np.sqrt(TRADING_DAYS))
    elif excess.mean() > 0:
        # No downside deviation at all: Sortino is unbounded, not zero. Reporting
        # zero here would make a strategy that never lost money look like its
        # worst possible case, which is the opposite of the truth.
        m.sortino = float("inf")

    dd = drawdown_series(equity)
    m.max_drawdown = float(dd.min())
    m.max_drawdown_days = max_drawdown_duration(equity)
    m.time_in_drawdown = float((dd < -1e-9).mean())
    m.calmar = float(m.cagr / abs(m.max_drawdown)) if m.max_drawdown < -1e-9 else 0.0

    m.var_95 = float(returns.quantile(0.05))
    tail = returns[returns <= m.var_95]
    m.cvar_95 = float(tail.mean()) if len(tail) else m.var_95
    m.skew = float(returns.skew())
    m.kurtosis = float(returns.kurtosis())
    m.best_day = float(returns.max())
    m.worst_day = float(returns.min())

    non_zero = returns[returns != 0]
    m.win_rate = float((non_zero > 0).mean()) if len(non_zero) else 0.0
    gains = returns[returns > 0].sum()
    losses = -returns[returns < 0].sum()
    m.profit_factor = float(gains / losses) if losses > 0 else float("inf") if gains > 0 else 0.0

    if weights is not None and not weights.empty:
        m.exposure = float(weights.abs().sum(axis=1).mean())

    if trades is not None and not trades.empty and equity is not None:
        # Measure each trade against the equity on the day it happened. Dividing
        # the summed notional by *average* equity looks equivalent but is not:
        # on a curve that grows several-fold, early trades get compared against a
        # book much larger than the one that placed them, and turnover comes out
        # inflated by a factor of several.
        equity_at_trade = (
            equity.reindex(pd.DatetimeIndex(trades["date"]), method="ffill")
            .to_numpy(dtype=float)
        )
        valid = np.isfinite(equity_at_trade) & (equity_at_trade > 0)
        if valid.any():
            notional = trades["notional"].to_numpy(dtype=float)[valid]
            costs = trades["cost"].to_numpy(dtype=float)[valid]
            base = equity_at_trade[valid]
            # One-way turnover, annualised: how many times the book is replaced.
            m.turnover = float(np.sum(notional / base) / years)
            m.cost_drag = float(np.sum(costs / base) / years)
        m.trades = int(len(trades))

    if benchmark_returns is not None:
        bench = benchmark_returns.reindex(returns.index).fillna(0.0)
        if bench.std(ddof=0) > 0:
            covariance = float(np.cov(returns, bench, ddof=0)[0, 1])
            m.beta = covariance / float(bench.var(ddof=0))
            m.alpha = float((returns.mean() - m.beta * bench.mean()) * TRADING_DAYS)
            active = returns - bench
            if active.std(ddof=0) > 0:
                m.information_ratio = float(
                    active.mean() / active.std(ddof=0) * np.sqrt(TRADING_DAYS)
                )

    m.deflated_sharpe = deflated_sharpe_ratio(returns, m.sharpe, n_trials)
    return m


def deflated_sharpe_ratio(
    returns: pd.Series, sharpe: float, n_trials: int = 1
) -> Optional[float]:
    """Sharpe adjusted for selection bias and non-normal returns.

    Follows Bailey & López de Prado: the expected maximum Sharpe from *n_trials*
    strategies with no real edge grows with the number of trials, so a headline
    Sharpe should be measured against that benchmark rather than against zero.
    Returns the probability that the true Sharpe exceeds the selection threshold.

    In practice: run one configuration and it barely moves the number; run two
    hundred and pick the best, and it collapses. That is the point.
    """
    n = len(returns)
    if n < 30 or not np.isfinite(sharpe):
        return None

    skew = float(returns.skew())
    kurtosis = float(returns.kurtosis()) + 3.0  # pandas reports excess kurtosis
    daily_sharpe = sharpe / np.sqrt(TRADING_DAYS)

    denominator = 1.0 - skew * daily_sharpe + (kurtosis - 1.0) / 4.0 * daily_sharpe**2
    if denominator <= 0:
        return None
    sharpe_std = np.sqrt(denominator / (n - 1))
    if sharpe_std <= 0:
        return None

    if n_trials > 1:
        euler = 0.5772156649
        e = np.e
        # Expected maximum of n_trials draws from a standard normal.
        expected_max = (1 - euler) * _normal_ppf(1 - 1.0 / n_trials) + euler * _normal_ppf(
            1 - 1.0 / (n_trials * e)
        )
        threshold = sharpe_std * expected_max
    else:
        threshold = 0.0

    return float(_normal_cdf((daily_sharpe - threshold) / sharpe_std))


def _normal_cdf(x: float) -> float:
    if _scipy_stats is not None:
        return float(_scipy_stats.norm.cdf(x))
    from math import erf, sqrt

    return 0.5 * (1.0 + erf(x / sqrt(2.0)))


def _normal_ppf(p: float) -> float:
    if _scipy_stats is not None:
        return float(_scipy_stats.norm.ppf(p))
    # Acklam's rational approximation — accurate to ~1e-9, no SciPy needed.
    from math import log, sqrt

    p = min(max(p, 1e-12), 1 - 1e-12)
    a = [-3.969683028665376e01, 2.209460984245205e02, -2.759285104469687e02,
         1.383577518672690e02, -3.066479806614716e01, 2.506628277459239e00]
    b = [-5.447609879822406e01, 1.615858368580409e02, -1.556989798598866e02,
         6.680131188771972e01, -1.328068155288572e01]
    c = [-7.784894002430293e-03, -3.223964580411365e-01, -2.400758277161838e00,
         -2.549732539343734e00, 4.374664141464968e00, 2.938163982698783e00]
    d = [7.784695709041462e-03, 3.224671290700398e-01, 2.445134137142996e00,
         3.754408661907416e00]
    plow, phigh = 0.02425, 1 - 0.02425
    if p < plow:
        q = sqrt(-2 * log(p))
        return (((((c[0]*q+c[1])*q+c[2])*q+c[3])*q+c[4])*q+c[5]) / ((((d[0]*q+d[1])*q+d[2])*q+d[3])*q+1)
    if p > phigh:
        q = sqrt(-2 * log(1 - p))
        return -(((((c[0]*q+c[1])*q+c[2])*q+c[3])*q+c[4])*q+c[5]) / ((((d[0]*q+d[1])*q+d[2])*q+d[3])*q+1)
    q = p - 0.5
    r = q * q
    return (((((a[0]*r+a[1])*r+a[2])*r+a[3])*r+a[4])*r+a[5])*q / (((((b[0]*r+b[1])*r+b[2])*r+b[3])*r+b[4])*r+1)


def rolling_sharpe(returns: pd.Series, window: int = 252) -> pd.Series:
    """Rolling annualised Sharpe — shows whether an edge decayed or held up."""
    mean = returns.rolling(window, min_periods=window // 2).mean()
    std = returns.rolling(window, min_periods=window // 2).std(ddof=0)
    return (mean / std.replace(0.0, np.nan)) * np.sqrt(TRADING_DAYS)


def monthly_returns_table(returns: pd.Series) -> pd.DataFrame:
    """Year x month return table — the standard way to eyeball consistency."""
    if returns.empty:
        return pd.DataFrame()
    monthly = (1.0 + returns).resample("ME").prod() - 1.0
    frame = monthly.to_frame("ret")
    frame["year"] = frame.index.year
    frame["month"] = frame.index.month
    table = frame.pivot_table(index="year", columns="month", values="ret")
    table.columns = [
        pd.Timestamp(2000, int(m), 1).strftime("%b") for m in table.columns
    ]
    table["Year"] = (1.0 + monthly).groupby(monthly.index.year).prod() - 1.0
    return table
