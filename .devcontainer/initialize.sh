#!/usr/bin/env bash
set -e
cd "$(dirname "$0")"


[ -f compose.override.local.yml ] || cp compose.override.local.yml.sample compose.override.local.yml
[ -f Dockerfile.local ] || cp Dockerfile.local.sample Dockerfile.local

source .env

echo "→ building team base image"
docker build . \
  -t "${PROJECT_SLUG}-devcontainer:latest"
