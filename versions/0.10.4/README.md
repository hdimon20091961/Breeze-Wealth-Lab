# Breeze 0.10.4

The full formatted Help is now a standalone `BreezeHelp.html` file installed
beside `Breeze.dll` and opened in the default browser.

- the external file is the primary Help;
- it is a self-contained HTML document with the full research tables;
- if the file is absent or Windows cannot open it, Breeze opens the embedded
  copy in its internal window;
- the embedded copy displays a red notice explaining why the fallback was used;
- Help installation and SHA-256 verification are handled together with the DLL.
