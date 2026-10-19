#!/usr/bin/env python3
"""Validate and align the exported SPY and market-breadth OHLCV CSV files."""

from __future__ import annotations

import argparse
import json
from pathlib import Path

import pandas as pd


EXPECTED = ["Date", "Open", "High", "Low", "Close", "Volume"]
FILES = {
    "SPY": "SPY.csv",
    "sp500_sma50": "sp500_sma50.csv",
    "sp500_sma100": "sp500_sma100.csv",
    "sp500_sma200": "sp500_sma200.csv",
    "nasdaq100_sma100": "nasdaq100_sma100.csv",
}


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("market_dir", type=Path)
    parser.add_argument("validation_dir", type=Path)
    args = parser.parse_args()
    args.validation_dir.mkdir(parents=True, exist_ok=True)

    frames: dict[str, pd.DataFrame] = {}
    profiles: dict[str, dict] = {}
    for key, filename in FILES.items():
        path = args.market_dir / filename
        frame = pd.read_csv(path)
        original_columns = frame.columns.tolist()
        frame["Date"] = pd.to_datetime(frame["Date"], errors="coerce")
        for column in EXPECTED[1:]:
            frame[column] = pd.to_numeric(frame[column], errors="coerce")
        frame = frame.sort_values("Date").reset_index(drop=True)
        frames[key] = frame
        profile = {
            "file": filename,
            "columns": original_columns,
            "expected_columns_match": original_columns == EXPECTED,
            "rows": int(len(frame)),
            "date_min": frame["Date"].min().strftime("%Y-%m-%d"),
            "date_max": frame["Date"].max().strftime("%Y-%m-%d"),
            "invalid_dates": int(frame["Date"].isna().sum()),
            "duplicate_dates": int(frame["Date"].duplicated().sum()),
            "null_cells": int(frame.isna().sum().sum()),
            "strictly_increasing_dates": bool(frame["Date"].is_monotonic_increasing),
            "ohlc_high_violations": int(
                (~(frame["High"] >= frame[["Open", "Close", "Low"]].max(axis=1))).sum()
            ),
            "ohlc_low_violations": int(
                (~(frame["Low"] <= frame[["Open", "Close", "High"]].min(axis=1))).sum()
            ),
            "negative_volume_rows": int((frame["Volume"] < 0).sum()),
            "close_min": float(frame["Close"].min()),
            "close_max": float(frame["Close"].max()),
        }
        if key != "SPY":
            profile["breadth_close_outside_0_100"] = int(
                ((frame["Close"] < 0) | (frame["Close"] > 100)).sum()
            )
        profiles[key] = profile

    combined = None
    for key, frame in frames.items():
        renamed = frame.rename(columns={c: f"{key}_{c}" for c in EXPECTED[1:]})
        combined = renamed if combined is None else combined.merge(renamed, on="Date", how="outer")
    assert combined is not None
    combined = combined.sort_values("Date")
    combined.to_csv(args.market_dir / "combined_daily.csv", index=False, date_format="%Y-%m-%d")

    close_columns = [f"{key}_Close" for key in FILES]
    close_table = combined[["Date", *close_columns]].dropna()
    correlations = {
        "level_pearson": close_table[close_columns].corr(method="pearson").round(6).to_dict(),
        "daily_change_pearson": close_table[close_columns].diff().corr(method="pearson").round(6).to_dict(),
    }

    spy = frames["SPY"][["Date", "Close"]].rename(columns={"Close": "SPY_Close"})
    spy["SPY_rolling_252_peak"] = spy["SPY_Close"].rolling(252, min_periods=20).max()
    spy["SPY_drawdown_252"] = spy["SPY_Close"] / spy["SPY_rolling_252_peak"] - 1
    signals = combined.merge(spy[["Date", "SPY_drawdown_252"]], on="Date", how="left")
    signals["author_extreme_bundle"] = (
        (signals["sp500_sma50_Close"] <= 4)
        & (signals["sp500_sma100_Close"] <= 8)
        & (signals["sp500_sma200_Close"] <= 15)
        & (signals["SPY_drawdown_252"] <= -0.10)
    )
    signal_rows = signals.loc[
        signals["author_extreme_bundle"],
        ["Date", "SPY_Close", "SPY_drawdown_252", *close_columns[1:]],
    ].copy()
    signal_rows.to_csv(
        args.validation_dir / "extreme_bundle_candidates.csv",
        index=False,
        date_format="%Y-%m-%d",
    )

    anchors = ["2019-12-04", "2020-03-24", "2022-10-18", "2023-10-06", "2026-10-08"]
    anchor_table = signals.loc[
        signals["Date"].isin(pd.to_datetime(anchors)),
        ["Date", "SPY_Close", "SPY_drawdown_252", *close_columns[1:]],
    ].copy()
    anchor_table.to_csv(
        args.validation_dir / "video_anchor_dates.csv",
        index=False,
        date_format="%Y-%m-%d",
    )

    coverage = combined[["Date", *close_columns]].copy()
    all_present = coverage[close_columns].notna().all(axis=1)
    report = {
        "profiles": profiles,
        "combined_rows": int(len(combined)),
        "combined_date_min": combined["Date"].min().strftime("%Y-%m-%d"),
        "combined_date_max": combined["Date"].max().strftime("%Y-%m-%d"),
        "dates_with_all_close_series": int(all_present.sum()),
        "dates_missing_any_close_series": int((~all_present).sum()),
        "extreme_bundle_candidate_rows": int(len(signal_rows)),
        "correlations": correlations,
        "notes": [
            "Breadth OHLC values are percentages of index constituents above the named SMA, not prices.",
            "Breadth Volume is copied from SPY volume by the exporter, as stated by the author in the video.",
            "The extreme bundle is a traceable hypothesis extracted from the video and remains pending user validation.",
        ],
    }
    (args.validation_dir / "market_data_profile.json").write_text(
        json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8"
    )


if __name__ == "__main__":
    main()
