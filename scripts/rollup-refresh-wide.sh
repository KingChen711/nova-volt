#!/usr/bin/env sh
#
# Refreshes a closed historical interval of the one-minute process-signal rollup. Optional inputs:
#   ROLLUP_FROM=2026-08-20T00:00:00Z ROLLUP_TO=2026-08-27T00:00:00Z make rollup-refresh-wide
# Omitting both chooses a paired seven-day interval ending one complete minute behind now.

set -eu

MSYS_NO_PATHCONV=1
export MSYS_NO_PATHCONV

if [ "$(docker inspect -f '{{.State.Running}}' nvm-timescale 2>/dev/null || echo false)" != "true" ]; then
  printf '%s\n' "Missing nvm-timescale. Run: make up" >&2
  exit 2
fi

NVM_POSTGRES_USER=$(grep -E '^NVM_POSTGRES_USER=' .env | cut -d= -f2-)
NVM_POSTGRES_DB=$(grep -E '^NVM_POSTGRES_DB=' .env | cut -d= -f2-)

docker exec -i nvm-timescale \
  psql -X --set ON_ERROR_STOP=1 \
    --set rollup_from="${ROLLUP_FROM:-}" \
    --set rollup_to="${ROLLUP_TO:-}" \
    -U "$NVM_POSTGRES_USER" -d "$NVM_POSTGRES_DB" \
  < scripts/sql/rollup-refresh-wide.sql
