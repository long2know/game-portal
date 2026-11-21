#!/usr/bin/env bash
set -euo pipefail

if (( $# != 3 )); then
  echo "Usage: bash scripts/smoke-container.sh IMAGE VERSION COMMIT" >&2
  exit 2
fi

image=$1
version=$2
revision=$3
umask 077
mkdir -p artifacts
name="portal-smoke-$$-$RANDOM"
work="$PWD/artifacts/$name"
mkdir -- "$work"
volume="$name-data"
restored="$name-restored"
archive="$work/data.tar"
state="$work/guest.json"
container=
volumes=()

cleanup() {
  local status=$?
  if [[ -n $container ]]; then
    if (( status != 0 )); then
      docker logs --tail 100 "$container" >&2 || true
    fi
    docker rm -f "$container" >/dev/null 2>&1 || true
  fi
  if (( ${#volumes[@]} > 0 )); then
    docker volume rm "${volumes[@]}" >/dev/null 2>&1 || true
  fi
  rm -rf -- "$work"
}
trap cleanup EXIT

actual_version=$(docker image inspect --format '{{ index .Config.Labels "org.opencontainers.image.version" }}' "$image")
actual_revision=$(docker image inspect --format '{{ index .Config.Labels "org.opencontainers.image.revision" }}' "$image")
if [[ $actual_version != "$version" || $actual_revision != "$revision" ]]; then
  echo "Expected image labels version=$version revision=$revision, got version=$actual_version revision=$actual_revision" >&2
  exit 1
fi
test "$(docker image inspect --format '{{.Os}}/{{.Architecture}}' "$image")" = linux/amd64
test "$(docker image inspect --format '{{json .Config.Entrypoint}}' "$image")" = '["dotnet","Mahjong.Server.dll"]'
test "$(docker image inspect --format '{{ index .Config.Labels "org.opencontainers.image.source" }}' "$image")" \
  = https://github.com/long2know/game-portal
printf 'Runtime image %s version=%s revision=%s\n' "$(docker image inspect --format '{{.Id}}' "$image")" "$actual_version" "$actual_revision"

start_container() {
  local volume=$1
  shift
  container=$(docker run -d --pull=never --name "$name" --cap-drop ALL --security-opt no-new-privileges:true \
    --env Mahjong__AllowHttpLoopbackOrigin=true "$@" \
    --publish 127.0.0.1::8080 --mount "type=volume,source=$volume,target=/data" "$image")
  origin="http://$(docker port "$container" 8080/tcp)"
}

stop_container() {
  docker stop --time 30 "$container" >/dev/null
  if [[ $(docker inspect --format '{{.State.ExitCode}}' "$container") != 0 ]]; then
    echo "Container failed to stop gracefully; will not archive its SQLite files" >&2
    return 1
  fi
  docker rm "$container" >/dev/null
  container=
}

wait_healthy() {
  local status
  for (( attempt=0; attempt<30; attempt++ )); do
    status=$(docker inspect --format '{{.State.Health.Status}}' "$container")
    case "$status" in
      healthy) return ;;
      unhealthy) break ;;
    esac
    sleep 1
  done
  echo "Docker readiness healthcheck did not become healthy" >&2
  docker inspect --format '{{json .State.Health}}' "$container" >&2
  return 1
}

docker volume create "$volume" >/dev/null
volumes+=("$volume")
baseline=(--env BASE_URL=/ --env Portal__Enabled=false)
start_container "$volume" "${baseline[@]}"
docker exec "$container" bash -ec '
  test "$(id -u)" -ne 0
  test -z "$(dotnet --list-sdks)"
  test -z "$(command -v node)"
  test -w /data
  test -w /data/keys
  for notice in LICENSE licenses/{THIRD-PARTY,Microsoft.NET-MIT,Apache-2.0,SQLitePCLRaw-NOTICE,Microsoft.Extensions.DependencyModel-NOTICES}.txt; do
    test -s "/app/$notice"
  done
'
wait_healthy
node scripts/smoke-runtime.mjs seed "$origin" "$version" "$revision" "$state"
docker exec "$container" bash -ec 'test -s /data/mahjong.db; compgen -G "/data/keys/key-*.xml" >/dev/null'
stop_container

start_container "$volume" "${baseline[@]}"
node scripts/smoke-runtime.mjs resume "$origin" "$version" "$revision" "$state"
stop_container

docker run --rm --entrypoint tar --mount "type=volume,source=$volume,target=/data,readonly" "$image" \
  -C /data -cpf - . > "$archive"
docker volume create "$restored" >/dev/null
volumes+=("$restored")
docker run --rm -i --entrypoint tar --mount "type=volume,source=$restored,target=/data" "$image" \
  -C /data -xpf - < "$archive"

start_container "$restored" "${baseline[@]}"
node scripts/smoke-runtime.mjs resume "$origin" "$version" "$revision" "$state"
stop_container

mounted="$name-mounted"
docker volume create "$mounted" >/dev/null
volumes+=("$mounted")
start_container "$mounted" --env BASE_URL=/mahjong --env Portal__Enabled=false
wait_healthy
node scripts/smoke-runtime.mjs seed "$origin/mahjong/" "$version" "$revision" "$work/guest-mounted.json"
stop_container

portal="$name-portal"
docker volume create "$portal" >/dev/null
volumes+=("$portal")
# No overrides: this path must work with the production image defaults.
start_container "$portal"
wait_healthy
docker exec "$container" bash -ec '
  test "$BASE_URL" = /portal
  test "$Portal__Enabled" = true
  test "$Portal__StockfishPath" = /usr/games/stockfish
  test -s /app/licenses/Gera.Chess-MIT.txt
  test -s /app/licenses/Ardalis.SmartEnum-MIT.txt
  test -s /usr/share/doc/stockfish/copyright
  test -s /usr/share/common-licenses/GPL-3
  test -s /usr/share/common-licenses/CC0-1.0
  cd /usr/share/doc/stockfish/source
  for file in stockfish_16-1build1.dsc stockfish_16.orig.tar.gz stockfish_16-1build1.debian.tar.xz; do
    test -s "$file"
  done
  response=$({ printf "uci\nisready\nposition startpos\ngo movetime 75\n"; sleep 1.5; printf "quit\n"; } \
    | timeout 10 "$Portal__StockfishPath")
  grep -Fx "id name Stockfish 16" <<< "$response"
  grep -Fx uciok <<< "$response"
  grep -Fx readyok <<< "$response"
  grep -Eq "^bestmove [a-h][1-8][a-h][1-8][qrbn]?( |$)" <<< "$response"
'
node scripts/smoke-portal.mjs seed "$origin/portal/" "$version" "$revision" "$work/guest-portal.json"
portal_log=$(docker logs "$container" 2>&1)
if grep -F 'Stockfish unavailable:' <<< "$portal_log"; then
  echo 'The production image fell back to the built-in chess bot.' >&2
  exit 1
fi
stop_container

start_container "$portal"
wait_healthy
node scripts/smoke-portal.mjs resume "$origin/portal/" "$version" "$revision" "$work/guest-portal.json"
stop_container
