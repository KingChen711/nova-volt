#!/usr/bin/env sh
# C10 / D2 — benchmarks the seven-day, one-minute Temperature query on a pinned fixture.
#
# The 100-channel fixture is deliberately NOT generated here. Replaying its deterministic
# duplicate stream is expensive and would contaminate both the operator workflow and the
# benchmark's thermal/cache conditions.

set -eu

MSYS_NO_PATHCONV=1
export MSYS_NO_PATHCONV

fixture_command="make telemetry-backfill CHANNELS=100 DAYS=7 END_AT=2026-07-27T00:00:00Z SAMPLE_PERIOD_SECONDS=5 DRIFTED_RATE=0"

printf '%s\n' "C10 fixture preparation (one time): $fixture_command"

if [ "$(docker inspect -f '{{.State.Running}}' nvm-timescale 2>/dev/null || echo false)" != "true" ]; then
  printf '%s\n' "Missing nvm-timescale. Run: make up" >&2
  exit 2
fi

env_value() { grep -E "^$1=" .env | cut -d= -f2-; }

NVM_POSTGRES_USER=$(env_value NVM_POSTGRES_USER)
NVM_POSTGRES_DB=$(env_value NVM_POSTGRES_DB)
export NVM_POSTGRES_USER NVM_POSTGRES_DB

if ! docker exec -i nvm-timescale \
  psql -X --set ON_ERROR_STOP=1 \
  -U "$NVM_POSTGRES_USER" -d "$NVM_POSTGRES_DB" \
  < scripts/sql/rollup-bench.sql
then
  printf '%s\n' "C10 failed. If the target fixture is absent, run once:" >&2
  printf '  %s\n' "$fixture_command" >&2
  exit 3
fi
