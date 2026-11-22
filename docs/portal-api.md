# Jade Court board tables

Set `Portal__Enabled=true` to enable the board API and portal shell (the default is off). With
`BASE_URL=/portal`, the base for assets, HTTP and WebSocket URLs is `/portal/`, including on
`/portal/chess/`, `/portal/go/`, `/portal/reversi/`, `/portal/checkers/`, and
`/portal/chinese-checkers/`. `/portal/mahjong/` and its nested SPA routes serve the existing
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

## HTTP contract

The following paths are relative to the configured mount. JSON properties are camelCase.
Call the existing `POST /api/guest` to obtain the protected guest cookie and
`PATCH /api/profile` to change the display name. Board writes require that cookie and
the same `Origin` accepted by the existing server policy.

| Method and path | Body and result |
| --- | --- |
| `GET /api/board/games` | Public catalog `{games}`; entries include `id`, `name`, `description`, `playerCounts`, and `variants` (`id` and `label`). |
| `POST /api/board/tables` | `{game, variant, players, bots}` → a new table with the creator in seat 0. `bots: true` fills every other seat and starts immediately. |
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
