#!/usr/bin/env python3
"""Event study of explicit SVXY entry hypotheses from the Contango video."""

from __future__ import annotations

import argparse
import math
from pathlib import Path

import numpy as np
import pandas as pd


HORIZONS = (1, 5, 10, 20, 40, 60)


def read(path: Path) -> pd.DataFrame:
    frame = pd.read_csv(path)
    frame["Date"] = pd.to_datetime(frame["Date"], errors="coerce")
    return frame.dropna(subset=["Date"]).sort_values("Date").drop_duplicates("Date", keep="last")


def curve_scores(term: pd.DataFrame) -> pd.DataFrame:
    rows: list[dict] = []
    for _, source in term.iterrows():
        contracts: list[tuple[float, float]] = []
        for number in range(1, 13):
            days = source.get(f"M{number}_Days")
            price = source.get(f"M{number}_Price")
            if pd.notna(days) and pd.notna(price) and float(days) <= 180:
                contracts.append((float(days), float(price)))
        differences = np.diff([price for _, price in contracts]) if len(contracts) >= 2 else np.array([])
        pairs = len(differences)
        rising = int((differences > 1e-6).sum())
        falling = int((differences < -1e-6).sum())
        balance = 0.0 if pairs == 0 else (rising - falling) / pairs
        contango = float(source.get("Contango30To60Pct", 0.0))
        score = 0.65 * balance + 0.35 * math.tanh(contango / 5.0)
        state = (
            "Strong contango" if score >= 0.60 else
            "Contango" if score >= 0.18 else
            "Broad backwardation" if score <= -0.60 else
            "Backwardation" if score <= -0.18 else
            "Mixed"
        )
        rows.append({"Date": source["Date"], "CurveScore": score, "CurveState": state})
    return pd.DataFrame(rows)


def summarize(asset: pd.DataFrame, event_dates: pd.Series, label: str) -> tuple[pd.DataFrame, pd.DataFrame]:
    prices = asset.set_index("Date")["Close"].astype(float)
    index = prices.index
    detail: list[dict] = []
    for date in pd.to_datetime(event_dates).drop_duplicates():
        position = index.searchsorted(date)
        if position >= len(index) or index[position] != date:
            continue
        base = float(prices.iloc[position])
        row: dict[str, object] = {"event": label, "Date": date.strftime("%Y-%m-%d"), "Close": base}
        for horizon in HORIZONS:
            if position + horizon >= len(prices):
                row[f"ret_{horizon}d"] = math.nan
                row[f"max_drawdown_{horizon}d"] = math.nan
                continue
            path = prices.iloc[position + 1:position + horizon + 1].to_numpy(float) / base - 1.0
            row[f"ret_{horizon}d"] = float(prices.iloc[position + horizon] / base - 1.0)
            row[f"max_drawdown_{horizon}d"] = float(path.min())
        detail.append(row)
    details = pd.DataFrame(detail)

    rng = np.random.default_rng(20261011)
    close = prices.to_numpy(float)
    summary: list[dict] = []
    for horizon in HORIZONS:
        values = details.get(f"ret_{horizon}d", pd.Series(dtype=float)).dropna().to_numpy(float)
        drawdowns = details.get(f"max_drawdown_{horizon}d", pd.Series(dtype=float)).dropna().to_numpy(float)
        baseline = close[horizon:] / close[:-horizon] - 1.0
        if len(values) == 0:
            continue
        random_means = np.array([
            rng.choice(baseline, size=len(values), replace=False).mean()
            for _ in range(5000)
        ])
        observed = float(values.mean())
        base_mean = float(baseline.mean())
        summary.append({
            "event": label,
            "horizon_days": horizon,
            "n": len(values),
            "mean_return": observed,
            "median_return": float(np.median(values)),
            "positive_rate": float((values > 0).mean()),
            "mean_max_drawdown": float(drawdowns.mean()),
            "baseline_mean": base_mean,
            "mean_excess": observed - base_mean,
            "randomization_two_sided_p": float(
                (np.abs(random_means - base_mean) >= abs(observed - base_mean)).mean()
            ),
        })
    return details, pd.DataFrame(summary)


def event_start(mask: pd.Series) -> pd.Series:
    return mask.fillna(False) & ~mask.shift(1, fill_value=False)


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("market_dir", type=Path)
    parser.add_argument("term_csv", type=Path)
    parser.add_argument("research_dir", type=Path)
    args = parser.parse_args()
    args.research_dir.mkdir(parents=True, exist_ok=True)

    svxy = read(args.market_dir / "SVXY.csv")
    term = curve_scores(read(args.term_csv))
    fuzzy = read(args.market_dir / "fuzzy_market_states.csv")
    indicators = read(args.market_dir / "spy_ema_myrsi_agc.csv")
    frame = svxy[["Date", "Close"]].merge(term, on="Date", how="inner")
    frame = frame.merge(fuzzy[["Date", "Zone"]], on="Date", how="left")
    frame = frame.merge(indicators[["Date", "MyRSI_rep_AGC_14_8", "MyRSI_rep_AGC_10_8"]], on="Date", how="left")

    phase_buy = frame["Zone"].isin(["Strong buy", "Buy"])
    phase_buy_start = event_start(phase_buy)
    curve_red = frame["CurveScore"] <= -0.18
    curve_exit_red = curve_red.shift(1, fill_value=False) & ~curve_red
    recent_red = frame["CurveScore"].shift(1).rolling(20, min_periods=1).min() <= -0.18
    strong_green_restored = (
        (frame["CurveScore"] >= 0.60)
        & (frame["CurveScore"].shift(1) < 0.60)
        & recent_red
    )
    curve_improving = frame["CurveScore"] - frame["CurveScore"].shift(3) >= 0.25
    phase_buy_curve_improving = event_start(phase_buy & curve_improving)
    strong_backwardation_start = event_start(frame["CurveScore"] <= -0.60)

    slow = frame["MyRSI_rep_AGC_14_8"]
    fast = frame["MyRSI_rep_AGC_10_8"]
    both_myrsi_release = (
        (slow.shift(1) <= -0.98) & (fast.shift(1) <= -0.98)
        & (slow > slow.shift(1) + 0.02) & (fast > fast.shift(1) + 0.02)
    )
    early_file = args.research_dir / "phase_candidate_events.csv"
    early_dates = read(early_file).query("event == 'Early Buy complex'")["Date"]

    hypotheses = {
        "Early Buy complex — SVXY": early_dates,
        "Phase Buy start — SVXY": frame.loc[phase_buy_start, "Date"],
        "Both MyRSI release from low — SVXY": frame.loc[both_myrsi_release, "Date"],
        "VIX curve exits red — SVXY": frame.loc[curve_exit_red, "Date"],
        "Strong contango restored after red — SVXY": frame.loc[strong_green_restored, "Date"],
        "Phase Buy + improving curve — SVXY": frame.loc[phase_buy_curve_improving, "Date"],
        "Strong backwardation starts — SVXY": frame.loc[strong_backwardation_start, "Date"],
    }

    details: list[pd.DataFrame] = []
    summaries: list[pd.DataFrame] = []
    for label, dates in hypotheses.items():
        detail, summary = summarize(svxy, dates, label)
        details.append(detail)
        summaries.append(summary)

    detail_frame = pd.concat(details, ignore_index=True)
    summary_frame = pd.concat(summaries, ignore_index=True)
    detail_frame.to_csv(args.research_dir / "svxy_entry_events.csv", index=False)
    summary_frame.to_csv(args.research_dir / "svxy_entry_summary.csv", index=False)
    term.to_csv(args.research_dir / "vix_curve_five_state_history.csv", index=False)
    print(summary_frame.to_string(index=False))


if __name__ == "__main__":
    main()
