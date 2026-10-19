# Contributing

Thanks for helping! Bug reports, fixes and documentation improvements are welcome.

- **Bugs**: open an issue with the [bug report form](https://github.com/manucruzleiva/ptcgl-auto-battlelog/issues/new/choose)
  and paste the tray menu's **Diagnostics ▸ Copy diagnostic info**. Security problems go through
  [SECURITY.md](SECURITY.md), not public issues.
- **Changes**: open an issue first for anything bigger than a small fix, so we can agree on the
  approach. Then send a pull request against `main`.

## Before you change anything

Read [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md). It is the contract between the plugin, the tray
app, the tests and the release pipeline: file names, JSON fields, command line, update rules.
A change that affects it must update it in the same pull request. The seal format is specified in
[docs/LOG-SEAL.md](docs/LOG-SEAL.md); `tools/verify_log.py` and the plugin must keep agreeing.

## Building and testing

You need the .NET 8 SDK version that `global.json` pins (exactly that one: it keeps release builds
reproducible) and Python 3.8+ (no Windows needed). Run the whole pipeline, exactly as
CI does:

```
./build.sh          # or .\build.ps1 on Windows
```

It must pass before a pull request is merged. Faster loops:

```
dotnet run --project tests/CoreTests -c Release    # plugin core
dotnet run --project tests/TrayTests -c Release    # tray logic + plugin/tray cross-part checks
python3 tools/verify_log.py --self-test           # seal verifier
```

Anything that can be tested without Windows or the game belongs in `src/Plugin/Core` or
`src/Tray/Logic` with tests; keep the Windows-only and Unity-only layers thin.

## Style

- English everywhere: code, comments, UI text, docs.
- Match the existing code: doc comments that explain *why*, compact, defensive (a failure is
  logged, never crashes the game or loses a log).
- No new runtime dependencies: the tray app must stay a single exe with framework references only,
  and the plugin a single DLL. Build-time packages are fine.
- Build paths with `Path.Combine`; the code runs on Windows and the tests on Linux too.
- `.editorconfig` and `.gitattributes` set formatting and line endings (`.ps1`/`.bat` CRLF,
  `.sh` LF).

By contributing you agree that your contribution is licensed under the [MIT License](LICENSE).
