#!/usr/bin/env sh
#
# net-check.sh — chứng minh ranh giới OT/IT (AGENTS.md K11) vẫn còn nguyên.
#
# Vì sao script này tồn tại: M0 §C05 đã kiểm 5 chiều bằng `ping <tên container>`.
# Phép đó chỉ chứng minh đường TRỰC TIẾP bị chặn. Nó không thấy đường vòng qua
# port host, và đường vòng đó đã có thật suốt từ M0 tới M1 (§C08.4).
#
# Hai nguyên tắc, cả hai đều rút từ những lần script báo sai trước đây:
#
#   1. Bắt exit code vào biến TRƯỚC khi lọc output. `cmd | tail -3 && echo DAT`
#      lấy exit code của `tail`, tức luôn bằng 0. Bẫy này ghi ở M0 §C05.
#   2. Báo cả LÝ DO chặn, không chỉ báo bị chặn. Một lệnh sai cú pháp cũng thất
#      bại, và từng làm M0 §C08.3 kết luận nhầm là "đạt". Cột `dns` có mặt để
#      phân biệt: chặn ở tầng phân giải tên, hay chặn ở tầng định tuyến TCP.
#
# Exit: 0 = ranh giới nguyên vẹn · 1 = có vi phạm · 2 = chưa đủ điều kiện chạy

set -u

# Git Bash đổi "/dev/null" trong tham số thành đường dẫn Windows, và lệnh chạy
# trong container nhận về rác. Cùng bẫy MSYS đã gặp ở M0 §C07.
MSYS_NO_PATHCONV=1
export MSYS_NO_PATHCONV

COMPOSE="docker compose"
TIMEOUT=3
FAILED=0
CHECKS=0

# 1.1.1.1 chứ không phải một tên miền: dùng IP thì phép thử đo ĐỊNH TUYẾN,
# không lẫn với việc DNS có chạy hay không.
INTERNET_IP=1.1.1.1
INTERNET_PORT=443

# TEN CONTAINER, khong phai ten service trong compose. `docker exec probe-it`
# bao "No such container" va tra ve khac 0 — y het mot ket noi bi chan. Lan dau
# viet script nay da dinh dung loi do: ca 9 chieu deu bao "blocked", ke ca chieu
# ma vong MQTT ngay ben duoi vua chay thanh cong.
OT=nvm-probe-ot
DMZ=nvm-probe-dmz
IT=nvm-probe-it

# ── Điều kiện tiên quyết ────────────────────────────────────────────────────
if ! docker inspect nvm-emqx >/dev/null 2>&1; then
  echo "nvm-emqx chua chay. Chay 'make up' truoc." >&2
  exit 2
fi

echo "Dung 3 container probe..."
$COMPOSE --profile probe up -d probe-ot probe-dmz probe-it >/dev/null 2>&1 || {
  echo "Khong dung duoc probe container." >&2
  exit 2
}

cleanup() {
  $COMPOSE rm -sf probe-ot probe-dmz probe-it >/dev/null 2>&1
}
trap cleanup EXIT

# Mot probe khong chay duoc lenh nao thi MOI phep do qua no deu tra ve khac 0,
# va bang ket qua se toan "blocked" — trong nhu mot ranh gioi rat chac chan.
# Do la kieu sai nguy hiem nhat: sai theo huong yen tam.
assert_alive() {
  if ! docker exec "$1" true >/dev/null 2>&1; then
    echo "Probe $1 khong phan hoi. Khong the do gi qua no." >&2
    exit 2
  fi
}
assert_alive "$OT"
assert_alive "$DMZ"
assert_alive "$IT"

# ── Hai phép đo nguyên thuỷ ─────────────────────────────────────────────────
resolves() {   # <container> <ten>
  docker exec "$1" sh -c "getent hosts $2" >/dev/null 2>&1
}

connects() {   # <container> <host> <port> — chi bat tay TCP, khong gui gi
  docker exec "$1" sh -c "timeout $TIMEOUT nc $2 $3 < /dev/null" >/dev/null 2>&1
}

check() {      # <nhan> <container> <host> <port> <ky vong: open|blocked>
  _label=$1; _c=$2; _h=$3; _p=$4; _want=$5

  # Voi dich la IP, cot DNS vo nghia — `getent hosts 1.1.1.1` luon tra ve
  # chinh no. In "-" thay vi "co" de khong ai doc nham thanh "ten phan giai duoc".
  case "$_h" in
    *[!0-9.]*)
      resolves "$_c" "$_h"
      _dnsrc=$?
      if [ "$_dnsrc" -eq 0 ]; then _dns="co"; else _dns="khong"; fi
      ;;
    *) _dns="-" ;;
  esac

  connects "$_c" "$_h" "$_p"
  _tcprc=$?
  if [ "$_tcprc" -eq 0 ]; then _got="open"; else _got="blocked"; fi

  CHECKS=$((CHECKS + 1))
  if [ "$_got" = "$_want" ]; then
    _verdict="dat"
  else
    _verdict="TRUOT"
    FAILED=$((FAILED + 1))
  fi

  printf '  %-34s %-8s %-8s %-5s %s\n' "$_label" "$_want" "$_got" "$_dns" "$_verdict"
}

# ── Bảng kết quả ────────────────────────────────────────────────────────────
echo
printf '  %-34s %-8s %-8s %-5s %s\n' "Chieu" "Ky vong" "Do duoc" "DNS" "Ket qua"
printf '  %-34s %-8s %-8s %-5s %s\n' "----------------------------------" "--------" "--------" "-----" "-------"

# K11 — ba duong tu IT xuong OT. Ca ba PHAI dong.
check "IT -> emqx:1883 (truc tiep)"      $IT   emqx                    1883  blocked
check "IT -> host:1883 (duong vong)"     $IT   host.docker.internal    1883  blocked
check "IT -> host:18083 (duong vong)"    $IT   host.docker.internal   18083  blocked

# OT bi nhot: khong internet, khong voi len IT.
check "OT -> internet"                   $OT   $INTERNET_IP  $INTERNET_PORT  blocked
check "OT -> rabbitmq:5672 (tang IT)"    $OT   rabbitmq                5672  blocked

# DMZ la vung dem, khong phai cua sau vao IT.
check "DMZ -> rabbitmq:5672 (tang IT)"   $DMZ  rabbitmq                5672  blocked

# Hai chieu HOP LE. Chung truot thi ranh gioi da that chat qua muc.
check "DMZ -> emqx:1883 (duong hop le)"  $DMZ  emqx                    1883  open
check "IT  -> internet"                  $IT   $INTERNET_IP  $INTERNET_PORT  open

# ── Vòng MQTT thật ──────────────────────────────────────────────────────────
# Bắt tay TCP mới chỉ nói "có đường". Chiều hợp lệ phải chở được dữ liệu thật,
# nếu không thì việc bỏ `ports:` của EMQX đã bịt luôn cả đường sống của M2.
CHECKS=$((CHECKS + 1))
docker exec "$DMZ" sh -c \
  "mosquitto_pub -h emqx -t nvm/netcheck -m ok -r -q 1 &&
   mosquitto_sub -h emqx -t nvm/netcheck -C 1 -W 5 | grep -q ok &&
   mosquitto_pub -h emqx -t nvm/netcheck -r -n -q 1" >/dev/null 2>&1
MQTT_RC=$?
if [ "$MQTT_RC" -eq 0 ]; then
  printf '  %-34s %-8s %-8s %-5s %s\n' "DMZ -> emqx pub/sub MQTT" "ok" "ok" "-" "dat"
else
  printf '  %-34s %-8s %-8s %-5s %s\n' "DMZ -> emqx pub/sub MQTT" "ok" "loi" "-" "TRUOT"
  FAILED=$((FAILED + 1))
fi

# ── Kết luận ────────────────────────────────────────────────────────────────
echo
if [ "$FAILED" -eq 0 ]; then
  echo "K11 nguyen ven: $CHECKS/$CHECKS phep do dat."
  exit 0
fi
echo "K11 BI VI PHAM: $FAILED/$CHECKS phep do truot."
echo "Xem AGENTS.md K11 va docs/plans/M0-bootstrap.md C08.4."
exit 1
