#!/usr/bin/env python3
"""Verify the seals of PTCGL Auto Battle Log battle logs (format: docs/LOG-SEAL.md).

    verify_log.py FILE...         check each log: a .txt with a footer seal, or a .txt with its
                                  .seal sidecar (passing the .seal file checks its .txt); the deck
                                  a v2 seal names (decks/<hash>.deck) is checked too when it is there
    verify_log.py --chain DIR     check every log in DIR, then each key's chain: forks and broken
                                  links are errors, gaps are warnings
    verify_log.py --key KEYID     (with FILEs or --chain, repeatable) also require the seal to be
                                  made with one of these keys: anyone can re-seal an edited log
                                  with a key of their own, so "unchanged since it was saved" only
                                  means something relative to the player's known keyId
    verify_log.py --self-test     check this verifier against logs it seals with a throwaway key

FILEs and --chain can be combined; the chain check then covers all of them.

Prints one line per file and a summary. Exit code: 0 everything checked is valid, 1 something is
invalid (or a FILE given by name is not sealed), 2 usage error. In --chain mode, logs without a
seal (saved before 1.1, or with [Metadata] Mode = Off) are listed but are not errors.

Python 3.8+, standard library only. The RSA check is done here with pow(): no crypto package
needed, and nothing is trusted that is not checked.
"""

import argparse
import base64
import hashlib
import hmac
import io
import json
import os
import re
import secrets
import shutil
import sys
import tempfile
import time
import unicodedata
from contextlib import redirect_stderr, redirect_stdout

# Seal format versions this verifier knows (docs/LOG-SEAL.md, "Versioning"). The plugin writes v2;
# v1 seals from earlier builds stay valid.
VERSIONS = (1, 2)
BEGINS = {v: "-----BEGIN PTCGL-ABL SEAL v%d-----" % v for v in VERSIONS}
ENDS = {v: "-----END PTCGL-ABL SEAL v%d-----" % v for v in VERSIONS}
BEGIN, END = BEGINS[2], ENDS[2]
# A footer seal of any version starts with this; the last occurrence counts.
SEPARATOR_PREFIX = b"\n\n-----BEGIN PTCGL-ABL SEAL v"
BOM = b"\xef\xbb\xbf"
SIDECAR_EXT = ".seal"
DECK_EXT = ".deck"
DECKS_FOLDER = "decks"  # each distinct deck once, named by its SHA-256 (docs/LOG-SEAL.md, 2a)
MAX_DECK_BYTES = 64 * 1024
APP = "PTCGL Auto Battle Log"
ACCOUNT_HASH_PREFIX = "ptcgl-abl:v1:"
# The plugin makes RSA-2048 keys with e = 65537. The bounds keep pow() cheap whatever a file
# claims: an 8192-bit key with a huge exponent would otherwise keep the verifier busy for minutes.
MIN_KEY_BITS = 2048
MAX_KEY_BITS = 4096
PUBLIC_EXPONENT = 65537
# A plugin seal block is about 1.5 KB; a battle log a few hundred KB at most.
MAX_BLOCK_BYTES = 16 * 1024
MAX_FILE_BYTES = 16 * 1024 * 1024

TOP_KEYS_V1 = ("v", "app", "pluginVersion", "gameVersion", "capture", "matchId", "startedUtc", "endedUtc",
               "result", "winner", "mySeat", "player", "opponent", "deck", "logSha256", "logChars",
               "sealedUtc", "seq", "prev", "keyId", "publicKey")
# v2 adds deckSha256 (the SHA-256 of the .deck file beside the log, or null) right after deck.
TOP_KEYS_V2 = TOP_KEYS_V1[:14] + ("deckSha256",) + TOP_KEYS_V1[14:]
TOP_KEYS = {1: TOP_KEYS_V1, 2: TOP_KEYS_V2}
PARTY_KEYS = ("name", "accountHash")
PUBLIC_KEY_KEYS = ("kty", "n", "e")
RESULTS = ("VICTORY", "DEFEAT", "NO-RESULT", "EXPORTED")
CAPTURES = ("auto", "export")

# DER of DigestInfo { AlgorithmIdentifier { id-sha256, NULL }, OCTET STRING (32 bytes) }: the
# only encoding RSASSA-PKCS1-v1_5 with SHA-256 allows (RFC 8017, section 9.2, note 1).
SHA256_DIGEST_INFO = bytes.fromhex("3031300d060960864801650304020105000420")

HEX64 = re.compile(r"[0-9a-f]{64}")
UTC_DATE = re.compile(r"\d{4}-\d\d-\d\dT\d\d:\d\d:\d\dZ")
B64URL = re.compile(r"[A-Za-z0-9_-]+")
B64STD = re.compile(r"[A-Za-z0-9+/]+={0,2}")
KEY_ID = re.compile(r"[A-Za-z0-9_-]{43}")   # base64url of a SHA-256, no padding


class Invalid(Exception):
    """The seal does not hold; the message says why."""


class Seal:
    """A seal that verified, with what the chain check needs."""

    def __init__(self, path, source, meta, chain_hash, deck=None):
        self.path = path
        self.source = source
        self.meta = meta
        self.hash = chain_hash
        self.deck = deck  # "deck" (checked), "deck file missing", or None (the seal names no deck)

    @property
    def seq(self):
        return self.meta["seq"]

    @property
    def prev(self):
        return self.meta["prev"]

    @property
    def key_id(self):
        return self.meta["keyId"]


# ---------------------------------------------------------------------------------- reading


def log_path_for(path):
    """A .seal sidecar stands for its .txt."""
    stem, ext = os.path.splitext(path)
    return stem + ".txt" if ext.lower() == SIDECAR_EXT else path


def read_log_and_block(path):
    """(log bytes, seal block bytes or None, "footer" | "sidecar" | None) of a .txt file.

    The log is everything before the last occurrence of SEPARATOR_PREFIX, after removing a leading BOM.
    Without a footer, the block is the .seal file next to the .txt (same name), if there is one.
    Oversized files and blocks are refused before anything is parsed.
    """
    data = _read_capped(path, MAX_FILE_BYTES, "the file")
    if data.startswith(BOM):
        data = data[len(BOM):]
    at = data.rfind(SEPARATOR_PREFIX)
    if at >= 0:
        return data[:at], _capped_block(data[at + 2:]), "footer"
    sidecar = os.path.splitext(path)[0] + SIDECAR_EXT
    if os.path.isfile(sidecar):
        block = _read_capped(sidecar, MAX_BLOCK_BYTES + len(BOM), "the .seal file")
        if block.startswith(BOM):
            block = block[len(BOM):]
        return data, _capped_block(block), "sidecar"
    if any(data.rstrip(b"\r\n").endswith(end.encode("ascii")) for end in ENDS.values()):
        raise Invalid("the file ends with a seal that is not separated from the log by exactly \"\\n\\n\" "
                      "(line endings converted?), so the file was modified")
    return data, None, None


def _read_capped(path, limit, what):
    with open(path, "rb") as f:
        data = f.read(limit + 1)
    if len(data) > limit:
        raise Invalid("%s is larger than %d bytes, which no plugin seal produces" % (what, limit))
    return data


def _capped_block(block):
    if len(block) > MAX_BLOCK_BYTES:
        raise Invalid("the seal block is longer than %d bytes, which no plugin seal produces" % MAX_BLOCK_BYTES)
    return block


def split_block(block):
    """(version, JSON line, signature line) of "BEGIN\\n<json>\\n<signature>\\nEND\\n", exactly."""
    try:
        text = block.decode("ascii")
    except UnicodeDecodeError:
        raise Invalid("the seal block is not ASCII")
    if "\r" in text:
        raise Invalid("the seal has CR characters: its line endings were converted, so the file was modified")
    lines = text.split("\n")
    version = next((v for v in VERSIONS if lines[0] == BEGINS[v]), None)
    if lines[0].startswith("-----BEGIN PTCGL-ABL SEAL v") and version is None:
        raise Invalid("the seal is of a version this verifier does not know: " + lines[0])
    if len(lines) != 5 or version is None or lines[3] != ENDS[version] or lines[4] != "":
        raise Invalid("the seal block is malformed (expected BEGIN line, JSON line, signature line, END line, "
                      "each ending with a line feed, and nothing after)")
    return version, lines[1], lines[2]


# ---------------------------------------------------------------------------------- metadata


def _pairs(items):
    keys = [k for k, _ in items]
    if len(set(keys)) != len(keys):
        raise Invalid("the metadata has a duplicate key")
    return dict(items)


def _no_float(text):
    raise Invalid("the metadata has a non-integer number: " + text)


def _no_constant(text):
    raise Invalid("the metadata has " + text)


def load_metadata(json_line, version):
    try:
        meta = json.loads(json_line, object_pairs_hook=_pairs, parse_float=_no_float, parse_constant=_no_constant)
    except ValueError as e:
        raise Invalid("the metadata is not valid JSON (%s)" % e)
    if not isinstance(meta, dict):
        raise Invalid("the metadata is not a JSON object")
    if tuple(meta) != TOP_KEYS[version]:
        raise Invalid("the metadata keys are not the v%d keys in order: %s" % (version, ",".join(meta)))
    return meta


def _is_int(v):
    return type(v) is int  # bool is an int subclass: excluded on purpose


def _opt_str(meta, key):
    if meta[key] is not None and not isinstance(meta[key], str):
        raise Invalid(key + " must be a string or null")


def _date(meta, key, nullable):
    v = meta[key]
    if v is None and nullable:
        return
    if not isinstance(v, str) or not UTC_DATE.fullmatch(v):
        raise Invalid(key + " must be a UTC date yyyy-MM-ddTHH:mm:ssZ" + (" or null" if nullable else ""))


def check_fields(meta, version):
    """Types and values of every field, so nothing downstream trips on a crafted seal."""
    if not _is_int(meta["v"]) or meta["v"] != version:
        raise Invalid("v must be %d, as in the BEGIN line" % version)
    if meta["app"] != APP:
        raise Invalid("app must be %r" % APP)
    if not isinstance(meta["pluginVersion"], str) or not meta["pluginVersion"]:
        raise Invalid("pluginVersion must be a non-empty string")
    for key in ("gameVersion", "matchId", "deck"):
        _opt_str(meta, key)
    if meta["capture"] not in CAPTURES:
        raise Invalid("capture must be one of " + ", ".join(CAPTURES))
    _date(meta, "startedUtc", False)
    _date(meta, "endedUtc", True)
    _date(meta, "sealedUtc", False)
    if meta["result"] not in RESULTS:
        raise Invalid("result must be one of " + ", ".join(RESULTS))
    for key in ("winner", "mySeat"):
        if meta[key] is not None and (not _is_int(meta[key]) or meta[key] not in (1, 2)):
            raise Invalid(key + " must be 1, 2 or null")
    for key in ("player", "opponent"):
        party = meta[key]
        if not isinstance(party, dict) or tuple(party) != PARTY_KEYS:
            raise Invalid(key + " must be {\"name\", \"accountHash\"} in that order")
        _opt_str(party, "name")
        if party["accountHash"] is not None and not (isinstance(party["accountHash"], str) and HEX64.fullmatch(party["accountHash"])):
            raise Invalid(key + ".accountHash must be 64 lowercase hex digits or null")
    if version >= 2 and meta["deckSha256"] is not None and not (isinstance(meta["deckSha256"], str) and HEX64.fullmatch(meta["deckSha256"])):
        raise Invalid("deckSha256 must be 64 lowercase hex digits or null")
    if not isinstance(meta["logSha256"], str) or not HEX64.fullmatch(meta["logSha256"]):
        raise Invalid("logSha256 must be 64 lowercase hex digits")
    if not _is_int(meta["logChars"]) or meta["logChars"] < 0:
        raise Invalid("logChars must be a non-negative integer")
    if not _is_int(meta["seq"]) or meta["seq"] < 1:
        raise Invalid("seq must be an integer >= 1")
    prev = meta["prev"]
    if meta["seq"] == 1:
        if prev is not None:
            raise Invalid("seq 1 must have prev null")
    elif not isinstance(prev, str) or not HEX64.fullmatch(prev):
        raise Invalid("prev must be 64 lowercase hex digits after seq 1")
    if not isinstance(meta["keyId"], str) or not B64URL.fullmatch(meta["keyId"]):
        raise Invalid("keyId must be base64url")
    pk = meta["publicKey"]
    if not isinstance(pk, dict) or tuple(pk) != PUBLIC_KEY_KEYS:
        raise Invalid("publicKey must be {\"kty\", \"n\", \"e\"} in that order")
    if pk["kty"] != "RSA":
        raise Invalid("publicKey.kty must be \"RSA\"")


# ---------------------------------------------------------------------------------- crypto


def b64url_uint(value, what):
    """A JWK Base64urlUInt: canonical base64url, no padding, no leading zero byte."""
    if not isinstance(value, str) or not B64URL.fullmatch(value) or len(value) % 4 == 1:
        raise Invalid(what + " is not base64url")
    raw = base64.urlsafe_b64decode(value + "=" * (-len(value) % 4))
    if base64.urlsafe_b64encode(raw).rstrip(b"=").decode("ascii") != value:
        raise Invalid(what + " is not canonical base64url")
    if len(raw) > 1 and raw[0] == 0:
        raise Invalid(what + " has a leading zero byte")
    return int.from_bytes(raw, "big")


def b64url(data):
    return base64.urlsafe_b64encode(data).rstrip(b"=").decode("ascii")


def jwk_thumbprint(n_b64, e_b64):
    """RFC 7638: SHA-256 of the required members in lexicographic order, no whitespace; base64url."""
    canonical = '{"e":"%s","kty":"RSA","n":"%s"}' % (e_b64, n_b64)
    return b64url(hashlib.sha256(canonical.encode("ascii")).digest())


def pkcs1_v15_sha256_encoding(message, k):
    """EM = 0x00 0x01 FF..FF 0x00 DigestInfo(SHA-256(message)), k bytes long (RFC 8017, 9.2)."""
    t = SHA256_DIGEST_INFO + hashlib.sha256(message).digest()
    if k < len(t) + 11:
        raise Invalid("the key is too small")
    return b"\x00\x01" + b"\xff" * (k - len(t) - 3) + b"\x00" + t


def verify_rsa_sha256(n, e, message, signature_b64):
    """RSASSA-PKCS1-v1_5 with SHA-256. The decrypted block is compared, whole, with the one valid
    encoding: no ASN.1 parsing, so no parser leniency to exploit."""
    if not B64STD.fullmatch(signature_b64) or len(signature_b64) % 4:
        raise Invalid("the signature is not standard base64")
    sig = base64.b64decode(signature_b64, validate=True)
    # base64 decoders ignore the unused low bits of the last character, so one signature has
    # several spellings. The chain hash covers the line's text: only the canonical spelling may
    # verify, or anyone could turn a shared seal into a "fork" without the key.
    if base64.b64encode(sig).decode("ascii") != signature_b64:
        raise Invalid("the signature is not canonical base64 (the unused bits of its last character must be zero)")
    k = (n.bit_length() + 7) // 8
    if len(sig) != k:
        raise Invalid("the signature has %d bytes, the key needs %d" % (len(sig), k))
    s = int.from_bytes(sig, "big")
    if s >= n:
        raise Invalid("the signature is out of range")
    em = pow(s, e, n).to_bytes(k, "big")
    if not hmac.compare_digest(em, pkcs1_v15_sha256_encoding(message, k)):
        raise Invalid("the signature does not match the metadata (the seal was edited, or signed by another key)")


def utf16_length(text):
    return len(text.encode("utf-16-le")) // 2


def account_hash(account_id):
    return hashlib.sha256((ACCOUNT_HASH_PREFIX + account_id).encode("utf-8")).hexdigest()


def chain_hash(json_line, signature_line):
    return hashlib.sha256(json_line.encode("ascii") + b"\n" + signature_line.encode("ascii")).hexdigest()


# ---------------------------------------------------------------------------------- one file


def verify_file(path):
    """(Seal, None) when valid, (None, None) when not sealed; raises Invalid otherwise."""
    log, block, source = read_log_and_block(path)
    if block is None:
        return None
    version, json_line, signature_line = split_block(block)
    meta = load_metadata(json_line, version)
    check_fields(meta, version)

    n = b64url_uint(meta["publicKey"]["n"], "publicKey.n")
    e = b64url_uint(meta["publicKey"]["e"], "publicKey.e")
    if not MIN_KEY_BITS <= n.bit_length() <= MAX_KEY_BITS:
        raise Invalid("the key has %d bits; %d to %d are allowed" % (n.bit_length(), MIN_KEY_BITS, MAX_KEY_BITS))
    if e != PUBLIC_EXPONENT:
        raise Invalid("the public exponent is not %d" % PUBLIC_EXPONENT)
    if meta["keyId"] != jwk_thumbprint(meta["publicKey"]["n"], meta["publicKey"]["e"]):
        raise Invalid("keyId is not the RFC 7638 thumbprint of publicKey")

    verify_rsa_sha256(n, e, json_line.encode("ascii"), signature_line)

    problems = []
    if hashlib.sha256(log).hexdigest() != meta["logSha256"]:
        problems.append("the log does not match logSha256: it was changed after it was sealed")
    try:
        chars = utf16_length(log.decode("utf-8"))
        if chars != meta["logChars"]:
            problems.append("the log has %d characters, the seal says %d" % (chars, meta["logChars"]))
    except UnicodeDecodeError:
        problems.append("the log is not valid UTF-8")
    deck = None
    if meta.get("deckSha256") is not None:
        # The deck is a file of its own, shared by every log played with it: one that was deleted or
        # not copied along leaves the log valid (reported); one that is there must be the sealed deck.
        deck_path = os.path.join(os.path.dirname(path), DECKS_FOLDER, meta["deckSha256"] + DECK_EXT)
        if os.path.isfile(deck_path):
            deck = "deck"
            if hashlib.sha256(_read_capped(deck_path, MAX_DECK_BYTES, "the .deck file")).hexdigest() != meta["deckSha256"]:
                problems.append("%s/%s%s does not match deckSha256: it was changed after it was sealed"
                                % (DECKS_FOLDER, meta["deckSha256"][:12] + "...", DECK_EXT))
        else:
            deck = "deck file missing"
    if problems:
        raise Invalid("; ".join(problems))
    return Seal(path, source, meta, chain_hash(json_line, signature_line), deck)


def describe(seal):
    m = seal.meta
    who = m["result"] + (" vs " + safe(m["opponent"]["name"]) if m["opponent"]["name"] else "")
    extra = ", " + seal.deck if seal.deck else ""
    return "seq %d  %s  (v%d %s, %s%s, key %s)" % (m["seq"], who, m["v"], seal.source, m["capture"], extra, safe(m["keyId"]))


def safe(text):
    """Text that came from a file or a file name, made safe to print: control and format
    characters (escape sequences, carriage returns, bidi overrides...) and line or paragraph
    separators become \\uXXXX, so a crafted name cannot rewrite or hide what the verifier says."""
    out = []
    for ch in str(text):
        category = unicodedata.category(ch)
        if category[0] == "C" or category in ("Zl", "Zp"):
            out.append("\\u%04x" % ord(ch) if ord(ch) <= 0xFFFF else "\\U%08x" % ord(ch))
        else:
            out.append(ch)
    return "".join(out)


# ---------------------------------------------------------------------------------- chains


def check_chains(seals):
    """Per keyId: forks (two different seals with one seq) and broken links are errors; gaps are
    warnings (a log re-sealed out of order, deleted, or kept elsewhere). Returns (errors, warnings)."""
    errors = warnings = 0
    by_key = {}
    for s in seals:
        by_key.setdefault(s.key_id, {}).setdefault(s.seq, []).append(s)
    for key_id in sorted(by_key):
        by_seq = by_key[key_id]
        seqs = sorted(by_seq)
        key_errors = 0
        for seq in seqs:
            group = by_seq[seq]
            if len({s.hash for s in group}) > 1:
                print("ERROR      key %s: fork at seq %d, %d different seals: %s"
                      % (safe(key_id), seq, len(group), ", ".join(safe(os.path.basename(s.path)) for s in group)))
                key_errors += 1
            elif len(group) > 1:
                print("WARNING    key %s: seq %d: the same seal is in %d files (copies): %s"
                      % (safe(key_id), seq, len(group), ", ".join(safe(os.path.basename(s.path)) for s in group)))
                warnings += 1
        for a, b in zip(seqs, seqs[1:]):
            if b == a + 1:
                before = {s.hash for s in by_seq[a]}
                for s in by_seq[b]:
                    if s.prev not in before:
                        print("ERROR      key %s: broken link: seq %d (%s) does not follow seq %d (%s)"
                              % (safe(key_id), b, safe(os.path.basename(s.path)), a,
                                 ", ".join(safe(os.path.basename(x.path)) for x in by_seq[a])))
                        key_errors += 1
            else:
                print("WARNING    key %s: gap: seq %s not found" % (safe(key_id), _range(a + 1, b - 1)))
                warnings += 1
        if seqs[0] > 1:
            print("WARNING    key %s: gap: seq %s not found" % (safe(key_id), _range(1, seqs[0] - 1)))
            warnings += 1
        total = sum(len(g) for g in by_seq.values())
        print("CHAIN      key %s: %d seal%s, seq %d-%d, %s"
              % (safe(key_id), total, "" if total == 1 else "s", seqs[0], seqs[-1],
                 "continuous" if key_errors == 0 else "%d error%s" % (key_errors, "" if key_errors == 1 else "s")))
        errors += key_errors
    return errors, warnings


def _range(a, b):
    return str(a) if a == b else "%d-%d" % (a, b)


# ---------------------------------------------------------------------------------- main


def _attach_key_values(argv):
    """Turn "--key VALUE" into "--key=VALUE". A keyId is base64url, so about one key in 64 starts
    with "-", and argparse would take such a value for an option and stop with a usage error."""
    out, i = [], 0
    while i < len(argv):
        if argv[i] == "--key" and i + 1 < len(argv):
            out.append("--key=" + argv[i + 1])
            i += 2
        else:
            out.append(argv[i])
            i += 1
    return out


def run(argv):
    argv = _attach_key_values(list(argv))
    parser = argparse.ArgumentParser(
        prog="verify_log.py",
        description="Verify the seals of PTCGL Auto Battle Log battle logs (docs/LOG-SEAL.md).")
    parser.add_argument("files", nargs="*", metavar="FILE", help="a log (.txt) or its .seal sidecar")
    parser.add_argument("--chain", metavar="DIR", help="check every log in DIR and each key's chain")
    parser.add_argument("--key", metavar="KEYID", action="append", default=[],
                        help="accept only seals made with this keyId (the player's known key); repeatable")
    parser.add_argument("--self-test", action="store_true", help="test this verifier and exit")
    try:
        args = parser.parse_args(argv)
    except SystemExit as e:
        return 2 if e.code else 0

    if args.self_test:
        if args.files or args.chain or args.key:
            print("--self-test takes no other arguments", file=sys.stderr)
            return 2
        return self_test()
    if not args.files and not args.chain:
        parser.print_usage(sys.stderr)
        print("verify_log.py: give FILEs, --chain DIR, or --self-test", file=sys.stderr)
        return 2
    for key_id in args.key:
        if not KEY_ID.fullmatch(key_id):
            print("verify_log.py: not a keyId (43 base64url characters): " + safe(key_id), file=sys.stderr)
            return 2
    wanted_keys = set(args.key)

    named = []
    for f in args.files:
        path = log_path_for(f)
        if not os.path.isfile(path):
            print("verify_log.py: no such log: " + safe(f), file=sys.stderr)
            return 2
        named.append(path)
    listed = []
    orphans = []
    if args.chain:
        if not os.path.isdir(args.chain):
            print("verify_log.py: not a folder: " + safe(args.chain), file=sys.stderr)
            return 2
        names = sorted(os.listdir(args.chain))
        lower = {n.lower() for n in names}
        for name in names:
            path = os.path.join(args.chain, name)
            if not os.path.isfile(path):
                continue
            stem, ext = os.path.splitext(name)
            if ext.lower() == ".txt":
                listed.append(path)
            elif ext.lower() == SIDECAR_EXT and (stem + ".txt").lower() not in lower:
                orphans.append(path)

    seals, seen = [], set()
    valid = invalid = unsealed = named_unsealed = 0
    for path, strict in [(p, True) for p in named] + [(p, False) for p in listed]:
        key = os.path.normcase(os.path.abspath(path))
        if key in seen:
            continue
        seen.add(key)
        try:
            seal = verify_file(path)
        except Invalid as e:
            print("INVALID    %s: %s" % (safe(path), safe(e)))
            invalid += 1
            continue
        except OSError as e:
            print("INVALID    %s: cannot be read (%s)" % (safe(path), safe(e.strerror or e)))
            invalid += 1
            continue
        if seal is None:
            print("UNSEALED   %s" % safe(path))
            unsealed += 1
            named_unsealed += strict
            continue
        if wanted_keys and seal.key_id not in wanted_keys:
            print("INVALID    %s: sealed by another key (%s), not by the --key given" % (safe(path), safe(seal.key_id)))
            invalid += 1
            continue
        print("OK         %s  %s" % (safe(path), describe(seal)))
        valid += 1
        seals.append(seal)
    for path in orphans:
        print("INVALID    %s: a .seal file without its .txt" % safe(path))
        invalid += 1

    errors = warnings = 0
    if args.chain and seals:
        errors, warnings = check_chains(seals)
    summary = "%d valid, %d invalid, %d unsealed" % (valid, invalid, unsealed)
    if args.chain:
        summary += "; chains: %d error%s, %d warning%s" % (errors, "" if errors == 1 else "s", warnings, "" if warnings == 1 else "s")
    ok = invalid == 0 and named_unsealed == 0 and errors == 0
    print("SUMMARY    " + summary + (" - all valid" if ok else " - NOT VALID"))
    return 0 if ok else 1


# ---------------------------------------------------------------------------------- self-test


def _is_probable_prime(n, rounds=24):
    if n < 4:
        return n in (2, 3)
    for p in _SMALL_PRIMES:
        if n % p == 0:
            return n == p
    d, r = n - 1, 0
    while d % 2 == 0:
        d //= 2
        r += 1
    for _ in range(rounds):
        a = secrets.randbelow(n - 3) + 2
        x = pow(a, d, n)
        if x in (1, n - 1):
            continue
        for _ in range(r - 1):
            x = pow(x, 2, n)
            if x == n - 1:
                break
        else:
            return False
    return True


_SMALL_PRIMES = [p for p in range(3, 2000, 2) if all(p % q for q in range(3, int(p ** 0.5) + 1, 2))]


def _prime(bits):
    while True:
        c = secrets.randbits(bits) | (3 << (bits - 2)) | 1
        if _is_probable_prime(c):
            return c


class _TestKey:
    """A throwaway RSA-2048 key, made here so the self-test needs nothing but the standard library."""

    def __init__(self):
        e = 65537
        while True:
            p, q = _prime(1024), _prime(1024)
            phi = (p - 1) * (q - 1)
            if p != q and (p * q).bit_length() == 2048 and phi % e != 0:
                try:
                    self.d = pow(e, -1, phi)
                    break
                except ValueError:
                    continue
        self.n, self.e = p * q, e
        self.n_b64 = b64url(self.n.to_bytes(256, "big"))
        self.e_b64 = b64url(e.to_bytes(3, "big"))
        self.key_id = jwk_thumbprint(self.n_b64, self.e_b64)

    def sign(self, message):
        em = pkcs1_v15_sha256_encoding(message, 256)
        return base64.b64encode(pow(int.from_bytes(em, "big"), self.d, self.n).to_bytes(256, "big")).decode("ascii")


class _Sealer:
    """Seals like the plugin does (same key order, compact ASCII JSON), for the self-test."""

    def __init__(self, key):
        self.key, self.seq, self.last = key, 0, None

    def block(self, log_text, result="VICTORY", opponent="Gary", seq=None, prev=None, key=None, edit=None, version=2, deck=None):
        key = key or self.key
        log = log_text.encode("utf-8")
        seq = self.seq + 1 if seq is None else seq
        prev = self.last if prev is None and seq > 1 else prev
        meta = {
            "v": 1, "app": APP, "pluginVersion": "1.1.0", "gameVersion": "1.24.0", "capture": "auto",
            "matchId": "m-%d" % seq, "startedUtc": "2026-10-19T21:00:00Z", "endedUtc": "2026-10-19T21:12:00Z",
            "result": result, "winner": 1, "mySeat": 1,
            "player": {"name": "Ash", "accountHash": account_hash("me-123")},
            "opponent": {"name": opponent, "accountHash": account_hash("opp-9")},
            "deck": "Charizard ex", "logSha256": hashlib.sha256(log).hexdigest(),
            "logChars": utf16_length(log_text), "sealedUtc": "2026-10-19T21:12:09Z", "seq": seq, "prev": prev,
            "keyId": key.key_id, "publicKey": {"kty": "RSA", "n": key.n_b64, "e": key.e_b64},
        }
        if version >= 2:
            items = list(meta.items())
            items.insert(14, ("deckSha256", hashlib.sha256(deck).hexdigest() if deck is not None else None))
            meta = dict(items)
        meta["v"] = version
        json_line = json.dumps(meta, separators=(",", ":"), ensure_ascii=True)
        signature = key.sign(json_line.encode("ascii"))
        if edit:
            json_line = edit(json_line)
        self.seq, self.last = seq, chain_hash(json_line, signature)
        return (BEGINS[version] + "\n" + json_line + "\n" + signature + "\n" + ENDS[version] + "\n").encode("ascii")


def self_test():
    checks = []

    def check(ok, what):
        checks.append(ok)
        print(("  ok   " if ok else "  FAIL ") + what)

    def quiet(argv):
        out = io.StringIO()
        with redirect_stdout(out), redirect_stderr(out):
            code = run(argv)
        return code, out.getvalue()

    def write(path, data):
        with open(path, "wb") as f:
            f.write(data)

    print("Making a throwaway RSA-2048 key...")
    key = _TestKey()
    other = _TestKey()
    check(account_hash("me-123") == hashlib.sha256(b"ptcgl-abl:v1:me-123").hexdigest(), "accountHash formula")
    root = tempfile.mkdtemp(prefix="verify-log-selftest-")
    try:
        chain = os.path.join(root, "chain")
        os.mkdir(chain)
        s = _Sealer(key)
        texts = [
            "Setup\r\nTurn 1 - Ash played Charmander\r\nAsh wins.",
            "Zo\u00eb \"Q\" \\ \U0001F525 \u65e5\u672c\u8a9e\nline 2",
            "a log that quotes a seal:\n\n" + BEGIN + "\n{}\nAAAA\n" + END + "\nand goes on",
        ]
        paths = []
        for i, text in enumerate(texts):
            p = os.path.join(chain, "2026-10-19 21.0%d.00 - VICTORY vs Gary.txt" % i)
            write(p, BOM + text.encode("utf-8") + b"\n\n" + s.block(text))
            paths.append(p)
        side = os.path.join(chain, "2026-10-19 21.09.00 - DEFEAT vs Misty.txt")
        write(side, BOM + b"sidecar log")
        write(side[:-4] + SIDECAR_EXT, s.block("sidecar log", result="DEFEAT", opponent="Misty"))
        code, out = quiet(["--chain", chain])
        check(code == 0 and out.count("\nOK ") + out.startswith("OK ") == 4, "chain of 3 footer seals + 1 sidecar is valid")
        check("continuous" in out and "gap" not in out, "chain is continuous")
        check(quiet([paths[2]])[0] == 0, "the last separator wins: a log quoting a seal still verifies")
        check(quiet([side[:-4] + SIDECAR_EXT])[0] == 0, "passing the .seal checks its .txt")
        nobom = os.path.join(root, "nobom.txt")
        write(nobom, texts[0].encode("utf-8") + b"\n\n" + _Sealer(key).block(texts[0]))
        check(quiet([nobom])[0] == 0, "a file without a BOM verifies (the BOM is not part of the log)")

        def tampered(name, data, reason_part, what):
            p = os.path.join(root, name)
            write(p, data)
            code, out = quiet([p])
            check(code == 1 and reason_part in out, what + (": " + out.strip().splitlines()[0] if out else ""))

        with open(paths[0], "rb") as f:
            good = f.read()
        tampered("log.txt", good.replace(b"Ash wins.", b"Ash wins!"), "logSha256", "an edited log is caught")
        tampered("crlf.txt", good.replace(b"\n", b"\r\n"), "line endings", "converted line endings are caught")
        tampered("meta.txt", good.replace(b'"result":"VICTORY"', b'"result":"DEFEAT"'), "signature does not match",
                 "edited metadata is caught")
        tampered("trunc.txt", good[:-60] + good[-59:], "INVALID", "a damaged signature is caught")
        tampered("tail.txt", good + b"\n", "malformed", "text after the seal is caught")
        blk = _Sealer(key).block("x")
        tampered("keyid.txt", BOM + b"x\n\n" + blk.replace(key.key_id.encode(), other.key_id.encode()),
                 "thumbprint", "a keyId that is not the key's thumbprint is caught")
        swapped = blk.replace(key.n_b64.encode(), other.n_b64.encode()).replace(key.key_id.encode(), other.key_id.encode())
        tampered("swap.txt", BOM + b"x\n\n" + swapped, "INVALID", "a seal re-labelled with another key is caught")
        head = '{"v":2,"app":"%s"' % APP
        reorder = lambda j: '{"app":"%s","v":2' % APP + j[len(head):]
        tampered("order.txt", BOM + b"x\n\n" + _Sealer(key).block("x", edit=reorder),
                 "keys are not the v2 keys", "reordered keys are caught")
        dup = lambda j: j[:-1] + ',"v":2}'
        tampered("dup.txt", BOM + b"x\n\n" + _Sealer(key).block("x", edit=dup), "duplicate key", "a duplicate key is caught")
        write(os.path.join(root, "unsealed.txt"), BOM + b"plain log")
        code, out = quiet([os.path.join(root, "unsealed.txt")])
        check(code == 1 and "UNSEALED" in out, "an unsealed FILE named on the command line fails")

        # One signature, several base64 spellings: only the canonical one verifies, or anyone holding
        # a shared log could make a "fork" (a different chain hash at the same seq) without the key.
        sig = sig_line_of(paths[0])
        respelled = respell(sig)
        check(respelled != sig and base64.b64decode(respelled) == base64.b64decode(sig), "(a re-spelled signature decodes to the same bytes)")
        tampered("respelled.txt", good.replace(sig, respelled), "canonical", "a re-spelled signature (same bytes, other chain hash) is caught")
        malleated = os.path.join(root, "malleated")
        os.mkdir(malleated)
        shutil.copy(paths[1], os.path.join(malleated, "original.txt"))
        with open(paths[1], "rb") as f:
            write(os.path.join(malleated, "copy.txt"), f.read().replace(sig_line_of(paths[1]), respell(sig_line_of(paths[1]))))
        code, out = quiet(["--chain", malleated])
        check(code == 1 and "fork" not in out and out.count("INVALID") == 1, "so it is one invalid file, not a fork in the player's chain")

        # Key size and exponent are bounded before any arithmetic: a huge key cannot stall the verifier.
        class Claimed:
            """A public key a crafted file claims; its 'signature' only has the right length."""
            def __init__(self, n, e):
                self.n_b64 = b64url(n.to_bytes((n.bit_length() + 7) // 8, "big"))
                self.e_b64 = b64url(e.to_bytes((e.bit_length() + 7) // 8, "big"))
                self.key_id = jwk_thumbprint(self.n_b64, self.e_b64)
                self.size = (n.bit_length() + 7) // 8

            def sign(self, message):
                return base64.b64encode(b"\x01" * self.size).decode("ascii")

        huge = secrets.randbits(16384) | (1 << 16383) | 1
        started = time.monotonic()
        tampered("huge.txt", BOM + b"x\n\n" + _Sealer(key).block("x", key=Claimed(huge, huge)), "16384 bits",
                 "a 16384-bit key with a 16384-bit exponent is refused")
        check(time.monotonic() - started < 2, "at once (%.2f s)" % (time.monotonic() - started))
        tampered("e3.txt", BOM + b"x\n\n" + _Sealer(key).block("x", key=Claimed(key.n, 3)), "exponent", "an exponent other than 65537 is refused")
        write(os.path.join(root, "long.txt"), BOM + b"x\n\n" + BEGIN.encode() + b"\n" + b"7" * MAX_BLOCK_BYTES + b"\n")
        code, out = quiet([os.path.join(root, "long.txt")])
        check(code == 1 and "longer than" in out, "an oversized seal block is refused before parsing")

        # Text from the file or its name is printed escaped: no terminal control sequences.
        esc = os.path.join(root, "esc.txt")
        write(esc, BOM + b"x\n\n" + _Sealer(key).block("x", opponent="Gary\x1b[8m\u202e"))
        code, out = quiet([esc])
        check(code == 0 and "vs Gary\\u001b[8m\\u202e" in out and "\x1b" not in out and "\u202e" not in out,
              "control and bidi characters in a name are escaped: " + out.splitlines()[0])
        keys = lambda j: j.replace('"v":2', '"v\\u001b[2J\\r":2', 1)
        write(esc, BOM + b"x\n\n" + _Sealer(key).block("x", edit=keys))
        code, out = quiet([esc])
        check(code == 1 and "\\u001b[2J\\u000d" in out and "\x1b" not in out and "\r" not in out, "and in the reason of an INVALID line")
        if os.name != "nt":   # Windows does not allow control characters in file names
            odd = os.path.join(root, "odd\x1b[2Kname.txt")
            write(odd, BOM + b"x\n\n" + _Sealer(key).block("x"))
            code, out = quiet([odd])
            check(code == 0 and "odd\\u001b[2Kname.txt" in out and "\x1b" not in out, "and in a file name")

        # --key: "unchanged since it was saved" relative to the player's known key.
        code, out = quiet(["--key", key.key_id, paths[0]])
        check(code == 0 and "key " + key.key_id in out, "--key with the sealing key: OK, the full keyId is printed")
        code, out = quiet(["--key", other.key_id, paths[0]])
        check(code == 1 and "another key" in out, "--key with another key: a valid seal by someone else is INVALID")
        code, out = quiet(["--key", other.key_id, "--key", key.key_id, "--chain", chain])
        check(code == 0, "--key is repeatable")
        check(quiet(["--key", "not-a-key-id", paths[0]])[0] == 2, "--key with something that is not a keyId -> exit 2")
        dash = "-" + "A" * 42   # a well-formed keyId that starts with "-" (about one real key in 64)
        code, out = quiet(["--key", dash, paths[0]])
        check(code == 1 and "another key" in out, "--key accepts a keyId starting with '-'")

        # Chains: a gap is a warning, a fork or a broken link an error.
        def chain_dir(name, blocks):
            d = os.path.join(root, name)
            os.mkdir(d)
            for i, (text, blk) in enumerate(blocks):
                write(os.path.join(d, "%02d.txt" % i), BOM + text.encode() + b"\n\n" + blk)
            write(os.path.join(d, "old 1.0 log.txt"), BOM + b"unsealed")
            return quiet(["--chain", d])

        g = _Sealer(key)
        b1, b2, b3 = g.block("one"), g.block("two"), g.block("three")
        code, out = chain_dir("gap", [("one", b1), ("three", b3)])
        check(code == 0 and "gap: seq 2 not found" in out and "UNSEALED" in out, "gap -> warning, unsealed listed, exit 0")
        f = _Sealer(key)
        f1 = f.block("one")
        f2 = f.block("two")
        f2b = _Sealer(key).block("two, other version", seq=2, prev=chain_hash_of(f1))
        code, out = chain_dir("fork", [("one", f1), ("two", f2), ("two, other version", f2b)])
        check(code == 1 and "fork at seq 2" in out, "fork -> error, exit 1")
        k = _Sealer(key)
        k1 = k.block("one")
        k2 = _Sealer(key).block("two", seq=2, prev="0" * 64)
        code, out = chain_dir("link", [("one", k1), ("two", k2)])
        check(code == 1 and "broken link" in out, "broken prev link -> error, exit 1")
        code, out = chain_dir("late", [("two", _Sealer(key).block("two", seq=2, prev="1" * 64))])
        check(code == 0 and "gap: seq 1 not found" in out, "a chain that starts after seq 1 -> warning")
        orphan_dir = os.path.join(root, "orphan")
        os.mkdir(orphan_dir)
        write(os.path.join(orphan_dir, "lost.seal"), _Sealer(key).block("lost"))
        check(quiet(["--chain", orphan_dir])[0] == 1, "a .seal without its .txt is invalid")

        # Seal v2: deckSha256 names the .deck beside the log; v1 seals of earlier builds stay valid.
        decks = os.path.join(root, "decks")
        os.mkdir(decks)
        deck = b'{"v":1,"deckGuid":null,"iterationCount":null,"name":null,"deckDefinitionId":"bnb-me3","size":2,"cards":{"sv8_174":2}}'
        d = _Sealer(key)
        with_deck = os.path.join(decks, "a.txt")
        write(with_deck, BOM + b"one\n\n" + d.block("one", deck=deck))
        os.mkdir(os.path.join(decks, DECKS_FOLDER))
        deck_file = os.path.join(decks, DECKS_FOLDER, hashlib.sha256(deck).hexdigest() + DECK_EXT)
        write(deck_file, deck)
        write(os.path.join(decks, "a2.txt"), BOM + b"one again\n\n" + d.block("one again", deck=deck))
        write(os.path.join(decks, "b.txt"), BOM + b"two\n\n" + d.block("two", version=1))
        write(os.path.join(decks, "c.txt"), BOM + b"three\n\n" + d.block("three"))
        code, out = quiet(["--chain", decks])
        check(code == 0 and "continuous" in out and out.count("(v2 footer, auto, deck,") == 2 and "(v1 footer" in out,
              "two v2 logs sharing one deck file, v1, and v2 without a deck: one valid chain")
        write(deck_file, deck.replace(b'"sv8_174":2', b'"sv8_174":3'))
        code, out = quiet([with_deck])
        check(code == 1 and "does not match deckSha256" in out, "an edited deck file is caught")
        os.remove(deck_file)
        code, out = quiet([with_deck])
        check(code == 0 and "deck file missing" in out, "a missing .deck leaves the log valid, and is reported")
        v1_keys = lambda j: j.replace('"v":2', '"v":1', 1)
        tampered("v-mismatch.txt", BOM + b"x\n\n" + _Sealer(key).block("x", edit=v1_keys), "v must be 2", "a v2 BEGIN line with v 1 is caught")
        no_deck_key = lambda j: j.replace(',"deckSha256":null', '', 1)
        tampered("v2-no-key.txt", BOM + b"x\n\n" + _Sealer(key).block("x", edit=no_deck_key), "not the v2 keys", "a v2 seal without deckSha256 is caught")
        v3 = _Sealer(key).block("x").replace(b"SEAL v2", b"SEAL v3")
        tampered("v3.txt", BOM + b"x\n\n" + v3, "does not know", "an unknown version is refused")

        check(quiet([])[0] == 2, "no arguments -> exit 2")
        check(quiet([os.path.join(root, "does-not-exist.txt")])[0] == 2, "missing FILE -> exit 2")
        check(quiet(["--chain", os.path.join(root, "nope")])[0] == 2, "missing DIR -> exit 2")
        check(quiet(["--bogus"])[0] == 2, "unknown option -> exit 2")
    finally:
        shutil.rmtree(root, ignore_errors=True)
    failed = checks.count(False)
    print("\nself-test: %d checks, %s" % (len(checks), "ALL PASSED" if not failed else "%d FAILED" % failed))
    return 0 if not failed else 1


def sig_line_of(path):
    with open(path, "rb") as f:
        return f.read().split(b"\n")[-3]


def respell(sig):
    """The same signature bytes in base64 with one unused bit of the last character set."""
    alphabet = b"ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/"
    data = sig.rstrip(b"=")
    return data[:-1] + bytes([alphabet[alphabet.index(data[-1]) ^ 1]]) + sig[len(data):]


def chain_hash_of(block):
    _, json_line, signature, _, _ = block.decode("ascii").split("\n")
    return chain_hash(json_line, signature)


def main():
    try:
        sys.stdout.reconfigure(errors="backslashreplace")
    except AttributeError:
        pass
    sys.exit(run(sys.argv[1:]))


if __name__ == "__main__":
    main()
