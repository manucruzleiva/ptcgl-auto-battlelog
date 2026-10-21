# tcgkb API (client side)

The tray app can connect to a [tcgkb.app](https://tcgkb.app) account and claim the PTCGL
accounts its player uses. This is the contract between the tray (`src/Tray/Logic/TcgkbApi.cs`) and
tcgkb. tcgkb implements the server side; the tray's tests run against a fake of it
(`tests/TrayTests/TcgkbTests.cs`).

Nothing here is used until the player chooses **tcgkb account ▸ Connect to tcgkb...** in the tray
menu. A tray that was never connected makes no request to tcgkb.

## Model

* A tcgkb user can own **several** PTCGL accounts. A PTCGL account belongs to **one** tcgkb user
  at most. Each PTCGL account keeps its own stats and its own place in a leaderboard; they are not
  merged.
* A PTCGL account is identified by the seal's `accountHash` (docs/LOG-SEAL.md §3), never by its
  display name: names can repeat and change, the account id does not. tcgkb keeps the names it has
  seen, newest first.
* A claim is proved with sealed logs in which that account is the `player`. Seals are made on the
  player's own computer (LOG-SEAL.md, "Threat model"), and an opponent sees your account id during
  a match, so a claim alone is not proof. Status:

  | `status` | Reached when | Effect |
  |---|---|---|
  | `claimed` | tcgkb accepted the evidence of a claim | shown on the user's profile as "not verified" |
  | `verified` | at least **1** match of this account is corroborated: another tcgkb user, with another `keyId`, uploaded the same `matchId`, and their seal names this `accountHash` as `opponent`, with the same result seen from the other side | counts in leaderboards; other claims of this account are refused |
  | `disputed` | two users claimed the same account and neither has a corroborated match | frozen until a tcgkb `mod` decides |

* A claimed account whose matches show up from another user's `keyId` with this account as
  `player` is a conflict; tcgkb resolves it by corroboration, as above.

## Transport

* Base URL `https://tcgkb.app`. HTTPS only. The tray never follows redirects (the bearer token
  goes nowhere else) and opens only `https://tcgkb.app/...` pages in the browser.
* `User-Agent: PtcglAutoBattleLog/<version>`. Responses are JSON (UTF-8), at most 1 MiB; the tray
  gives up on a request after 30 s.
* Errors: a non-2xx status with `{"error":"<code>","message":"<text for people>"}` (`message`
  optional). The tray shows `message` after its own wording of `error`.
* API calls carry `Authorization: Bearer <access token>`. 401 means the access token is no good:
  the tray refreshes once and retries, and treats a second 401 as "disconnected".

## Connecting: OAuth 2.0 device authorization grant ([RFC 8628](https://www.rfc-editor.org/rfc/rfc8628))

`client_id` is `ptcgl-auto-battlelog` (a public client: no secret). Scopes: `claims:write` (claim and
release PTCGL accounts) and `logs:upload` (reserved for uploading battle logs).

1. `POST /api/device/code`, `application/x-www-form-urlencoded`: `client_id`, `scope`
   (`claims:write logs:upload`), `device_name` (the Windows computer name, at most 64 characters).
   → `200` `{"device_code","user_code","verification_uri","verification_uri_complete","expires_in","interval"}`.
   `user_code`: `A-Z`, `0-9` and `-`, at most 16 (e.g. `WDJB-MJHT`). `verification_uri` must be a
   `https://tcgkb.app/` page (the tray refuses anything else; a foreign `verification_uri_complete`
   is ignored). Suggested: 10 minutes, interval 5 s.
2. The tray opens `verification_uri_complete` (else `verification_uri`) and shows `user_code`. On
   that page the user signs in with tcgkb's normal login (Google or password), sees the app, the
   computer name and the scopes, checks the code and approves or declines.
3. `POST /api/device/token`, form: `grant_type=urn:ietf:params:oauth:grant-type:device_code`,
   `device_code`, `client_id`, every `interval` seconds until:
   * `200` `{"access_token","token_type":"Bearer","expires_in","refresh_token","scope"}`;
   * `400` `{"error":"authorization_pending"}`: keep waiting;
   * `400` `{"error":"slow_down"}`: add 5 s to the interval;
   * `400` `{"error":"access_denied"}` or `{"error":"expired_token"}`: stop.
4. `GET /api/ptcgl/accounts` with the new token, for the user's name. Only then does the tray save
   the connection; if that call fails it revokes the refresh token and saves nothing.

Suggested lifetimes: access token 1 hour, refresh token 90 days from its last use. tcgkb lists
connected computers (name, created, last used) on the user's profile, where each can be revoked.

**Refresh**: `POST /api/device/token`, form `grant_type=refresh_token`, `refresh_token`,
`client_id` → `200` as above, with a **new** `refresh_token` (rotation: the old one stops working).
`400 {"error":"invalid_grant"}` (revoked, expired, already used) means disconnected: the tray
deletes its saved connection. A reused refresh token should revoke the whole grant.

**Revoke** ([RFC 7009](https://www.rfc-editor.org/rfc/rfc7009)): `POST /api/device/revoke`, form
`token=<refresh token>`, `client_id` → `200` (also for a token tcgkb does not know).

## PTCGL accounts

`GET /api/ptcgl/accounts` → `200`
```json
{"user":{"name":"Manu"},
 "accounts":[{"accountHash":"<64 hex>","name":"Ash","status":"verified"}]}
```

`POST /api/ptcgl/claims`, `application/json`:
```json
{"accountHash":"<64 hex>",
 "evidence":[{"fileName":"2026-10-19 21.34.05 - VICTORY vs Gary.txt","log":"<base64>","seal":null}]}
```
* `evidence`: 1 to 3 battle logs, newest first, each a tracked match (a `matchId`, a result other
  than `EXPORTED`). `log` is the `.txt` exactly as on disk (BOM, log text and, in Footer mode, the
  seal block); `seal` is the `.seal` sidecar exactly as on disk, or null. Each file is at most
  1 MiB; a request is at most about 4 MB.
* tcgkb checks every file as `tools/verify_log.py` does (LOG-SEAL.md §2–§6): the block's form, the
  RSA signature over the exact JSON line, `keyId` = thumbprint of `publicKey`, `logSha256` and
  `logChars`; then that `player.accountHash` is the claimed hash and `player.name` appears in the
  log text. It records the seals' `keyId`s for this user and stores the evidence (it is the first
  upload of those matches).
* → `200 {"account":{"accountHash","name","status"}}` (`name` from the newest seal).
* `409 {"error":"claimed_by_other"}`: the account is `verified` for another user (or disputed).
  `422 {"error":"invalid_evidence","message":"…"}`: a file failed a check; `message` says which and
  why. `429 {"error":"rate_limited"}`.

`DELETE /api/ptcgl/accounts/<accountHash>` → `204` (also `404` when it is not the user's): the
user gives the account up; its matches stay, unowned.
