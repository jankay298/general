"""News sentiment scoring.

Two scorers, deliberately in that order of preference:

* :class:`LexiconScorer` — a finance-specific word list with negation and
  intensifier handling. Deterministic, offline, free, and fast. Because it is
  deterministic it is also the only one of the two that can be re-run over a
  historical archive and produce the same numbers every time, which is what a
  backtest needs.
* :class:`ClaudeScorer` — optional, uses the Anthropic API to read headlines the
  way an analyst would: it understands that "chip curbs eased" is good for
  semiconductors and that "beats estimates, guides lower" is not. Better signal,
  but it costs money per call and its output is not perfectly reproducible, so it
  is opt-in and intended for the live path.

Both return a score in [-1, +1] per headline. :func:`symbol_sentiment` then
aggregates headlines onto symbols with an exponential time decay, because a
three-day-old headline should not carry the weight of one from this morning.
"""

from __future__ import annotations

import abc
import json
import re
from dataclasses import dataclass
from typing import Dict, List, Optional, Sequence

import numpy as np
import pandas as pd

from ..utils.logging import get_logger

log = get_logger(__name__)

# --------------------------------------------------------------------------- #
# Finance lexicon
# --------------------------------------------------------------------------- #

#: Words that are positive *in a financial context*. "Growth" and "beat" mean
#: something specific here that a general-purpose sentiment list gets wrong.
_POSITIVE: Dict[str, float] = {
    "beat": 1.0, "beats": 1.0, "outperform": 1.0, "outperforms": 0.9, "upgrade": 1.0,
    "upgraded": 1.0, "surge": 0.9, "surges": 0.9, "soar": 1.0, "soars": 1.0,
    "rally": 0.8, "rallies": 0.8, "gain": 0.6, "gains": 0.6, "jump": 0.7, "jumps": 0.7,
    "record": 0.7, "profit": 0.6, "profits": 0.6, "growth": 0.6, "expands": 0.5,
    "strong": 0.7, "stronger": 0.7, "robust": 0.7, "solid": 0.5, "resilient": 0.5,
    "raises": 0.7, "raised": 0.6, "boost": 0.7, "boosts": 0.7, "optimistic": 0.7,
    "bullish": 1.0, "recovery": 0.6, "rebound": 0.7, "rebounds": 0.7, "approval": 0.6,
    "approved": 0.6, "wins": 0.7, "won": 0.5, "buyback": 0.7, "dividend": 0.4,
    "breakthrough": 0.9, "accelerate": 0.6, "accelerating": 0.6, "exceeded": 0.9,
    "exceeds": 0.9, "topped": 0.7, "tops": 0.7, "momentum": 0.5, "upside": 0.7,
    "easing": 0.6, "eased": 0.6, "truce": 0.8, "deal": 0.4, "agreement": 0.5,
    "stimulus": 0.6, "cuts rates": 0.7, "dovish": 0.7, "settlement": 0.4,
}

_NEGATIVE: Dict[str, float] = {
    "miss": 1.0, "misses": 1.0, "missed": 0.9, "downgrade": 1.0, "downgraded": 1.0,
    "plunge": 1.0, "plunges": 1.0, "slump": 0.9, "slumps": 0.9, "tumble": 0.9,
    "tumbles": 0.9, "crash": 1.0, "crashes": 1.0, "sink": 0.7, "sinks": 0.7,
    "fall": 0.5, "falls": 0.5, "drop": 0.6, "drops": 0.6, "decline": 0.6,
    "declines": 0.6, "loss": 0.7, "losses": 0.7, "weak": 0.7, "weaker": 0.7,
    "warns": 0.9, "warning": 0.8, "cuts": 0.6, "cut": 0.5, "slashes": 0.9,
    "lowered": 0.7, "lowers": 0.7, "bearish": 1.0, "recession": 1.0, "slowdown": 0.8,
    "layoffs": 0.8, "bankruptcy": 1.0, "default": 0.9, "probe": 0.6, "lawsuit": 0.6,
    "investigation": 0.6, "fraud": 1.0, "scandal": 0.9, "fine": 0.5, "penalty": 0.6,
    "recall": 0.7, "halts": 0.6, "halted": 0.6, "shortfall": 0.8, "downside": 0.7,
    "inflation": 0.4, "hawkish": 0.7, "tariff": 0.7, "tariffs": 0.7, "sanctions": 0.8,
    "conflict": 0.8, "war": 0.9, "invasion": 1.0, "strike": 0.5, "shutdown": 0.7,
    "crisis": 0.9, "turmoil": 0.8, "uncertainty": 0.5, "selloff": 0.9, "correction": 0.6,
    "delisting": 0.9, "restructuring": 0.5, "impairment": 0.7, "writedown": 0.8,
}

#: Words that reverse the polarity of what follows them.
_NEGATIONS = frozenset(
    {"not", "no", "never", "without", "avoids", "avoided", "denies", "denied",
     "fails", "failed", "unlikely", "despite", "isn't", "wasn't", "won't", "doesn't"}
)

#: Words that scale the polarity of what follows them.
_INTENSIFIERS: Dict[str, float] = {
    "very": 1.4, "sharply": 1.5, "significantly": 1.4, "massively": 1.7, "deeply": 1.4,
    "slightly": 0.5, "marginally": 0.4, "modestly": 0.6, "somewhat": 0.6, "barely": 0.4,
    "record": 1.3, "unprecedented": 1.6, "steeply": 1.5, "dramatically": 1.5,
}

_TOKEN_RE = re.compile(r"[a-z][a-z'\-]+")

#: Headlines matching these are market-wide rather than company-specific.
_MACRO_MARKERS = re.compile(
    # Terms that may take a suffix (tariff/tariffs, sanction/sanctions) are grouped
    # separately from the ones that must not — "war\w*" would happily match "warns".
    r"\b(?:"
    r"(?:fed|fomc|ecb|boj|central bank|inflation|cpi|ppi|payroll|gdp|recession|"
    r"tariff|sanction|opec|treasury|election|geopolitic)s?"
    r"|war|wars|jobs report|yield curve|rate (?:cut|hike)s?"
    r")\b",
    re.IGNORECASE,
)


# --------------------------------------------------------------------------- #
# Scorers
# --------------------------------------------------------------------------- #


@dataclass
class Scored:
    """One headline's sentiment plus how confident the scorer is in it."""

    score: float          # [-1, +1]
    confidence: float     # [0, 1] — 0 means "no opinion", used as a blending weight
    is_macro: bool = False


class SentimentScorer(abc.ABC):
    name: str = "base"

    @abc.abstractmethod
    def score_batch(self, texts: Sequence[str]) -> List[Scored]:
        """Score a batch of headlines."""

    def score(self, text: str) -> Scored:
        return self.score_batch([text])[0]


class LexiconScorer(SentimentScorer):
    """Word-list sentiment with negation and intensifier handling."""

    name = "lexicon"

    def score_batch(self, texts: Sequence[str]) -> List[Scored]:
        return [self._score_one(t) for t in texts]

    def _score_one(self, text: str) -> Scored:
        if not text:
            return Scored(0.0, 0.0)
        tokens = _TOKEN_RE.findall(text.lower())
        if not tokens:
            return Scored(0.0, 0.0)

        total = 0.0
        hits = 0
        for i, token in enumerate(tokens):
            polarity = _POSITIVE.get(token, 0.0) - _NEGATIVE.get(token, 0.0)
            if polarity == 0.0:
                continue
            # Look back two tokens for a negation or an intensifier — enough to
            # catch "not strong" and "sharply lower" without dragging in the
            # unrelated clause three words back.
            window = tokens[max(0, i - 2) : i]
            if any(w in _NEGATIONS for w in window):
                polarity = -polarity * 0.8  # negated statements are weaker claims
            for w in window:
                polarity *= _INTENSIFIERS.get(w, 1.0)
            total += polarity
            hits += 1

        if hits == 0:
            return Scored(0.0, 0.0, bool(_MACRO_MARKERS.search(text)))

        # Average rather than sum: a long headline should not out-shout a short one.
        raw = total / np.sqrt(hits)
        score = float(np.tanh(raw / 2.0))
        # More matched words means a more trustworthy read, saturating quickly.
        confidence = float(min(1.0, 0.35 + 0.25 * hits))
        return Scored(score, confidence, bool(_MACRO_MARKERS.search(text)))


class ClaudeScorer(SentimentScorer):
    """Sentiment via the Anthropic API — reads headlines the way an analyst would.

    Opt-in and live-path only. Headlines are scored in batches inside a single
    request with a JSON schema constraining the output, so a hundred headlines
    cost one call rather than a hundred.
    """

    name = "claude"

    def __init__(
        self,
        model: str = "claude-opus-5",
        batch_size: int = 40,
        effort: str = "low",
        api_key_env: str = "ANTHROPIC_API_KEY",
        fallback: Optional[SentimentScorer] = None,
    ) -> None:
        self.model = model
        self.batch_size = batch_size
        self.effort = effort
        self.api_key_env = api_key_env
        self.fallback = fallback or LexiconScorer()
        self._client = None

    def available(self) -> bool:
        try:
            import anthropic  # noqa: F401
        except ImportError:
            return False
        # An unset key does not mean no credentials — the SDK also resolves an
        # `ant auth login` profile — so only report unavailable if the import failed.
        return True

    def _get_client(self):
        if self._client is None:
            import anthropic

            self._client = anthropic.Anthropic()
        return self._client

    def score_batch(self, texts: Sequence[str]) -> List[Scored]:
        if not texts:
            return []
        if not self.available():
            log.info("anthropic SDK unavailable — falling back to the lexicon scorer")
            return self.fallback.score_batch(texts)

        out: List[Scored] = []
        for start in range(0, len(texts), self.batch_size):
            chunk = list(texts[start : start + self.batch_size])
            try:
                out.extend(self._score_chunk(chunk))
            except Exception as exc:
                log.warning("Claude sentiment failed (%s) — using the lexicon scorer", exc)
                out.extend(self.fallback.score_batch(chunk))
        return out

    def _score_chunk(self, texts: List[str]) -> List[Scored]:
        client = self._get_client()
        numbered = "\n".join(f"{i}. {t}" for i, t in enumerate(texts))

        schema = {
            "type": "object",
            "properties": {
                "items": {
                    "type": "array",
                    "items": {
                        "type": "object",
                        "properties": {
                            "index": {"type": "integer"},
                            "score": {"type": "number"},
                            "confidence": {"type": "number"},
                            "is_macro": {"type": "boolean"},
                        },
                        "required": ["index", "score", "confidence", "is_macro"],
                        "additionalProperties": False,
                    },
                }
            },
            "required": ["items"],
            "additionalProperties": False,
        }

        response = client.messages.create(
            model=self.model,
            max_tokens=16000,
            output_config={"effort": self.effort, "format": {"type": "json_schema", "schema": schema}},
            system=(
                "You score financial news headlines for their likely effect on the price "
                "of the asset they concern, over the next few trading days.\n\n"
                "score: -1.0 (strongly bearish) to +1.0 (strongly bullish), 0.0 if the "
                "headline carries no directional information.\n"
                "confidence: 0.0 to 1.0 — how clear the directional read is. Routine "
                "coverage with no news content gets a low confidence, not a fabricated score.\n"
                "is_macro: true when the headline is about the economy, policy, rates, or "
                "geopolitics rather than one company.\n\n"
                "Judge market impact, not whether the news is good for the world. A layoff "
                "announcement often lifts a share price. Anticipated news that merely "
                "confirms expectations moves little. Return one entry per input index."
            ),
            messages=[{"role": "user", "content": f"Score these headlines:\n\n{numbered}"}],
        )

        if response.stop_reason == "refusal":
            raise RuntimeError("model declined to score this batch")

        text = next((b.text for b in response.content if b.type == "text"), "")
        payload = json.loads(text)

        scored = [Scored(0.0, 0.0) for _ in texts]
        for item in payload.get("items", []):
            idx = int(item.get("index", -1))
            if 0 <= idx < len(scored):
                scored[idx] = Scored(
                    score=float(np.clip(item.get("score", 0.0), -1.0, 1.0)),
                    confidence=float(np.clip(item.get("confidence", 0.0), 0.0, 1.0)),
                    is_macro=bool(item.get("is_macro", False)),
                )
        return scored


def build_scorer(name: str = "lexicon", **kwargs) -> SentimentScorer:
    key = str(name).strip().lower()
    if key == "lexicon":
        return LexiconScorer()
    if key == "claude":
        return ClaudeScorer(**kwargs)
    raise ValueError(f"unknown sentiment scorer {name!r}; use 'lexicon' or 'claude'")


# --------------------------------------------------------------------------- #
# Aggregation
# --------------------------------------------------------------------------- #


def symbol_sentiment(
    news: pd.DataFrame,
    symbols: Sequence[str],
    scorer: Optional[SentimentScorer] = None,
    halflife_days: float = 3.0,
    as_of: Optional[pd.Timestamp] = None,
) -> pd.Series:
    """Aggregate tagged headlines into one sentiment score per symbol.

    Headlines are weighted by the scorer's confidence and by an exponential decay
    on age, so this morning's downgrade dominates last week's feature piece.
    Symbols with no coverage come back as NaN rather than 0.0 — "no news" is not
    the same as "neutral news", and the blending layer treats the two differently.
    """
    result = pd.Series(np.nan, index=list(symbols), dtype=float)
    if news is None or news.empty or "symbols" not in news.columns:
        return result

    scorer = scorer or LexiconScorer()
    as_of = pd.Timestamp(as_of) if as_of is not None else news["published"].max()

    texts = (news["title"].fillna("") + ". " + news.get("summary", "").fillna("")).tolist()
    scores = scorer.score_batch(texts)

    age_days = (as_of - pd.to_datetime(news["published"])).dt.total_seconds() / 86400.0
    decay = np.exp(-np.log(2.0) * np.clip(age_days, 0, None) / max(0.1, halflife_days))

    weighted: Dict[str, float] = {s: 0.0 for s in symbols}
    weights: Dict[str, float] = {s: 0.0 for s in symbols}

    for row_idx, tagged in enumerate(news["symbols"].fillna("")):
        if not tagged:
            continue
        scored = scores[row_idx]
        weight = scored.confidence * float(decay.iloc[row_idx])
        if weight <= 0:
            continue
        for symbol in str(tagged).split(","):
            symbol = symbol.strip()
            if symbol in weighted:
                weighted[symbol] += scored.score * weight
                weights[symbol] += weight

    for symbol in symbols:
        if weights[symbol] > 0:
            result[symbol] = weighted[symbol] / weights[symbol]
    return result


def market_sentiment(
    news: pd.DataFrame,
    scorer: Optional[SentimentScorer] = None,
    halflife_days: float = 2.0,
    as_of: Optional[pd.Timestamp] = None,
) -> float:
    """One market-wide mood number from the macro and geopolitical headlines.

    Feeds the regime overlay as a fast-moving complement to the (slower) macro
    data series, which are published with a lag.
    """
    if news is None or news.empty:
        return 0.0
    scorer = scorer or LexiconScorer()
    as_of = pd.Timestamp(as_of) if as_of is not None else news["published"].max()

    texts = (news["title"].fillna("") + ". " + news.get("summary", "").fillna("")).tolist()
    scores = scorer.score_batch(texts)
    age_days = (as_of - pd.to_datetime(news["published"])).dt.total_seconds() / 86400.0
    decay = np.exp(-np.log(2.0) * np.clip(age_days, 0, None) / max(0.1, halflife_days))

    total = weight_sum = 0.0
    for i, scored in enumerate(scores):
        if not scored.is_macro:
            continue
        weight = scored.confidence * float(decay.iloc[i])
        total += scored.score * weight
        weight_sum += weight
    return float(total / weight_sum) if weight_sum > 0 else 0.0
