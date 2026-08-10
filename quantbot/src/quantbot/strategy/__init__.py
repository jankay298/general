"""Strategies: turn signals into target portfolio weights."""

from .base import Strategy, StrategyDiagnostics
from .composite import CompositeStrategy
from .baselines import BuyAndHoldStrategy, TrendFollowingStrategy

_REGISTRY = {
    "composite": CompositeStrategy,
    "trend": TrendFollowingStrategy,
    "buy_and_hold": BuyAndHoldStrategy,
}


def build_strategy(name: str, cfg):
    key = str(name).strip().lower()
    if key not in _REGISTRY:
        raise ValueError(
            f"unknown strategy {name!r}; available: {', '.join(sorted(_REGISTRY))}"
        )
    return _REGISTRY[key](cfg)


__all__ = [
    "Strategy",
    "StrategyDiagnostics",
    "CompositeStrategy",
    "TrendFollowingStrategy",
    "BuyAndHoldStrategy",
    "build_strategy",
]
