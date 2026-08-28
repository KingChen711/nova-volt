#!/usr/bin/env sh
#
# Lab phá hoại #3 (plan §5.C10.3) — xả một buffer lớn HAI LẦN, tắt và bật rate limit.
#
# Đây là lab DUY NHẤT trả lời được ADR-029 có đáng tồn tại không. C10.2 chỉ tắt backend 2 phút:
# buffer nhỏ, xả xong trước khi kịp thấy gì.
#
# Hai lần xả phải xuất phát từ CÙNG một backlog, nếu không thì đang so hai bài toán khác nhau.
# Vì vậy buffer được chụp lại sau khi nạp, và lần B khôi phục đúng bản chụp đó.
#
# LỆCH SO VỚI PLAN, nói rõ theo AGENTS.md §2.3: nguồn là `load-harness` chứ không phải simulator.
# Plan viết "chạy simulator ở tốc độ N1", nhưng simulator phát theo report-by-exception nên tốc độ
# của nó là hệ quả của TimeCompression chứ không đặt thẳng được; harness là thứ duy nhất phát đúng
# N1 và đếm được số phép đo logic của chính nó.

set -eu

MSYS_NO_PATHCONV=1
export MSYS_NO_PATHCONV

COMPOSE="docker compose"
. "$(dirname "$0")/gateway-snapshot.sh"
. "$(dirname "$0")/reset-ingestion-fixture.sh"

# Con so cua plan §5.C10.3, va chung khong ha duoc. Mot lab #3 chay 60 giay o 500 msg/s van in
# ra "bon con so cua ADR-029" — chi la bon con so ve mot backlog khong ai gap ngoai doi. ADR khong
# phan biet duoc, nguoi doc ADR sau nay cang khong.
RATE=${RATE:-5000}
FILL=${FILL:-1800}
N1_MINIMUM=5000
FILL_MINIMUM=1800

DRAIN_BUDGET=${DRAIN_BUDGET:-3600}
BUFFER_DIR=/var/lib/nvm-edge-gateway/buffer
BACKUP_DIR=/var/lib/nvm-edge-gateway/buffer-lab-snapshot
RESULT_FILE=$(mktemp)
SAMPLE_FILE=$(mktemp)
POLLER=nvm-backpressure-poller

say() { printf '%s\n' "$1"; }

if [ "$RATE" -lt "$N1_MINIMUM" ]; then
	say "RATE=$RATE < N1 ($N1_MINIMUM). Lab #3 do hanh vi khi xa mot backlog o N1." >&2
	exit 2
fi

if [ "$FILL" -lt "$FILL_MINIMUM" ]; then
	say "FILL=$FILL < $FILL_MINIMUM giay. Plan §5.C10.3 hoi ve 30 phut nap." >&2
	exit 2
fi

cleanup() {
	status=$?
	trap - EXIT
	rm -f "$RESULT_FILE" "$SAMPLE_FILE"
	docker rm -f "$POLLER" >/dev/null 2>&1 || true
	exit "$status"
}
trap cleanup EXIT

ingestion_stats() {
	$COMPOSE --profile tools run --rm -T dmz-shell \
		-c 'wget -qO- http://ingestion:8080/api/ingestion/v1/stats' 2>/dev/null
}

json_value() {
	printf '%s' "$1" | tr -d ' \r\n' | sed -n "s/.*\"${2}\":\([^,}]*\).*/\1/p"
}

# Volume của gateway, đọc từ chính container thay vì ghép tên project bằng tay.
gateway_volume() {
	docker inspect nvm-edge-gateway \
		--format '{{range .Mounts}}{{if eq .Destination "/var/lib/nvm-edge-gateway"}}{{.Name}}{{end}}{{end}}'
}

telemetry_rows() {
	docker exec nvm-timescale sh -c \
		'psql -U "$POSTGRES_USER" -d "$POSTGRES_DB" -tAc "
            SELECT count(*) FROM ts.telemetry_measurement
            WHERE site_id = '\''NV1'\'' AND signal_code = '\''Formation/Voltage'\'';"' \
		2>/dev/null | tr -d ' \r'
}

result_value() {
	awk -v key="$1" '
		$1 == "NVM_LOAD_RESULT" {
			for (f = 2; f <= NF; f++) {
				p = key "="
				if (index($f, p) == 1) { print substr($f, length(p) + 1) }
			}
		}' "$RESULT_FILE" | tail -1
}

# Xả tới khi hàng đợi bền vững về 0. Nguồn đã dừng hẳn ở đây, nên 0 mới là trạng thái ổn định.
drain_and_time() {
	budget=$1
	started=$(date +%s)

	while [ "$(( $(date +%s) - started ))" -le "$budget" ]; do
		depth=$(gateway_stat buffer_depth)

		case "$depth" in
			''|*[!0-9]*) ;;
			0) printf '%s' "$(( $(date +%s) - started ))"; return 0 ;;
		esac

		sleep 2
	done

	printf '%s' "-1"
	return 1
}

start_poller() {
	net=$(docker inspect nvm-edge-gateway \
		--format '{{range $n, $c := .NetworkSettings.Networks}}{{$n}}{{end}}')
	docker rm -f "$POLLER" >/dev/null 2>&1 || true
	docker run -d --name "$POLLER" --network "$net" --entrypoint /bin/sh \
		eclipse-mosquitto:2.0.22 \
		-c 'while true; do wget -qO- http://ingestion:8080/api/ingestion/v1/stats; echo; sleep 5; done' \
		>/dev/null
}

# Không có sample nào là THIẾU bằng chứng, không phải p95 bằng 0. Bản cũ in "0.000" trong đúng
# tình huống poller chết ngay từ đầu, và 0.000 là con số đẹp nhất bảng.
peak_p95() {
	docker logs "$POLLER" 2>/dev/null | awk '
		{
			p = $0
			sub(/.*"lagP95Seconds":/, "", p)
			sub(/[,}].*/, "", p)
			if (p ~ /^-?[0-9.eE+-]+$/) {
				seen++
				if (p + 0 > peak) { peak = p + 0 }
			}
		}
		END {
			if (seen == 0) { exit 1 }
			printf "%.3f", peak
		}'
}

# ── một lượt xả ───────────────────────────────────────────────────────────────
# $1 nhãn · $2 giá trị NVM_EDGE_FLUSH_RATE (0 = tắt rate limit) · $3 số record đã chụp
run_arm() {
	label=$1
	flush_rate=$2
	expected=$3
	note() { printf '%s\n' "$1" >&2; }

	note ""
	note "== $label (NVM_EDGE_FLUSH_RATE=$flush_rate)"

	reset_ingestion_fixture >&2 || return 1
	rows_before=$(telemetry_rows)
	stats_before=$(ingestion_stats)
	dup_before=$(json_value "$stats_before" duplicates)

	start_poller
	NVM_EDGE_FLUSH_RATE="$flush_rate" $COMPOSE up -d --force-recreate edge-gateway >/dev/null 2>&1
	gateway_wait_snapshot 120 || { note "gateway khong len"; return 1; }

	elapsed=$(drain_and_time "$DRAIN_BUDGET") || note "  CANH BAO: het ngan sach ${DRAIN_BUDGET}s"

	snapshot=$(gateway_snapshot)
	throttled=$(snapshot_field "$snapshot" throttled_flushes)
	limited=$(snapshot_field "$snapshot" rate_limited_flushes)
	forwarded=$(snapshot_field "$snapshot" forwarded)
	p95=$(peak_p95) || { note "  khong co sample p95 nao trong luot nay"; return 1; }
	docker rm -f "$POLLER" >/dev/null 2>&1 || true

	rows_after=$(telemetry_rows)
	stats_after=$(ingestion_stats)
	dup_after=$(json_value "$stats_after" duplicates)

	# Mỗi lượt phải giải trình ĐÚNG bản chụp nó xuất phát. Không có phép này, một lượt xả nửa
	# buffer rồi hết ngân sách vẫn in ra thời gian và p95 trông hợp lý — và hai lượt vẫn "bằng
	# nhau" nếu cả hai cùng xả nửa. Con số duy nhất chứng minh chúng nói về cùng một backlog là
	# tổng row + dedup của từng lượt so với số record đã chụp.
	accounted=$(( (rows_after - rows_before) + (dup_after - dup_before) ))

	if [ "$accounted" -ne "$expected" ]; then
		note "  LUOT HONG: giai trinh $accounted / $expected record cua ban chup"
		return 1
	fi

	note "  Thoi gian tieu backlog : ${elapsed}s"
	note "  Peak p95 lag           : ${p95}s"
	note "  Throttled (429/503)    : $throttled"
	note "  Rate-limited flush     : $limited"
	note "  Gateway forwarded      : $forwarded"
	note "  Row delta              : $((rows_after - rows_before))  (dedup $((dup_after - dup_before)))"
	note "  Giai trinh ban chup    : $accounted / $expected record"

	printf '%s %s %s %s %s %s\n' \
		"$elapsed" "$p95" "$throttled" "$limited" \
		"$((rows_after - rows_before))" "$((dup_after - dup_before))"
}

# ── nạp buffer ────────────────────────────────────────────────────────────────
say "Lab #3 — backpressure A/B"
say "  Rate         : $RATE msg/s"
say "  Nap           : $FILL giay (~$((RATE * FILL)) message)"
say "  Ngan sach xa : $DRAIN_BUDGET giay moi luot"
say ""

$COMPOSE --profile sim stop simulator >/dev/null 2>&1 || true
$COMPOSE --profile load build load-harness >/dev/null
$COMPOSE build edge-gateway >/dev/null

say "== tat ingestion, xoa buffer cu, gateway bat dau don hang"
$COMPOSE --profile ingestion stop ingestion >/dev/null 2>&1
$COMPOSE stop edge-gateway >/dev/null 2>&1 || true

# Bắt đầu từ đĩa trống. "Buffer sau 30 phút ở N1" là con số 1 của ADR-029, và nếu lượt chạy trước
# để lại record thì con số đó là của hai lần nạp cộng lại — sai theo hướng làm ADR trông đúng hơn.
docker run --rm -v "$(gateway_volume)":/data alpine:3.20 \
	sh -c "rm -rf /data/$(basename "$BUFFER_DIR") /data/$(basename "$BACKUP_DIR")" >/dev/null

$COMPOSE up -d --force-recreate edge-gateway >/dev/null 2>&1
gateway_wait_snapshot 120 || { say "gateway khong len" >&2; exit 1; }

# Nguon KHONG duoc phat truoc khi gateway da subscribe lai. Session cua gateway la persistent,
# nen EMQX giu message cho no toi max_mqueue_len (16.384) roi XOA phan con lai — mot lan smoke da
# nap duoc dung 11.342 record thay vi 300.000 vi ly do nay.
say "== cho gateway subscribe lai truoc khi mo nguon"
sleep 15

say "== phat $FILL giay o $RATE msg/s (ingestion DANG TAT)"
set +e
NVM_LOAD_RATE="$RATE" NVM_LOAD_DURATION="$FILL" \
	$COMPOSE --profile load run --rm load-harness >"$RESULT_FILE" 2>&1
HARNESS_STATUS=$?
set -e

# `|| true` ở đây từng nuốt đúng thứ duy nhất nói rằng lần nạp đã hỏng. Một harness chết ở phút
# thứ ba vẫn để lại một buffer, và lab vẫn xả nó hai lần rồi in ra bốn con số.
if [ "$HARNESS_STATUS" -ne 0 ]; then
	cat "$RESULT_FILE" >&2
	say "LAB HUY: load harness exit=$HARNESS_STATUS, lan nap khong hop le." >&2
	exit 1
fi

SOURCE_MEASUREMENTS=$(result_value source_measurements)
SOURCE_RATE=$(result_value achieved_rate)

case "${SOURCE_MEASUREMENTS:-}" in
	''|*[!0-9]*)
		say "LAB HUY: khong doc duoc source_measurements tu harness." >&2
		exit 1
		;;
esac

awk -v actual="${SOURCE_RATE:-0}" -v minimum="$N1_MINIMUM" 'BEGIN { exit !(actual >= minimum) }' || {
	say "LAB HUY: nguon chi dat ${SOURCE_RATE} msg/s, duoi N1 $N1_MINIMUM." >&2
	say "Backlog nay khong phai backlog cua 30 phut o N1." >&2
	exit 1
}

filled=$(gateway_snapshot)
BUFFER_DEPTH=$(snapshot_field "$filled" buffer_depth)
BUFFER_BYTES=$(snapshot_field "$filled" buffer_bytes)

say ""
say "  Nguon phat            : $SOURCE_MEASUREMENTS phep do @ $SOURCE_RATE msg/s"
say "  ★ Buffer sau $FILL giay : $BUFFER_DEPTH record · $BUFFER_BYTES byte"

# Lab nay chi co nghia neu buffer that su da day. Neu phan lon message khong toi duoc gateway thi
# hai luot xa ben duoi dang do mot backlog khong dai dien, va do te hon la khong do.
EXPECTED_MIN=$((SOURCE_MEASUREMENTS * 9 / 10))
if [ "${BUFFER_DEPTH:-0}" -lt "$EXPECTED_MIN" ]; then
	say "" >&2
	say "LAB HUY: gateway chi don duoc $BUFFER_DEPTH / $SOURCE_MEASUREMENTS phep do." >&2
	say "Backlog khong dai dien cho 30 phut o N1, nen hai luot xa se do nham thu." >&2
	exit 1
fi

# Chụp buffer khi gateway đã dừng, nếu không là chụp một file đang được ghi dở.
say "== dung gateway, chup buffer de hai luot xuat phat giong het nhau"
$COMPOSE stop edge-gateway >/dev/null 2>&1
docker run --rm -v "$(gateway_volume)":/data alpine:3.20 \
	sh -c "rm -rf /data/$(basename "$BACKUP_DIR") && cp -a /data/$(basename "$BUFFER_DIR") /data/$(basename "$BACKUP_DIR")"

$COMPOSE --profile ingestion up -d --wait ingestion >/dev/null 2>&1

A=$(run_arm "LAN A — rate limit TAT" 0 "$BUFFER_DEPTH")
A_ELAPSED=$(printf '%s' "$A" | tail -1 | cut -d' ' -f1)
A_P95=$(printf '%s' "$A" | tail -1 | cut -d' ' -f2)
A_THROTTLED=$(printf '%s' "$A" | tail -1 | cut -d' ' -f3)
A_ROWS=$(printf '%s' "$A" | tail -1 | cut -d' ' -f5)

say ""
say "== khoi phuc buffer da chup cho lan B"
$COMPOSE stop edge-gateway >/dev/null 2>&1
docker run --rm -v "$(gateway_volume)":/data alpine:3.20 \
	sh -c "rm -rf /data/$(basename "$BUFFER_DIR") && cp -a /data/$(basename "$BACKUP_DIR") /data/$(basename "$BUFFER_DIR")"

B=$(run_arm "LAN B — rate limit BAT" 12000 "$BUFFER_DEPTH")
B_ELAPSED=$(printf '%s' "$B" | tail -1 | cut -d' ' -f1)
B_P95=$(printf '%s' "$B" | tail -1 | cut -d' ' -f2)
B_LIMITED=$(printf '%s' "$B" | tail -1 | cut -d' ' -f4)
B_ROWS=$(printf '%s' "$B" | tail -1 | cut -d' ' -f5)

say ""
say "  BON CON SO CUA ADR-029"
say "  ----------------------------------------------------------------"
say "  1. Buffer sau $FILL giay          : $BUFFER_DEPTH record · $BUFFER_BYTES byte"
say "  2. Peak p95 lag khi KHONG gioi han: ${A_P95}s   (throttle $A_THROTTLED)"
say "  3. Thoi gian tieu backlog khi CO  : ${B_ELAPSED}s (rate-limited flush $B_LIMITED)"
say "  4. Row delta A / B                : $A_ROWS / $B_ROWS"
say ""
say "  Doi chieu: lan A xa het trong ${A_ELAPSED}s, lan B trong ${B_ELAPSED}s."

FAILED=0
[ "$A_ROWS" = "$B_ROWS" ] || { say "FAIL: hai lan xa ra so row khac nhau" >&2; FAILED=1; }
[ "$A_ELAPSED" != "-1" ] || { say "FAIL: lan A khong xa het trong ngan sach" >&2; FAILED=1; }
[ "$B_ELAPSED" != "-1" ] || { say "FAIL: lan B khong xa het trong ngan sach" >&2; FAILED=1; }

exit "$FAILED"
