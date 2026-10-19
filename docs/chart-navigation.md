# Breeze synchronized chart navigation

This document defines the project-specific configuration for the global `$synchronized-chart-navigation` skill. The reusable behavior belongs in the skill; values and component names specific to Breeze belong here.

## Current controls

- Default visible range: 200 bars, aligned to the most recent observation.
- Minimum visible range: 50 bars.
- Wheel and plus/minus increment: exactly 25 bars per user action. Keep this
  value in the shared `VisibleBarStep` constant; do not duplicate a literal in
  individual chart controls.
- Slider small change: 10 bars.
- Slider large change: 100 bars.
- The S&P 500 SMA(200) breadth series is the master navigation timeline.
- Plus/minus buttons preserve the current right edge.
- Mouse-wheel zoom is anchored at the current cursor date and falls back to the center of the viewport.
- Left-button horizontal drag pans the same shared position used by the history slider.

## Participating charts

Every horizontal viewport change must be applied to:

- All breadths S&P 500 SMA(50), SMA(100), and SMA(200);
- All breadths Nasdaq-100 SMA(100);
- the All breadths combined chart and its optional ATRP(1) lower panel;
- Fuzzy map;
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
The visible tab order is Fuzzy map, All breadths, Contango, Titanic, then Zweig.
Fuzzy map reserves the left one-quarter column for a compact cursor-date market
explanation and the right three-quarters for the synchronized chart. A five-pixel
vertical brown `GridSplitter` separates the columns and lets the user resize them
horizontally; the selected proportion is persisted in `WorkspaceString`. The
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

`BreadthChart`, `AtrpChart`, `FuzzyStateChart`, `TitanicChart`, and `ZweigChart` publish cursor-date changes to the common `BreezeWindow.UpdateCursorValues` path. The cursor date and bottom value rows must remain synchronized when moving between charts and tabs.

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

## Acceptance checks

1. The date under the pointer remains visually anchored during wheel zoom.
2. Dragging any participating chart moves all charts and the slider together.
3. Switching tabs preserves range, zoom, and cursor date.
4. All views stop cleanly at the beginning and end of history.
5. Rapid input remains responsive and does not cause repeated full redraws.
6. Navigation performs no market-data request, indicator calculation, or export.
7. Each chart preserves independent vertical auto-scaling and its configured left/right axes.
8. One wheel action, one minus click, and one plus click each change the visible
   range by exactly 25 bars and keep every participating panel on the same start
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
