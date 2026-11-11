"""Compare entry-day and signal-day chandelier high-watermark initialization."""
from __future__ import annotations

import csv
import json
from dataclasses import dataclass
from datetime import date
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
PRICE_FILE = Path(r"D:\fear_greed_close\Breeze\SPY.csv")
SERIES_DIR = ROOT / "research/ensemble_fix_20261102"
PACKAGES = (
    "GPU-ZZ1",
    "GPU-ZZ2",
    "GPU-ZZ3",
    "Torch-ZZ1",
    "Torch-ZZ2",
    "Torch-ZZ1-J",
    "Torch-ZZ2-J",
    "Encog-ZZ1",
    "Encog-ZZ2",
)
START_DATE = date(2018, 7, 15)
STARTING_CAPITAL = 3000.0
STOP_FACTOR = 1.0 - 3.75 / 100.0
REENTRY_DELAY_BARS = 1


@dataclass
class Bar:
    day: date
    open: float
    high: float
    low: float
    close: float


@dataclass
class Lot:
    highest_high: float
    stop_for_bar: float | None = None


def load_prices() -> list[Bar]:
    with PRICE_FILE.open(newline="", encoding="utf-8-sig") as stream:
        return [
            Bar(
                date.fromisoformat(row["Date"]),
                float(row["Open"]),
                float(row["High"]),
                float(row["Low"]),
                float(row["Close"]),
            )
            for row in csv.DictReader(stream)
        ]


def load_signals(package: str) -> dict[date, float]:
    series_file = SERIES_DIR / f"{package}-validated-series.csv"
    with series_file.open(newline="", encoding="utf-8-sig") as stream:
        return {
            date.fromisoformat(row["Date"]): float(row["EnsemblePercent"])
            for row in csv.DictReader(stream)
        }


def simulate(prices: list[Bar], signals: dict[date, float], signal_bar_seed: bool) -> dict:
    start_index = next(index for index, bar in enumerate(prices) if bar.day >= START_DATE)
    end_index = max(index for index, bar in enumerate(prices) if bar.day in signals)
    lots: list[Lot] = []
    pending_target: int | None = None
    pending_signal_high: float | None = None
    last_stop_exit_index: int | None = None
    cash = 0.0
    entries = 0
    stop_units = 0
    stop_events: list[dict] = []
    equity_curve: list[tuple[date, float]] = []

    for index in range(start_index, end_index + 1):
        bar = prices[index]
        if pending_target is not None:
            target = max(0, min(10, pending_target))
            while len(lots) < target:
                cash -= bar.open
                seed = bar.open
                if signal_bar_seed and pending_signal_high is not None:
                    seed = max(seed, pending_signal_high)
                elif not signal_bar_seed:
                    seed = bar.high
                lots.append(Lot(seed))
                entries += 1
            while len(lots) > target:
                cash += bar.open
                lots.pop(0)
            pending_target = None
            pending_signal_high = None

        executed: list[float] = []
        for lot_index in range(len(lots) - 1, -1, -1):
            lot = lots[lot_index]
            if lot.stop_for_bar is None or bar.low > lot.stop_for_bar:
                continue
            fill = bar.open if bar.open <= lot.stop_for_bar else lot.stop_for_bar
            cash += fill
            executed.append(fill)
            lots.pop(lot_index)
        if executed:
            stop_units += len(executed)
            last_stop_exit_index = index
            stop_events.append(
                {
                    "date": bar.day.isoformat(),
                    "units": len(executed),
                    "minimum_fill": min(executed),
                    "maximum_fill": max(executed),
                }
            )

        for lot in lots:
            lot.highest_high = max(lot.highest_high, bar.high)

        desired = 10 if signals[bar.day] >= 50.0 else 0
        blocked = (
            last_stop_exit_index is not None
            and index - last_stop_exit_index < REENTRY_DELAY_BARS
        )
        pending_target = len(lots) if desired > len(lots) and blocked else desired
        pending_signal_high = bar.high

        place_stops = len(lots) <= desired
        for lot in lots:
            lot.stop_for_bar = lot.highest_high * STOP_FACTOR if place_stops else None

        equity_curve.append((bar.day, STARTING_CAPITAL + cash + len(lots) * bar.close))

    profit_percent = (cash + len(lots) * prices[end_index].close) / STARTING_CAPITAL * 100.0
    peak = STARTING_CAPITAL
    max_drawdown = 0.0
    for _, equity in equity_curve:
        peak = max(peak, equity)
        if peak > 0:
            max_drawdown = min(max_drawdown, (equity / peak - 1.0) * 100.0)
    next_stop = max((lot.stop_for_bar for lot in lots if lot.stop_for_bar is not None), default=None)
    return {
        "profit_percent": profit_percent,
        "max_drawdown_percent": max_drawdown,
        "entry_units": entries,
        "stop_exit_units": stop_units,
        "stop_event_count": len(stop_events),
        "final_quantity": len(lots),
        "next_session_stop": next_stop,
        "last_stop_events": stop_events[-5:],
    }


def main() -> None:
    prices = load_prices()
    rows = []
    for package in PACKAGES:
        signals = load_signals(package)
        current = simulate(prices, signals, signal_bar_seed=False)
        proposed = simulate(prices, signals, signal_bar_seed=True)
        rows.append(
            {
                "package": package,
                "period": [START_DATE.isoformat(), max(signals).isoformat()],
                "current": current,
                "proposed": proposed,
                "difference": {
                    key: proposed[key] - current[key]
                    for key in (
                        "profit_percent",
                        "max_drawdown_percent",
                        "entry_units",
                        "stop_exit_units",
                        "stop_event_count",
                    )
                },
            }
        )
    average_current = sum(row["current"]["profit_percent"] for row in rows) / len(rows)
    average_proposed = sum(row["proposed"]["profit_percent"] for row in rows) / len(rows)
    result = {
        "packages": list(PACKAGES),
        "mode": "quantum 0/10 at 50%",
        "period": [START_DATE.isoformat(), max(load_signals(PACKAGES[0])).isoformat()],
        "stop_percent": 3.75,
        "reentry_delay_bars": 1,
        "entry_day_protection": False,
        "rows": rows,
        "summary": {
            "average_current_profit_percent": average_current,
            "average_proposed_profit_percent": average_proposed,
            "average_difference_percentage_points": average_proposed - average_current,
            "improved_packages": sum(
                row["difference"]["profit_percent"] > 0 for row in rows
            ),
            "worsened_packages": sum(
                row["difference"]["profit_percent"] < 0 for row in rows
            ),
            "unchanged_packages": sum(
                row["difference"]["profit_percent"] == 0 for row in rows
            ),
        },
    }
    print(json.dumps(result, ensure_ascii=False, indent=2))


if __name__ == "__main__":
    main()
