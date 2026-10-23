# Code review Breeze — 2026-10-23

## Scope and verification

Reviewed the current product sources in `Breeze/`, the compiled strategy
project, deployment scripts, maintained integration projects, repository
status, release artifacts, and the new persistent layout implementation.

Verification completed successfully:

- `Breeze` Release x64 build: 0 warnings, 0 errors;
- `Breeze.Strategies` Release build: 0 warnings, 0 errors;
- Fuzzy/Help integration smoke test: passed;
- Contango integration smoke test: 4,651 source files, passed;
- event-model integration test: 664,193 checks, 75 features, passed;
- no credential-like values were found in maintained product sources.

## Findings

### High — closing from a hidden tab can overwrite saved layout ratios

`BreezeWindow.SavePersistentLayout` reconstructs Fuzzy and Contango ratios from
`ActualWidth`/`ActualHeight` every time any splitter ends and again on
`Unloaded`. WPF tab content that is not currently measured can report zero.
`Ratio` then substitutes the hard-coded defaults, so closing Breeze while a
different tab is selected can replace previously saved Fuzzy or Contango
positions with 0.25, 0.50, or 0.6667.

Keep one in-memory `BreezeLayoutState` as the authority. Update only the ratio
owned by the splitter that completed its drag, and write that cached state.
Never reconstruct unrelated hidden-tab ratios during another splitter event or
window unload.

### High — multiple different binaries use the same 0.10.25 version

The current build, `release/Breeze.dll`, and `versions/0.10.25/Breeze.dll` have
three different hashes while all report file version `0.10.25.0`. This makes a
GitHub release named 0.10.25 ambiguous and prevents users from knowing which
binary contains Forecast and persistent-layout work.

Bump the reviewed snapshot to a new version, rebuild once, and copy that exact
binary plus matching Help and rules into the release directory. Record hashes
in the release notes.

### High — the working tree is not safe for a blanket publication

The branch contains a 185 MB root archive, local video/research trees, cached
models and runtimes, temporary compile-check projects, many historical binary
directories, and accumulated changes from several development stages. A broad
`git add .` would publish workstation-only material and likely exceed ordinary
GitHub file limits.

Create the release commit from an explicit allow-list. Add `E.ZIP`, `.codex/`,
temporary compile checks, and local research inputs/artifacts to `.gitignore`.

### Medium — deployment scripts are tied to one checkout path

Both deployment scripts use `D:\CODEX_PRG\Breadth` as their source root. A
GitHub checkout elsewhere cannot use them without editing the scripts.

Resolve sources from `$PSScriptRoot`; retain the Wealth-Lab installation path
as an optional parameter with the current Program Files location as default.

### Medium — model calculation can silently fall back

`RefreshHoldConfidence` still discards the error returned by
`ResearchHoldConfidenceCalculator.TryCalculate` and silently substitutes the
legacy calculation. Corrupt data or a programming error therefore leaves the
Soft/Standard/Hard selector visible while a different model drives the screen.

Distinguish the expected first-snapshot-not-ready condition from other errors,
retain the last valid research result, and surface unexpected failures.

### Medium — machine-specific runtime data roots remain

Product and strategy sources still contain fixed `D:\fear_greed_close` and
`D:\TorchSharp4GPU` defaults. This is usable on the current workstation but a
fresh GitHub checkout is not a self-contained runtime configuration.

Document these as required external data roots and preferably add one shared
user configuration file before calling the repository portable.

### Test gap — WPF layout persistence is not automated

The numerical integration coverage is strong, but there is no automated test
for dragging all seven splitters, switching to another tab, closing, reopening,
and verifying `BreezeLayout.json`. This exact path exposed the current high
finding and should remain a manual release acceptance check if WPF automation
is not added.

## Recommended minimal GitHub snapshot

Include only:

- `.gitignore`, `README.md`;
- maintained source under `Breeze/` and `Breeze.Strategies/`, excluding
  `bin/` and `obj/`;
- maintained tests under `tests/Contango`, `tests/EventModel`, and
  `tests/Fuzzy`, plus `tests/README.md`;
- documentation required by those tests, especially `docs/chart-navigation.md`
  and `docs/event-catalog-v1/`;
- portable deployment scripts;
- one synchronized `release/` set: `Breeze.dll`, `Breeze.Strategies.dll`,
  `BreezeHelp.html`, `BreezeRules.json`, hashes, and concise release notes.

Exclude local caches, videos, generated research artifacts, `E.ZIP`, the
temporary Futures compile-check tree, historical `versions/` binaries, machine
deployment results, and unrelated experimental TorchSharp source copies.

## Publication decision

Do not publish the current working tree. Correct the two release-blocking code
and versioning findings, make deployment scripts portable, rerun the existing
test suite, perform the seven-splitter close/reopen acceptance check, and then
publish an explicit minimal commit on a new release branch.

## Resolution

The release-blocking findings were corrected for 0.10.26 before publication:

- one cached `BreezeLayoutState` is now authoritative, and each splitter updates
  only its own value before the JSON file is written;
- both assemblies carry version 0.10.26 and the `release/` files are generated
  from that single reviewed build;
- deployment scripts resolve build files from `$PSScriptRoot` and accept an
  optional Wealth-Lab installation directory;
- expected first-snapshot unavailability is separated from unexpected research
  model failures; unexpected failures are visible and retain the last result;
- publication uses an explicit allow-list and excludes caches, videos, local
  research data, historical binaries and temporary compile-check projects.

Release verification passed with zero build warnings/errors, all 664,193
event-model checks, the Fuzzy/Help integration test, and the 4,651-file
Contango integration test.
