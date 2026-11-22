#!/usr/bin/env bash
set -euo pipefail

if (( $# < 3 || $# > 4 )) || [[ ${4:-} != "" && $4 != --with-browser ]]; then
  echo "Usage: bash scripts/smoke-published.sh PUBLISH_DIR VERSION COMMIT [--with-browser]" >&2
  exit 2
fi

publish=$(realpath -- "$1")
version=$2
revision=$3
if [[ ! -s "$publish/Mahjong.Server.dll" || ! -s "$publish/index.html" ]]; then
  echo "Missing published server or index.html in $publish" >&2
  exit 1
fi
for notice in LICENSE licenses/{THIRD-PARTY,Microsoft.NET-MIT,Apache-2.0,SQLitePCLRaw-NOTICE,Microsoft.Extensions.DependencyModel-NOTICES,Gera.Chess-MIT,Ardalis.SmartEnum-MIT}.txt; do
  if [[ ! -s "$publish/$notice" ]]; then
    echo "Missing published license or notice: $publish/$notice" >&2
    exit 1
  fi
done

mount=${SMOKE_PATH_BASE:-}
portal=${Portal__Enabled:-false}
https=${SMOKE_HTTPS:-false}
case "$portal" in
  true)
    if [[ ( ${mount%/} != /portal && ${mount%/} != /game-portal ) || ! -s "$publish/portal.html" ]]; then
      echo "Portal smoke requires SMOKE_PATH_BASE=/portal or /game-portal and a published portal.html" >&2
      exit 1
    fi
    if ! cmp -s src/JadeCourt.Words/Lexicon/SCOWL-Copyright.txt "$publish/licenses/SCOWL-2020.12.07.txt"; then
      echo "Missing or changed SCOWL notice: $publish/licenses/SCOWL-2020.12.07.txt" >&2
      exit 1
    fi
    if ! cmp -s scripts/licenses/Mahjong-Calls-NOTICE.txt "$publish/licenses/Mahjong-Calls-NOTICE.txt"; then
      echo "Missing or changed Mahjong calls notice: $publish/licenses/Mahjong-Calls-NOTICE.txt" >&2
      exit 1
    fi
    runtime=scripts/smoke-portal.mjs
    ;;
  false) runtime=scripts/smoke-runtime.mjs ;;
  *)
    echo "Portal__Enabled must be true or false" >&2
    exit 2
    ;;
esac
case "$https" in
  true)
    if [[ $portal != true || ${4:-} == --with-browser ]]; then
      echo "SMOKE_HTTPS=true requires portal mode without --with-browser" >&2
      exit 2
    fi
    ;;
  false) ;;
  *)
    echo "SMOKE_HTTPS must be true or false" >&2
    exit 2
    ;;
esac

umask 077
mkdir -p artifacts
work="$PWD/artifacts/infra-smoke-$$-$RANDOM"
mkdir -- "$work"
data="$work/data"
restored="$work/restored"
archive="$work/data.tar"
state="$work/guest.json"
export TMPDIR="$work/scratch"
mkdir -- "$TMPDIR"
server_pid=
portal_specs=(e2e/portal.spec.ts e2e/mahjong-audio.spec.ts e2e/board-controls.spec.ts e2e/expansion.spec.ts e2e/dice-games.spec.ts e2e/art-direction.spec.ts e2e/lantern-run.spec.ts)

stop_server() {
  local pid=$server_pid
  server_pid=
  if kill -0 "$pid" 2>/dev/null; then
    kill -TERM "$pid"
  fi
  wait "$pid"
}

cleanup() {
  local status=$?
  if [[ -n $server_pid ]]; then
    stop_server || true
  fi
  rm -rf -- "$data" "$restored" "$work/browser" "$archive" "$state" "$TMPDIR"
  if (( status == 0 )); then
    rm -rf -- "$work"
  else
    echo "Published smoke failed; server logs: $work/*.log" >&2
  fi
}
trap cleanup EXIT

scheme=http
tls_args=()
if [[ $https == true ]]; then
  scheme=https
  openssl req -x509 -newkey rsa:2048 -sha256 -nodes -days 1 -subj '/CN=localhost' \
    -addext 'subjectAltName=IP:127.0.0.1' -out "$TMPDIR/loopback.crt" -keyout "$TMPDIR/loopback.key" \
    > /dev/null 2>&1 || { echo "Could not generate the isolated HTTPS smoke certificate" >&2; exit 1; }
  export NODE_EXTRA_CA_CERTS="$TMPDIR/loopback.crt"
  tls_args=(--Kestrel:Certificates:Default:Path "$NODE_EXTRA_CA_CERTS"
    --Kestrel:Certificates:Default:KeyPath "$TMPDIR/loopback.key")
fi

start_server() {
  local directory=$1 log=$2
  (
    cd -- "$publish"
    # Pin isolated data and the direct loopback listener at higher priority than inherited settings.
    exec env -u Mahjong__PublicOrigin -u DOTNET_ENVIRONMENT -u Portal__StockfishPath \
      ASPNETCORE_ENVIRONMENT="${ASPNETCORE_ENVIRONMENT:-Production}" \
      Portal__Enabled="$portal" \
      BASE_URL="$mount" \
      dotnet ./Mahjong.Server.dll --urls "$scheme://127.0.0.1:0" "${tls_args[@]}" \
      --GamePortal:DataDirectory "$directory" --GamePortal:BehindHttpsProxy false \
      --Logging:LogLevel:Microsoft.Hosting.Lifetime=Information
  ) > "$log" 2>&1 &
  server_pid=$!
  for (( attempt=0; attempt<160; attempt++ )); do
    origin=$(sed -nE 's#^.*Now listening on: (https?://127\.0\.0\.1:[0-9]+)$#\1#p' "$log")
    if [[ $origin == "$scheme://127.0.0.1:"* ]]; then
      return
    fi
    kill -0 "$server_pid" 2>/dev/null || break
    sleep 0.25
  done
  echo "Published server did not start; see $log" >&2
  return 1
}

run_browsers() {
  BASE_URL="$origin${mount%/}/" PLAYWRIGHT_HTML_OPEN=never npm --prefix web run test:e2e -- \
    "$@" --reporter=line,html --output="${SMOKE_PLAYWRIGHT_OUTPUT_DIR:-test-results}"
}

run_standalone_browsers() {
  local spec portal_spec
  local -a specs=()
  if [[ -n ${mount%/} ]]; then
    specs+=(e2e/mount.spec.ts)
  else
    for spec in web/e2e/*.spec.ts; do
      spec=${spec#web/}
      for portal_spec in "${portal_specs[@]}"; do
        if [[ $spec == "$portal_spec" ]]; then continue 2; fi
      done
      specs+=("$spec")
    done
  fi
  run_browsers --project=chromium --project=webkit "${specs[@]}"
}

run_portal_browsers() {
  local spec group browser
  local -a specs=("${portal_specs[@]}")
  if [[ ${mount%/} == /game-portal ]]; then
    specs=(e2e/dice-games.spec.ts e2e/mahjong-audio.spec.ts e2e/lantern-run.spec.ts)
  fi
  for spec in "${specs[@]}"; do
    [[ -s "web/$spec" ]] || { echo "Missing portal browser spec: web/$spec" >&2; return 1; }
  done
  # Every suite gets fresh data and rate limits on its own loopback server.
  for browser in chromium webkit; do
    for spec in "${specs[@]}"; do
      group=${spec##*/}
      group=${group%.spec.ts}
      start_server "$work/browser/$browser/$group" "$work/browser-$browser-$group.log"
      SMOKE_PLAYWRIGHT_OUTPUT_DIR="${SMOKE_PLAYWRIGHT_OUTPUT_DIR:-test-results}/$browser/$group" \
        PLAYWRIGHT_HTML_OUTPUT_DIR="${PLAYWRIGHT_HTML_OUTPUT_DIR:-playwright-report}/$browser/$group" \
        run_browsers --project="$browser" "$spec"
      stop_server
    done
  done
}

start_server "$data" "$work/server.log"
if [[ ${4:-} == --with-browser && $portal == false ]]; then
  run_standalone_browsers
fi
node "$runtime" seed "$origin${mount%/}/" "$version" "$revision" "$state"
stop_server

test -s "$data/mahjong.db"
compgen -G "$data/keys/key-*.xml" >/dev/null
tar -cpf "$archive" -C "$data" .
mkdir -- "$restored"
tar -xpf "$archive" -C "$restored"

start_server "$restored" "$work/restore.log"
node "$runtime" resume "$origin${mount%/}/" "$version" "$revision" "$state"
stop_server

if [[ ${4:-} == --with-browser && $portal == true ]]; then
  run_portal_browsers
fi
