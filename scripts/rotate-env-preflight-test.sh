#!/usr/bin/env sh
# Pure negative-control matrix for rotate-env-preflight.sh. No real .env or service is read.

set -eu

SCRIPT_DIR=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
PREFLIGHT="$SCRIPT_DIR/rotate-env-preflight.sh"
ROTATE_SCRIPT="$SCRIPT_DIR/rotate-credentials.sh"
REAL_MANIFEST="$SCRIPT_DIR/burned-credentials.sha256"
TEMP_DIR=$(mktemp -d "${TMPDIR:-/tmp}/nvm-rotate-preflight.XXXXXX")
trap 'rm -rf "$TEMP_DIR"' EXIT HUP INT TERM

NVM_ROTATE_PREFLIGHT_SOURCE_ONLY=1
. "$PREFLIGHT"

KEYS='NVM_POSTGRES_PASSWORD
NVM_GRAFANA_DB_PASSWORD
NVM_MSSQL_SA_PASSWORD
NVM_MSSQL_APP_PASSWORD
NVM_RABBITMQ_PASSWORD
NVM_EMQX_PASSWORD
NVM_MINIO_PASSWORD
NVM_KEYCLOAK_PASSWORD
NVM_GRAFANA_ADMIN_PASSWORD'

write_fixture() {
  target=$1
  prefix=$2
  : > "$target"
  index=0
  for key in $KEYS; do
    index=$((index + 1))
    printf '%s=%s_%02d_Aa9\n' "$key" "$prefix" "$index" >> "$target"
  done
}

write_manifest() {
  target=$1
  source_env=$2
  : > "$target"
  for key in $KEYS; do
    value=$(awk -v wanted="$key" '
      index($0, "=") > 0 && substr($0, 1, index($0, "=") - 1) == wanted {
        print substr($0, index($0, "=") + 1)
        exit
      }
    ' "$source_env")
    printf '%s  %s\n' "$(rotation_hash_of "$value")" "$key" >> "$target"
  done
}

write_old_variant() {
  target=$1
  mode=$2
  target_key=$3
  : > "$target"
  index=0
  for key in $KEYS; do
    index=$((index + 1))
    if [ "$mode" = missing ] && [ "$key" = "$target_key" ]; then
      continue
    fi

    if [ "$mode" = empty ] && [ "$key" = "$target_key" ]; then
      printf '%s=\n' "$key" >> "$target"
    elif [ "$mode" = unchanged ] && [ "$key" = "$target_key" ]; then
      printf '%s=current_%02d_Aa9\n' "$key" "$index" >> "$target"
    else
      printf '%s=previous_%02d_Aa9\n' "$key" "$index" >> "$target"
    fi
  done

  if [ "$mode" = duplicate ]; then
    printf '%s=duplicate_Aa9\n' "$target_key" >> "$target"
  fi
}

passed=0
failed=0

expect_accepts() {
  label=$1
  shift
  if rotation_env_preflight "$@" >/dev/null; then
    passed=$((passed + 1))
  else
    printf 'FAIL: expected preflight to accept %s\n' "$label" >&2
    failed=$((failed + 1))
  fi
}

expect_rejects() {
  label=$1
  shift
  if rotation_env_preflight "$@" >/dev/null 2>&1; then
    printf 'FAIL: expected preflight to reject %s\n' "$label" >&2
    failed=$((failed + 1))
  else
    passed=$((passed + 1))
  fi
}

current="$TEMP_DIR/current.env"
old="$TEMP_DIR/old.env"
candidate="$TEMP_DIR/candidate.env"
manifest="$TEMP_DIR/burned.sha256"
write_fixture "$current" current
write_fixture "$old" previous
write_manifest "$manifest" "$old"

expect_accepts 'one complete changed backup bound to its manifest' "$current" "$old" "$manifest"
expect_rejects 'no backup' "$current" '' "$manifest"
expect_rejects '.env.example placeholders' "$current" "$SCRIPT_DIR/../.env.example" "$REAL_MANIFEST"
expect_rejects 'arbitrary synthetic backup against the repository manifest' "$current" "$old" "$REAL_MANIFEST"
expect_rejects 'missing burned-credential manifest' "$current" "$old" "$TEMP_DIR/missing.sha256"

cp "$old" "$candidate"
sed -i 's/^NVM_POSTGRES_PASSWORD=.*/NVM_POSTGRES_PASSWORD=tampered_Aa9/' "$candidate"
expect_rejects 'one tampered backup value against the bound manifest' "$current" "$candidate" "$manifest"

wrong_key_manifest="$TEMP_DIR/wrong-key.sha256"
awk '
  $2 == "NVM_POSTGRES_PASSWORD" { $2 = "NVM_RABBITMQ_PASSWORD" }
  $2 == "NVM_RABBITMQ_PASSWORD" { $2 = "NVM_POSTGRES_PASSWORD" }
  { print $1 "  " $2 }
' "$manifest" > "$wrong_key_manifest"
expect_rejects 'digest recorded for a different key' "$current" "$old" "$wrong_key_manifest"

missing_key_manifest="$TEMP_DIR/missing-key.sha256"
awk '$2 != "NVM_MINIO_PASSWORD"' "$manifest" > "$missing_key_manifest"
expect_rejects 'manifest missing one credential key' "$current" "$old" "$missing_key_manifest"

for key in $KEYS; do
  write_old_variant "$candidate" missing "$key"
  expect_rejects "missing $key" "$current" "$candidate" "$manifest"

  write_old_variant "$candidate" empty "$key"
  expect_rejects "empty $key" "$current" "$candidate" "$manifest"

  write_old_variant "$candidate" duplicate "$key"
  expect_rejects "duplicate $key" "$current" "$candidate" "$manifest"

  write_old_variant "$candidate" unchanged "$key"
  expect_rejects "unchanged $key" "$current" "$candidate" "$manifest"
done

# A missing RabbitMQ node must stop rotate-credentials.sh before it copies .env or changes the first
# service. The fake Docker command makes every earlier node healthy and RabbitMQ unavailable.
guard_dir="$TEMP_DIR/missing-rabbit"
fake_bin="$guard_dir/bin"
mkdir -p "$fake_bin"
: > "$guard_dir/.env"
cat > "$fake_bin/docker" <<'SCRIPT'
#!/usr/bin/env sh
if [ "${1:-}" != inspect ]; then
  exit 99
fi
for argument in "$@"; do container=$argument; done
if [ "$container" = nvm-rabbitmq ]; then
  printf 'false\n'
else
  printf 'true\n'
fi
SCRIPT
chmod +x "$fake_bin/docker"

if (cd "$guard_dir" && PATH="$fake_bin:$PATH" sh "$ROTATE_SCRIPT") >/dev/null 2>&1; then
  printf 'FAIL: expected missing RabbitMQ to stop credential rotation\n' >&2
  failed=$((failed + 1))
elif find "$guard_dir" -maxdepth 1 -name '.env.rotate-backup.*' | grep -q .; then
  printf 'FAIL: missing RabbitMQ was detected only after the backup/mutation boundary\n' >&2
  failed=$((failed + 1))
else
  passed=$((passed + 1))
fi

printf 'ROTATE_PREFLIGHT PASS=%s FAIL=%s\n' "$passed" "$failed"
[ "$failed" -eq 0 ]
