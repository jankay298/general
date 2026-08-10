"""Configuration loading/validation and the data normalisation layer."""

from __future__ import annotations

import numpy as np
import pandas as pd
import pytest
import yaml

from quantbot.config import Config
from quantbot.data.base import normalise_frame
from quantbot.data.macro import release_lag_days
from quantbot.data.providers.stooq import to_stooq_symbol
from quantbot.data.providers.synthetic import SyntheticProvider


# --------------------------------------------------------------------------- #
# Config
# --------------------------------------------------------------------------- #


def test_defaults_are_self_consistent():
    cfg = Config()
    cfg.universe.symbols = ["A", "B", "C"]
    problems = [p for p in cfg.validate() if "will be ignored" not in p]
    assert problems == [], problems


def test_yaml_round_trip(tmp_path):
    path = tmp_path / "c.yml"
    path.write_text(
        yaml.safe_dump(
            {
                "run": {"name": "x"},
                "universe": {"symbols": ["AAA", "BBB"], "benchmark": "SPY"},
                "risk": {"target_vol": 0.20},
            }
        ),
        encoding="utf-8",
    )
    cfg = Config.load(path)
    assert cfg.run.name == "x"
    assert cfg.universe.symbols == ["AAA", "BBB"]
    assert cfg.risk.target_vol == 0.20
    # Unspecified values keep their defaults rather than becoming None.
    assert cfg.backtest.execution_lag_bars == 1
    assert cfg.strategy.name == "composite"


def test_unknown_config_keys_are_rejected(tmp_path):
    path = tmp_path / "c.yml"
    path.write_text(yaml.safe_dump({"risk": {"target_volatility": 0.2}}), encoding="utf-8")
    with pytest.raises(ValueError, match="unknown config key"):
        Config.load(path)


def test_missing_config_file_raises():
    with pytest.raises(FileNotFoundError):
        Config.load("/nonexistent/quantbot.yml")


def test_validation_catches_an_unreachable_gross_target():
    cfg = Config()
    cfg.universe.symbols = ["A", "B", "C"]
    cfg.risk.max_weight = 0.02
    cfg.strategy.max_positions = 5
    cfg.risk.max_gross = 1.0
    assert any("can never be fully invested" in p for p in cfg.validate())


def test_validation_catches_an_empty_universe():
    assert any("universe.symbols is empty" in p for p in Config().validate())


def test_validation_catches_a_bad_execution_mode():
    cfg = Config()
    cfg.universe.symbols = ["A"]
    cfg.execution.mode = "yolo"
    assert any("execution.mode" in p for p in cfg.validate())


def test_relative_paths_resolve_against_the_config_directory(tmp_path):
    path = tmp_path / "c.yml"
    path.write_text(yaml.safe_dump({"run": {"cache_dir": "cache"}}), encoding="utf-8")
    cfg = Config.load(path)
    assert cfg.resolve(cfg.run.cache_dir) == (tmp_path / "cache").resolve()


# --------------------------------------------------------------------------- #
# Frame normalisation
# --------------------------------------------------------------------------- #


def test_normalise_lowercases_and_orders_columns():
    raw = pd.DataFrame(
        {"Open": [1.0], "High": [2.0], "Low": [0.5], "Close": [1.5], "Volume": [100]},
        index=pd.to_datetime(["2024-01-02"]),
    )
    out = normalise_frame(raw)
    assert list(out.columns) == ["open", "high", "low", "close", "volume"]


def test_normalise_applies_the_adjustment_factor_to_the_whole_bar():
    raw = pd.DataFrame(
        {"open": [100.0], "high": [110.0], "low": [90.0], "close": [100.0],
         "adj_close": [50.0], "volume": [10]},
        index=pd.to_datetime(["2024-01-02"]),
    )
    out = normalise_frame(raw)
    # Everything halves together, so the bar stays internally consistent.
    assert out["close"].iloc[0] == pytest.approx(50.0)
    assert out["high"].iloc[0] == pytest.approx(55.0)
    assert out["low"].iloc[0] == pytest.approx(45.0)
    assert "adj_close" not in out.columns


def test_normalise_repairs_impossible_bars():
    raw = pd.DataFrame(
        {"open": [10.0], "high": [5.0], "low": [20.0], "close": [12.0], "volume": [1]},
        index=pd.to_datetime(["2024-01-02"]),
    )
    out = normalise_frame(raw)
    row = out.iloc[0]
    assert row["high"] >= max(row["open"], row["close"])
    assert row["low"] <= min(row["open"], row["close"])


def test_normalise_drops_duplicates_and_sorts():
    raw = pd.DataFrame(
        {"close": [3.0, 1.0, 2.0]},
        index=pd.to_datetime(["2024-01-03", "2024-01-02", "2024-01-03"]),
    )
    out = normalise_frame(raw)
    assert out.index.is_monotonic_increasing
    assert not out.index.duplicated().any()
    assert out["close"].iloc[-1] == 2.0  # last wins


def test_normalise_rejects_frames_without_a_close():
    raw = pd.DataFrame({"volume": [1]}, index=pd.to_datetime(["2024-01-02"]))
    assert normalise_frame(raw) is None


def test_normalise_handles_yfinance_multiindex_columns():
    columns = pd.MultiIndex.from_tuples(
        [("Open", "AAPL"), ("High", "AAPL"), ("Low", "AAPL"),
         ("Close", "AAPL"), ("Volume", "AAPL")]
    )
    raw = pd.DataFrame([[1.0, 2.0, 0.5, 1.5, 10]],
                       index=pd.to_datetime(["2024-01-02"]), columns=columns)
    out = normalise_frame(raw)
    assert list(out.columns) == ["open", "high", "low", "close", "volume"]


def test_normalise_drops_non_positive_prices():
    raw = pd.DataFrame(
        {"close": [10.0, 0.0, -5.0, 12.0]},
        index=pd.to_datetime(["2024-01-02", "2024-01-03", "2024-01-04", "2024-01-05"]),
    )
    out = normalise_frame(raw)
    assert len(out) == 2


# --------------------------------------------------------------------------- #
# Providers
# --------------------------------------------------------------------------- #


def test_stooq_symbol_mapping():
    assert to_stooq_symbol("AAPL") == "aapl.us"
    assert to_stooq_symbol("^GSPC") == "^spx"
    assert to_stooq_symbol("SAP.DE") == "sap.de"


def test_synthetic_data_is_deterministic():
    a = SyntheticProvider(seed=42).fetch("AAA", "2020-01-01", "2021-01-01")
    b = SyntheticProvider(seed=42).fetch("AAA", "2020-01-01", "2021-01-01")
    pd.testing.assert_frame_equal(a, b)


def test_synthetic_data_differs_by_seed_and_symbol():
    a = SyntheticProvider(seed=1).fetch("AAA", "2020-01-01", "2021-01-01")
    b = SyntheticProvider(seed=2).fetch("AAA", "2020-01-01", "2021-01-01")
    c = SyntheticProvider(seed=1).fetch("BBB", "2020-01-01", "2021-01-01")
    assert not a["close"].equals(b["close"])
    assert not a["close"].equals(c["close"])


def test_synthetic_bars_are_internally_consistent():
    frame = SyntheticProvider(seed=5).fetch("AAA", "2019-01-01", "2023-01-01")
    assert (frame["high"] >= frame[["open", "close"]].max(axis=1) - 1e-9).all()
    assert (frame["low"] <= frame[["open", "close"]].min(axis=1) + 1e-9).all()
    assert (frame["close"] > 0).all()
    assert (frame["volume"] >= 0).all()


def test_synthetic_symbols_share_a_market_factor():
    """Cross-sectional ranking is meaningless without common risk."""
    provider = SyntheticProvider(seed=9)
    frames = {
        s: provider.fetch(s, "2018-01-01", "2023-01-01") for s in ["A", "B", "C", "D", "E"]
    }
    returns = pd.DataFrame({s: f["close"].pct_change() for s, f in frames.items()}).dropna()
    correlations = returns.corr().to_numpy()
    off_diagonal = correlations[~np.eye(len(correlations), dtype=bool)]
    assert off_diagonal.mean() > 0.15, "synthetic assets are unrealistically independent"


def test_synthetic_returns_have_fat_tails():
    frame = SyntheticProvider(seed=3).fetch("AAA", "2010-01-01", "2024-01-01")
    returns = frame["close"].pct_change().dropna()
    assert returns.kurtosis() > 1.0, "a Gaussian market would understate real drawdowns"


# --------------------------------------------------------------------------- #
# Macro publication lag
# --------------------------------------------------------------------------- #


def test_survey_data_is_lagged_more_than_market_data():
    assert release_lag_days("DGS10") <= 2
    assert release_lag_days("UNRATE") >= 14
    assert release_lag_days("GDPC1") >= 25
    # An unknown series gets a conservative default rather than zero.
    assert release_lag_days("SOMETHING_NEW") >= 7
