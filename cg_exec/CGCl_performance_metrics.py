"""
Comprehensive Performance Metrics for Trading Simulation.

Tracks and calculates:
- PnL (realized, unrealized, total)
- Sharpe ratio, Sortino ratio
- Maximum drawdown
- Win rate, profit factor
- Average win/loss, risk/reward
- Fill quality metrics
- Slippage analysis
- Trade statistics
- Execution quality

Remember: CGCl_ prefix for Claude-generated files
"""

from __future__ import annotations

from dataclasses import dataclass, field
from typing import Literal, Optional
import math


@dataclass(slots=True)
class Trade:
    """A completed trade (entry + exit)."""

    # Trade identification
    trade_id: str
    entry_ts_ns: int
    exit_ts_ns: int

    # Trade details
    side: Literal["LONG", "SHORT"]
    quantity: int
    entry_price: float
    exit_price: float

    # PnL
    gross_pnl: float
    fees: float = 0.0
    net_pnl: float = 0.0

    # Execution quality
    entry_slippage_ticks: float = 0.0
    exit_slippage_ticks: float = 0.0
    entry_fill_type: str = "unknown"  # market, limit, stop
    exit_fill_type: str = "unknown"

    # Risk management
    max_adverse_excursion: float = 0.0  # MAE - worst drawdown during trade
    max_favorable_excursion: float = 0.0  # MFE - best profit during trade

    # Tags
    strategy_tag: str = ""
    setup_tag: str = ""

    def __post_init__(self):
        self.net_pnl = self.gross_pnl - self.fees

    @property
    def duration_seconds(self) -> float:
        return (self.exit_ts_ns - self.entry_ts_ns) / 1_000_000_000

    @property
    def is_winner(self) -> bool:
        return self.net_pnl > 0

    @property
    def return_on_capital(self) -> float:
        """Return as percentage of capital risked."""
        capital = self.entry_price * self.quantity
        return (self.net_pnl / capital * 100) if capital > 0 else 0.0


@dataclass
class PerformanceMetrics:
    """
    Comprehensive performance tracking and analysis.

    Calculates all standard trading metrics plus execution quality.
    """

    # Configuration
    tick_size: float = 0.25
    tick_value: float = 0.50  # MNQ: $0.50 per tick per contract
    initial_capital: float = 100000.0
    risk_free_rate: float = 0.04  # 4% annual risk-free rate

    # Trade history
    trades: list[Trade] = field(default_factory=list)

    # Position tracking
    current_position: int = 0
    current_avg_price: float = 0.0
    realized_pnl: float = 0.0

    # Equity curve
    equity_curve: list[tuple[int, float]] = field(default_factory=list)  # (ts_ns, equity)
    peak_equity: float = 0.0

    # Fill tracking
    total_fills: int = 0
    market_fills: int = 0
    limit_fills: int = 0
    stop_fills: int = 0

    def __post_init__(self):
        self.peak_equity = self.initial_capital
        self.equity_curve.append((0, self.initial_capital))

    def record_trade(self, trade: Trade) -> None:
        """Record a completed trade."""
        self.trades.append(trade)
        self.realized_pnl += trade.net_pnl

        # Update equity curve
        current_equity = self.initial_capital + self.realized_pnl
        self.equity_curve.append((trade.exit_ts_ns, current_equity))

        if current_equity > self.peak_equity:
            self.peak_equity = current_equity

        # Track fill types
        self.total_fills += 2  # Entry + exit
        if trade.entry_fill_type == "market":
            self.market_fills += 1
        elif trade.entry_fill_type == "limit":
            self.limit_fills += 1
        elif trade.entry_fill_type == "stop":
            self.stop_fills += 1

        if trade.exit_fill_type == "market":
            self.market_fills += 1
        elif trade.exit_fill_type == "limit":
            self.limit_fills += 1
        elif trade.exit_fill_type == "stop":
            self.stop_fills += 1

    def update_position(
        self,
        qty_signed: int,
        price: float,
        fees: float = 0.0,
        ts_ns: Optional[int] = None,
    ) -> None:
        """Update current position."""
        if self.current_position == 0:
            self.current_position = qty_signed
            self.current_avg_price = price
        elif (self.current_position > 0 and qty_signed > 0) or \
             (self.current_position < 0 and qty_signed < 0):
            # Adding to position
            new_qty = self.current_position + qty_signed
            self.current_avg_price = (
                (abs(self.current_position) * self.current_avg_price) +
                (abs(qty_signed) * price)
            ) / abs(new_qty)
            self.current_position = new_qty
        else:
            # Closing or flipping
            closing_qty = min(abs(self.current_position), abs(qty_signed))
            if self.current_position > 0:
                pnl = (price - self.current_avg_price) * closing_qty
            else:
                pnl = (self.current_avg_price - price) * closing_qty

            self.realized_pnl += (pnl - fees)
            self.current_position += qty_signed

            if self.current_position == 0:
                self.current_avg_price = 0.0
            else:
                self.current_avg_price = price

    def unrealized_pnl(self, current_price: float) -> float:
        """Calculate unrealized PnL on open position."""
        if self.current_position == 0:
            return 0.0

        if self.current_position > 0:
            return (current_price - self.current_avg_price) * self.current_position
        else:
            return (self.current_avg_price - current_price) * abs(self.current_position)

    def total_pnl(self, current_price: float) -> float:
        """Total PnL (realized + unrealized)."""
        return self.realized_pnl + self.unrealized_pnl(current_price)

    def current_equity(self, current_price: float) -> float:
        """Current account equity."""
        return self.initial_capital + self.total_pnl(current_price)

    # ==================== CORE METRICS ====================

    def total_trades(self) -> int:
        """Total number of completed trades."""
        return len(self.trades)

    def winning_trades(self) -> int:
        """Number of winning trades."""
        return sum(1 for t in self.trades if t.is_winner)

    def losing_trades(self) -> int:
        """Number of losing trades."""
        return sum(1 for t in self.trades if not t.is_winner)

    def win_rate(self) -> float:
        """Win rate as percentage."""
        total = self.total_trades()
        return (self.winning_trades() / total * 100) if total > 0 else 0.0

    def average_win(self) -> float:
        """Average winning trade PnL."""
        winners = [t.net_pnl for t in self.trades if t.is_winner]
        return sum(winners) / len(winners) if winners else 0.0

    def average_loss(self) -> float:
        """Average losing trade PnL."""
        losers = [t.net_pnl for t in self.trades if not t.is_winner]
        return sum(losers) / len(losers) if losers else 0.0

    def largest_win(self) -> float:
        """Largest winning trade."""
        winners = [t.net_pnl for t in self.trades if t.is_winner]
        return max(winners) if winners else 0.0

    def largest_loss(self) -> float:
        """Largest losing trade."""
        losers = [t.net_pnl for t in self.trades if not t.is_winner]
        return min(losers) if losers else 0.0

    def profit_factor(self) -> float:
        """Profit factor (gross wins / gross losses)."""
        gross_wins = sum(t.net_pnl for t in self.trades if t.is_winner)
        gross_losses = abs(sum(t.net_pnl for t in self.trades if not t.is_winner))
        return gross_wins / gross_losses if gross_losses > 0 else float('inf')

    def expectancy(self) -> float:
        """Expected value per trade."""
        total = self.total_trades()
        return self.realized_pnl / total if total > 0 else 0.0

    # ==================== RISK METRICS ====================

    def max_drawdown(self) -> tuple[float, float]:
        """
        Maximum drawdown in dollars and percentage.

        Returns:
            (max_dd_dollars, max_dd_percent)
        """
        if not self.equity_curve:
            return 0.0, 0.0

        peak = self.equity_curve[0][1]
        max_dd = 0.0
        max_dd_pct = 0.0

        for ts, equity in self.equity_curve:
            if equity > peak:
                peak = equity

            dd = peak - equity
            dd_pct = (dd / peak * 100) if peak > 0 else 0.0

            if dd > max_dd:
                max_dd = dd
                max_dd_pct = dd_pct

        return max_dd, max_dd_pct

    def sharpe_ratio(self, periods_per_year: int = 252) -> float:
        """
        Sharpe ratio (risk-adjusted return).

        Args:
            periods_per_year: Trading periods per year (252 for daily, 52 for weekly)
        """
        if len(self.trades) < 2:
            return 0.0

        returns = [t.return_on_capital for t in self.trades]
        avg_return = sum(returns) / len(returns)

        # Calculate standard deviation
        variance = sum((r - avg_return) ** 2 for r in returns) / (len(returns) - 1)
        std_dev = math.sqrt(variance)

        if std_dev == 0:
            return 0.0

        # Annualize
        annual_return = avg_return * math.sqrt(periods_per_year)
        annual_std = std_dev * math.sqrt(periods_per_year)

        return (annual_return - self.risk_free_rate * 100) / annual_std

    def sortino_ratio(self, periods_per_year: int = 252) -> float:
        """
        Sortino ratio (downside deviation only).

        Only penalizes downside volatility, not upside.
        """
        if len(self.trades) < 2:
            return 0.0

        returns = [t.return_on_capital for t in self.trades]
        avg_return = sum(returns) / len(returns)

        # Calculate downside deviation
        downside_returns = [r for r in returns if r < 0]
        if not downside_returns:
            return float('inf')

        downside_variance = sum(r ** 2 for r in downside_returns) / len(downside_returns)
        downside_dev = math.sqrt(downside_variance)

        if downside_dev == 0:
            return 0.0

        # Annualize
        annual_return = avg_return * math.sqrt(periods_per_year)
        annual_downside = downside_dev * math.sqrt(periods_per_year)

        return (annual_return - self.risk_free_rate * 100) / annual_downside

    def calmar_ratio(self) -> float:
        """Calmar ratio (return / max drawdown)."""
        max_dd, _ = self.max_drawdown()
        if max_dd == 0:
            return 0.0

        total_return = self.realized_pnl / self.initial_capital
        return total_return / (max_dd / self.initial_capital)

    # ==================== EXECUTION QUALITY ====================

    def average_slippage(self) -> dict:
        """Average slippage statistics."""
        if not self.trades:
            return {
                "avg_entry_slippage": 0.0,
                "avg_exit_slippage": 0.0,
                "avg_total_slippage": 0.0,
                "max_entry_slippage": 0.0,
                "max_exit_slippage": 0.0,
            }

        entry_slips = [t.entry_slippage_ticks for t in self.trades]
        exit_slips = [t.exit_slippage_ticks for t in self.trades]

        return {
            "avg_entry_slippage": sum(entry_slips) / len(entry_slips),
            "avg_exit_slippage": sum(exit_slips) / len(exit_slips),
            "avg_total_slippage": sum(entry_slips + exit_slips) / len(entry_slips + exit_slips),
            "max_entry_slippage": max(entry_slips) if entry_slips else 0.0,
            "max_exit_slippage": max(exit_slips) if exit_slips else 0.0,
        }

    def fill_type_distribution(self) -> dict:
        """Distribution of fill types."""
        return {
            "market_fills": self.market_fills,
            "limit_fills": self.limit_fills,
            "stop_fills": self.stop_fills,
            "total_fills": self.total_fills,
            "market_pct": (self.market_fills / self.total_fills * 100) if self.total_fills > 0 else 0.0,
            "limit_pct": (self.limit_fills / self.total_fills * 100) if self.total_fills > 0 else 0.0,
            "stop_pct": (self.stop_fills / self.total_fills * 100) if self.total_fills > 0 else 0.0,
        }

    def mae_mfe_analysis(self) -> dict:
        """MAE (Max Adverse Excursion) and MFE (Max Favorable Excursion) analysis."""
        if not self.trades:
            return {
                "avg_mae": 0.0,
                "avg_mfe": 0.0,
                "avg_mae_winners": 0.0,
                "avg_mae_losers": 0.0,
                "avg_mfe_winners": 0.0,
                "avg_mfe_losers": 0.0,
            }

        winners = [t for t in self.trades if t.is_winner]
        losers = [t for t in self.trades if not t.is_winner]

        return {
            "avg_mae": sum(t.max_adverse_excursion for t in self.trades) / len(self.trades),
            "avg_mfe": sum(t.max_favorable_excursion for t in self.trades) / len(self.trades),
            "avg_mae_winners": sum(t.max_adverse_excursion for t in winners) / len(winners) if winners else 0.0,
            "avg_mae_losers": sum(t.max_adverse_excursion for t in losers) / len(losers) if losers else 0.0,
            "avg_mfe_winners": sum(t.max_favorable_excursion for t in winners) / len(winners) if winners else 0.0,
            "avg_mfe_losers": sum(t.max_favorable_excursion for t in losers) / len(losers) if losers else 0.0,
        }

    # ==================== REPORTING ====================

    def summary_dict(self, current_price: float = 0.0) -> dict:
        """Complete performance summary."""
        max_dd, max_dd_pct = self.max_drawdown()
        slippage = self.average_slippage()
        fills = self.fill_type_distribution()
        mae_mfe = self.mae_mfe_analysis()

        return {
            # PnL
            "initial_capital": self.initial_capital,
            "realized_pnl": self.realized_pnl,
            "unrealized_pnl": self.unrealized_pnl(current_price) if current_price > 0 else 0.0,
            "total_pnl": self.total_pnl(current_price) if current_price > 0 else self.realized_pnl,
            "current_equity": self.current_equity(current_price) if current_price > 0 else self.initial_capital + self.realized_pnl,
            "return_pct": (self.realized_pnl / self.initial_capital * 100),

            # Trade stats
            "total_trades": self.total_trades(),
            "winning_trades": self.winning_trades(),
            "losing_trades": self.losing_trades(),
            "win_rate": self.win_rate(),

            # Win/Loss analysis
            "average_win": self.average_win(),
            "average_loss": self.average_loss(),
            "largest_win": self.largest_win(),
            "largest_loss": self.largest_loss(),
            "profit_factor": self.profit_factor(),
            "expectancy": self.expectancy(),

            # Risk metrics
            "max_drawdown_dollars": max_dd,
            "max_drawdown_pct": max_dd_pct,
            "sharpe_ratio": self.sharpe_ratio(),
            "sortino_ratio": self.sortino_ratio(),
            "calmar_ratio": self.calmar_ratio(),

            # Execution quality
            **slippage,
            **fills,
            **mae_mfe,

            # Position
            "current_position": self.current_position,
            "current_avg_price": self.current_avg_price,
        }

    def print_summary(self, current_price: float = 0.0) -> None:
        """Print formatted performance summary."""
        summary = self.summary_dict(current_price)

        print("\n" + "=" * 80)
        print("PERFORMANCE SUMMARY")
        print("=" * 80)

        print("\n[PNL & RETURNS]")
        print(f"  Initial Capital:      ${summary['initial_capital']:,.2f}")
        print(f"  Realized PnL:         ${summary['realized_pnl']:,.2f}")
        print(f"  Unrealized PnL:       ${summary['unrealized_pnl']:,.2f}")
        print(f"  Total PnL:            ${summary['total_pnl']:,.2f}")
        print(f"  Current Equity:       ${summary['current_equity']:,.2f}")
        print(f"  Return:               {summary['return_pct']:.2f}%")

        print("\n[TRADE STATISTICS]")
        print(f"  Total Trades:         {summary['total_trades']}")
        print(f"  Winning Trades:       {summary['winning_trades']}")
        print(f"  Losing Trades:        {summary['losing_trades']}")
        print(f"  Win Rate:             {summary['win_rate']:.1f}%")

        print("\n[WIN/LOSS ANALYSIS]")
        print(f"  Average Win:          ${summary['average_win']:.2f}")
        print(f"  Average Loss:         ${summary['average_loss']:.2f}")
        print(f"  Largest Win:          ${summary['largest_win']:.2f}")
        print(f"  Largest Loss:         ${summary['largest_loss']:.2f}")
        print(f"  Profit Factor:        {summary['profit_factor']:.2f}")
        print(f"  Expectancy:           ${summary['expectancy']:.2f}")

        print("\n[RISK METRICS]")
        print(f"  Max Drawdown:         ${summary['max_drawdown_dollars']:.2f} ({summary['max_drawdown_pct']:.1f}%)")
        print(f"  Sharpe Ratio:         {summary['sharpe_ratio']:.2f}")
        print(f"  Sortino Ratio:        {summary['sortino_ratio']:.2f}")
        print(f"  Calmar Ratio:         {summary['calmar_ratio']:.2f}")

        print("\n[EXECUTION QUALITY]")
        print(f"  Avg Entry Slippage:   {summary['avg_entry_slippage']:.2f} ticks")
        print(f"  Avg Exit Slippage:    {summary['avg_exit_slippage']:.2f} ticks")
        print(f"  Market Fills:         {summary['market_fills']} ({summary['market_pct']:.1f}%)")
        print(f"  Limit Fills:          {summary['limit_fills']} ({summary['limit_pct']:.1f}%)")
        print(f"  Stop Fills:           {summary['stop_fills']} ({summary['stop_pct']:.1f}%)")

        print("\n[MAE/MFE ANALYSIS]")
        print(f"  Avg MAE:              ${summary['avg_mae']:.2f}")
        print(f"  Avg MFE:              ${summary['avg_mfe']:.2f}")
        print(f"  Avg MAE (Winners):    ${summary['avg_mae_winners']:.2f}")
        print(f"  Avg MFE (Winners):    ${summary['avg_mfe_winners']:.2f}")

        print("\n" + "=" * 80)
