from __future__ import annotations

import json
import shutil
from pathlib import Path

import numpy as np
import pandas as pd


ROOT = Path(r"D:\CODEX_PRG\Breadth")
SOURCE = Path(r"D:\fear_greed_close\Breeze")
PACKAGE = ROOT / "artifacts" / "Podhvat_1"
OUT = PACKAGE / "research"
SNAPSHOT = PACKAGE / "market_data"

FILES = [
    "SPY.csv",
    "spy_ema_myrsi_agc.csv",
    "spy_atrp1.csv",
    "sp500_sma50.csv",
    "sp500_sma100.csv",
    "sp500_sma200.csv",
    "nasdaq100_sma100.csv",
    "fuzzy_market_states.csv",
    "phase_model_components.csv",
    "titanic_sentiment.csv",
    "zweig_breadth_thrust.csv",
    "contango_market_state.csv",
]


def read_daily(name: str) -> pd.DataFrame:
    frame = pd.read_csv(SOURCE / name)
    frame["Date"] = pd.to_datetime(frame["Date"])
    return frame.sort_values("Date").drop_duplicates("Date", keep="last")


def copy_sources() -> None:
    SNAPSHOT.mkdir(parents=True, exist_ok=True)
    for name in FILES:
        shutil.copy2(SOURCE / name, SNAPSHOT / name)


def prepare_data() -> pd.DataFrame:
    spy = read_daily("SPY.csv")
    ema = read_daily("spy_ema_myrsi_agc.csv")
    atrp_raw = read_daily("spy_atrp1.csv")

    # The exported ATRP series is stored in the OHLC-shaped file's Open field.
    atrp = atrp_raw[["Date", "Open"]].rename(columns={"Open": "ATRP_1_export"})
    d = spy.merge(ema, on="Date", how="left", validate="one_to_one").merge(
        atrp, on="Date", how="left", validate="one_to_one"
    )

    for name, out_col in [
        ("sp500_sma50.csv", "SP500_SMA50"),
        ("sp500_sma100.csv", "SP500_SMA100"),
        ("sp500_sma200.csv", "SP500_SMA200"),
        ("nasdaq100_sma100.csv", "NASDAQ100_SMA100"),
    ]:
        breadth = read_daily(name)[["Date", "Close"]].rename(columns={"Close": out_col})
        d = d.merge(breadth, on="Date", how="left", validate="one_to_one")

    phase = read_daily("phase_model_components.csv")
    phase_cols = [
        "Date", "Phase", "Confidence", "Score", "VIXCurveState",
        "Contango30To60Pct", "RisingPairShare", "CurveRegimeScore",
        "SVXYBuyCandidate", "SVXYRecommendation", "TitanicSignal",
        "TitanicPersistent", "ZweigDensity", "ZweigSignal",
    ]
    d = d.merge(phase[phase_cols], on="Date", how="left", validate="one_to_one")

    d["ATRP_1"] = d["ATRP_1_export"].combine_first(d["ATRP_1"])
    d["DayReturnPct"] = d["Close"].pct_change() * 100
    d["BodyPct"] = (d["Close"] - d["Open"]) / d["Open"] * 100
    for horizon in (1, 3, 5, 10, 15, 20):
        d[f"Fwd{horizon}Pct"] = (d["Close"].shift(-horizon) / d["Close"] - 1) * 100

    for length in (20, 50, 100, 200):
        d[f"EMA{length}Slope5Pct"] = (d[f"EMA{length}"] / d[f"EMA{length}"].shift(5) - 1) * 100

    d["BullishEMAOrder"] = (
        (d["Close"] > d["EMA20"])
        & (d["EMA20"] > d["EMA50"])
        & (d["EMA50"] > d["EMA100"])
        & (d["EMA100"] > d["EMA200"])
    )
    d["AllEMASlopesPositive"] = np.logical_and.reduce(
        [d[f"EMA{x}Slope5Pct"] > 0 for x in (20, 50, 100, 200)]
    )
    d["AllBreadthAbove50"] = np.logical_and.reduce(
        [d[x] > 50 for x in ("SP500_SMA50", "SP500_SMA100", "SP500_SMA200", "NASDAQ100_SMA100")]
    )
    d["FanWidthPct"] = (d["EMA20"] - d["EMA200"]).abs() / d["Close"] * 100
    d["StableGrowth"] = d["BullishEMAOrder"] & d["AllEMASlopesPositive"] & d["AllBreadthAbove50"]
    d["StableGrowthStreak"] = d["StableGrowth"].groupby((~d["StableGrowth"]).cumsum()).cumcount() + 1
    d.loc[~d["StableGrowth"], "StableGrowthStreak"] = 0
    return d


def spaced_events(mask: pd.Series, minimum_gap: int) -> list[int]:
    result: list[int] = []
    last = -10_000
    for idx in np.flatnonzero(mask.fillna(False).to_numpy()):
        if idx - last >= minimum_gap:
            result.append(int(idx))
            last = int(idx)
    return result


def event_record(d: pd.DataFrame, idx: int, event_type: str, subtype: str, notes: str = "") -> dict:
    row = d.iloc[idx]
    cols = [
        "Date", "Open", "High", "Low", "Close", "ATRP_1", "DayReturnPct", "BodyPct",
        "EMA20", "EMA50", "EMA100", "EMA200", "FanWidthPct",
        "SP500_SMA50", "SP500_SMA100", "SP500_SMA200", "NASDAQ100_SMA100",
        "Phase", "Confidence", "Score", "VIXCurveState", "Contango30To60Pct",
        "RisingPairShare", "CurveRegimeScore", "SVXYRecommendation", "TitanicSignal",
        "TitanicPersistent", "ZweigDensity", "ZweigSignal",
        "Fwd1Pct", "Fwd3Pct", "Fwd5Pct", "Fwd10Pct", "Fwd15Pct", "Fwd20Pct",
    ]
    rec = {c: row.get(c, np.nan) for c in cols}
    rec.update(EventType=event_type, Subtype=subtype, Notes=notes)
    return rec


def build_events(d: pd.DataFrame) -> pd.DataFrame:
    records: list[dict] = []

    stable_start = d["StableGrowth"] & ~d["StableGrowth"].shift(1, fill_value=False)
    for idx in spaced_events(stable_start, 10):
        records.append(event_record(d, idx, "Stable growth", "Regime start", "EMA order + positive slopes + all breadth > 50"))

    pickup_mask = pd.Series(False, index=d.index)
    pickup_depth: dict[int, str] = {}
    for idx in range(5, len(d)):
        if not (d.at[idx, "Close"] > d.at[idx, "Open"] and d.at[idx, "Close"] > d.at[idx, "EMA20"]):
            continue
        prev = d.iloc[idx - 5:idx]
        if not (prev["Close"] < prev["EMA20"]).any():
            continue
        if not bool(d.at[idx, "AllEMASlopesPositive"]):
            continue
        low_close = prev["Close"].min()
        reference = prev.loc[prev["Close"].idxmin()]
        if low_close >= reference["EMA50"]:
            depth = "EMA20 pickup"
        elif low_close >= reference["EMA100"]:
            depth = "EMA50 pickup"
        elif low_close >= reference["EMA200"]:
            depth = "EMA100 pickup"
        else:
            depth = "Deep / below EMA200"
        pickup_mask.iloc[idx] = True
        pickup_depth[idx] = depth
    for idx in spaced_events(pickup_mask, 5):
        records.append(event_record(d, idx, "EMA pickup", pickup_depth[idx], "Green recovery close above EMA20 after a close below EMA20 in prior 5 bars"))

    bearish = (d["Close"] < d["Open"]) & ((d["BodyPct"] <= -2.0) | (d["DayReturnPct"] <= -2.0))
    bullish = (d["Close"] > d["Open"]) & ((d["BodyPct"] >= 2.5) | (d["DayReturnPct"] >= 2.5))
    for mask, event_type, direction in [
        (bearish, "Strong anchor bar", "Bearish"),
        (bullish, "Strong anchor bar", "Bullish"),
    ]:
        for idx in spaced_events(mask, 3):
            if idx + 1 >= len(d):
                continue
            row, nxt = d.iloc[idx], d.iloc[idx + 1]
            body_mid = (row["Open"] + row["Close"]) / 2
            range_mid = (row["High"] + row["Low"]) / 2
            close_mid = (d.iloc[idx - 1]["Close"] + row["Close"]) / 2 if idx else np.nan
            if direction == "Bearish":
                reached = lambda level: bool(nxt["High"] >= level)
            else:
                reached = lambda level: bool(nxt["Low"] <= level)
            notes = (
                f"next-day 50% touch: body={reached(body_mid)}; range={reached(range_mid)}; "
                f"close-to-close={reached(close_mid)}"
            )
            rec = event_record(d, idx, event_type, direction, notes)
            rec.update(
                BodyMidpoint=body_mid,
                RangeMidpoint=range_mid,
                CloseToCloseMidpoint=close_mid,
                NextDayBodyMidTouch=reached(body_mid),
                NextDayRangeMidTouch=reached(range_mid),
                NextDayCloseMidTouch=reached(close_mid),
            )
            records.append(rec)

    events = pd.DataFrame(records)
    events["Date"] = pd.to_datetime(events["Date"])
    return events.sort_values(["Date", "EventType", "Subtype"]).reset_index(drop=True)


def event_summary(events: pd.DataFrame) -> pd.DataFrame:
    rows = []
    for (event_type, subtype), g in events.groupby(["EventType", "Subtype"], dropna=False):
        row = {"EventType": event_type, "Subtype": subtype, "N": len(g)}
        for horizon in (1, 3, 5, 10, 15, 20):
            s = g[f"Fwd{horizon}Pct"].dropna()
            row[f"AvgFwd{horizon}Pct"] = s.mean()
            row[f"MedianFwd{horizon}Pct"] = s.median()
            row[f"WinRate{horizon}Pct"] = (s > 0).mean() * 100 if len(s) else np.nan
        if event_type == "Strong anchor bar":
            for col in ("NextDayBodyMidTouch", "NextDayRangeMidTouch", "NextDayCloseMidTouch"):
                row[f"{col}Pct"] = g[col].mean() * 100
        rows.append(row)
    return pd.DataFrame(rows)


def walk_forward(d: pd.DataFrame, events: pd.DataFrame) -> pd.DataFrame:
    rows = []
    for test_year in range(2019, int(d["Date"].dt.year.max()) + 1):
        train = d[d["Date"].dt.year < test_year]
        test_events = events[events["Date"].dt.year == test_year].copy()
        if train.empty or test_events.empty:
            continue
        q33, q67 = train["FanWidthPct"].quantile([0.33, 0.67])
        atrp_median = train["ATRP_1"].median()
        test_events["FanState"] = pd.cut(
            test_events["FanWidthPct"], [-np.inf, q33, q67, np.inf], labels=["Compressed", "Middle", "Stretched"]
        )
        test_events["VolState"] = np.where(test_events["ATRP_1"] <= atrp_median, "Low", "High")
        for event_type, g in test_events.groupby("EventType"):
            rows.append({
                "TrainThrough": test_year - 1,
                "TestYear": test_year,
                "EventType": event_type,
                "N": len(g),
                "TrainFanQ33Pct": q33,
                "TrainFanQ67Pct": q67,
                "TrainATRPMedianPct": atrp_median,
                "AvgFwd5Pct": g["Fwd5Pct"].mean(),
                "WinRate5Pct": (g["Fwd5Pct"] > 0).mean() * 100,
                "AvgFwd10Pct": g["Fwd10Pct"].mean(),
                "WinRate10Pct": (g["Fwd10Pct"] > 0).mean() * 100,
                "AvgFwd20Pct": g["Fwd20Pct"].mean(),
                "WinRate20Pct": (g["Fwd20Pct"] > 0).mean() * 100,
                "CompressedN": int((g["FanState"] == "Compressed").sum()),
                "StretchedN": int((g["FanState"] == "Stretched").sum()),
                "HighVolN": int((g["VolState"] == "High").sum()),
            })
    return pd.DataFrame(rows)


def anchor_summary(events: pd.DataFrame) -> pd.DataFrame:
    a = events[events["EventType"] == "Strong anchor bar"]
    rows = []
    for subtype, g in a.groupby("Subtype"):
        rows.append({
            "Direction": subtype,
            "N": len(g),
            "BodyMidTouchPct": g["NextDayBodyMidTouch"].mean() * 100,
            "RangeMidTouchPct": g["NextDayRangeMidTouch"].mean() * 100,
            "CloseToCloseMidTouchPct": g["NextDayCloseMidTouch"].mean() * 100,
        })
    return pd.DataFrame(rows)


def main() -> None:
    OUT.mkdir(parents=True, exist_ok=True)
    copy_sources()
    daily = prepare_data()
    events = build_events(daily)
    summary = event_summary(events)
    wf = walk_forward(daily, events)
    anchors = anchor_summary(events)

    daily.to_csv(OUT / "daily_features.csv", index=False, encoding="utf-8-sig")
    events.to_csv(OUT / "events.csv", index=False, encoding="utf-8-sig")
    summary.to_csv(OUT / "event_summary.csv", index=False, encoding="utf-8-sig")
    wf.to_csv(OUT / "walk_forward.csv", index=False, encoding="utf-8-sig")
    anchors.to_csv(OUT / "anchor_midpoint_summary.csv", index=False, encoding="utf-8-sig")

    metadata = {
        "daily_rows": len(daily),
        "date_from": str(daily["Date"].min().date()),
        "date_to": str(daily["Date"].max().date()),
        "events": len(events),
        "event_counts": events.groupby(["EventType", "Subtype"]).size().to_dict(),
        "atrp_export_max_abs_difference": float((daily["ATRP_1"] - daily["ATRP_1_export"]).abs().max()),
        "walk_forward_first_test_year": 2019,
        "walk_forward_last_test_year": int(daily["Date"].dt.year.max()),
    }
    # JSON does not support tuple keys.
    metadata["event_counts"] = {" | ".join(k): int(v) for k, v in metadata["event_counts"].items()}
    (OUT / "analysis_metadata.json").write_text(json.dumps(metadata, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps(metadata, ensure_ascii=False, indent=2))


if __name__ == "__main__":
    main()
