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
| 1 | `pab-core`: logic without UI, ported with the C# tests. Log names, seal reading, updates, settings, paths, token files + DPAPI, tcgkb and Pokematch clients | done |
| 2 | The rest of `src/Tray/Logic`: upload queues, plugin install/repair, BepInEx cfg, plugin status, daemon judgement, self-update swap, command line, disclaimer text, 1.0 leftovers, uninstall order, tray.log | done |
| 3 | `pab-tray` (the exe). 3a: Windows layer (registry, processes, single instance, shortcuts, embedded payload). 3b: session, repair daemon, logs watcher, then tray, menu, notifications, disclaimer. 3c: installer/uninstaller, portable | 3a done, 3b in progress |
| 4 | Status window (egui) | |
| 5 | `2.0.0-beta.1` on the beta channel, then stable | |

```
cd rust
cargo test
```

`pab-tray/build.rs` embeds the plugin DLL (`src/Plugin/bin/Release`) and the pinned BepInEx zip
(`cache/`) when they exist; without them the exe builds but refuses to install. Release builds set
`PAB_REQUIRE_PAYLOAD=1`, plus `PAB_VERSION`, `PAB_REPO_OWNER` and `PAB_REPO_NAME`.

`pab-core/tests/golden` is written by the C# code itself (`tests/RustGoldens`, .NET 8, run with
TZ=UTC); the Rust tests compare against it byte for byte. Regenerate it there, never by hand:
`dotnet run --project tests/RustGoldens -c Release -- <folder>`, then copy the files you need (a
full run re-seals the logs with a new key).
