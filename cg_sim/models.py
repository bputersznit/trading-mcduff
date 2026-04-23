from __future__ import annotations

from dataclasses import dataclass, field
from enum import Enum
from typing import Optional, Literal

Side = Literal["BID", "ASK", "BUY", "SELL"]


class EventType(str, Enum):
    ADD = "add"
    CANCEL = "cancel"
    MODIFY = "modify"
    TRADE = "trade"
    DEPTH = "depth"
    BAR_CLOSE = "bar_close"
    MARKER = "marker"


@dataclass(slots=True)
class MarketEvent:
    ts_event_ns: int
    event_type: EventType
    side: Optional[Side] = None
    price: Optional[float] = None
    size: Optional[int] = None
    order_id: Optional[int] = None
    flags: Optional[int] = None
    ts_recv_ns: Optional[int] = None
    source: str = "unknown"
    symbol: str = "MNQ"
    meta: dict = field(default_factory=dict)


@dataclass(slots=True)
class BookLevel:
    price: float
    total_size: int = 0
    order_count: int = 0
    added_size: int = 0
    canceled_size: int = 0
    traded_size: int = 0
    last_update_ns: int = 0


@dataclass(slots=True)
class SimOrder:
    ts_created_ns: int
    side: Literal["BUY", "SELL"]
    order_type: Literal["MARKET", "LIMIT", "STOP"]
    qty: int
    price: Optional[float] = None
    tif: str = "GTC"
    tag: str = ""


@dataclass(slots=True)
class FillEvent:
    ts_fill_ns: int
    side: Literal["BUY", "SELL"]
    qty: int
    price: float
    tag: str = ""
    fee: float = 0.0
    fill_type: str = "sim"


@dataclass(slots=True)
class PositionState:
    qty: int = 0
    avg_price: float = 0.0
    realized_pnl: float = 0.0
    unrealized_pnl: float = 0.0
