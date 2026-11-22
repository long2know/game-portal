# Jade Court game APIs

Set `Portal__Enabled=true` to enable the board API and portal shell (the default is off). With
`BASE_URL=/portal`, the base for assets, HTTP and WebSocket URLs is `/portal/`, including on
`/portal/chess/`, `/portal/go/`, `/portal/reversi/`, `/portal/checkers/`, and
`/portal/chinese-checkers/`, `/portal/holdem/`, `/portal/solitaire/`, `/portal/sudoku/`, and
`/portal/word-tiles/`. `/portal/mahjong/` and its nested SPA routes serve the existing
Changsha Mahjong client. The bare routes return a 308 with the query intact; since the
redirect does not supply a fragment, browsers retain the original fragment. Other shell
paths return 404. The build publishes `index.html` and `portal.html` outside `wwwroot`.

Use a dedicated data volume for the portal (`GamePortal__DataDirectory`, `/data` in the
image). Its EF migration installs board tables and memberships alongside the existing
guest/profile and Mahjong tables in the portal's own SQLite file. The guest cookie and
its protection keys are shared across games within this app, not imported from the
original Mahjong installation. Keep the portal's database and keys together across
restarts. With `GamePortal__BehindHttpsProxy=true`, the HTTPS edge must preserve the
public `Host` and `/portal` path as for the existing Mahjong API.

## Shared boards

The following paths are relative to the configured mount. JSON properties are camelCase.
Call the existing `POST /api/guest` to obtain the protected guest cookie and
`PATCH /api/profile` to change the display name. Board writes require that cookie and
the same `Origin` accepted by the existing server policy.

| Method and path | Body and result |
| --- | --- |
| `GET /api/board/games` | Public catalog `{games}`; entries include `id`, `name`, `description`, `playerCounts`, and `variants` (`id` and `label`). |
| `POST /api/board/tables` | `{game, variant, players, bots, botDifficulty?}` → a new table with the creator in seat 0. `bots: true` fills every other seat and starts immediately. |
| `POST /api/board/join` | `{invite}` → a table for the requesting guest. The final human to join starts the game. Valid member rejoin is idempotent. |
| `GET /api/board/tables/{id}` | Current committed table, only for a seated guest. |
| `POST /api/board/tables/{id}/moves` | `{revision, moveId}` → the committed table, after any bot replies. `moveId` is an opaque ID from `position.moves`. |
| `POST /api/board/tables/{id}/leave` | `{revision}` → closes a waiting table or a live non-Chess table. |
| `POST /api/board/tables/{id}/resign` | `{revision}` → a seated guest forfeits live Chess regardless of turn. |
| `GET /api/board/resume?game={gameId}` | `{tableId}`: the guest's most recently created or joined unexpired table for the game, skipping closed or resigned tables, or `null`. |

A table includes `id`, `game`, `variant`, `revision`, `status` (`waiting`, `playing`,
`finished`, or `closed`), the caller's zero-based `seat`, `invite`, `seats`, and `position`. Seats contain
only `seat`, `name`, `bot`, and `occupied`. While waiting, `position` is `null` and seated
guests can share the 128-bit invite (32 hex characters). `invite` is `null` after the game
starts. A table ID is not an invite. There is no public table list; nonmembers cannot read
the seats or game state by guessing an ID. The position is the selected rules module's
public board and legal options, never its checkpoint or any raw guest identifiers.

Chess bot tables accept and return `botDifficulty` (`easy`, `standard`, or `hard`).
Omitting it defaults to `standard`, including on saves created before the difficulty option.
Human Chess and all other games reject the field and do not return it.

Closing a table records `endReason: "left"` and `endedBySeat`. Live Chess requires resignation
instead, which records `status: "finished"` and `result: {cause: "resignation", loserSeat, winnerSeat}`.
Both operations revoke the invite, preserve the last public board (if started) without legal
actions, and leave the terminal table readable by members. Naturally finished games cannot
be closed or resigned. Navigating away does not close or resign a table.

Revision starts at 0 and advances once per new seat, accepted human move, or exit. Each move and
its bot replies are committed as one revision; reload using `GET` on any conflict. Bots
take at most one seating round per move, outside the SQLite write transaction. If a bot
doesn't return a legal move, the server logs the fallback and applies the first legal
option. `Portal__StockfishPath` is an optional operator filesystem path; Chess can choose a
legal fallback without a Stockfish process.

Only a successful move response includes `turnMotion: {fromRevision, steps}`. `fromRevision`
is the accepted prior table revision. The ordered `steps` start with the human move and
include each bot reply. Each `{move, changes}` contains the canonical `GameMove` (including
any promotion or jump path) and just the public `GameCell`s whose owner or piece changed in
that ply. A pass has empty `changes`. Animate only from a matching `fromRevision`, replacing
cells by ID in step order; the returned `position` is always authoritative. Create, join,
read, resume, exit and error responses omit `turnMotion`; it is not saved or replayed.

Errors use `{error:{code}}`: `invalid_request`, `illegal_move`, `not_your_turn`,
`room_not_started`, `room_closed`, `resign_required`, and `game_finished` (400); `not_authenticated` (401); `forbidden`
(403); `room_not_found` (404, also for nonmembers or a full/unknown invite); `stale_room`
(409); `busy` or `table_limit` (429); and `engine_unavailable` (500). Board requests cap
mutation JSON at 4 KiB. Authenticated reads have a separate per-guest quota for polling
every 2–3 seconds through shared proxies; Mahjong quotas are unchanged.

Waiting tables expire after 24 hours without a join; started tables expire seven days
after starting or the last move. Polling and idempotent rejoin do not extend expiry.
Each guest can own up to 32 unexpired tables, with a shared limit of 512. Creating a
new table does not evict earlier tables or change their memberships. Explicitly closed
or resigned tables remain readable for 24 hours and do not count toward those limits.

## Play-chip Hold’em

The Poker endpoints use the same cookie, mount, same-origin check, body limit, and portal
read/write quotas as shared boards. These chips have no monetary value: seats receive 1,000
chips and play at fixed 5/10 blinds; there are no payments, rebuys, or ratings.

| Method and path | Body and result |
| --- | --- |
| `POST /api/poker/tables` | `{players, bots}`, with 2–6 seats. `bots: true` populates other seats and starts immediately; otherwise the final guest to join starts play. |
| `POST /api/poker/join` | `{invite}` seats the caller; a member reusing the invite is returned the same seat. |
| `GET /api/poker/tables/{id}` | A member's own view of the committed table. |
| `GET /api/poker/resume` | `{tableId}` for the caller's most recently joined or created, unexpired waiting or playing table; otherwise `null`. |
| `POST /api/poker/tables/{id}/actions` | `{revision, action:{kind, to?}}`. `kind` is `fold`, `check`, `call`, `bet`, or `raise`; for bet and raise only, `to` is the integer **total on the current street** (not an increment). |
| `POST /api/poker/tables/{id}/next` | `{revision}`. Any seated guest can deal after a hand settles and before the match is complete. |
| `POST /api/poker/tables/{id}/leave` | `{revision}` closes a waiting or playing table and revokes its invite. It does not award chips or change a prior settlement. |

The response is `{id, revision, status, seat, seats, invite, position, endReason?}`. Seat rows
use the board seat shape. Waiting tables have an invite and `position: null`; active and
terminal tables return no invite. `position` is the engine's [`HoldemView`](holdem-rules.md),
only with cards and options authorized for the viewer. Card ranks/suits, streets, and other
engine enums are JSON numbers. Future cards and the private checkpoint never leave SQLite.
`position.result` reports the settled hand; the wrapper remains `playing` until
`position.matchWinnerSeat` is set and `status` becomes `finished`. A member closing a table
sets `status: "closed"` and `endReason: "left"` and may still read it with no legal actions.

Use the **outer** `revision` for writes, not `position.revision`. An accepted write can
include multiple bounded bot actions but advances the table revision only once. Errors
include `illegal_action`, `not_your_turn`, `hand_finished` (deal the next hand),
`hand_in_progress` (cannot deal yet), `room_not_started`, `room_closed`, and `game_finished`
(400); auth, member lookup, stale revision, and busy errors match boards. Waiting tables
expire after 24 hours, playing tables after seven days, and closed or finished tables
after 24 hours. Terminal tables cannot be resumed and do not count toward active limits.
