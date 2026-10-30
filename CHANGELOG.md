# Changelog

All notable changes to this project are documented in this file. The format is based on
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project uses
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

The section of a version becomes the top of its GitHub release notes, so add one before tagging
(see "Releasing" in the README).

## [Unreleased]

## [1.6.2] - 2026-10-30

### Fixed

- **The status window no longer opens blank and fills in block by block.** It now paints all at
  once, and closing it only hides it, so opening it again is instant. The app log records how long
  the first opening took.

## [1.6.1] - 2026-10-30

### Changed

- **A click on the tray icon opens the status and settings window** (it used to be a double-click
  that opened the logs folder, which is still a button on the window's home page).

### Added

- **Delete local battle logs...** (Settings, in the menu and the window): moves every battle log in
  the logs folder to the Recycle Bin, after asking. Matches already on tcgkb or Pokematch stay there.

### Fixed

- **The status and settings window opens fast again.** It no longer waits for tcgkb before it
  shows: the accounts and ladder are asked for in the background. The Matches page builds its list
  only when you open it, and the lists are no longer rebuilt every minute.

## [1.6.0] - 2026-10-30

Install, sign in, play: signing in to tcgkb is the only step.

### Changed

- **Sign in and you're done.** Right after installing, the app offers to sign in to tcgkb.app.
  Once signed in, **Upload my matches automatically** is on and every PTCGL account that plays on
  this computer is added to your tcgkb account with its first match: no more claiming accounts.
  "Connect to tcgkb..." is now **Sign in to tcgkb...**.
- **Half-finished setups finish themselves.** Signed in but never claimed an account, or never
  answered the upload question: your matches now upload (after you accept the updated disclaimer).
  Installed but never signed in: the app offers it once.
- **Disclaimer version 5**: it now says uploads are on once you sign in, and that the PTCGL
  accounts on this computer are added to your tcgkb account. Everyone is asked to accept it again.
- **Quick-setup guide**: the tcgkb guide drops the claim and upload steps.
- **A new status and settings window.** Dark, with tcgkb's yellow. **Home** shows what the app is
  doing, your tcgkb account, the last seven days and your last matches; **Matches** lists every
  battle log (click one to open it). The **tcgkb** page shows your place on the ladder: the steps to
  get a row and, until you have 5 matches, why you show up as "Not ranked yet". It also explains what
  a **verified** account is (an opponent who uses tcgkb uploaded the same match): it adds the check
  next to your name, it does not decide whether you are ranked.

### Removed

- The **My PTCGL accounts...** window and the "Claim <name> on tcgkb?" notification.

## [1.5.0] - 2026-10-29

Connect PAB to tcgkb before you play: the app now offers to claim your PTCGL account right after
your first match.

### Added

- **Connect to tcgkb before your first match.** Connected with **Upload my matches automatically**
  on, the app offers to claim your PTCGL account right after you play with it ("Claim <name> on
  tcgkb?"; click to open **My PTCGL accounts...**), once per account. Before, you had to come back
  to the menu after playing, because a claim needs a saved match.
- **Quick-setup guides:** the tcgkb guide now connects before the first match, and the pictures
  look like the real thing: a Windows 11 desktop and taskbar, the real SmartScreen steps, the app's
  own windows, real cards and the tcgkb ranking with deck sprites.

## [1.4.0] - 2026-10-28

Everything from the 1.3.2 betas, now for everyone: your decklist, queue and format with each
match, a shorter tray menu and a status and settings window. The disclaimer changed (version 4),
so the app asks you to accept it again; until you do, tcgkb and Pokematch uploads wait.

### Added

- **Your decklist with each match** (#22). The plugin reads the cards of your own deck while a
  match loads (the game never has your opponent's) and saves each distinct deck once, as
  `decks\<hash>.deck` in the logs folder. The log's seal names the deck it was played with.
  Nothing is saved in seal mode **Off**.
- **Queue and format of each match** (#26): `Ranked`, `Casual`, `BuildAndBattle` or `Friend`, and
  `Standard`, `Expanded` or `BuildAndBattle`. They go into two new columns at the end of
  `index.csv`, into the seal, and as tags on Pokematch uploads. File names are unchanged.
- **Decks on tcgkb.** With **Upload my matches automatically** on, each match is sent with its
  decklist, each distinct deck only once.
- **Status and settings window** (#30, #41). **Status and settings...** in the tray menu opens a
  window with the status at the top and the options under General, Updates, Accounts (tcgkb and
  Pokematch) and Help. Starting the app again while it runs opens this window.
- **Mod version in the status window** (#42). The "Status and settings" window shows which version
  of the mod the game has loaded (or last loaded) and says when it is not the app's version, with
  what to do: restart the game, or nothing if the game is closed and will load the new one.
- **"Review the updated disclaimer..." in the tray menu** (#29), in red, while a newer disclaimer
  waits for you.
- **Beta update channel.** **Updates ▸ Update channel ▸ Beta** also installs test versions before
  they are released. **Stable** stays the default.
- **Quick-setup guides** (#34) in `docs/quick-setup/`: install PAB, connect tcgkb and upload to
  Pokematch, with a picture per step, in nine languages.

### Changed

- **A new look for the status and settings window** (#41). A white header with the app icon and the
  status next to a dot in the tray icon's colour, a navigation column instead of tabs, settings in
  cards, switches instead of check boxes and flat buttons (Open logs folder in the accent colour, the
  updated disclaimer in red). It works as before; high contrast mode keeps the system colours.
- **A shorter tray menu** (#30). The top keeps the status, Open logs folder, Open last log, a waiting
  update, tcgkb and Pokematch; everything else moved into **Settings ▸**, **Updates ▸** and **Help ▸**.
- **Seal format v2** with the deck, queue and format. v1 seals stay valid, your chain continues,
  and the new `tools/verify_log.py` checks both (an older one reports v2 logs as unsealed).
- **Disclaimer updated** (version 4): it now mentions the decklist, the queue and the format.
- **tcgkb uploads wait for the updated disclaimer**, as Pokematch uploads already did.

### Fixed

- **Decks not reaching tcgkb after an update** (#39). The mod inside the game is only replaced while
  the game is closed, so after updating the app the game kept running the previous mod (without
  decklists) and the tray said nothing. Now, while a newer mod waits, the status reads "... · restart
  the game to finish the mod update" and a notification asks once to restart Pokémon TCG Live.

## [1.3.2-beta.4] - 2026-10-28

A beta with a shorter tray menu and a new status and settings window. On the Beta channel it
installs itself.

### Added

- **Quick-setup guides in this repository** (#34): `docs/quick-setup/` has three step-by-step guides
  with a picture per step (install PAB; connect tcgkb; upload to Pokematch) in nine languages, and
  `steps.json` for other sites to show them, starting with tcgkb's own guide. The build checks
  that every text and picture is there and that the menu items the guides name still exist.
- **"Review the updated disclaimer..." in the tray menu** (#29). While a newer disclaimer than the
  one you accepted waits (after an update, it was only offered by a balloon that is easy to miss),
  the menu shows this item in red under the status line, the status reads "Action needed: accept
  the updated disclaimer" and the icon shows attention. Help ▸ View disclaimer opens the same window.
- **Status and settings window** (#30). **Status and settings...** in the tray menu opens a window with
  the status at the top and the same options in tabs: General, Updates, Accounts (tcgkb and
  Pokematch) and Help. Starting the app again from the Start menu while it runs now opens this
  window instead of showing a notification.

### Changed

- **A shorter tray menu** (#30). The top keeps the status, Open logs folder, Open last log, a waiting
  update, tcgkb and Pokematch; everything else moved into **Settings ▸** (notifications, Start with
  Windows, logs folder, battle log seal), **Updates ▸** and **Help ▸** (diagnostics, Repair now,
  project page, disclaimer, licenses, uninstall).
- **tcgkb uploads wait for the updated disclaimer**, as Pokematch uploads already did: they now
  carry your decklist, which the current disclaimer covers. The tcgkb menu says "Waiting: accept
  the updated disclaimer", "Upload matches now" asks for it, and waiting matches go once you accept.

## [1.3.2-beta.3] - 2026-10-28

A beta with your decklist, queue and format on each match. On the Beta channel it installs itself.

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

[Unreleased]: https://github.com/manucruzleiva/ptcgl-auto-battlelog/compare/v1.5.0...HEAD
[1.5.0]: https://github.com/manucruzleiva/ptcgl-auto-battlelog/compare/v1.4.0...v1.5.0
[1.4.0]: https://github.com/manucruzleiva/ptcgl-auto-battlelog/compare/v1.3.1...v1.4.0
[1.3.2-beta.1]: https://github.com/manucruzleiva/ptcgl-auto-battlelog/compare/v1.3.1...v1.3.2-beta.1
[1.3.1]: https://github.com/manucruzleiva/ptcgl-auto-battlelog/compare/v1.3.0...v1.3.1
[1.3.0]: https://github.com/manucruzleiva/ptcgl-auto-battlelog/compare/v1.2.0...v1.3.0
[1.2.0]: https://github.com/manucruzleiva/ptcgl-auto-battlelog/compare/v1.1.0...v1.2.0
[1.1.0]: https://github.com/manucruzleiva/ptcgl-auto-battlelog/releases/tag/v1.1.0
