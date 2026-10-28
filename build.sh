#!/usr/bin/env bash
# Tests, builds and packages PTCGL Auto Battle Log into dist/ (docs/ARCHITECTURE.md). CI and the
# release workflow run exactly this; build.ps1 is the same for Windows developers.
#
#   ./build.sh [VERSION]
#
# VERSION      X.Y.Z or X.Y.Z-beta.N (1st argument, else the VERSION env var, else Directory.Build.props).
# REPO_OWNER   GitHub owner/repository the exe takes updates from and update.json points at
# REPO_NAME    (default: Directory.Build.props). Forks set these, or edit Directory.Build.props.
# DOTNET       the .NET 8 SDK's dotnet (default: dotnet on PATH); global.json pins the exact SDK.
# PYTHON       Python 3.8+ (default: the first of python3, python that is 3.8+).
# SOURCE_DATE_EPOCH  timestamp of the files in the portable zip (default: the HEAD commit's time).
#
# Output: dist/PtcglAutoBattleLog.exe, dist/PtcglAutoBattleLog-portable.zip, dist/update.json,
# dist/SHA256SUMS.txt - the four assets of a GitHub release.
set -euo pipefail
cd "$(dirname "$0")"

# The BepInEx release embedded in the exe and installed into the game. Pinned by hash: what players
# get must be exactly what was reviewed. Change both together (and BepInExVersion in
# Directory.Build.props, and build.ps1).
BEPINEX_VER=5.4.23.3
BEPINEX_SHA256=41a089e5b1b1f0713b331346baf6677b1184c69eabebf51101097954e854c749
BEPINEX_ZIP="BepInEx_win_x64_${BEPINEX_VER}.zip"
BEPINEX_URL="https://github.com/BepInEx/BepInEx/releases/download/v${BEPINEX_VER}/${BEPINEX_ZIP}"

EXE_NAME=PtcglAutoBattleLog.exe
PORTABLE_ZIP=PtcglAutoBattleLog-portable.zip

DOTNET="${DOTNET:-dotnet}"
PYTHON="${PYTHON:-}"
export DOTNET_NOLOGO=1
export DOTNET_CLI_TELEMETRY_OPTOUT="${DOTNET_CLI_TELEMETRY_OPTOUT:-1}"

# ------------------------------------------------------------------------------------- helpers

in_group=0
end_group() { if [ "$in_group" = 1 ]; then echo "::endgroup::"; in_group=0; fi; }
step() {
    end_group
    if [ "${GITHUB_ACTIONS:-}" = true ]; then echo "::group::$*"; in_group=1; else printf '\n==> %s\n' "$*"; fi
}
die() {
    end_group
    if [ "${GITHUB_ACTIONS:-}" = true ]; then echo "::error::build.sh: $*"; fi
    echo "build.sh: error: $*" >&2
    exit 1
}
# A value from Directory.Build.props, e.g. prop RepoOwner.
prop() { sed -n "s:.*<$1[^>]*>\([^<]*\)</$1>.*:\1:p" Directory.Build.props | head -n 1; }
sha256() { "${PYRUN[@]}" -c 'import hashlib, sys; print(hashlib.sha256(open(sys.argv[1], "rb").read()).hexdigest())' "$1"; }

case "${1:-}" in
    -h|--help) sed -n '2,15p' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
esac

# ------------------------------------------------------------------------------------- inputs

VERSION="${1:-${VERSION:-$(prop Version)}}"
VERSION="${VERSION#v}"
# UpdateChecker.TryParseVersion: three numbers without leading zeros, optionally followed by a
# pre-release suffix (1.3.2-beta.1). The stable updater ignores pre-releases; they are published
# as GitHub pre-releases for testers, never as the latest release.
[[ "$VERSION" =~ ^(0|[1-9][0-9]{0,8})\.(0|[1-9][0-9]{0,8})\.(0|[1-9][0-9]{0,8})(-[0-9A-Za-z-]+(\.[0-9A-Za-z-]+)*)?$ && ${#VERSION} -le 40 ]] \
    || die "VERSION must be MAJOR.MINOR.PATCH or MAJOR.MINOR.PATCH-PRERELEASE (e.g. 1.2.0, 1.3.2-beta.1), got '$VERSION'"
OWNER="${REPO_OWNER:-$(prop RepoOwner)}"
REPO="${REPO_NAME:-$(prop RepoName)}"
for v in "$OWNER" "$REPO"; do
    # UpdateChecker.IsSafeName: ASCII letters, digits, '.', '_', '-', not starting with '.', at most 100.
    [[ "$v" =~ ^[A-Za-z0-9_-][A-Za-z0-9._-]{0,99}$ ]] || die "invalid GitHub owner/repository name '$v'"
done
[ "$(prop BepInExVersion)" = "$BEPINEX_VER" ] \
    || die "Directory.Build.props has BepInExVersion $(prop BepInExVersion) but build.sh pins $BEPINEX_VER: update BEPINEX_VER and BEPINEX_SHA256 here and in build.ps1"

command -v "$DOTNET" >/dev/null 2>&1 || die "'$DOTNET' not found: install the .NET 8 SDK or set DOTNET"
# global.json pins the exact SDK (rollForward: disable): the same commit then builds the same exe.
SDK_PINNED="$(sed -n 's/.*"version"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' global.json | head -n 1)"
"$DOTNET" --version >/dev/null 2>&1 \
    || die "the .NET SDK $SDK_PINNED that global.json pins is not installed (installed: $("$DOTNET" --list-sdks 2>/dev/null | cut -d' ' -f1 | tr '\n' ' ')): get it from https://dotnet.microsoft.com/download/dotnet/8.0"
# "python" may be the only name (Git Bash on Windows), or a stub that is not Python at all.
if [ -n "$PYTHON" ]; then candidates=("$PYTHON"); else candidates=(python3 python); fi
PYTHON=
for candidate in "${candidates[@]}"; do
    if "$candidate" -c 'import sys; sys.exit(0 if sys.version_info >= (3, 8) else 1)' >/dev/null 2>&1; then
        PYTHON="$candidate"
        break
    fi
done
[ -n "$PYTHON" ] || die "Python 3.8+ not found (tried: ${candidates[*]}): install it or set PYTHON"
# -I: ignore PYTHON* variables and the user's site-packages; -X utf8: file names with any character
# print whatever the locale (the sample logs name opponents with accents, emoji and CJK).
PYRUN=("$PYTHON" -I -X utf8)

echo "PTCGL Auto Battle Log v$VERSION - updates from github.com/$OWNER/$REPO"
echo "dotnet $("$DOTNET" --version), $("$PYTHON" --version 2>&1)"

WORK="$(mktemp -d "${TMPDIR:-/tmp}/ptcgl-build.XXXXXX")"
trap 'rm -rf "$WORK"' EXIT

# ------------------------------------------------------------------------------------- BepInEx

step "BepInEx $BEPINEX_VER: cache/$BEPINEX_ZIP"
mkdir -p cache
if [ ! -f "cache/$BEPINEX_ZIP" ]; then
    echo "Downloading $BEPINEX_URL"
    if command -v curl >/dev/null 2>&1; then
        curl -fsSL --proto '=https' --retry 3 --retry-delay 2 -o "cache/$BEPINEX_ZIP.partial" "$BEPINEX_URL"
    else
        "${PYRUN[@]}" -c 'import shutil, sys, urllib.request
with urllib.request.urlopen(sys.argv[1], timeout=120) as r, open(sys.argv[2], "wb") as f: shutil.copyfileobj(r, f)' \
            "$BEPINEX_URL" "cache/$BEPINEX_ZIP.partial"
    fi
    actual="$(sha256 "cache/$BEPINEX_ZIP.partial")"
    if [ "$actual" != "$BEPINEX_SHA256" ]; then
        rm -f "cache/$BEPINEX_ZIP.partial"
        die "downloaded $BEPINEX_ZIP has SHA-256 $actual, expected $BEPINEX_SHA256"
    fi
    mv -f "cache/$BEPINEX_ZIP.partial" "cache/$BEPINEX_ZIP"
fi
actual="$(sha256 "cache/$BEPINEX_ZIP")"
[ "$actual" = "$BEPINEX_SHA256" ] \
    || die "cache/$BEPINEX_ZIP has SHA-256 $actual, expected $BEPINEX_SHA256 - delete it and run again"
echo "SHA-256 OK ($BEPINEX_SHA256)"

# The plugin compiles against the same BepInEx that ships to players. Written only when missing or
# different, so an unchanged lib/ does not force a rebuild.
"${PYRUN[@]}" - "cache/$BEPINEX_ZIP" lib <<'PY'
import os, sys, zipfile
src, out = sys.argv[1], sys.argv[2]
os.makedirs(out, exist_ok=True)
with zipfile.ZipFile(src) as z:
    for name in ("BepInEx/core/BepInEx.dll", "BepInEx/core/0Harmony.dll"):
        data = z.read(name)
        target = os.path.join(out, name.rsplit("/", 1)[1])
        if os.path.isfile(target) and open(target, "rb").read() == data:
            continue
        with open(target + ".tmp", "wb") as f:
            f.write(data)
        os.replace(target + ".tmp", target)
        print("extracted " + target)
PY
echo "lib/BepInEx.dll, lib/0Harmony.dll ready"

# ------------------------------------------------------------------------------------- tests

step "Plugin core tests (tests/CoreTests), writing sample sealed logs"
SAMPLES="$WORK/seal-samples"
mkdir -p "$SAMPLES"
PTCGL_SEAL_SAMPLES="$SAMPLES" "$DOTNET" run --project tests/CoreTests -c Release \
    || die "tests/CoreTests failed"
ls "$SAMPLES"/*.txt >/dev/null 2>&1 || die "tests/CoreTests wrote no sample logs into PTCGL_SEAL_SAMPLES"

step "Seal cross-check: tools/verify_log.py on the plugin's sample logs"
"${PYRUN[@]}" tools/verify_log.py --self-test || die "tools/verify_log.py --self-test failed"
"${PYRUN[@]}" tools/verify_log.py --chain "$SAMPLES" \
    || die "tools/verify_log.py rejected the seals written by the plugin's code (see above)"

step "Seal cross-check: tampered copies must be rejected"
for kind in footer sidecar; do
    TAMPERED="$WORK/tampered-$kind"
    mkdir -p "$TAMPERED"
    cp -R "$SAMPLES"/. "$TAMPERED"/
    # Flips one bit of the first character of a log: the seal no longer matches its text.
    victim="$("${PYRUN[@]}" - "$TAMPERED" "$kind" <<'PY'
import os, sys
folder, kind = sys.argv[1], sys.argv[2]
marker = b"\n\n-----BEGIN PTCGL-ABL SEAL v1-----\n"
for name in sorted(os.listdir(folder)):
    path = os.path.join(folder, name)
    if not name.endswith(".txt"):
        continue
    has_sidecar = os.path.isfile(path[:-4] + ".seal")
    data = bytearray(open(path, "rb").read())
    if (kind == "sidecar") != has_sidecar or (kind == "footer" and marker not in data):
        continue
    i = 3 if data.startswith(b"\xef\xbb\xbf") else 0
    data[i] ^= 0x01
    open(path, "wb").write(bytes(data))
    print(name)
    break
PY
)"
    [ -n "$victim" ] || die "no $kind-sealed sample log to tamper with"
    set +e
    "${PYRUN[@]}" tools/verify_log.py --chain "$TAMPERED" >"$WORK/tampered-$kind.out" 2>&1
    rc=$?
    set -e
    # Exit code 1 and a proper report (a crash would also exit 1).
    if [ "$rc" -ne 1 ] || ! grep -q '^INVALID ' "$WORK/tampered-$kind.out" \
            || ! grep -q '^SUMMARY .*NOT VALID' "$WORK/tampered-$kind.out"; then
        cat "$WORK/tampered-$kind.out"
        die "tools/verify_log.py did not reject a tampered $kind seal ($victim): exit code $rc, expected 1"
    fi
    echo "tampered $kind log rejected as expected: $victim"
done

step "Tray logic tests (tests/TrayTests)"
# PYTHON: the cross-part checks run tools/*.py with the same interpreter.
PYTHON="$PYTHON" "$DOTNET" run --project tests/TrayTests -c Release \
    "--property:Version=$VERSION" "--property:RepoOwner=$OWNER" "--property:RepoName=$REPO" \
    || die "tests/TrayTests failed"

# ------------------------------------------------------------------------------------- build

step "Building $EXE_NAME v$VERSION (plugin embedded)"
"$DOTNET" build src/Tray/PtcglAutoBattleLog.Tray.csproj -c Release -nologo \
    "-p:Version=$VERSION" "-p:RepoOwner=$OWNER" "-p:RepoName=$REPO" \
    || die "building src/Tray failed"
EXE="src/Tray/bin/Release/$EXE_NAME"
[ -f "$EXE" ] || die "$EXE was not produced"
# The BepInEx zip is embedded, so a smaller exe means the payload is missing.
[ "$(wc -c <"$EXE")" -gt "$(wc -c <"cache/$BEPINEX_ZIP")" ] || die "$EXE is too small to contain its payload"

# ------------------------------------------------------------------------------------- package

step "Packaging dist/"
rm -rf dist
mkdir -p dist
cp "$EXE" "dist/$EXE_NAME"

if [ -z "${SOURCE_DATE_EPOCH:-}" ]; then
    SOURCE_DATE_EPOCH="$(git log -1 --format=%ct 2>/dev/null || true)"
fi
# Same inputs, same zip: fixed timestamps (the commit's), fixed order, no host-specific attributes,
# and stored, not deflated, so the bytes do not depend on the zlib build (zlib-ng deflates differently).
# The license texts travel with the BepInEx and Doorstop binaries inside the exe (MIT and LGPL-2.1).
"${PYRUN[@]}" - "dist/$EXE_NAME" build/portable.txt "dist/$PORTABLE_ZIP" "${SOURCE_DATE_EPOCH:-0}" \
    "https://github.com/$OWNER/$REPO" "$VERSION" \
    LICENSE LICENSE.txt THIRD-PARTY-NOTICES.md THIRD-PARTY-NOTICES.txt licenses/LGPL-2.1.txt licenses/LGPL-2.1.txt <<'PY'
import os, sys, time, zipfile
exe, note_path, out, epoch, project_url, version = sys.argv[1:7]
texts = list(zip(sys.argv[7::2], sys.argv[8::2]))   # (source, name in the zip)
stamp = time.gmtime(max(int(epoch or 0), 315532800))[:6]   # zip dates start in 1980

def notepad(text):
    return text.replace("\r\n", "\n").replace("\n", "\r\n").encode("utf-8")   # Notepad-friendly

note = open(note_path, encoding="utf-8").read()
note = note.replace("@PROJECT_URL@", project_url).replace("@VERSION@", version)

def entry(name):
    info = zipfile.ZipInfo("PtcglAutoBattleLog/" + name, date_time=stamp)
    info.compress_type = zipfile.ZIP_STORED
    info.create_system = 0
    info.external_attr = 0
    return info

with zipfile.ZipFile(out + ".tmp", "w") as z:
    z.writestr(entry("PtcglAutoBattleLog.exe"), open(exe, "rb").read())
    z.writestr(entry("portable.txt"), notepad(note))
    for source, name in texts:
        z.writestr(entry(name), notepad(open(source, encoding="utf-8").read()))
os.replace(out + ".tmp", out)
PY

step "Release manifest: dist/update.json, dist/SHA256SUMS.txt"
"${PYRUN[@]}" tools/make-manifest.py --version "$VERSION" --tag "v$VERSION" --owner "$OWNER" --repo "$REPO" \
    --exe "dist/$EXE_NAME" --portable-zip "dist/$PORTABLE_ZIP" --out dist \
    || die "tools/make-manifest.py failed"

end_group
echo
echo "Done: PTCGL Auto Battle Log v$VERSION (updates from github.com/$OWNER/$REPO)"
for f in dist/*; do printf '  %-40s %10s bytes\n' "$f" "$(wc -c <"$f" | tr -d ' ')"; done
