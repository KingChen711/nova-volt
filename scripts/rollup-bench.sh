#!/usr/bin/env sh
# ADR-037: hai phạm vi dùng chung phép đo; preparation là thao tác riêng có ghi.
set -eu
export MSYS_NO_PATHCONV=1
mode=${1:-gate}
case "$mode" in
  gate|line) operation=scripts/sql/rollup-bench.sql ;;
  prepare) operation=scripts/sql/rollup-bench-prepare.sql ;;
  *) printf '%s\n' 'Usage: rollup-bench.sh [gate|line|prepare]' >&2; exit 2 ;;
esac
if [ "$(docker inspect -f '{{.State.Running}}' nvm-timescale 2>/dev/null || echo false)" != true ]; then
  printf '%s\n' 'Missing nvm-timescale. Run: make up' >&2
  exit 2
fi
env_value() { grep -E "^$1=" .env | cut -d= -f2-; }
NVM_POSTGRES_USER=$(env_value NVM_POSTGRES_USER)
NVM_POSTGRES_DB=$(env_value NVM_POSTGRES_DB)
test -r scripts/sql/rollup-bench-fixtures.sql
test -r "$operation"
cat scripts/sql/rollup-bench-fixtures.sql "$operation" |
  docker exec -i nvm-timescale psql -X --set ON_ERROR_STOP=1 \
    --set benchmark_mode="$mode" -U "$NVM_POSTGRES_USER" -d "$NVM_POSTGRES_DB"
