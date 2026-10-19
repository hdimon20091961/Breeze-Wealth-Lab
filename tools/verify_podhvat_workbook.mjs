import { createRequire } from "node:module";
const require = createRequire(import.meta.url);
const { FileBlob, SpreadsheetFile } = require("@oai/artifact-tool");

const file = String.raw`D:\CODEX_PRG\Breadth\artifacts\Podhvat_1\research\podhvat_combined_analysis.xlsx`;
const wb = await SpreadsheetFile.importXlsx(await FileBlob.load(file));
const overview = await wb.inspect({ kind: "table,formula", sheetId: "Overview", range: "A1:H24", maxChars: 5000, tableMaxRows: 24, tableMaxCols: 8 });
console.log(overview.ndjson);
for (const error of ["#REF!", "#DIV/0!", "#VALUE!", "#NAME?", "#N/A"]) {
  const found = await wb.inspect({ kind: "match", searchTerm: error, options: { matchCase: false, matchEntireCell: false, maxResults: 20 }, maxChars: 2000 });
  console.log(`${error}: ${found.ndjson}`);
}
