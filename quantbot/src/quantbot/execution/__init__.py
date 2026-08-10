"""Live execution: broker adapters and the trading loop."""

from .broker import Account, Broker, Order, OrderResult
from .paper import PaperBroker
from .alpaca import AlpacaBroker
from .runner import LiveRunner, TradePlan

_BROKERS = {"paper": PaperBroker, "alpaca": AlpacaBroker}


def build_broker(name: str, cfg):
    key = str(name).strip().lower()
    if key not in _BROKERS:
        raise ValueError(f"unknown broker {name!r}; available: {', '.join(sorted(_BROKERS))}")
    return _BROKERS[key].from_config(cfg)


__all__ = [
    "Broker",
    "Order",
    "OrderResult",
    "Account",
    "PaperBroker",
    "AlpacaBroker",
    "LiveRunner",
    "TradePlan",
    "build_broker",
]
