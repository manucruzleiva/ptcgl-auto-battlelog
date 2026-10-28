#!/usr/bin/env python3
"""Write update.json and SHA256SUMS.txt for a release (docs/ARCHITECTURE.md, "OTA updates").

    make-manifest.py --version 1.2.0 --tag v1.2.0 --owner OWNER --repo REPO \\
                     --exe dist/PtcglAutoBattleLog.exe --out dist \\
                     [--portable-zip dist/PtcglAutoBattleLog-portable.zip] \\
                     [--released-utc 2026-11-01T12:00:00Z]

update.json lists only the exe: installed and portable copies update from the same file. The tray
accepts an asset only if its URL is exactly https://github.com/{owner}/{repo}/releases/download/
{tag}/{file} and the download matches "size" and "sha256", so everything that ends up in those
fields is validated here with the same rules, and a manifest the tray would refuse is never written.

SHA256SUMS.txt is in the format of `sha256sum` (and `sha256sum -c` checks it): the exe, then the
portable zip when given.

Python 3.8+, standard library only. Exit code 0 on success, 2 on any error.
"""

import argparse
import datetime
import hashlib
import json
import os
import re
import sys

EXE_ASSET = "PtcglAutoBattleLog.exe"
PORTABLE_ASSET = "PtcglAutoBattleLog-portable.zip"
MANIFEST = "update.json"
SUMS = "SHA256SUMS.txt"

# UpdateChecker.TryParseVersion: three numbers, no leading zeros, at most 9 digits each, optionally
# a -suffix. A suffixed version is a pre-release: published as a GitHub pre-release (never the
# latest release), and the stable tray ignores it.
VERSION = re.compile(r"(0|[1-9][0-9]{0,8})\.(0|[1-9][0-9]{0,8})\.(0|[1-9][0-9]{0,8})(-[0-9A-Za-z-]+(\.[0-9A-Za-z-]+)*)?")
# UpdateChecker.IsSafeName: what may appear as one path segment of a trusted asset URL (also: not
# starting with '.', at most 100 characters; see safe_name).
SAFE_NAME = re.compile(r"[A-Za-z0-9._-]+")
UTC_DATE = re.compile(r"\d{4}-\d\d-\d\dT\d\d:\d\d:\d\dZ")


class Failure(Exception):
    """Something about the inputs is wrong; the message says what."""


def safe_name(value, what):
    if not SAFE_NAME.fullmatch(value or "") or value.startswith(".") or len(value) > 100:
        raise Failure("%s %r: only ASCII letters, digits, '.', '_' and '-' (not first), at most 100 characters" % (what, value))
    return value


def file_info(path, expected_name):
    """(size, sha256 hex) of a release file, which must already carry its published name."""
    if not os.path.isfile(path):
        raise Failure("not found: " + path)
    name = os.path.basename(path)
    if name != expected_name:
        raise Failure("%s must be named %s (the asset name is the file name), got %s" % (path, expected_name, name))
    digest = hashlib.sha256()
    size = 0
    with open(path, "rb") as f:
        for block in iter(lambda: f.read(1 << 20), b""):
            digest.update(block)
            size += len(block)
    if size == 0:
        raise Failure("empty file: " + path)
    return size, digest.hexdigest()


def write_atomic(path, data):
    tmp = path + ".tmp"
    with open(tmp, "wb") as f:
        f.write(data)
    os.replace(tmp, path)


def build(args):
    if not VERSION.fullmatch(args.version or "") or len(args.version) > 40:
        raise Failure("--version must be MAJOR.MINOR.PATCH or MAJOR.MINOR.PATCH-PRERELEASE (e.g. 1.2.0, 1.3.2-beta.1, no 'v'), got %r" % args.version)
    if args.tag != "v" + args.version:
        raise Failure("--tag must be 'v' + version (v%s), got %r" % (args.version, args.tag))
    owner = safe_name(args.owner, "--owner")
    repo = safe_name(args.repo, "--repo")

    released = args.released_utc
    if released is None:
        released = datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")
    elif not UTC_DATE.fullmatch(released):
        raise Failure("--released-utc must be yyyy-MM-ddTHH:mm:ssZ, got %r" % released)
    else:
        try:
            datetime.datetime.strptime(released, "%Y-%m-%dT%H:%M:%SZ")
        except ValueError:
            raise Failure("--released-utc is not a real date: %r" % released)

    size, sha = file_info(args.exe, EXE_ASSET)
    with open(args.exe, "rb") as f:
        if f.read(2) != b"MZ":
            raise Failure(args.exe + " is not a Windows executable")
    sums = [(sha, EXE_ASSET)]
    if args.portable_zip:
        zip_size, zip_sha = file_info(args.portable_zip, PORTABLE_ASSET)
        with open(args.portable_zip, "rb") as f:
            if f.read(4) != b"PK\x03\x04":
                raise Failure(args.portable_zip + " is not a zip file")
        sums.append((zip_sha, PORTABLE_ASSET))

    base = "https://github.com/%s/%s/releases" % (owner, repo)
    manifest = {
        "version": args.version,
        "tag": args.tag,
        "releasedUtc": released,
        "notesUrl": "%s/tag/%s" % (base, args.tag),
        "assets": [
            {
                "name": EXE_ASSET,
                "url": "%s/download/%s/%s" % (base, args.tag, EXE_ASSET),
                "size": size,
                "sha256": sha,
            }
        ],
    }

    os.makedirs(args.out, exist_ok=True)
    text = json.dumps(manifest, indent=2, ensure_ascii=True) + "\n"
    write_atomic(os.path.join(args.out, MANIFEST), text.encode("ascii"))
    write_atomic(os.path.join(args.out, SUMS), "".join("%s  %s\n" % s for s in sums).encode("ascii"))
    return manifest, sums


def main(argv):
    parser = argparse.ArgumentParser(
        description="Write update.json and SHA256SUMS.txt for a PTCGL Auto Battle Log release.")
    parser.add_argument("--version", required=True, help="release version, MAJOR.MINOR.PATCH[-PRERELEASE]")
    parser.add_argument("--tag", required=True, help="release tag, 'v' + version")
    parser.add_argument("--owner", required=True, help="GitHub owner the release is published under")
    parser.add_argument("--repo", required=True, help="GitHub repository the release is published in")
    parser.add_argument("--exe", required=True, help="path to " + EXE_ASSET)
    parser.add_argument("--out", required=True, help="folder for " + MANIFEST + " and " + SUMS)
    parser.add_argument("--portable-zip", help="path to " + PORTABLE_ASSET + " (hashed into " + SUMS + ")")
    parser.add_argument("--released-utc", help="release date, yyyy-MM-ddTHH:mm:ssZ (default: now)")
    args = parser.parse_args(argv)
    try:
        manifest, sums = build(args)
    except (Failure, OSError) as e:
        print("make-manifest: error: %s" % e, file=sys.stderr)
        return 2
    asset = manifest["assets"][0]
    print("%s: v%s, %s (%d bytes, sha256 %s)" % (os.path.join(args.out, MANIFEST), manifest["version"],
                                                 asset["url"], asset["size"], asset["sha256"]))
    print("%s: %s" % (os.path.join(args.out, SUMS), ", ".join(name for _, name in sums)))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
