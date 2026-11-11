#!/usr/bin/env python3
"""Study SPX/VIX monthly expirations, volatility, direction and EMA20 events."""

from __future__ import annotations

import csv
import math
from dataclasses import dataclass
from datetime import date, timedelta
from pathlib import Path

import numpy as np


ROOT = Path(r"D:\fear_greed_close\Breeze")
OUT = Path("research/artifacts/expiration-ema20-study")
RNG = np.random.default_rng(20261020)
PERMUTATIONS = 20_000


def read_csv(path: Path) -> list[dict[str, str]]:
    with path.open(encoding="utf-8-sig", newline="") as handle:
        return list(csv.DictReader(handle))


def third_friday(year: int, month: int) -> date:
    first = date(year, month, 1)
    return first + timedelta(days=(4 - first.weekday()) % 7 + 14)


def next_month(year: int, month: int) -> tuple[int, int]:
    return (year + 1, 1) if month == 12 else (year, month + 1)


def previous_trading_day(day: date, trading_days: set[date]) -> date | None:
    for _ in range(10):
        if day in trading_days:
            return day
        day -= timedelta(days=1)
    return None


def monthly_event_dates(start: date, end: date, trading_days: set[date]) -> tuple[set[date], set[date]]:
    spx: set[date] = set()
    vix: set[date] = set()
    cursor = date(start.year, start.month, 1)
    final = date(end.year, end.month, 1)
    while cursor <= final:
        friday = third_friday(cursor.year, cursor.month)
        adjusted_friday = previous_trading_day(friday, trading_days)
        if adjusted_friday is not None and start <= adjusted_friday <= end:
            spx.add(adjusted_friday)

        ny, nm = next_month(cursor.year, cursor.month)
        following_friday = third_friday(ny, nm)
        adjusted_following = previous_trading_day(following_friday, trading_days)
        if adjusted_following is not None:
            settlement = previous_trading_day(adjusted_following - timedelta(days=30), trading_days)
            if settlement is not None and start <= settlement <= end:
                vix.add(settlement)
        cursor = date(ny, nm, 1)
    return spx, vix


def permutation_p(event: np.ndarray, other: np.ndarray) -> float:
    event = event[np.isfinite(event)]
    other = other[np.isfinite(other)]
    observed = abs(float(event.mean() - other.mean()))
    pool = np.concatenate([event, other])
    count = 0
    n = len(event)
    for _ in range(PERMUTATIONS):
        shuffled = RNG.permutation(pool)
        if abs(float(shuffled[:n].mean() - shuffled[n:].mean())) >= observed - 1e-15:
            count += 1
    return (count + 1) / (PERMUTATIONS + 1)


def log_choose(n: int, k: int) -> float:
    if k < 0 or k > n:
        return -math.inf
    return math.lgamma(n + 1) - math.lgamma(k + 1) - math.lgamma(n - k + 1)


def fisher_exact(a: int, b: int, c: int, d: int) -> float:
    row1, row2, col1 = a + b, c + d, a + c
    total = row1 + row2

    def probability(x: int) -> float:
        return math.exp(log_choose(col1, x) + log_choose(total - col1, row1 - x) - log_choose(total, row1))

    observed = probability(a)
    lower = max(0, row1 - (total - col1))
    upper = min(row1, col1)
    return min(1.0, sum(probability(x) for x in range(lower, upper + 1) if probability(x) <= observed + 1e-14))


def wilson(successes: int, total: int) -> tuple[float, float]:
    if total == 0:
        return (math.nan, math.nan)
    z = 1.959963984540054
    p = successes / total
    denominator = 1 + z * z / total
    center = (p + z * z / (2 * total)) / denominator
    half = z * math.sqrt(p * (1 - p) / total + z * z / (4 * total * total)) / denominator
    return center - half, center + half


@dataclass
class TestResult:
    family: str
    event: str
    metric: str
    n_event: int
    event_value: float
    n_control: int
    control_value: float
    difference: float
    p: float
    q: float = math.nan
    unit: str = ""


def bh_adjust(results: list[TestResult]) -> None:
    order = sorted(range(len(results)), key=lambda i: results[i].p)
    running = 1.0
    m = len(results)
    for rank_from_end, index in enumerate(reversed(order), start=1):
        rank = m - rank_from_end + 1
        running = min(running, results[index].p * m / rank)
        results[index].q = running


def main() -> None:
    price_rows = read_csv(ROOT / "SPY.csv")
    ema_rows = {date.fromisoformat(row["Date"]): row for row in read_csv(ROOT / "spy_ema_myrsi_agc.csv")}
    prices = []
    for row in price_rows:
        day = date.fromisoformat(row["Date"])
        if day not in ema_rows:
            continue
        prices.append({
            "date": day,
            "open": float(row["Open"]),
            "high": float(row["High"]),
            "low": float(row["Low"]),
            "close": float(row["Close"]),
            "ema20": float(ema_rows[day]["EMA20"]),
            "ema50": float(ema_rows[day]["EMA50"]),
        })
    trading_days = {row["date"] for row in prices}
    spx_dates, vix_dates = monthly_event_dates(prices[0]["date"], prices[-1]["date"], trading_days)

    rows = []
    for i in range(1, len(prices) - 1):
        previous, current, following = prices[i - 1], prices[i], prices[i + 1]
        prev_close = previous["close"]
        cc = current["close"] / prev_close - 1
        prior_cc = previous["close"] / prices[i - 2]["close"] - 1 if i >= 2 else math.nan
        next_cc = following["close"] / current["close"] - 1
        down_cross20 = previous["close"] >= previous["ema20"] and current["close"] < current["ema20"]
        only20_down = down_cross20 and current["close"] >= current["ema50"]
        intraday20_hold = current["low"] < current["ema20"] and current["close"] >= current["ema20"]
        only20_intraday_hold = intraday20_hold and current["low"] >= current["ema50"]
        rows.append({
            **current,
            "cc": cc,
            "oc": current["close"] / current["open"] - 1,
            "abs_cc": abs(cc),
            "range": (current["high"] - current["low"]) / prev_close,
            "true_range": max(current["high"] - current["low"], abs(current["high"] - prev_close), abs(current["low"] - prev_close)) / prev_close,
            "prior_cc": prior_cc,
            "next_cc": next_cc,
            "negative_day": cc < 0,
            "next_reversal": cc * next_cc < 0,
            "next_continuation": cc * next_cc > 0,
            "next_reclaim20": following["close"] >= following["ema20"],
            "down_cross20": down_cross20,
            "only20_down": only20_down,
            "intraday20_hold": intraday20_hold,
            "only20_intraday_hold": only20_intraday_hold,
            "spx_expiry": current["date"] in spx_dates,
            "vix_expiry": current["date"] in vix_dates,
        })

    tests: list[TestResult] = []
    continuous = [
        ("доходность закрытие/закрытие", "cc", "%"),
        ("модуль дневной доходности", "abs_cc", "%"),
        ("дневной диапазон High–Low", "range", "%"),
        ("истинный диапазон с учётом гэпа", "true_range", "%"),
        ("доходность следующего дня", "next_cc", "%"),
    ]
    proportions = [
        ("день закрылся снижением", "negative_day"),
        ("разворот направления на следующий день", "next_reversal"),
        ("продолжение направления на следующий день", "next_continuation"),
        ("закрытие пересекло EMA20 сверху вниз", "down_cross20"),
        ("закрытие пробило только EMA20, оставшись выше EMA50", "only20_down"),
        ("минимум проколол EMA20, но закрытие осталось выше", "intraday20_hold"),
        ("внутридневной прокол только EMA20 без касания EMA50", "only20_intraday_hold"),
    ]
    for event_name, event_key, regular_weekday in [
        ("третья пятница SPX", "spx_expiry", 4),
        ("месячная экспирация VIX", "vix_expiry", 2),
    ]:
        # The primary inference compares like with like: regular Friday SPX
        # expirations against other Fridays, and regular Wednesday VIX
        # settlements against other Wednesdays. Holiday-shifted events remain
        # in the descriptive event-time profiles below.
        event_rows = [row for row in rows if row[event_key] and row["date"].weekday() == regular_weekday]
        control_rows = [row for row in rows if not row[event_key] and row["date"].weekday() == regular_weekday]
        for metric_name, key, unit in continuous:
            event_values = np.array([row[key] for row in event_rows], dtype=float)
            control_values = np.array([row[key] for row in control_rows], dtype=float)
            tests.append(TestResult(
                "основная", event_name, metric_name, len(event_values), float(event_values.mean() * 100),
                len(control_values), float(control_values.mean() * 100),
                float((event_values.mean() - control_values.mean()) * 100), permutation_p(event_values, control_values), unit=unit))
        for metric_name, key in proportions:
            a = sum(bool(row[key]) for row in event_rows)
            b = len(event_rows) - a
            c = sum(bool(row[key]) for row in control_rows)
            d = len(control_rows) - c
            tests.append(TestResult(
                "основная", event_name, metric_name, len(event_rows), 100 * a / len(event_rows),
                len(control_rows), 100 * c / len(control_rows),
                100 * (a / len(event_rows) - c / len(control_rows)), fisher_exact(a, b, c, d), unit="п.п."))

        # Conditional next-day behavior after the two EMA20 patterns.
        for pattern_name, pattern_key in [
            ("после закрытия ниже только EMA20", "only20_down"),
            ("после внутридневного прокола только EMA20 с закрытием сверху", "only20_intraday_hold"),
        ]:
            event_pattern = [row for row in event_rows if row[pattern_key]]
            control_pattern = [row for row in control_rows if row[pattern_key]]
            if not event_pattern or not control_pattern:
                continue
            a = sum(bool(row["next_reclaim20"]) for row in event_pattern)
            b = len(event_pattern) - a
            c = sum(bool(row["next_reclaim20"]) for row in control_pattern)
            d = len(control_pattern) - c
            tests.append(TestResult(
                "условная EMA20", event_name, pattern_name + ": закрытие следующего дня выше EMA20",
                len(event_pattern), 100 * a / len(event_pattern), len(control_pattern), 100 * c / len(control_pattern),
                100 * (a / len(event_pattern) - c / len(control_pattern)), fisher_exact(a, b, c, d), unit="п.п."))
            event_next = np.array([row["next_cc"] for row in event_pattern])
            control_next = np.array([row["next_cc"] for row in control_pattern])
            tests.append(TestResult(
                "условная EMA20", event_name, pattern_name + ": доходность следующего дня",
                len(event_pattern), float(event_next.mean() * 100), len(control_pattern), float(control_next.mean() * 100),
                float((event_next.mean() - control_next.mean()) * 100), permutation_p(event_next, control_next), unit="%"))

    bh_adjust(tests)
    OUT.mkdir(parents=True, exist_ok=True)
    with (OUT / "tests.csv").open("w", encoding="utf-8-sig", newline="") as handle:
        writer = csv.writer(handle)
        writer.writerow(["Family", "Event", "Metric", "N_event", "Event_value", "N_control", "Control_value", "Difference", "P", "Q_BH", "Unit"])
        for result in tests:
            writer.writerow([result.family, result.event, result.metric, result.n_event, f"{result.event_value:.6f}",
                             result.n_control, f"{result.control_value:.6f}", f"{result.difference:.6f}",
                             f"{result.p:.8f}", f"{result.q:.8f}", result.unit])

    report = [
        "# Экспирации SPX/VIX, волатильность, направление и EMA20",
        "",
        f"Период: {prices[0]['date']} — {prices[-1]['date']}; торговых баров: {len(prices)}.",
        f"Третьих пятниц SPX в выборке: {len(spx_dates)}; месячных экспираций VIX: {len(vix_dates)}.",
        f"Перестановок для тестов средних: {PERMUTATIONS:,}; доли: двусторонний точный тест Фишера; q: Benjamini–Hochberg по всем {len(tests)} тестам.",
        "",
        "| Событие | Метрика | N | Событие | Контроль | Разница | p | q |",
        "|---|---|---:|---:|---:|---:|---:|---:|",
    ]
    for result in tests:
        report.append(
            f"| {result.event} | {result.metric} | {result.n_event} | {result.event_value:.2f}{result.unit} | "
            f"{result.control_value:.2f}{result.unit} | {result.difference:+.2f}{result.unit} | {result.p:.4f} | {result.q:.4f} |"
        )

    report.extend(["", "## Профиль вокруг события", "",
                   "Средние значения на торговых днях от −2 до +2 относительно события.", "",
                   "| Событие | Смещение | Доходность C/C | Модуль доходности | True Range | N |",
                   "|---|---:|---:|---:|---:|---:|"])
    index_by_date = {row["date"]: index for index, row in enumerate(rows)}
    for event_name, dates in [("третья пятница SPX", spx_dates), ("месячная экспирация VIX", vix_dates)]:
        for offset in range(-2, 3):
            sample = []
            for event_date in dates:
                index = index_by_date.get(event_date)
                if index is not None and 0 <= index + offset < len(rows):
                    sample.append(rows[index + offset])
            report.append(
                f"| {event_name} | {offset:+d} | {100*np.mean([row['cc'] for row in sample]):+.2f}% | "
                f"{100*np.mean([row['abs_cc'] for row in sample]):.2f}% | "
                f"{100*np.mean([row['true_range'] for row in sample]):.2f}% | {len(sample)} |"
            )

    report.extend(["", "## Направление и устойчивость по половинам выборки", "",
                   "| Событие/период | N | Доходность события | Разворот на следующий день | После отрицательного события: следующий день | После положительного события: следующий день |",
                   "|---|---:|---:|---:|---:|---:|"])
    for event_name, event_key in [("третья пятница SPX", "spx_expiry"), ("месячная экспирация VIX", "vix_expiry")]:
        for period_name, predicate in [
            ("вся выборка", lambda row: True),
            ("20.10.2014–31.12.2020", lambda row: row["date"] <= date(2020, 12, 31)),
            ("01.01.2021–19.10.2026", lambda row: row["date"] >= date(2021, 1, 1)),
        ]:
            sample = [row for row in rows if row[event_key] and predicate(row)]
            down = [row for row in sample if row["cc"] < 0]
            up = [row for row in sample if row["cc"] > 0]
            report.append(
                f"| {event_name}, {period_name} | {len(sample)} | {100*np.mean([row['cc'] for row in sample]):+.2f}% | "
                f"{100*np.mean([row['next_reversal'] for row in sample]):.1f}% | "
                f"{100*np.mean([row['next_cc'] for row in down]):+.2f}% (n={len(down)}) | "
                f"{100*np.mean([row['next_cc'] for row in up]):+.2f}% (n={len(up)}) |"
            )

    report.extend(["", "## Определения", "",
        "- Третья пятница SPX переносится на предыдущий торговый день, если пятница не торговая.",
        "- Месячная экспирация VIX: 30 календарных дней до скорректированной третьей пятницы следующего месяца; неторговая расчётная дата переносится назад.",
        "- «Только EMA20»: закрытие пересекает EMA20 сверху вниз, но остаётся не ниже EMA50.",
        "- «Внутридневной прокол только EMA20»: Low ниже EMA20, Close не ниже EMA20 и Low не касается EMA50.",
        "- Основной контроль согласован по дню недели: обычные третьи пятницы сравниваются с другими пятницами, обычные VIX-среды — с другими средами. Редкие праздничные переносы входят только в описательный профиль окна.",
        "- Причинность из календарного сравнения не следует; условные EMA20-выборки особенно малы.",
    ])
    (OUT / "report.md").write_text("\n".join(report) + "\n", encoding="utf-8")

    print(f"SPY bars={len(prices)} SPX expiries={len(spx_dates)} VIX expiries={len(vix_dates)} tests={len(tests)}")
    for result in tests:
        if result.q < 0.10 or result.family == "условная EMA20":
            print(f"{result.event} | {result.metric}: {result.event_value:.2f}{result.unit} vs {result.control_value:.2f}{result.unit}; p={result.p:.4f} q={result.q:.4f} n={result.n_event}")
    print(OUT / "report.md")


if __name__ == "__main__":
    main()
