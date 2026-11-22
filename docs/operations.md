# Jade Court operations

Run one portal process per data directory. The `long2know/game-portal` Linux/amd64 image runs `dotnet Mahjong.Server.dll` on port `8080` as non-root (UID `1654` by default), with no SDK or Node. Its defaults are `BASE_URL=/portal`, `Portal__Enabled=true`, and `Portal__StockfishPath=/usr/games/stockfish`. The HTML, HTTP API and WebSocket share the mount.

**Do not change an existing Mahjong container, image, proxy route or volume.** Jade Court uses a new service, host port and data directory. Although the new SQLite file is named `mahjong.db`, the portal must not open the old file or key ring. There is no migration from the other service.

| Setting | Purpose |
|---|---|
| `GAME_PORTAL_HOST_PORT` | Loopback port for either Compose example; default `8912`. |
| `DOCKER_DATA` | Required for [production Compose](#production-compose-on-the-proxy-host). Mounts only `${DOCKER_DATA}/games/portal/data`. |
| `GAME_PORTAL_UID`, `GAME_PORTAL_GID` | Production bind-mount user; both default to `1000`. Match the owner of the new directory. |
| `GAME_PORTAL_PRODUCTION_IMAGE` | Published tag or digest; default `ghcr.io/long2know/game-portal:latest`. |
| `GAME_PORTAL_PULL_POLICY` | `always` by default. Set to `never` only for a verified image already loaded locally. |
| `GAME_PORTAL_IMAGE` | Local named-volume Compose build; default `game-portal:local`, no registry pull of the application image. |
| `BUILD_VERSION`, `SOURCE_COMMIT` | Build-time identity (defaults `0.1.0` and `unknown`). Use a full SHA from a clean checkout; `/portal/api/version` and the OCI labels report it. |
| `GamePortal__DataDirectory` | Directory holding the SQLite database and key ring. The image defaults to `/data`. |
| `GamePortal__BehindHttpsProxy` | Defaults to `false`. Set `true` only behind a TLS edge that preserves `Host` and the path and has exclusive access to the HTTP upstream. |
| `Mahjong__AllowHttpLoopbackOrigin` | Local quickstart only; allows HTTP from a matching loopback browser origin through Docker. Omit in production. |
| `Portal__StockfishPath` | Optional path to a local UCI binary for native runs. Without it, Chess uses a built-in legal bot. The image supplies its own exact pin. |

Each `GamePortal__` setting takes precedence over its corresponding `Mahjong__DataDirectory` or `Mahjong__BehindHttpsProxy` alias, even when the new proxy setting is `false`. An empty or invalid new value stops startup instead of falling back. The image keeps its `/data` default on the legacy alias so a deployed `Mahjong__DataDirectory` override still works.

`GET /portal/health/live` checks HTTP; `/portal/health/ready` requires startup migrations, recovery and a usable SQLite directory. The Docker healthcheck follows the image mount. Keep `/data` writable by only the intended user: it holds SQLite and ASP.NET Data Protection keys. The keys are not automatically encrypted at rest.

## Run the published image

Check the [CI summary](https://github.com/long2know/game-portal/actions/workflows/ci.yml) for the desired full source commit. An authenticated `latest` digest does **not** establish public access. Use the [README Docker command](../README.md#run-the-published-image) without login only after the run reports **Verified anonymous pull**. For exact production rollback or promotion, record `ghcr.io/long2know/game-portal@sha256:<manifest-digest>`, not just `latest`. The `sha-<full-commit>` tag identifies the source build; digest pinning identifies exact registry bytes.

GHCR creates a fresh package as private, even for this public repository. **HTTP 401/403 on the anonymous token request** is reported as not anonymously pullable. Only the package owner can make `game-portal` public: in the new package's **Package settings → Danger Zone → Change visibility**, choose **Public** if intended. Then check without signing in (use a clean Docker config) that `latest` pulls and has the tested digest and image ID. Do not grant the new repository access to the old package. If the new package rejects the push, inspect only that new package's Actions access; no personal token is required by these jobs.

```bash
mkdir -m 0700 .jade-court-anonymous
printf '%s\n' '{"auths":{"ghcr.io":{}}}' > .jade-court-anonymous/config.json
image=ghcr.io/long2know/game-portal:latest
docker --config "$PWD/.jade-court-anonymous" pull --platform linux/amd64 "$image"
docker image inspect --format '{{.Id}} {{json .RepoDigests}}' "$image"
rm -r .jade-court-anonymous
```

### No-login image archive

If GHCR is private or its publication job fails, the separate CI archive job can attach the **same tested image**, not a rebuild, to a build-specific [prerelease](https://github.com/long2know/game-portal/releases) in this public repo. Use a release after the job reports **Verified anonymous download**. It does not change the latest GitHub Release, old Mahjong package, or live deployment.

Copy the full source commit, build-specific tag and tested image ID from the release:

```bash
sha='<full source commit>'
tag='<portal-image-commit-run-attempt tag>'
tested_id='<sha256:tested image ID>'
file=game-portal-linux-amd64.tar.gz
base="https://github.com/long2know/game-portal/releases/download/$tag"
curl --disable -fLO "$base/$file"
curl --disable -fLO "$base/$file.sha256"
sha256sum -c "$file.sha256"
docker image load --input "$file"
image="ghcr.io/long2know/game-portal:sha-$sha"
test "$(docker image inspect --format '{{.Id}}' "$image")" = "$tested_id"
```

In the README's `docker run` command, replace `--pull=always` with `--pull=never` and the final image name with `"$image"`. For production Compose, set `GAME_PORTAL_PRODUCTION_IMAGE="$image"` and `GAME_PORTAL_PULL_POLICY=never`. If no public asset is available yet, check out the intended clean revision from the [public source](https://github.com/long2know/game-portal) and use the [named-volume Compose build](../README.md#run-with-docker-compose). A build from the same pinned recipe is not proof of the hosted image's exact digest.

## Production Compose on the proxy host

On a Linux/amd64 host with Docker Engine 28+ and Compose, set `DOCKER_DATA` in the shell or your deployment `.env`. The checked-in [compose.production.yaml](../compose.production.yaml) runs the distinct `game-portal` service and does not merge with the local quickstart:

```yaml
services:
  game-portal:
    image: ${GAME_PORTAL_PRODUCTION_IMAGE:-ghcr.io/long2know/game-portal:latest}
    platform: linux/amd64
    pull_policy: ${GAME_PORTAL_PULL_POLICY:-always}
    user: "${GAME_PORTAL_UID:-1000}:${GAME_PORTAL_GID:-1000}"
    ports:
      - "127.0.0.1:${GAME_PORTAL_HOST_PORT:-8912}:8080"
    environment:
      ASPNETCORE_ENVIRONMENT: Production
      BASE_URL: /portal
      Portal__Enabled: "true"
      GamePortal__DataDirectory: /data
      GamePortal__BehindHttpsProxy: "true"
    volumes:
      - type: bind
        source: ${DOCKER_DATA:?Set DOCKER_DATA in .env}/games/portal/data
        target: /data
        bind:
          create_host_path: false
    restart: unless-stopped
    stop_grace_period: 30s
    cap_drop: [ALL]
    security_opt:
      - no-new-privileges:true
```

Prepare an unused directory before the first launch. Do not point it or any symlink at other service data. Keep the owner and mode across restores; never make it world-writable.

```bash
set -eu
export DOCKER_DATA=/srv/docker-data # Set to your own storage root.
data_dir="$DOCKER_DATA/games/portal/data"
if [[ -e $data_dir || -L $data_dir ]]; then echo "Not a new data directory: $data_dir" >&2; exit 1; fi
sudo install -d -m 0700 -o "${GAME_PORTAL_UID:-1000}" -g "${GAME_PORTAL_GID:-1000}" "$data_dir"
docker compose -f compose.production.yaml up -d --wait
curl -fsS http://127.0.0.1:8912/portal/health/ready
curl -fsS http://127.0.0.1:8912/portal/api/version
```

If the directory already exists, stop and inspect it before launching; reuse only data intentionally created by this same Jade Court deployment. A containerized proxy on a private Docker network can instead use upstream `http://game-portal:8080`; remove the host port mapping if the proxy is the only client. Do not publish the raw HTTP port on `0.0.0.0`.

## HTTPS for other devices

Add these locations **inside your existing TLS virtual host**, retaining its certificates, server-name allowlist, and `/mahjong` locations. Set the upstream to the host port chosen above. Add this dedicated upgrade map in Nginx's `http` context:

```nginx
map $http_upgrade $portal_connection {
    default upgrade;
    '' close;
}
```

```nginx
location = /portal {
    absolute_redirect off;
    return 308 /portal/$is_args$args;
}

location ^~ /portal/ {
    proxy_pass http://127.0.0.1:8912;
    proxy_http_version 1.1;
    proxy_set_header Host $http_host;
    proxy_set_header Origin $http_origin;
    proxy_set_header Upgrade $http_upgrade;
    proxy_set_header Connection $portal_connection;
    proxy_set_header Forwarded "";
    proxy_set_header X-Forwarded-For "";
    proxy_set_header X-Forwarded-Host "";
    proxy_set_header X-Forwarded-Proto "";
    proxy_set_header X-Forwarded-Prefix "";
    proxy_read_timeout 180s;
}
```

The `proxy_pass` URL has **no trailing slash**: Nginx sends `/portal/...` unchanged. Preserve the public `Host`, including any non-default HTTPS port. Reject unmatched hosts and redirect or block public HTTP at the edge. Run `nginx -t` before reloading, then verify the existing `/mahjong/` route is unchanged and open `/portal/` on the intended HTTPS host.

The app checks exactly one matching HTTPS `Origin` on guest writes, moves, and WebSocket upgrades. The proxy must pass the supplied `Origin`, not invent it. Guest cookies are host-only, `Secure`, `HttpOnly`, `SameSite=Lax`, with `Path=/portal`; don't rewrite their path or domain. No app hostname, proxy IP, JWT, CORS, or forwarded-headers configuration is needed. Do not enable `ASPNETCORE_FORWARDEDHEADERS_ENABLED` or set `Mahjong__TrustedProxies__*` with this mode. It does not authenticate the proxy or encrypt its HTTP hop: restrict that hop to your trusted proxy. Apps sharing an origin are not a security boundary; use a separate origin if the other apps aren't trusted.

On an isolated staging deployment, run `node scripts/smoke-portal.mjs seed https://YOUR-HOST/portal/ <version> <full-commit> <new-state-file>` against the edge. It creates disposable guests/tables and checks cookie scope and HTTPS flags, allowed and rejected `Origin` values, `/portal/ws` upgrades, and actual play. Use a trusted certificate (or add the staging CA via `NODE_EXTRA_CA_CERTS`), not disabled certificate verification. Delete the smoke state file afterward; don't log cookies or access tokens.

## Stop, back up, update, and restore

The examples use production Compose. For a local named volume, define `portal()` with `docker compose --env-file /dev/null` instead. Never stop or archive the old Mahjong service as part of a portal operation. Never use `down -v` or start a second writer to make a backup.

### Offline backup

Warn players, record `/portal/api/version`, and stop only the portal. Reuse the stopped container's exact image and user for the read-only `tar` helper:

```bash
set -eu
portal() { docker compose -f compose.production.yaml "$@"; }
umask 077
mkdir -p backups
backup="backups/jade-court-data-$(date -u +%Y%m%dT%H%M%SZ).tar"
container="$(portal ps --all --quiet game-portal)"
test -n "$container"
portal stop --timeout 30 game-portal
test "$(docker inspect --format '{{.State.Status}}:{{.State.ExitCode}}' "$container")" = exited:0
image_id="$(docker inspect --format '{{.Image}}' "$container")"
run_user="$(docker inspect --format '{{.Config.User}}' "$container")"
docker run --rm --pull=never --network none --user "$run_user" --volumes-from "$container:ro" \
  --entrypoint tar "$image_id" -C /data -cpf - . > "$backup.part"
tar -tf "$backup.part" >/dev/null
mv "$backup.part" "$backup"
sha256sum "$backup" > "$backup.sha256"
portal start --wait game-portal
```

The backup contains the entire stable directory: database, possible WAL/SHM sidecars, and `keys/`. A backup of the live main DB alone cannot restore guest identity and seats. Keep the verified backup off-host, restricted and encrypted; record the image digest/ID and source revision with it. Retain backups according to your data-loss tolerance and periodically rehearse a restore. Keep deployment backups and software image archives separate.

### Update and roll back

Take a backup first, skipping the final `start` to keep the service stopped. For a registry update, set the intended digest in `GAME_PORTAL_PRODUCTION_IMAGE` and run `portal up -d --wait`. For an archive update, verify and load the new archive first and set its revision tag with `GAME_PORTAL_PULL_POLICY=never`. Validate readiness, version and guest reconnect. A simple restart does not change the image. For a source-build update, rebuild from the intended clean checkout but retain the previous image ID/tag.

The app migrates SQLite at startup. For rollback, restore the **matching** pre-update data and image rather than opening a migrated database in older code. Stop only the portal before reusing its host port. Preserve the original data directory or volume as a fallback.

### Restore into a fresh directory

Set `$backup` to the restore archive and `$restore_root` to a new, unused storage root. Use the same production UID/GID, port and public origin/mount. Set `GAME_PORTAL_PRODUCTION_IMAGE` to the corresponding saved digest or loaded tag, and choose its pull policy.

```bash
set -eu
sha256sum -c "$backup.sha256"
restore_dir="$restore_root/games/portal/data"
if [[ -e $restore_dir || -L $restore_dir ]]; then echo "Not a new restore directory: $restore_dir" >&2; exit 1; fi
sudo install -d -m 0700 -o "${GAME_PORTAL_UID:-1000}" -g "${GAME_PORTAL_GID:-1000}" "$restore_dir"
DOCKER_DATA="$restore_root" docker compose -f compose.production.yaml run --rm --no-deps -T \
  --entrypoint tar game-portal -C /data -xpf - < "$backup"
DOCKER_DATA="$restore_root" docker compose -f compose.production.yaml up -d --force-recreate --wait
```

Record the new `DOCKER_DATA` in subsequent deployment settings. For a quickstart named-volume restore, use a fresh Compose project name with `-p game-portal-restored` and verify `game-portal-restored_portal-data` does not exist before invoking the same `run`/`up` flow with the local Compose file and matching `GAME_PORTAL_IMAGE` already loaded.

Check version and readiness, then reconnect from a browser with its original cookie at the original origin and path. Confirm the original guest, table/seat and a valid position. A page load alone doesn't prove recovery. Clearing the cookie, changing the origin or path, or losing the key ring can lose access to seats. Mahjong deadlines continue to elapse during downtime.

## Release gates and local verification

CI uses pinned action/tool and base-image versions, locked dependencies, and two read-only gates:

- **Application:** Original Mahjong Rules/Server tests plus the JadeCourt.Games and JadeCourt.Chess tests in NuGet locked mode. Playwright runs Chromium and WebKit against the same native publish: the original root and `/mahjong` specs have `Portal__Enabled=false`; the dedicated `/portal` spec uses `Portal__Enabled=true` and the chess fallback with no configured native engine.
- **Image:** Build Linux/amd64, smoke the **exact local image ID** and inspect runtime identity/architecture/entrypoint, non-root execution, no SDK or Node, and writable isolated volumes. Test the original root and `/mahjong` cold shell under the unchanged 32 KiB gzip budget and real WebSocket play and restore. Test `/portal` defaults, a separate 24 KiB shell budget, pinned Stockfish, real guest/Chess bot/Reversi invite moves, route/cookie boundaries and retained state after recreation. No runtime test means no promotion.

For local use, publish from a clean checkout with the pinned SDK and Node after building the client with `npm --prefix web ci && npm --prefix web run build`:

```bash
version=0.1.0
revision="$(git rev-parse HEAD)"
publish="artifacts/publish-$revision"
dotnet restore src/Mahjong.Server/Mahjong.Server.csproj -p:RestoreLockedMode=true
dotnet publish src/Mahjong.Server/Mahjong.Server.csproj -c Release --no-restore -o "$publish" \
  -p:UseAppHost=false -p:ContinuousIntegrationBuild=true \
  -p:Version="$version" -p:InformationalVersion="$version+$revision" \
  -p:IncludeSourceRevisionInInformationalVersion=false
Portal__Enabled=false bash scripts/smoke-published.sh "$publish" "$version" "$revision"
SMOKE_PATH_BASE=/mahjong Portal__Enabled=false bash scripts/smoke-published.sh "$publish" "$version" "$revision"
SMOKE_PATH_BASE=/portal Portal__Enabled=true bash scripts/smoke-published.sh "$publish" "$version" "$revision"
```

Add `--with-browser` after installing the exact Playwright Chromium/WebKit dependencies to run browser cases too. On a host with a working Docker daemon, run the actual image gate:

```bash
docker compose --env-file /dev/null config --quiet
DOCKER_DATA=/example docker compose --env-file /dev/null -f compose.production.yaml config --quiet
BUILD_VERSION="$version" SOURCE_COMMIT="$revision" docker compose --env-file /dev/null build game-portal
bash scripts/smoke-container.sh game-portal:local "$version" "$revision"
```

Only a push to `main` in `long2know/game-portal` can publish. Both read-only gates must pass before release jobs load the checksummed tested archive and compare the image ID. The serialized GHCR job alone has `packages: write`: it verifies the commit tag and `latest` at the same manifest digest, checks `main` just before promotion and reports actual anonymous visibility. The fallback job alone has `contents: write`: it creates a namespaced tag pointing at that build's full SHA, verifies assets on a draft, publishes it as a non-latest prerelease and checks the anonymous download's SHA-256. No remote deployment step runs in CI.

### Artifact licenses and provenance

Native publish and the image include the project's [GPL v3 license](../LICENSE) and the [runtime notices](../scripts/licenses/THIRD-PARTY.txt) plus the bundled Chess notices. The image uses the Ubuntu Noble universe amd64 `stockfish=16-1build1` package. APT verifies the signed Ubuntu release metadata for both binary and source, then the build checks their pinned SHA-256 values. The exact corresponding source (including Noble’s package patches) and copyright are in `/usr/share/doc/stockfish/`; see [engine provenance, upstream mirrors and license details](portal-engines.md). The base image retains its runtime licenses. If redistributing, provide corresponding source as required by the GPL.
