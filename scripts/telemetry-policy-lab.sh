#!/usr/bin/env sh
#
# C06 destructive lab. It proves two properties on the real compose database:
# 1. a row whose device clock is 500 days behind lands in an expired chunk and an explicit
#    drop_chunks removes it — recorded seconds ago, deleted anyway, no error. Migration 007
#    unscheduled the retention job, so the lab also asserts that no such job is scheduled;
# 2. inserting into an already-compressed chunk remains possible, with the elapsed-time cost printed.
#
# The script never resets data. The global claim rows deliberately survive telemetry retention,
# preserving the rule that an ancient replay cannot become new again (ADR-030).

set -eu

MSYS_NO_PATHCONV=1
export MSYS_NO_PATHCONV

if [ "$(docker inspect -f '{{.State.Running}}' nvm-timescale 2>/dev/null || echo false)" != "true" ]; then
  printf '%s\n' "Missing nvm-timescale. Run: make up" >&2
  exit 2
fi

NVM_POSTGRES_USER=$(grep -E '^NVM_POSTGRES_USER=' .env | cut -d= -f2-)
NVM_POSTGRES_DB=$(grep -E '^NVM_POSTGRES_DB=' .env | cut -d= -f2-)
export NVM_POSTGRES_USER NVM_POSTGRES_DB

docker exec -i nvm-timescale \
  psql -X --set ON_ERROR_STOP=1 -U "$NVM_POSTGRES_USER" -d "$NVM_POSTGRES_DB" \
  < scripts/sql/telemetry-policy-lab.sql
