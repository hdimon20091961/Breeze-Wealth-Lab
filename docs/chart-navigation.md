# Breeze synchronized chart navigation

This document defines the project-specific configuration for the global `$synchronized-chart-navigation` skill. The reusable behavior belongs in the skill; values and component names specific to Breeze belong here.

## Current controls

- Default visible range: 200 bars, aligned to the most recent observation.
- Minimum visible range: 20 bars.
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
- Trading Ensemble (SPY/EMA, ensemble percentages, package deciles, and the summed active-decile count);
- Titanic;
- Zweig.
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
The visible tab order is Fuzzy map, Trading Ensemble, All breadths, Contango, Titanic, then Zweig.
Trading Ensemble calculates ZZ1, ZZ2, and ZZ3 directly from the existing
`trading_package_Jordan_ZZ1` through `trading_package_Jordan_ZZ3` JBIN/INI
packages in the selected working directory, using the same feature formulas,
model classification, Bench ordering, and ten-decile grouping as GPU
проторговка. It does not create or read a derived ensemble/decile cache.
The main `Calculate 12 years` pass loads SPY but does not run package inference.
Inference starts once, asynchronously, when the Trading Ensemble tab is first
opened after SPY is ready; returning to the tab does not rerun it. There is no
manual recalculate button. The compact read-only working-directory field opens
a real, lazily populated tree of ready drives and their folders. Expanding a
node enumerates only its immediate children; never recursively scan an entire
drive merely to populate the selector.
Navigation, package visibility, EMA visibility, and scoring-classification
visibility are view-only and never rerun inference. Ensemble and decile panes
always retain the green dashed 50% reference. The unchecked-by-default
`Scoring classification` option applies only the accepted TorchSharp R03 rule:
when either MyRSI 14,8 or MyRSI 10,8 exits upward from -1, the classification
threshold jumps from 50 to 70 for five trading bars and then returns to 50 in
one step. Those 70% intervals are red and dashed. Rejected or not-yet-enabled
events do not affect this line. Raw MyRSI curves and vertical event marks are
not drawn here. When the option is enabled, the active-decile panel counts
deciles against that exact daily R03 threshold; otherwise it counts against 50.
Its vertical range uses the exact visible finite minimum and maximum without
padding; a constant series is centered without inventing false neighboring
values, and the latest finite count is shown as `Last: N`. The missing future
slot is not treated as a zero count.
Trading-ensemble pane titles are drawn inside their panes in the same visual
style as the Contango chart titles. Its white toolbar contains the working
directory, S&P 500, ZZ1, ZZ2, ZZ3, EMA, and Scoring classification selectors.
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
palette. On this tab only, the shared bottom strip hides the two cursor-value
rows. It retains the complete standard navigation group without modification:
minus, visible-bar count, plus, history slider, and date range. Leaving the tab
restores the two cursor-value rows.

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
The same layout file stores all seven user-adjustable divider positions: Fuzzy,
the three Trading Ensemble pane boundaries, All breadths/ATRP, and the vertical
and horizontal Contango proportions. Values are normalized ratios rather than
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

`BreadthChart`, `AtrpChart`, `FuzzyStateChart`, `TitanicChart`, `ZweigChart`, and
`TradingEnsembleChart` publish cursor-date changes to the common
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
- `Breeze/ZweigChart.cs`: Zweig viewport and cursor rendering.
- `Breeze/TradingEnsembleChart.cs`: trading-ensemble panes, static 50% reference, and active-decile count.
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
    is visibly filled with `#D7CFBB`, sits inside a white gutter, resizes only
    the adjacent panes, and never pans the shared time viewport.
18. The Trading Ensemble directory button opens a folder tree that can browse
    beyond the default path without a recursive whole-drive enumeration.
19. With `Scoring classification` off, both threshold guides and active-decile
    counts use 50. With it on, only R03 may replace the daily threshold with 70
    for five bars; the same dates must be red-dashed in both percentage panes
    and must drive the active-decile count. The green 50 guide remains visible.
20. Every Trading Ensemble pane matches the Contango axis treatment: a dark
    right-axis line, 10-point labels in the same color, and right padding before
    the last bar. Its S&P header contains both `S&P 500 (SPY)` and the latest
    visible close. The tab has no redundant outer chart frame.
21. Selecting Trading Ensemble hides only the two cursor-value rows. The
    standard `minus / visible bars / plus / history slider / date range` group
    remains identical to every other tab. Selecting another tab restores the
    two cursor-value rows.
