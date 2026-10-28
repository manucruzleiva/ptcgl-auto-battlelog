# Changelog

All notable changes to this project are documented in this file. The format is based on
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project uses
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

The section of a version becomes the top of its GitHub release notes, so add one before tagging
(see "Releasing" in the README).

## [Unreleased]

## [1.3.2-beta.3] - 2026-10-28

A beta with your decklist, queue and format on each match. On the Beta channel it installs itself.
Needs tcgkb.app with seal v2 support to upload matches there.

### Added

- **Your decklist with each match** (#22). The plugin reads the cards of your own deck while a
  match loads (the game never has your opponent's) and saves each distinct deck once, as
  `decks\<hash>.deck` in the logs folder: one line of JSON with the game's card ids and counts.
  The log's seal names the deck it was played with, so an edited deck file is detectable.
  Nothing is saved in seal mode **Off**.
- **Queue and format of each match** (#26): `Ranked`, `Casual`, `BuildAndBattle` (or `Friend`
  for a direct match the game names no queue for) and `Standard`, `Expanded`, `BuildAndBattle`,
  as the game names them. They go into two new columns at the end of `index.csv` (an existing
  file gets them in its header), into the seal, and as tags on Pokematch uploads. File names are
  unchanged.
- **Decks on tcgkb.** With **Upload my matches automatically** on, each match is sent with its
  decklist, each distinct deck only once; tcgkb keeps your decks and their versions.

### Changed

- **Seal format v2**: `deckSha256`, `queue` and `format` after `deck`. v1 seals of earlier builds stay valid, your chain
  continues across the update, and `tools/verify_log.py` checks both (and the deck file).
  An older `verify_log.py` reports v2 logs as unsealed: use the new one.
- **Disclaimer updated** (version 4): it now mentions the decklist, the queue and the format. You are asked to accept it again.

### Removed

- The one-time `Deck info (research, issue #15)` line in `BepInEx\LogOutput.log`.

## [1.3.2-beta.2] - 2026-10-28

A second beta for testers of the queue research (#14): play a ranked and a friend match with
`DumpMatchFields = true`. On the Beta channel it installs itself.

### Changed

- **Match fields dump: every value an enum field can take.** An enum field now lists all its
  values, not only the current one (so one match shows every queue type), and the match details,
  lobby and direct-match (friend battle) members are expanded too (#14).

## [1.3.2-beta.1] - 2026-10-28

A beta for testers, to research the queue type (#14) and decklists (#15). Installed apps
(1.3.1 and earlier) never update to it on their own: install it by hand. It updates itself to
1.3.2 once that is released, or to later betas on the Beta channel.

### Added

- **Beta update channel.** **Update channel ▸ Beta** in the tray menu also installs test versions
  (GitHub pre-releases such as `v1.3.2-beta.1`) before they are released. **Stable** stays the
  default and only gets releases; switching back keeps your beta until the next release replaces it.
- **Research aid: dump the match fields.** With `DumpMatchFields = true` under `[Diagnostics]` in
  `BepInEx\config\ptcgl.autobattlelog.cfg` (off by default) the plugin writes the fields of the
  game's match objects to `LogOutput.log` at the start and end of every match, plus the loaded
  enums and types that look queue related, to find out whether the client says which queue a
  match came from (#14). Player names and account ids are hidden.
- **Deck research line in the BepInEx log.** Once per game session the plugin writes what the
  client's `deckInfo` holds, to find out whether the player's decklist can be saved with the log
  (issue #15). Your own deck is written with its values; the opponent's only as member names and
  types. It stays in `BepInEx\LogOutput.log` on your computer and is never sent anywhere.
- **Beta releases.** A tag like `v1.3.2-beta.1` is published as a GitHub pre-release: never the
  latest release, so apps on the Stable channel never get it.

## [1.3.1] - 2026-10-22

### Fixed

- **Pokematch uploads waited silently for the updated disclaimer.** After updating to 1.3.0 the
  new disclaimer is only offered by a balloon, and until it was accepted Pokematch uploads (even
  "Upload past battle logs...") did nothing, without a word. The Pokematch menu now asks for it,
  automatic uploads say once that they are waiting (balloon and app log) and start as soon as it
  is accepted, the menu shows "Waiting: accept the updated disclaimer", and a manual upload always
  writes its result to the app log.

## [1.3.0] - 2026-10-21

### Added

- **Upload my matches automatically** (tray menu, tcgkb account, off until you turn it on): every
  tracked match of the PTCGL accounts you claimed is sent to tcgkb.app, older ones included, so
  tcgkb can show your winrate, decks and the global meta. Each log is sent once; when tcgkb cannot
  be reached the app tries again later. **Upload matches now** sends what is pending at once.
- **Pokematch** (tray menu, optional, off by default): set your Pokematch API token and the app
  adds each finished match (VICTORY or DEFEAT) to your Pokematch TCG logs about two minutes after
  it is saved: your deck, the result and the date (opponent deck "Unknown"; no names, no log
  text). "Upload past battle logs..." sends older ones. Never sent twice: a corrected result
  updates the log on Pokematch, and a match deleted there is not sent again. The token is kept
  encrypted with DPAPI. Contract: `docs/POKEMATCH-API.md`.

### Changed

- **Disclaimer updated** (version 3): it now explains the optional automatic tcgkb uploads and the
  optional Pokematch uploads, and what they send. Installed apps ask for consent again after
  updating, with a notification; until it is accepted the mod is not repaired or updated.

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

[Unreleased]: https://github.com/manucruzleiva/ptcgl-auto-battlelog/compare/v1.3.2-beta.1...HEAD
[1.3.2-beta.1]: https://github.com/manucruzleiva/ptcgl-auto-battlelog/compare/v1.3.1...v1.3.2-beta.1
[1.3.1]: https://github.com/manucruzleiva/ptcgl-auto-battlelog/compare/v1.3.0...v1.3.1
[1.3.0]: https://github.com/manucruzleiva/ptcgl-auto-battlelog/compare/v1.2.0...v1.3.0
[1.2.0]: https://github.com/manucruzleiva/ptcgl-auto-battlelog/compare/v1.1.0...v1.2.0
[1.1.0]: https://github.com/manucruzleiva/ptcgl-auto-battlelog/releases/tag/v1.1.0
