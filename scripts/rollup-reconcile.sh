#!/usr/bin/env sh
# C11 / D5 — proves late telemetry stays visible across raw, parent and machine rollups.

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

if [ -z "$NVM_POSTGRES_USER" ] || [ -z "$NVM_POSTGRES_DB" ]; then
  printf '%s\n' "Missing NVM_POSTGRES_USER or NVM_POSTGRES_DB in .env." >&2
  exit 2
fi

if ! docker exec -i nvm-timescale \
  psql -X --set ON_ERROR_STOP=1 --quiet --no-align --tuples-only \
  -U "$NVM_POSTGRES_USER" -d "$NVM_POSTGRES_DB" \
  < scripts/sql/rollup-reconcile.sql
then
  printf '%s\n' "C11 rollup reconciliation failed; no cleanup was attempted." >&2
  exit 3
fi
