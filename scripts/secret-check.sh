#!/usr/bin/env sh
#
# K13 o tang runtime. Tra loi mot cau: .env tren may nay co dang xac thuc bang mot secret cong khai
# khong?
#
# Ba phep kiem, va phep thu ba la phep quan trong nhat:
#
#   1. Thieu key  — .env.example co, .env khong. `make up` se hong theo kieu kho doan.
#   2. Con placeholder — gia tri van la CHANGE_ME_*, tuc la chua ai dat mat khau.
#   3. Trung mot credential DA TUNG bi commit — doi chieu voi scripts/burned-credentials.sha256.
#
# Phep 3 ton tai vi mot ly do cu the: truoc day .env.example chua mat khau chay duoc, va phep kiem
# chi so sanh .env voi .env.example. Doi .env.example sang placeholder lam phep so sanh do XANH GIA:
# gia tri cu khong con khop file example nua, du no van nam trong lich su git va van cong khai y
# nguyen. Mot secret da push la mot secret khong rut lai duoc; danh sach hash la tri nho cua dieu do.
#
# Script khong bao gio in gia tri, chi in TEN key. Mot script echo ra mat khau no dang phan nan la
# mot script vua chuyen secret sang CI log va terminal scrollback.
#
# Exit 1 khi co bat ky phat hien nao. `make up` va `make up-obs` goi ma khong lam hong build: xoay
# mat khau sau khi volume da tao la quyet dinh cua nguoi van hanh, khong phai cua mot task runner.

set -eu

EXAMPLE=".env.example"
LOCAL=".env"
BURNED="scripts/burned-credentials.sha256"
PLACEHOLDER_PREFIX="CHANGE_ME"

for required in "$EXAMPLE" "$BURNED"; do
  if [ ! -f "$required" ]; then
    echo "secret-check: $required is missing." >&2
    exit 1
  fi
done

if [ ! -f "$LOCAL" ]; then
  echo "secret-check: no $LOCAL yet. Run: cp $EXAMPLE $LOCAL" >&2
  exit 1
fi

# `sha256sum` tren Linux va Git Bash, `shasum -a 256` tren macOS. Chon mot lan, khong chon lai moi dong.
if command -v sha256sum >/dev/null 2>&1; then
  hash_of() { printf '%s' "$1" | sha256sum | cut -d' ' -f1; }
elif command -v shasum >/dev/null 2>&1; then
  hash_of() { printf '%s' "$1" | shasum -a 256 | cut -d' ' -f1; }
else
  echo "secret-check: neither sha256sum nor shasum is available." >&2
  exit 1
fi

# Chi nhung key that su xac thuc mot thu gi do, va chi khi tu khoa la mot tu dung cuoi: 'KEY' khong
# neo se khop ca NVM_PORT_KEYCLOAK, va mot phep kiem la lang ve so hieu cong la mot phep kiem nguoi
# ta hoc cach luot qua. Ten dang nhap co y nam ngoai pham vi vi cung ly do — no khong phai secret.
KEYS=$(grep -Eo '^[A-Z0-9_]+_(PASSWORD|SECRET|TOKEN|KEY)=' "$EXAMPLE" | sed 's/=$//' | sort -u)

missing=""
placeholder=""
burned=""

for key in $KEYS; do
  local_value=$(grep -E "^${key}=" "$LOCAL" | head -n 1 | cut -d= -f2-)

  if [ -z "$local_value" ]; then
    missing="$missing $key"
    continue
  fi

  case "$local_value" in
    "$PLACEHOLDER_PREFIX"*)
      placeholder="$placeholder $key"
      continue
      ;;
  esac

  if grep -qi "^$(hash_of "$local_value")  " "$BURNED"; then
    burned="$burned $key"
  fi
done

status=0

if [ -n "$missing" ]; then
  echo "secret-check: these keys exist in $EXAMPLE but not in $LOCAL:" >&2
  for key in $missing; do echo "  - $key" >&2; done
  status=1
fi

if [ -n "$placeholder" ]; then
  echo "secret-check: these keys in $LOCAL are still the ${PLACEHOLDER_PREFIX}_* placeholder:" >&2
  for key in $placeholder; do echo "  - $key" >&2; done
  status=1
fi

if [ -n "$burned" ]; then
  echo "secret-check: these credentials in $LOCAL were published in this repo's git history:" >&2
  for key in $burned; do echo "  - $key" >&2; done
  echo "" >&2
  echo "  They are public and cannot be un-published. They authenticate the running containers." >&2
  echo "  Rotate them in place, keeping every volume: docs/runbook.md section 1." >&2
  echo "  Do NOT use 'make down-v' - it destroys the M3 evidence dataset and the locked raw-curve" >&2
  echo "  objects, and rotating a password is not a reason to delete data." >&2
  status=1
fi

if [ "$status" -eq 0 ]; then
  echo "secret-check: OK. No credential in $LOCAL is a placeholder or a published value."
fi

exit "$status"
