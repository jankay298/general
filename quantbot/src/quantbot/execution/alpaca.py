"""Alpaca broker adapter.

Talks to Alpaca's REST API over plain ``requests`` — no SDK dependency, so there
is one less package whose version can silently change order semantics.

**It points at the paper endpoint by default.** Switching to live money takes
both ``execution.alpaca_paper: false`` in the config *and* ``execution.mode:
live`` on the runner. Two independent switches, because one is too easy to flip
by accident.

Credentials come from the environment, never from the config file, so a config
can be committed to a repository without leaking keys.
"""

from __future__ import annotations

import os
from datetime import datetime
from typing import Dict, List, Optional

import pandas as pd
import requests

from ..utils.logging import get_logger
from .broker import Account, Broker, Order, OrderResult

log = get_logger(__name__)

_PAPER_URL = "https://paper-api.alpaca.markets"
_LIVE_URL = "https://api.alpaca.markets"
_DATA_URL = "https://data.alpaca.markets"


class AlpacaBroker(Broker):
    """REST adapter for Alpaca (US equities and crypto)."""

    name = "alpaca"

    def __init__(
        self,
        key_id: str,
        secret_key: str,
        paper: bool = True,
        timeout: float = 20.0,
    ) -> None:
        if not key_id or not secret_key:
            raise ValueError(
                "Alpaca credentials missing. Export ALPACA_API_KEY_ID and "
                "ALPACA_API_SECRET_KEY (names configurable under execution.*_env)."
            )
        self.key_id = key_id
        self.secret_key = secret_key
        self.paper = paper
        self.is_live = not paper
        self.base_url = _PAPER_URL if paper else _LIVE_URL
        self.timeout = timeout
        self._session = requests.Session()
        self._session.headers.update(
            {
                "APCA-API-KEY-ID": key_id,
                "APCA-API-SECRET-KEY": secret_key,
                "accept": "application/json",
            }
        )
        if self.is_live:
            log.warning("Alpaca adapter is pointed at the LIVE endpoint — real money")

    @classmethod
    def from_config(cls, cfg) -> "AlpacaBroker":
        return cls(
            key_id=os.environ.get(cfg.execution.alpaca_key_env, ""),
            secret_key=os.environ.get(cfg.execution.alpaca_secret_env, ""),
            paper=cfg.execution.alpaca_paper,
        )

    # ----------------------------------------------------------------- plumbing

    def _request(self, method: str, path: str, base: Optional[str] = None, **kwargs):
        url = f"{base or self.base_url}{path}"
        try:
            response = self._session.request(method, url, timeout=self.timeout, **kwargs)
        except requests.RequestException as exc:
            raise RuntimeError(f"Alpaca request failed ({method} {path}): {exc}") from exc

        if response.status_code == 401:
            raise RuntimeError("Alpaca rejected the credentials (HTTP 401)")
        if response.status_code == 403:
            raise RuntimeError(f"Alpaca forbade the request (HTTP 403): {response.text[:200]}")
        if response.status_code >= 400:
            raise RuntimeError(
                f"Alpaca HTTP {response.status_code} on {method} {path}: {response.text[:300]}"
            )
        if not response.content:
            return None
        return response.json()

    # ---------------------------------------------------------------- interface

    def account(self) -> Account:
        payload = self._request("GET", "/v2/account")
        return Account(
            equity=float(payload.get("equity", 0.0)),
            cash=float(payload.get("cash", 0.0)),
            buying_power=float(payload.get("buying_power", 0.0)),
            currency=payload.get("currency", "USD"),
            blocked=bool(
                payload.get("trading_blocked")
                or payload.get("account_blocked")
                or payload.get("transfers_blocked")
            ),
        )

    def positions(self) -> pd.Series:
        payload = self._request("GET", "/v2/positions") or []
        held = {p["symbol"]: float(p["qty"]) for p in payload}
        return pd.Series(held, dtype=float)

    def submit(self, order: Order, reference_price: Optional[float] = None) -> OrderResult:
        quantity = abs(order.quantity)
        if quantity <= 0:
            return OrderResult(order=order, accepted=False, status="rejected",
                               message="zero quantity")

        body: Dict = {
            "symbol": order.symbol,
            "side": order.side,
            "type": order.order_type,
            "time_in_force": order.time_in_force,
            "qty": f"{quantity:.9f}".rstrip("0").rstrip("."),
        }
        if order.client_order_id:
            body["client_order_id"] = order.client_order_id
        if order.order_type == "limit":
            limit = order.limit_price or reference_price
            if limit is None:
                return OrderResult(order=order, accepted=False, status="rejected",
                                   message="limit order without a price")
            body["limit_price"] = round(float(limit), 2)

        try:
            payload = self._request("POST", "/v2/orders", json=body)
        except RuntimeError as exc:
            return OrderResult(order=order, accepted=False, status="rejected", message=str(exc))

        filled = float(payload.get("filled_qty") or 0.0)
        fill_price = payload.get("filled_avg_price")
        return OrderResult(
            order=order,
            accepted=True,
            broker_order_id=payload.get("id"),
            filled_quantity=filled * (1 if order.quantity > 0 else -1),
            filled_price=float(fill_price) if fill_price else None,
            status=payload.get("status", "accepted"),
            message=f"submitted {order.side} {quantity} {order.symbol}",
        )

    def cancel_open_orders(self) -> int:
        payload = self._request("DELETE", "/v2/orders")
        count = len(payload) if isinstance(payload, list) else 0
        if count:
            log.info("cancelled %d resting order(s)", count)
        return count

    def is_market_open(self) -> bool:
        try:
            payload = self._request("GET", "/v2/clock")
            return bool(payload.get("is_open", False))
        except RuntimeError as exc:
            log.warning("could not read the market clock: %s", exc)
            return False

    # ------------------------------------------------------------------ prices

    def last_prices(self, symbols: List[str]) -> pd.Series:
        """Latest trade price per symbol, from Alpaca's market data API."""
        if not symbols:
            return pd.Series(dtype=float)
        try:
            payload = self._request(
                "GET", "/v2/stocks/trades/latest", base=_DATA_URL,
                params={"symbols": ",".join(symbols)},
            )
        except RuntimeError as exc:
            log.warning("latest-price lookup failed: %s", exc)
            return pd.Series(dtype=float)

        trades = (payload or {}).get("trades", {})
        prices = {symbol: float(t["p"]) for symbol, t in trades.items() if t.get("p")}
        return pd.Series(prices, dtype=float)

    def next_open(self) -> Optional[datetime]:
        try:
            payload = self._request("GET", "/v2/clock")
            return pd.Timestamp(payload["next_open"]).to_pydatetime()
        except Exception:
            return None
