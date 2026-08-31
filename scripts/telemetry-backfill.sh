#!/usr/bin/env sh
# Runs the C08 historical generator against the local TimescaleDB and records storage at both sides.

set -eu

MSYS_NO_PATHCONV=1
export MSYS_NO_PATHCONV

if [ "$(docker inspect -f '{{.State.Running}}' nvm-timescale 2>/dev/null || echo false)" != "true" ]; then
  printf '%s\n' "Missing nvm-timescale. Run: make up" >&2
  exit 2
fi

env_value() { grep -E "^$1=" .env | cut -d= -f2-; }
disk_free() { docker exec nvm-timescale df -B1 /var/lib/postgresql/data | tail -n 1 | tr -s ' ' | cut -d' ' -f4; }
database_bytes() {
  docker exec nvm-timescale psql -X -U "$NVM_POSTGRES_USER" -d "$NVM_POSTGRES_DB" -Atc \
    "SELECT pg_database_size(current_database());"
}

NVM_POSTGRES_USER=$(env_value NVM_POSTGRES_USER)
NVM_POSTGRES_PASSWORD=$(env_value NVM_POSTGRES_PASSWORD)
NVM_POSTGRES_DB=$(env_value NVM_POSTGRES_DB)
export NVM_POSTGRES_USER NVM_POSTGRES_DB

disk_before=$(disk_free)
database_before=$(database_bytes)

printf '%s\n' \
  "NVM_BACKFILL_STORAGE phase=before disk_free_bytes=$disk_before database_bytes=$database_before"

export NVM_BACKFILL_CONNECTION_STRING="Host=localhost;Port=5432;Database=$NVM_POSTGRES_DB;Username=$NVM_POSTGRES_USER;Password=$NVM_POSTGRES_PASSWORD;Application Name=Nvm.TelemetryBackfill"
export NVM_BACKFILL_CHANNELS="${CHANNELS:-8}"
export NVM_BACKFILL_DAYS="${DAYS:-1}"
export NVM_BACKFILL_END_AT="${END_AT:-2026-08-29T00:00:00Z}"
export NVM_BACKFILL_SAMPLE_PERIOD_SECONDS="${SAMPLE_PERIOD_SECONDS:-5}"
export NVM_BACKFILL_DRIFTED_RATE="${DRIFTED_RATE:-0}"
export NVM_BACKFILL_CLOCK_DRIFT_HOURS="${CLOCK_DRIFT_HOURS:-2}"
export NVM_BACKFILL_BATCH_SIZE="${BATCH_SIZE:-50000}"

dotnet run --project tools/telemetry/Nvm.TelemetryBackfill/Nvm.TelemetryBackfill.csproj \
  --no-launch-profile

disk_after=$(disk_free)
database_after=$(database_bytes)

printf '%s\n' \
  "NVM_BACKFILL_STORAGE phase=after disk_free_bytes=$disk_after database_bytes=$database_after database_delta_bytes=$((database_after - database_before))"
