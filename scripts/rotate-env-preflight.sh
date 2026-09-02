#!/usr/bin/env sh
#
# Structural and provenance guard for rotate-verify.sh. It deliberately performs no network or
# container I/O, so every false-pass case can run in CI without a live stack. It prints key names
# only, never values.

set -eu

KEYS='NVM_POSTGRES_PASSWORD
NVM_GRAFANA_DB_PASSWORD
NVM_MSSQL_SA_PASSWORD
NVM_MSSQL_APP_PASSWORD
NVM_RABBITMQ_PASSWORD
NVM_EMQX_PASSWORD
NVM_MINIO_PASSWORD
NVM_KEYCLOAK_PASSWORD
NVM_GRAFANA_ADMIN_PASSWORD'

SCRIPT_DIR=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
DEFAULT_BURNED_MANIFEST="$SCRIPT_DIR/burned-credentials.sha256"

if command -v sha256sum >/dev/null 2>&1; then
  rotation_hash_of() { printf '%s' "$1" | sha256sum | cut -d' ' -f1; }
elif command -v shasum >/dev/null 2>&1; then
  rotation_hash_of() { printf '%s' "$1" | shasum -a 256 | cut -d' ' -f1; }
else
  rotation_hash_of() {
    printf 'rotate-preflight: neither sha256sum nor shasum is available.\n' >&2
    return 2
  }
fi

rotation_env_preflight() {
  CURRENT_ENV="${1:-}"
  OLD_ENV="${2:-}"
  BURNED_MANIFEST="${3:-$DEFAULT_BURNED_MANIFEST}"
  if [ -z "$CURRENT_ENV" ] || [ ! -r "$CURRENT_ENV" ]; then
    printf 'rotate-preflight: current environment file is missing or unreadable.\n' >&2
    return 2
  fi

  if [ -z "$OLD_ENV" ] || [ ! -r "$OLD_ENV" ]; then
    printf 'rotate-preflight: the pre-rotation backup is required and must be readable.\n' >&2
    return 2
  fi

  if [ -z "$BURNED_MANIFEST" ] || [ ! -r "$BURNED_MANIFEST" ]; then
    printf 'rotate-preflight: the burned-credential manifest is required and must be readable.\n' >&2
    return 2
  fi

  # Parse both files once. Besides being simpler, this avoids spawning dozens of grep processes on
  # Windows; the same negative-control matrix then stays cheap enough to belong in every CI run.
  if ! awk -v required="$KEYS" '
    BEGIN {
      split(required, key_list)
      for (slot in key_list) {
        wanted[key_list[slot]] = 1
      }
    }

    {
      line = $0
      sub(/\r$/, "", line)
      separator = index(line, "=")
      if (separator == 0) {
        next
      }

      key = substr(line, 1, separator - 1)
      if (!(key in wanted)) {
        next
      }

      value = substr(line, separator + 1)
      if (FILENAME == ARGV[1]) {
        current_count[key]++
        current_value[key] = value
      } else {
        backup_count[key]++
        backup_value[key] = value
      }
    }

    END {
      failed = 0
      for (slot in key_list) {
        key = key_list[slot]
        if (current_count[key] != 1) {
          print "rotate-preflight: current must contain " key " exactly once." > "/dev/stderr"
          failed = 1
        } else if (current_value[key] == "") {
          print "rotate-preflight: current has an empty " key "." > "/dev/stderr"
          failed = 1
        } else if (current_value[key] ~ /^CHANGE_ME_/) {
          print "rotate-preflight: current still has the placeholder for " key "." > "/dev/stderr"
          failed = 1
        }

        if (backup_count[key] != 1) {
          print "rotate-preflight: backup must contain " key " exactly once." > "/dev/stderr"
          failed = 1
        } else if (backup_value[key] == "") {
          print "rotate-preflight: backup has an empty " key "." > "/dev/stderr"
          failed = 1
        } else if (backup_value[key] ~ /^CHANGE_ME_/) {
          print "rotate-preflight: backup still has the placeholder for " key "." > "/dev/stderr"
          failed = 1
        }

        if (current_count[key] == 1 && backup_count[key] == 1 &&
            current_value[key] != "" && backup_value[key] != "" &&
            current_value[key] == backup_value[key]) {
          print "rotate-preflight: " key " did not change between backup and current environment." > "/dev/stderr"
          failed = 1
        }
      }

      exit failed ? 2 : 0
    }
  ' "$CURRENT_ENV" "$OLD_ENV"; then
    return 2
  fi

  # `old != current` only proves that two files differ. It does not prove the alleged backup is the
  # pre-rotation snapshot whose credentials were actually burned. Bind every old value to the
  # committed manifest by BOTH digest and key; matching a digest published for another service is
  # not sufficient. The manifest deliberately supports more than one historical digest per key.
  matched=0
  for key in $KEYS; do
    backup_value=$(awk -v wanted="$key" '
      {
        line = $0
        sub(/\r$/, "", line)
        separator = index(line, "=")
        if (separator > 0 && substr(line, 1, separator - 1) == wanted) {
          print substr(line, separator + 1)
          exit
        }
      }
    ' "$OLD_ENV")
    backup_digest=$(rotation_hash_of "$backup_value") || return 2

    if ! awk -v digest="$backup_digest" -v wanted="$key" '
      $1 == digest && $2 == wanted { found = 1 }
      END { exit found ? 0 : 1 }
    ' "$BURNED_MANIFEST"; then
      printf 'rotate-preflight: backup %s does not match the burned-credential manifest.\n' "$key" >&2
      return 2
    fi
    matched=$((matched + 1))
  done

  printf 'rotate-preflight: backup matches burned credential manifest (%s/9).\n' "$matched"
}

if [ "${NVM_ROTATE_PREFLIGHT_SOURCE_ONLY:-0}" != "1" ]; then
  rotation_env_preflight "${1:-}" "${2:-}" "${3:-$DEFAULT_BURNED_MANIFEST}"
fi
