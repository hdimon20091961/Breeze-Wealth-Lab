import fs from "node:fs/promises";
import path from "node:path";
import { createRequire } from "node:module";

const require = createRequire(import.meta.url);
const { SpreadsheetFile, Workbook } = require("@oai/artifact-tool");

const root = String.raw`D:\CODEX_PRG\Breadth`;
const research = path.join(root, "artifacts", "Podhvat_1", "research");
const outputPath = path.join(research, "podhvat_combined_analysis.xlsx");

function parseCsv(text) {
  const rows = [];
  let row = [], field = "", quoted = false;
  text = text.replace(/^\uFEFF/, "");
  for (let i = 0; i < text.length; i++) {
    const c = text[i];
    if (quoted) {
      if (c === '"' && text[i + 1] === '"') { field += '"'; i++; }
      else if (c === '"') quoted = false;
      else field += c;
    } else if (c === '"') quoted = true;
    else if (c === ',') { row.push(field); field = ""; }
    else if (c === '\n') { row.push(field.replace(/\r$/, "")); rows.push(row); row = []; field = ""; }
    else field += c;
  }
  if (field.length || row.length) { row.push(field.replace(/\r$/, "")); rows.push(row); }
  return rows;
}

function typed(rows) {
  if (!rows.length) return rows;
  const dateCols = new Set(rows[0].map((v, i) => /Date$/.test(v) ? i : -1).filter(i => i >= 0));
  return rows.map((row, ri) => row.map((v, ci) => {
    if (ri === 0 || v === "") return ri === 0 ? v : null;
    if (dateCols.has(ci) && /^\d{4}-\d{2}-\d{2}/.test(v)) return new Date(v.slice(0, 10) + "T00:00:00");
    if (v === "True" || v === "TRUE") return true;
    if (v === "False" || v === "FALSE") return false;
    const n = Number(v);
    return Number.isFinite(n) ? n : v;
  }));
}

async function loadCsv(name) {
  return typed(parseCsv(await fs.readFile(path.join(research, name), "utf8")));
}

function title(sheet, text, subtitle, lastCol) {
  sheet.showGridLines = false;
  sheet.getRange(`A1:${lastCol}1`).merge();
  sheet.getRange("A1").values = [[text]];
  sheet.getRange("A1").format = {
    fill: "#17365D", font: { color: "#FFFFFF", bold: true, size: 18 },
    rowHeight: 30, verticalAlignment: "center"
  };
  sheet.getRange(`A2:${lastCol}2`).merge();
  sheet.getRange("A2").values = [[subtitle]];
  sheet.getRange("A2").format = { fill: "#D9EAF7", font: { color: "#17365D", italic: true }, wrapText: true, rowHeight: 34 };
}

function styleTable(sheet, rows, startRow, startCol = 0) {
  const rowCount = rows.length, colCount = rows[0]?.length ?? 0;
  if (!rowCount || !colCount) return;
  const range = sheet.getRangeByIndexes(startRow, startCol, rowCount, colCount);
  range.values = rows;
  const header = sheet.getRangeByIndexes(startRow, startCol, 1, colCount);
  header.format = { fill: "#4472C4", font: { color: "#FFFFFF", bold: true }, wrapText: true, rowHeight: 30 };
  range.format.borders = { preset: "all", style: "thin", color: "#D9E2F3" };
  range.format.verticalAlignment = "center";
  sheet.freezePanes.freezeRows(startRow + 1);
}

const summaryRows = await loadCsv("event_summary.csv");
const wfRows = await loadCsv("walk_forward.csv");
const eventRows = await loadCsv("events.csv");
const dailyAll = await loadCsv("daily_features.csv");
const keep = ["Date", "Open", "High", "Low", "Close", "ATRP_1", "EMA20", "EMA50", "EMA100", "EMA200", "SP500_SMA50", "SP500_SMA100", "SP500_SMA200", "NASDAQ100_SMA100", "Phase", "Contango30To60Pct", "Fwd5Pct", "Fwd10Pct", "Fwd20Pct"];
const headerIndex = new Map(dailyAll[0].map((v, i) => [v, i]));
const dailyRows = [keep, ...dailyAll.slice(1).map(r => keep.map(k => r[headerIndex.get(k)]))];

const wb = Workbook.create();
const overview = wb.worksheets.add("Overview");
const summary = wb.worksheets.add("Event Summary");
const wf = wb.worksheets.add("Walk Forward");
const events = wb.worksheets.add("Events");
const daily = wb.worksheets.add("Daily Data");
const method = wb.worksheets.add("Methodology");

title(overview, "Podhvat: combined quantitative review", "Video-derived hypotheses tested on the complete daily history. Near-duplicate frames are not counted as separate trades.", "H");
overview.getRange("A4:B10").values = [
  ["Coverage", "Value"],
  ["Podhvat_1 candidate images", 116],
  ["All processed candidate images", 439],
  ["Contact sheets (review aids)", 22],
  ["Daily observations", 3016],
  ["Market events after de-duplication", null],
  ["History", "2014-10-13 to 2026-10-09"],
];
overview.getRange("A4:B4").format = { fill: "#4472C4", font: { color: "#FFFFFF", bold: true } };
overview.getRange("B9").formulas = [["=SUM('Event Summary'!C5:C11)"]];
overview.getRange("D4:H4").merge(); overview.getRange("D4").values = [["What was tested"]];
overview.getRange("D4:H4").format = { fill: "#70AD47", font: { color: "#FFFFFF", bold: true } };
overview.getRange("D5:H10").merge();
overview.getRange("D5").values = [[
  "1) Stable growth: bullish EMA order, positive 5-day slopes, four breadth series above 50%.\n" +
  "2) EMA pickup: green recovery close above EMA20 after a close below EMA20 in the previous five bars.\n" +
  "3) Strong anchor bars: bearish body/day return ≤ -2%; bullish ≥ +2.5%; next-day 50% retracement is checked with three midpoint definitions."
]];
overview.getRange("D5:H10").format = { wrapText: true, verticalAlignment: "top", fill: "#E2F0D9" };
overview.getRange("A12:H12").merge(); overview.getRange("A12").values = [["Interpretation guardrail"]];
overview.getRange("A12:H12").format = { fill: "#F4B183", font: { bold: true, color: "#7F4122" } };
overview.getRange("A13:H16").merge();
overview.getRange("A13").values = [["This is a historical temporal replay, not a pristine out-of-sample proof: the ideas were formulated after viewing historical charts. A genuine forward test starts only after the rule set is frozen and future unseen bars arrive."]];
overview.getRange("A13:H16").format = { wrapText: true, verticalAlignment: "top", fill: "#FCE4D6" };
overview.getRange("A18:B22").values = [
  ["Data control", "Status"],
  ["Fresh ATRP export", "Included"],
  ["ATRP export vs ATRP_1", "Exact match (max abs diff 0.0)"],
  ["Snapshot folder", "artifacts/Podhvat_1/market_data"],
  ["Event rows", null],
];
overview.getRange("A18:B18").format = { fill: "#4472C4", font: { color: "#FFFFFF", bold: true } };
overview.getRange("B22").formulas = [["=COUNTA(Events!A5:A1000)"]];
overview.getRange("A1:H24").format.font = { name: "Aptos", size: 10 };
overview.getRange("A:H").format.columnWidth = 16;
overview.getRange("A:A").format.columnWidth = 30;
overview.getRange("B:B").format.columnWidth = 28;

title(summary, "Event summary", "Forward returns are measured from the signal-day close. Win rate means a positive close-to-close return at the stated horizon.", "X");
styleTable(summary, summaryRows, 3);
summary.getUsedRange().format.font = { name: "Aptos", size: 9 };
summary.getUsedRange().format.autofitColumns();
summary.getRange("A:A").format.columnWidth = 22; summary.getRange("B:B").format.columnWidth = 22;
summary.getRange("D:Z").setNumberFormat("0.00");

title(wf, "Walk-forward temporal replay", "Each test year uses only earlier years to set fan-width percentiles and the ATRP median; the next calendar year is then evaluated without recalibration.", "P");
styleTable(wf, wfRows, 3);
wf.getUsedRange().format.font = { name: "Aptos", size: 9 };
wf.getUsedRange().format.autofitColumns(); wf.getRange("C:C").format.columnWidth = 22; wf.getRange("E:Q").setNumberFormat("0.00");

title(events, "De-duplicated events", "One row per market event; neighboring video frames are not observations. Source context (phase, contango, breadth, Titanic, Zweig) is joined by date.", "AR");
styleTable(events, eventRows, 3);
events.getUsedRange().format.font = { name: "Aptos", size: 8 };
events.getUsedRange().format.autofitColumns(); events.getRange("A:A").setNumberFormat("yyyy-mm-dd");
events.getRange("A:A").format.columnWidth = 12; events.getRange("B:C").format.columnWidth = 20; events.getRange("D:D").format.columnWidth = 52;

title(daily, "Daily source panel", "Selected daily inputs and forward returns. The complete immutable source CSV snapshot is stored alongside the video package.", "S");
styleTable(daily, dailyRows, 3);
daily.getUsedRange().format.font = { name: "Aptos", size: 8 };
daily.getUsedRange().format.autofitColumns(); daily.getRange("A:A").setNumberFormat("yyyy-mm-dd");
daily.getRange("A:A").format.columnWidth = 12; daily.getRange("B:S").format.columnWidth = 12;

title(method, "Methodology and forward-test protocol", "Rules frozen from the video interpretation; adaptive thresholds are estimated from past data only.", "H");
method.getRange("A4:B13").values = [
  ["Item", "Definition"],
  ["Frame accounting", "36 speech-linked anchors + 80 low-motion stable frames = 116 Podhvat_1 candidate images. Contact sheets are navigation aids."],
  ["Stable growth", "Close > EMA20 > EMA50 > EMA100 > EMA200; all EMA 5-day slopes positive; four breadth measures > 50%."],
  ["EMA pickup", "Green close above EMA20 after at least one close below EMA20 in the prior five bars; all EMA slopes positive."],
  ["Pickup depth", "Classified by the deepest EMA zone reached during the five-bar lookback."],
  ["Bearish anchor", "Red bar with body return or close-to-close return <= -2.0%."],
  ["Bullish anchor", "Green bar with body return or close-to-close return >= +2.5%."],
  ["50% retracement", "Checked separately for body midpoint, full-range midpoint, and previous-close-to-current-close midpoint."],
  ["Event spacing", "Stable starts: 10 bars; pickups: 5 bars; strong bars: 3 bars."],
  ["Walk-forward", "Expanding training sample. Train through year T-1, freeze train fan Q33/Q67 and ATRP median, test year T."],
];
method.getRange("A4:B4").format = { fill: "#4472C4", font: { color: "#FFFFFF", bold: true } };
method.getRange("A4:B13").format = { wrapText: true, verticalAlignment: "top", borders: { preset: "all", style: "thin", color: "#D9E2F3" } };
method.getRange("A:A").format.columnWidth = 24; method.getRange("B:B").format.columnWidth = 95;
method.getRange("A15:H15").merge(); method.getRange("A15").values = [["Why this is not yet a true untouched forward test"]];
method.getRange("A15:H15").format = { fill: "#F4B183", font: { bold: true } };
method.getRange("A16:H20").merge();
method.getRange("A16").values = [["The author inspected historical charts before the rules were formalized, so even year-by-year replay can inherit selection bias. It still protects against using future values for adaptive thresholds, but it does not erase the original visual hindsight. The strict test begins on the first bar after this workbook's rule freeze date."]];
method.getRange("A16:H20").format = { fill: "#FCE4D6", wrapText: true, verticalAlignment: "top" };
method.getUsedRange().format.font = { name: "Aptos", size: 10 };

wb.recalculate();
const inspect = await wb.inspect({ kind: "sheet,formula", maxChars: 5000, options: { maxResults: 50 } });
console.log(inspect.ndjson ?? String(inspect));
const previewRanges = {
  "Overview": "A1:H24",
  "Event Summary": "A1:X11",
  "Walk Forward": "A1:P27",
  "Events": "A1:AR35",
  "Daily Data": "A1:S35",
  "Methodology": "A1:H20",
};
for (const [sheetName, range] of Object.entries(previewRanges)) {
  const preview = await wb.render({ sheetName, range, scale: 0.8, format: "png" });
  await fs.writeFile(path.join(research, `preview_${sheetName.replaceAll(" ", "_")}.png`), new Uint8Array(await preview.arrayBuffer()));
}
const xlsx = await SpreadsheetFile.exportXlsx(wb);
await xlsx.save(outputPath);
console.log(`SAVED ${outputPath}`);
