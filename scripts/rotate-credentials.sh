#!/usr/bin/env sh
#
# Xoay ca 9 credential TAI CHO, giu nguyen moi volume va moi dataset.
#
# Duong thu cong o docs/runbook.md §1 la duong MAC DINH, va no ton tai vi mot ly do:
# nguoi giu secret nen la nguoi dat secret (AGENTS.md §3.6, ADR-034 §O8).
# Script nay la duong owner chon cho rieng may local nay, 2026-08-31, khi doi lay
# 20 phut thao tac bang mot lenh. No KHONG thay the runbook — runbook giai thich
# CO CHE, script chi thi hanh no.
#
# Ba tinh chat script nay giu:
#
#   1. KHONG BAO GIO in mot gia tri credential. Chi in ten khoa va OK/FAIL. Gia tri
#      moi duoc sinh, dung, roi ghi thang vao .env — khong qua man hinh, khong qua
#      shell history, khong qua doi so dong lenh cua chinh script nay.
#   2. KHONG dung `down -v`, khong xoa volume, khong dung toi dataset.
#   3. Cap nhat .env cho TUNG credential NGAY SAU khi service do xoay xong. Neu mot
#      buoc hong o giua, .env van khop voi thuc te tinh toi buoc do — khong bao gio
#      roi vao trang thai "file noi mot dang, service noi mot dang".
#
# Gioi han da biet, ghi ra thay vi giau: mot so lenh nhan mat khau qua doi so ben
# TRONG container (sqlcmd -P, curl -d cua EMQX), nen gia tri co the thay duoc trong
# `ps` cua container do vai mili-giay. Tren mot stack dev local do la danh doi chap
# nhan duoc; tren production thi khong, va luc do phai dung duong thu cong.
#
# Chay:  sh scripts/rotate-credentials.sh
# Sau do: make down && make up-obs && make ingestion-up

set -eu

MSYS_NO_PATHCONV=1
export MSYS_NO_PATHCONV

LOCAL=".env"
BACKUP="$LOCAL.rotate-backup.$(date +%Y%m%d-%H%M%S)"

# ─────────────────────────────────────────────────────────── guards ──

if [ ! -f "$LOCAL" ]; then
  echo "rotate: khong tim thay $LOCAL." >&2
  exit 1
fi

for container in nvm-timescale nvm-mssql nvm-rabbitmq nvm-emqx nvm-grafana; do
  if [ "$(docker inspect -f '{{.State.Running}}' "$container" 2>/dev/null || echo false)" != "true" ]; then
    echo "rotate: container $container khong chay. Chay 'make up-obs' truoc." >&2
    exit 1
  fi
done

cp "$LOCAL" "$BACKUP"
echo "rotate: da sao luu $LOCAL -> $BACKUP (.env.* nam trong .gitignore)"
echo ""

# ────────────────────────────────────────────────────────── helpers ──

env_value() { grep -E "^$1=" "$LOCAL" | head -n 1 | cut -d= -f2-; }

# Ghi de dung mot dong. Gia tri chi gom [A-Za-z0-9] nen khong co ky tu nao la
# metacharacter cua sed — day la ly do ham gen() gioi han bang chu do.
set_env() {
  sed -i "s|^$1=.*|$1=$2|" "$LOCAL"
}

# 32 ky tu, bat buoc co ca chu hoa, chu thuong va so: login nvm_app duoc tao voi
# CHECK_POLICY = ON nen SQL Server tu choi mat khau khong du ba lop.
gen() {
  attempt=0
  while [ "$attempt" -lt 50 ]; do
    attempt=$((attempt + 1))
    candidate=$(LC_ALL=C tr -dc 'A-Za-z0-9' < /dev/urandom | head -c 32)
    case "$candidate" in
      *[A-Z]*) ;;
      *) continue ;;
    esac
    case "$candidate" in
      *[a-z]*) ;;
      *) continue ;;
    esac
    case "$candidate" in
      *[0-9]*) printf '%s' "$candidate"; return 0 ;;
      *) continue ;;
    esac
  done
  echo "rotate: khong sinh duoc mat khau du ba lop sau 50 lan." >&2
  exit 1
}

step() { printf '  %-28s ' "$1"; }
ok() { printf 'OK\n'; }
skipped() { printf 'de lai cho recreate\n'; }

# ───────────────────────────────────────────── doc gia tri dang dung ──

POSTGRES_USER=$(env_value NVM_POSTGRES_USER)
POSTGRES_DB=$(env_value NVM_POSTGRES_DB)
EMQX_USER=$(env_value NVM_EMQX_USER)
RABBITMQ_USER=$(env_value NVM_RABBITMQ_USER)
OLD_MSSQL_SA=$(env_value NVM_MSSQL_SA_PASSWORD)
OLD_EMQX=$(env_value NVM_EMQX_PASSWORD)

# ─────────────────────────────────────────────────── sinh gia tri moi ──

NEW_POSTGRES=$(gen)
NEW_GRAFANA_DB=$(gen)
NEW_MSSQL_SA=$(gen)
NEW_MSSQL_APP=$(gen)
NEW_RABBITMQ=$(gen)
NEW_EMQX=$(gen)
NEW_MINIO=$(gen)
NEW_KEYCLOAK=$(gen)
NEW_GRAFANA_ADMIN=$(gen)

echo "Nhom A — doi trong service, roi ghi .env"

# 1. RabbitMQ is deliberately first. A pinned node does not reapply RABBITMQ_DEFAULT_PASS on
# recreate, so discovering an unavailable broker after changing four other services creates an
# avoidable partial rotation. The guard above proves the node is running; this command proves the
# credential can actually be changed before any other service is mutated.
step "NVM_RABBITMQ_PASSWORD"
docker exec nvm-rabbitmq rabbitmqctl -q change_password "$RABBITMQ_USER" "$NEW_RABBITMQ" > /dev/null
set_env NVM_RABBITMQ_PASSWORD "$NEW_RABBITMQ"
ok

# 2. Postgres role chinh. Khong can mat khau cu: pg_hba.conf do initdb sinh ra co
#    'local all all trust', va day la ket noi qua unix socket ben trong container.
step "NVM_POSTGRES_PASSWORD"
printf "ALTER ROLE %s PASSWORD '%s';\n" "$POSTGRES_USER" "$NEW_POSTGRES" \
  | docker exec -i nvm-timescale psql -X -q -v ON_ERROR_STOP=1 \
      -U "$POSTGRES_USER" -d "$POSTGRES_DB" > /dev/null
set_env NVM_POSTGRES_PASSWORD "$NEW_POSTGRES"
ok

# 3. SQL Server sa. OLD_PASSWORD la co y: no bat SQL Server xac minh lai nguoi goi.
step "NVM_MSSQL_SA_PASSWORD"
printf "ALTER LOGIN sa WITH PASSWORD = '%s' OLD_PASSWORD = '%s';\nGO\n" "$NEW_MSSQL_SA" "$OLD_MSSQL_SA" \
  | docker exec -i nvm-mssql /opt/mssql-tools18/bin/sqlcmd \
      -S localhost -U sa -P "$OLD_MSSQL_SA" -C -b > /dev/null
set_env NVM_MSSQL_SA_PASSWORD "$NEW_MSSQL_SA"
ok

# 4. Login ung dung. Phai lam tay vi deploy/mssql/init/01-database.sql chi CREATE LOGIN
#    trong nhanh IF NOT EXISTS — chay lai mssql-init KHONG xoay no. Xem runbook §1.1 dong 4.
step "NVM_MSSQL_APP_PASSWORD"
printf "ALTER LOGIN nvm_app WITH PASSWORD = '%s';\nGO\n" "$NEW_MSSQL_APP" \
  | docker exec -i nvm-mssql /opt/mssql-tools18/bin/sqlcmd \
      -S localhost -U sa -P "$NEW_MSSQL_SA" -C -b > /dev/null
set_env NVM_MSSQL_APP_PASSWORD "$NEW_MSSQL_APP"
ok

# 5. EMQX Dashboard. `emqx ctl` khong toi duoc node trong image nay (da kiem 2026-08-31,
#    va docker-compose.yml da ghi cung dieu do o phan healthcheck), nen doi qua REST API
#    cua chinh dashboard, goi tu ben trong container. Ba gia tri di qua STDIN.
step "NVM_EMQX_PASSWORD"
docker exec -i nvm-emqx sh -c 'cat > /tmp/emqx-rotate.sh' <<'SCRIPT'
set -eu
API=http://127.0.0.1:18083/api/v5
read -r U
read -r OLD
read -r NEW
TOKEN=$(curl -sS -X POST "$API/login" -H 'Content-Type: application/json' \
  -d "$(printf '{"username":"%s","password":"%s"}' "$U" "$OLD")" \
  | sed -n 's/.*"token":"\([^"]*\)".*/\1/p')
[ -n "$TOKEN" ] || { echo 'emqx: login that bai voi mat khau cu' >&2; exit 1; }
CODE=$(curl -sS -o /dev/null -w '%{http_code}' -X POST "$API/users/$U/change_pwd" \
  -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' \
  -d "$(printf '{"old_pwd":"%s","new_pwd":"%s"}' "$OLD" "$NEW")")
case "$CODE" in
  200|204) exit 0 ;;
  *) echo "emqx: change_pwd tra ve HTTP $CODE" >&2; exit 1 ;;
esac
SCRIPT
printf '%s\n%s\n%s\n' "$EMQX_USER" "$OLD_EMQX" "$NEW_EMQX" \
  | docker exec -i nvm-emqx sh /tmp/emqx-rotate.sh > /dev/null
docker exec nvm-emqx rm -f /tmp/emqx-rotate.sh
set_env NVM_EMQX_PASSWORD "$NEW_EMQX"
ok

# 6. Grafana admin. Co --password-from-stdin nen khong co gi nam tren dong lenh.
#    Lenh nay mo grafana.db trong luc server dang chay — da kiem, khong can dung Grafana.
step "NVM_GRAFANA_ADMIN_PASSWORD"
printf '%s' "$NEW_GRAFANA_ADMIN" \
  | docker exec -i nvm-grafana grafana cli --homepath /usr/share/grafana \
      admin reset-admin-password --password-from-stdin > /dev/null
set_env NVM_GRAFANA_ADMIN_PASSWORD "$NEW_GRAFANA_ADMIN"
ok

echo ""
echo "Nhom B/C — chi can .env moi, recreate se ap dung"

# nvm_grafana: grafana-db-init chay ALTER ROLE vo dieu kien o moi `make up-obs`.
step "NVM_GRAFANA_DB_PASSWORD"
set_env NVM_GRAFANA_DB_PASSWORD "$NEW_GRAFANA_DB"
skipped

# MinIO: root credential khong nam tren dia — /data/.minio.sys/config/iam chi co format.json.
step "NVM_MINIO_PASSWORD"
set_env NVM_MINIO_PASSWORD "$NEW_MINIO"
skipped

# Keycloak: H2 nam trong writable layer cua container, KHONG co volume, nen container moi
# la mot DB rong va KC_BOOTSTRAP_ADMIN_PASSWORD bootstrap lai.
step "NVM_KEYCLOAK_PASSWORD"
set_env NVM_KEYCLOAK_PASSWORD "$NEW_KEYCLOAK"
skipped

echo ""
echo "Da xoay 9/9 trong .env. Buoc con lai — chay tay de nhin duoc tung buoc:"
echo ""
echo "    make down && make up-obs && make ingestion-up"
echo "    make secret-check"
echo ""
echo "Roi kiem 9 phep dang nhap that o docs/runbook.md §1.4. secret-check chi doc .env;"
echo "no KHONG chung minh service da doi. Chin phep kia moi chung minh."
