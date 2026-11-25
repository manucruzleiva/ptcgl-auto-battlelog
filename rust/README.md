# Rust tray (in progress)

The tray app (`PtcglAutoBattleLog.exe`) is being rewritten in Rust: one exe with no runtime
dependencies, `tray-icon` + `muda` for the tray and menu, `egui` for the status window. The BepInEx
plugin stays in C# (`src/Plugin`): BepInEx only loads .NET assemblies inside the game's Mono runtime,
so the seal (docs/LOG-SEAL.md) and the `pluginVersion` tcgkb checks do not change.

Until the Rust tray ships, the C# tray in `src/Tray` is what releases are built from.

What stays compatible: the exe name, `%LOCALAPPDATA%\PtcglAutoBattleLog` and its `settings.json`,
the DPAPI-protected tcgkb and Pokematch tokens (same entropy, so nobody signs in again), the
command-line arguments the 1.6.x updater passes, `update.json`, and the tcgkb and Pokematch APIs.

| Phase | What | State |
|---|---|---|
| 1 | `pab-core`: logic without UI, ported with the C# tests (log names, seal reading, updates; then settings, paths, tcgkb/Pokematch clients, DPAPI) | in progress |
| 2 | Daemon: watch logs, upload, repair/install the plugin, self-update | |
| 3 | Tray, menu, notifications, disclaimer, installer/uninstaller, portable | |
| 4 | Status window (egui) | |
| 5 | `2.0.0-beta.1` on the beta channel, then stable | |

```
cd rust
cargo test
```
