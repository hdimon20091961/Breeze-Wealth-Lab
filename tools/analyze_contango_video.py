#!/usr/bin/env python3
"""Reproducible event study for the Contango 1 video and Breeze exports."""

from __future__ import annotations

import argparse
import json
import math
from pathlib import Path

import numpy as np
import pandas as pd


HORIZONS = (1, 5, 10, 20, 40, 60)
SCREEN_EPISODES = [
    ("00:00:51", "2026-03-02", "intro; ordinary positive term structure"),
    ("00:08:27", "2026-05-07", "normal contango and 30-60 roll example"),
    ("00:16:00", "2026-06-16", "steep front contango; spot VIX below futures"),
    ("00:21:37", "2026-03-31", "broad backwardation after market decline"),
    ("00:29:36", "2026-07-23", "normal contango with MyRSI weakening"),
    ("00:35:29", "2026-02-10", "pre-decline normal contango and breadth overlays"),
    ("00:45:22", "2026-03-30", "bottom lead: breadth/price turn while both MyRSI remain -1"),
    ("00:47:06", "2026-03-31", "Phase Map turns strong-buy while MyRSI remains -1"),
    ("00:48:40", "2026-04-02", "recovery continues; front term structure still mixed"),
    ("00:52:32", "2026-04-08", "contango restored through the visible curve"),
    ("00:57:00", "2025-09-19", "calm bull regime: VIX 15.45, positive contango"),
    ("01:02:35", "2025-02-20", "Titanic first active day near a local high"),
    ("01:05:01", "2025-02-19", "day before Titanic activation"),
    ("01:09:37", "2025-02-26", "Titanic active day 5 during decline"),
    ("01:11:06", "2025-03-03", "Titanic active day 8; persistent dark-green phase"),
    ("01:12:34", "2020-03-20", "COVID crisis: VIX 66.04 and deep backwardation"),
    ("01:13:33", "2020-03-23", "COVID price low; MyRSI -1; backwardation"),
    ("01:14:44", "2020-03-23", "Titanic view at COVID low; inactive on the exact low"),
]


def load(path: Path) -> pd.DataFrame:
    frame = pd.read_csv(path)
    frame["Date"] = pd.to_datetime(frame["Date"], errors="coerce")
    return frame.dropna(subset=["Date"]).sort_values("Date").drop_duplicates("Date", keep="last")


def event_rows(asset: pd.DataFrame, dates: pd.Series, label: str) -> pd.DataFrame:
    prices = asset.set_index("Date")["Close"].astype(float)
    index = prices.index
    rows: list[dict] = []
    for date in pd.to_datetime(dates):
        pos = index.searchsorted(date)
        if pos >= len(index) or index[pos] != date:
            continue
        base = float(prices.iloc[pos])
        row: dict[str, object] = {"event": label, "Date": date.strftime("%Y-%m-%d"), "Close": base}
        for horizon in HORIZONS:
            if pos + horizon >= len(prices):
                row[f"ret_{horizon}d"] = math.nan
                row[f"max_drawdown_{horizon}d"] = math.nan
                row[f"max_gain_{horizon}d"] = math.nan
                continue
            path = prices.iloc[pos + 1 : pos + horizon + 1].to_numpy(dtype=float) / base - 1.0
            row[f"ret_{horizon}d"] = float(prices.iloc[pos + horizon] / base - 1.0)
            row[f"max_drawdown_{horizon}d"] = float(path.min())
            row[f"max_gain_{horizon}d"] = float(path.max())
        rows.append(row)
    return pd.DataFrame(rows)


def unconditional(asset: pd.DataFrame, horizon: int) -> np.ndarray:
    close = asset["Close"].astype(float).to_numpy()
    return close[horizon:] / close[:-horizon] - 1.0


def summarize(events: pd.DataFrame, asset: pd.DataFrame, label: str) -> list[dict]:
    result: list[dict] = []
    rng = np.random.default_rng(20261011)
    for horizon in HORIZONS:
        column = f"ret_{horizon}d"
        values = events[column].dropna().to_numpy(dtype=float) if column in events else np.array([])
        baseline = unconditional(asset, horizon)
        if len(values) == 0:
            continue
        random_means = np.array([
            rng.choice(baseline, size=len(values), replace=False).mean()
            for _ in range(5000)
        ])
        observed = float(values.mean())
        result.append(
            {
                "event": label,
                "horizon_days": horizon,
                "n": int(len(values)),
                "mean_return": observed,
                "median_return": float(np.median(values)),
                "positive_rate": float((values > 0).mean()),
                "baseline_mean": float(baseline.mean()),
                "mean_excess": float(observed - baseline.mean()),
                "randomization_two_sided_p": float(
                    (np.abs(random_means - baseline.mean()) >= abs(observed - baseline.mean())).mean()
                ),
            }
        )
    return result


def super_smoother(values: np.ndarray, period: int) -> np.ndarray:
    result = np.zeros_like(values, dtype=float)
    result[0] = values[0]
    if len(values) == 1:
        return result
    result[1] = values[1]
    a = math.exp(-math.sqrt(2.0) * math.pi / period)
    a1 = -2.0 * a * math.cos(math.sqrt(2.0) * math.pi / period)
    a2 = a * a
    b0 = (1.0 + a1 + a2) / 2.0
    for index in range(2, len(values)):
        result[index] = (
            b0 * values[index] + b0 * values[index - 1]
            - a1 * result[index - 1] - a2 * result[index - 2]
        )
    return result


def myrsi_agc(values: np.ndarray, rsi_period: int, smooth_period: int = 8, half_life: int = 77) -> np.ndarray:
    filtered = super_smoother(values, smooth_period)
    result = np.zeros_like(values, dtype=float)
    first_valid = max(smooth_period, rsi_period)
    decay = 0.5 ** (1.0 / half_life)
    peak = 1e-7
    for bar in range(first_valid, len(values)):
        differences = filtered[bar - rsi_period + 1 : bar + 1] - filtered[bar - rsi_period : bar]
        up = float(differences[differences > 0].sum())
        down = float(-differences[differences < 0].sum())
        total = up + down
        raw = 0.0 if total == 0 else (up - down) / total
        peak = max(peak * decay, abs(raw))
        result[bar] = float(np.clip(raw / peak, -1.0, 1.0))
    return result


def release_dates(values: np.ndarray, dates: pd.Series, side: str) -> pd.Series:
    previous = values[:-1]
    current = values[1:]
    if side == "low":
        mask = (previous <= -0.999) & (current > -0.98)
    else:
        mask = (previous >= 0.999) & (current < 0.98)
    return dates.iloc[1:].reset_index(drop=True)[mask]


def term_features(term: pd.DataFrame) -> pd.DataFrame:
    rows: list[dict] = []
    for _, source in term.iterrows():
        contracts = []
        for number in range(1, 13):
            days = source.get(f"M{number}_Days")
            price = source.get(f"M{number}_Price")
            if pd.notna(days) and pd.notna(price) and float(days) <= 180:
                contracts.append((float(days), float(price)))
        differences = np.diff([price for _, price in contracts]) if len(contracts) >= 2 else np.array([])
        rows.append(
            {
                "Date": source["Date"],
                "M0_VIX": source.get("M0_VIX"),
                "contracts_to_180": len(contracts),
                "inverted_adjacent_pairs_to_180": int((differences < 0).sum()),
                "positive_adjacent_pairs_to_180": int((differences > 0).sum()),
                "front_M1_M2_pct": (
                    differences[0] / contracts[0][1] * 100.0 if len(differences) else math.nan
                ),
                "Contango30To60Pct": source.get("Contango30To60Pct"),
            }
        )
    return pd.DataFrame(rows)


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("market_dir", type=Path)
    parser.add_argument("output_dir", type=Path)
    args = parser.parse_args()
    args.output_dir.mkdir(parents=True, exist_ok=True)

    spy = load(args.market_dir / "SPY.csv")
    svxy = load(args.market_dir / "SVXY.csv")
    titanic = load(args.market_dir / "titanic_sentiment.csv")
    fuzzy = load(args.market_dir / "fuzzy_market_states.csv")
    technical = load(args.market_dir / "spy_ema_myrsi_agc.csv")
    term = load(args.market_dir / "vix_term_structure.csv")

    signal = titanic["Signal"].astype(int).eq(1)
    persistent = titanic["Persistent"].astype(int).eq(1)
    event_sets = {
        "Titanic signal start": titanic.loc[signal & ~signal.shift(fill_value=False), "Date"],
        "Titanic persistent start (dark green)": titanic.loc[
            persistent & ~persistent.shift(fill_value=False), "Date"
        ],
        "Titanic signal end": titanic.loc[~signal & signal.shift(fill_value=False), "Date"],
    }

    all_event_rows = []
    all_summary = []
    for label, dates in event_sets.items():
        for asset_name, asset in (("SPY", spy), ("SVXY", svxy), ("QQQ", titanic.rename(columns={"QQQClose": "Close"}))):
            rows = event_rows(asset, dates, f"{label} — {asset_name}")
            all_event_rows.append(rows)
            all_summary.extend(summarize(rows, asset, f"{label} — {asset_name}"))
    events = pd.concat(all_event_rows, ignore_index=True)
    summary = pd.DataFrame(all_summary)
    events.to_csv(args.output_dir / "titanic_event_returns.csv", index=False)
    summary.to_csv(args.output_dir / "titanic_event_summary.csv", index=False)

    # Matched-context check: does a persistent transition add information beyond
    # the existing Phase Map state, drawdown, and slow MyRSI?
    spy_by_date = spy.set_index("Date")["Close"]
    context = fuzzy.merge(titanic[["Date", "Persistent"]], on="Date", how="inner")
    matched_rows = []
    for date in event_sets["Titanic persistent start (dark green)"]:
        current = context.loc[context["Date"].eq(date)]
        if current.empty or date not in spy_by_date.index:
            continue
        current = current.iloc[0]
        pool = context[
            context["Zone"].eq(current["Zone"])
            & context["Persistent"].astype(int).eq(0)
            & (context["Drawdown"].sub(float(current["Drawdown"])).abs() <= 0.025)
            & (context["MyRSI_rep_AGC"].sub(float(current["MyRSI_rep_AGC"])).abs() <= 0.25)
        ]
        for horizon in HORIZONS:
            event_return = event_rows(spy, pd.Series([date]), "event").get(f"ret_{horizon}d", pd.Series(dtype=float))
            candidate_returns = event_rows(spy, pool["Date"], "pool").get(f"ret_{horizon}d", pd.Series(dtype=float)).dropna()
            if event_return.empty or pd.isna(event_return.iloc[0]) or candidate_returns.empty:
                continue
            matched_rows.append(
                {
                    "Date": date.strftime("%Y-%m-%d"),
                    "Zone": current["Zone"],
                    "horizon_days": horizon,
                    "event_return": float(event_return.iloc[0]),
                    "matched_pool_n": int(len(candidate_returns)),
                    "matched_pool_median": float(candidate_returns.median()),
                    "event_excess_vs_matched_median": float(event_return.iloc[0] - candidate_returns.median()),
                }
            )
    matched = pd.DataFrame(matched_rows)
    matched.to_csv(args.output_dir / "titanic_phase_matched.csv", index=False)

    # Experimental 7/8 MyRSI, calculated without changing the DLL.
    closes = spy["Close"].astype(float).to_numpy()
    myrsi7 = myrsi_agc(closes, 7)
    myrsi10 = myrsi_agc(closes, 10)
    myrsi14 = myrsi_agc(closes, 14)
    technical_by_date = technical.set_index("Date")
    validation = spy[["Date"]].copy()
    validation["MyRSI_7_8"] = myrsi7
    validation["MyRSI_10_8_recomputed"] = myrsi10
    validation["MyRSI_14_8_recomputed"] = myrsi14
    validation = validation.join(
        technical_by_date[["MyRSI_rep_AGC_10_8", "MyRSI_rep_AGC_14_8"]], on="Date"
    )
    validation.to_csv(args.output_dir / "experimental_myrsi_7_8.csv", index=False)
    myrsi_summary = []
    for period, values in ((7, myrsi7), (10, myrsi10), (14, myrsi14)):
        for side in ("low", "high"):
            dates = release_dates(values, spy["Date"], side)
            rows = event_rows(spy, dates, f"MyRSI {period}/8 release from {side}")
            all_event_rows.append(rows)
            myrsi_summary.extend(summarize(rows, spy, f"MyRSI {period}/8 release from {side}"))
    pd.DataFrame(myrsi_summary).to_csv(args.output_dir / "myrsi_period_comparison.csv", index=False)

    term_table = term_features(term)
    screen = pd.DataFrame(SCREEN_EPISODES, columns=["video_time", "Date", "role"])
    screen["Date"] = pd.to_datetime(screen["Date"])
    screen = screen.merge(term_table, on="Date", how="left")
    screen = screen.merge(
        fuzzy[["Date", "Zone", "Confidence", "Score", "SPY", "Drawdown", "MyRSI_rep_AGC"]],
        on="Date", how="left",
    )
    screen = screen.merge(
        titanic[["Date", "Signal", "RunLength", "Persistent"]], on="Date", how="left"
    )
    screen.to_csv(args.output_dir / "video_date_alignment.csv", index=False)

    tail = validation.dropna().tail(1000)
    payload = {
        "date_range": [spy["Date"].min().strftime("%Y-%m-%d"), spy["Date"].max().strftime("%Y-%m-%d")],
        "titanic_event_counts": {label: int(len(dates)) for label, dates in event_sets.items()},
        "myrsi_recalculation_mae_last_1000": {
            "10_8": float((tail["MyRSI_10_8_recomputed"] - tail["MyRSI_rep_AGC_10_8"]).abs().mean()),
            "14_8": float((tail["MyRSI_14_8_recomputed"] - tail["MyRSI_rep_AGC_14_8"]).abs().mean()),
        },
        "screen_episode_count": len(SCREEN_EPISODES),
        "notes": [
            "Event study is descriptive and does not establish causality.",
            "Persistent-start events are episode transitions, avoiding repeated counting of every dark-green day.",
            "The 7/8 variant is research-only and is not added to Breeze.",
        ],
    }
    (args.output_dir / "research_profile.json").write_text(
        json.dumps(payload, ensure_ascii=False, indent=2), encoding="utf-8"
    )


if __name__ == "__main__":
    main()
