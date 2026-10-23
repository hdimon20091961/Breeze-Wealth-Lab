# Breeze 0.10.26

Ready-to-install Wealth-Lab 8 files:

- `Breeze.dll` — Client Extension;
- `Breeze.Strategies.dll` — compiled strategy library;
- `BreezeHelp.html` — standalone Help opened by the extension;
- `BreezeRules.json` — the shared 75-event rule catalog;
- `SHA256SUMS.txt` — checksums for this exact release set.

Close Wealth-Lab, copy the four runtime files into its installation directory,
and restart Wealth-Lab. The default destination is
`C:\Program Files\Quantacula, LLC\WealthLab 8`.

The source build requires the locally installed Wealth-Lab assemblies. Runtime
market/research data is not bundled; the current defaults use
`D:\fear_greed_close` and `D:\TorchSharp4GPU`.
