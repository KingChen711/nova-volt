#!/usr/bin/env sh
#
# load-net-check.sh — chứng minh Nvm.LoadHarness đứng đúng zone.
#
# Harness GIẢ LÀM thiết bị, nên nó phải bị ràng buộc y hệt thiết bị: chỉ ot-net, tới được
# EMQX, không tới RabbitMQ, không tới ingestion. Một harness chạy ngoài ot-net vẫn ra được
# một con số — nhưng là con số của một đường đi không tồn tại trong production
# (R-M2-2, scope.md §13.1).
#
# Container harness thoát khi chạy xong, nên phép đo membership tạo container mà KHÔNG chạy
# rồi soi chân mạng thật, thay vì đọc YAML. Chân mạng được gán lúc create.

set -u

IMAGE=alpine:3.21
CHECKS=0
FAILED=0

PROJECT=$(docker inspect -f '{{index .Config.Labels "com.docker.compose.project"}}' nvm-emqx 2>/dev/null)

if [ -z "$PROJECT" ]; then
  echo "Khong doc duoc ten compose project. Chay 'make up' truoc." >&2
  exit 2
fi

NETWORK="${PROJECT}_ot-net"

if ! docker network inspect "$NETWORK" >/dev/null 2>&1; then
  echo "Khong tim thay network $NETWORK. Chay 'make up' truoc." >&2
  exit 2
fi

row() {
  printf '  %-36s %-12s %-12s %s\n' "$1" "$2" "$3" "$4"
}

CHECKS=$((CHECKS + 1))
DECLARED=

if docker compose --profile load create load-harness >/dev/null 2>&1; then
  DECLARED=$(docker inspect -f \
    '{{range $network, $configuration := .NetworkSettings.Networks}}{{println $network}}{{end}}' \
    nvm-load-harness 2>/dev/null | grep -v '^$' | sort | tr '\n' ' ' | sed 's/ $//')
  docker rm -f nvm-load-harness >/dev/null 2>&1 || true
fi

echo
row "Phep do C16" "Ky vong" "Do duoc" "Ket qua"
row "------------------------------------" "------------" "------------" "-------"

if [ "$DECLARED" = "$NETWORK" ]; then
  row "Harness network membership" "ot-net" "ot-net" "dat"
else
  row "Harness network membership" "ot-net" "${DECLARED:-?}" "TRUOT"
  FAILED=$((FAILED + 1))
fi

# Đối chứng dương: chứng minh sidecar, `nc` và network đều hoạt động, nên hai phép chặn phía
# dưới thật sự là kết nối bị chặn chứ không phải một lệnh kiểm hỏng rồi báo xanh giả.
CHECKS=$((CHECKS + 1))
if docker run --rm --network "$NETWORK" "$IMAGE" sh -c 'nc -z -w 3 emqx 1883' >/dev/null 2>&1; then
  row "ot-net -> EMQX:1883" "open" "open" "dat"
else
  row "ot-net -> EMQX:1883" "open" "blocked" "TRUOT"
  FAILED=$((FAILED + 1))
fi

CHECKS=$((CHECKS + 1))
if docker run --rm --network "$NETWORK" "$IMAGE" sh -c 'nc -z -w 3 rabbitmq 5672' >/dev/null 2>&1; then
  row "ot-net -> RabbitMQ:5672" "blocked" "open" "TRUOT"
  FAILED=$((FAILED + 1))
else
  row "ot-net -> RabbitMQ:5672" "blocked" "blocked" "dat"
fi

# Ingestion nằm ở dmz-net + it-net. Thiết bị không được nhìn thấy nó, nên harness cũng không.
CHECKS=$((CHECKS + 1))
if docker run --rm --network "$NETWORK" "$IMAGE" sh -c 'nc -z -w 3 ingestion 8080' >/dev/null 2>&1; then
  row "ot-net -> ingestion:8080" "blocked" "open" "TRUOT"
  FAILED=$((FAILED + 1))
else
  row "ot-net -> ingestion:8080" "blocked" "blocked" "dat"
fi

echo
if [ "$FAILED" -eq 0 ]; then
  echo "C16 dung zone: $CHECKS/$CHECKS phep do dat."
  exit 0
fi

echo "C16 SAI RANH GIOI: $FAILED/$CHECKS phep do truot."
echo "Xem AGENTS.md K11 va docs/scope.md §13.1."
exit 1
