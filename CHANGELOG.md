# Changelog

All notable changes to this project are documented in this file. The format is based on
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project uses
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

The section of a version becomes the top of its GitHub release notes, so add one before tagging
(see "Releasing" in the README).

## [Unreleased]

### Added

- **Upload my matches automatically** (tray menu, tcgkb account, off until you turn it on): every
  tracked match of the PTCGL accounts you claimed is sent to tcgkb.app, older ones included, so
  tcgkb can show your winrate, decks and the global meta. Each log is sent once; when tcgkb cannot
  be reached the app tries again later. **Upload matches now** sends what is pending at once.

## [1.2.0] - 2026-10-21

### Added

- **tcgkb account** (tray menu, optional): connect this computer to a tcgkb.app account with a
  code approved in the browser (OAuth device flow; the app never sees the password), then claim
  the PTCGL accounts found in your sealed logs. One tcgkb account can own several PTCGL accounts.
  A claim sends that account's 3 newest sealed logs as proof. Nothing is sent until you connect
  and claim. Contract: `docs/TCGKB-API.md`.

### Changed

- **Disclaimer updated** (version 2): it now explains the optional tcgkb connection, what a claim
  sends and what those logs contain. Installed apps ask for consent again after updating, with a
  notification; until it is accepted the mod is not repaired or updated.
- `docs/LOG-SEAL.md` no longer says that nothing is uploaded.

## [1.1.0] - 2026-10-19

The first public release: a Windows tray app replaces the 1.0 PowerShell installer, and
everything is in English.

### Added

- **Tray app** (`PtcglAutoBattleLog.exe`): status icon in the notification area, a
  "battle log saved" notification for every match (click to open the log), and a menu to open
  the logs, change the logs folder, repair the mod, and more.
- **One-file installer**: the same exe installs itself for the current Windows user, without
  administrator rights. BepInEx 5.4.23.3 and the plugin are embedded in it. Adds "Start with
  Windows", a Start menu entry, a desktop shortcut to the logs and an Apps & Features entry with
  a clean uninstaller that keeps your logs.
- **Portable version** (`PtcglAutoBattleLog-portable.zip`): runs from its folder with a
  `portable.txt` marker (or `--portable`) and keeps its data in `data\`; it only installs the mod
  into the game.
- **Automatic repair**: when a game update removes BepInEx or the plugin, the tray app puts them
  back as soon as the game is closed, and tells you when the game must be restarted.
- **Automatic updates** from GitHub Releases, installed only when the version is newer, the file
  comes from this repository's release downloads on github.com, and its size and SHA-256 match
  `update.json`. Can be turned off (notify only, skip a version).
- **Sealed logs**: every log carries a signed, chained metadata block (footer, `.seal` sidecar,
  or off) so a future leaderboard can detect edited logs; `tools/verify_log.py` checks them. See
  `docs/LOG-SEAL.md`.
- **Disclaimer and consent**: the app shows `DISCLAIMER.md` and asks for consent before the first
  install or run, and again if the disclaimer changes.
- `plugin-status.json`: the plugin's heartbeat (state, last saved log), used by the tray app.
- **Licenses**: the tray menu shows the licenses of the app and of BepInEx and the other bundled
  components, and the portable zip contains them too.
- Builds and releases by GitHub Actions from tags, with `update.json` and `SHA256SUMS.txt`.

### Changed

- English everywhere: file names (`VICTORY`, `DEFEAT`, `NO-RESULT`, `EXPORTED`), the `index.csv`
  header, messages and documentation. The 1.0 `index.csv` header and labels are converted on the
  first start.
- The 1.0 PowerShell installer, uninstaller and watchdog are replaced by the tray app, which
  migrates a 1.0 installation automatically (the watchdog is stopped and its files removed).
- `estado.txt` in the logs folder is replaced by `plugin-status.json` in
  `%LOCALAPPDATA%\PtcglAutoBattleLog`.
- An empty `OutputFolder` in the plugin's BepInEx config now means the default folder
  (`Documents\PTCGL Battle Logs`), so profile paths that BepInEx cannot store (for example with an
  apostrophe) work. A folder that 1.0's config left garbled falls back to the default folder.

### Fixed

- A result or opponent that becomes known after the first save (late winner, reconnect) now
  renames the file and updates its `index.csv` row; a reconnect to the same match continues the
  same file.
- File names and `index.csv` dates no longer depend on the Windows culture (e.g. Thai or Arabic
  calendars).
- Log, index and status files are written atomically.

## 1.0.0 - not released

First version, never published: the BepInEx plugin, a Spanish PowerShell installer
(`Instalar.bat`, `Desinstalar.bat`) with a hidden watchdog that restored the mod after game
updates, and Spanish file names and documentation.

[Unreleased]: https://github.com/manucruzleiva/ptcgl-auto-battlelog/compare/v1.2.0...HEAD
[1.2.0]: https://github.com/manucruzleiva/ptcgl-auto-battlelog/compare/v1.1.0...v1.2.0
[1.1.0]: https://github.com/manucruzleiva/ptcgl-auto-battlelog/releases/tag/v1.1.0
