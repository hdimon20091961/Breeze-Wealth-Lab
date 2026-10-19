#!/usr/bin/env python3
"""Evaluate the author's early-Buy and strong-Reduce-Risk complexes."""

from __future__ import annotations

import argparse
from pathlib import Path

import numpy as np
import pandas as pd


HORIZONS = (1, 5, 10, 20, 40, 60)


def read(path: Path) -> pd.DataFrame:
    frame = pd.read_csv(path)
    frame["Date"] = pd.to_datetime(frame["Date"])
    return frame.sort_values("Date").drop_duplicates("Date", keep="last")


def summarize(frame: pd.DataFrame, mask: pd.Series, name: str, direction: int) -> list[dict]:
    close = frame["Close"].to_numpy(float)
    positions = np.flatnonzero(mask.to_numpy(bool))
    rows: list[dict] = []
    rng = np.random.default_rng(20261011)
    for horizon in HORIZONS:
        valid = positions[positions + horizon < len(frame)]
        values = close[valid + horizon] / close[valid] - 1.0
        baseline = close[horizon:] / close[:-horizon] - 1.0
        if len(values) == 0:
            continue
        samples = np.array([
            rng.choice(baseline, size=len(values), replace=False).mean()
            for _ in range(5000)
        ])
        observed = float(values.mean())
        base = float(baseline.mean())
        rows.append({
            "event": name,
            "horizon_days": horizon,
            "n": len(values),
            "mean_return": observed,
            "median_return": float(np.median(values)),
            "positive_rate": float((values > 0).mean()),
            "baseline_mean": base,
            "directional_excess": direction * (observed - base),
            "randomization_two_sided_p": float(
                (np.abs(samples - base) >= abs(observed - base)).mean()
            ),
        })
    return rows


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("market_dir", type=Path)
    parser.add_argument("output_dir", type=Path)
    args = parser.parse_args()
    args.output_dir.mkdir(parents=True, exist_ok=True)

    spy = read(args.market_dir / "SPY.csv")
    indicators = read(args.market_dir / "spy_ema_myrsi_agc.csv")
    merged = spy.merge(indicators, on="Date", how="inner")
    for file_name, column in (
        ("sp500_sma50.csv", "SP50"),
        ("sp500_sma100.csv", "SP100"),
        ("sp500_sma200.csv", "SP200"),
        ("nasdaq100_sma100.csv", "Nasdaq100"),
    ):
        breadth = read(args.market_dir / file_name)[["Date", "Open", "Close"]]
        breadth = breadth.rename(columns={"Open": f"{column}Open", "Close": f"{column}Close"})
        merged = merged.merge(breadth, on="Date", how="inner")

    rolling_peak = merged["Close"].rolling(252, min_periods=20).max()
    drawdown = 1.0 - merged["Close"] / rolling_peak
    breadth_up_count = sum(
        merged[f"{name}Close"] > merged[f"{name}Open"]
        for name in ("SP50", "SP100", "SP200", "Nasdaq100")
    )
    both_low = (
        (merged["MyRSI_rep_AGC_14_8"] <= -0.98)
        & (merged["MyRSI_rep_AGC_10_8"] <= -0.98)
    )
    early_buy_raw = (
        both_low
        & (breadth_up_count >= 3)
        & (merged["Close"] > merged["Open"])
        & (drawdown >= 0.035)
    )
    early_buy = early_buy_raw & ~early_buy_raw.shift(1, fill_value=False)

    previous_slow = merged["MyRSI_rep_AGC_14_8"].shift(1)
    previous_fast = merged["MyRSI_rep_AGC_10_8"].shift(1)
    both_release_high = (
        (previous_slow >= 0.95)
        & (previous_fast >= 0.95)
        & (merged["MyRSI_rep_AGC_14_8"] < previous_slow - 0.02)
        & (merged["MyRSI_rep_AGC_10_8"] < previous_fast - 0.02)
    )
    red_body = (merged["Open"] - merged["Close"]).clip(lower=0)
    prior_body_norm = red_body.shift(1).rolling(20, min_periods=10).median()
    true_range = pd.concat(
        [
            merged["High"] - merged["Low"],
            (merged["High"] - merged["Close"].shift(1)).abs(),
            (merged["Low"] - merged["Close"].shift(1)).abs(),
        ],
        axis=1,
    ).max(axis=1)
    prior_tr_norm = true_range.shift(1).rolling(20, min_periods=10).median()
    large_red_day = (red_body > 2.0 * prior_body_norm) | (
        (merged["Close"] < merged["Open"]) & (true_range > 2.0 * prior_tr_norm)
    )
    reduce_risk_raw = both_release_high & (merged["Close"] < merged["EMA20"]) & large_red_day
    reduce_risk = reduce_risk_raw & ~reduce_risk_raw.shift(1, fill_value=False)

    events = pd.concat(
        [
            merged.loc[early_buy, ["Date", "Close"]].assign(event="Early Buy complex"),
            merged.loc[reduce_risk, ["Date", "Close"]].assign(event="Strong Reduce Risk complex"),
        ],
        ignore_index=True,
    ).sort_values("Date")
    events.to_csv(args.output_dir / "phase_candidate_events.csv", index=False)

    summary = summarize(merged, early_buy, "Early Buy complex", 1)
    summary += summarize(merged, reduce_risk, "Strong Reduce Risk complex", -1)
    pd.DataFrame(summary).to_csv(args.output_dir / "phase_candidate_summary.csv", index=False)

    print(f"Early Buy events: {int(early_buy.sum())}")
    print(f"Strong Reduce Risk events: {int(reduce_risk.sum())}")
    print(pd.DataFrame(summary).to_string(index=False))


if __name__ == "__main__":
    main()
