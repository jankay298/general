"""Feature construction, cross-sectional scoring, regime detection and risk limits."""

from __future__ import annotations

import numpy as np
import pandas as pd
import pytest

from quantbot.config import RegimeConfig, RiskConfig
from quantbot.features.crosssection import blend, cross_sectional_zscore, neutralise
from quantbot.features.macro_regime import Regime, classify_regime
from quantbot.features.sentiment import LexiconScorer, symbol_sentiment
from quantbot.features.technical import atr, rsi
from quantbot.risk.limits import DrawdownGuard, apply_limits
from quantbot.risk.sizing import inverse_vol_weights, portfolio_volatility, scale_to_target_vol


# --------------------------------------------------------------------------- #
# Indicators
# --------------------------------------------------------------------------- #


def test_rsi_saturates_on_an_unbroken_rally():
    index = pd.bdate_range("2024-01-01", periods=60)
    rising = pd.DataFrame({"A": np.linspace(100, 200, 60)}, index=index)
    values = rsi(rising, 14).dropna()
    assert values["A"].iloc[-1] == pytest.approx(100.0)


def test_rsi_bottoms_out_on_an_unbroken_decline():
    index = pd.bdate_range("2024-01-01", periods=60)
    falling = pd.DataFrame({"A": np.linspace(200, 100, 60)}, index=index)
    values = rsi(falling, 14).dropna()
    assert values["A"].iloc[-1] == pytest.approx(0.0, abs=1e-6)


def test_atr_equals_the_range_for_gapless_bars():
    index = pd.bdate_range("2024-01-01", periods=40)
    close = pd.DataFrame({"A": np.full(40, 100.0)}, index=index)
    high = close + 2.0
    low = close - 2.0
    values = atr(high, low, close, 14).dropna()
    assert values["A"].iloc[-1] == pytest.approx(4.0)


# --------------------------------------------------------------------------- #
# Cross-section
# --------------------------------------------------------------------------- #


def test_zscore_is_standardised_within_each_date():
    index = pd.bdate_range("2024-01-01", periods=3)
    panel = pd.DataFrame(
        np.random.default_rng(0).normal(size=(3, 30)),
        index=index,
        columns=[f"S{i}" for i in range(30)],
    )
    z = cross_sectional_zscore(panel, clip=10.0)
    for _, row in z.iterrows():
        assert row.mean() == pytest.approx(0.0, abs=1e-9)
        assert row.std(ddof=0) == pytest.approx(1.0, abs=1e-9)


def test_zscore_clips_outliers():
    panel = pd.DataFrame(
        [[1.0, 1.1, 0.9, 1.05, 1.02, 0.98, 1.01, 0.99, 1.03, 0.97, 1.04, 500.0]],
        index=pd.bdate_range("2024-01-01", periods=1),
        columns=[f"S{i}" for i in range(12)],
    )
    z = cross_sectional_zscore(panel, clip=3.0)
    assert z.abs().max().max() <= 3.0 + 1e-9


def test_zscore_of_a_constant_row_is_zero_not_nan():
    panel = pd.DataFrame(
        [[5.0] * 15], index=pd.bdate_range("2024-01-01", periods=1),
        columns=[f"S{i}" for i in range(15)],
    )
    z = cross_sectional_zscore(panel)
    assert z.notna().all().all()
    assert (z.abs() < 1e-9).all().all()


def test_neutralise_removes_the_group_mean():
    panel = pd.DataFrame(
        [[1.0, 3.0, 10.0, 20.0]],
        index=pd.bdate_range("2024-01-01", periods=1),
        columns=["A", "B", "C", "D"],
    )
    groups = {"A": "x", "B": "x", "C": "y", "D": "y"}
    out = neutralise(panel, groups)
    assert out.loc[out.index[0], ["A", "B"]].sum() == pytest.approx(0.0)
    assert out.loc[out.index[0], ["C", "D"]].sum() == pytest.approx(0.0)


def test_blend_renormalises_over_available_factors():
    index = pd.bdate_range("2024-01-01", periods=1)
    columns = ["A", "B"]
    full = pd.DataFrame([[1.0, -1.0]], index=index, columns=columns)
    partial = pd.DataFrame([[2.0, np.nan]], index=index, columns=columns)

    out = blend({"f1": full, "f2": partial}, {"f1": 0.5, "f2": 0.5})
    # A sees both factors: (1*0.5 + 2*0.5) / 1.0 = 1.5
    assert out.loc[index[0], "A"] == pytest.approx(1.5)
    # B sees only f1, covering 50% of the weight — above the 35% floor, so it is
    # scored on what exists rather than dropped.
    assert out.loc[index[0], "B"] == pytest.approx(-1.0)


def test_blend_drops_cells_with_too_little_coverage():
    index = pd.bdate_range("2024-01-01", periods=1)
    columns = ["A"]
    thin = pd.DataFrame([[1.0]], index=index, columns=columns)
    missing = pd.DataFrame([[np.nan]], index=index, columns=columns)
    out = blend({"f1": thin, "f2": missing}, {"f1": 0.2, "f2": 0.8})
    assert pd.isna(out.loc[index[0], "A"])


# --------------------------------------------------------------------------- #
# Regime
# --------------------------------------------------------------------------- #


def test_regime_is_neutral_without_any_inputs():
    index = pd.bdate_range("2020-01-01", periods=300)
    series = classify_regime(pd.DataFrame(), RegimeConfig(), None, index)
    assert (series.label == Regime.NEUTRAL.value).all()
    assert series.gross_at(index[-1]) == pytest.approx(
        RegimeConfig().gross_exposure["neutral"]
    )


def test_regime_hysteresis_suppresses_flip_flopping():
    """A stress score hovering on the threshold must not flip the label every day.

    Tested against the labelling step directly: driving the full classifier with a
    contrived macro series does not isolate the mechanism, because the rolling
    z-score rescales whatever you feed it.
    """
    from quantbot.features.macro_regime import _label_with_hysteresis

    cfg = RegimeConfig()
    index = pd.bdate_range("2020-01-01", periods=400)
    # Hover just either side of the risk-off threshold, day after day.
    score = pd.Series(
        cfg.risk_off_threshold + 0.05 * np.sin(np.arange(400)), index=index
    )

    labels = _label_with_hysteresis(score, cfg)
    changes = int((labels != labels.shift()).sum())

    naive = score.map(
        lambda v: Regime.RISK_OFF.value if v > cfg.risk_off_threshold else Regime.NEUTRAL.value
    )
    naive_changes = int((naive != naive.shift()).sum())

    assert changes <= 2, f"label changed {changes} times — hysteresis is not working"
    assert naive_changes > 50, "the test data should whipsaw a naive classifier"


def test_regime_labels_are_stable_on_real_shaped_data():
    """End-to-end: the classifier must not produce a new regime every other day."""
    rng = np.random.default_rng(17)
    index = pd.bdate_range("2015-01-01", periods=1500)
    # A slow-moving stress proxy with daily noise on top, like real VIX.
    trend = np.cumsum(rng.normal(0, 0.05, 1500))
    macro = pd.DataFrame(
        {"vix": 18 + trend + rng.normal(0, 0.8, 1500)}, index=index
    )
    series = classify_regime(macro, RegimeConfig(), None, index)
    changes = int((series.label != series.label.shift()).sum())
    # Deliberately noisy input; without the dwell brake this exceeds 120 flips.
    assert changes < len(index) / 30, f"regime flipped {changes} times in {len(index)} bars"


def test_minimum_dwell_time_is_enforced():
    from quantbot.features.macro_regime import _label_with_hysteresis

    cfg = RegimeConfig()
    cfg.min_dwell_bars = 30
    index = pd.bdate_range("2020-01-01", periods=300)
    # Alternate hard between extremes every single bar.
    score = pd.Series(np.where(np.arange(300) % 2 == 0, 5.0, -5.0), index=index)
    labels = _label_with_hysteresis(score, cfg)

    runs = (labels != labels.shift()).cumsum().value_counts()
    # Every run except possibly the first and last must last the full dwell.
    assert runs.drop(runs.index[[0, -1]], errors="ignore").min() >= 30


def test_risk_off_regime_reduces_gross_exposure():
    cfg = RegimeConfig()
    assert cfg.gross_exposure["risk_off"] < cfg.gross_exposure["neutral"]
    assert cfg.gross_exposure["neutral"] < cfg.gross_exposure["risk_on"]


def test_risk_off_weights_favour_quality_over_trend():
    cfg = RegimeConfig()
    assert cfg.factor_weights["risk_off"]["quality"] > cfg.factor_weights["risk_off"]["trend"]
    assert cfg.factor_weights["risk_on"]["trend"] > cfg.factor_weights["risk_on"]["quality"]


# --------------------------------------------------------------------------- #
# Risk
# --------------------------------------------------------------------------- #


def test_limits_cap_position_sector_and_gross():
    cfg = RiskConfig(max_weight=0.10, max_sector_weight=0.30, max_gross=0.80, max_net=1.0)
    weights = pd.Series({"A": 0.40, "B": 0.30, "C": 0.20, "D": 0.20, "E": 0.20})
    sectors = {"A": "tech", "B": "tech", "C": "tech", "D": "energy", "E": "energy"}

    out = apply_limits(weights, cfg, sectors)
    assert out.abs().max() <= cfg.max_weight + 1e-9
    tech = out[["A", "B", "C"]].abs().sum()
    assert tech <= cfg.max_sector_weight + 1e-9
    assert out.abs().sum() <= cfg.max_gross + 1e-9


def test_limits_preserve_relative_ordering():
    cfg = RiskConfig(max_weight=1.0, max_sector_weight=1.0, max_gross=0.5, max_net=1.0)
    weights = pd.Series({"A": 0.4, "B": 0.3, "C": 0.2})
    out = apply_limits(weights, cfg, {})
    assert out["A"] > out["B"] > out["C"]
    assert out.sum() == pytest.approx(0.5)


def test_inverse_vol_weighting_favours_the_calmer_asset():
    scores = pd.Series({"calm": 1.0, "wild": 1.0})
    vol = pd.Series({"calm": 0.10, "wild": 0.40})
    weights = inverse_vol_weights(scores, vol)
    assert weights["calm"] > weights["wild"]
    assert weights.sum() == pytest.approx(1.0)
    assert weights["calm"] / weights["wild"] == pytest.approx(4.0, rel=1e-6)


def test_vol_targeting_scales_a_hot_book_down():
    rng = np.random.default_rng(3)
    index = pd.bdate_range("2022-01-01", periods=300)
    # ~32% annualised volatility.
    returns = pd.DataFrame(
        rng.normal(0, 0.02, size=(300, 4)), index=index, columns=list("ABCD")
    )
    weights = pd.Series({"A": 0.25, "B": 0.25, "C": 0.25, "D": 0.25})

    scaled, factor = scale_to_target_vol(weights, returns, target_vol=0.10, lookback=250)
    assert factor < 1.0
    assert portfolio_volatility(scaled, returns, 250) < portfolio_volatility(weights, returns, 250)
    assert portfolio_volatility(scaled, returns, 250) == pytest.approx(0.10, rel=0.15)


def test_vol_targeting_deadband_suppresses_small_changes():
    rng = np.random.default_rng(4)
    index = pd.bdate_range("2022-01-01", periods=300)
    returns = pd.DataFrame(rng.normal(0, 0.012, size=(300, 3)), index=index, columns=list("ABC"))
    weights = pd.Series({"A": 0.3, "B": 0.3, "C": 0.3})

    _, first = scale_to_target_vol(weights, returns, 0.12, 250)
    # A previous scale within the deadband must be kept, not nudged.
    _, second = scale_to_target_vol(
        weights, returns, 0.12, 250, previous_scale=first * 1.03, deadband=0.10
    )
    assert second == pytest.approx(first * 1.03)


def test_drawdown_guard_fires_and_then_releases():
    guard = DrawdownGuard(threshold=0.20, cooldown_bars=3)
    assert not guard.update(100.0, pd.Timestamp("2024-01-01"))
    assert not guard.update(120.0, pd.Timestamp("2024-01-02"))
    assert not guard.update(100.0, pd.Timestamp("2024-01-03"))  # -16.7%, under threshold

    assert guard.update(95.0, pd.Timestamp("2024-01-04"))       # -20.8%, fires
    assert guard.active
    assert len(guard.events) == 1

    assert guard.update(95.0, pd.Timestamp("2024-01-05"))
    assert guard.update(95.0, pd.Timestamp("2024-01-06"))
    assert not guard.update(95.0, pd.Timestamp("2024-01-07"))   # cooldown expired
    assert not guard.active
    # The high-water mark resets to the recovery point, so it cannot re-fire
    # immediately against a peak that may never return.
    assert guard.high_water_mark == pytest.approx(95.0)


# --------------------------------------------------------------------------- #
# Sentiment
# --------------------------------------------------------------------------- #


def test_lexicon_reads_direction_correctly():
    scorer = LexiconScorer()
    assert scorer.score("Shares soar after earnings beat").score > 0.2
    assert scorer.score("Shares plunge as company warns of losses").score < -0.2


def test_lexicon_handles_negation():
    scorer = LexiconScorer()
    plain = scorer.score("Company reports strong growth").score
    negated = scorer.score("Company does not report strong growth").score
    assert plain > 0
    assert negated < plain


def test_lexicon_has_no_opinion_on_neutral_text():
    scorer = LexiconScorer()
    result = scorer.score("The company will hold its annual meeting on Tuesday")
    assert result.confidence == 0.0
    assert result.score == 0.0


def test_macro_detection_does_not_trip_on_warns():
    """'war' must not match inside 'warns' — a classic regex trap."""
    scorer = LexiconScorer()
    assert not scorer.score("Apple warns on weak guidance").is_macro
    assert scorer.score("Trade war escalates between major economies").is_macro


def test_symbol_sentiment_decays_with_age():
    now = pd.Timestamp("2024-06-10")
    news = pd.DataFrame(
        {
            "published": [now - pd.Timedelta(days=10), now],
            "title": ["AAA plunges on fraud probe", "AAA soars on record profit"],
            "summary": ["", ""],
            "symbols": ["AAA", "AAA"],
        }
    )
    scores = symbol_sentiment(news, ["AAA"], halflife_days=2.0, as_of=now)
    # The fresh positive headline should dominate the stale negative one.
    assert scores["AAA"] > 0.3


def test_symbol_without_coverage_is_nan_not_zero():
    news = pd.DataFrame(
        {
            "published": [pd.Timestamp("2024-06-10")],
            "title": ["AAA soars"],
            "summary": [""],
            "symbols": ["AAA"],
        }
    )
    scores = symbol_sentiment(news, ["AAA", "BBB"], as_of=pd.Timestamp("2024-06-10"))
    assert not pd.isna(scores["AAA"])
    assert pd.isna(scores["BBB"]), "no news must mean 'no opinion', not 'neutral'"
