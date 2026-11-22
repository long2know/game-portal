# Jade Court game APIs

Set `Portal__Enabled=true` to enable the game APIs and portal shell (the default is off). With
`BASE_URL=/portal`, the base for assets, HTTP and WebSocket URLs is `/portal/`, including on
`/portal/chess/`, `/portal/go/`, `/portal/reversi/`, `/portal/checkers/`, and
`/portal/chinese-checkers/`, `/portal/holdem/`, `/portal/solitaire/`, `/portal/sudoku/`,
`/portal/word-tiles/`, `/portal/farkle/`, `/portal/backgammon/`, and `/portal/lantern-run/`. `/portal/mahjong/` and
its nested SPA routes serve the existing Changsha Mahjong client. The bare routes return
a 308 with the query intact. Browsers retain the original fragment because the redirect
does not supply one. Other shell
paths return 404. The build publishes `index.html` and `portal.html` outside `wwwroot`.

Lantern Run has no game API, multiplayer rooms, or invite links. It runs in the browser
and stores only the local best finished score, separately for each mount such as `/portal/`
or `/game-portal/`. If local storage is unavailable the best lasts until the page is closed
or reloaded.

Use a dedicated data volume for the portal (`GamePortal__DataDirectory`, `/data` in the
image). Its EF migrations preserve existing board, guest/profile and Mahjong data and add
private seated-game tables and solo saves to the portal's own SQLite file. The guest cookie and
its protection keys are shared across games within this app, not imported from the
original Mahjong installation. Keep the portal's database and keys together across
restarts. With `GamePortal__BehindHttpsProxy=true`, the HTTPS edge must preserve the
public `Host` and `/portal` path as for the existing Mahjong API.

## Private player invites

These endpoints are portal-only. Paths are relative to `BASE_URL` (for example,
`/portal/api/social/presence`). Each POST requires an existing protected guest cookie,
the same-origin `Origin` header, and a JSON object of at most 4 KiB. Responses use
camelCase and `Cache-Control: no-store`. Success is HTTP 200.

| Method and path | Body → result |
| --- | --- |
| `POST /api/social/presence` | `{tabId, active, busy, available?}` → `{available, players, incoming, outgoing}` |
| `POST /api/social/invites` | `{playerId, game, tableId}` → `{id}` |
| `POST /api/social/invites/{id}/accept` | `{}` → `{game, tableId}` after a committed seat |
| `POST /api/social/invites/{id}/decline` | `{}` → `{ok:true}` (recipient only) |
| `POST /api/social/invites/{id}/revoke` | `{}` → `{ok:true}` (sender only) |
| `POST /api/social/invites/{id}/block` | `{}` → `{ok:true}` (recipient only) |

`tabId` is a random per-document string (1–128 ASCII letters, digits or `-_.~`); `active`,
`busy` and the optional `available` are booleans. New guests are opted out. Only explicitly
sending `available` changes the persisted guest preference; heartbeats omit it. A visible
tab heartbeats every 15 seconds and gets a 45-second lease. `active:false` deactivates that
tab without renewal; any reported busy state still suppresses discovery until that lease
expires or the tab returns and reports idle. Discovery requires opt-in, a fresh visible
tab, and no fresh busy tab. A visible opted-in sender can still browse and invite while busy.
Inactive and opted-out pulses return empty arrays. The server caps presence at eight tabs
per guest, 512 leased guests per instance, and 32 players per response.

`players` entries are `{id, displayName}`. The IDs are short-lived, viewer-scoped handles
(up to 45 seconds), not persistent guest identifiers and not interchangeable with invite IDs.
A player disappearing or the service restarting invalidates its previous handle. `incoming`
and `outgoing` entries are `{id, game, displayName, expiresAtUtc}`; the name is always the
other guest's current profile name, and expiry is UTC ISO 8601. These responses never expose
guest identities, current games, table IDs, or link credentials.

`game` is one of `mahjong`, `chess`, `go`, `reversi`, `checkers`, `chinese-checkers`,
`holdem`, `word-tiles`, `farkle`, or `backgammon` (the existing HTTP paths for Hold'em
and Word Tiles are still `/api/poker` and `/api/words`). Send uses the sender's own
waiting table ID: the sender must be seated, or be the current host for Mahjong, and
there must be an open human seat. Sending does not reserve a seat. An identical pending
send returns the original ID and does not extend its two-minute lifetime. The pending
limits are eight sent and eight received per guest, three per pair, and 512 per instance.

Accept rechecks the recipient, both opt-ins, any block, the recipient's live tab state,
and the sender's authority and the open seat in the same waiting table. It never leaves
or resigns from another game. An exact-table seat acquired through a share link, another
live Mahjong membership for a Mahjong invite, or a busy recipient returns `already_seated`.
An in-memory busy hold covers the 15 seconds after joining so the recipient can navigate.
The response identifies only the now-authorized table: use the existing exact member GET
or authenticated Mahjong WebSocket `sync` to load it. A retry can replay the same result,
including across restart, for five minutes while the recipient remains entitled to the
same unexpired table, it has not closed, and neither guest has opted out or blocked the
other. This does not add a seat or start a game again. The server prunes expired invites
and receipts independently of client polling.

Opting out immediately cancels all of that guest's pending requests, across tabs.
Blocking persists for the guests' lifetime, hides the pair in both directions, and
cancels their pending requests. Neither action undoes a committed join.

Errors are `{error:{code}}`: `invalid_request` (400, including unsupported slugs),
`not_authenticated` (401), `forbidden` (403, origin), `invite_unavailable` (404, uniform
for wrong actors, blocked or opted-out guests, absent/expired tokens or handles, and
ineligible or closed targets), `already_seated` (409, including busy), and `busy`
(429, limits or transient contention). No error returns a target or guest identifier.

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

## Jade Letters

The Jade Letters page and invites keep the `word-tiles/` path under the configured mount.
The table routes, invite behavior, response shape, and outer revision rules match Poker
under `/api/words` instead of `/api/poker`. Create with `{players: 2, bots}`; there is no
`/next` route. `position` is the [typed `WordView`](word-tiles-rules.md), including sparse
board cells, scores, bag count, seat rack sizes, and the caller's own rack only. New deals have
`variant: "duel"` (15×15, `rackSize: 7`); saved `"classic"` and `"quick"` games retain their
original layouts and values. The creator (seat `0`) opens; Duel's center has no premium. `premiums` gives the table's
map as `{row,column,kind}` entries (`double-letter`, `triple-letter`, `double-word`, `triple-word`);
Quick has none. Derive which premiums have been used from occupied cells. In Duel, a cell
includes `recent: true` only if its tile was placed in the latest action. `exchangeLimit` is the
number of tiles the caller may exchange now, or `0` if unavailable.
Tiles include `blank`; a blank shows `letter: "?"` in the rack and its assigned uppercase letter
on the board, with zero points either way. Neither the other rack nor bag order is returned.
A finished game cannot be changed.

Write `POST /api/words/tables/{id}/actions` with `{revision, action}`. The action is
`{kind: "pass"}`, `{kind: "exchange", exchange: [tileId, ...]}`, or
`{kind: "place", placements: [{row, column, tileId, letter?}, ...]}`. Coordinates are zero-based
and IDs must be distinct and in the caller's current rack. Set `letter` to one uppercase `A`–`Z`
for a blank only; omit it for other tiles. The request parser accepts arrays of 1–7 tiles and
coordinates 0–14; the rules enforce the table's actual board, rack, and exchange limits.
Malformed input returns `invalid_request`; requests that cannot be played can return
`not_your_turn`, `illegal_action`, or the shared room/revision errors. A bot sees only the
public position and its own rack and takes at most one turn before the response is committed.

## Farkle and Backgammon

`/api/farkle` (2–6 seats) and `/api/backgammon` (2 seats) follow the same seated table,
invite, resume, leave, and outer revision contract as Jade Letters. Both support bots and
private guest invitations; neither has a `/next` route. A roll takes only `{kind: "roll"}`:
the server chooses the dice and saves the committed position. Requests cannot supply
faces or a random seed.

- In Farkle, `{kind: "keep", dice: [index, ...]}` selects distinct zero-based indices in the
  current roll; `{kind: "bank"}` ends the turn and banks its points when allowed.
  `position.choices` lists the current player's scoring subsets as `{mask, points}`,
  with bit `i` corresponding to die `i`. A roll with no scoring subset ends the turn.
- In Backgammon, the server rolls both opening dice; the seat with the higher die starts.
  `{kind: "move", from, to, die}` uses a move from `position.legalActions.moves`;
  points are numbered 1–24, with `from: 0` for the bar and `to: 25` for bearing off.
  `{kind: "pass"}` is allowed only where advertised by `position.legalActions.canPass`.

## Solitaire and Sudoku

Each protected guest has one saved table per game in SQLite, independent of other guests.
The save lasts with the guest identity and survives server restarts when the database and
cookie protection keys are preserved. No seed, solution, or future card order is sent or
accepted. The same portal body size, same-origin, and per-guest quota rules apply.

| Method and path | Body and result |
| --- | --- |
| `GET /api/solo/{game}` | `{table: null}` if there is no save, or `{table: SoloTable}`. |
| `POST /api/solo/{game}` | `{variant}` replaces the save with a fresh deal and returns `SoloTable`. |
| `POST /api/solo/{game}/moves` | `{revision, action}` applies one legal action and returns `SoloTable`. |

`SoloTable` is `{game, variant, revision, position}`. The first deal has revision `0`;
each accepted action or redeal advances it by one. Always send the current revision for a
move; a prior board's revision after a redeal returns `stale_room` (409).

- `solitaire` supports variant `draw-one` and actions `{kind: "draw"}` and
  `{kind: "move", from, to, count}`. Use pile IDs from `position.legalMoves`; covered
  cards show `null` rank and suit.
- `sudoku` supports `easy`, `standard`, and `hard` and actions
  `{kind: "set", index, value}` (value `0` clears the cell), or `{kind: "hint", index?}`.
  The view contains row-major givens, current values, and conflicts. A hint changes
  just one editable cell.

See [solo engine rules](solo-rules.md) for action details. Unsupported variants, games, or
malformed actions return `invalid_request`; illegal plays and finished saves return
`illegal_action` and `game_finished` (400). A move without a save returns `room_not_found` (404).
