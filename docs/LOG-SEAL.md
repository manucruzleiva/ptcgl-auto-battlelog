# Sealed logs (seal format v1)

Every battle log the plugin saves carries a **seal**: one line of metadata about the match and the
log, signed with a key that belongs to the player's Windows account and chained to the previous
seal. The seal lets anyone check that a log has not changed since it was saved, and lets a future
online leaderboard follow a player's history of matches.

This document is the full specification. `docs/ARCHITECTURE.md` ("Sealed logs") holds the summary
that the code is tested against. The reference verifier is `tools/verify_log.py`.

Read the [threat model](#threat-model) before relying on a seal. A seal made on the player's own
computer cannot prove that a match took place.

## 1. Identity

| | |
|---|---|
| Key | RSA, 2048-bit modulus, public exponent 65537. One key per Windows user. Verifiers accept moduli of 2048 to 4096 bits and only the exponent 65537 (§6), which bounds the cost of checking a seal whatever a file claims. |
| Private key file | `%LOCALAPPDATA%\PtcglAutoBattleLog\identity\signing-key.xml`, the .NET `RSA.ToXmlString(true)` format. It is protected only by the user profile's file permissions. |
| Chain state | `%LOCALAPPDATA%\PtcglAutoBattleLog\identity\chain.json`: `{"seq":N,"last":"<64 hex>","keyId":"<keyId>"}`, or `{"seq":0,"last":null,"keyId":"<keyId>"}` before the first seal. A file without `keyId` (written by an earlier build) is read as the current key's. |
| `keyId` | The [RFC 7638](https://www.rfc-editor.org/rfc/rfc7638) JWK thumbprint of the public key. |

* The key is created the first time the plugin needs it. Creation runs on a background thread,
  started when the game starts, because Mono's managed RSA takes several seconds to generate 2048
  bits. If a log has to be saved while the key is still being made, which is only possible in the
  first seconds after the game starts, the save waits up to 3 seconds and then saves the log
  without a seal.
* The key never leaves the computer. The plugin sends nothing over the network.
* **keyId**: take the public key as a JWK whose members are base64url without padding and use the
  big-endian bytes of each integer with no leading zero bytes. Then compute
  `base64url(SHA-256(UTF-8('{"e":"' + e + '","kty":"RSA","n":"' + n + '"}')))`, again without padding.
  With RFC 7638's example key this gives `NzbLsXh8uDCcd-6MNwXF4W_7noWXFZAfHkxZsRGC9Xs` (this
  value is tested).
* If `signing-key.xml` cannot be used (it does not parse, has no private part, has fewer than 2048
  or more than 4096 bits, an exponent other than 65537, or fails a test signature), it is renamed
  to `signing-key.xml.corrupt` and a new key is made. A new key starts a new chain at `seq` 1.
* At every load the plugin checks `chain.json` against the logs folder:
  * Missing, corrupt (kept as `chain.json.corrupt`) or naming another `keyId`: it is rebuilt from
    the newest seal of this key in the logs folder, and the chain continues after that seal.
  * Present: the logs written since about ten minutes before `chain.json` itself are read. A seal
    of this key there with a higher `seq`, or with the same `seq` and a chain hash `chain.json` does
    not name (a log re-sealed in place), becomes the latest seal. That repairs a `chain.json` that
    fell behind because its write failed and the game exited before the retry, or because the game
    died between writing a log and `chain.json`.
  * Only a seal whose public key is this key and whose signature, in its canonical base64 spelling
    (§4), verifies counts. A `keyId` is public, so anyone could put a file into the logs folder
    that claims it with any `seq`.
  * If the newest logs are no longer in that folder, the next seal reuses a `seq`, which a
    verifier reports as a fork.
* A new Windows account, a new computer, or a deleted `identity` folder means a new key and a
  new chain. Copying the `identity` folder to another computer moves the identity. If both
  computers then keep sealing with the copy, the result is a fork.

## 2. Where the seal goes

The BepInEx config `BepInEx\config\ptcgl.autobattlelog.cfg` chooses the layout in `[Metadata]`
`Mode`:

| Mode | `.txt` file | `.seal` file |
|---|---|---|
| `Footer` (default) | `EF BB BF` + log + `0A 0A` + block | none |
| `Sidecar` | `EF BB BF` + log | block. Its name is the `.txt` name with the extension `.seal`, e.g. `2026-10-19 21.34.05 - VICTORY vs Gary.seal`. |
| `Off` | `EF BB BF` + log | none. The chain does not advance. |

* **log**: the exact text the game's Export button produces, encoded as UTF-8. Line endings and
  everything else are kept as the game produced them. A lone UTF-16 surrogate in that text (not
  expected in practice) is written as U+FFFD. `logSha256` and `logChars` describe the bytes that
  are in the file.
* **block**: ASCII only, at most 16 KiB (16384 bytes; a plugin seal is about 1.5 KB), exactly five
  `\n`-separated parts with nothing after the last `\n`:

  ```
  -----BEGIN PTCGL-ABL SEAL v1-----\n
  <metadata JSON, one line>\n
  <signature, standard base64 with padding, one line>\n
  -----END PTCGL-ABL SEAL v1-----\n
  ```

* **How a verifier finds the log** (in this order):
  1. Read the `.txt` bytes and drop a leading `EF BB BF` if there is one.
  2. If the bytes contain `\n\n-----BEGIN PTCGL-ABL SEAL v1-----\n`, the log is everything before
     the **last** occurrence, and the block is everything after its first two bytes (`\n\n`). The
     last occurrence is used so that a log which happens to quote a seal still verifies.
  3. Otherwise, if `<name>.seal` exists, the log is all of the `.txt` bytes and the block is the
     `.seal` file (a leading BOM there is ignored).
  4. Otherwise the log is unsealed.
* In `Sidecar` mode, a log whose own text contains the separator line (not expected in practice)
  is sealed with a footer instead, so the rule above still finds its seal.
* Each time a log is rewritten (a longer read, a late result), its seal is written again with it.
  Switching modes leaves no stale seal: rewriting a file in `Footer` or `Off` mode deletes its old
  `.seal`. Moving a file to its corrected name moves its seal too.
* If anything about sealing fails, **the log is still saved**, without a seal. A warning goes to
  `BepInEx\LogOutput.log` and to the `message` in `plugin-status.json`. Possible failures: no key,
  a signing error, a signature that does not verify against its own key (it is never written), or
  a `.seal` file that cannot be written (the log then has no seal, rather than a stale one).

## 3. Metadata JSON

The JSON is one line with no spaces and no line breaks. It has exactly the following keys, in this
order. Nested objects also keep the order shown. Every number is an integer, and dates are UTC
`yyyy-MM-ddTHH:mm:ssZ`. The line is pure ASCII:

* `"` and `\` are escaped as `\"` and `\\`.
* Control characters are escaped as `\n`, `\r`, `\t`, `\b` and `\f`, or as `\u00xx` for the rest.
* Every character from U+007F up is escaped as `\uxxxx` in lowercase hex. A character outside the
  BMP becomes two escaped surrogates.

**Verifiers check the signature over the exact bytes of the line and must never re-serialize it.**

| # | Key | Type | Meaning |
|---|---|---|---|
| 1 | `v` | `1` | Format version. |
| 2 | `app` | `"PTCGL Auto Battle Log"` | |
| 3 | `pluginVersion` | string | Version of the plugin that sealed the log. |
| 4 | `gameVersion` | string or null | Unity `Application.version` of the game client. |
| 5 | `capture` | `"auto"` or `"export"` | `auto`: the plugin read the log itself (at game over, a few seconds later, or at teardown). `export`: the sealed text came from the player pressing Export. That is always the case for `EXPORTED`, and also when a manual export of a tracked match was longer than the plugin's own read. |
| 6 | `matchId` | string or null | The client's match id. Null for `EXPORTED`. |
| 7 | `startedUtc` | date | When the plugin first saw the match. For `EXPORTED`, the time of the click. |
| 8 | `endedUtc` | date or null | When the plugin saw game over, or the match being torn down. Null for `EXPORTED`. |
| 9 | `result` | `VICTORY`, `DEFEAT`, `NO-RESULT`, `EXPORTED` | As in the file name. |
| 10 | `winner` | 1, 2 or null | The winner's seat, as reported by the client. |
| 11 | `mySeat` | 1, 2 or null | The sealing player's seat. |
| 12 | `player` | `{"name", "accountHash"}` | The sealing player: display name, and the hash of their account id. |
| 13 | `opponent` | `{"name", "accountHash"}` | The opponent, same fields. |
| 14 | `deck` | string or null | The sealing player's deck name. |
| 15 | `logSha256` | 64 lowercase hex | SHA-256 of the log bytes. |
| 16 | `logChars` | integer | The log's length in UTF-16 code units, which is the .NET string length. |
| 17 | `sealedUtc` | date | When the seal was made (the computer's clock). |
| 18 | `seq` | integer ≥ 1 | Position in this key's chain. |
| 19 | `prev` | 64 lowercase hex or null | Chain hash of the previous seal. Null exactly when `seq` is 1. |
| 20 | `keyId` | base64url | RFC 7638 thumbprint of `publicKey`. |
| 21 | `publicKey` | `{"kty":"RSA","n","e"}` | The signing key: base64url, no padding, no leading zero bytes. |

`accountHash` is the lowercase hex SHA-256 of the UTF-8 string `"ptcgl-abl:v1:" + accountId`, or
null when the client did not give an account id. Both players' seals of one match name the same two
hashes, with player and opponent swapped. This lets the two seals be matched without putting raw
account ids in the files.

Example (wrapped here, one line in the file):

```
{"v":1,"app":"PTCGL Auto Battle Log","pluginVersion":"1.1.0","gameVersion":"1.24.0","capture":"auto",
"matchId":"sample-1","startedUtc":"2026-10-19T20:00:00Z","endedUtc":"2026-10-19T20:11:00Z","result":"VICTORY",
"winner":1,"mySeat":1,"player":{"name":"Ash","accountHash":"f824fc06…"},"opponent":{"name":"Gary",
"accountHash":"be4f4101…"},"deck":"Charizard ex","logSha256":"f73f2b04…","logChars":65,
"sealedUtc":"2026-10-19T20:11:09Z","seq":1,"prev":null,"keyId":"E34E3c57…","publicKey":{"kty":"RSA",
"n":"28DR3rx2…","e":"AQAB"}}
```

## 4. Signature

RSASSA-PKCS1-v1_5 ([RFC 8017](https://www.rfc-editor.org/rfc/rfc8017) §8.2) with SHA-256, computed
over the bytes of the JSON line, with no line feed. The signature line is the standard base64
(`+/`, with `=` padding) of the `k`-byte signature, where `k` is the modulus length in bytes (256
for a 2048-bit key).

The signature line must be **canonical**: exactly what encoding those `k` bytes gives (.NET's
`Convert.ToBase64String`, Python's `base64.b64encode`), so the unused low bits of its last
character are zero. Most base64 decoders ignore those bits, which gives one signature several
spellings; since the chain hash (§5) covers the line's text, accepting them would let anyone who
holds a shared log produce a second, "different" seal at the same `seq` without the key. A
verifier re-encodes the decoded bytes and rejects the seal unless the result is the line itself.

The plugin uses `RSACryptoServiceProvider.SignHash(hash, "2.16.840.1.101.3.4.2.1")`, which exists
on Unity's Mono. It checks every signature with `VerifyHash` before writing it.

## 5. Chain

* **Chain hash** of a seal: the lowercase hex of
  `SHA-256(<JSON line bytes> + 0x0A + <signature line bytes>)`.
* The first seal of a key has `seq` 1 and `prev` null. Each new seal takes `seq` = the latest seal's
  `seq` + 1 and `prev` = the latest seal's chain hash.
* **Re-sealing** happens when the same match's file is written again: a longer read of the log, a
  result or opponent that arrived late, or the file moved to its corrected name.
  * If the file's current seal is still the latest seal of the chain, the new seal takes its place,
    with the same `seq` and `prev`. The old seal is overwritten and no longer exists anywhere.
  * Otherwise (another log was sealed in between), the new seal takes the next `seq`. The file's
    old `seq` then exists nowhere, and a verifier reports a **gap** there. This is expected after,
    for example, pressing Export on the results screen of a match after the next match has already
    been sealed.
  * If the file has to move to a new name and the old file cannot be moved because it is locked,
    the old file keeps its seal and the new file takes the next `seq`. The two files never claim the
    same place.
* `chain.json` is updated atomically (`.tmp` + replace) right after the log, and its `.seal`, have
  been written. A log whose write failed does not advance the chain. If `chain.json` cannot be
  written, the chain continues in memory, and the file is written again every few seconds (with
  the plugin's heartbeat), at the next seal and when the game exits. If all of that fails, the next
  start finds the newer seal in the logs folder (§1).
* `Off` and the unsealed fallback do not advance the chain.

What a verifier reports for one `keyId`:

| Finding | Severity | Ordinary causes |
|---|---|---|
| Gap: a `seq` is missing | warning | An out-of-order re-seal, deleted logs, logs kept in another folder, a seq skipped after a file-move failure. |
| The same seal (same chain hash) in two files | warning | A copied file. |
| Fork: two different seals with one `seq` | error | The `identity` folder copied to a second computer, `chain.json` restored from an old backup, or rebuilt or carried on while the newest logs were not in the logs folder (moved away, or the logs folder changed while `chain.json` could not be written), or manipulation. |
| Broken link: seal `seq` N+1 whose `prev` is not the chain hash of seal `seq` N | error | Manipulation, or seals from two diverged copies of a key. |

## 6. Verifying

### With the reference verifier

In PowerShell (Windows' default terminal), from a copy of this repository, with the logs folder
that the tray menu's **Open logs folder** shows (it may be in OneDrive, or a folder you chose):

```
py tools\verify_log.py "2026-10-19 21.34.05 - VICTORY vs Gary.txt"
py tools\verify_log.py --chain "$env:USERPROFILE\Documents\PTCGL Battle Logs"
py tools\verify_log.py --key NzbLsXh8uDCcd-6MNwXF4W_7noWXFZAfHkxZsRGC9Xs "2026-10-19 21.34.05 - VICTORY vs Gary.txt"
py tools\verify_log.py --self-test
```

(`NzbLsXh8...` stands for the player's own `keyId`: the `key` that `OK` lines print for their logs.)
In `cmd.exe` the folder is `"%USERPROFILE%\Documents\PTCGL Battle Logs"`; on Linux and macOS use
`python3 tools/verify_log.py ...`.

* The verifier needs Python 3.8+ and nothing else. It does the RSA check itself with `pow()`.
* It prints one line per file (`OK` with the seal's full `keyId`, `INVALID` with the reason, or
  `UNSEALED`), the chain findings, and a summary. Names and reasons taken from a file or a file
  name are printed with control and format characters escaped as `\uXXXX`, so a crafted log
  cannot change what the output looks like.
* `--key KEYID` (repeatable) also requires every seal to be made with one of these keys; a valid
  seal made with any other key is reported as `INVALID`. Without it, a seal only proves that the
  file has not changed since *someone* sealed it (see the [threat model](#threat-model)).
* Files over 16 MiB, `.seal` files and seal blocks over 16 KiB, and keys outside 2048-4096 bits or
  with an exponent other than 65537 are rejected before any signature arithmetic.
* Exit codes: `0` all valid, `1` something invalid, `2` usage error.
* A file given by name that has no seal counts as invalid. In `--chain` mode, unsealed logs (from
  before 1.1, or saved with `Mode = Off`) are listed but are not errors.
* `.seal` files without their `.txt` are invalid.

### Pseudo-code

```
verify(txt):
    data  = bytes(txt); if data starts with EF BB BF: data = data[3:]
    i     = last index of "\n\n-----BEGIN PTCGL-ABL SEAL v1-----\n" in data
    if i >= 0:                 log = data[:i];  block = data[i+2:]
    elif exists(stem + ".seal"): log = data;    block = bytes(stem + ".seal") minus a leading BOM
    else:                      return UNSEALED
    require len(block) <= 16384
    lines = ascii(block).split("\n")              # non-ASCII -> INVALID
    require len(lines) == 5 and lines[0] == BEGIN and lines[3] == END and lines[4] == ""
    J, S  = lines[1], lines[2]
    meta  = json_parse(J)                         # reject duplicate keys and non-integers
    require keys(meta) == [v, app, …, publicKey] in order, nested keys in order, field types as in §3
    require (meta.seq == 1) == (meta.prev == null)
    n, e  = b64url_uint(meta.publicKey.n), b64url_uint(meta.publicKey.e)   # canonical, no leading 0x00
    require 2048 <= bits(n) <= 4096 and e == 65537
    require meta.keyId == b64url(sha256('{"e":"' + meta.publicKey.e + '","kty":"RSA","n":"' + meta.publicKey.n + '"}'))
    sig   = base64_decode_strict(S); k = byte_length(n)
    require base64_encode(sig) == S               # canonical: one spelling per signature (§4)
    require len(sig) == k and int(sig) < n
    EM    = I2OSP(int(sig)^e mod n, k)
    require EM == 00 01 || FF × (k − 54) || 00 || 3031300d060960864801650304020105000420 || sha256(J)
    require hex(sha256(log)) == meta.logSha256
    require utf16_length(utf8_decode_strict(log)) == meta.logChars
    if keys were given: require meta.keyId in keys  # the player's known key (--key)
    return VALID, chain_hash = hex(sha256(J + "\n" + S))

chains(valid seals):
    for each keyId, group by seq:
        two different chain hashes at one seq           -> ERROR fork
        seal at seq N+1 whose prev ∉ hashes at seq N    -> ERROR broken link
        missing seq between the lowest and the highest, or lowest > 1 -> WARNING gap
```

`EM` is compared as a whole with the single valid encoding, in constant time. The verifier never
parses the ASN.1 inside a signature, so lenient parsers cannot be exploited (Bleichenbacher-style
forgeries rely on them).

## Threat model

**Who controls what.** The player controls the computer, the game client, the plugin, and the
private key in `signing-key.xml`. The plugin is open source. Anyone can build a copy that signs
whatever they want, or read the key file and sign by hand. Hiding or obfuscating the key in an
open-source client would not change this. The key is not kept in hardware, and there is no
attestation.

**What a seal cannot do.**

* It cannot prove that a match happened, that the log is what the game produced, or that the
  result is true. A player who wants to cheat can write a log and seal it with their own key.
* It cannot stop a player from starting over. A new key costs nothing and gives a clean chain.
* It does not hide anything. Logs and seals are plain text. `accountHash` is a pseudonym, not
  encryption: anyone who knows an account id can compute its hash and recognize it in a seal.

**What a seal does give.**

* **Integrity after sealing, relative to a known key.** Any change to the log or the metadata
  after the seal was made is detected, as long as the seal is checked against the `keyId` the
  reader expects. A seal on its own carries its public key, so anyone who edits a log can seal it
  again with a key of their own, and that new seal verifies. Checked with the player's known
  `keyId` (`verify_log.py --key <keyId>`, or a server's key registration below), the seal covers
  an accidental edit, a log "cleaned up" by hand, a file damaged in transit, and someone else
  editing and re-sealing a log that a player shared. It turns "this file was edited" from a
  suspicion into a fact. Compare the full `keyId`, not a prefix of it.
* **A per-install identity.** All seals made with one key come from the same Windows account (or
  from whoever copied its key). A server can attach a reputation to a key, see when a player
  switches keys, and require a key to be registered once (see TOFU below).
* **Ordering and gap evidence.** `seq` and `prev` order a key's seals. If a player deletes a bad
  result after reporting it, the chain shows a gap. If a player rewrites a sealed result and keeps
  the chain, the chain shows a fork or a broken link. On its own this is evidence, not proof: the
  player could re-seal a whole edited chain. It becomes binding only when seals are committed to a
  server as they happen (below).
* **Material for two-party checks.** Both players of a match produce independent seals that name
  the same match and the same two accounts. Faking a match that both sides corroborate requires
  the cooperation of the opponent, or a second account with its own history.

In short, the seal is a tamper-evident envelope plus a pseudonymous, ordered history. Trust in a
leaderboard has to come from the server-side checks below.

## Server-side verification guide (future leaderboard)

The plugin uploads nothing. The tray app sends sealed logs only to tcgkb.app, and only when the
player connects a tcgkb account and claims a PTCGL account (see [TCGKB-API.md](TCGKB-API.md)).
Pokematch uploads ([POKEMATCH-API.md](POKEMATCH-API.md)) send no log text and no seal, only the deck, result and date.
This section is guidance for the server side, or any online leaderboard, that accepts sealed logs.

1. **Verify every submission exactly as in §6**, with the exact bytes received, including the size
   and key bounds (checked before any RSA arithmetic, so a crafted submission cannot tie up the
   server) and the canonical signature encoding. Reject anything that fails. Store the raw `.txt`
   and seal so that every decision can be re-checked later.
2. **Register keys trust-on-first-use (TOFU), one per player.**
   * The first valid submission from a player registers its `keyId` for that player.
   * Later submissions must use that key. A key change, such as a new computer or a lost profile,
     needs an explicit re-registration that is rate-limited and visible, for example a cooldown, a
     note on the player's profile, and the old key's history kept.
   * The same key for two players, or one player cycling through keys, is a red flag.
   * Binding a key to a game account is the weak point: the seal says which `accountHash` the client
     reported, but anyone can write that field. Rely on corroboration (next point) more than on
     the binding.
3. **Corroborate with the other player, by `matchId`.** A match is *verified* only when both
   players' seals, under two different registered keys, agree:
   * `matchId` is equal.
   * The two `accountHash` pairs mirror each other: A's player is B's opponent, and the reverse.
   * Results are complementary: `VICTORY` against `DEFEAT`, or both `NO-RESULT`. `winner` is equal,
     and the two `mySeat` values differ.
   * The logs agree. If the Export text turns out to be identical from both seats, `logSha256`
     must be equal. If it is written from each player's perspective, compare a normalized form
     of the public events: turns, attacks, knockouts, prizes and the final line. Measure this on
     real pairs before relying on it.
   * `startedUtc` and `endedUtc` agree within a few minutes, and `gameVersion` is the same.
4. **Commit immediately at match end.** The strongest protection against rewriting history is to
   receive each seal, or at least its chain hash, `seq` and `keyId`, within minutes of `endedUtc`.
   The server then holds each key's chain head itself:
   * A late submission, a `seq` that does not follow the head, or a fork is visible at once and
     cannot be repaired afterwards.
   * Record the time each seal was received, and reject or flag seals whose `sealedUtc` is far
     from that time (allow for clock skew).
   * A `capture` of `export` long after `endedUtc` deserves less trust than an `auto` capture
     sealed seconds after game over.
5. **Plausibility checks.**
   * `endedUtc − startedUtc` is in a realistic range for the number of turns in the log.
   * `sealedUtc ≥ endedUtc`, by seconds for an `auto` capture.
   * No dates in the future.
   * `pluginVersion` is a released version, and `gameVersion` was live on that date.
   * `result`, `winner` and `mySeat` are consistent with each other and with the last lines of the
     log (who conceded, who took the last prize card).
   * The deck in the log is legal in the format.
   * `logChars` and the log length are sane.
6. **Rate limits.** Limit submissions per key, per account and per IP address. Limit new keys per
   account and per day. Flag a number of matches per hour that a human cannot play, or matches
   that overlap in time under one account.
7. **Single-sided matches.** When only one player submits (the opponent does not use this tool),
   the match is **unverified**. Show it as unverified, keep it out of rankings or give it a reduced
   weight, and upgrade it if the other side's seal arrives later. Never treat a single seal as proof.
8. **Privacy and consent.**
   * Upload only after the player has explicitly opted in, in the tray app, with a clear
     explanation. The plugin sends nothing; the tray's tcgkb claim is the only upload, and
     `DISCLAIMER.md` says so (version 2). Any new upload feature must change that text and ask
     again (`Disclaimer.Version`).
   * A log contains the opponent's display name and their plays. Publish only what the
     leaderboard needs, and show opponents' names only for players who also opted in.
   * Use `accountHash` as the join key and never ask for raw account ids. It is specific to this
     project, so it does not match plain hashes of the same ids elsewhere. It is not a secret,
     though, so do not publish it next to names that would make it a lookup table.
   * Keep data for a limited time, and let players delete their submissions and their key
     registration.
9. **Revocation.** Let a player revoke a key (for example after a lost or shared profile).
   Submissions under a revoked key after the revocation time are rejected. Earlier ones keep
   whatever status they had.

## Versioning

`v` in the JSON and `v1` in the BEGIN/END lines change together. A verifier must reject versions it
does not know. A new version may add keys or change their order, so v1 verifiers must not accept a
v2 seal.
