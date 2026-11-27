# Jade Court

Self-hosted, points-only game portal: no accounts, payments, wagering, or cash value. Read [active decisions](../.squad/decisions.md) and the relevant [game API](../docs/portal-api.md), [Mahjong protocol](../docs/protocol.md), and game-specific rules in `docs/` before changing contracts.

## Build and focused checks

All commands below run from the **repository root**, using .NET SDK **10.0.404** (`global.json`, roll-forward disabled) and Node.js **26.13.1** (CI/Docker pin). Projects target `net10.0`; EF Core SQLite is **10.0.15**. The frontend is plain DOM TypeScript **5.9.3**, Vite **7.3.8**, and Playwright **1.65.1**, not a component framework.

```bash
npm --prefix web ci
npm --prefix web run build
dotnet build src/Mahjong.Server/Mahjong.Server.csproj -c Release
```

Build the client **before** building/testing/publishing the server: its project copies `web/dist` into the output; .NET does not invoke npm. The npm build runs `tsc --noEmit`, then Mahjong and portal Vite builds in that order into the same dist directory. There is no configured lint/format script or separate frontend unit-test runner; typechecking/building is not linting. NuGet locked restore is enabled repository-wide; retain `packages.lock.json` and `web/package-lock.json`.

All seven xUnit suites (same project set and flags as [CI](workflows/ci.yml)):

```bash
for project in tests/*/*.csproj; do
  dotnet test "$project" -c Release -p:RestoreLockedMode=true \
    -- RunConfiguration.TreatNoTestsAsError=true || exit 1
done
```

One backend test (requires built web assets); use the corresponding test project's path and fully qualified name for other engine/server tests:

```bash
dotnet test tests/Mahjong.Server.Tests/Mahjong.Server.Tests.csproj -c Release \
  --filter 'FullyQualifiedName=Mahjong.Server.Tests.PortalMountTests.ServesBothBuiltShellsOnlyAtTheirCanonicalMountedRoutes' \
  -- RunConfiguration.TreatNoTestsAsError=true
```

Install the locked browsers when needed: `(cd web && ./node_modules/.bin/playwright install --with-deps chromium webkit)`.
Fixture-only geometry and a single frontend logic test:

```bash
npm --prefix web run test:geometry
npm --prefix web run test:e2e -- e2e/geometry.spec.ts --project=webkit \
  --grep '^wrapped discard confirmation does not move the compact rack$'
npm --prefix web run test:e2e -- e2e/lantern-run.spec.ts --project=chromium \
  --grep '^generated rows leave a reachable open path all the way to maximum speed$'
```

Playwright starts **only the Vite geometry fixture server**, not ASP.NET. For a single real-server browser test, start the native portal in one terminal after building:

```bash
BASE_URL=/portal Portal__Enabled=true GamePortal__DataDirectory="$PWD/artifacts/jade-court-native-data" \
  dotnet run --project src/Mahjong.Server --no-launch-profile -- --urls http://127.0.0.1:8912
```

In another terminal, at the root:

```bash
BASE_URL=http://127.0.0.1:8912/portal/ npm --prefix web run test:e2e -- \
  e2e/catalog.spec.ts --project=chromium \
  --grep '^lobby presents thirteen real destinations with local, cold catalog art$'
```

The server's `BASE_URL` is a **path**; Playwright's is an **absolute URL with trailing slash**. Catalog specs skip outside `/portal/` or `/game-portal/`. `npm --prefix web run test:e2e` runs all specs, but live coverage needs the matching real server/mount.

For CI-equivalent published-server/browser/restart qualification, follow [release gates and local verification](../docs/operations.md#release-gates-and-local-verification), including the exact version/commit publish properties. From root, with `publish`, `version`, and `revision` set to that artifact's path and identity:

```bash
SMOKE_PATH_BASE=/portal Portal__Enabled=true \
  bash scripts/smoke-published.sh "$publish" "$version" "$revision" --with-browser
```

This runs Chromium and WebKit against the publish, with fresh portal data per spec/browser and offline restore checks. Standalone root and `/mahjong` qualification uses `Portal__Enabled=false`; custom-mount TLS uses `/game-portal`, `SMOKE_HTTPS=true`, and **no** `--with-browser`. Geometry/controlled snapshots are not server integration evidence; neither is social coverage with `SOCIAL_FIXTURES=1`. Server xUnit integration tests launch real Kestrel processes and SQLite, not an in-memory HTTP host.

## Architecture and compatibility

- `src/Mahjong.Server` is the single ASP.NET host. `Program.cs` wires EF migrations/recovery, protected guest cookies, room clocks, native WebSocket `/ws`, and optional portal services. Portal mode is off by default natively; the image enables it. There are no external database services or runtime Node.
- Mahjong uses `Mahjong.Rules`' immutable, deterministic `ChangshaEngine` with a supplied clock/seed, public/seat projections, and limited-view bots. Host `Rooms` serializes work through bounded room lanes and a writer lane; persist checkpoint and command receipt before sending acknowledgements/snapshots. Preserve request/lease retry semantics and recovered deadlines; never reshuffle on resume. See [architecture](../docs/architecture.md) and [protocol](../docs/protocol.md).
- Other games do **not** share Mahjong's transport/state machine. `JadeCourt.Games.IBoardGame` exposes opaque checkpoints and public board/legal-move types; `BoardRegistry` adds `JadeCourt.Chess`. `BoardTables` commits human moves plus bounded bot replies with optimistic revision checks. `SeatedTables` hosts Poker, Words, Farkle, and Backgammon through adapters in `Seated/`; `SoloTables` stores guest-specific Solitaire/Sudoku checkpoints. Use the **outer table revision** for writes, and reload after `stale_room`.
- Checkpoints are authoritative **server-only** formats, never wire DTOs. Return recipient projections only: no wall/seed, future cards, opponent racks, or raw guest IDs. Preserve saved formats and original rules/draw order. Chess `chess-v1` replays canonical UCI history; its optional Stockfish process must return an authoritative legal move or use the managed fallback ([engine boundaries](../docs/portal-engines.md)).
- **Jade Letters** is a display name: retain `word-tiles/` links and `/api/words`. New deals use Duel checkpoint **3**; Quick **1** and Classic **2** resume with their original rules, seats, tile IDs, and draw order ([rules/checkpoints](../docs/word-tiles-rules.md#typed-library-checkpoints-and-wire)).
- `web/src/main.ts` is the Mahjong client; `web/src/portal/main.ts` routes the catalog and lazy game modules. `ClientShells` serves separate built `index.html`/`portal.html` templates outside `wwwroot`, injecting the runtime `data-base` and asset prefix. Use injected mount helpers (`web/src/paths.ts`, portal host/API), not root-absolute URLs. Lantern Run alone is browser-local with mount-scoped best-score storage, not a server save/API.

## Isolation, privacy, and contribution constraints

- Jade Court is separate from the standalone Mahjong service. Never reuse or alter its container, `/mahjong/` proxy route, SQLite file, volume, or key ring. Explicitly set a **portal-only** `GamePortal__DataDirectory` for native runs; the native legacy default is not an isolated portal location. One process/writer per data directory. Keep `mahjong.db`, sidecars, and `keys/` together for backup/restore; roll back matching data **and** image ([operations](../docs/operations.md)).
- Default portal mount is `/portal/`, including `/portal/mahjong/`; preserve `/game-portal/` compatibility across HTML/assets, redirects, HTTP, WebSocket, cookies, invites, and browser storage. HTTPS proxies preserve the public Host, Origin, and path; do not strip the prefix or invent Origin. Keep protected guest cookies and same-origin authorization; this is not a JWT/CORS design.
- Availability is opt-in and requires live-tab/busy checks. Discovery must not expose persistent guest IDs, current tables, or invite credentials. Accept checks recipient authorization and an open seat in the **exact** waiting table, atomically with a retryable receipt; never auto-leave another game ([private invites](../docs/portal-api.md#private-player-invites)).
- Keep cold shells within **24 KiB gzip portal / 32 KiB Mahjong**; defer game art/audio. Preserve touch/keyboard controls and reduced motion. Attribute licensed assets and retain redistribution notices, embedded lexicon notice, and exact Stockfish corresponding source ([assets](../docs/assets.md#static-asset-budget), [engines](../docs/portal-engines.md)).
- Release qualification requires both application and **exact Linux/amd64 image** gates. Promote the tested image, not a rebuild; authenticated registry success does not prove anonymous pull/download. Use the existing [operations runbook](../docs/operations.md#release-gates-and-local-verification), not generic npm-package release procedures.
- Follow [secret handling](skills/secret-handling/SKILL.md): never read `.env` or `.env.*` except `.env.example`, `.env.sample`, `.env.template`; do not commit credentials, private session data, cookies, or invite codes. Squad logs/histories/drafts remain local. Do not initialize another team or enable issue/PR/watch automation; remote mutations require maintainer authorization ([routing](../.squad/routing.md)).
- Public API/contract changes include tests of actual outputs and caller paths ([test discipline](skills/test-discipline/SKILL.md)). Honor explicit reviewer rejection/lockout under the [reviewer protocol](skills/reviewer-protocol/SKILL.md); do not bypass the gate or self-revise a rejected artifact. Keep factual/release claims grounded in source or retained evidence, not fixture passes or historical counts.
