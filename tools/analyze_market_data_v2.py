#!/usr/bin/env python3
"""Validate the corrected Breeze export and derive traceable rule candidates."""

from __future__ import annotations

import argparse
import json
from pathlib import Path

import numpy as np
import pandas as pd


BREADTH = {
    "sp50": "sp500_sma50.csv",
    "sp100": "sp500_sma100.csv",
    "sp200": "sp500_sma200.csv",
    "nasdaq100": "nasdaq100_sma100.csv",
}


def load(path: Path) -> pd.DataFrame:
    frame = pd.read_csv(path)
    frame["Date"] = pd.to_datetime(frame["Date"], errors="coerce")
    return frame.sort_values("Date").drop_duplicates("Date", keep="last")


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("market_dir", type=Path)
    parser.add_argument("validation_dir", type=Path)
    args = parser.parse_args()
    args.validation_dir.mkdir(parents=True, exist_ok=True)

    spy = load(args.market_dir / "SPY.csv")
    core = load(args.market_dir / "spy_ema_myrsi_agc.csv")
    data = spy.rename(columns={c: f"SPY_{c}" for c in ["Open", "High", "Low", "Close", "Volume"]})
    data = data.merge(core, on="Date", how="inner")

    profiles: dict[str, dict] = {}
    all_files = ["SPY.csv", "spy_ema_myrsi_agc.csv", "spy_myrsi_rep_agc.csv", *BREADTH.values()]
    for filename in all_files:
        frame = load(args.market_dir / filename)
        numeric = frame.select_dtypes(include=["number"])
        profiles[filename] = {
            "rows": int(len(frame)),
            "date_min": frame["Date"].min().strftime("%Y-%m-%d"),
            "date_max": frame["Date"].max().strftime("%Y-%m-%d"),
            "duplicate_dates": int(frame["Date"].duplicated().sum()),
            "null_cells": int(frame.isna().sum().sum()),
            "numeric_min": float(numeric.min().min()) if not numeric.empty else None,
            "numeric_max": float(numeric.max().max()) if not numeric.empty else None,
        }

    for key, filename in BREADTH.items():
        frame = load(args.market_dir / filename)
        frame = frame[["Date", "Open", "High", "Low", "Close"]].rename(
            columns={c: f"{key}_{c}" for c in ["Open", "High", "Low", "Close"]}
        )
        data = data.merge(frame, on="Date", how="left")

    fuzzy = load(args.market_dir / "fuzzy_market_states.csv")
    fuzzy = fuzzy[["Date", "Zone", "Confidence", "Score"]].rename(
        columns={"Zone": "FuzzyZone", "Confidence": "FuzzyConfidence", "Score": "FuzzyScore"}
    )
    data = data.merge(fuzzy, on="Date", how="left")

    rsi = "MyRSI_rep_AGC_Normalized_Minus1_Plus1"
    data["EMA_bullish_order"] = (
        (data["SPY_Close"] > data["EMA20"])
        & (data["EMA20"] > data["EMA50"])
        & (data["EMA50"] > data["EMA100"])
        & (data["EMA100"] > data["EMA200"])
    )
    data["Price_above_EMA20"] = data["SPY_Close"] > data["EMA20"]
    data["Price_above_EMA50"] = data["SPY_Close"] > data["EMA50"]
    data["Price_above_EMA100"] = data["SPY_Close"] > data["EMA100"]
    data["Price_above_EMA200"] = data["SPY_Close"] > data["EMA200"]
    up_cols = []
    for key in BREADTH:
        col = f"{key}_UpCandle"
        data[col] = data[f"{key}_Close"] > data[f"{key}_Open"]
        up_cols.append(col)
    data["BreadthUpCount"] = data[up_cols].sum(axis=1)
    data["AllBreadthUp"] = data["BreadthUpCount"] == len(up_cols)

    data["MyRSI_at_minus1"] = data[rsi] <= -0.999
    data["MyRSI_at_plus1"] = data[rsi] >= 0.999
    data["MyRSI_exit_minus1"] = data["MyRSI_at_minus1"].shift(1, fill_value=False) & (data[rsi] > -0.98)
    data["MyRSI_exit_plus1"] = data["MyRSI_at_plus1"].shift(1, fill_value=False) & (data[rsi] < 0.98)
    for horizon in [1, 3, 5, 10, 20]:
        data[f"SPY_fwd_{horizon}d_pct"] = (data["SPY_Close"].shift(-horizon) / data["SPY_Close"] - 1) * 100

    data.to_csv(args.market_dir / "combined_daily_v2.csv", index=False, date_format="%Y-%m-%d")

    event_columns = [
        "Date", "SPY_Close", rsi, "BreadthUpCount", "AllBreadthUp", "EMA_bullish_order",
        "Price_above_EMA20", "Price_above_EMA50", "Price_above_EMA100", "Price_above_EMA200",
        "FuzzyZone", "FuzzyConfidence", "SPY_fwd_1d_pct", "SPY_fwd_3d_pct", "SPY_fwd_5d_pct",
        "SPY_fwd_10d_pct", "SPY_fwd_20d_pct",
    ]
    exits_low = data.loc[data["MyRSI_exit_minus1"], event_columns].copy()
    exits_high = data.loc[data["MyRSI_exit_plus1"], event_columns].copy()
    exits_low.to_csv(args.validation_dir / "myrsi_exit_minus1_events.csv", index=False, date_format="%Y-%m-%d")
    exits_high.to_csv(args.validation_dir / "myrsi_exit_plus1_events.csv", index=False, date_format="%Y-%m-%d")

    confirmed = exits_low[exits_low["BreadthUpCount"] >= 3]
    unconfirmed = exits_low[exits_low["BreadthUpCount"] < 3]
    high_bull = exits_high[exits_high["EMA_bullish_order"]]
    high_other = exits_high[~exits_high["EMA_bullish_order"]]

    def stats(frame: pd.DataFrame) -> dict:
        out = {"events": int(len(frame))}
        for horizon in [1, 3, 5, 10, 20]:
            col = f"SPY_fwd_{horizon}d_pct"
            values = frame[col].dropna()
            out[f"forward_{horizon}d"] = {
                "n": int(len(values)),
                "mean_pct": round(float(values.mean()), 4) if len(values) else None,
                "median_pct": round(float(values.median()), 4) if len(values) else None,
                "positive_fraction": round(float((values > 0).mean()), 4) if len(values) else None,
            }
        return out

    summary = {
        "scope": {
            "rows": int(len(data)),
            "date_min": data["Date"].min().strftime("%Y-%m-%d"),
            "date_max": data["Date"].max().strftime("%Y-%m-%d"),
        },
        "myrsi_validation": {
            "column": rsi,
            "minimum": float(data[rsi].min()),
            "maximum": float(data[rsi].max()),
            "outside_minus1_plus1": int(((data[rsi] < -1) | (data[rsi] > 1)).sum()),
            "rows_at_minus1": int(data["MyRSI_at_minus1"].sum()),
            "rows_at_plus1": int(data["MyRSI_at_plus1"].sum()),
        },
        "standalone_series_consistency": {},
        "event_studies_descriptive_not_causal": {
            "exit_minus1_all": stats(exits_low),
            "exit_minus1_with_3_or_4_up_breadth_candles": stats(confirmed),
            "exit_minus1_with_0_to_2_up_breadth_candles": stats(unconfirmed),
            "exit_plus1_with_bullish_ema_order": stats(high_bull),
            "exit_plus1_without_bullish_ema_order": stats(high_other),
        },
        "profiles": profiles,
        "notes": [
            "MyRSI AGC is validated in the exported normalized [-1,+1] representation.",
            "An exit from an extreme is detected only from information available on that date.",
            "Breadth confirmation is encoded as same-day OHLC breadth Close > Open; it is intentionally graded by count, not a single rigid threshold.",
            "Forward returns are descriptive diagnostics and do not establish a tradable or causal rule.",
        ],
    }
    checks = {
        "MyRSI_rep_AGC_Normalized_Minus1_Plus1": "spy_myrsi_rep_agc.csv",
        "EMA20": "spy_ema20.csv",
        "EMA50": "spy_ema50.csv",
        "EMA100": "spy_ema100.csv",
        "EMA200": "spy_ema200.csv",
    }
    for column, filename in checks.items():
        standalone = load(args.market_dir / filename)[["Date", "Close"]]
        comparison = core[["Date", column]].merge(standalone, on="Date", how="inner")
        difference = (comparison[column] - comparison["Close"]).abs()
        summary["standalone_series_consistency"][column] = {
            "file": filename,
            "matched_rows": int(len(comparison)),
            "maximum_absolute_difference": float(difference.max()),
            "all_equal_within_1e-9": bool((difference <= 1e-9).all()),
        }
    (args.validation_dir / "market_data_profile_v2.json").write_text(
        json.dumps(summary, ensure_ascii=False, indent=2), encoding="utf-8"
    )


if __name__ == "__main__":
    main()
