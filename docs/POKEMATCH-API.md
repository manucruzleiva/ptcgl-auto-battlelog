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
| `GET /api/v1/me` | check a token when it is set | any 2xx | 401: the token is refused and not kept. |
| `POST /api/v1/tcg-matches` | add a match | any 2xx; the body's `_id` (or `id`, also under `match` or `data`) is kept to replace the match later | 401/403: token refused. 408, 429, 5xx, no answer: retried later. Anything else: this log is refused until what it would send changes. |
| `PUT /api/v1/tcg-matches/:id` | update a match this app added, when what it would send changed (a corrected result) | any 2xx | 404 or 410: the player deleted it on Pokematch; it is never sent again. Otherwise as `POST`. |

An error body's `message` (or `error`) string, if any, is shown to the player and written to the
app log.

## Body

Pokematch's TCG log, as its API docs define it (Pokematch → API Docs):

```json
{
  "activeDeck": { "name": "Charizard ex" },
  "opponentDeck": "Unknown",
  "format": "Bo1",
  "games": [ { "result": "win" } ],
  "date": "2026-10-20",
  "notes": "Added by PTCGL Auto Battle Log.",
  "tags": [ "ptcgl-auto-battle-log" ]
}
```

| Field | Value |
|---|---|
| `activeDeck.name` | The player's deck name from the seal, else `"Unknown"`. Required by Pokematch. |
| `opponentDeck` | Always `"Unknown"`: the client does not tell the player the opponent's archetype. Required by Pokematch; the player can edit it there. |
| `format` | `"Bo1"`: PTCGL matches are single games. |
| `games` | One game: `"win"` for a `VICTORY` log, `"loss"` for `DEFEAT`. Other logs are not sent. |
| `date` | The match end's local date, `yyyy-MM-dd`: the seal's `endedUtc`, else the file name. |
| `notes` | `"Added by PTCGL Auto Battle Log."` |
| `tags` | `"ptcgl-auto-battle-log"`, then the seal's `queue` and `format` in lowercase when it names them, e.g. `["ptcgl-auto-battle-log","ranked","standard"]` (Pokematch stores tags lowercase, at most 30 characters; a repeated name is sent once). |

Nothing else is sent: no player or opponent names, no account ids or hashes, no log text, no seal,
no file paths. `GET /api/v1/me` is called once when the player sets a token, to check it.

Pokematch's limits: 1000 requests per 15 minutes per IP and 2000 per account (429); a run sends at
most 25 logs.
