#!/usr/bin/env sh
# C14 — compares a site-local SQL date cast with the production calendar on pinned fixtures.

set -eu

if [ "$(docker inspect -f '{{.State.Running}}' nvm-timescale 2>/dev/null || echo false)" != "true" ]; then
  printf '%s\n' "Missing nvm-timescale. Run: make up" >&2
  exit 2
fi

env_value() { grep -E "^$1=" .env | cut -d= -f2-; }

NVM_POSTGRES_USER=$(env_value NVM_POSTGRES_USER)
NVM_POSTGRES_PASSWORD=$(env_value NVM_POSTGRES_PASSWORD)
NVM_POSTGRES_DB=$(env_value NVM_POSTGRES_DB)
NVM_PORT_POSTGRES=$(env_value NVM_PORT_POSTGRES)

export NVM_CALENDAR_LAB_CONNECTION_STRING="Host=localhost;Port=$NVM_PORT_POSTGRES;Database=$NVM_POSTGRES_DB;Username=$NVM_POSTGRES_USER;Password=$NVM_POSTGRES_PASSWORD;Application Name=Nvm.CalendarLab"

dotnet run --project tools/telemetry/Nvm.CalendarLab/Nvm.CalendarLab.csproj \
  --no-launch-profile
