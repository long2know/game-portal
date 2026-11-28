# Mahjong web protocol v1

Paths below are relative to the app root. With `BASE_URL=/mahjong`, use `/mahjong/api/guest`, `/mahjong/ws`, and so forth; the browser's `Origin` remains scheme, host and port only. Wire names below are exact: camelCase JSON, UTF-8 text WebSocket messages, UTC RFC 3339 timestamps (`Z`), and nonnegative JS-safe integer revisions. No state/delta inference from a command ack; only snapshots replace rendered state.

## HTTP

| Route | Contract |
|---|---|
| `GET /` | Built client. WebSocket URLs use this origin (`ws:` or `wss:`); no base path. |
| `POST /api/guest` | Same-origin JSON `{displayName?: string}`. Create a guest if no valid cookie, otherwise return the existing guest unchanged. `200` with the guest DTO below. Default name `"Guest"`. |
| `PATCH /api/profile` | Same-origin, signed guest required. JSON `{displayName?,preferences?:{sound,music,volume,reducedMotion}}`; omitted fields stay unchanged. `200` with the updated guest DTO. |
| `GET /api/rooms` | Guest required. `200 {rooms:[{roomId,occupiedSeats,matchHands,rulesVersion?,status?,botSeats?,hostName?}]}`. At most 64 unexpired public rooms, ordered by `roomId`; see below for eligibility and portal-only fields. `rulesVersion` is present for `changsha-two-bird-v1` and `changsha-two-tile-kong-v1`; absent means `changsha-v1`. |
| `GET /health/live` | `200 {"status":"live"}` once HTTP can serve. |
| `GET /health/ready` | `200 {"status":"ready"}` after migrations and checkpoint recovery and while SQLite is usable; otherwise `503 {"status":"notReady"}`. |
| `GET /api/version` | `200 {appVersion,protocolVersion:1,commitSha}`. Build supplies a version string and full Git SHA, or `"unknown"` for a local build without SHA metadata. |
| `GET /ws` | Same-origin WebSocket upgrade. Reject invalid guest with HTTP `401`, mismatched/missing `Origin` with `403`; check against the configured serving origin. |

Guest DTO: `{displayName,preferences:{sound,music,volume,reducedMotion},resumeRoomId}`. `sound`, `music` and `reducedMotion` are booleans; `volume` is a finite number in `[0,1]`. Defaults are `true`, `false`, `0.7`, `false`, respectively. A submitted `preferences` object includes all four fields. Store preferences with the guest and return them only to that guest. Trim names and require 1–24 characters without controls; publish a changed seat name via the room executor when seated. `resumeRoomId` is a string or `null` and identifies only the caller's membership, including an invite membership.

API responses are `Cache-Control: no-store`; HTTP errors use `{error:{code}}` (`400 invalid_request`, `401 not_authenticated`, `403 forbidden`, `429 busy`). Cookie `mahjong_guest` is a signed/Data Protection-protected guest reference: `HttpOnly`, `SameSite=Lax`, `Path` set to `BASE_URL` (default `/`), `Secure` under HTTPS, 30-day lifetime. The [private HTTPS proxy mode](operations.md#https-for-other-devices) also issues `Secure` cookies over the HTTP upstream and requires one HTTPS `Origin` matching the actual `Host` and effective port. A valid cookie and the persisted membership authorize a seat, never a seat number supplied by a client. No access tokens or invite codes in URLs; no guest IDs, credentials, chat text, private payloads or names in logs.

Standalone discovery lists only `waiting` rooms with an open seat and omits `status`, `botSeats` and `hostName`. In Jade Court (`Portal__Enabled=true`), discovery additionally lists full waiting rooms with bots and `playing` rooms (including paused rooms) with a bot seat available for admission. Full all-human rooms, `betweenHands`, `complete` and invite rooms are never listed. Each portal row includes `status` (`waiting` or `playing`), `botSeats` (the number of bots in the roster), and `hostName` (the current public host's display name, or the default `Guest`). No other player names, guest IDs, invite data, game state, scores, chat or private availability data are returned. This read does not touch room state or expiry and does not reserve a seat.

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
| `createRoom` | `{visibility,matchHands,fillBots?,rulesVersion?}` | `visibility` is `public` or `invite`, `matchHands` is `1` or `4`. Creator takes seat 0 and is host. Optional boolean `fillBots` defaults to `false`; `true` atomically seats three bots in seats 1–3. `rulesVersion` is exactly `changsha-v1` (default if omitted), `changsha-two-bird-v1`, or `changsha-two-tile-kong-v1`. Explicit `null` or any other value returns `invalid_request`. |
| `joinRoom` | Exactly one of `{roomId,takeBot?}` or `{inviteCode}` | By default, take the lowest open seat in a waiting public room via ID or waiting invite room via code. In Jade Court only, `takeBot:true` explicitly takes the lowest bot seat in a playing or full waiting public room. Existing membership reconnects via `sync`. |
| `setReady` | `{roomId,expectedRoomRevision,ready}` | Waiting human marks their own seat. |
| `setBot` | `{roomId,expectedRoomRevision,seat,enabled}` | Waiting host adds a bot in an open seat or removes the bot there. |
| `start` | `{roomId,expectedRoomRevision}` | Waiting host; four occupied seats, every human ready. |
| `setPaused` | `{roomId,expectedRoomRevision,paused}` | Playing host; freezes game clock. Automatically resumes after at most 60 seconds. |
| `choose` | `{roomId,windowId,anchorGameRevision,optionId}` | Current unpaused decision; validate a legal option for the guest's seat. Never post a tile face or a seat. |
| `nextHand` | `{roomId,expectedRoomRevision}` | Any seated human in `betweenHands`; starts next hand with engine's `nextDealerSeat`. |
| `chat` | `{roomId,text}` | Attached human with a persisted seat. Trim to 1–300 characters without controls. At most five accepted messages per guest per room per rolling 10 seconds; ack replays do not count. |
| `rematch` | `{roomId,expectedRoomRevision}` | Complete-room host returns the same seats to `waiting`, clears the game and match totals, and resets human readiness. Keeps table totals and the settlement's next dealer. |
| `restartMatch` | `{roomId,expectedRoomRevision}` | Attached host only, in `playing`, `betweenHands` or `complete` with three bots and no other humans. Immediately deal hand 1 of a fresh match in the same room. |
| `leaveRoom` | `{roomId,expectedRoomRevision,abandon?,replaceWithBot?}` | With neither flag set, remove the seat in `waiting` or `complete`, or detach and reserve it in `playing` or `betweenHands`. `abandon:true` permanently leaves and closes a sole-human host's room in any status. In Jade Court only, `replaceWithBot:true` permanently hands off the attached human's seat during `playing` if another durable human member remains. |

Successful create/join binds this socket and yields a snapshot. The newest authenticated socket attached by the room executor wins for that seat. Mark the old socket superseded under the executor before closing it with `4001` (`superseded`); it cannot sync or mutate afterwards, including queued commands not yet applied. Check the active socket under that same executor before each seated mutation. The closed tab must not retry automatically.

Mutations with `expectedRoomRevision` require exact equality; setting readiness, bot occupancy or pause to its current value is a no-op when valid. Lobby roster changes reset human readiness; bots are always ready. The host transfers on seat removal to the lowest occupied human seat, or the room closes if none remain. Rooms admit one membership per guest and at most four seats, all human or one or more humans with bots; no spectators. Cap the server at 64 live rooms. Expire rooms after 24 hours without a successful human subscription or room command. WebSocket/room IDs are not credentials; generate 128-bit random room IDs and separate 96-bit random invite codes, base64url without padding. Unseated callers cannot discover invite rooms by ID, even if they guess it: an unknown ID/code or unentitled lookup returns the same `room_not_found` with no room metadata.

Room status is `waiting → playing → betweenHands → playing … → complete`. `betweenHands` is skipped for the final hand; a draw still counts. `rematch` clears pause and the previous hand/settlement and requires the normal ready/start flow to deal the next match. `restartMatch` resets match scores, settlement, pause and timers and deals hand 1 immediately with a fresh server-generated seed. It publishes only the new `playing` state, not an intermediate `waiting` state. Both preserve the room ID, host, roster, settings, invite, chat and table totals. The room's immutable `rulesVersion` applies to every hand; switching profiles requires a new room. All three profiles are accepted by this protocol with either deployment mode.

For new tables in the Jade Court Mahjong client (`/portal/mahjong/` and `/game-portal/mahjong/`), **Play with bots** explicitly sends `changsha-two-tile-kong-v1` and **Play with friends** selects it by default. The create form can choose Two Birds or classic instead. Neither the standalone client default nor the server's `changsha-v1` fallback on omission changes; existing tables keep their saved profile.

A new room in portal mode chooses its first dealer uniformly from seats 0–3 on the server when created; standalone and older waiting rooms with no choice recorded start at seat 0. At a settled hand, the settlement chooses the next dealer for `nextHand`, `rematch` and `restartMatch`: the sole winner, the source in multi-Hu, or the [draw rule](rules.md) when nobody wins. Restarting an unfinished hand keeps that hand's dealer, even when the host occupies another seat. Every hand uses a fresh server-generated seed.

Attached nonhosts receive `forbidden` for `restartMatch` or `abandon:true`. For an attached host with a current revision, `restartMatch` returns `invalid_state` in `waiting` or without three bots, and `abandon:true` returns `invalid_state` if any other seat is human, including disconnected reservations. A new request with a non-boolean `abandon` returns `invalid_request`; absent and `false` are the same intent for retries. Neither operation bypasses membership, active-socket or exact room revision checks.

`takeBot` and `replaceWithBot` must be booleans if supplied. `takeBot` cannot accompany `inviteCode`, and `replaceWithBot:true` cannot accompany `abandon:true`. Omitted and `false` are the original intent for retries. Sending either new flag as `true` on standalone returns `invalid_request`.

The server checks for an available bot when it commits a `takeBot:true` request, not when it lists the table. An incompatible status returns `invalid_state`; a lost or unavailable bot seat returns `room_full`. `already_seated` still prevents implicitly leaving another membership, and a reserved human seat is never claimable. If the guest already handed off a seat in the current hand, the request returns `seat_released` rather than disclosing another rack. Further takeovers can also be unavailable for that hand after repeated handoffs. Handoffs remain possible, and starting a new hand or restarting/rematching clears the handoff history.

Both playing-seat changes also work while paused. Each advances `roomRevision` once, with no engine transition beyond any independently due timers. They preserve the game, current window and responses, pause, match totals and other seats' readiness and table totals. Only the changed seat's table total is reset. A handoff transfers a departing host's role to the lowest occupied human seat, even if reserved. An invite table stays private and only its remaining members can continue the hand. The departing guest loses membership and its attachment, receives no more snapshots from that room, and no longer has a `resumeRoomId` or Continue entry for it. A handoff in any status other than `playing`, or from the last human seat, returns `invalid_state`.

A network disconnect always reserves the seat. A successful leave returns an ack but no snapshot to the leaver. Reserved seats keep their table totals, as do all seats through pause and process recovery. An actual seat removal or replacement (human or bot) zeros only that seat's table total; a new occupant never inherits it. The UI can offer resumption only for a reserved seat; `abandon:true` deletes the room, its table totals and its membership, and leaves the guest identity and preferences unchanged, with `resumeRoomId: null` until the guest joins or creates another room. If the current room expires, drop the attachment and send `error` with `room_not_found`.

Chat is plain text, never markup: take `seat` and `displayName` from the sender at commit time, set `messageId` (opaque string) and `sentAtUtc` at the server, and render as text, not HTML. Keep only the last 30 messages in the room checkpoint, oldest first; no public listing or global history.

Ack error codes: `invalid_request` (including unknown op), `forbidden` (member lacks required role), `room_not_found`, `room_full`, `already_seated`, `seat_released`, `invalid_state`, `not_ready`, `stale_room`, `stale_window`, `illegal_option`, `paused`, `rate_limited` (chat quota), `request_conflict`, `request_expired`, `busy` (transient; not recorded).

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
self = {seat,inviteCode,hand,drawnTileId,legalOptions,kongReplacements?}
  inviteCode: string only for the host of an invite room, otherwise null
  hand: [{tileId,face}]; drawnTileId: string | null
  legalOptions: [{optionId,kind,tileIds,faces}]
  kongReplacements: [{tileId,face}] only for the Two-Tile Kong candidate owner
game = null before first hand of each match, otherwise
  {handNumber,dealerSeat,phase,turnSeat,wallCount,kongRoll,seats,window,settlement}
  phase: "turn" | "claim" | "settled"; turnSeat: seat | null
  kongRoll: null or {seat,dice,lowerDie,stackCount?,stackMethod?}
    dice: [integer,integer] in roll order, faces 1–6
    stackCount: integer; stackMethod: "sum" | "lower" | "tail"
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

Faces are suited codes `b1`–`b9` (bamboo), `c1`–`c9` (characters), `d1`–`d9` (dots). `discards` and `revealedTiles` are public face arrays; discards are chronological unclaimed tiles. Legal option `kind` is `discard`, `draw`, `chi`, `peng`, `gang`, `bu`, `hu`, `pass`, `reveal` or `discardKong`; `tileIds` references only the recipient's hand or private Kong candidates, and `faces` previews involved faces. Both arrays may be empty. Only the server interprets `optionId`; `legalOptions` is empty when the seat has no pending decision or the room is paused. Opponents' hidden faces remain private except for opening evidence, public triggers and terminal winning hands. Nothing exposes the wall order, seed, opponents' tile IDs, unrevealed option choices, guest identifiers or another room.

Seat IDs are absolute (`0`–`3`). Turn order is `(seat + 1) % 4`, displayed counterclockwise: from the viewer's seat at the bottom, offsets `0`, `1`, `2`, `3` are near, right, far, left. Only the discarder’s immediate successor may call Cha (`chi`), so the meld’s `fromSeat` is the claimant’s left neighbor. Seat references on the wire remain absolute.

In Jade Court (`Portal__Enabled=true`), `room.seats[].displayName` names bots after the artwork at their absolute seat: 0 Maple (Fox), 1 Pip (Rabbit), 2 Tallow (Tanuki), 3 Nori (Dragon). This applies to new and recovered rooms without rewriting saved names. Human names are unchanged, open seats have `null`, and standalone bots still display `Bot 1`–`Bot 4`.

`kongRoll` is non-null only during the replacement turn after a committed Gang. `seat` identifies the player who made the Gang, and `lowerDie` is the smaller die. Classic and Two Birds use it to pick the replacement stack and omit `stackCount` and `stackMethod`. Two-Tile Kong reports the selected live stack as `stackCount`, counting from the tail stack at 1. `stackMethod` is `sum` when the dice sum fits, `lower` when only the lower die fits, or `tail` when neither fits and the tail stack is used. Optional keys are omitted if null. Choosing the legal `gang` option requests the roll, with no separate command. A pending rob, Bu, ordinary draws, and any subsequent discard claim do not show a roll.

With `changsha-two-tile-kong-v1`, the candidate window is `game.phase: "turn"`, `game.window.reason: "turn"` and `game.window.trigger: null`. Only its owner sees `self.kongReplacements`, containing the selected stack's tiles in rear-first order (one if the stack is partial), separate from `self.hand`. These remain visible to the owner during a pause, but the list is omitted for every other viewer and in every other phase or profile. The owner can choose a legal `hu` for one candidate, or `discardKong` to throw the entire batch. Submit the same `choose` payload with that window ID, anchor and option ID, never candidate IDs, faces, dice or stack data. An unanswered candidate turn expires to `discardKong`, not to Hu or a normal rack discard.

If another seat can Hu the thrown batch, the claim window is `game.phase: "claim"`, `game.window.reason: "kong"`; its `trigger` contains the declarer's seat and all thrown faces in batch order. The roll and private replacement list are gone. Each eligible `hu` option previews a single public face and has no tile IDs; choose one or pass, with unanswered claims treated as passes at the deadline. The server commits the batch even if no claim window is needed.

`room.tableTotals` and `room.nextDealerSeat` are additive; clients reading older snapshots may see neither. The next dealer is public to seated members (including invite joins before Ready) if a waiting choice exists, or from the current settlement in `betweenHands`/`complete`. It is `null` during play or while waiting with no saved choice. Use `game.dealerSeat` to show the actual hand dealer.

`game.seats[].total` and `settlement.seatResults[].total` are current match totals, reset by a rematch or restart. `room.tableTotals` is the same room's running score for the current occupants, not a balance carried to a different room. Each committed hand adds its signed `delta` once to both totals; per-hand deltas sum to zero, but retained table totals need not balance after a seat is cleared. A legacy room starts its table ledger from the stored match totals of its occupied seats only. These are points with no cash value. The current engine emits `transfers` from scoring; pass through the actual breakdown, never synthesize it from net scores. Each transfer moves positive integer `points` from payer to recipient, and per-seat net transfers must match the deltas.

`sourceSeat` is null for self-draw or draw. `settlement.nextDealerSeat` is always a seat (even at match end); a draw has no winners. `birds` contains public faces in reveal order, with at most one in classic play or two in [Two Birds](rules.md#optional-two-birds-changsha-two-bird-v1) and [Two-Tile Kong](rules.md#two-tile-kong-changsha-two-tile-kong-v1). A short wall supplies only the tiles that are available, and the settled `game.wallCount` is the count after removing any wall birds. For an actual last-tile win (`lastTileDraw` or `lastTileDiscard` in a winner's `patterns`), `birds` references the winning tile once instead, without drawing another tile. A draw has no birds. The array never includes hidden wall faces or physical tile identifiers.

Only a committed win projects a `winningHand` for each winner. Display the single face in `winningTiles` separately: `concealedTiles` excludes only the actual self-drawn tile, or is the winner's unchanged concealed hand for discard or rob wins. Multiple winners each display their own hand and event tile; in a Two-Tile Kong batch they may win on different faces. The winner's melds use the seat meld metadata but show all faces, including all four in a concealed kong. `game.seats[].melds` stays redacted for concealed kongs, even after settlement.

- New room starts at `(roomRevision,gameRevision)=(1,0)`. Advance `roomRevision` once for changed roster/readiness/pause/status, seat name or committed chat. Advance `gameRevision` once per committed engine transition including starting a hand; neither resets. `rematch` and `restartMatch` each advance both revisions once, even if a restart replaces a match that was already `playing`. Removing a seat advances `roomRevision` once (also in the ack when closing the room) without a game transition. No changes for a retry, ordinary reconnect, transient `connected` change or ticking clock. Publish latest presence on equal-revision snapshots; accept those in socket delivery order.
- Anchor a window to the `gameRevision` that first exposed its `windowId`; wire window IDs are unique for the room lifetime, including rematches and restarts. Intermediate claim responses may advance `gameRevision` without changing that window's anchor or another seat's options. Each seat responds at most once per window. `choose` checks the **window ID and anchor**, not the latest room/game revision. A replaced, resolved or elapsed window rejects as `stale_window`, even if the same face has since appeared.
- Default limits: 30 seconds for a turn, 8 seconds for a claim; shorter limits only in test hosts. Use UTC minus accumulated paused time for the engine clock, frozen while paused. `deadlineUtc` is null while paused, otherwise the projected UTC deadline; `autoResumeAtUtc` is null when not paused. Pause/resume changes neither the engine revision nor the window anchor. Resolve due timers before commands; deadline is exclusive for choices and inclusive for expiration. On restart use persisted time/anchors (auto-resume at the scheduled time if overdue). Bots and timeouts never bypass the rules reducer.
- The [server's bot delay](operations.md#jade-court-operations) paces individual bot decisions without extending a turn or claim deadline. An elapsed window still expires before any pending bot decision. After a live roster change the server keeps an existing due time while a bot still has a legal decision, cancels it when none do, or schedules a newly eligible bot if no time is pending. Recovery uses the stored bot due time; a changed default applies only when a new bot decision is scheduled.

## Commit, idempotency and recovery

Each signed lease binds a random lease ID and 10-minute expiry to the guest. Renew before expiry; old leases remain valid until their own expiry, including on a new socket. For a live lease, persist the command's semantic fingerprint and final ack under `(guest,leaseId,requestId)` in the same transaction as any mutation. Check it **before** re-validating window or room revision: identical retry replays the original ack, differing payload/op yields `request_conflict`. A replay never re-applies, re-attaches an obsolete socket or re-attaches a member who left; a `leaveRoom` retry never sends a snapshot. Keep receipts through expiry, even if their rooms are deleted. Reject an expired lease before receipt lookup as `request_expired`; atomically persist a monotonic prune cutoff when deleting expired receipts and use `max(server UTC, cutoff)` for expiry, so clock rollback cannot revalidate a deleted request.

For `createRoom`, omitted `rulesVersion` and explicit `changsha-v1` are the same intent, including when replaying a classic receipt from before the option was added. Each other profile is a distinct intent. The room profile, dealer choice and both score totals are committed with the checkpoint and any command receipt. Recovery does not reroll a pending Kong or replay settled wins or draw more birds.

If the sender is still entitled to a snapshot, its ack precedes that snapshot; other entitled sockets receive a fresh snapshot after commit. The client's latest authoritative tuple only moves componentwise forward within the same room. After reconnect, invoke `POST /api/guest` if needed to discover `resumeRoomId`, `sync` to it, and retry a pending command only with its **original** lease, request ID and payload. If the old lease is expired, use the synced state; never silently reissue the old intent under a new ID/lease. In case of an ack newer than the last snapshot or a stale error, use `sync` if the fresh snapshot does not arrive. Do not advance the rendered state on an ack alone.
