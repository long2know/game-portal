# Jade Court

A self-hosted board-game portal for phones and desktops, including four-seat Changsha Mahjong, Chess, and [Jade Letters](docs/word-tiles-rules.md), a two-seat crossword game. Play a bot or share a private invite. This is free play with points only: no accounts, payments, wagering, or cash value.

The catalog also includes [Xiangqi and Gomoku](docs/board-game-rules.md), [Big Two](docs/big-two-rules.md), and [Mahjong Solitaire](docs/solo-rules.md#mahjong-solitaire-mahjong-solitaire). Use **Continue playing** in the lobby to reopen your saved games. Mahjong Solitaire progress stays in this browser.

The catalog is at `/portal/`; Changsha Mahjong is at `/portal/mahjong/`. Jade Letters keeps its existing `/portal/word-tiles/` URL for bookmarks and invites. For a solo arcade game, open `/portal/lantern-run/` and use arrows, WASD, or swipes and the on-screen controls. Lantern Run saves your best finished run in this browser only, not on a leaderboard. Jade Court is a separate service and data store. Leave any existing Mahjong container, volume, and `/mahjong/` proxy route unchanged.

[Daily Court](docs/portal-api.md#daily-court) pairs a one-move Xiangqi tactic with a solvable Mahjong Solitaire stack. Both reset at 00:00 UTC and keep progress in this browser only.

## Run the published image

On a Linux/amd64 Docker host, use GHCR **only after** [CI verifies anonymous access](docs/operations.md#run-the-published-image). If the package is private, use the [verified no-login image archive](docs/operations.md#no-login-image-archive) or [build from source](#run-with-docker-compose). Use a fresh `jade-court-data` volume, never a volume from another service.

```bash
docker run -d --name jade-court --restart unless-stopped --pull=always --platform linux/amd64 \
  --publish 127.0.0.1:8912:8080 --mount type=volume,source=jade-court-data,target=/data \
  --cap-drop ALL --security-opt no-new-privileges:true \
  --env Mahjong__AllowHttpLoopbackOrigin=true \
  ghcr.io/long2know/game-portal:latest
```

Open [http://127.0.0.1:8912/portal/](http://127.0.0.1:8912/portal/). Stop with `docker stop jade-court` to keep the data. For remote play, use the [production Compose and Nginx examples](docs/operations.md#production-compose-on-the-proxy-host), not the HTTP quickstart. No JWT, CORS setting, or separate database is needed.

## Run with Docker Compose

Build the same pinned image recipe with Docker Engine 28+ and the Compose plugin, from a clean checkout:

```bash
SOURCE_COMMIT="$(git rev-parse HEAD)" docker compose --env-file /dev/null up --build -d --wait
```

Open [http://127.0.0.1:8912/portal/](http://127.0.0.1:8912/portal/). Set `GAME_PORTAL_HOST_PORT` for a different port. `docker compose --env-file /dev/null down` keeps the new named volume; don't use `down -v`. There is no .NET SDK or Node at runtime. See the [operations runbook](docs/operations.md) for release verification, HTTPS, backups and rollback.

## Run natively from source

Install the exact [.NET 10 SDK](global.json) and Node.js **26.13.1**. Use a new data directory explicitly rather than the server's legacy native default:

```bash
npm --prefix web ci && npm --prefix web run build
BASE_URL=/portal Portal__Enabled=true GamePortal__DataDirectory="$PWD/artifacts/jade-court-native-data" \
  dotnet run --project src/Mahjong.Server --no-launch-profile -- --urls http://127.0.0.1:8912
```

The native quickstart uses the built-in legal chess bot without Stockfish. The container includes [pinned Stockfish, exact corresponding source and engine notices](docs/portal-engines.md).

## Contributor Squad

The [Jade Court roster](.squad/team.md), [routing](.squad/routing.md), and
[decisions](.squad/decisions.md) are versioned with the Copilot coordinator,
skills, and repo-local MCP launcher. No Squad issue assignment or scheduled
workflow is enabled. Session logs, agent histories, and draft decisions stay local.

From a fresh clone with Node.js 22.5 or later, Squad CLI 2.0.0 or later, and GitHub Copilot CLI
installed, run `squad health --json` and `squad status` at the repo root. Start the
team with `copilot --agent squad` and trust the checkout when prompted so Copilot
can load `.mcp.json`. Do not re-run `squad init`; the launcher uses this checkout's
local `.squad` state, not the standalone Mahjong team's state.

Jade Court is licensed under the [GNU General Public License, version 3](LICENSE).
See the [Changsha rules profile](docs/rules.md) and [asset provenance](docs/assets.md).
The Squad tooling includes the [Squad](.squad/templates/LICENSE.squad) and
[agency-agents](.squad/templates/LICENSE.agency-agents) MIT notices.
