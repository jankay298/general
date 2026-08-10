"""Report rendering.

The charts are hand-written SVG, which means a coordinate bug produces a file
that still contains every element, still passes any "does it have a polyline"
check, and draws nothing visible. These tests therefore assert on the actual
*geometry* — where the points land inside the viewBox — not on the presence of
markup.
"""

from __future__ import annotations

import re

import numpy as np
import pandas as pd
import pytest

from quantbot.backtest.engine import BacktestEngine
from quantbot.backtest.report import _line_chart, render_html, render_markdown, write_report
from quantbot.strategy import build_strategy

_CHART_WIDTH = 940
_PAD_LEFT, _PAD_RIGHT = 62, 14


def _points(svg: str) -> list[list[tuple[float, float]]]:
    lines = []
    for raw in re.findall(r'<polyline points="([^"]*)"', svg):
        pairs = []
        for token in raw.split():
            x, _, y = token.partition(",")
            pairs.append((float(x), float(y)))
        lines.append(pairs)
    return lines


def _series(periods: int = 400, unit: str = "ns") -> pd.Series:
    index = pd.bdate_range("2020-01-01", periods=periods).astype(f"datetime64[{unit}]")
    values = np.linspace(100.0, 250.0, periods)
    return pd.Series(values, index=pd.DatetimeIndex(index))


def test_chart_draws_points_inside_the_plot_area():
    svg = _line_chart({"a": _series()}, "test")
    lines = _points(svg)
    assert lines and lines[0], "no polyline points were emitted"

    xs = [x for x, _ in lines[0]]
    assert min(xs) >= _PAD_LEFT - 1, f"points start left of the axis at x={min(xs)}"
    assert max(xs) <= _CHART_WIDTH - _PAD_RIGHT + 1, f"points run off the right at x={max(xs)}"


def test_chart_spans_the_full_width():
    """A line that collapses to one x is the failure mode a smoke test misses."""
    svg = _line_chart({"a": _series()}, "test")
    xs = [x for x, _ in _points(svg)[0]]
    span = max(xs) - min(xs)
    plot_width = _CHART_WIDTH - _PAD_LEFT - _PAD_RIGHT
    assert span > plot_width * 0.9, (
        f"the series spans only {span:.0f}px of {plot_width}px — the x-axis scaling "
        f"is collapsing every point onto one column"
    )


@pytest.mark.parametrize("unit", ["ns", "us", "ms", "s"])
def test_chart_handles_every_datetime_resolution(unit):
    """pandas may hand back us-resolution indices; mixing units breaks the axis."""
    svg = _line_chart({"a": _series(unit=unit)}, "test")
    xs = [x for x, _ in _points(svg)[0]]
    plot_width = _CHART_WIDTH - _PAD_LEFT - _PAD_RIGHT
    assert max(xs) - min(xs) > plot_width * 0.9, f"{unit}-resolution index broke the x-axis"
    assert min(xs) >= _PAD_LEFT - 1


def test_chart_aligns_series_with_different_ranges():
    early = _series(200)
    late = _series(200)
    late.index = late.index + pd.Timedelta(days=400)

    svg = _line_chart({"early": early, "late": late}, "test")
    lines = _points(svg)
    assert len(lines) == 2
    # The later series must start to the right of where the earlier one starts.
    assert min(x for x, _ in lines[1]) > min(x for x, _ in lines[0])


def test_chart_survives_an_empty_or_flat_series():
    assert "no data" in _line_chart({"a": pd.Series(dtype=float)}, "t")
    flat = pd.Series([5.0] * 50, index=pd.bdate_range("2020-01-01", periods=50))
    svg = _line_chart({"a": flat}, "t")
    ys = [y for _, y in _points(svg)[0]]
    assert all(np.isfinite(y) for y in ys)


def test_report_is_self_contained(small_cfg, small_market):
    result = BacktestEngine(small_cfg).run(
        build_strategy("composite", small_cfg), small_market, label="c"
    )
    html = render_html([result], title="test")

    assert "<!doctype html>" in html.lower()
    assert not re.search(r'(src|href)="https?://', html), "the report loads an external resource"
    assert "<script" not in html, "the report should need no JavaScript"
    assert "NaN" not in html, "a NaN leaked into the rendered markup"
    # Both theme paths must define their own colours.
    assert "prefers-color-scheme: dark" in html
    assert 'data-theme="dark"' in html


def test_report_includes_the_caveats(small_cfg, small_market):
    result = BacktestEngine(small_cfg).run(
        build_strategy("composite", small_cfg), small_market, label="c"
    )
    html = render_html([result], title="test")
    assert "survivorship bias" in html.lower()
    assert "walk-forward" in html.lower()


def test_write_report_produces_the_expected_files(small_cfg, small_market, tmp_path):
    result = BacktestEngine(small_cfg).run(
        build_strategy("composite", small_cfg), small_market, label="c"
    )
    paths = write_report([result], tmp_path, stem="run")
    assert paths["html"].is_file() and paths["html"].stat().st_size > 5_000
    assert paths["equity"].is_file()
    assert paths["metrics"].is_file()

    equity = pd.read_csv(paths["equity"], index_col=0)
    assert "c" in equity.columns
    assert len(equity) == len(result.equity)


def test_markdown_report_mentions_every_strategy(small_cfg, small_market):
    engine = BacktestEngine(small_cfg)
    results = [
        engine.run(build_strategy(name, small_cfg), small_market, label=name)
        for name in ("composite", "buy_and_hold")
    ]
    text = render_markdown(results)
    assert "composite" in text and "buy_and_hold" in text
    assert "max drawdown" in text


def test_report_flags_simulated_data(small_cfg, small_market):
    """Synthetic prices must be impossible to mistake for a real backtest."""
    result = BacktestEngine(small_cfg).run(
        build_strategy("composite", small_cfg), small_market, label="c"
    )
    assert result.meta["simulated_symbols"] == len(small_market.symbols)

    html = render_html([result], title="test")
    assert "Simulated data" in html
    assert "market that does not exist" in html


def test_report_omits_the_banner_for_real_data(small_cfg, small_market):
    import copy as _copy

    real = _copy.copy(small_market)
    real.sources = {s: "yahoo" for s in small_market.symbols}
    result = BacktestEngine(small_cfg).run(
        build_strategy("composite", small_cfg), real, label="c"
    )
    assert result.meta["simulated_symbols"] == 0
    assert "Simulated data" not in render_html([result], title="test")
