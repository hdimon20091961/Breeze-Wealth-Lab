# Breeze synchronized chart navigation

On Trading Ensemble, the bottom pane compares two series on the same execution date. Orange `Без люстры` is the ensemble position without chandelier protection or re-entry delay; blue `С люстрой` is the actually retained position after chandelier fills and the re-entry rule. The cursor strip is ordered as `date → Без люстры P% → С люстрой P% → Stop → package values`. A value is never borrowed from an earlier date for the `Last` annotation. Hovering inside either line's hit area for 400 ms opens its Russian explanation; the popup has no automatic timeout and closes immediately on leaving that line.

## Market Breeze 0.11.0 — approved 29 October 2026

Rules1 and Rules2 ribbons share the master dates and viewport. Both remain
visible on Fuzzy map; the model selector changes the detailed explanation and
the named single ribbon on Titanic/Zweig. Rules2 displays its own normalized
0–100 range; trading uses its original raw thresholds and causal state. Missing
scores are grey and preserve the previous target. Switching the model and pane
collapse/restore must preserve dates, viewport and pinned cursor.

Trading Ensemble titles now use 12-point normal text, with a left-hand collapse
button (▼ expanded, ▲ collapsed). Each collapsed pane occupies one 26-pixel
header. Remaining panes share the released height in their saved proportions.
Expanded weights and four collapse flags persist independently. All three gray
divider strips remain visible around collapsed headers. Dragging either strip
beside a collapsed middle pane resizes the nearest expanded panes above and
below without reopening the collapsed pane; the compact header moves with the
boundary. Dividers do not begin horizontal panning. All four headers remain
usable when every pane is collapsed. These values supersede the old 14-point
semibold title requirement below.

Tab headers have equal compact widths and wrap without expanding to fill a
whole row. The upper toolbar and the Fuzzy legend wrap when space is limited.
For full operating details see [market-breeze-rules-switch.md](market-breeze-rules-switch.md).

This document defines the project-specific configuration for the global `$synchronized-chart-navigation` skill. The reusable behavior belongs in the skill; values and component names specific to Breeze belong here.

The accepted presentation values and documented exceptions are collected in
[ui-standards.md](ui-standards.md). The latest skills/UI audit is
[review-2026-10-25/skills-and-ui.md](review-2026-10-25/skills-and-ui.md).

## Current controls

- Default visible range: 200 bars, aligned to the most recent observation.
- Minimum visible range: 20 bars.
- Holding either navigation button repeats after 350 ms, then every 100 ms;
  each repeat uses the same 20-bar step and the existing history bounds.
- Trading Ensemble Forecast accepts exact integer input (0–100), committed on
  Enter or focus loss; Escape restores the current value. Its repeat buttons
  use a 5-day step with the same timing. Calculation waits for 350 ms of quiet.
- Selecting a Market Setup rule resets all plotted layers to that rule's needs.
  Manual additions last until the next rule selection. Its cursor description
  contains only the date and rule value; current series values stay in the footer.
- Indicator and ATRP plots redraw on the inner Canvas size change, including
  compact rows below the old 90/70-pixel outer-frame thresholds. Never retain
  geometry from a previous larger pane or arrange the Canvas manually in tests.
- Wheel and plus/minus increment: exactly 20 bars per user action. Keep this
  value in the shared `VisibleBarStep` constant; do not duplicate a literal in
  individual chart controls.
- Slider small change: 10 bars.
- Slider large change: 100 bars.
- The S&P 500 SMA(200) breadth series is the master navigation timeline.
- Plus/minus buttons preserve the current right edge.
- Mouse-wheel zoom preserves the newest observation and all right-side forecast
  space while the viewport is already aligned to the right edge. Away from the
  right edge it is anchored at the current cursor date and falls back to the
  center of the viewport.
- Left-button horizontal drag pans the same shared position used by the history slider.

## Participating charts

Every horizontal viewport change must be applied to:

- All breadths S&P 500 SMA(50), SMA(100), and SMA(200);
- All breadths Nasdaq-100 SMA(100);
- the All breadths combined chart and its optional ATRP(1) lower panel;
- Fuzzy map;
- Trading Ensemble (SPY/EMA, ensemble percentages, package deciles, and the selectable active-decile/active-ensemble count);
- Titanic;
- Sentiment (SPY price/EMA pane and the normalized indicator pane);
- Zweig;
- Market Setup (SPY/rule lifecycle pane and the normalized indicator pane).
- Contango S&P panel (price-only SPY rendering; the VIX term-structure panel follows the shared cursor date but has its own days-to-expiration axis).
- Contango SVXY and MyRSI panels.

The Contango S&P panel can optionally overlay S&P SMA(50), SMA(100), SMA(200),
and Nasdaq-100 SMA(100) breadth. Selected overlays share one unlabeled vertical
scale derived from the combined visible minimum and maximum with 5% padding;
the SPY price scale remains independently auto-scaled on the right.

The All breadths `Full` mode renders the four original breadth panels. It is
mutually exclusive with the individual breadth selectors. One or more selected
breadth series are rendered together in a single panel; their shared scale is
derived from the combined visible minimum and maximum with 5% padding at each
end. Log breadth affects only All breadths panels. In the individual mode,
ATRP(1) is visible by default in a synchronized lower pane, while the optional
EMA toggle overlays EMA(20/50/100/200) on the SPY price scale. Neither toggle
may cause a market-data request or indicator recalculation.
The divider between the combined breadth/SPY pane and ATRP(1) is vertically
draggable and preserves the user's proportions while the pane remains active.
ATRP(1) uses a fixed zero lower bound and a visible-data upper bound with 5%
headroom. The duplicate first/last date captions are hidden in both panes; the
shared date range remains available in the window's navigation strip.

The Contango layout uses two synchronized time-series stacks around the VIX
term curve: SVXY occupies the lower third of the left column; SPY occupies the
upper two-thirds and MyRSI 14,8 / 10,8 the lower third of the right column.
MyRSI labels and scale are shown only on the right.

The participating controls are currently updated by `BreezeWindow.ApplyHistoryViewport`.
The visible tab order is Fuzzy map, Trading Ensemble, All breadths, Contango,
Titanic, Zweig, Sentiment, then Market Setup.
The Sentiment tab uses the same SPY price rendering as the other price panes and a
separate indicator pane. Its current renderer auto-scales the visible selected
values after clipping to `-1..+1`; clipping of out-of-range source values is an
open review finding, not a calculation or export rule. Indicator and EMA checkboxes are view-only;
they select already calculated data and must never request history, recalculate, or export.
Only CNN Fear Greed is selected by default. The tab exports one combined sentiment
CSV plus one `Date,Value` CSV per indicator during the main calculation export.
Market Setup is the rightmost tab. Its left side contains a vertical indicator
selector, the complete 141-entry research catalogue grouped by family,
and a resizable description pane. The 86 executable detector names, formulas, definitions,
lifecycle text, activation, weights, TorchSharp status, unresolved questions,
and sources are read from the embedded `BreezeRules.json` catalog. The complete
registry and preserved research records come from `BreezeEventCatalog.json`,
generated from catalogue v7 and the accepted-model manifest. Its 77 selected /
34 nonzero research components are distinguished from the DLL's 75 scoring /
32 nonzero original scoring rules. Eleven additional EMA display states are
enabled, cached, described and rendered without extending the frozen scoring
vector. Their permanent numbers are 131–141; bearish bands use a separate
display direction so boolean values remain 0/1. Research-only entries show definitions, saved outcomes and
provenance; they have no fabricated dated value, runtime weight or price marker.
The selected executable rule's dated values come from the same compiled detector used by the event-model
smoke test; navigation and rule selection only render cached values. Event
onsets are translucent red markers, persistent signed states are pale green/red
bands, and R03 additionally shows its R01 waiting state in pale yellow. EMA and
MyRSI selectors are enabled automatically when the selected detector requires
them. The bottom value rows show the selected rule and only the checked
indicators. Its vertical analysis/chart divider and horizontal SPY/indicator
divider persist as normalized ratios. Two additional visible five-pixel brown
dividers in eleven-pixel white gutters resize Indicators/Rules/Description
independently and persist the three column shares. The Indicators column can
shrink to 52px (checkboxes and color swatches, full names in tooltips); Rules
can shrink to 110px (permanent number and code, such as `08 T08`). Wider
columns restore their names. The description can collapse to 0px; the entire
analysis area can shrink to 200px. Saved ratios must preserve those compact
positions instead of clamping them back to the former 15%/28% limits.
Market Setup keeps only MyRSI_rep(14,8) + AGC(77) and
MyRSI_rep(10,8) + AGC(77); it does not expose the ensemble-only 14,7 variant.
The selector order follows the ensemble inputs, with ARSI14 and the second
MyRSI added: CNN Fear Greed, Risk Measure, Panic Euphoria, ARSI14, the two
MyRSI series, Trend Score, Aroon Up40, Aroon Down48, DI-7/AGC, DI+14/AGC,
Correlation Angle28/180, Correlation Trend35/AGC, Hurst120 with inverse Fisher,
CMF80 with inverse Fisher, Decibel96 with inverse Fisher, Jocker, Market Breadth,
Dumb Money and Smart Money. Jocker uses ZZ1/ZZ2 mode0, window65, EMA3; Market
Breadth uses VWMA64 and EMA1 for SPY/XLV/XLU/XLE/XLP. DI and Correlation Trend
use the ensemble's 0.991 decaying peak AGC; the two MyRSI use half-life77.
The JSON catalog's `detector_descriptions_ru` contains all 75 readable Russian
detectors. Descriptions use compact bullet headings and show dated evidence.
Descriptions omit the redundant rule-number/type line and the separate
"Breeze weights" heading. Instead the three weights appear near the top as
`Soft`, `Standard`, `Hard`, matching the Phase Map profile names. Beside the
Rules heading an unchecked-by-default `Активные` checkbox filters to rules
with any nonzero Breeze profile weight, including negative weights. Empty
families are omitted; selection is retained if still visible, otherwise the
first visible rule is selected. Clearing the filter restores the full catalog.
The filter uses only actual DLL weights; accepted research overlays without a
runtime detector do not become active merely because they were accepted for research.
Filtering is view-only and does not modify weights, scoring, dates or inputs.
T02's compiled fraction is retained, but its green band requires exactly 1
(all four EMA rising). The cursor reports the count and each daily EMA delta.
U01 is an event sequence: actual qualifying second exits are red, their prior
first exits blue, and renewed saturation between them yellow. First-event
highlighting is retrospective context for a completed sequence; it does not
change any causal detector or score. Navigation renders only precomputed data.
Four unchecked-by-default breadth selectors follow EMA: S&P 500 SMA50, SMA100,
SMA200, and Nasdaq-100 SMA100. They use the existing calculated breadth series
and render in the lower Market Setup pane. Their shared percentage Y scale is
on the right and auto-scales from all selected visible breadth values with 5%
padding (at least 1 percentage point), bounded by 0..100%. Normalized sentiment
indicators retain an independent left Y scale. With breadths alone the unused
left axis is hidden; without breadths the existing indicator-only axes remain.
Adding breadths must not alter indicator Y positions. Both groups use the
same master date slots, including gaps, viewport and pinned cursor. Breadth
date indexes are built once when data is assigned; selectors and navigation
never load providers or calculate/export series. Selected breadth percentages
also appear in the bottom cursor values.
ATRP(1) is the final selector in that same lower block, immediately after
Nasdaq-100 SMA100, rather than a normalized sentiment indicator. It is off by
default and opens a separate synchronized percentage pane below the existing
indicator/breadth pane. The pane uses the same master dates, including gaps,
50px horizontal margins and two right-padding slots. Its visible splitter uses
the standard 5px strip in an 11px white gutter; the indicator/ATRP ratio is saved
as `MarketSetupIndicatorsRatio`. Selecting T03 enables all four breadths and
ATRP alongside EMA, and the ATRP pane shows the existing 2% reference. These
view actions never change calculations, scoring weights or CSV exports.
Market Setup removes unused panes and their splitter gutters entirely. With
EMA only, SPY fills the chart area. Indicators/breadths and ATRP can each be
shown independently, and three panes appear only when both lower groups are
selected. Saved top/bottom and indicator/ATRP ratios survive hiding and reopening.
Selections distinguish manual choices from rule dependencies: T03-to-T01/T04
removes automatically enabled breadths/ATRP, retaining manually chosen layers.
Selecting the same rule again preserves manual suppression of its dependencies.
After a layer/rule change, the layout is measured before one shared viewport
refresh recalculates visible Y scales against the existing date interval.
Trading Ensemble calculates nine independently selectable packages directly:
the original TorchSharp4GPU ZZ1/ZZ2/ZZ3 (`trading_package_Jordan_ZZ1` through
`trading_package_Jordan_ZZ3`), TorchSharp ZZ1/ZZ2 (`trading_package`, `trading_package_zz2`), Jordan ZZ1/ZZ2
(`trading_package_J`, `trading_package_zz2_J`), and Encog ZZ1/ZZ2
(`trading_package`, `trading_package_zz2`). TorchSharp/Jordan and Encog use
three separate selectable root directories. The three original GPU selectors
and all six added selectors are checked by default. Clearing individual selectors
replaces the former numeric `ALL 6` mode. The calculation uses
the same feature formulas, per-model classification, Bench ordering, and
ten-decile grouping as WealthLab проторговка. A broken model is skipped with a
warning; a missing/broken package is disabled without stopping the other five.
It does not create or read a derived ensemble/decile cache.
The main `Calculate 12 years` pass loads SPY but does not run package inference.
Every successful Recalculate invalidates ensembles created from the previous
market-data generation. Inference starts asynchronously when the Trading Ensemble
tab is open and only after all required latest inputs are ready; stale sentiment,
breadth, or sector inputs produce an explicit waiting message instead of being
silently replaced by zero. A failed or cancelled calculation can be retried. There is no
manual recalculate button. The compact read-only working-directory field opens
a real, lazily populated tree of ready drives and their folders. Expanding a
node enumerates only its immediate children; never recursively scan an entire
drive merely to populate the selector.
Navigation, package visibility, EMA visibility, and the active-series mode are
view-only and never rerun inference. Ensemble and decile panes always retain
the green dashed 50% reference. Scoring classification is disabled and has no
toolbar control. A three-position combo changes the fourth pane and the profit
calculation without rerunning inference. `Active Deciles` is the default and sums
each selected package's deciles at or above 50. `Ensembles 0–10` converts every
selected package to the proportional 0..10 position used by reference mode 0,
then averages available packages. `Ensembles 0/10` assigns each package either
all ten units when Ensemble % is at least 50, or zero units below 50, then averages
available packages. Both ensemble modes use the fixed vertical range 0..10;
the title switches between `Без люстры / С люстрой · Децили`, `Без люстры / С люстрой · Ансамбли`, and
`Без люстры / С люстрой · Ансамбли 0/10`. Decile mode uses the physical range from zero to
`10 × selected packages`; both ensemble modes use 0..10. Therefore line height is
directly comparable with the displayed percentage. The last annotation shows both
values as `Без люстры: N (P.P%) · С люстрой: N (P.P%)` for the latest real SPY date. The
missing future slot is not treated as a zero count.
Trading-ensemble pane titles are drawn inside their panes in the same visual
style as the Contango chart titles. Its white toolbar contains the three compact
TorchSharp4GPU/TorchSharp/Encog directory selectors followed by their 3/4/2 package
selectors, the unlabeled `− value +` Forecast control, short `EMA`, and the
three-position trading-mode combo. The duplicate local S&P 500 selector, working-directory
caption, and local green calculation status are intentionally absent; the global
price selector and the window's top status line remain authoritative.
The chart surface is white, and every one of its four panes is a separate ivory
`#FFFCF4` card with a thin one-pixel `#CDC3AB` frame extending around the full
pane, including its scale-label area. The multi-pane control itself has no
second outer frame or margin around those cards. Each pane reserves a title
header matching Contango: 14-point semibold title at the upper left; the S&P
pane also shows `Last: 0.00` at the upper right in 11-point text. Each plot has
a distinct 0.9-pixel dark right-axis line, 10-point scale labels in the same
color, and two empty date slots so the final bar stops before the axis. When a
next-bar forecast is present, all of its markers and price/ensemble gradient
scales are placed exactly two ordinary bar intervals to the right of the last
real bar, while the right-axis padding remains separate. Each
pane has three internal horizontal
guides (five integer scale levels including its top and bottom borders) and
matching scale labels on both the left and right. The 50% guide in the Ensemble
and Decile panes is green and dashed. Three draggable horizontal dividers let
the user resize adjacent panes without activating horizontal chart panning.
Their normalized boundary positions are persisted in the user-writable
`%APPDATA%\WealthLab8\BreezeLayout.json` file and restored on every opening,
independently of whether a Wealth-Lab workspace was saved. Invalid or absent
values retain the defaults. `WorkspaceString` remains a compatibility fallback.
Each divider is a persistent five-pixel `#D7CFBB` strip matching the All breadths
ATRP splitter. It is centered in an eleven-pixel white gutter, leaving a
three-pixel white separation from both neighboring pane frames; the interaction
must not rely on an invisible hit target. Package
names are not duplicated as a legend above the chart. Ensemble curves retain
package colors, while the ten decile ranks use the shared red-to-blue ten-color
palette. On this tab only, the shared bottom strip replaces the two general
cursor-value rows with one compact Trading Ensemble row. It shows the shared
cursor date, exact-date percentages without and with the chandelier, the active Chandelier stop
with two decimals, and every selected package's Ensemble % value with one decimal
and the package's line color (without percent signs). It retains the complete standard
navigation group without modification: minus, visible-bar count, plus, history
slider, and date range. Leaving the tab restores the two general cursor-value rows.
Trading Ensemble permanently applies a 3.75% Chandelier to every open virtual
position. The stop is based on the highest High since entry and can only rise.
The black dot dated `D` is the maximum protective `StopForBar` prepared after
`D-1`; it must never be calculated from the still-unknown High of candle `D`.
A hollow black circle in a separate future slot to the right of the latest candle
is the stop already calculated after the latest close for the next trading
session. When the latest bar is the entry day, this projected order uses that
bar's High. It is not active inside the already completed entry bar and does not
change completed-history profit accounting.
A black cross marks actual execution on `D`. Normally it coincides with the stop;
when `Open[D]` gaps below the level, the cross is drawn at `Open[D]`, because that
is the simulated fill. The active dot remains visible on the execution date so
the order that caused the exit is explicit. After execution, the complete SPY
trading bar `D+1` is skipped; if the ensemble still requests a position, re-entry
is allowed only on the following trading bar, and that new trade's first stop
becomes active one further bar later. Profit accounting remains position-by-position,
but the display caches only the maximum stop and maximum execution per date, so
navigation never creates redundant lower markers. The price-pane `Last / Stop`
annotation is right-aligned; the left heading shows `Без люстры / С люстрой / Stop`.
Absent positions display an em dash. Cursor movement, zoom, panning and resizing
select these cached dated maxima and never rerun the trading simulation or neural
inference.
The same Trading Ensemble surface draws one light-green 50%-opacity vertical
band through all four plot panes at the start of the recent-performance window.
A dedicated light-green minus/value/plus group appears in the shared bottom
strip before the standard visible-bar group. It defaults to 150 SPY trading
intervals on every new application start, changes in steps of 10, and is not
stored in the layout file. Changing it moves the band and updates the displayed
period dates and `Str%` column from already calculated model results; it must not
restart a provider request or neural-network inference. Calendar-day subtraction
is forbidden.

All custom candlestick panes derive body width from the current horizontal slot:
the body occupies one half of a slot, so the body and the empty interval are
approximately equal at every zoom level. Width is not capped at a fixed pixel
value. The future price gradient remains twice the current candle-body width.
When one or several ZZ packages are selected, the Ensemble forecast gradient is
aggregated across all selected next-bar scenarios: its lower and upper limits
are the combined minimum and maximum, while the yellow center is the average
0% scenario across the selected packages. A dedicated toolbar status reports
Forecast progress and completion; results from a cancelled older generation
must never replace a newer calculation.
The same layout file stores every user-adjustable divider position: Fuzzy, the
three Trading Ensemble pane boundaries, All breadths/ATRP, both Full-grid
boundaries, the vertical and horizontal Contango proportions, Titanic, Zweig,
Sentiment, and both Market Setup boundaries. Values are normalized ratios rather than
pixels, are saved when dragging ends and again when the window unloads, and are
written through a temporary file before atomic replacement.
Fuzzy map reserves the left one-quarter column for a compact cursor-date market
explanation and the right three-quarters for the synchronized chart. A five-pixel
vertical brown `GridSplitter` separates the columns and lets the user resize them
horizontally; the selected proportion is persisted in `BreezeLayout.json`. The
left header stays on one line: `Market state at cursor` at the left and
`Model: [profile]` at the upper-right. The explanation
is derived exclusively from already calculated nearest-date points and must not
trigger calculations or provider requests. Historical 20-bar Phase and EMA
outcome summaries are also precomputed during Calculate 12 years; cursor motion
only selects the matching summary.
The explanation column begins with a MODEL POSITION card synchronized to the
same cursor date. It uses the already calculated Hold and VIX/SVXY points to show
the ten-contract target, prior-trading-day comparison, action, strongest support,
and strongest risk. Cursor movement must only perform dated lookup and formatting;
it must not calculate Hold, request history, or export files.
The Fuzzy map includes Phase, continuous 0..100 Hold confidence, five-level
VIX-curve, and blue SVXY recommendation ribbons. The identical dated Hold
confidence series is also rendered at the bottom of Titanic and between the SPY
and breadth panes on Zweig. Each of those three tabs has its own checked-by-default
Hold % visibility toggle. Toggling is view-only. Titanic and Zweig do not render
duplicate left/right date captions because the shared bottom navigation already
shows the active range. Hold confidence is precomputed from Phase, EMA, breadth,
ATRP, Contango, Titanic, and Zweig inputs; navigation and visibility changes only
select or render its dated value and never recalculate the score. On the Contango tab, the S&P panel shows no ribbons, while the
lower-left SVXY panel shows both the five-level VIX ribbon and the blue SVXY
recommendation ribbon. All
instances use the same master dates, curve-state series, viewport, palettes,
and cursor. Missing VIX dates render gray in the VIX row and neutral white in
the SVXY row without shifting any bar index.

The VIX ribbon has five ordered states: strong contango, contango, mixed,
backwardation, and strong/broad backwardation. A viewport change is not allowed
to recalculate these states; it only selects and draws already loaded dated
values.
The SVXY row uses a distinct blue palette: saturated blue on the red-regime
exit event, light blue for the statistically tested bars 1..60, very pale blue
for Extended Hold from bar 61 until a return to a red curve score, and white
when neutral. It must render the precomputed recommendation and
bars-since-candidate values; viewport navigation must never restart or modify
that state machine.
The Contango S&P control must be included explicitly in that method; its left
term-structure panel is not a time-series viewport and follows only cursor date.
First-time Contango initialization must not issue its VIX provider request while
`Calculate 12 years` is active. It remains pending with a visible wait banner
and starts automatically after the main calculation finishes. Since the Fuzzy
map consumes the curve-state ribbon, post-calculation initialization no longer
depends on the Contango tab remaining selected.

## Cursor synchronization

`BreadthChart` (including the Market Setup price pane), `AtrpChart`,
`FuzzyStateChart`, `TitanicChart`, `SentimentChart`,
`ZweigChart`, and `TradingEnsembleChart` publish cursor-date changes to the common
`BreezeWindow.UpdateCursorValues` path. The cursor date and bottom value rows must
remain synchronized when moving between charts and tabs.

Pressing the left mouse button anywhere inside the tab area temporarily changes
the pointer to the horizontal `SizeWE` arrows; releasing the button restores the
previous pointer. A double click on the chart at the current vertical cursor
pins the shared cursor date. Every participating chart then renders the same
cursor as a thicker blue line and ignores hover-date changes. A subsequent
single click on a chart releases the pin before ordinary navigation resumes.
Clicks in the Fuzzy explanation panel do not release the pin.

The mouse wheel is routed by the surface under the pointer. Over a chart it
zooms the shared viewport by `VisibleBarStep`. Anywhere over the left Fuzzy
explanation panel, including its title and MODEL POSITION card, it scrolls the
explanation text and must not zoom the chart. Do not use a generic ancestor
`ScrollViewer` test here: the WPF `TabControl` template can contain its own
scroll viewer and would incorrectly suppress zoom over the right-hand chart.

Series may have different calendars and missing observations. Resolve display values by date using nearest-observation lookup; do not synchronize by assuming equal array indices.

## Rendering and calculations

Wheel zoom, drag pan, slider movement, plus/minus buttons, tab changes, and resizing are view-only operations. They must never call `WLHost.GetHistory`, recalculate breadth, Fuzzy, Titanic, Zweig, EMA, or MyRSI, or rewrite CSV files.

Viewport changes are coalesced through the WPF dispatcher at render priority. Maintain this behavior or an equivalent one-frame throttle. Mouse capture must always be released on button-up and lost capture.

Horizontal ranges are shared. Vertical scales and left/right axes remain chart-specific and are recalculated from the visible data of each chart.

## Relevant implementation locations

- `Breeze/BreezeWindow.xaml`: shared routed mouse handlers, slider, and navigation controls.
- `Breeze/BreezeWindow.xaml.cs`: viewport state, zoom, pan, slider synchronization, cursor values, and tab refresh.
- `Breeze/BreadthChart.cs`: breadth viewport and cursor rendering.
- `Breeze/AtrpChart.cs`: All breadths ATRP(1) viewport and cursor rendering.
- `Breeze/FuzzyStateChart.cs`: Fuzzy viewport and cursor rendering.
- `Breeze/TitanicChart.cs`: Titanic viewport and cursor rendering.
- `Breeze/SentimentChart.cs`: normalized multi-indicator viewport and cursor rendering.
- `Breeze/SentimentIndicators.cs`: Futures-default formulas and aligned sentiment data.
- `Breeze/ZweigChart.cs`: Zweig viewport and cursor rendering.
- `Breeze/TradingEnsembleChart.cs`: trading-ensemble panes, static 50% reference, and active-decile/active-ensemble count.
- `Breeze/TradingEnsembleData.cs`: in-memory ZZ1/ZZ2/ZZ3 chart records.
- `Breeze/TradingEnsembleInference.cs`: direct read-only JBIN/INI inference matching GPU проторговка.

## Acceptance checks

1. The date under the pointer remains visually anchored during wheel zoom.
2. Dragging any participating chart moves all charts and the slider together.
3. Switching tabs preserves range, zoom, and cursor date.
4. All views stop cleanly at the beginning and end of history.
5. Rapid input remains responsive and does not cause repeated full redraws.
6. Navigation performs no market-data request, indicator calculation, or export.
7. Each chart preserves independent vertical auto-scaling and its configured left/right axes.
8. One wheel action, one minus click, and one plus click each change the visible
   range by exactly 20 bars and keep every participating panel on the same start
   and end dates.
9. Fuzzy and Contango show identical VIX/SVXY values and colors for the same
   cursor date before and after zoom, pan, tab changes, and resize, even though
   Contango places both ribbons under the lower-left SVXY chart and no ribbon
   under S&P.
10. The optional VIX series in the SVXY panel shares the horizontal date viewport,
    uses an independent left auto-scale, and disappears together with that axis
    when its checkbox is cleared; SVXY remains on the right scale.
11. The shared Candles toggle renders both SVXY and the optional VIX overlay as
    OHLC candlesticks from already loaded histories. Clearing it restores both
    line renderings without a provider request or viewport change.
12. Moving the cursor updates the Fuzzy explanation to the same date as every
    chart. Switching among the reordered tabs preserves the viewport, cursor,
    and explanation; no text refresh may request or recalculate data.
13. Left-button down anywhere in the tab area shows horizontal move arrows and
    button-up, lost capture, or window unload restores the prior pointer.
14. Double-clicking a chart pins the current shared date; all cursor lines turn
    blue and remain fixed while the pointer moves into the Fuzzy explanation.
    One later chart click unpins them and restores hover following.
15. Wheel input anywhere inside the Fuzzy explanation scrolls that text only;
    it never changes the shared visible-bar count.
16. Dragging the brown divider resizes only the Fuzzy explanation and chart
    columns, preserves their minimum widths, and the saved workspace restores
    the chosen proportion. The model selector remains in the upper-right corner
    of the one-line left-panel header.
17. Trading Ensemble shows three internal horizontal guides and integer scale
    labels on both sides of every pane. The four ivory panes have independent
    thin frames on a white chart surface. Each of its three horizontal dividers
    is visibly filled with `#D7CFBB` and remains present around collapsed
    headers. Beside a collapsed pane it resizes the nearest expanded panes on
    either side without reopening the pane. Expanded headers use ▼, collapsed
    headers use ▲, and divider dragging never pans the shared time viewport.
18. The Trading Ensemble directory button opens a folder tree that can browse
    beyond the default path without a recursive whole-drive enumeration.
19. The three-position trading-mode combo defaults to the decile mode; the lower
    pane contains separate orange `Без люстры` and blue `С люстрой` lines. The orange value
    dated `D` comes from the signal formed on the preceding loaded SPY trading bar;
    it is not shifted by a calendar-day guess. `Last` contains both
    exact-date counts plus their percentages of available selected-package deciles.
    `Ensembles 0–10` shows the mean proportional position on a fixed 0..10 scale.
    `Ensembles 0/10` switches each available package between 0 and 10 at the 50%
    barrier and shows their mean. The same mode drives each package's Profit row,
    while source `Str%` remains unchanged. Switching must not rerun inference or
    alter the shared viewport. The left header shows the latest exact-date values as
    `Ансамбли: Без люстры P.P% · С люстрой P.P% · Stop NN.NN`, without a redundant Price
    field. A new SPY candle always belongs to the real timeline; only dates strictly
    later than the latest SPY candle can occupy the forecast slot. Hovering inside
    either line's 12-pixel hit area for 400 ms shows what that line means. The popup
    stays open without a timeout while the pointer remains inside the hit area and
    closes immediately when it leaves. The green 50 guides remain visible.
    The trading-mode description in the efficiency card wraps by words and reflows
    whenever the left analysis column is resized; it must never be clipped or ellipsized.
20. Every Trading Ensemble pane matches the Contango axis treatment: a dark
    right-axis line, 10-point labels in the same color, and right padding before
    the last bar. Its S&P header contains both `S&P 500 (SPY)` and the latest
    visible close. The tab has no redundant outer chart frame.
21. Selecting Trading Ensemble hides only the two cursor-value rows. The
    standard `minus / visible bars / plus / history slider / date range` group
    remains identical to every other tab. Selecting another tab restores the
    two cursor-value rows.
22. Sentiment opens with only CNN Fear Greed selected. Its SPY and indicator
    panes share the same dates and cursor as every other tab; changing any
    Sentiment checkbox is view-only. The exported combined file and twenty
    individual files contain the same precomputed values drawn on the chart.
23. Market Setup is the final rightmost tab. Selecting any of its 75 JSON-backed
    rules updates the description, lifecycle overlay, automatically required
    EMA/MyRSI layers, and cursor value without recalculation or provider access.
24. In All breadths `Full` mode, drag both the vertical and horizontal visible
    splitters. The four panels must resize in both directions and preserve the
    resulting proportions after reopening Breeze.
25. Market Setup's internal column dividers resize without chart panning;
    compact Russian explanations retain readable wrapped indicator labels.
    T02 at 2026-03-12 reports one rising EMA out of four without an all-rising
    green band. U01 shows the two exit dates and the intermediate saturation.
26. Collapse Market Setup to checkbox-only indicators, code-only rules and a
    hidden description; reopening must retain the compact column widths.
    Select each of the four breadths after EMA and combine them with MyRSI:
    percentages use the right axis, normalized indicators the left, with
    unchanged indicator coordinates and date-aligned gaps and cursor.
