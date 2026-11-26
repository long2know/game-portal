# Mahjong web protocol v1

Paths below are relative to the app root. With `BASE_URL=/mahjong`, use `/mahjong/api/guest`, `/mahjong/ws`, and so forth; the browser's `Origin` remains scheme, host and port only. Wire names below are exact: camelCase JSON, UTF-8 text WebSocket messages, UTC RFC 3339 timestamps (`Z`), and nonnegative JS-safe integer revisions. No state/delta inference from a command ack; only snapshots replace rendered state.

## HTTP

| Route | Contract |
|---|---|
| `GET /` | Built client. WebSocket URLs use this origin (`ws:` or `wss:`); no base path. |
| `POST /api/guest` | Same-origin JSON `{displayName?: string}`. Create a guest if no valid cookie, otherwise return the existing guest unchanged. `200` with the guest DTO below. Default name `"Guest"`. |
| `PATCH /api/profile` | Same-origin, signed guest required. JSON `{displayName?,preferences?:{sound,music,volume,reducedMotion}}`; omitted fields stay unchanged. `200` with the updated guest DTO. |
| `GET /api/rooms` | Guest required. `200 {rooms:[{roomId,occupiedSeats,matchHands,rulesVersion?}]}` for public `waiting` rooms with an open seat only; at most 64. `rulesVersion` is present only for `changsha-two-bird-v1`; absent means `changsha-v1`. No scores, guest names, invite rooms or invite codes. |
| `GET /health/live` | `200 {"status":"live"}` once HTTP can serve. |
| `GET /health/ready` | `200 {"status":"ready"}` after migrations and checkpoint recovery and while SQLite is usable; otherwise `503 {"status":"notReady"}`. |
| `GET /api/version` | `200 {appVersion,protocolVersion:1,commitSha}`. Build supplies a version string and full Git SHA, or `"unknown"` for a local build without SHA metadata. |
| `GET /ws` | Same-origin WebSocket upgrade. Reject invalid guest with HTTP `401`, mismatched/missing `Origin` with `403`; check against the configured serving origin. |

Guest DTO: `{displayName,preferences:{sound,music,volume,reducedMotion},resumeRoomId}`. `sound`, `music` and `reducedMotion` are booleans; `volume` is a finite number in `[0,1]`. Defaults are `true`, `false`, `0.7`, `false`, respectively. A submitted `preferences` object includes all four fields. Store preferences with the guest and return them only to that guest. Trim names and require 1–24 characters without controls; publish a changed seat name via the room executor when seated. `resumeRoomId` is a string or `null` and identifies only the caller's membership, including an invite membership.

API responses are `Cache-Control: no-store`; HTTP errors use `{error:{code}}` (`400 invalid_request`, `401 not_authenticated`, `403 forbidden`, `429 busy`). Cookie `mahjong_guest` is a signed/Data Protection-protected guest reference: `HttpOnly`, `SameSite=Lax`, `Path` set to `BASE_URL` (default `/`), `Secure` under HTTPS, 30-day lifetime. The [private HTTPS proxy mode](operations.md#https-for-other-devices) also issues `Secure` cookies over the HTTP upstream and requires one HTTPS `Origin` matching the actual `Host` and effective port. A valid cookie and the persisted membership authorize a seat, never a seat number supplied by a client. No access tokens or invite codes in URLs; no guest IDs, credentials, chat text, private payloads or names in logs.

## Realtime envelope

Each message is one JSON object. Client sends:

```text
{v:1,type:"renewLease"}
{v:1,type:"sync",requestId,roomId}
{v:1,type:"command",requestId,lease,op,payload}
```

Server sends:

```text
{v:1,type:"lease",lease,expiresAtUtc,serverTimeUtc}
{v:1,type:"ack",requestId,status,error,roomId,roomRevision,gameRevision}
{v:1,type:"snapshot",roomId,roomRevision,gameRevision,serverTimeUtc,room,game,self}
{v:1,type:"error",error}
```

`requestId` is a client-generated UUID. Server sends a lease on connect and on `renewLease`. `status` is `ok` or `error`; `error` is `null` on success or `{code}` on failure. Ack room ID/revisions are `null` when not applicable or not authorized, otherwise the tuple at the original decision. `sync` authenticates membership, attaches the socket and sends ack then the current snapshot; it does not change revisions and needs no lease. A bad envelope that cannot be correlated gets a message of type `error`; an unsupported `v` gets `error` with code `unsupported_version` and WebSocket close `1002`. Bound inbound messages to 16 KiB, use one writer per socket, and close slow connections with `1013` rather than accumulating snapshots.

### Commands

| `op` | `payload` | Policy |
|---|---|---|
| `createRoom` | `{visibility,matchHands,fillBots?,rulesVersion?}` | `visibility` is `public` or `invite`, `matchHands` is `1` or `4`. Creator takes seat 0 and is host. Optional boolean `fillBots` defaults to `false`; `true` atomically seats three bots in seats 1–3. `rulesVersion` is exactly `changsha-v1` (default if omitted) or `changsha-two-bird-v1`. Explicit `null` or any other value returns `invalid_request`. |
| `joinRoom` | Exactly one of `{roomId}` or `{inviteCode}` | Public waiting room via ID; invite waiting room via code. Lowest open seat, never a bot's seat. Existing membership reconnects via `sync`. |
| `setReady` | `{roomId,expectedRoomRevision,ready}` | Waiting human marks their own seat. |
| `setBot` | `{roomId,expectedRoomRevision,seat,enabled}` | Waiting host adds a bot in an open seat or removes the bot there. |
| `start` | `{roomId,expectedRoomRevision}` | Waiting host; four occupied seats, every human ready. |
| `setPaused` | `{roomId,expectedRoomRevision,paused}` | Playing host; freezes game clock. Automatically resumes after at most 60 seconds. |
| `choose` | `{roomId,windowId,anchorGameRevision,optionId}` | Current unpaused decision; validate a legal option for the guest's seat. Never post a tile face or a seat. |
| `nextHand` | `{roomId,expectedRoomRevision}` | Any seated human in `betweenHands`; starts next hand with engine's `nextDealerSeat`. |
| `chat` | `{roomId,text}` | Attached human with a persisted seat. Trim to 1–300 characters without controls. At most five accepted messages per guest per room per rolling 10 seconds; ack replays do not count. |
| `rematch` | `{roomId,expectedRoomRevision}` | Complete-room host returns the same seats to `waiting`, clears the game and match totals, and resets human readiness. Keeps table totals and the settlement's next dealer. |
| `restartMatch` | `{roomId,expectedRoomRevision}` | Attached host only, in `playing`, `betweenHands` or `complete` with three bots and no other humans. Immediately deal hand 1 of a fresh match in the same room. |
| `leaveRoom` | `{roomId,expectedRoomRevision,abandon?}` | Omit `abandon` or send `false` for ordinary leave: remove the seat in `waiting` or `complete`, detach and reserve it in `playing` or `betweenHands`. Send `true` to permanently leave and close a sole-human host's room in any status. |

Successful create/join binds this socket and yields a snapshot. The newest authenticated socket attached by the room executor wins for that seat. Mark the old socket superseded under the executor before closing it with `4001` (`superseded`); it cannot sync or mutate afterwards, including queued commands not yet applied. Check the active socket under that same executor before each seated mutation. The closed tab must not retry automatically.

Mutations with `expectedRoomRevision` require exact equality; setting readiness, bot occupancy or pause to its current value is a no-op when valid. Lobby roster changes reset human readiness; bots are always ready. The host transfers on seat removal to the lowest occupied human seat, or the room closes if none remain. Rooms admit one membership per guest and at most four seats, all human or one or more humans with bots; no spectators. Cap the server at 64 live rooms. Expire rooms after 24 hours without a successful human subscription or room command. WebSocket/room IDs are not credentials; generate 128-bit random room IDs and separate 96-bit random invite codes, base64url without padding. Unseated callers cannot discover invite rooms by ID, even if they guess it: an unknown ID/code or unentitled lookup returns the same `room_not_found` with no room metadata.

Room status is `waiting → playing → betweenHands → playing … → complete`. `betweenHands` is skipped for the final hand; a draw still counts. `rematch` clears pause and the previous hand/settlement and requires the normal ready/start flow to deal the next match. `restartMatch` resets match scores, settlement, pause and timers and deals hand 1 immediately with a fresh server-generated seed. It publishes only the new `playing` state, not an intermediate `waiting` state. Both preserve the room ID, host, roster, settings, invite, chat and table totals. The room's immutable `rulesVersion` applies to every hand; switching profiles requires a new room. Both profiles are accepted by this protocol with either deployment mode.

A new room in portal mode chooses its first dealer uniformly from seats 0–3 on the server when created; standalone and older waiting rooms with no choice recorded start at seat 0. At a settled hand, the settlement chooses the next dealer for `nextHand`, `rematch` and `restartMatch`: the sole winner, the source in multi-Hu, or the [draw rule](rules.md) when nobody wins. Restarting an unfinished hand keeps that hand's dealer, even when the host occupies another seat. Every hand uses a fresh server-generated seed.

Attached nonhosts receive `forbidden` for `restartMatch` or `abandon:true`. For an attached host with a current revision, `restartMatch` returns `invalid_state` in `waiting` or without three bots, and `abandon:true` returns `invalid_state` if any other seat is human, including disconnected reservations. A new request with a non-boolean `abandon` returns `invalid_request`; absent and `false` are the same intent for retries. Neither operation bypasses membership, active-socket or exact room revision checks.

A network disconnect always reserves the seat. A successful leave returns an ack but no snapshot to the leaver. Reserved seats keep their table totals, as do all seats through pause and process recovery. An actual seat removal or replacement (human or bot) zeros only that seat's table total; a new occupant never inherits it. The UI can offer resumption only for a reserved seat; `abandon:true` deletes the room, its table totals and its membership, and leaves the guest identity and preferences unchanged, with `resumeRoomId: null` until the guest joins or creates another room. If the current room expires, drop the attachment and send `error` with `room_not_found`.

Chat is plain text, never markup: take `seat` and `displayName` from the sender at commit time, set `messageId` (opaque string) and `sentAtUtc` at the server, and render as text, not HTML. Keep only the last 30 messages in the room checkpoint, oldest first; no public listing or global history.

Ack error codes: `invalid_request` (including unknown op), `forbidden` (member lacks required role), `room_not_found`, `room_full`, `already_seated`, `invalid_state`, `not_ready`, `stale_room`, `stale_window`, `illegal_option`, `paused`, `rate_limited` (chat quota), `request_conflict`, `request_expired`, `busy` (transient; not recorded).

## Snapshots and windows

Every snapshot is a full projection built from committed state for **one** authorized guest seat. All fields are present unless marked optional; `null` means absent, collections use `[]`.

```text
room = {visibility,status,hostSeat,matchHands,rulesVersion,tableTotals,nextDealerSeat,paused,autoResumeAtUtc,seats,chat}
  tableTotals: four signed integers in absolute seat order
  nextDealerSeat: seat or null; when playing, the actual dealer is game.dealerSeat
  seats: four {seat,kind,displayName,ready,connected} in absolute seat order
  kind: "open" | "human" | "bot"; displayName: string | null; ready: boolean
  connected: boolean, true only for a human with an active attached socket
  chat: [{messageId,seat,displayName,text,sentAtUtc}]
self = {seat,inviteCode,hand,drawnTileId,legalOptions}
  inviteCode: string only for the host of an invite room, otherwise null
  hand: [{tileId,face}]; drawnTileId: string | null
  legalOptions: [{optionId,kind,tileIds,faces}]
game = null before first hand of each match, otherwise
  {handNumber,dealerSeat,phase,turnSeat,wallCount,kongRoll,seats,window,settlement}
  phase: "turn" | "claim" | "settled"; turnSeat: seat | null
  kongRoll: null or {seat,dice,lowerDie}, with dice: [integer,integer] in roll order, faces 1–6
  seats: four {seat,concealedCount,discards,melds,revealedTiles,total}
  melds: [{kind,method,fromSeat,tiles}] where kind is "chi" | "peng" | "gang" | "bu",
    method: "open" | "concealed" | "added" | null (not applicable),
    fromSeat: seat | null, tiles: (face | null)[] (four nulls for a concealed kong)
  window: null or {windowId,anchorGameRevision,reason,deadlineUtc,trigger}
    reason: "turn" | "opening" | "discard" | "kong" | "lastTile"
    trigger: null or {seat,faces}, where faces is face[] of public faces (up to two for a kong)
  settlement: null or {reason,nextDealerSeat,sourceSeat,winners,birds,seatResults,transfers?}
    reason: "win" | "draw"; sourceSeat: seat | null
    winners: [{seat,winBy,winningTiles,patterns,winningHand}] where winningTiles is face[]
    winBy: "selfDraw" | "discard" | "robKong"; patterns: [{code,label,points}]
    winningHand: {concealedTiles: face[], melds: [{kind,method,fromSeat,tiles: face[]}]}
    birds: face[]; seatResults: four {seat,delta,total}
    optional transfers: [{fromSeat,toSeat,points,reason}] where reason is "win" | "opening"
```

Faces are suited codes `b1`–`b9` (bamboo), `c1`–`c9` (characters), `d1`–`d9` (dots). `discards` and `revealedTiles` are public face arrays; discards are chronological unclaimed tiles. Legal option `kind` is `discard`, `draw`, `chi`, `peng`, `gang`, `bu`, `hu`, `pass` or `reveal`; `tileIds` references only own hand tiles, `faces` previews involved faces, both arrays possibly empty. Only the server interprets `optionId`; the list is empty when the seat has no pending decision and disabled in the UI while paused. Opponents' hidden faces remain private except for opening evidence, public triggers and terminal winning hands. Nothing exposes the wall order, seed, opponents' tile IDs, unrevealed option choices, guest identifiers or another room.

Seat IDs are absolute (`0`–`3`). Turn order is `(seat + 1) % 4`, displayed counterclockwise: from the viewer's seat at the bottom, offsets `0`, `1`, `2`, `3` are near, right, far, left. Only the discarder’s immediate successor may call Cha (`chi`), so the meld’s `fromSeat` is the claimant’s left neighbor. Seat references on the wire remain absolute.

In Jade Court (`Portal__Enabled=true`), `room.seats[].displayName` names bots after the artwork at their absolute seat: 0 Maple (Fox), 1 Pip (Rabbit), 2 Tallow (Tanuki), 3 Nori (Dragon). This applies to new and recovered rooms without rewriting saved names. Human names are unchanged, open seats have `null`, and standalone bots still display `Bot 1`–`Bot 4`.

`kongRoll` is non-null only during the replacement turn after a committed Gang. `seat` identifies the player who made the Gang, and `lowerDie` is the smaller face used to pick the replacement stack. Choosing the legal `gang` option requests the roll, with no separate command. A pending rob, Bu, ordinary draws, and any subsequent discard claim do not show a roll.

`room.tableTotals` and `room.nextDealerSeat` are additive; clients reading older snapshots may see neither. The next dealer is public to seated members (including invite joins before Ready) if a waiting choice exists, or from the current settlement in `betweenHands`/`complete`. It is `null` during play or while waiting with no saved choice. Use `game.dealerSeat` to show the actual hand dealer.

`game.seats[].total` and `settlement.seatResults[].total` are current match totals, reset by a rematch or restart. `room.tableTotals` is the same room's running score for the current occupants, not a balance carried to a different room. Each committed hand adds its signed `delta` once to both totals; per-hand deltas sum to zero, but retained table totals need not balance after a seat is cleared. A legacy room starts its table ledger from the stored match totals of its occupied seats only. These are points with no cash value. The current engine emits `transfers` from scoring; pass through the actual breakdown, never synthesize it from net scores. Each transfer moves positive integer `points` from payer to recipient, and per-seat net transfers must match the deltas.

`sourceSeat` is null for self-draw or draw. `settlement.nextDealerSeat` is always a seat (even at match end); a draw has no winners. `birds` contains public faces in reveal order, with at most one in classic play or two in [Two Birds](rules.md#optional-two-birds-changsha-two-bird-v1). A genuine last-tile win references its winning tile once in this array; a draw has no birds. The array never includes hidden wall faces or physical tile identifiers.

Only a committed win projects a `winningHand` for each winner. Display `winningTiles` (the event tile) separately: `concealedTiles` excludes only the actual self-drawn tile, or is the winner's unchanged concealed hand for discard or rob wins. Multiple winners each display their own hand and the same public event tile. The winner's melds use the seat meld metadata but show all faces, including all four in a concealed kong. `game.seats[].melds` stays redacted for concealed kongs, even after settlement.

- New room starts at `(roomRevision,gameRevision)=(1,0)`. Advance `roomRevision` once for changed roster/readiness/pause/status, seat name or committed chat. Advance `gameRevision` once per committed engine transition including starting a hand; neither resets. `rematch` and `restartMatch` each advance both revisions once, even if a restart replaces a match that was already `playing`. Removing a seat advances `roomRevision` once (also in the ack when closing the room) without a game transition. No changes for a retry, ordinary reconnect, transient `connected` change or ticking clock. Publish latest presence on equal-revision snapshots; accept those in socket delivery order.
- Anchor a window to the `gameRevision` that first exposed its `windowId`; wire window IDs are unique for the room lifetime, including rematches and restarts. Intermediate claim responses may advance `gameRevision` without changing that window's anchor or another seat's options. Each seat responds at most once per window. `choose` checks the **window ID and anchor**, not the latest room/game revision. A replaced, resolved or elapsed window rejects as `stale_window`, even if the same face has since appeared.
- Default limits: 30 seconds for a turn, 8 seconds for a claim; shorter limits only in test hosts. Use UTC minus accumulated paused time for the engine clock, frozen while paused. `deadlineUtc` is null while paused, otherwise the projected UTC deadline; `autoResumeAtUtc` is null when not paused. Pause/resume changes neither the engine revision nor the window anchor. Resolve due timers before commands; deadline is exclusive for choices and inclusive for expiration. On restart use persisted time/anchors (auto-resume at the scheduled time if overdue). Bots and timeouts never bypass the rules reducer.
- The [server's bot delay](operations.md#jade-court-operations) paces individual bot decisions without extending a turn or claim deadline. An elapsed window still expires before any pending bot decision. Recovery uses the stored bot due time; a changed default applies only when a new bot decision is scheduled.

## Commit, idempotency and recovery

Each signed lease binds a random lease ID and 10-minute expiry to the guest. Renew before expiry; old leases remain valid until their own expiry, including on a new socket. For a live lease, persist the command's semantic fingerprint and final ack under `(guest,leaseId,requestId)` in the same transaction as any mutation. Check it **before** re-validating window or room revision: identical retry replays the original ack, differing payload/op yields `request_conflict`. A replay never re-applies, re-attaches an obsolete socket or re-attaches a member who left; a `leaveRoom` retry never sends a snapshot. Keep receipts through expiry, even if their rooms are deleted. Reject an expired lease before receipt lookup as `request_expired`; atomically persist a monotonic prune cutoff when deleting expired receipts and use `max(server UTC, cutoff)` for expiry, so clock rollback cannot revalidate a deleted request.

For `createRoom`, omitted `rulesVersion` and explicit `changsha-v1` are the same intent, including when replaying a classic receipt from before the option was added. `changsha-two-bird-v1` is a different intent. The room profile, dealer choice and both score totals are committed with the checkpoint and any command receipt. Recovery does not replay settled wins or draw more birds.

If the sender is still entitled to a snapshot, its ack precedes that snapshot; other entitled sockets receive a fresh snapshot after commit. The client's latest authoritative tuple only moves componentwise forward within the same room. After reconnect, invoke `POST /api/guest` if needed to discover `resumeRoomId`, `sync` to it, and retry a pending command only with its **original** lease, request ID and payload. If the old lease is expired, use the synced state; never silently reissue the old intent under a new ID/lease. In case of an ack newer than the last snapshot or a stale error, use `sync` if the fresh snapshot does not arrive. Do not advance the rendered state on an ack alone.
