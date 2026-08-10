"""quantbot — a multi-factor systematic trading bot.

The package is organised in layers that only ever depend downwards:

    data      raw prices, fundamentals, macro series, news headlines
    features  indicators and scores derived from that raw data
    strategy  scores -> target portfolio weights
    risk      target weights -> risk-adjusted, constrained weights
    backtest  historical simulation of those weights
    execution turning weights into live orders at a broker

Everything is driven by a single :class:`quantbot.config.Config` object so the
backtest and the live loop see byte-identical parameters.
"""

__version__ = "0.1.0"

__all__ = ["__version__"]
