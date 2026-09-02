#!/usr/bin/env sh
#
# Chin phep dang nhap THAT cho gia tri MOI, va chin phep doi chieu cho gia tri CU.
# docs/runbook.md §1.4.
#
# Ly do script nay ton tai tach khoi `make secret-check`: secret-check chi doc .env.
# No tra loi cau "file con chua gia tri da lo khong", KHONG tra loi cau "service con
# nhan gia tri da lo khong". Hai cau do khac nhau, va lan truoc chung bi gop lam mot
# roi K13 bi danh dau xong khi moi xong mot nua (ADR-034 §O8).
#
# Moi phep chay theo dung duong ma mot client that di:
#   - Postgres kiem tu MOT CONTAINER KHAC tren it-net, khong `docker exec` vao chinh
#     nvm-timescale: pg_hba.conf do initdb sinh ra co 'host all all 127.0.0.1/32 trust',
#     nen exec vao trong roi noi loopback se XANH voi mat khau bat ky. Do la bay do luong,
#     khong phai lo hong.
#   - EMQX kiem HAI lan: gia tri moi phai 200, gia tri CU phai 401. Ve thu hai moi la ve
#     tra loi cau hoi cua K13.
#
# Script khong in gia tri nao, chi in ten khoa va PASS/FAIL.
#
# Chay: sh scripts/rotate-verify.sh [duong-dan-.env-backup]

set -eu

# Can cho `docker exec ... /opt/mssql-tools18/bin/sqlcmd`: khong co no thi Git Bash dich
# doi so bat dau bang '/' thanh duong dan Windows va sqlcmd bien mat.
#
# Nhung no cung TAT luon phep dich '/dev/null' sang 'NUL', nen `curl -o /dev/null` chet
# voi `curl: (23) client returned ERROR on write`. Da mat mot vong debug vi dieu do:
# hai phep kiem bao FAIL trong khi credential hoan toan dung. Vi vay hai lenh curl duoi
# KHONG dung -o; chung ghi ra stdout, va ham check() da chuyen huong stdout roi —
# chuyen huong cua shell thi khong bi dich duong dan.
MSYS_NO_PATHCONV=1
export MSYS_NO_PATHCONV

LOCAL=".env"
NETWORK="novavolt-mes_it-net"
PG_IMAGE="timescale/timescaledb:2.29.2-pg17"
MC_IMAGE="minio/mc:RELEASE.2025-08-13T08-35-41Z"

OLD_ENV="${1:-}"
if [ -z "$OLD_ENV" ]; then
  OLD_ENV=$(ls -1t "$LOCAL".rotate-backup.* 2>/dev/null | head -n 1 || true)
fi

# Kiem cau truc TRUOC moi docker/curl. File chi can "doc duoc" la chua du: .env.example, file
# thieu mot key, key rong, key lap, hoac current=old deu tung co the bien mot login bi tu choi thanh
# bang chung xanh gia. Preflight la script thuan de ma tran negative control chay duoc trong CI.
if ! sh scripts/rotate-env-preflight.sh \
    "$LOCAL" "$OLD_ENV" scripts/burned-credentials.sha256; then
  exit 2
fi

env_value() { grep -E "^$1=" "$LOCAL" | head -n 1 | cut -d= -f2-; }
old_value() { [ -n "$OLD_ENV" ] && grep -E "^$1=" "$OLD_ENV" | head -n 1 | cut -d= -f2- || printf ''; }

passed=0
failed=0

check() {
  printf '  %-32s ' "$1"
  shift
  if "$@" > /dev/null 2>&1; then
    printf 'PASS\n'
    passed=$((passed + 1))
  else
    printf 'FAIL\n'
    failed=$((failed + 1))
  fi
}

# Dao nguoc ket qua: dung cho cac phep PHAI do.
check_rejects() {
  printf '  %-32s ' "$1"
  shift
  if "$@" > /dev/null 2>&1; then
    printf 'FAIL (van dang nhap duoc)\n'
    failed=$((failed + 1))
  else
    printf 'PASS (bi tu choi)\n'
    passed=$((passed + 1))
  fi
}

POSTGRES_USER=$(env_value NVM_POSTGRES_USER)
POSTGRES_DB=$(env_value NVM_POSTGRES_DB)
RABBITMQ_USER=$(env_value NVM_RABBITMQ_USER)
EMQX_USER=$(env_value NVM_EMQX_USER)
MINIO_USER=$(env_value NVM_MINIO_USER)
KEYCLOAK_ADMIN=$(env_value NVM_KEYCLOAK_ADMIN)
GRAFANA_ADMIN_USER=$(env_value NVM_GRAFANA_ADMIN_USER)
PORT_KEYCLOAK=$(env_value NVM_PORT_KEYCLOAK)
PORT_GRAFANA=$(env_value NVM_PORT_GRAFANA)

pg_login() {
  docker run --rm --network "$NETWORK" -e PGPASSWORD="$2" "$PG_IMAGE" \
    psql -h timescale -U "$1" -d "$POSTGRES_DB" -w -Atc 'SELECT 1'
}

pg_scoped_read() {
  docker run --rm --network "$NETWORK" -e PGPASSWORD="$1" "$PG_IMAGE" \
    psql -h timescale -U nvm_grafana -d "$POSTGRES_DB" -w -Atc \
    'SELECT count(*) FROM ts_scoped.readable_site'
}

pg_raw_read() {
  docker run --rm --network "$NETWORK" -e PGPASSWORD="$1" "$PG_IMAGE" \
    psql -h timescale -U nvm_grafana -d "$POSTGRES_DB" -w -Atc \
    'SELECT 1 FROM ts.telemetry_measurement LIMIT 1'
}

mssql_login() {
  printf 'SELECT 1;\nGO\n' \
    | docker exec -i nvm-mssql /opt/mssql-tools18/bin/sqlcmd \
        -S localhost -U "$1" -P "$2" -C -b -d "${3:-master}"
}

emqx_login() {
  printf '%s\n%s\n' "$EMQX_USER" "$1" \
    | docker exec -i nvm-emqx sh -c '
        read -r U; read -r P
        CODE=$(curl -sS -o /dev/null -w "%{http_code}" -X POST \
          http://127.0.0.1:18083/api/v5/login -H "Content-Type: application/json" \
          -d "$(printf "{\"username\":\"%s\",\"password\":\"%s\"}" "$U" "$P")")
        [ "$CODE" = "200" ]'
}

echo "Chin phep dang nhap that (docs/runbook.md §1.4)"
echo ""

check "1 NVM_POSTGRES_PASSWORD"      pg_login "$POSTGRES_USER" "$(env_value NVM_POSTGRES_PASSWORD)"
check "2 NVM_GRAFANA_DB_PASSWORD"    pg_scoped_read "$(env_value NVM_GRAFANA_DB_PASSWORD)"
check "3 NVM_MSSQL_SA_PASSWORD"      mssql_login sa "$(env_value NVM_MSSQL_SA_PASSWORD)"
check "4 NVM_MSSQL_APP_PASSWORD"     mssql_login nvm_app "$(env_value NVM_MSSQL_APP_PASSWORD)" NovaVolt
check "5 NVM_RABBITMQ_PASSWORD"      docker exec nvm-rabbitmq rabbitmqctl authenticate_user "$RABBITMQ_USER" "$(env_value NVM_RABBITMQ_PASSWORD)"
check "6 NVM_EMQX_PASSWORD"          emqx_login "$(env_value NVM_EMQX_PASSWORD)"
check "7 NVM_MINIO_PASSWORD"         docker run --rm --network "$NETWORK" --entrypoint mc "$MC_IMAGE" alias set probe http://minio:9000 "$MINIO_USER" "$(env_value NVM_MINIO_PASSWORD)"
check "8 NVM_KEYCLOAK_PASSWORD"      curl -sSf -d grant_type=password -d client_id=admin-cli --data-urlencode "username=$KEYCLOAK_ADMIN" --data-urlencode "password=$(env_value NVM_KEYCLOAK_PASSWORD)" "http://127.0.0.1:$PORT_KEYCLOAK/realms/master/protocol/openid-connect/token"
check "9 NVM_GRAFANA_ADMIN_PASSWORD" curl -sSf -u "$GRAFANA_ADMIN_USER:$(env_value NVM_GRAFANA_ADMIN_PASSWORD)" "http://127.0.0.1:$PORT_GRAFANA/api/user"

echo ""
echo "K3 van con nguyen sau khi xoay"
check_rejects "nvm_grafana doc thang schema ts"  pg_raw_read "$(env_value NVM_GRAFANA_DB_PASSWORD)"

echo ""
echo "Gia tri CU phai bi tu choi — day moi la cau hoi cua K13 (doi chieu $OLD_ENV)"
# CA CHIN, khong phai ba. Ban dau muc nay chi thu Postgres, MSSQL sa va EMQX, roi bao cao
# "13/13, K13 dong ca hai nua" — mot ket luan rong hon bang chung dung sau no. Sau credential
# con lai chua tung duoc thu, tuc chua tung duoc chung minh la da chet. Audit doc lap
# 2026-09-01 bat dung cho do.
check_rejects "postgres nvm, gia tri cu"    pg_login "$POSTGRES_USER" "$(old_value NVM_POSTGRES_PASSWORD)"
check_rejects "postgres nvm_grafana, cu"    pg_scoped_read "$(old_value NVM_GRAFANA_DB_PASSWORD)"
check_rejects "mssql sa, gia tri cu"        mssql_login sa "$(old_value NVM_MSSQL_SA_PASSWORD)"
check_rejects "mssql nvm_app, gia tri cu"   mssql_login nvm_app "$(old_value NVM_MSSQL_APP_PASSWORD)" NovaVolt
check_rejects "rabbitmq, gia tri cu"        docker exec nvm-rabbitmq rabbitmqctl authenticate_user "$RABBITMQ_USER" "$(old_value NVM_RABBITMQ_PASSWORD)"
check_rejects "emqx, gia tri cu"            emqx_login "$(old_value NVM_EMQX_PASSWORD)"
check_rejects "minio, gia tri cu"           docker run --rm --network "$NETWORK" --entrypoint mc "$MC_IMAGE" alias set probeold http://minio:9000 "$MINIO_USER" "$(old_value NVM_MINIO_PASSWORD)"
check_rejects "keycloak, gia tri cu"        curl -sSf -d grant_type=password -d client_id=admin-cli --data-urlencode "username=$KEYCLOAK_ADMIN" --data-urlencode "password=$(old_value NVM_KEYCLOAK_PASSWORD)" "http://127.0.0.1:$PORT_KEYCLOAK/realms/master/protocol/openid-connect/token"
check_rejects "grafana admin, gia tri cu"   curl -sSf -u "$GRAFANA_ADMIN_USER:$(old_value NVM_GRAFANA_ADMIN_PASSWORD)" "http://127.0.0.1:$PORT_GRAFANA/api/user"

echo ""
echo "PASS=$passed FAIL=$failed"
[ "$failed" -eq 0 ]
