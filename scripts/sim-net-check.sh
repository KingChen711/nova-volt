#!/usr/bin/env sh
#
# sim-net-check.sh — chứng minh Nvm.Simulator đứng đúng zone và chỉ đi đúng conduit.
#
# `make net-check` giữ nguyên chín phép đo của M0. C07 thêm ba phép riêng trên chính
# network namespace của simulator: đúng một network, tới được EMQX, không tới RabbitMQ.
# Dùng sidecar Alpine vì image runtime .NET cố ý không mang công cụ mạng.

set -u

CONTAINER=nvm-simulator
IMAGE=alpine:3.21
CHECKS=0
FAILED=0

if ! docker inspect "$CONTAINER" >/dev/null 2>&1; then
  echo "$CONTAINER chua chay. Chay 'make sim-up' truoc." >&2
  exit 2
fi

PROJECT=$(docker inspect -f '{{index .Config.Labels "com.docker.compose.project"}}' "$CONTAINER")
EXPECTED_NETWORK="${PROJECT}_ot-net"
ACTUAL_NETWORKS=$(docker inspect -f \
  '{{range $network, $configuration := .NetworkSettings.Networks}}{{println $network}}{{end}}' \
  "$CONTAINER" | grep -v '^$' | sort | tr '\n' ' ' | sed 's/ $//')

echo
printf '  %-36s %-12s %-12s %s\n' "Phep do C07" "Ky vong" "Do duoc" "Ket qua"
printf '  %-36s %-12s %-12s %s\n' "------------------------------------" "------------" "------------" "-------"

CHECKS=$((CHECKS + 1))
if [ "$ACTUAL_NETWORKS" = "$EXPECTED_NETWORK" ]; then
  printf '  %-36s %-12s %-12s %s\n' "Simulator network membership" "ot-net" "ot-net" "dat"
else
  printf '  %-36s %-12s %-12s %s\n' "Simulator network membership" "ot-net" "$ACTUAL_NETWORKS" "TRUOT"
  FAILED=$((FAILED + 1))
fi

# Đối chứng dương này đồng thời chứng minh `docker run`, sidecar và `nc` đều chạy
# được. Vì vậy phép RabbitMQ trả non-zero phía dưới thật sự là kết nối bị chặn,
# không phải một lệnh kiểm bị hỏng rồi báo xanh giả.
CHECKS=$((CHECKS + 1))
if docker run --rm --network "container:$CONTAINER" "$IMAGE" \
  sh -c 'nc -z -w 3 emqx 1883' >/dev/null 2>&1; then
  printf '  %-36s %-12s %-12s %s\n' "Simulator -> EMQX:1883" "open" "open" "dat"
else
  printf '  %-36s %-12s %-12s %s\n' "Simulator -> EMQX:1883" "open" "blocked" "TRUOT"
  FAILED=$((FAILED + 1))
fi

CHECKS=$((CHECKS + 1))
if docker run --rm --network "container:$CONTAINER" "$IMAGE" \
  sh -c 'nc -z -w 3 rabbitmq 5672' >/dev/null 2>&1; then
  printf '  %-36s %-12s %-12s %s\n' "Simulator -> RabbitMQ:5672" "blocked" "open" "TRUOT"
  FAILED=$((FAILED + 1))
else
  printf '  %-36s %-12s %-12s %s\n' "Simulator -> RabbitMQ:5672" "blocked" "blocked" "dat"
fi

echo
if [ "$FAILED" -eq 0 ]; then
  echo "C07 dung zone va conduit: $CHECKS/$CHECKS phep do dat."
  exit 0
fi

echo "C07 SAI RANH GIOI: $FAILED/$CHECKS phep do truot."
echo "Xem AGENTS.md K11 va docs/scope.md §13.1."
exit 1
