#!/usr/bin/env sh
# Measures C09 compression on two comparable five-day windows created by C08 backfill.

set -eu

MSYS_NO_PATHCONV=1
export MSYS_NO_PATHCONV

if [ "$(docker inspect -f '{{.State.Running}}' nvm-timescale 2>/dev/null || echo false)" != "true" ]; then
  printf '%s\n' "Missing nvm-timescale. Run: make up" >&2
  exit 2
fi

env_value() { grep -E "^$1=" .env | cut -d= -f2-; }

NVM_POSTGRES_USER=$(env_value NVM_POSTGRES_USER)
NVM_POSTGRES_DB=$(env_value NVM_POSTGRES_DB)
export NVM_POSTGRES_USER NVM_POSTGRES_DB

docker exec -i nvm-timescale \
  psql -X --set ON_ERROR_STOP=1 \
  -U "$NVM_POSTGRES_USER" -d "$NVM_POSTGRES_DB" \
  < scripts/sql/compression-report.sql
