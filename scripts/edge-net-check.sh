#!/usr/bin/env sh
#
# edge-net-check.sh — prove the real gateway has one DMZ leg and no route into IT.

set -u

CONTAINER=nvm-edge-gateway
IMAGE=alpine:3.21
CHECKS=0
FAILED=0

if ! docker inspect "$CONTAINER" >/dev/null 2>&1; then
  echo "$CONTAINER chua chay. Chay 'make edge-up' truoc." >&2
  exit 2
fi

PROJECT=$(docker inspect -f '{{index .Config.Labels "com.docker.compose.project"}}' "$CONTAINER")
EXPECTED_NETWORK="${PROJECT}_dmz-net"
ACTUAL_NETWORKS=$(docker inspect -f \
  '{{range $network, $configuration := .NetworkSettings.Networks}}{{println $network}}{{end}}' \
  "$CONTAINER" | grep -v '^$' | sort | tr '\n' ' ' | sed 's/ $//')

echo
printf '  %-36s %-12s %-12s %s\n' "Phep do C08" "Ky vong" "Do duoc" "Ket qua"
printf '  %-36s %-12s %-12s %s\n' "------------------------------------" "------------" "------------" "-------"

CHECKS=$((CHECKS + 1))
if [ "$ACTUAL_NETWORKS" = "$EXPECTED_NETWORK" ]; then
  printf '  %-36s %-12s %-12s %s\n' "Gateway network membership" "dmz-net" "dmz-net" "dat"
else
  printf '  %-36s %-12s %-12s %s\n' "Gateway network membership" "dmz-net" "$ACTUAL_NETWORKS" "TRUOT"
  FAILED=$((FAILED + 1))
fi

# EMQX là đối chứng dương: sidecar + nc hoạt động trước khi phép RabbitMQ trả non-zero
# được coi là một ranh giới đúng, tránh kiểu script hỏng mà báo an toàn giả.
CHECKS=$((CHECKS + 1))
if docker run --rm --network "container:$CONTAINER" "$IMAGE" \
  sh -c 'nc -z -w 3 emqx 1883' >/dev/null 2>&1; then
  printf '  %-36s %-12s %-12s %s\n' "Gateway -> EMQX:1883" "open" "open" "dat"
else
  printf '  %-36s %-12s %-12s %s\n' "Gateway -> EMQX:1883" "open" "blocked" "TRUOT"
  FAILED=$((FAILED + 1))
fi

CHECKS=$((CHECKS + 1))
if docker run --rm --network "container:$CONTAINER" "$IMAGE" \
  sh -c 'nc -z -w 3 rabbitmq 5672' >/dev/null 2>&1; then
  printf '  %-36s %-12s %-12s %s\n' "Gateway -> RabbitMQ:5672" "blocked" "open" "TRUOT"
  FAILED=$((FAILED + 1))
else
  printf '  %-36s %-12s %-12s %s\n' "Gateway -> RabbitMQ:5672" "blocked" "blocked" "dat"
fi

echo
if [ "$FAILED" -eq 0 ]; then
  echo "C08 dung zone va conduit: $CHECKS/$CHECKS phep do dat."
  exit 0
fi

echo "C08 SAI RANH GIOI: $FAILED/$CHECKS phep do truot."
echo "Xem AGENTS.md K11/K12 va docs/scope.md §13.1."
exit 1
