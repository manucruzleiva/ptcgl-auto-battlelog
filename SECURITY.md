# Security

## Reporting a vulnerability

Please **do not open a public issue** for a security problem. Report it privately through GitHub:
**Security** tab → **Report a vulnerability**
(<https://github.com/manucruzleiva/ptcgl-auto-battlelog/security/advisories/new>).

Include what you found, how to reproduce it, the version (tray menu, first line), and whether it
is the installed or the portable app. This is a volunteer project: we aim to acknowledge reports
within a week and will credit you in the advisory unless you prefer otherwise. Only the latest
release receives fixes.

In scope, for example: anything that lets someone other than this repository's release workflow
get code run through the updater; file handling of the installer, repair or uninstaller (for
example writing outside the intended folders); the plugin; and `tools/verify_log.py` accepting a
seal that does not verify. Out of scope: a player signing a made-up log with their own key (a
documented limitation, see [docs/LOG-SEAL.md](docs/LOG-SEAL.md#threat-model)), and problems in
BepInEx or the game itself (report those to their authors).

## What the software does on your computer

- **Plugin** (inside Pokémon TCG Live): reads the battle log through the game's own exporter at the
  end of a match, and during a match the details the client already holds (match id, both
  players' names and account ids, your deck and its cards, seats, winner) to name, index and seal the log; it
  writes files to your logs folder and to `%LOCALAPPDATA%\PtcglAutoBattleLog\`. Account ids are
  stored only as hashes. It makes **no network connections**.
- **Tray app**: runs as your Windows user, never asks for administrator rights, and writes only to
  your user profile (`%LOCALAPPDATA%\PtcglAutoBattleLog\`, `HKCU` registry keys for "Start with
  Windows" and Apps & Features, Start menu and desktop shortcuts) and to the game folder (BepInEx
  and the plugin). See [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) for the complete list.
- **Network**: the tray app's only connections are HTTPS requests to `github.com` for this
  repository's releases (`releases/latest/download/update.json`, then the exe it names, following
  GitHub's redirect to its download servers; on the beta update channel, which the player has to
  choose, first `https://api.github.com/repos/{owner}/{repo}/releases` to find the newest
  pre-release), with the `User-Agent` `PtcglAutoBattleLog/<version>`.
  No telemetry and nothing uploaded, unless the player connects a tcgkb account: then HTTPS
  requests to `https://tcgkb.app` only (no redirects), with a bearer token kept encrypted with DPAPI
  for the Windows user, and battle logs (with the player's decklist) sent only for the PTCGL accounts the player claims
  ([docs/TCGKB-API.md](docs/TCGKB-API.md)). If the player sets a Pokematch API token, HTTPS requests
  to `https://pokematch-vslogger.onrender.com` only (no redirects), with that token as the bearer
  token, kept encrypted with DPAPI for the Windows user; for each finished match, only the player's
  deck name, the result and the date, sent only while the player has uploads on, or on request ([docs/POKEMATCH-API.md](docs/POKEMATCH-API.md)).
  "Project page" and "What's new" open your browser.
- **Uninstall** removes files that are still in use (the running exe) after the uninstaller exits,
  with a hidden Windows PowerShell whose script is a constant (the folder is passed in an
  environment variable, never inside the script).

## Updates

The tray app installs an update only when **all** of these hold (the rules are in
`src/Tray/Logic/UpdateChecker.cs` and covered by `tests/TrayTests`):

1. `update.json` names a version `MAJOR.MINOR.PATCH` strictly greater than the running one
   (pre-release versions are ignored).
2. The exe's URL is exactly `https://github.com/{owner}/{repo}/releases/download/{tag}/{file}` for
   the owner and repository compiled into the app: HTTPS only, host exactly `github.com`, no port,
   user info, query, fragment, percent-encoding, dot segments or non-ASCII characters. Redirects
   must stay on HTTPS.
3. The downloaded file has exactly the `size` and `sha256` given in `update.json`. Otherwise it is
   deleted and the update is not applied.

The previous exe is kept as `PtcglAutoBattleLog.exe.old` until the new one has started. You can
turn automatic installation off in the tray menu ("Install updates automatically"); you are then
only notified.

These checks make sure an update is a file published in this repository's releases, intact. They
do not protect against a compromise of the repository or of a maintainer's GitHub account, since
`update.json` is published there too. That is why releases are made only by the workflow below.

## How releases are built

- Releases are created only by [`.github/workflows/release.yml`](.github/workflows/release.yml),
  on GitHub-hosted runners, when a `vMAJOR.MINOR.PATCH` tag is pushed. It runs `build.sh` with the
  version from the tag: all tests, the cross-check of the plugin's seals with `tools/verify_log.py`,
  the build, and `tools/make-manifest.py`, which writes `update.json` and `SHA256SUMS.txt` from
  the files it just built. The four files are uploaded by the workflow itself; nothing is built on
  a maintainer's computer.
- BepInEx is not downloaded at install time: the exact official
  `BepInEx_win_x64_5.4.23.3.zip` is embedded in the exe, and the build fails if its SHA-256 is not
  `41a089e5b1b1f0713b331346baf6677b1184c69eabebf51101097954e854c749`.
- The build is deterministic: the same commit, built on Linux with the .NET SDK that `global.json`
  pins (exactly that version: `rollForward` is `disable`, and the workflows install it), gives the
  same exe and zip, byte for byte. The zip's entries are stored, not compressed, so it does not
  depend on the zlib or Python version either; the release notes still name the SDK and Python
  that built them. You can rebuild a release tag with `./build.sh X.Y.Z` (and `REPO_OWNER` /
  `REPO_NAME` set to the repository) and compare with `SHA256SUMS.txt`. `build.ps1` on Windows
  writes the zip with .NET's own zip code, so only its exe is comparable.
- The exe is not code-signed (Windows SmartScreen warns about it). Check downloads against
  `SHA256SUMS.txt` from the same release, e.g. in PowerShell:
  `Get-FileHash .\PtcglAutoBattleLog.exe -Algorithm SHA256`.

## Your signing key

The plugin seals every log with an RSA key created for your Windows account:
`%LOCALAPPDATA%\PtcglAutoBattleLog\identity\signing-key.xml`. The **private key is stored
unencrypted**, protected only by the file permissions of your user profile. Anyone who can read
your profile (another administrator, malware, a backup you share) can copy it and sign logs as
you. Protect your Windows account, don't share or sync the `identity` folder, and if you think
the key leaked, delete the `identity` folder: the next game start creates a new key and a new
chain. The key is kept when you uninstall the app. Details: [docs/LOG-SEAL.md](docs/LOG-SEAL.md).
