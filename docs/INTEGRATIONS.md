# Integrating a site with PTCGL Auto Battle Log

This guide is for people who run a site (a leaderboard, a deck tracker, a match journal, a team
tool) and want to receive the battle logs that PTCGL Auto Battle Log (PAB) captures. It describes
what PAB has, how the two sites it already talks to receive it, and what a new site would need.

Players who only want to install and use the app should read the quick-setup guide in
[docs/quick-setup/](quick-setup/) and the [README](../README.md) instead.

> **Status.** Today PAB uploads to two fixed destinations, tcgkb ([TCGKB-API.md](TCGKB-API.md)) and
> Pokematch ([POKEMATCH-API.md](POKEMATCH-API.md)). Their hosts are compiled into the app. A third
> site cannot be added by configuration yet: §6 proposes how that would work. Until then, a site
> can (a) implement one of the two contracts below so the change in PAB is small, and (b) open an
> issue in this repository to ask for it.

## 1. What PAB produces

Everything starts on the player's computer. The BepInEx plugin inside the game saves each match
to the logs folder (default `Documents\PTCGL Battle Logs`); it never touches the network. Only the
tray app uploads, and only after the player turns an upload on.

| Item | What it is | Spec |
|---|---|---|
| Battle log `.txt` | The exact text of the game's own **Export** button, UTF-8 with BOM, named `yyyy-MM-dd HH.mm.ss - RESULT vs Opponent.txt` (`VICTORY`, `DEFEAT`, `NO-RESULT`, `EXPORTED`). | [ARCHITECTURE.md](ARCHITECTURE.md), "Logs folder" |
| Seal | One signed line of metadata per log, at the end of the `.txt` (`Footer`, default) or in a `.seal` sidecar. RSA-2048 key per Windows user, chained by `seq`/`prev`. | [LOG-SEAL.md](LOG-SEAL.md) |
| Deck file | `decks\<sha256>.deck`: the player's own decklist as PTCGL card ids → counts, stored once per distinct deck, named by the seal's `deckSha256` (seal v2). | [LOG-SEAL.md §2a](LOG-SEAL.md#2a-deck-file) |

The seal's metadata (v2) is what a site usually wants to index:

| Field | Use |
|---|---|
| `matchId` | The client's match id: the key to de-duplicate, and to pair the two players' uploads of one match. Null for `EXPORTED` logs. |
| `result`, `winner`, `mySeat` | Outcome from the uploader's side. |
| `player`, `opponent` | `{"name","accountHash"}`. `accountHash` = SHA-256 of `"ptcgl-abl:v1:" + accountId`, a stable pseudonym; names can repeat and change. |
| `deck`, `deckSha256` | The player's deck name, and the hash of its deck file. |
| `queue`, `format` | As the client names them, e.g. `Ranked`/`Casual`/`Friend`, `Standard`/`Expanded`. Treat as open lists. |
| `startedUtc`, `endedUtc`, `sealedUtc`, `capture` | Timing, and whether the plugin read the log itself (`auto`) or it came from Export. |
| `pluginVersion`, `gameVersion` | For plausibility checks and compatibility. |
| `keyId`, `seq`, `prev`, `publicKey` | The signing identity and its chain. |

## 2. Choose an integration model

PAB supports two models today. Both are opt-in per player, and both keep the credential encrypted
for the Windows user (DPAPI) on the player's computer.

| | **A. API key** (like Pokematch) | **B. Account sync** (like tcgkb) |
|---|---|---|
| The player… | makes a token on your site and pastes it into the tray | clicks **Connect**, approves a short code in the browser, signed in to your site as usual |
| Your site implements | Bearer-token auth and one "add match" endpoint | OAuth 2.0 device authorization grant (RFC 8628), refresh/revoke, then the endpoints you need |
| What you receive | A summary you define (deck name, result, date, tags): no log text | The sealed log exactly as on disk, plus the deck file |
| You can verify | Nothing: you trust the token's owner | The seal, the player's key (TOFU), and corroboration by the opponent's upload |
| Good for | Personal journals, stat trackers | Leaderboards, anything that ranks or compares players |
| Reference | [POKEMATCH-API.md](POKEMATCH-API.md) | [TCGKB-API.md](TCGKB-API.md) |

You can mix them: an API key that receives the full sealed log (model A transport with model B's
payload, §4.2) is the simplest way to get verifiable data, at the cost of a token the player pastes.

## 3. Model A: API key

The reference is the Pokematch client (`src/Tray/Logic/PokematchApi.cs`, `PokematchUploader.cs`,
tests in `tests/TrayTests/PokematchTests.cs`). What PAB expects from such a site:

* **Token.** Made by the player on your site, revocable there. PAB accepts printable ASCII without
  spaces, at most 512 characters; a pasted `Bearer ` prefix is dropped. Tell players where to make
  it (a page URL PAB can open).
* **Requests.** `Authorization: Bearer <token>`, `Content-Type: application/json`,
  `Accept: application/json`, `User-Agent: PtcglAutoBattleLog/<version>`. HTTPS only. PAB follows
  no redirects (the token must go nowhere else), sends no cookies, reads at most 1 MiB of response.
* **Check endpoint** (`GET /api/v1/me` on Pokematch): any 2xx means the token is good; 401 means it
  is refused and PAB does not keep it.
* **Add** (`POST`): any 2xx; return the new record's id as `_id` or `id` (top level, or under
  `match` or `data`) so PAB can update it later.
* **Update** (`PUT /<id>`): PAB re-sends a match whose summary changed (a result corrected after a
  longer read). 404/410 means the player deleted it on your site: PAB never sends it again.
* **Answers PAB understands.** 401/403: token revoked, PAB pauses uploads and asks for a new one.
  408, 429, 5xx, timeouts: retried later (PAB waits 30 minutes). Any other 4xx: that one match is
  recorded as refused, and is sent again only if its content changes. An error body
  `{"message": "…"}` (or `error`) is shown to the player.
* **Volume.** At most 25 matches per run; runs about 2 minutes after each saved match and every 10
  minutes. Only `VICTORY` and `DEFEAT` logs, not written in the last 2 minutes.
* **De-duplication** is done by PAB (`pokematch-uploads.json`: per match key, your id and the hash
  of what was sent). Your site should still tolerate a repeat.

The Pokematch body is Pokematch's own format; a new site would define its own (see §4).

## 4. Model B: account sync

The reference is the tcgkb client (`src/Tray/Logic/TcgkbApi.cs`, `TcgkbConnection.cs`,
`LogUploader.cs`, tests in `tests/TrayTests/TcgkbTests.cs`). [TCGKB-API.md](TCGKB-API.md) is the
full contract; this is what a new site would reuse.

### 4.1 Connecting (OAuth 2.0 device flow)

* `client_id` is `ptcgl-auto-battlelog`, a public client (no secret). Scopes are yours to define;
  tcgkb uses `claims:write logs:upload`.
* `POST /api/device/code` (form: `client_id`, `scope`, `device_name` = Windows computer name, ≤ 64
  chars) → `device_code`, `user_code` (`A-Z0-9-`, ≤ 16), `verification_uri`(`_complete`),
  `expires_in`, `interval`. PAB opens the verification page **only if it is on your own host**.
* `POST /api/device/token` with `grant_type=urn:ietf:params:oauth:grant-type:device_code` polled
  until `200 {access_token, token_type, expires_in, refresh_token, scope}`, with the RFC 8628
  errors `authorization_pending`, `slow_down`, `access_denied`, `expired_token`.
* Refresh with `grant_type=refresh_token` and rotate the refresh token; `invalid_grant` disconnects.
  Revoke with `POST /api/device/revoke` (RFC 7009).
* After connecting, PAB calls one authenticated endpoint to get the user's display name and saves
  the connection only if it succeeds.
* Errors: non-2xx with `{"error":"<code>","message":"<text>"}`. 401 → PAB refreshes once and retries.

Let users see and revoke connected computers on their profile.

### 4.2 Receiving matches

PAB's upload body (`POST /api/ptcgl/logs` on tcgkb):

```json
{"fileName":"2026-10-19 21.34.05 - VICTORY vs Gary.txt","log":"<base64>","seal":null,"deck":"<base64>"}
```

* `log`: the `.txt` bytes exactly as on disk (BOM, text, footer seal). `seal`: the `.seal` sidecar
  bytes, or null. `deck`: the deck file the seal names, sent once per distinct deck, or absent.
  Each file ≤ 1 MiB (deck ≤ 64 KiB).
* Only tracked matches (`matchId` set, result not `EXPORTED`), newest first, at most 50 per run.
* PAB de-duplicates by `matchId|accountHash|keyId` (`tcgkb-uploads.json`). Answer a repeat with
  2xx (`"added": false`), not an error.
* Refusals: a `4xx` with a code. PAB treats "this file is bad" (`422 invalid_log`) and "this
  account is someone else's" (`409 claimed_by_other`) as final for that file, `403 not_claimed`
  as "try later", anything else as "stop this run".
* PAB sends the logs of every PTCGL account that played on the computer: the seal's `player` is
  the local account, never the opponent. tcgkb ties a new `accountHash` to the user on its first
  upload.

tcgkb also has an explicit **claim** endpoint (the user proves a PTCGL `accountHash` is theirs by
sending 1–3 sealed logs; PAB no longer uses it) and a deck API. Neither is needed to receive logs; a site that ranks players should
read [TCGKB-API.md](TCGKB-API.md), "Model", before designing its own.

### 4.3 Verifying what you receive

A seal proves the file did not change after it was sealed, by a given key. It does **not** prove
that the match happened: the player controls the key. Follow
[LOG-SEAL.md, "Server-side verification guide"](LOG-SEAL.md#server-side-verification-guide-future-leaderboard):

1. Verify every file exactly as §6 there (size and key bounds first, canonical signature, the
   exact JSON bytes, `logSha256`, `logChars`, the deck hash). `tools/verify_log.py` is the
   reference; port it, and run its `--self-test` vectors against your port.
2. Register the player's `keyId` trust-on-first-use; make key changes visible.
3. Treat a match as verified only when the opponent's seal for the same `matchId`, under another
   key, mirrors it.
4. Rate-limit per key, account and IP; store the raw bytes so decisions can be re-checked.

## 5. Rules for any destination

These hold for tcgkb and Pokematch and would hold for any new site; they come from the project's
goal and [DISCLAIMER.md](../DISCLAIMER.md).

* **Opt-in, off by default.** Nothing is sent until the player sets a token or connects, and turns
  the upload on. Every new kind of upload changes `DISCLAIMER.md` and bumps `Disclaimer.Version`, so
  every player is asked again.
* **Send the minimum.** Model A sends no names, no `accountHash`, no log text. Model B sends the
  log, which contains the opponent's display name: show opponents' names only for players who also
  opted in, and never publish `accountHash` next to names.
* **One host per credential.** HTTPS, no redirects, no cookies, bounded responses and timeouts.
* **Deletion.** Let players delete their uploads and revoke tokens or computers on your site.
* **No gameplay effect.** PAB never automates play. A site must not ask for anything that would.
* **Tell players what you keep**, and for how long.

## 6. Proposal: configurable third-party destinations in PAB

Not implemented. This is what PAB would need so a new site does not require a code change per site.

### 6.1 A destination descriptor

A small JSON document per site, shipped with the app (an allow-list reviewed in this repository)
rather than typed in by players, so a phishing site cannot be added with one paste. `auth` is
`apiKey` (model A) or `deviceFlow` (model B):

```json
{
  "id": "example",
  "name": "Example Leaderboard",
  "baseUrl": "https://example.gg",
  "auth": "apiKey",
  "tokenPage": "https://example.gg/settings/tokens",
  "checkPath": "/api/pab/me",
  "deviceFlow": { "codePath": "/api/device/code", "tokenPath": "/api/device/token",
                  "revokePath": "/api/device/revoke", "scope": "logs:upload" },
  "upload": { "path": "/api/pab/matches", "payload": "sealedLog", "results": ["VICTORY","DEFEAT"],
              "sendDeck": true, "maxPerRun": 50 },
  "privacyUrl": "https://example.gg/privacy"
}
```

`payload` is one of two fixed shapes PAB already knows how to build: `sealedLog` (§4.2) or
`summary` (a neutral JSON from the seal: `matchId`, `result`, `deck`, `queue`, `format`, local
date, `pluginVersion`; no names or hashes). Site-specific bodies like Pokematch's stay in code.

### 6.2 Changes in the tray

| Area | Today | Needed |
|---|---|---|
| HTTP clients | `TcgkbApi.BaseUrl`, `PokematchApi.BaseUrl` constants; one `HttpClient` per site | A `DestinationClient` built from a descriptor, keeping the same rules (its host only, no redirects, 1 MiB, timeouts, `User-Agent`) |
| Auth | `TcgkbConnection` (device flow), `PokematchTokenStore` (token) | The same two, parameterised by descriptor; credentials in `destinations\<id>.dat` (DPAPI) |
| Upload loop | `LogUploader`, `PokematchUploader`, each with its own state file | One uploader per destination, state in `destinations\<id>-uploads.json`; the existing de-dup keys and retry rules |
| Settings | `tcgkbAutoUpload`, `pokematchAutoUpload`, `pokematchSinceUtc` | `destinations: { "<id>": { "autoUpload", "sinceUtc" } }` (old fields migrated) |
| Menu / status window | A fixed submenu per site | One generated submenu per destination: Connect/Set token, automatic upload, upload now, last result, disconnect |
| Consent | `DISCLAIMER.md` lists each site | A generic section describing both payloads, plus a per-destination confirmation naming the site, its privacy page and the payload |
| Tests | Fakes of tcgkb and Pokematch | A fake destination driven by descriptors, for both auth models and both payloads |

tcgkb and Pokematch could stay as they are, or become the first two descriptors once the generic
path is proven.

### 6.3 Open questions

* Allow-list only, or also a "developer mode" that accepts a local descriptor file for testing a
  site before it is listed?
* Should the `summary` payload include `accountHash` for sites that want to group by account
  without receiving logs? (Privacy cost: it is a stable pseudonym.)
* Who reviews a new site (privacy page, deletion, HTTPS) before it is added to the list?

## 7. Getting listed

Open an issue in this repository with your site, the model (A or B), the endpoints you implement,
your privacy and deletion policy, and a test account or token. Until §6 exists, adding a site is a
code change in the tray app, reviewed like any other.
