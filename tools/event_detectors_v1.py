"""Independent EOD primitives for the event catalogue; no scores or orders.

Inputs are values already available at the decision time. Indicator calculation,
calendar alignment and data loading belong to an adapter, not these detectors.
None means unknown, including a missing previous bar. No future bar is accepted.
"""
from dataclasses import dataclass, field
from math import isfinite
from typing import Mapping


PERIODS = (20, 50, 100, 200)
BREADTH_KEYS = ("B50", "B100", "B200", "BN")


@dataclass(frozen=True)
class Bar:
    open: float | None = None
    high: float | None = None
    low: float | None = None
    close: float | None = None
    ema: Mapping[int, float | None] = field(default_factory=dict)
    rsi: Mapping[str, float | None] = field(default_factory=dict)
    breadth_close: Mapping[str, float | None] = field(default_factory=dict)
    breadth_open: Mapping[str, float | None] = field(default_factory=dict)
    # Only actual breadth OHLC, never Open synthesized from Close.
    breadth_ohlc_verified: bool = False


def known(*values):
    return all(v is not None and isfinite(v) for v in values)


def calc(function, *values):
    return function(*values) if known(*values) else None


def conjunction(*values):
    # Conservative completeness: a missing prerequisite is not a false signal.
    return None if any(v is None for v in values) else all(values)


def cross(previous_value, previous_level, value, level, *, up):
    return calc(
        (lambda p, q, x, y: p <= q and x > y) if up else
        (lambda p, q, x, y: p >= q and x < y),
        previous_value, previous_level, value, level,
    )


def validate_bar(bar):
    for x in (bar.open, bar.high, bar.low, bar.close, *bar.ema.values()):
        if x is not None and (not isfinite(x) or x <= 0):
            raise ValueError("Prices and EMA must be positive finite numbers or None")
    if known(bar.open, bar.high, bar.low, bar.close):
        if not bar.low <= min(bar.open, bar.close) <= max(bar.open, bar.close) <= bar.high:
            raise ValueError("Inconsistent OHLC")
    for x in bar.rsi.values():
        if x is not None and (not isfinite(x) or not -1 <= x <= 1):
            raise ValueError("Normalized MyRSI must be in [-1, 1] or None")
    for x in (*bar.breadth_close.values(), *bar.breadth_open.values()):
        if x is not None and (not isfinite(x) or not 0 <= x <= 100):
            raise ValueError("Breadth must be in [0, 100] or None")


def detect(current: Bar, previous: Bar | None = None, *, rsi_epsilon=0.001):
    """Return per-day measurements and flags, not complete lifecycle detectors.

    RSI tolerance is an explicit provisional parameter, not a fitted weight.
    Call only with adjacent completed trading bars; do not skip a missing bar.
    """
    if not isfinite(rsi_epsilon) or not 0 <= rsi_epsilon < 1:
        raise ValueError("rsi_epsilon must be finite and in [0, 1)")
    validate_bar(current)
    if previous is not None:
        validate_bar(previous)
    p = previous or Bar()
    c = current
    out = {}
    order = [calc(lambda a, b: a > b, c.ema.get(a), c.ema.get(b))
             for a, b in zip(PERIODS, PERIODS[1:])]
    out["T01.order"] = conjunction(*order)
    out["T01.price_above_20"] = calc(lambda a, b: a > b, c.close, c.ema.get(20))
    slopes = []
    below = []
    for n in PERIODS:
        e, old_e = c.ema.get(n), p.ema.get(n)
        slope = calc(lambda x, y: x - y, e, old_e)
        slopes.append(slope)
        out[f"T02.slope_{n}"] = slope
        below.append(calc(lambda x, y: x < y, c.close, e))
        out[f"E01.below_{n}"] = below[-1]
        out[f"E01.cross_down_{n}"] = cross(p.close, old_e, c.close, e, up=False)
        out[f"E05.cross_up_{n}"] = cross(p.close, old_e, c.close, e, up=True)
        out[f"E02.wick_hold_{n}"] = calc(lambda l, x, y: l < y and x >= y, c.low, c.close, e)
    out["T02.all_rising"] = calc(lambda *s: all(x > 0 for x in s), *slopes)
    out["T04.only_20_falling"] = calc(lambda a, b, d, e: a < 0 and min(b, d, e) > 0, *slopes)
    out["T04.fast_pair_falling"] = calc(lambda a, b, d, e: max(a, b) < 0 and min(d, e) > 0, *slopes)
    out["T07.below_all"] = conjunction(*below)
    out["E03.double_break"] = conjunction(out["E01.cross_down_20"], out["E01.cross_down_50"])
    for name in ("slow", "fast"):
        value, old = c.rsi.get(name), p.rsi.get(name)
        lower, upper = -1 + rsi_epsilon, 1 - rsi_epsilon
        out[f"R01.{name}"] = calc(lambda x: x <= lower, value)
        out[f"R02.{name}"] = calc(lambda x: x >= upper, value)
        out[f"R03.{name}"] = cross(old, lower, value, lower, up=True)
        out[f"R04.{name}"] = cross(old, upper, value, upper, up=False)
        out[f"R05.up_{name}"] = cross(old, 0, value, 0, up=True)
        out[f"R05.down_{name}"] = cross(old, 0, value, 0, up=False)
    out["R07.both_lower"] = conjunction(out["R01.slow"], out["R01.fast"])
    out["R07.both_upper"] = conjunction(out["R02.slow"], out["R02.fast"])
    out["R08.both_leave_upper"] = conjunction(out["R04.slow"], out["R04.fast"])
    for key in BREADTH_KEYS:
        value, old = c.breadth_close.get(key), p.breadth_close.get(key)
        out[f"B01.{key}"] = value
        out[f"B04.delta_pp_{key}"] = calc(lambda x, y: x - y, value, old)
        out[f"B04.rising_{key}"] = calc(lambda x, y: x > y, value, old)
        out[f"B05.green_{key}"] = (
            calc(lambda x, y: x > y, value, c.breadth_open.get(key))
            if c.breadth_ohlc_verified else None
        )
    out["P01.body_pct"] = calc(lambda x, o: 100 * (x - o) / o, c.close, c.open)
    out["P03.true_range"] = calc(lambda h, l, pc: max(h-l, abs(h-pc), abs(l-pc)), c.high, c.low, p.close)
    out["P03.atrp1"] = calc(lambda tr, x: 100 * tr / x, out["P03.true_range"], c.close)
    return out
