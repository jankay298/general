"""Command line interface.

    quantbot init          write a starter config
    quantbot data          fetch and summarise the data the bot would use
    quantbot backtest      simulate the strategy and write a report
    quantbot walkforward   out-of-sample validation
    quantbot signals       today's target weights, without trading
    quantbot trade         one live (or dry-run) trading pass
    quantbot account       paper account status

``trade`` is a dry run unless the config says ``mode: live`` **and** the command
line carries ``--yes-really-trade``. Two switches, deliberately.
"""

from __future__ import annotations

import argparse
import sys
from pathlib import Path
from typing import List, Optional

from . import __version__
from .config import Config
from .utils.cache import Cache
from .utils.logging import get_logger, setup_logging

log = get_logger(__name__)

_STARTER_CONFIG = """\
# quantbot configuration.
# Every value here has a default; override only what you need.
# Run `quantbot backtest -c this-file.yml` to try it.

run:
  name: demo
  cache_dir: .cache
  output_dir: output
  log_level: INFO

universe:
  name: us_mega_cap
  benchmark: SPY
  symbols:
    - AAPL
    - MSFT
    - GOOGL
    - AMZN
    - NVDA
    - META
    - JPM
    - XOM
    - JNJ
    - WMT
    - UNH
    - PG
    - HD
    - BAC
    - KO
    - PFE
    - CVX
    - INTC
    - DIS
    - CSCO
  # Sector labels enable sector-neutral scoring and the sector exposure cap.
  sectors:
    AAPL: tech
    MSFT: tech
    GOOGL: tech
    AMZN: consumer
    NVDA: tech
    META: tech
    JPM: financials
    XOM: energy
    JNJ: healthcare
    WMT: consumer
    UNH: healthcare
    PG: staples
    HD: consumer
    BAC: financials
    KO: staples
    PFE: healthcare
    CVX: energy
    INTC: tech
    DIS: consumer
    CSCO: tech

data:
  # Tried in order; the first provider that answers wins.
  # 'synthetic' always works offline and is what makes the tests runnable.
  price_providers: [yahoo, stooq, alphavantage, csv, synthetic]
  start: "2015-01-01"
  cache_ttl_hours: 12

strategy:
  name: composite
  long_only: true
  max_positions: 12
  long_quantile: 0.30

risk:
  target_vol: 0.12
  max_weight: 0.15
  drawdown_killswitch: 0.25

backtest:
  initial_capital: 100000
  rebalance: W-WED          # weekly; BME for monthly (roughly half the turnover)
  execution_lag_bars: 1     # decide on the close, fill the next open

execution:
  broker: paper             # paper | alpaca
  mode: dry_run             # dry_run | live
"""


# --------------------------------------------------------------------------- #
# Helpers
# --------------------------------------------------------------------------- #


def _load_config(args) -> Config:
    cfg = Config.load(args.config) if args.config else Config()

    if getattr(args, "symbols", None):
        cfg.universe.symbols = [s.strip().upper() for s in args.symbols.split(",") if s.strip()]
    if getattr(args, "start", None):
        cfg.data.start = args.start
    if getattr(args, "end", None):
        cfg.data.end = args.end
    if getattr(args, "provider", None):
        cfg.data.price_providers = [p.strip() for p in args.provider.split(",") if p.strip()]
    if getattr(args, "capital", None):
        cfg.backtest.initial_capital = args.capital
    if getattr(args, "rebalance", None):
        cfg.backtest.rebalance = args.rebalance
    if getattr(args, "log_level", None):
        cfg.run.log_level = args.log_level

    if not cfg.universe.symbols:
        # A demo universe so `quantbot backtest` does something useful with no
        # arguments at all. Synthetic data means it works with no network too.
        cfg.universe.symbols = [f"SYN{i:02d}" for i in range(30)]
        cfg.universe.benchmark = "SYNMKT"
        if "synthetic" not in cfg.data.price_providers:
            cfg.data.price_providers = ["synthetic"]
        log.warning(
            "no universe configured — using a 30-symbol synthetic demo universe. "
            "Run `quantbot init` to create a real config."
        )

    problems = cfg.validate()
    fatal = [p for p in problems if "will be ignored" not in p]
    for problem in problems:
        log.warning("config: %s", problem)
    if fatal and not getattr(args, "force", False):
        raise SystemExit(
            "configuration problems above are fatal; fix them or pass --force"
        )
    return cfg


def _load_data(cfg: Config, for_backtest: bool = True):
    from .data.repository import load_market_data

    cache = Cache(cfg.resolve(cfg.run.cache_dir), cfg.data.cache_ttl_hours)
    return load_market_data(cfg, cache, for_backtest=for_backtest)


# --------------------------------------------------------------------------- #
# Commands
# --------------------------------------------------------------------------- #


def cmd_init(args) -> int:
    path = Path(args.output)
    if path.exists() and not args.force:
        print(f"{path} already exists; pass --force to overwrite", file=sys.stderr)
        return 1
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(_STARTER_CONFIG, encoding="utf-8")
    print(f"wrote {path}")
    print(f"next: quantbot backtest -c {path}")
    return 0


def cmd_data(args) -> int:
    cfg = _load_config(args)
    data = _load_data(cfg, for_backtest=not args.live)
    print(data.describe())
    print()
    print("last 5 closes:")
    print(data.close.tail(5).round(2).to_string())
    if not data.macro.empty:
        print()
        print("macro (latest published values, release-lagged):")
        print(data.macro.iloc[-1].round(3).to_string())
    if not data.news.empty:
        print()
        print(f"news: {len(data.news)} headlines")
        for _, row in data.news.tail(8).iterrows():
            tags = f" [{row['symbols']}]" if row.get("symbols") else ""
            print(f"  {row['published']:%Y-%m-%d %H:%M} {row['title'][:88]}{tags}")
    if not data.fundamentals.empty:
        print()
        print("fundamentals:")
        columns = [c for c in ("sector", "pe", "roe", "profit_margin") if c in data.fundamentals]
        print(data.fundamentals[columns].round(3).to_string())
    return 0


def cmd_backtest(args) -> int:
    from .backtest.engine import BacktestEngine
    from .backtest.report import render_markdown, write_report
    from .strategy import build_strategy

    cfg = _load_config(args)
    data = _load_data(cfg, for_backtest=True)

    names = [n.strip() for n in args.strategies.split(",") if n.strip()]
    engine = BacktestEngine(cfg)
    results = []
    for name in names:
        results.append(engine.run(build_strategy(name, cfg), data, label=name))

    print()
    print(render_markdown(results))

    if not args.no_report:
        paths = write_report(
            results,
            cfg.resolve(cfg.run.output_dir),
            title=f"quantbot — {cfg.run.name}",
            stem=args.stem,
        )
        print()
        for kind, path in paths.items():
            print(f"{kind:8} -> {path}")
    return 0


def cmd_walkforward(args) -> int:
    from .backtest.engine import BacktestEngine
    from .backtest.report import write_report
    from .backtest.walkforward import run_walk_forward
    from .strategy import build_strategy

    cfg = _load_config(args)
    data = _load_data(cfg, for_backtest=True)

    result = run_walk_forward(cfg, data, strategy_name=args.strategy)
    print()
    print(result.summary())
    print()
    print(result.fold_table().to_string(index=False))

    if not args.no_report:
        engine = BacktestEngine(cfg)
        full = engine.run(build_strategy(args.strategy, cfg), data, label=args.strategy)
        paths = write_report(
            [full],
            cfg.resolve(cfg.run.output_dir),
            walk_forward=result,
            title=f"quantbot walk-forward — {cfg.run.name}",
            stem=args.stem,
        )
        print()
        for kind, path in paths.items():
            print(f"{kind:8} -> {path}")
    return 0


def cmd_signals(args) -> int:
    from .strategy import build_strategy

    cfg = _load_config(args)
    data = _load_data(cfg, for_backtest=not args.live)

    strategy = build_strategy(cfg.strategy.name, cfg)
    strategy.prepare(data)
    as_of = data.close.index[-1]
    weights = strategy.target_weights(as_of)

    print(f"target book as of {as_of.date()} (strategy: {strategy.name})")
    if strategy.last_diagnostics is not None:
        print(strategy.last_diagnostics.summary())
    print()
    active = weights[weights.abs() > 1e-6].sort_values(ascending=False)
    if active.empty:
        print("  no positions — the strategy wants to be flat")
    else:
        prices = data.close.iloc[-1]
        for symbol, weight in active.items():
            price = prices.get(symbol, float("nan"))
            notional = weight * cfg.backtest.initial_capital
            print(f"  {symbol:<8} {weight:>7.2%}  ~{notional:>12,.0f}  @ {price:,.2f}")
        print(f"  {'gross':<8} {active.abs().sum():>7.2%}")
        print(f"  {'net':<8} {active.sum():>7.2%}")
    return 0


def cmd_trade(args) -> int:
    from .execution.runner import LiveRunner

    cfg = _load_config(args)
    if args.broker:
        cfg.execution.broker = args.broker
    if args.live_mode:
        cfg.execution.mode = "live"

    runner = LiveRunner(cfg)
    plan = runner.build_plan()
    print(plan.render())

    if plan.blocked:
        print()
        print("BLOCKED — no orders sent:")
        for reason in plan.block_reasons:
            print(f"  - {reason}")
        return 2

    results = runner.execute(plan, confirm=args.yes_really_trade)
    if results:
        print()
        accepted = sum(1 for r in results if r.accepted)
        print(f"submitted {accepted}/{len(results)} order(s)")
    elif cfg.execution.mode != "live":
        print()
        print("dry run — nothing was sent. To trade for real:")
        print("  1. set execution.mode: live in the config")
        print("  2. pass --yes-really-trade")
    return 0


def cmd_account(args) -> int:
    from .execution import build_broker

    cfg = _load_config(args)
    if args.broker:
        cfg.execution.broker = args.broker
    broker = build_broker(cfg.execution.broker, cfg)

    healthy, message = broker.health_check()
    print(f"broker : {broker.name} ({'live' if broker.is_live else 'paper'})")
    print(f"health : {'ok' if healthy else 'PROBLEM'} — {message}")

    account = broker.account()
    print(f"equity : {account.equity:,.2f} {account.currency}")
    print(f"cash   : {account.cash:,.2f}")
    positions = broker.positions()
    if positions.empty:
        print("positions: flat")
    else:
        print("positions:")
        for symbol, quantity in positions.sort_index().items():
            print(f"  {symbol:<8} {quantity:>14,.4f}")

    if args.reset:
        if not hasattr(broker, "reset"):
            print("this broker cannot be reset", file=sys.stderr)
            return 1
        broker.reset()
        print("paper account reset")
    return 0


# --------------------------------------------------------------------------- #
# Parser
# --------------------------------------------------------------------------- #


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        prog="quantbot",
        description="Multi-factor trading bot: research, backtest, and trade.",
        formatter_class=argparse.RawDescriptionHelpFormatter,
        epilog=__doc__,
    )
    parser.add_argument("--version", action="version", version=f"quantbot {__version__}")

    common = argparse.ArgumentParser(add_help=False)
    common.add_argument("-c", "--config", help="path to a YAML config file")
    common.add_argument("--symbols", help="comma-separated universe override")
    common.add_argument("--start", help="first date (YYYY-MM-DD)")
    common.add_argument("--end", help="last date (YYYY-MM-DD)")
    common.add_argument("--provider", help="comma-separated price provider chain")
    common.add_argument("--log-level", default="INFO",
                        choices=["DEBUG", "INFO", "WARNING", "ERROR"])
    common.add_argument("--force", action="store_true",
                        help="run even if the config has problems")

    subparsers = parser.add_subparsers(dest="command", required=True)

    p = subparsers.add_parser("init", help="write a starter config file")
    p.add_argument("-o", "--output", default="quantbot.yml")
    p.add_argument("--force", action="store_true", help="overwrite an existing file")
    p.set_defaults(func=cmd_init)

    p = subparsers.add_parser("data", parents=[common], help="fetch and summarise data")
    p.add_argument("--live", action="store_true",
                   help="also pull snapshot fundamentals and current headlines")
    p.set_defaults(func=cmd_data)

    p = subparsers.add_parser("backtest", parents=[common], help="run a historical simulation")
    p.add_argument("--strategies", default="composite,trend,buy_and_hold",
                   help="comma-separated strategies to compare")
    p.add_argument("--capital", type=float, help="starting capital")
    p.add_argument("--rebalance", help="pandas offset alias, e.g. B, W-WED, BME")
    p.add_argument("--stem", default="backtest", help="output file stem")
    p.add_argument("--no-report", action="store_true", help="skip writing files")
    p.set_defaults(func=cmd_backtest)

    p = subparsers.add_parser("walkforward", parents=[common],
                              help="out-of-sample validation")
    p.add_argument("--strategy", default="composite")
    p.add_argument("--stem", default="walkforward")
    p.add_argument("--no-report", action="store_true")
    p.set_defaults(func=cmd_walkforward)

    p = subparsers.add_parser("signals", parents=[common],
                              help="show the target book without trading")
    p.add_argument("--live", action="store_true",
                   help="use live fundamentals and headlines")
    p.set_defaults(func=cmd_signals)

    p = subparsers.add_parser("trade", parents=[common],
                              help="one trading pass (dry run unless told otherwise)")
    p.add_argument("--broker", choices=["paper", "alpaca"])
    p.add_argument("--live-mode", action="store_true",
                   help="set execution.mode=live for this run")
    p.add_argument("--yes-really-trade", action="store_true",
                   help="required alongside live mode before any order is sent")
    p.set_defaults(func=cmd_trade)

    p = subparsers.add_parser("account", parents=[common], help="broker account status")
    p.add_argument("--broker", choices=["paper", "alpaca"])
    p.add_argument("--reset", action="store_true", help="wipe the paper account")
    p.set_defaults(func=cmd_account)

    return parser


def main(argv: Optional[List[str]] = None) -> int:
    parser = build_parser()
    args = parser.parse_args(argv)
    setup_logging(getattr(args, "log_level", "INFO"))

    try:
        return args.func(args)
    except KeyboardInterrupt:
        print("\ninterrupted", file=sys.stderr)
        return 130
    except SystemExit:
        raise
    except Exception as exc:
        log.error("%s: %s", type(exc).__name__, exc)
        if getattr(args, "log_level", "INFO") == "DEBUG":
            raise
        print("\nrun with --log-level DEBUG for the full traceback", file=sys.stderr)
        return 1


if __name__ == "__main__":  # pragma: no cover
    sys.exit(main())
