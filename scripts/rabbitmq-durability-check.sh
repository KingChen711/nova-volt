#!/usr/bin/env sh
#
# Chung minh message durable song qua mot lan recreate container — N-M3-4.
#
# Vi sao phep kiem nay ton tai. Toi 2026-09-01, `docker-compose.yml` khong ghim hostname, nen ten
# Erlang node bam theo container id va MOI lan `up` sau mot `down` la mot node MOI. Node cu nam
# nguyen trong volume nhung khong con broker nao nhin thay no: volume co 5 thu muc `rabbit@<id>`,
# cai lon nhat giu 4,1 MB, con node dang chay bao 0 queue.
#
# Ca thang M2 va M3 khong ai thay, vi khong co gi durable ton tai du lau de mat. Mot audit doc lap
# moi bat duoc. Do la ly do phep kiem nay khong hoi "co ghim hostname chua" — mot cau hoi ve cau
# hinh luon tra loi duoc bang cach doc file — ma hoi "message co con khong", cau hoi ma chi mot lan
# recreate that moi tra loi duoc.
#
# Phep kiem CO recreate container rabbitmq. No khong dung `down -v`, khong cham volume, va khong
# cham service nao khac.
#
# Chay: make rabbitmq-durability-check

set -eu

QUEUE="nvm.durability-probe"
MESSAGES=5

env_value() { grep -E "^$1=" .env | head -n 1 | cut -d= -f2-; }

USER_NAME=$(env_value NVM_RABBITMQ_USER)
PASSWORD=$(env_value NVM_RABBITMQ_PASSWORD)
PORT=$(env_value NVM_PORT_RABBITMQ_UI)
API="http://127.0.0.1:$PORT/api"

if [ "$(docker inspect -f '{{.State.Running}}' nvm-rabbitmq 2>/dev/null || echo false)" != "true" ]; then
  printf '%s\n' "Missing nvm-rabbitmq. Run: make up" >&2
  exit 2
fi

api() {
  method=$1
  path=$2
  shift 2
  curl -sS -u "$USER_NAME:$PASSWORD" -X "$method" "$API$path" -H 'content-type: application/json' "$@"
}

node_name() { docker exec nvm-rabbitmq rabbitmqctl -q eval 'node().' | tr -d "'"; }

# Hoi thang broker thay vi hoi lop thong ke cua management plugin. Mot lop it hon giua cau hoi va
# cau tra loi, va `rabbitmqctl` doc tu chinh queue chu khong tu mot ban sao duoc lam moi dinh ky.
message_count() {
  docker exec nvm-rabbitmq rabbitmqctl -q list_queues name messages 2>/dev/null \
    | sed -n "s/^$QUEUE[[:space:]][[:space:]]*\([0-9][0-9]*\)$/\1/p" | head -n 1
}

# Quorum queue tra loi `routed: true` NGAY, nhung so dem thi tre: ghi phai commit qua Raft roi
# metric moi cap nhat. Do duoc 2026-09-01 — publish xong doc ngay thi CA HAI duong deu bao 0:
# `rabbitmqctl list_queues` (hoi thang broker) va `/api/queues` (qua stats collector). Nen phai cho
# THAT, va `sleep` o day khong phai de cho may cham ma la de cho mot he thong bat dong bo.
#
# Het gio thi tra ve so doc duoc that chu khong tra 0: mot phep kiem bao "0 message" trong khi that
# ra no khong doc duoc gi la mot phep kiem noi doi, va no se bi doc thanh mat du lieu.
wait_for_messages() {
  expected=$1
  attempt=0
  observed=""

  while [ "$attempt" -lt 20 ]; do
    observed=$(message_count)

    if [ "${observed:-}" = "$expected" ]; then
      printf '%s' "$observed"
      return 0
    fi

    sleep 1
    attempt=$((attempt + 1))
  done

  printf '%s' "${observed:-<khong doc duoc>}"
  return 1
}

# Ten node truoc va sau phai GIONG NHAU. Day la ve cho biet VI SAO message song sot — neu khong
# kiem, mot lan chay may man tren cung mot container se trong y het mot lan recreate that.
node_before=$(node_name)
printf 'node truoc      : %s\n' "$node_before"

# Xoa queue cu truoc khi bat dau. Mot lan chay truoc de lai message thi phep dem se so hai con so
# khong cung nguon goc, roi bao FAIL vi mot ly do khong lien quan gi toi dieu dang hoi.
api DELETE "/queues/%2F/$QUEUE" > /dev/null 2>&1 || true

# Quorum queue, khong phai classic: quorum la lua chon durable that o RabbitMQ 4, va no la thu M6
# se dung khi outbox can bao dam giao hang.
api PUT "/queues/%2F/$QUEUE" \
  -d '{"durable":true,"arguments":{"x-queue-type":"quorum"}}' > /dev/null

index=1
while [ "$index" -le "$MESSAGES" ]; do
  api POST "/exchanges/%2F/amq.default/publish" \
    -d "{\"properties\":{\"delivery_mode\":2},\"routing_key\":\"$QUEUE\",\"payload\":\"probe-$index\",\"payload_encoding\":\"string\"}" \
    > /dev/null
  index=$((index + 1))
done

if ! before=$(wait_for_messages "$MESSAGES"); then
  printf 'FAIL: publish %s message nhung queue bao %s\n' "$MESSAGES" "$before" >&2
  exit 1
fi

printf 'message truoc   : %s\n' "$before"
printf 'recreate container...\n'
docker compose up -d --force-recreate --wait rabbitmq > /dev/null 2>&1

node_after=$(node_name)
after=$(wait_for_messages "$MESSAGES") || true

printf 'node sau        : %s\n' "$node_after"
printf 'message sau     : %s\n' "${after:-0}"

api DELETE "/queues/%2F/$QUEUE" > /dev/null 2>&1 || true

if [ "$node_before" != "$node_after" ]; then
  printf 'FAIL: ten node doi tu %s sang %s, state cu da bi bo roi\n' "$node_before" "$node_after" >&2
  exit 1
fi

if [ "${after:-0}" != "$MESSAGES" ]; then
  printf 'FAIL: %s message truoc recreate, %s sau\n' "$MESSAGES" "${after:-0}" >&2
  exit 1
fi

printf 'DAT: %s/%s message song qua recreate, node giu nguyen %s\n' "$after" "$MESSAGES" "$node_after"
