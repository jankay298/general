"""Backtest reporting.

Emits a self-contained HTML file — inline CSS, hand-drawn SVG charts, no external
requests and no plotting dependency. That matters for two reasons: the report
opens anywhere including offline, and it can be published as-is without pulling
in a CDN.

The charts are deliberately the four that catch problems: the equity curve on a
log scale (so a 10% move looks the same early and late), the underwater plot (the
one that predicts whether the strategy gets switched off), rolling Sharpe (which
reveals an edge that decayed), and the monthly table (which reveals a result that
came from three lucky months).
"""

from __future__ import annotations

import html
from pathlib import Path
from typing import Dict, List, Optional, Sequence

import numpy as np
import pandas as pd

from .engine import BacktestResult
from .metrics import drawdown_series, monthly_returns_table, rolling_sharpe
from .walkforward import WalkForwardResult

_PALETTE = ["#2f6f9f", "#c06014", "#4a8c5c", "#8a5fa8", "#a03c3c", "#7a7a7a"]


# --------------------------------------------------------------------------- #
# SVG primitives
# --------------------------------------------------------------------------- #


def _scale(values: np.ndarray, lo: float, hi: float, size: float, invert: bool = False):
    span = hi - lo
    if span <= 0 or not np.isfinite(span):
        return np.full_like(values, size / 2.0, dtype=float)
    scaled = (values - lo) / span * size
    return size - scaled if invert else scaled


def _line_chart(
    series_map: Dict[str, pd.Series],
    title: str,
    height: int = 240,
    width: int = 940,
    log_scale: bool = False,
    percent: bool = False,
    zero_line: bool = False,
) -> str:
    """Multi-series line chart as inline SVG."""
    series_map = {k: v.dropna() for k, v in series_map.items() if v is not None and not v.dropna().empty}
    if not series_map:
        return f'<div class="chart-empty">{html.escape(title)}: no data</div>'

    pad_l, pad_r, pad_t, pad_b = 62, 14, 26, 26
    plot_w, plot_h = width - pad_l - pad_r, height - pad_t - pad_b

    all_values = np.concatenate([s.to_numpy(dtype=float) for s in series_map.values()])
    all_values = all_values[np.isfinite(all_values)]
    if all_values.size == 0:
        return f'<div class="chart-empty">{html.escape(title)}: no data</div>'

    if log_scale:
        floor = max(all_values[all_values > 0].min() if (all_values > 0).any() else 1.0, 1e-9)
        transform = lambda a: np.log10(np.clip(a, floor, None))  # noqa: E731
    else:
        transform = lambda a: a  # noqa: E731

    t_values = transform(all_values)
    lo, hi = float(np.min(t_values)), float(np.max(t_values))
    if zero_line and not log_scale:
        lo, hi = min(lo, 0.0), max(hi, 0.0)
    if hi - lo < 1e-12:
        hi, lo = hi + 1.0, lo - 1.0
    margin = (hi - lo) * 0.06
    lo, hi = lo - margin, hi + margin

    first = next(iter(series_map.values()))
    index = first.index
    x_lo, x_hi = index[0].value, index[-1].value

    parts: List[str] = [
        f'<svg viewBox="0 0 {width} {height}" class="chart" role="img" '
        f'aria-label="{html.escape(title)}">',
        f'<text x="{pad_l}" y="16" class="chart-title">{html.escape(title)}</text>',
    ]

    # Gridlines + y labels
    for frac in (0.0, 0.25, 0.5, 0.75, 1.0):
        y = pad_t + plot_h * frac
        value = hi - (hi - lo) * frac
        display = 10**value if log_scale else value
        label = f"{display:.1%}" if percent else (
            f"{display:,.0f}" if abs(display) >= 100 else f"{display:,.2f}"
        )
        parts.append(
            f'<line x1="{pad_l}" y1="{y:.1f}" x2="{width - pad_r}" y2="{y:.1f}" class="grid"/>'
            f'<text x="{pad_l - 6}" y="{y + 3:.1f}" class="tick tick-y">{html.escape(label)}</text>'
        )

    if zero_line and lo < 0 < hi:
        y0 = pad_t + plot_h * (hi - transform(np.array([0.0]))[0]) / (hi - lo)
        parts.append(f'<line x1="{pad_l}" y1="{y0:.1f}" x2="{width - pad_r}" y2="{y0:.1f}" class="zero"/>')

    for i, (name, series) in enumerate(series_map.items()):
        colour = _PALETTE[i % len(_PALETTE)]
        xs = pad_l + _scale(
            series.index.astype("int64").to_numpy(dtype=float), x_lo, x_hi, plot_w
        )
        ys = pad_t + _scale(transform(series.to_numpy(dtype=float)), lo, hi, plot_h, invert=True)
        # Thin the path: a decade of daily bars is ~2500 points and the eye cannot
        # use them, but the file size is real.
        step = max(1, len(xs) // 1200)
        points = " ".join(
            f"{x:.1f},{y:.1f}" for x, y in zip(xs[::step], ys[::step]) if np.isfinite(y)
        )
        parts.append(f'<polyline points="{points}" fill="none" stroke="{colour}" stroke-width="1.6"/>')
        parts.append(
            f'<text x="{width - pad_r - 8}" y="{pad_t + 14 + i * 15}" '
            f'class="legend" fill="{colour}">{html.escape(name)}</text>'
        )

    for frac in (0.0, 0.5, 1.0):
        x = pad_l + plot_w * frac
        stamp = pd.Timestamp(x_lo + (x_hi - x_lo) * frac)
        parts.append(
            f'<text x="{x:.1f}" y="{height - 8}" class="tick tick-x">{stamp.date()}</text>'
        )

    parts.append("</svg>")
    return "".join(parts)


def _heatmap_table(table: pd.DataFrame) -> str:
    """Monthly return table with colour-scaled cells."""
    if table.empty:
        return '<div class="chart-empty">no monthly data</div>'

    values = table.to_numpy(dtype=float)
    finite = values[np.isfinite(values)]
    limit = float(np.nanpercentile(np.abs(finite), 95)) if finite.size else 0.05
    limit = max(limit, 1e-6)

    head = "".join(f"<th>{html.escape(str(c))}</th>" for c in table.columns)
    rows = []
    for year, row in table.iterrows():
        cells = []
        for value in row:
            if not np.isfinite(value):
                cells.append('<td class="na"></td>')
                continue
            intensity = min(abs(value) / limit, 1.0)
            colour = (
                f"rgba(46,125,80,{0.10 + 0.55 * intensity:.2f})"
                if value >= 0
                else f"rgba(183,45,45,{0.10 + 0.55 * intensity:.2f})"
            )
            cells.append(f'<td style="background:{colour}">{value:.1%}</td>')
        rows.append(f"<tr><th>{year}</th>{''.join(cells)}</tr>")
    return (
        f'<table class="heatmap"><thead><tr><th></th>{head}</tr></thead>'
        f"<tbody>{''.join(rows)}</tbody></table>"
    )


def _metrics_table(results: Sequence[BacktestResult]) -> str:
    rows = [
        ("Total return", "total_return", "pct"),
        ("CAGR", "cagr", "pct"),
        ("Volatility (ann.)", "volatility", "pct"),
        ("Sharpe", "sharpe", "num"),
        ("Sortino", "sortino", "num"),
        ("Calmar", "calmar", "num"),
        ("Max drawdown", "max_drawdown", "pct"),
        ("Longest drawdown (days)", "max_drawdown_days", "int"),
        ("Time in drawdown", "time_in_drawdown", "pct"),
        ("Daily VaR 95%", "var_95", "pct"),
        ("Daily CVaR 95%", "cvar_95", "pct"),
        ("Skew", "skew", "num"),
        ("Excess kurtosis", "kurtosis", "num"),
        ("Win rate (days)", "win_rate", "pct"),
        ("Profit factor", "profit_factor", "num"),
        ("Annual turnover", "turnover", "pct"),
        ("Cost drag (ann.)", "cost_drag", "pct"),
        ("Trades", "trades", "int"),
        ("Beta", "beta", "num"),
        ("Alpha (ann.)", "alpha", "pct"),
        ("Information ratio", "information_ratio", "num"),
        ("Deflated Sharpe", "deflated_sharpe", "num"),
    ]

    columns = [(r.label, r.metrics) for r in results]
    first = results[0]
    if first.benchmark_metrics is not None:
        columns.append((f"{first.meta.get('benchmark_symbol', 'benchmark')} (bench)",
                        first.benchmark_metrics))

    def fmt(value, kind):
        if value is None or (isinstance(value, float) and not np.isfinite(value)):
            return "—"
        if kind == "pct":
            return f"{value:.2%}"
        if kind == "int":
            return f"{int(value):,}"
        return f"{value:.2f}"

    head = "".join(f"<th>{html.escape(name)}</th>" for name, _ in columns)
    body = []
    for label, attribute, kind in rows:
        cells = "".join(f"<td>{fmt(getattr(m, attribute, None), kind)}</td>" for _, m in columns)
        body.append(f"<tr><th>{html.escape(label)}</th>{cells}</tr>")
    return (
        f'<table class="metrics"><thead><tr><th>Metric</th>{head}</tr></thead>'
        f"<tbody>{''.join(body)}</tbody></table>"
    )


_CSS = """
:root{--bg:#ffffff;--fg:#1b1f24;--muted:#5b6672;--line:#e2e6ea;--card:#f7f9fb;--accent:#2f6f9f;}
:root:not([data-theme="light"]){}
@media (prefers-color-scheme: dark){:root:not([data-theme="light"]){
--bg:#14181d;--fg:#e6eaee;--muted:#9aa5b1;--line:#2a3138;--card:#1b2027;--accent:#6fa8d6;}}
:root[data-theme="dark"]{--bg:#14181d;--fg:#e6eaee;--muted:#9aa5b1;--line:#2a3138;--card:#1b2027;--accent:#6fa8d6;}
*{box-sizing:border-box}
body{background:var(--bg);color:var(--fg);margin:0;padding:28px 20px 64px;
font:15px/1.55 -apple-system,BlinkMacSystemFont,"Segoe UI",Roboto,Helvetica,Arial,sans-serif;}
.wrap{max-width:1000px;margin:0 auto}
h1{font-size:26px;margin:0 0 4px} h2{font-size:18px;margin:34px 0 12px;
padding-bottom:6px;border-bottom:1px solid var(--line)}
.sub{color:var(--muted);margin:0 0 22px;font-size:14px}
.card{background:var(--card);border:1px solid var(--line);border-radius:8px;padding:14px;margin:14px 0}
.scroll{overflow-x:auto;-webkit-overflow-scrolling:touch}
table{border-collapse:collapse;width:100%;font-size:13.5px;min-width:520px}
th,td{padding:6px 9px;text-align:right;border-bottom:1px solid var(--line);white-space:nowrap}
thead th{color:var(--muted);font-weight:600;text-align:right}
tbody th,thead th:first-child{text-align:left;font-weight:500}
.metrics tbody tr:hover{background:rgba(127,127,127,.07)}
.heatmap td{font-variant-numeric:tabular-nums;font-size:12.5px}
.heatmap td.na{background:transparent}
.chart{width:100%;height:auto;display:block}
.chart-title{font-size:13px;fill:var(--fg);font-weight:600}
.grid{stroke:var(--line);stroke-width:1}
.zero{stroke:var(--muted);stroke-width:1;stroke-dasharray:3 3}
.tick{font-size:10.5px;fill:var(--muted)}
.tick-y{text-anchor:end} .tick-x{text-anchor:middle}
.legend{font-size:11.5px;text-anchor:end;font-weight:600}
.chart-empty{color:var(--muted);padding:20px;text-align:center;font-size:13px}
.note{background:var(--card);border-left:3px solid var(--accent);padding:10px 14px;
margin:16px 0;font-size:13.5px;color:var(--fg);border-radius:0 6px 6px 0}
.note strong{color:var(--accent)}
code{background:rgba(127,127,127,.14);padding:1px 5px;border-radius:4px;font-size:12.5px}
"""


def render_html(
    results: Sequence[BacktestResult],
    walk_forward: Optional[WalkForwardResult] = None,
    title: str = "quantbot backtest",
) -> str:
    """Build a complete, self-contained HTML report."""
    if not results:
        raise ValueError("no backtest results to report")

    primary = results[0]
    meta = primary.meta

    equity_curves = {r.label: r.equity for r in results}
    if primary.benchmark is not None and not primary.benchmark.empty:
        scaled = primary.benchmark / primary.benchmark.iloc[0] * primary.equity.iloc[0]
        equity_curves[f"{meta.get('benchmark_symbol', 'benchmark')} (bench)"] = scaled

    drawdowns = {r.label: drawdown_series(r.equity) for r in results}
    sharpes = {r.label: rolling_sharpe(r.returns, 252) for r in results}

    sections: List[str] = []
    sections.append(f'<div class="card">{_line_chart(equity_curves, "Equity curve (log scale)", log_scale=True)}</div>')
    sections.append(f'<div class="card">{_line_chart(drawdowns, "Drawdown", percent=True, zero_line=True, height=180)}</div>')
    sections.append(f'<div class="card">{_line_chart(sharpes, "Rolling 1-year Sharpe", zero_line=True, height=180)}</div>')

    sections.append("<h2>Metrics</h2>")
    sections.append(f'<div class="card scroll">{_metrics_table(results)}</div>')

    sections.append(f"<h2>Monthly returns — {html.escape(primary.label)}</h2>")
    sections.append(f'<div class="card scroll">{_heatmap_table(monthly_returns_table(primary.returns))}</div>')

    if not primary.exposure.empty:
        exposure = {
            "gross": primary.exposure["gross"],
            "net": primary.exposure["net"],
        }
        sections.append("<h2>Exposure</h2>")
        sections.append(f'<div class="card">{_line_chart(exposure, "Gross and net exposure", percent=True, zero_line=True, height=180)}</div>')

    if not primary.diagnostics.empty and "regime" in primary.diagnostics:
        counts = primary.diagnostics["regime"].value_counts(normalize=True)
        rows = "".join(
            f"<tr><th>{html.escape(str(k))}</th><td>{v:.1%}</td></tr>" for k, v in counts.items()
        )
        sections.append("<h2>Regime distribution</h2>")
        sections.append(
            f'<div class="card scroll"><table><thead><tr><th>Regime</th>'
            f"<th>Share of rebalances</th></tr></thead><tbody>{rows}</tbody></table></div>"
        )

    if walk_forward is not None:
        sections.append("<h2>Walk-forward (out of sample)</h2>")
        gap = ""
        if walk_forward.in_sample_metrics is not None:
            difference = walk_forward.in_sample_metrics.sharpe - walk_forward.oos_metrics.sharpe
            gap = (
                f"<div class='note'><strong>Overfitting gap:</strong> in-sample Sharpe "
                f"{walk_forward.in_sample_metrics.sharpe:.2f} vs out-of-sample "
                f"{walk_forward.oos_metrics.sharpe:.2f} (difference {difference:.2f}). "
                f"The out-of-sample figure is the one to believe.</div>"
            )
        sections.append(gap)
        sections.append(
            f'<div class="card">{_line_chart({"out-of-sample": walk_forward.oos_equity}, "Stitched out-of-sample equity", log_scale=True)}</div>'
        )
        table = walk_forward.fold_table()
        if not table.empty:
            head = "".join(f"<th>{html.escape(str(c))}</th>" for c in table.columns)
            body = "".join(
                "<tr>"
                + "".join(
                    f"<td>{v:.2f}</td>" if isinstance(v, float) and np.isfinite(v)
                    else f"<td>{html.escape(str(v))}</td>"
                    for v in row
                )
                + "</tr>"
                for row in table.to_numpy()
            )
            sections.append(
                f'<div class="card scroll"><table><thead><tr>{head}</tr></thead>'
                f"<tbody>{body}</tbody></table></div>"
            )

    if primary.guard_events:
        items = "".join(
            f"<li>{pd.Timestamp(d).date()} — {dd:.1%} below the high-water mark</li>"
            for d, dd in primary.guard_events
        )
        sections.append(f"<h2>Drawdown guard</h2><div class='card'><ul>{items}</ul></div>")

    caveats = (
        "<div class='note'><strong>Read this before believing any number above.</strong> "
        "A backtest is a lower bound on how wrong you can be, not a forecast. This one "
        "charges commission, spread and square-root market impact, fills at the next "
        "open rather than the signal's own close, and caps orders at a share of daily "
        "volume — but it still cannot model a market that reacts to your presence, a "
        "universe chosen with hindsight (survivorship bias), or a regime that has no "
        "precedent in the sample. Trade the walk-forward numbers, not the in-sample ones, "
        "and start on paper.</div>"
    )

    body = f"""
<div class="wrap">
  <h1>{html.escape(title)}</h1>
  <p class="sub">{html.escape(str(meta.get('start', '')))} to {html.escape(str(meta.get('end', '')))}
   &middot; {len(meta.get('symbols', []))} symbols
   &middot; rebalance {html.escape(str(meta.get('rebalance', '')))}
   &middot; execution lag {meta.get('execution_lag_bars', 1)} bar(s)
   &middot; initial capital {meta.get('initial_capital', 0):,.0f}</p>
  {caveats}
  {''.join(sections)}
</div>"""

    return (
        "<!doctype html><html lang='en'><head><meta charset='utf-8'>"
        "<meta name='viewport' content='width=device-width,initial-scale=1'>"
        f"<title>{html.escape(title)}</title><style>{_CSS}</style></head>"
        f"<body>{body}</body></html>"
    )


def render_markdown(
    results: Sequence[BacktestResult], walk_forward: Optional[WalkForwardResult] = None
) -> str:
    """Plain-text report for terminals, commit messages and CI logs."""
    lines: List[str] = []
    for result in results:
        lines.append(result.summary())
        lines.append("")
    if walk_forward is not None:
        lines.append(walk_forward.summary())
        lines.append("")
        lines.append(walk_forward.fold_table().to_string(index=False))
    return "\n".join(lines)


def write_report(
    results: Sequence[BacktestResult],
    output_dir: str | Path,
    walk_forward: Optional[WalkForwardResult] = None,
    title: str = "quantbot backtest",
    stem: str = "backtest",
) -> Dict[str, Path]:
    """Write the HTML report plus the raw series as CSV. Returns the paths."""
    directory = Path(output_dir)
    directory.mkdir(parents=True, exist_ok=True)
    written: Dict[str, Path] = {}

    html_path = directory / f"{stem}.html"
    html_path.write_text(render_html(results, walk_forward, title), encoding="utf-8")
    written["html"] = html_path

    primary = results[0]
    equity_frame = pd.DataFrame({r.label: r.equity for r in results})
    equity_path = directory / f"{stem}-equity.csv"
    equity_frame.to_csv(equity_path)
    written["equity"] = equity_path

    if not primary.trades.empty:
        trades_path = directory / f"{stem}-trades.csv"
        primary.trades.to_csv(trades_path, index=False)
        written["trades"] = trades_path

    metrics_frame = pd.DataFrame({r.label: r.metrics.to_dict() for r in results})
    metrics_path = directory / f"{stem}-metrics.csv"
    metrics_frame.to_csv(metrics_path)
    written["metrics"] = metrics_path

    return written
