#!/usr/bin/env bash
set -euo pipefail

if (( $# != 4 )) || [[ $1 != save && $1 != load ]]; then
  echo 'Usage: bash scripts/smoke-image-archive.sh {save|load} ARCHIVE_DIR TESTED_IMAGE_ID COMMIT' >&2
  exit 2
fi

operation=$1
directory=$2
image_id=$3
revision=$4
[[ $image_id =~ ^sha256:[0-9a-f]{64}$ && $revision =~ ^[0-9a-f]{40}$ ]]
tag="ghcr.io/long2know/game-portal:sha-$revision"
archive=game-portal-linux-amd64.tar.gz

if [[ $operation == save ]]; then
  umask 077
  mkdir -- "$directory"
  cd -- "$directory"
  docker image tag "$image_id" "$tag"
  docker image save "$tag" | gzip -n -1 > "$archive"
  sha256sum -- "$archive" > "$archive.sha256"
else
  cd -- "$directory"
  [[ "$(cat -- "$archive.sha256")" == "$(sha256sum -- "$archive")" ]]
  if docker image inspect "$tag" >/dev/null 2>&1; then
    echo "Refusing to load over an existing image: $tag" >&2
    exit 1
  fi
  docker image load --input "$archive"
fi

if [[ "$(docker image inspect --format '{{.Id}}' "$tag")" != "$image_id" ]]; then
  echo "Image from $archive must match the smoke-tested $image_id" >&2
  exit 1
fi
[[ "$(docker image inspect --format '{{.Os}}/{{.Architecture}}' "$tag")" == linux/amd64 ]]
[[ "$(docker image inspect --format '{{ index .Config.Labels "org.opencontainers.image.revision" }}' "$tag")" == "$revision" ]]
