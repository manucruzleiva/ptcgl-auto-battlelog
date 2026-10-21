# Pokematch uploads

What the tray app sends to [Pokematch](https://pokematch-vslogger.onrender.com) when the player sets
a Pokematch API token (tray menu **Pokematch ▸**). The behaviour around it (when, which logs, what
is remembered) is in [ARCHITECTURE.md](ARCHITECTURE.md), "Pokematch". The client side is
`src/Tray/Logic/PokematchApi.cs` and `PokematchUploader.cs`; the tests are
`tests/TrayTests/PokematchTests.cs`.

## Authentication

Players make API tokens on Pokematch's **API Tokens** page. Changing the Pokematch password revokes
all of them. Every request carries:

```
Authorization: Bearer <token>
Content-Type: application/json
Accept: application/json
User-Agent: PtcglAutoBattleLog/<version>
```

The token goes to `https://pokematch-vslogger.onrender.com` and nowhere else: no redirects are
followed. A 401 or 403 means the token is no longer accepted.

## Endpoints

| Request | Used for | Success | Other answers |
|---|---|---|---|
| `POST /api/v1/tcg-matches` | add a match | any 2xx; the body's `id` or `_id` (also under `match` or `data`) is kept to replace the match later | 401/403: token refused. 408, 429, 5xx, no answer: retried later. Anything else: this log is refused, until the plugin rewrites it. |
| `PUT /api/v1/tcg-matches/:id` | replace a match this app added, after the plugin rewrote its log | any 2xx | 404 or 410: the player deleted it on Pokematch; it is never sent again. Otherwise as `POST`. |

An error body's `message` (or `error`) string, if any, is shown to the player and written to the
app log.

## Body

<!-- TODO: confirm the field names against Pokematch's own form/export before release. -->

```json
{
  "result": "win",
  "opponent": "Gary",
  "deck": "Charizard ex",
  "playedAt": "2026-10-20T19:12:00Z",
  "log": "Setup\nAsh drew 7 cards for the opening hand.\n...",
  "source": "PTCGL Auto Battle Log"
}
```

| Field | Value |
|---|---|
| `result` | `"win"` for a `VICTORY` log, `"loss"` for `DEFEAT`. Other logs are not sent. |
| `opponent` | The opponent's display name: the seal's `opponent.name`, else the name in the file name. Left out when unknown. |
| `deck` | The player's deck name from the seal. Left out when unknown. |
| `playedAt` | When the match ended, UTC: the seal's `endedUtc`, else the file name's local time. |
| `log` | The battle log text exactly as the game's Export button gives it: the `.txt` without its BOM and without a footer seal ([LOG-SEAL.md](LOG-SEAL.md) §2). It contains both players' display names. |
| `source` | Always `"PTCGL Auto Battle Log"`. |

Nothing else is sent: no account ids or their hashes, no seal, no file paths.
