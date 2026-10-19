# Breeze

Wealth-Lab 8 Client Extension for synchronized market-breadth and volatility views.

The main tab order is Fuzzy map, All breadths, Contango, Titanic, and Zweig. Fuzzy
map uses a one-quarter left column for a concise market-state explanation at the
shared cursor date and a three-quarters right column for the chart. The explanation
combines Phase confidence, MyRSI significance, EMA structure, breadth, candle
impulse, VIX/SVXY, Titanic, and Zweig from already calculated dated values. It
also reports precomputed 20-bar SPY outcomes for the current Phase and EMA
regime: positive-outcome frequency, average return, and sample size.

In `All breadths`, `Full` remains the default four-panel view. Selecting one or
more individual breadth series opens a combined synchronized view. Its `ATRP(1)`
toggle is on by default and adds a lower one-bar percentage True Range panel;
the optional `EMA 20/50/100/200` toggle overlays the four exponential averages
on SPY. ATRP(1) is exported both in `spy_ema_myrsi_agc.csv` and as
`spy_atrp1.csv`.

Version 0.8 adds a `Contango` tab. The left half draws the VIX
futures term structure for the date selected by the shared S&P cursor, with
alternating yellow 30-day bands, adjacent-contract differences/contango, and
interpolated 30-to-60-day metrics. The right half is the same synchronized S&P
price/breadth chart used elsewhere. VIX source files are read from
`D:\fear_greed_close\VIX`. Since version 0.9.5, the dated curve-state history
loads automatically after `Calculate 12 years` completes so the Fuzzy ribbon
is available without first opening the Contango tab. Opening Breeze itself
still does not scan or calculate the VIX history.

The first visit also refreshes `D:\fear_greed_close\vix_term_structure.csv` in
the background when source files have changed. Each row is one trading date;
the `M1_Days/M1_Price`, `M2_Days/M2_Price`, ... columns contain the ordered
contracts, followed by 30-day and 60-day interpolation, difference, and
contango percentage. An up-to-date aggregate is reused without rebuilding it.
The on-screen curve, its 30-day bands, and adjacent-contract table stop at 180
days to expiration. The dated CSV deliberately retains M1–M12 so research can
still test longer horizons without crowding the chart.
The Fuzzy map and the lower-left Contango SVXY pane contain a thin, date-aligned
VIX-curve ribbon. The upper-right Contango S&P pane has no ribbons. Five colors
distinguish strong contango, contango, mixed, backwardation,
and broad backwardation; gray marks a missing dated curve. The regime score is
`0.65 * adjacent-pair balance + 0.35 * tanh(Contango30To60Pct / 5)` with
thresholds at `-0.60`, `-0.18`, `+0.18`, and `+0.60`. The ribbon shares the
normal chart viewport and cursor. Derived features are exported to
`D:\fear_greed_close\Breeze\contango_market_state.csv`.
Directly below it, an SVXY recommendation ribbon uses a separate blue palette:
saturated blue marks the first exit from a red VIX-curve regime, light blue
marks up to 60 subsequent Buy/Hold bars, and white is neutral. A return to a red
CurveScore (`<= -0.18`) immediately ends the blue window. From bar 61 onward a
very pale blue Extended Hold continues while the curve stays above the red
threshold. The SVXY pane can also
show VIX on an independent left scale; clearing its checkbox removes both that
series and the left axis while SVXY remains on the right scale. The shared
`Candles` toggle renders both SVXY and VIX as OHLC candlesticks, using a purple
outline for VIX so the two scales remain visually distinct.
`phase_model_components.csv` joins each Phase decision to the early-Buy and
strong-Reduce-Risk memberships, VIX-curve state, Titanic state, and Zweig
density so research can explain and recalibrate the model without reconstructing
dates from separate files.
The Contango S&P pane uses the standard cream chart background, a right-side
price scale, two horizontal third-height guides, and candles enabled by default.
Spot VIX is loaded once with the first Contango initialization, rendered as a
green dashed horizontal line with its numeric value, and exported as `M0_VIX`.
The main `Calculate 12 years` run also exports the original SVXY daily OHLCV
history as `D:\fear_greed_close\Breeze\SVXY.csv`, using the same `Date` key as
the SPY, breadth, indicator, and VIX term-structure exports. If Wealth-Lab
cannot load SVXY, the calculation remains usable and the status line explicitly
reports that `SVXY.csv` was not written.
When the aggregate already has the current schema, unchanged history is reused;
new dated source files are appended. A missing aggregate, a schema change, or a
modification to an older daily source file triggers a full rebuild.
Four optional, initially unchecked controls above the Contango S&P pane overlay
S&P SMA(50/100/200) and Nasdaq-100 SMA(100) breadth. Their shared unlabeled
scale uses the combined visible extrema plus 5% padding, while SPY retains its
independent right-side price scale.
If the user opens Contango while `Calculate 12 years` is still running, its
first-time VIX request is queued rather than started concurrently. A wait banner
explains the dependency, and initialization resumes automatically after the
main calculation finishes.

Version 0.6 adds a `Zweig` tab with the two fixed post-research cover modes:
Strong 60/11/21 and Working 60/13/15.  The saved interpretation is the exact
green-density scale from the source strategy (30 for bars 0–50, 15 for 51–65,
5 for 66–85, then 0), exported to `zweig_breadth_thrust.csv`.  There are no
optimization controls in the extension.  The Phase Map now overlays a red
MyRSI_rep + AGC (10,8, HL77) on the existing (14,8, HL77) lower pane, and the
top-level `? Help` button opens the complete help embedded inside `Breeze.dll`.
Version 0.9.14 adds the complete Podhvat daily research package to that embedded
help: overview, event summary, expanding walk-forward replay, all de-duplicated
events, the full 3,016-row daily panel, and the frozen methodology. Opening help
is read-only and does not request history or recalculate indicators.
Version 0.9.17 turns that research into a daily position-management output on
Fuzzy map, Titanic, and Zweig: a synchronized red-yellow-green `Hold %` ribbon
from 0 to 100. On Titanic it is below the chart; on Zweig it is between SPY and
the breadth pane. Each tab has a checked-by-default `Hold %` visibility toggle;
changing it is view-only. Its
precomputed score combines EMA structure, breadth, Phase, ATRP, Contango and
Zweig support, with Titanic and shock/Reduce Risk penalties.  Exact values and
components are exported to `hold_confidence.csv`.
The Fuzzy-map explanation now begins with a cursor-synchronized operational
card showing Hold, a ten-contract linear target, the change from the preceding
trading day, the strongest support and risk contributions, VIX-curve state, and
SVXY recommendation. Exact zero maps to no position; positive values add one
contract per ten Hold points, with 90..100 mapped to all ten contracts. This is
a transparent display rule, not yet a backtested Wealth-Lab strategy.

Version 0.9.18 adds a separate compiled Wealth-Lab strategy in
`Breeze.Strategies.dll`. `Breeze Hold Position Strategy` reads the dated values
from `D:\fear_greed_close\Breeze\hold_confidence.csv`, targets 0..10 long
contracts with the same shared `ModelPositionSizing` rule as the operational
card, and sends only the integer difference from `OpenQuantity`. A signal is
read from the current bar's date in `Execute`; its `OrderType.Market` order is
therefore filled by Wealth-Lab at the following bar's open. Missing dates leave
the current position unchanged, and values from later dates are never used.

Version 0.9.19 makes the Fuzzy-chart `Last` readout independent of its movable
viewport: it now comes from the newest dated calculated state and shows that
date explicitly. The Hold legend is placed after EMA200 with two
EMA100-to-EMA200 label intervals of spacing, while the current `Last` readout is
right-aligned immediately above the price scale. Preserved release binaries are
consolidated under `versions/<version>`; `versions/README.md` records the known
iteration history and which binaries still exist.

Version 0.9.20 adds the strategy's actual integer `Target contracts` time
series in a separate `Breeze Position 0-10` line pane. `Execute` trades that
series directly and uses the same one-order-per-contract balancing loops as the
TorchSharp reference: repeated market Buy orders raise `OpenQuantity` to the
target and repeated market Sell orders reduce it to the target. This also fixes
the earlier misuse of the five-argument `PlaceTrade` overload: its integer
argument is a position tag, not an order quantity. Missing Hold dates carry the
last target forward. Explicit namespace imports make the source suitable for
direct use in the Wealth-Lab C# editor as well as in the compiled library.

- S&P 500 percentage of historically active constituents above SMA(200)
- S&P 500 percentage above SMA(100)
- S&P 500 percentage above SMA(50)
- Nasdaq-100 percentage above SMA(100)

The calculation covers the latest 12 years and honors Wealth-Lab dynamic DataSet membership through each loaded `BarHistory.ExecutableRanges`.

History requests run in a bounded pool. If every active provider request stops
responding for 20 seconds, Breeze skips that stalled tail and continues with the
hundreds of histories already received. The three S&P panels are displayed as
soon as their stage completes; Nasdaq-100, fuzzy inference, and export follow as
separate visible stages.

## Fuzzy market-state map

Version 0.2 adds an expert fuzzy-logic view. It is a qualitative market-state
map rather than an automatic trading strategy. The new `Fuzzy map` tab overlays
SPY with a daily state ribbon:

- dark green: strong buy after a deep decline and confirmed reversal event;
- light green: buy zone while the setup is developing;
- white: neutral;
- orange: sell when price strength is not confirmed and technical structure weakens;
- red: strong sell when thinning participation is joined by broader deterioration.

Internal caution remains available in the cursor readout and rationale as a
warning, but it is rendered white like Neutral so warnings do not create a
separate market zone.

Membership functions overlap broadly. A value such as 4% is therefore not a
hard boundary. Version 0.4 combines three blocks inside the inference itself:
the four breadth horizons, MyRSI_rep + AGC saturation/release, and the graded
SPY/EMA20/50/100/200 structure. A release from -1 gains buy membership when
breadth turns up on that day or the preceding trading day. A synchronized
breadth-and-price reversal may produce Buy one bar earlier while MyRSI is still
pinned at -1; saturation without that reversal remains Prepare Buy. A +1 reading is
caution rather than an automatic sale, especially while the EMA order is
bullish. Confirmed breaks of progressively slower EMAs increase risk. The
earlier drawdown, momentum, breadth-peak and horizon-disagreement features are
retained. All rolling calculations use only information available on or before
the evaluated date.

Enable `Fuzzy zones` to add a light version of the same background to the four
existing breadth charts. Moving the synchronized cursor shows the dominant
state, its confidence, and the individual fuzzy degrees. Every calculation also
exports `fuzzy_market_states.csv` beside the existing OHLCV files for review;
the export now includes EMA values, normalized MyRSI, bullish-EMA-order and
breadth-turn memberships used by the fuzzy engine.

The price pane also shows EMA(20), EMA(50), EMA(100), and EMA(200). A synchronized
lower pane renders `MyRSI_rep(Close, 14, 8)` from TorchSharp4GPU followed by the
user's Automatic Gain Control with half-life 77:
the close is first filtered by John Ehlers' SuperSmoother(8), then the 14-bar
normalized balance of upward and downward filtered changes is plotted on its
native -1 to +1 scale. The AGC peak decays by `0.5^(1/77)` per bar, is raised
to the current absolute signal when necessary, and normalizes the result by the
current peak.

The cursor readout uses two fixed rows so breadth/fuzzy values and the technical
indicators remain visible at the same time. Each calculation also exports
`spy_ema_myrsi_agc.csv` to `D:\fear_greed_close\Breeze` with the four EMA series,
the normalized MyRSI value, and the three MyRSI/AGC parameters on every row.
The Fuzzy map chart and MyRSI pane use a pure white background to maximize the
contrast of the market-state colors.

Technical indicators are calculated from three extra years of hidden SPY
history and cropped only at export/display time, so EMA(200) and AGC are already
warmed up on the first visible day. For consumers that expect the same OHLCV
schema as the breadth files, Breeze also writes `spy_ema20.csv`, `spy_ema50.csv`,
`spy_ema100.csv`, `spy_ema200.csv`, and `spy_myrsi_rep_agc.csv`. Each file repeats
the indicator value in Open/High/Low/Close; the MyRSI file is validated to remain
inside [-1,+1].

## SentimenTrader Titanic

Version 0.5 adds a separate `Titanic` tab based on the SentimenTrader defaults
from the user's updated `TitanicSyndromeResearch.cs`:

- QQQ 252-session closing high;
- at least one such high in the latest 7 sessions;
- Nasdaq 52-week New Lows greater than New Highs on the current session;
- 3% high tolerance, matching the active research settings shown in Wealth-Lab;
- persistence begins on day 7;
- the optional Nasdaq-100 SMA(50) breadth filter is calculated from the selected
  Nasdaq DataSet but remains disabled by default, matching the source settings.

The early Titanic phase is blue and a persistent run is green. Breeze exports
all evaluated dates to `titanic_sentiment.csv` and active dates to
`titanic_sentiment_events.csv` in `D:\fear_greed_close\Breeze`. Both files include
the exact date, run length, QQQ/high condition, Nasdaq New Highs/New Lows,
DataSet-based SMA(50) breadth and all active defaults.

The Titanic header includes `- 7 days +` immediately before the latest-state
readout. It changes the persistence threshold from 1 to 30 days, recolors the
history immediately, and rewrites both Titanic CSV files. The shared `Candles`
toggle switches the Titanic QQQ plot between a line and OHLC candles. The lower
cursor row deliberately shows only `MyRSI: value`; implementation parameters
remain in the technical CSV metadata.

## Build

The source project does not redistribute Wealth-Lab assemblies. A ready-to-use
`Breeze.dll` and its standalone `BreezeHelp.html` are published in `release/`
for users who only want to install the extension.

### Requirements

- Windows x64
- .NET 8 SDK (the project targets `net8.0-windows`)
- Wealth-Lab 8 installed in `C:\Program Files\Quantacula, LLC\WealthLab 8`

`WealthLab.Core.dll` and `WealthLab.WPF.dll` are resolved from the Wealth-Lab
installation directory during the build. If Wealth-Lab is installed elsewhere,
override the path, for example:

```powershell
dotnet build .\Breeze\Breeze.csproj -c Release -p:Platform=x64 -p:WealthLabInstallDir="D:\\Path\\To\\WealthLab 8"
```

If the .NET 8 SDK is missing, install it from the official [.NET download
page](https://dotnet.microsoft.com/download/dotnet/8.0) before running the build.

```powershell
dotnet build .\Breeze\Breeze.csproj -c Release -p:Platform=x64
```

The resulting assembly is:

`Breeze\\bin\\x64\\Release\\net8.0-windows\\Breeze.dll`

Build the separate strategy library after Breeze:

```powershell
dotnet build .\Breeze.Strategies\Breeze.Strategies.csproj -c Release
```

Its assembly is:

`Breeze.Strategies\\bin\\Release\\net8.0-windows\\Breeze.Strategies.dll`

Copy `Breeze.dll`, `BreezeHelp.html` and, when the separate compiled strategies
are required, `Breeze.Strategies.dll` into
`C:\Program Files\Quantacula, LLC\WealthLab 8` while Wealth-Lab is closed.
Wealth-Lab discovers the Client Extension and compiled strategy during startup;
there is no documented hot-reload mechanism for these assemblies. Before a
backtest, open Breeze and run `Calculate 12 years` to refresh
`hold_confidence.csv`. Then select `Breeze Hold Position Strategy` in the
Strategies tree, choose the instrument/DataSet and Daily scale, and run the
backtest. Position size is supplied explicitly by the strategy, so no separate
percentage-based sizing rule is required.

The Breeze Help button first opens the standalone `BreezeHelp.html` located
beside `Breeze.dll` in the default browser. If that file is missing or Windows
cannot open it, Breeze displays its embedded fallback copy and identifies the
fallback at the top of the page.

The GitHub source repository contains both ready-to-install release files, so
users do not need to install the .NET SDK or compile the project themselves.

The upstream reference project is in `reference/WL8ExtensionDemos`.
