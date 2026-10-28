<p align="center">
  <img src="docs/icon.png" width="96" height="96" alt="PTCGL Auto Battle Log icon">
</p>

<h1 align="center">PTCGL Auto Battle Log</h1>

<p align="center">
  Saves the battle log of every Pokémon TCG Live match to a folder on your PC, automatically. No clicks.
</p>

<p align="center">
  <a href="https://github.com/manucruzleiva/ptcgl-auto-battlelog/releases/latest"><img alt="Latest release" src="https://img.shields.io/github/v/release/manucruzleiva/ptcgl-auto-battlelog?sort=semver"></a>
  <a href="https://github.com/manucruzleiva/ptcgl-auto-battlelog/actions/workflows/ci.yml"><img alt="CI" src="https://github.com/manucruzleiva/ptcgl-auto-battlelog/actions/workflows/ci.yml/badge.svg"></a>
  <a href="LICENSE"><img alt="License: MIT" src="https://img.shields.io/badge/license-MIT-blue.svg"></a>
  <img alt="Platform: Windows" src="https://img.shields.io/badge/platform-Windows%2010%20%7C%2011-lightgrey.svg">
</p>

> [!WARNING]
> **Unofficial mod. Use it at your own risk.** Installing it is each player's own decision.
> Modifying the game client may violate the game's Terms of Service, and the authors are **not
> responsible** for warnings, suspensions or bans of your account, or for any other consequence.
> It only automates obtaining data the game already gives you (the text of the Battle Log's
> **Export** button). It does not take part in the battle: during a match it reads the match
> details the client already holds (match id, both players' names and account ids, your deck and
> its cards, the seats and the winner) to name, index and seal the log; account ids are stored only
> as hashes.
> Please read the full **[disclaimer](DISCLAIMER.md)** before installing.

## Contents

- [Features](#features)
- [Download and install](#download-and-install)
- [Portable version](#portable-version)
- [Where your logs are](#where-your-logs-are)
- [The tray menu](#the-tray-menu)
- [Automatic updates](#automatic-updates)
- [Sealed logs and leaderboards](#sealed-logs-and-leaderboards)
- [tcgkb account](#tcgkb-account)
- [Uninstall](#uninstall)
- [FAQ](#faq)
- [Troubleshooting](#troubleshooting)
- [How it works](#how-it-works)
- [Building from source](#building-from-source) · [Releasing](#releasing) · [Forking](#forking)
- [Credits](#credits) · [License](#license)

## Features

- **Every match saved, automatically.** When a match ends, the exact text that the in-game
  Export button would give you is written to `Documents\PTCGL Battle Logs`, one `.txt` file per
  match, named by date, result and opponent: `2026-10-19 21.34.05 - VICTORY vs Gary.txt`.
- **A spreadsheet of your matches.** `index.csv` gets one row per match (date, result, opponent,
  your name, deck, duration, match id, file, queue and format), ready for Excel or Google Sheets.
- **Your decklist with each match.** The cards of the deck you played are saved with the log, once
  per distinct deck in a `decks` subfolder (only yours: the game never has your opponent's).
- **Your own Export clicks too.** Pressing Export yourself still works and is saved as well.
- **One small download, no admin rights.** A single `.exe` installs everything for your Windows
  user in a few seconds. A portable version is available too.
- **Repairs itself.** Game updates can remove the mod; the tray app puts it back as soon as the
  game is closed.
- **Updates itself.** New versions are downloaded from this project's GitHub releases and checked
  before they are installed. You can turn this off.
- **Notifications.** "VICTORY vs Gary — battle log saved", click to open it.
- **Sealed logs.** Each log carries a signed seal, so edits made after saving are detectable
  (for a future online leaderboard). See [Sealed logs](#sealed-logs-and-leaderboards).
- **Private.** Nothing is uploaded anywhere unless you connect a [tcgkb](https://tcgkb.app)
  account and claim your PTCGL accounts (see [tcgkb account](#tcgkb-account)), or turn on
  uploads to Pokematch (see [Pokematch](#pokematch)). Otherwise the only network traffic is the
  update check against GitHub. Free and open source (MIT).

## Download and install

Requirements: Windows 10 (version 1903 or later) or Windows 11, 64-bit, with Pokémon TCG Live
installed. Nothing else: the app uses .NET Framework 4.8, which those Windows versions include.

| Download | For |
|---|---|
| [`PtcglAutoBattleLog.exe`](https://github.com/manucruzleiva/ptcgl-auto-battlelog/releases/latest/download/PtcglAutoBattleLog.exe) | **Most players.** Installs for your Windows user, starts with Windows, appears in Apps & Features. |
| [`PtcglAutoBattleLog-portable.zip`](https://github.com/manucruzleiva/ptcgl-auto-battlelog/releases/latest/download/PtcglAutoBattleLog-portable.zip) | Players who don't want an installation. See [Portable version](#portable-version). |

Both are on the [latest release](https://github.com/manucruzleiva/ptcgl-auto-battlelog/releases/latest)
page, together with `SHA256SUMS.txt` if you want to check the files.

1. **Run `PtcglAutoBattleLog.exe`** (double-click it; not with "Run as administrator", which the
   installer refuses: it installs for your Windows user only). The exe is not code-signed, so
   Windows SmartScreen may say "Windows protected your PC". Click **More info**, then **Run
   anyway**. (Every release is built from this repository's source by GitHub Actions; see
   [SECURITY.md](SECURITY.md).)
2. **Read the disclaimer** that the app shows and choose **I understand, install**, or **Cancel**
   to leave without changing anything.
3. The installer then, **without administrator rights** (no UAC prompt):
   - copies itself to `%LOCALAPPDATA%\PtcglAutoBattleLog\`;
   - finds the game (it asks for the folder if it cannot; the folder is the one that contains
     `Pokemon TCG Live.exe`);
   - installs [BepInEx 5](https://github.com/BepInEx/BepInEx) and the plugin into the game folder
     (right away if the game is closed, otherwise as soon as you close it). Existing BepInEx files
     and other mods are never overwritten;
   - creates the logs folder, a Start menu entry, a desktop shortcut to your logs, the "Start with
     Windows" entry and an Apps & Features entry;
   - starts the tray app (look for the icon in the notification area, next to the clock).
4. **Start Pokémon TCG Live and play.** At the end of each match a notification tells you the log
   was saved.

Upgrading from the 1.0 PowerShell version? Just run the new exe: it stops and removes the old
watchdog and its files, reuses its game folder, and keeps your logs. The `index.csv` header and
its Spanish result labels are converted to English; existing file names are left as they are.

## Portable version

Same app, no installation. Extract `PtcglAutoBattleLog-portable.zip` anywhere (a folder you
sync, a USB stick) and run `PtcglAutoBattleLog\PtcglAutoBattleLog.exe`. The file `portable.txt`
next to the exe is what makes it portable; if you delete it, the next start installs the app
normally. (Starting the exe with `--portable` has the same effect as the file.) The zip also
holds `LICENSE.txt`, `THIRD-PARTY-NOTICES.txt` and `licenses\LGPL-2.1.txt`, the licenses of the
app and of the BepInEx files it carries.

> [!TIP]
> Windows may show a security warning the first time the extracted exe starts (files from a
> downloaded zip carry a "Mark of the Web"). Once you have accepted the disclaimer, the app removes
> that mark from its own exe, like the installer does, so later starts and "Start with Windows" do
> not warn. To avoid even the first warning: right-click the zip → **Properties** → tick
> **Unblock** → **OK** before extracting.

**Kept in the portable folder:** `data\settings.json`, `data\tray.log` and `data\updates\`. If the
folder is read-only, they go to `%LOCALAPPDATA%\PtcglAutoBattleLog\portable-data\` instead and the
tray menu says so.

**Not done in portable mode:** no copy of the exe in `%LOCALAPPDATA%`, no Start menu or desktop
shortcuts, no Apps & Features entry. "Start with Windows" is off unless you turn it on in the
menu. Updates replace the portable exe in place.

**Unavoidable, even in portable mode,** because the mod has to run inside the game:

- BepInEx (`winhttp.dll`, `doorstop_config.ini`, `.doorstop_version`, `changelog.txt`, `BepInEx\`)
  and the plugin (`BepInEx\plugins\PtcglAutoBattleLog\`) in the game folder;
- `%LOCALAPPDATA%\PtcglAutoBattleLog\plugin-status.json` (the plugin's heartbeat, which the tray
  reads) and `%LOCALAPPDATA%\PtcglAutoBattleLog\identity\` (the key that seals your logs);
- your logs folder.

The installed and the portable app never run at the same time: starting one while the other is
running just shows the running one. (Running a downloaded `PtcglAutoBattleLog.exe` to install is
the exception: it closes whichever copy is running, then installs. If a newer version is already
installed, an older download installs nothing and starts the installed one instead.)

## Where your logs are

By default in **`Documents\PTCGL Battle Logs`** (wherever Windows keeps your Documents folder,
which may be inside OneDrive). Use **Settings ▸ Change logs folder...** in the tray menu to pick another one.

- One file per match: `yyyy-MM-dd HH.mm.ss - RESULT vs OPPONENT.txt`, in your local time of the
  match end. `RESULT` is `VICTORY`, `DEFEAT`, `NO-RESULT` (no winner known, e.g. a disconnection)
  or `EXPORTED` (you pressed Export outside a tracked match). A second file with the same name
  gets ` (2)`.
- The content is exactly the text the game's Export button produces (UTF-8). If the result
  becomes known only later (for example after a reconnect), the file is updated and renamed, so a
  `NO-RESULT` file can become `VICTORY` or `DEFEAT`.
- `decks\` holds the decks you played, one file per distinct deck, named by its hash: one line of
  JSON with the game's card ids and counts, e.g. `{"v":1,...,"size":60,"cards":{"sv8_174":2,...}}`.
  Each log's seal names the deck it was played with, so matches with the same deck share one file
  and an edited deck file is detectable. Without a seal (**Off**) no deck is saved.
- `index.csv` (UTF-8, opens directly in Excel) has the columns
  `Date, Result, Opponent, Player, Deck, Duration (min), MatchId, File, Queue, Format`. Queue and
  Format are the game's own names: `Ranked`, `Casual` or `BuildAndBattle` (`Friend` for a direct
  match the game names no queue for), and `Standard`, `Expanded` or `BuildAndBattle`; empty when
  unknown. An `index.csv` from an earlier version gets the two columns in its header; its old rows
  keep them empty.
- **The seal footer.** By default each `.txt` ends with a block like this, after the log:

  ```
  -----BEGIN PTCGL-ABL SEAL v2-----
  {"v":2,"app":"PTCGL Auto Battle Log",...}
  kq3Vx...==
  -----END PTCGL-ABL SEAL v2-----
  ```

  Everything before that block is the original log. If another tool chokes on it, remove those
  lines from a *copy*, or switch the tray menu's **Settings ▸ Battle log seal** to **Sidecar** (the seal then
  goes to a `.seal` file next to the `.txt`) or **Off**. Editing a sealed file breaks its seal;
  that is the point of it.

## The tray menu

Right-click the icon in the notification area (it may be hidden behind the `^` arrow).
Double-click it to open your logs folder.

Only what you use often is at the top; everything else is grouped in **Settings ▸**, **Updates ▸**
and **Help ▸**. **Status and settings...** opens a window with the same status and options in tabs
(General, Updates, Accounts, Help); starting the app again from the Start menu while it runs opens
it too.

| Item | What it does |
|---|---|
| Status lines | Version (and "portable"), what the app is doing, the last saved log. |
| Review the updated disclaimer... | In red, under the status lines, only when the disclaimer changed since you accepted it. Uploads to tcgkb and Pokematch wait until you accept it. |
| Open logs folder · Open last log | |
| Update vX available ▸ | Install now, What's new, Skip this version (only when an update is waiting). |
| tcgkb account ▸ | Optional. Connect to tcgkb, claim or release your PTCGL accounts, disconnect. See [tcgkb account](#tcgkb-account). |
| Pokematch ▸ | Optional. Set your Pokematch API token, upload new battle logs automatically, upload past ones, remove the token. See [Pokematch](#pokematch). |
| Status and settings... | The same status lines and options as the menu, in a window with tabs. |
| Settings ▸ Notify when a log is saved | Turns the "battle log saved" notifications on or off. |
| Settings ▸ Start with Windows | Starts the tray app when you sign in (recommended: it is what repairs the mod after game updates). |
| Settings ▸ Change logs folder... | Shows the current logs folder. New logs go there (from the next game start if the game is running). Folders whose path contains `'` or `"`, and network paths (`\\server\...`), are not supported by BepInEx's settings file. Folders inside the game's `BepInEx` folder or the app's own folders are refused, because uninstalling removes those. |
| Settings ▸ Use the default logs folder | Back to `Documents\PTCGL Battle Logs` (only shown when you changed it). |
| Settings ▸ Battle log seal ▸ | Footer (default), Sidecar or Off. Takes effect the next time the game starts. |
| Updates ▸ | Install updates automatically, Update channel ▸, Check for updates now. See [Automatic updates](#automatic-updates). |
| Help ▸ Diagnostics ▸ | Open the app log, the BepInEx log, the game folder or the app data folder; choose the game folder; copy diagnostic info for a bug report. |
| Help ▸ Repair now | Puts back any missing BepInEx or plugin file (with the game closed). |
| Help ▸ Project page · View disclaimer · Licenses | Licenses shows this app's license and those of BepInEx and the other files it carries. |
| Help ▸ Uninstall... (portable: Remove mod from game...) | See [Uninstall](#uninstall). |
| Exit | Closes the tray app. Logs are still saved while it is closed (the plugin runs inside the game), but nothing is repaired or updated. |

The icon shows the state at a glance: **green dot** = the game is running with the mod; **no
dot** = idle, all good; **amber** = needs attention (repair pending, the game is running without
the mod, or the game was not found); **red** = error (the mod reported a problem, or a repair
failed). Hover over it or open the menu for details.

## Automatic updates

The tray app checks one minute after it starts and then every 6 hours (or when you click **Check
for updates now**) by reading
`https://github.com/manucruzleiva/ptcgl-auto-battlelog/releases/latest/download/update.json`.

An update is installed only if **all** of these hold:

- its version is strictly newer than yours (pre-release versions are ignored, unless you chose the
  beta channel, below);
- the download address is exactly a release download of this repository on `https://github.com`;
- the downloaded file has exactly the size and the SHA-256 hash that `update.json` states.

Anything else is discarded and written to the app log. The new exe replaces the old one and
restarts; the plugin inside the game is updated the next time the game is closed.

**Beta channel.** **Updates ▸ Update channel ▸ Beta** also installs test versions (GitHub pre-releases such
as `v1.3.2-beta.1`) before they become a release: the app then lists the repository's newest
releases through `https://api.github.com/repos/manucruzleiva/ptcgl-auto-battlelog/releases` and
takes the highest version, beta or not. The same download checks apply. Betas may have bugs. **Stable**
(the default) gets releases only; switching back to it keeps the beta you have until the next
release, which replaces it.

**To turn it off**, untick **Updates ▸ Install updates automatically**. You then get one notification per
new version and can install it from the menu (**Update vX available ▸ Install now**), read
**What's new**, or **Skip this version**.

These checks protect against corrupted downloads and against files from anywhere other than this
repository's releases. They cannot protect against a compromise of the repository itself; see
[SECURITY.md](SECURITY.md) for how releases are made.

## Sealed logs and leaderboards

Every saved log carries a **seal**: one line of metadata about the match (match id, result, both
players' names, deck, times, a hash of the log text and of the `.deck` file) signed with an RSA key that belongs to your
Windows account, and chained to your previous seal (`seq`, `prev`). The goal is a future online
leaderboard that can tell edited or made-up logs from real ones.

Anyone can check a log with the reference verifier (Python 3.8+, no packages needed). In
PowerShell, from a copy of this repository, with the folder that **Open logs folder** in the tray
menu shows (it may be inside OneDrive, or a folder you chose):

```
py tools\verify_log.py "2026-10-19 21.34.05 - VICTORY vs Gary.txt"
py tools\verify_log.py --chain "$env:USERPROFILE\Documents\PTCGL Battle Logs"
```

(In `cmd.exe` write the folder as `"%USERPROFILE%\Documents\PTCGL Battle Logs"`; on Linux or macOS
run `python3 tools/verify_log.py`.) Each `OK` line shows the `key` that made the seal. To check a
log someone shared with you, add `--key <their keyId>`: a log that was edited and sealed again
with another key is then reported as invalid.

**What a seal can and cannot do.** The key lives on your own PC, and the plugin is open source,
so a seal **cannot prove that a match took place**: someone determined to cheat can sign
anything with their own key. What it does prove is that the file has not changed since it was
sealed **by that key**: anyone can edit a log and seal it again with a key of their own, so
compare the seal's full `keyId` with the player's known one (`--key`). It also gives each install
an ordered, pseudonymous history in which deleted or rewritten results leave gaps or forks. A leaderboard has to add server-side checks on top, above all
matching the seals of *both* players of a match. The full format, threat model and server-side
guidance are in [docs/LOG-SEAL.md](docs/LOG-SEAL.md).

**Privacy.** Nothing is uploaded unless you claim an account on tcgkb or turn on Pokematch uploads (below): the seal is just text in your files. Players are identified in
the seal by an `accountHash` (SHA-256 of `ptcgl-abl:v1:` + the game's account id), a pseudonym that
lets the two seals of one match name the same accounts without writing the raw ids. It is not
encryption: anyone who knows an account id can compute its hash. Your logs (sealed or not)
contain your opponents' names, as the Export text does; keep that in mind before sharing them.

**Your key** is `%LOCALAPPDATA%\PtcglAutoBattleLog\identity\signing-key.xml`. Anyone with a copy can
sign as you, so do not share it. It is kept when you uninstall, so a reinstall continues the same
chain. Set **Battle log seal ▸ Off** if you do not want seals.

## tcgkb account

Optional. [tcgkb.app](https://tcgkb.app) is a separate site by the same author, for decks and,
later, leaderboards built from sealed logs. If you have an account there, **tcgkb account ▸ Connect
to tcgkb...** links this computer to it:

1. Your browser opens tcgkb.app and the app shows a short code. Sign in there as usual (Google or
   password), check that the code matches and approve. The app never sees your password.
2. **My PTCGL accounts...** lists the PTCGL accounts that played on this computer (found in your
   sealed logs) and the ones your tcgkb account already owns. **Claim** the ones that are yours: you
   can own several, and each keeps its own stats.
3. A claim sends tcgkb that account's 3 newest battle logs, sealed, as proof. They contain your
   opponents' display names, as the Export text does. A claim is "not verified" until an opponent
   who also uses tcgkb uploads the same match; then it is "verified".
4. **Upload my matches automatically** (off until you turn it on) sends each tracked match of
   your claimed accounts the same way, with your decklist (each distinct deck is sent once), so
   tcgkb can keep your decks and statistics.

Nothing is sent until you connect and claim. **Disconnect** ends the connection (you can also
remove the computer from your tcgkb profile). The connection is kept encrypted for your Windows
user in `%LOCALAPPDATA%\PtcglAutoBattleLog\tcgkb-connection.dat`. The details are in
[docs/TCGKB-API.md](docs/TCGKB-API.md).

## Pokematch

Optional, and independent of tcgkb. If you keep your match history on Pokematch, the app can
upload your battle logs there for you:

1. On Pokematch, make an API token (**Profile Settings → Account → API Tokens**, for example named
   "PTCGL uploader").
2. Tray menu → **Pokematch ▸ Set API token...**, paste it and save. The app checks it with
   Pokematch, then asks whether to upload new battle logs automatically.
3. From then on, each finished match (VICTORY or DEFEAT) is sent about two minutes after it is
   saved (the plugin may still improve the log right after the match). If Pokematch is down or
   asleep, it is tried again later. **Upload past battle logs...** sends the older ones once.

What is sent for each match: a Pokematch TCG log with your deck name, the result (one Bo1 game)
and the date, tagged `ptcgl-auto-battle-log`. The game does not tell you your opponent's
archetype, so the opponent deck shows as "Unknown": edit it on Pokematch if you like. Your
opponent's name and the battle log text stay on your computer, and NO-RESULT and EXPORTED logs
are not sent. A match is never sent twice: if its result is corrected after it was sent, the log
on Pokematch is updated, and a match you delete on Pokematch is not sent again. What was sent is listed in
`%LOCALAPPDATA%\PtcglAutoBattleLog\pokematch-uploads.json`.

The token is kept encrypted for your Windows user in
`%LOCALAPPDATA%\PtcglAutoBattleLog\pokematch-token.dat` and sent only to Pokematch. Changing your
Pokematch password revokes its tokens: the app tells you, and you set a new one. Turn **Upload new
battle logs automatically** off, or **Remove API token**, at any time. Details:
[docs/POKEMATCH-API.md](docs/POKEMATCH-API.md).

## Uninstall

**Installed app:** *Settings → Apps → Installed apps → PTCGL Auto Battle Log → Uninstall*, or
**Help ▸ Uninstall...** in the tray menu. Close the game first (the uninstaller waits for it; a silent
uninstall, `--uninstall --quiet`, stops with exit code 1 and changes nothing while the game runs).
This removes:

- the plugin and its settings from the game folder, and BepInEx too if this app installed it and
  no other mod uses it;
- the app, its Start menu and desktop shortcuts, "Start with Windows" and the Apps & Features
  entry, and `%LOCALAPPDATA%\PtcglAutoBattleLog\` except its `identity\` folder.

**Portable app:** tray menu → **Help ▸ Remove mod from game...** (same removal from the game folder, and
"Start with Windows" off), then delete the portable folder yourself.

**Always kept:** your battle logs, and `%LOCALAPPDATA%\PtcglAutoBattleLog\identity\` (your seal key).
Delete that folder by hand for a complete cleanup; a later install then starts a new key.

If some of the mod's files cannot be removed from the game (still in use, or locked by an
antivirus), the uninstall stops there and tells you which: the app stays installed, so close
whatever holds them and uninstall again.

## FAQ

**Can I get banned for using this?**
Nobody can promise that you won't. Please read the [disclaimer](DISCLAIMER.md); in short:

- Modifying the game client may violate the game's Terms of Service. The Pokémon Company can
  change its rules or its detection at any time.
- Using it is entirely your decision and at your own risk. The authors are not responsible for
  any consequence, including warnings, suspensions or bans.
- It only automates obtaining data the game already gives you: the Battle Log's Export text.
- It does not take part in the battle. During a match it reads match details the client already
  holds (the match id, both players' display names and account ids, your deck name and decklist,
  the seats and the winner) to name, index and seal the log; account ids are stored only as hashes. It shows you
  nothing, automates no game action, and does not change cards, decks, matches or results.
- It sends nothing to The Pokémon Company. Its only network traffic is the update check against
  this project's GitHub releases and, only if you choose them, requests to tcgkb.app and to
  Pokematch.

**My antivirus complains / what is `winhttp.dll`?**
BepInEx, the standard mod loader for Unity games, is started by a small `winhttp.dll` (Unity
Doorstop) placed in the game folder; Windows loads it when the game starts. Some antivirus
products flag DLLs like this, unsigned executables, or apps that install other files, as
suspicious. You can check the downloaded files against `SHA256SUMS.txt`, read the source, or
build it yourself. If you trust it, report the false positive to your antivirus vendor. If the
antivirus removed `winhttp.dll`, use **Help ▸ Repair now** after allowing it.

**Does it work on Mac, iPhone/iPad, Android or a Chromebook?**
No. Windows only (Windows 10/11, 64-bit).

**The game updated and logs stopped being saved.**
Game updates can delete the mod's files. With the tray app running, the mod is put back
automatically as soon as the game is closed. If the game was already running you get a
"Restart Pokémon TCG Live" notification: let the game finish updating, close it and start it
again. If the icon turns red after a game update, the game's internals may have changed; check
for an update of this app.

**I already use other BepInEx mods.**
That's fine: an existing BepInEx is used as it is (never overwritten), and uninstalling leaves
BepInEx in place when other mods are installed.

**Does the tray app have to run for logs to be saved?**
No, the plugin runs inside the game. The tray app adds notifications, repairs after game
updates and updates.

## Troubleshooting

Start with **Help ▸ Diagnostics ▸ Copy diagnostic info** in the tray menu and paste it into a
[bug report](https://github.com/manucruzleiva/ptcgl-auto-battlelog/issues/new/choose). The files
worth looking at:

| File | What it tells you |
|---|---|
| `%LOCALAPPDATA%\PtcglAutoBattleLog\tray.log` (portable: `data\tray.log`) | What the tray app did: install, repairs, updates, errors. |
| `<game folder>\BepInEx\LogOutput.log` | The game-side log, rewritten at every game start. The plugin's lines are tagged `PTCGL Auto Battle Log`, e.g. `[Info   :PTCGL Auto Battle Log]`. If this file is not updated when you start the game, BepInEx is not loading: use **Help ▸ Repair now** with the game closed and check your antivirus. |
| `%LOCALAPPDATA%\PtcglAutoBattleLog\plugin-status.json` | The plugin's heartbeat while the game runs (refreshed at least every 30 seconds): `state` (`starting`, `ok` or `error`), `message`, the logs folder and the last saved file. |

Common cases:

- **"Battle logs are not being saved"** notification: the game is running without the mod. Close
  the game; the tray app repairs it; start the game again.
- **Amber icon, "game not found"**: use **Choose the game folder...** and select the folder that
  contains `Pokemon TCG Live.exe` (by default `%USERPROFILE%\The Pokémon Company International\Pokémon Trading Card Game Live`).
- **No notification but a file was saved**: check **Notify when a log is saved** and Windows'
  notification settings (Focus assist / Do not disturb).

## How it works

Pokémon TCG Live is a Unity game. The plugin, loaded by BepInEx 5 inside the game, watches for
the end of a match and then calls the game's own battle-log exporter, the code behind the Export
button, and writes its text to disk (reading it again a few seconds later and keeping the longest
version, in case the last lines arrive late). Everything is found by reflection, so the plugin
does not ship any game code and a game update does not require a rebuild unless the client's
classes change. While the plugin calls the exporter, a Harmony patch keeps it from overwriting
your clipboard.

The tray app (a .NET Framework 4.8 WinForms exe with the plugin and BepInEx embedded) installs,
repairs and updates the mod. The two never talk directly; they share files on disk. The full
design, file formats and rules are in [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md).

## Building from source

You need the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) version that
[`global.json`](global.json) names (exactly that one, so that a build is reproducible) and Python
3.8+. Windows is not required: the .NET Framework exe is built against reference assemblies, and
the tests run on .NET 8 on any OS.

```
./build.sh                                                      # Linux, macOS, Git Bash
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1  # Windows (or pwsh)
```

The script downloads BepInEx 5.4.23.3 once (into `cache/`, checked against a pinned SHA-256),
runs the plugin core tests (`tests/CoreTests`) and the tray logic and cross-part tests (`tests/TrayTests`),
checks the plugin's sealed sample logs with `tools/verify_log.py` (and that tampered copies are
rejected), builds the exe and writes `dist/`:

```
dist/PtcglAutoBattleLog.exe            the app (installer, tray, updater)
dist/PtcglAutoBattleLog-portable.zip   PtcglAutoBattleLog/PtcglAutoBattleLog.exe + portable.txt + license texts
dist/update.json                       the update manifest
dist/SHA256SUMS.txt                    hashes of the exe and the zip
```

Pass a version as `./build.sh 1.2.0` (default: `Version` in `Directory.Build.props`). Run a single
test suite with `dotnet run --project tests/CoreTests -c Release` (or `tests/TrayTests`), and the
verifier's own tests with `python3 tools/verify_log.py --self-test`. See
[CONTRIBUTING.md](CONTRIBUTING.md).

```
src/Plugin/        BepInEx plugin (net472)
src/Tray/          tray app (net48 WinForms); Logic/ is platform-neutral and unit-tested
tests/             CoreTests (plugin core vs fake game classes), TrayTests (tray logic, and the
                   cross-part checks: what the plugin writes, the tray and the real BepInEx read)
tools/             verify_log.py (seal verifier), make-manifest.py (release manifest)
docs/              ARCHITECTURE.md, LOG-SEAL.md
```

## Releasing

1. Add a `## [X.Y.Z] - YYYY-MM-DD` section to [CHANGELOG.md](CHANGELOG.md) (it becomes the top of
   the release notes) and, optionally, bump `Version` in `Directory.Build.props`. Commit and push.
2. Tag and push the tag:

   ```
   git tag v1.2.0
   git push origin v1.2.0
   ```

Instead of pushing a tag you can run the workflow by hand: **Actions > Release > Run workflow** on
`main`, with the tag (e.g. `v1.3.2`) as input. It requires the version's CHANGELOG section on
`main`, and creates the tag on the commit it built when it publishes the release.

The [Release workflow](.github/workflows/release.yml) checks that the tag is `vMAJOR.MINOR.PATCH`,
builds with that version (all tests included), and publishes a GitHub release marked **latest**
with the four files from `dist/`. Installed apps pick it up within 6 hours. Releases are never
drafts, because the apps read `releases/latest/download/update.json`.

A tag with a pre-release suffix (`v1.3.2-beta.1`, with a `## [1.3.2-beta.1]` changelog section)
publishes a **beta**: a GitHub pre-release, never the latest, so installed apps never see it and
testers install it by hand. Its `X.Y.Z` must be higher than every published release; once the
final `vX.Y.Z` is out, a beta install updates to it like any other. If you create the release on
the GitHub website instead of pushing the tag, tick **Set as a pre-release**.

Versions must always increase: a tag lower than the latest
published release fails, and re-running an older tag's workflow replaces its files without making
it the latest again. Tag runs are serialized. A published version should never be re-tagged with
different content. The release notes name the commit, .NET SDK and Python that built the files.

## Forking

The exe trusts updates only from `github.com/{RepoOwner}/{RepoName}`. Releases built by a fork's
own workflows automatically use the fork's owner and name, so a fork's builds update from the
fork's releases. For local builds, change `RepoOwner` and `RepoName` in `Directory.Build.props`,
or set the `REPO_OWNER` and `REPO_NAME` environment variables for `build.sh` / `build.ps1`. Please
also change the product name if you publish a fork that behaves differently.

## Credits

- Created and maintained by [@manucruzleiva](https://github.com/manucruzleiva).
- [BepInEx](https://github.com/BepInEx/BepInEx) (MIT), the mod loader, with
  [HarmonyX](https://github.com/BepInEx/HarmonyX), [MonoMod](https://github.com/MonoMod/MonoMod),
  [Mono.Cecil](https://github.com/jbevain/cecil) and [Unity Doorstop](https://github.com/NeighTools/UnityDoorstop).
- [PtcglLeaderboard](https://github.com/Jxkobyte/PtcglLeaderboard) by Jxkobyte (MIT), for
  identifying the client's battle-log classes that this plugin uses.

Licenses and notices of the bundled components: [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

## License

[MIT](LICENSE) © 2026 manucruzleiva and PTCGL Auto Battle Log contributors. BepInEx and the other bundled components
keep their own licenses (see [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)).

This is an unofficial fan project. It is not affiliated with, endorsed, sponsored or approved by
Nintendo, The Pokémon Company, The Pokémon Company International, Creatures Inc. or GAME FREAK inc.
Pokémon and Pokémon TCG Live are trademarks of their respective owners.
