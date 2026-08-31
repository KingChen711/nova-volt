#!/usr/bin/env sh
# C15-3 — do cung truy van cua D2 o muc LINE `F1` (1.000 kenh), lam EVIDENCE cho M6/M7.
#
# Khong phai gate M3: D2 da dat o 144,061 ms tren `FORM-01` (100 kenh), va `ADR-034` chot
# rang so cua line duoc GHI chu khong duoc dung de sieu DoD sau khi da do.
#
# Fixture sinh mot lan, KHONG sinh o day: mot benchmark tu sinh du lieu ngay truoc khi do la
# mot benchmark do luon trang thai cache cua chinh lan sinh do.

set -eu

MSYS_NO_PATHCONV=1
export MSYS_NO_PATHCONV

fixture_command="make telemetry-backfill CHANNELS=1000 DAYS=7 END_AT=2026-05-20T00:00:00Z SAMPLE_PERIOD_SECONDS=5 DRIFTED_RATE=0"

printf '%s\n' "C15-3 fixture preparation (one time): $fixture_command"

if [ "$(docker inspect -f '{{.State.Running}}' nvm-timescale 2>/dev/null || echo false)" != "true" ]; then
  printf '%s\n' "Missing nvm-timescale. Run: make up" >&2
  exit 2
fi

env_value() { grep -E "^$1=" .env | cut -d= -f2-; }

NVM_POSTGRES_USER=$(env_value NVM_POSTGRES_USER)
NVM_POSTGRES_DB=$(env_value NVM_POSTGRES_DB)
export NVM_POSTGRES_USER NVM_POSTGRES_DB

# 0 = chua ghim. Lan chay dau in fingerprint roi dung lai kem dung con so can ghim.
EXPECTED_ROWS="${EXPECTED_ROWS:-0}"

if ! docker exec -i nvm-timescale \
  psql -X --set ON_ERROR_STOP=1 --set expected_rows="$EXPECTED_ROWS" \
  -U "$NVM_POSTGRES_USER" -d "$NVM_POSTGRES_DB" \
  < scripts/sql/rollup-bench-line.sql
then
  printf '%s\n' "C15-3 failed. Neu fixture chua co, chay mot lan:" >&2
  printf '  %s\n' "$fixture_command" >&2
  printf '%s\n' "Neu no bao 'expected_rows chua duoc ghim', chay lai voi con so no in ra:" >&2
  printf '  %s\n' "EXPECTED_ROWS=<so> make rollup-bench-line" >&2
  exit 3
fi
