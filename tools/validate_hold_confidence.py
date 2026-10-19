from __future__ import annotations

from pathlib import Path

import numpy as np
import pandas as pd


SOURCE = Path(r"D:\fear_greed_close\Breeze")
OUT = Path(r"D:\CODEX_PRG\Breadth\artifacts\Podhvat_1\research")


def ramp(value: pd.Series, start: float, end: float) -> pd.Series:
    t = ((value - start) / (end - start)).clip(0, 1)
    return t * t * (3 - 2 * t)


def main() -> None:
    fuzzy = pd.read_csv(SOURCE / "fuzzy_market_states.csv")
    technical = pd.read_csv(SOURCE / "spy_ema_myrsi_agc.csv")
    phase = pd.read_csv(SOURCE / "phase_model_components.csv")
    spy = pd.read_csv(SOURCE / "SPY.csv")
    for frame in (fuzzy, technical, phase, spy):
        frame["Date"] = pd.to_datetime(frame["Date"])

    keep = [
        "Date", "CurveRegimeScore", "TitanicSignal", "TitanicPersistent", "ZweigDensity"
    ]
    d = fuzzy.merge(technical[["Date", "ATRP_1"]], on="Date", how="inner")
    d = d.merge(phase[keep], on="Date", how="left")
    d = d.merge(spy[["Date", "Close"]].rename(columns={"Close": "SpyClose"}), on="Date", how="left")

    d["Trend"] = d["BullishEMAOrder"].clip(0, 1)
    breadth_average = 0.30 * d["SP50"] + 0.25 * d["SP100"] + 0.25 * d["SP200"] + 0.20 * d["Nasdaq100"]
    d["Breadth"] = ramp(breadth_average, 25, 75)
    d["PhaseComponent"] = (0.50 + 0.50 * d["Score"]).clip(0, 1)
    d["VolatilityCalm"] = 1 - ramp(d["ATRP_1"], 1.20, 3.50)
    d["Contango"] = (0.50 + 0.50 * d["CurveRegimeScore"].fillna(0)).clip(0, 1)
    d["ZweigSupport"] = (d["ZweigDensity"].fillna(0) / 30).clip(0, 1)
    d["TitanicPenalty"] = np.where(
        d["TitanicSignal"].fillna(0).astype(bool),
        np.where(d["TitanicPersistent"].fillna(0).astype(bool), 1.0, 0.65), 0.0)
    d["ShockPenalty"] = d[["StrongSell", "StrongReduceRiskComplex", "LargeRedDay"]].max(axis=1)
    raw = (
        0.30 * d["Trend"] + 0.25 * d["Breadth"] + 0.20 * d["PhaseComponent"] +
        0.10 * d["VolatilityCalm"] + 0.10 * d["Contango"] + 0.05 * d["ZweigSupport"] -
        0.20 * d["TitanicPenalty"] - 0.25 * d["ShockPenalty"]
    ).clip(0, 1) * 100
    raw = np.where(d["Zone"].eq("Strong sell"), np.minimum(raw, 15), raw)
    raw = np.where(d["Zone"].eq("Sell"), np.minimum(raw, 39), raw)
    raw = np.where((d["SpyClose"] < d["EMA200"]) & (breadth_average < 30), np.minimum(raw, 10), raw)
    smoothed = []
    for value in raw:
        if not smoothed:
            smoothed.append(float(value))
        else:
            weight = 0.75 if value < smoothed[-1] else 0.45
            smoothed.append(float(weight * value + (1 - weight) * smoothed[-1]))
    d["HoldConfidencePct"] = smoothed
    d["Band"] = pd.cut(
        d["HoldConfidencePct"], [-0.001, 20, 40, 60, 80, 100.001], right=False,
        labels=["No position", "Weak hold", "Cautious hold", "Hold", "Strong hold"])
    for horizon in (5, 10, 20):
        d[f"Fwd{horizon}Pct"] = (d["SpyClose"].shift(-horizon) / d["SpyClose"] - 1) * 100
    closes = d["SpyClose"].to_numpy()
    adverse_20 = []
    for index, close in enumerate(closes):
        future = closes[index + 1:min(len(closes), index + 21)]
        adverse_20.append((future.min() / close - 1) * 100 if len(future) else np.nan)
    d["MaxAdverse20Pct"] = adverse_20

    rows = []
    for band, g in d.groupby("Band", observed=False):
        row = {"Band": str(band), "N": len(g), "AverageHoldPct": g["HoldConfidencePct"].mean()}
        for horizon in (5, 10, 20):
            values = g[f"Fwd{horizon}Pct"].dropna()
            row[f"AvgFwd{horizon}Pct"] = values.mean()
            row[f"WinRate{horizon}Pct"] = (values > 0).mean() * 100
        adverse = g["MaxAdverse20Pct"].dropna()
        row["AvgMaxAdverse20Pct"] = adverse.mean()
        row["DrawdownOver5Pct"] = (adverse <= -5).mean() * 100
        row["Avoided5PctDrawdownPct"] = (adverse > -5).mean() * 100
        rows.append(row)
    summary = pd.DataFrame(rows)
    summary.to_csv(OUT / "hold_confidence_validation.csv", index=False, encoding="utf-8-sig")
    d.to_csv(OUT / "hold_confidence_replay.csv", index=False, encoding="utf-8-sig")
    print(summary.round(2).to_string(index=False))


if __name__ == "__main__":
    main()
