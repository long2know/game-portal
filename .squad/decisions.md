# Squad Decisions

## Active Decisions

### Portal deployment and persistence stay separate

Jade Court defaults to `/portal/`, including its `/portal/mahjong/` child. Keep
HTML, assets, HTTP, WebSocket and guest cookies mount-aware, including at the
alternate `/game-portal/` mount. Use a separate image, service and data directory
for Jade Court: keep its SQLite database and key ring together and never reuse
or modify the standalone Mahjong service, `/mahjong/` route or volume.
See [operations](../docs/operations.md) and the
[mount contract](../docs/portal-api.md).

### Engine changes preserve existing games

Server checkpoints are versioned and authoritative; do not reinterpret existing
formats or expose hidden state in wire projections. Jade Letters is a display
name: retain `word-tiles/` links and `/api/words`. Deal Duel (checkpoint 3) for
new games; resume Quick (1) and Classic (2) with their original rules, seats and
draw order. Chess replays canonical UCI moves for `chess-v1`. See
[Jade Letters checkpoints](../docs/word-tiles-rules.md#typed-library-checkpoints-and-wire)
and [engine boundaries](../docs/portal-engines.md).

### Availability and invitations are private by default

Discovery requires an authenticated guest and explicit availability; honor
live-tab and busy rules. Bind invites to the recipient and exact waiting table,
recheck authorization and the open seat at acceptance, and keep retries
idempotent. Do not expose guest IDs, current tables or link credentials in
discovery. See [private player invites](../docs/portal-api.md#private-player-invites).

### Keep the web shell small and artwork attributable

Keep the cold portal shell within 24 KiB gzip and the cold Mahjong shell within
32 KiB. Defer game-specific art and audio; use original or licensed, attributed
assets and preserve keyboard, touch and reduced-motion behavior. See
[asset provenance and budgets](../docs/assets.md#static-asset-budget) and
[browser validation](../docs/browser-validation.md).

### Promote only the tested image

Use the real published server for Chromium and WebKit qualification; do not use
fixture-only results as integration evidence. The application and exact
Linux/amd64 image gates both pass before any promotion. Publish the tested image,
not a rebuild, and verify anonymous access before documenting a no-login pull
or download. See [release gates](../docs/operations.md#release-gates-and-local-verification).

## Governance

- Record accepted, durable, public-safe decisions here; keep working drafts and
  personal or session data out of Git.
- Use the linked portal docs for precise contracts and release procedures.
