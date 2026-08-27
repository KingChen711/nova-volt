#!/bin/sh
# NovaVolt MES — kịch bản kiểm chứng bus của M1/C13.
#
# Ba bằng chứng của C13 đều cần HAI process thật đang chạy: Nvm.Host.All publish,
# Nvm.BusProbe consume. Script này tự dựng, tự chạy kịch bản, tự dọn — để con số
# in ra là con số lặp lại được, không phụ thuộc vào việc ai đó nhớ mở đúng hai
# terminal theo đúng thứ tự.
#
#   ./bus-lab.sh fanout   D1 — 1 publish, 2 queue độc lập cùng nhận
#   ./bus-lab.sh dlq      D2 — consumer ném exception 5 lần, message vào _error
#   ./bus-lab.sh chaos    D4 — tắt broker giữa lúc publish, ĐẾM số event mất
#
# Script này bị XOÁ cùng Nvm.BusProbe ở M2 (xem docs/plans/M1-factory-model-bus.md §8).

set -e

ROOT=$(cd "$(dirname "$0")/../../.." && pwd)
LOGS="$ROOT/artifacts/bus-logs"
HOST_URL="http://localhost:5080"
RABBIT="nvm-rabbitmq"

# Số của lab phá hoại. Ghi ở đây, không rải trong code, vì chúng là ĐIỀU KIỆN ĐO
# và phải đi kèm con số khi chép sang benchmarks.md.
CHAOS_COUNT=${CHAOS_COUNT:-200}
CHAOS_DELAY_MS=${CHAOS_DELAY_MS:-100}
CHAOS_STOP_AFTER=${CHAOS_STOP_AFTER:-5}
CHAOS_DOWNTIME=${CHAOS_DOWNTIME:-30}

host_pid=""
probe_pid=""
probe_log=""

# ─────────────────────────────────────────────────────────
# Chạy và dọn process
# ─────────────────────────────────────────────────────────

# Chạy thẳng binary chứ không `dotnet run`: `dotnet run` sinh một process con, và
# giết process cha để lại app chạy tiếp, chiếm cổng 5080 cho lần sau.
exe() {
	if [ -f "$1.exe" ]; then echo "$1.exe"; else echo "$1"; fi
}

# KHÔNG bọc lệnh trong subshell `( ... ) &`. Làm thế thì $! là pid của subshell,
# và giết subshell để lại binary chạy tiếp — đúng cái bẫy vừa nêu ở trên, chỉ là
# một tầng thấp hơn. Đã gặp thật: hai probe cũ vẫn đang đọc queue trong khi lần
# chạy mới tưởng chúng đã tắt, và phép kiểm thứ ba của D1 cho kết quả ngược.
stop() {
	[ -n "$1" ] || return 0
	kill "$1" 2>/dev/null || true
	wait "$1" 2>/dev/null || true
}

cleanup() {
	stop "$probe_pid"
	stop "$host_pid"
}
trap cleanup EXIT INT TERM

require_broker() {
	docker exec "$RABBIT" rabbitmq-diagnostics -q check_running >/dev/null 2>&1 || {
		echo "RabbitMQ chua chay. Chay 'make up' truoc."
		exit 1
	}
}

build() {
	echo "== build"
	dotnet build "$ROOT/NovaVolt.Mes.slnx" --nologo -v quiet >/dev/null
}

start_host() {
	mkdir -p "$LOGS"
	cd "$ROOT/artifacts/bin/Nvm.Host.All/debug"

	# cd vao thu muc binary: appsettings.json nam canh no, con .env va deploy/seed
	# duoc DotEnvLoader/SeedFileLocator tim bang cach di nguoc len toi goc repo.
	ASPNETCORE_ENVIRONMENT=Development ASPNETCORE_URLS="$HOST_URL" \
		"$(exe ./Nvm.Host.All)" >"$LOGS/host.log" 2>&1 &
	host_pid=$!
	cd "$ROOT"

	printf "== host"
	i=0
	while [ $i -lt 60 ]; do
		if curl -fsS -o /dev/null "$HOST_URL/health/live" 2>/dev/null; then
			echo " up (live=Healthy)"
			return 0
		fi
		printf "."
		sleep 1
		i=$((i + 1))
	done

	echo " KHONG len duoc — xem $LOGS/host.log"
	exit 1
}

# $1 = danh sach consumer ("cache,audit")   $2 = ten file log
start_probe() {
	mkdir -p "$LOGS"
	probe_log="$LOGS/$2"
	cd "$ROOT/artifacts/bin/Nvm.BusProbe/debug"

	DOTNET_ENVIRONMENT=Development NVM_BUS_PROBE_CONSUMERS="$1" \
		"$(exe ./Nvm.BusProbe)" >"$probe_log" 2>&1 &
	probe_pid=$!
	cd "$ROOT"

	printf "== probe [%s] -> %s" "$1" "$2"
	i=0
	while [ $i -lt 60 ]; do
		if grep -aq "Bus started" "$probe_log" 2>/dev/null; then
			echo " up"
			return 0
		fi
		printf "."
		sleep 1
		i=$((i + 1))
	done

	echo " KHONG len duoc — xem $probe_log"
	exit 1
}

stop_probe() {
	stop "$probe_pid"
	probe_pid=""
	# Cho broker thay connection dong han, neu khong queue van con consumer online.
	sleep 3
}

# ─────────────────────────────────────────────────────────
# Đọc kết quả
# ─────────────────────────────────────────────────────────

# `grep -c` tra ve 0 VA thoat 1 khi khong khop, nen `|| echo 0` in ra hai dong.
count() {
	n=$(grep -ac "$1" "$2" 2>/dev/null) || n=0
	echo "$n"
}

wait_for() {
	i=0
	while [ "$i" -lt "$3" ]; do
		if [ "$(count "$1" "$probe_log")" -ge "$2" ]; then
			# Mot nhip nua, de dong cuoi kip duoc ghi ra dia truoc khi doc.
			sleep 1
			return 0
		fi
		sleep 1
		i=$((i + 1))
	done
	return 1
}

publish_one() {
	curl -fsS -X POST "$HOST_URL/dev/bus/activate-revision?site=$1&revision=$2"
	echo
}

show_topology() {
	echo
	echo "-- exchange tren broker"
	docker exec "$RABBIT" rabbitmqctl -q list_exchanges name type | grep '^nvm\.' || true
	echo
	echo "-- queue tren broker (name / type / messages)"
	docker exec "$RABBIT" rabbitmq-diagnostics -q list_queues name type messages | grep '^nvm\.' || true
}

env_value() {
	grep -E "^$1=" "$ROOT/.env" | cut -d= -f2-
}

# Moi kich ban bat dau tu trang thai da biet. Lan chay truoc de lai 183 message trong
# queue cua failing-probe, va lan chay sau dem ca chung — mot con so ma dieu kien do
# khong con dung la mot con so vo nghia (benchmarks.md, luat 2).
purge_probe_queues() {
	for q in nvm.factory-model.cache-updater nvm.factory-model.audit-trail 		nvm.factory-model.failing-probe nvm.factory-model.failing-probe_error; do
		docker exec "$RABBIT" rabbitmqadmin 			-u "$(env_value NVM_RABBITMQ_USER)" -p "$(env_value NVM_RABBITMQ_PASSWORD)" 			purge queue --name "$q" >/dev/null 2>&1 || true
	done
}

# ─────────────────────────────────────────────────────────
# D1 — một publish, hai queue độc lập cùng nhận
# ─────────────────────────────────────────────────────────
fanout() {
	require_broker
	purge_probe_queues
	build
	start_host
	start_probe "cache,audit" "probe-fanout.log"

	echo
	echo "== publish 1 event"
	publish_one NV1 1

	wait_for "received revision\|recorded revision" 2 20 || true

	echo
	echo "-- probe nhan duoc:"
	grep -a "cache-updater received\|audit-trail recorded" "$probe_log" || true
	echo
	echo "-- so dong: $(count 'cache-updater received\|audit-trail recorded' "$probe_log")  (ky vong 2)"

	show_topology

	# Phep kiem quan trong nhat cua D1: tat MOT consumer roi publish tiep. Neu hai
	# queue that su doc lap thi queue cua consumer dang tat phai TANG messages, con
	# consumer con lai van nhan binh thuong. Hai consumer dung chung mot queue thi
	# khong the tao ra ket qua nay.
	echo
	echo "== tat audit-trail, chay lai chi voi cache-updater"
	stop_probe
	start_probe "cache" "probe-cache-only.log"

	echo
	echo "== publish 1 event nua"
	publish_one NV1 1

	wait_for "cache-updater received" 1 20 || true

	echo
	echo "-- probe (chi cache-updater) nhan duoc:"
	grep -a "cache-updater received\|audit-trail recorded" "$probe_log" || true

	show_topology
	echo
	echo "Ky vong: nvm.factory-model.audit-trail co messages > 0 (khong ai doc),"
	echo "         nvm.factory-model.cache-updater co messages = 0 (da doc xong)."
}

# ─────────────────────────────────────────────────────────
# D2 — 5 lần thử, rồi vào _error, không mất
# ─────────────────────────────────────────────────────────
dlq() {
	require_broker
	purge_probe_queues
	build
	start_host
	start_probe "cache,audit,failing" "probe-dlq.log"

	echo
	echo "== publish 1 event, failing-probe se nem exception"
	publish_one NV1 1

	wait_for "failing-probe attempt 5" 1 30 || true

	echo
	echo "-- so lan thu (log cua consumer, KHONG phai counter cua broker):"
	grep -a "failing-probe attempt" "$probe_log" || true
	echo
	echo "-- so lan: $(count 'failing-probe attempt' "$probe_log")  (ky vong 5)"

	# _error chi ra doi SAU lan loi dau tien — MassTransit khai bao no luc can.
	# Kiem su ton tai cua no truoc khi co message loi la kiem mot thu chua ai tao.
	show_topology

	# reject_requeue_true: xem xong TRA message lai queue, khong tieu thu mat.
	# rabbitmqadmin v2 in ra mot bang rong ca man hinh, nen loc lay dung nhung header
	# tra loi hai cau hoi: no hong vi cai gi, va no van con la event gi.
	#
	# Loc DU CA SAU header ce_*, khong phai ba cai de nhan ra nhat. ADR-008 quyet dinh
	# rang sau thuoc tinh la MOT BO; mot lab chi soi ba cai thi khong the do duoc quyet
	# dinh do - no van xanh khi mot trong ba cai con lai bien mat tren day.
	echo
	echo "-- header cua message trong _error (doc roi tra lai queue):"
	dlq_headers=$(docker exec "$RABBIT" rabbitmqadmin -u "$(env_value NVM_RABBITMQ_USER)" -p "$(env_value NVM_RABBITMQ_PASSWORD)" get messages --queue nvm.factory-model.failing-probe_error --ack-mode reject_requeue_true 2>/dev/null | grep -aoE '"(MT-Fault-(ConsumerType|ExceptionType|Message|RetryCount)|MT-Reason|ce_(specversion|id|type|source|time|datacontenttype))":"?[^",]*' | sort -u || true)
	echo "$dlq_headers"

	ce_found=$(echo "$dlq_headers" | grep -c '"ce_' || true)

	echo
	echo "MT-Fault-RetryCount dem LAN THU LAI, nen 4 nghia la 5 lan chay."
	echo "-- so header ce_* con lai tren day: $ce_found  (ky vong 6)"

	if [ "$ce_found" -eq 6 ]; then
		echo "DAT: ca 6 thuoc tinh CloudEvents song sot qua broker that va qua 5 lan thu."
		echo "     Message khong deserialize noi van noi duoc no la event gi, cua site nao,"
		echo "     luc nao, va payload duoc ma hoa bang gi."
	else
		echo "TRUOT: thieu header ce_*. ADR-008 doi du 6; dem duoc $ce_found."
		return 1
	fi
}

# ─────────────────────────────────────────────────────────
# D4 — lab phá hoại: tắt broker giữa lúc publish, ĐẾM số event mất
# ─────────────────────────────────────────────────────────
chaos() {
	require_broker
	purge_probe_queues
	build
	start_host
	start_probe "cache,audit" "probe-chaos.log"

	echo
	echo "== burst $CHAOS_COUNT event, moi event cach nhau $CHAOS_DELAY_MS ms"
	echo "   broker bi tat sau $CHAOS_STOP_AFTER s, bat lai sau $CHAOS_DOWNTIME s"

	: >"$LOGS/burst.json"
	curl -sS -X POST \
		"$HOST_URL/dev/bus/burst?site=NV1&count=$CHAOS_COUNT&delayMs=$CHAOS_DELAY_MS" \
		>"$LOGS/burst.json" 2>&1 &
	burst_pid=$!

	sleep "$CHAOS_STOP_AFTER"
	echo
	echo "-- docker compose stop rabbitmq"
	docker compose --project-directory "$ROOT" stop rabbitmq >/dev/null 2>&1

	echo "-- /health/live trong luc broker tat:"
	curl -fsS "$HOST_URL/health/live" || echo "(khong tra loi — app da chet)"
	echo
	echo "-- /health/ready trong luc broker tat:"
	curl -sS "$HOST_URL/health/ready" || true
	echo

	sleep "$CHAOS_DOWNTIME"
	echo "-- docker compose start rabbitmq"
	docker compose --project-directory "$ROOT" start rabbitmq >/dev/null 2>&1

	wait "$burst_pid" 2>/dev/null || true

	# Cho consumer tieu thu not phan ton dong sau khi broker len lai.
	sleep 20

	received=$(count "cache-updater received revision" "$probe_log")
	published=$(grep -o '"published":[0-9]*' "$LOGS/burst.json" | cut -d: -f2)
	failed=$(grep -o '"failed":[0-9]*' "$LOGS/burst.json" | cut -d: -f2)

	echo
	echo "-- ket qua burst (phia publisher):"
	cat "$LOGS/burst.json"
	echo
	echo "-- /health/live sau khi broker len lai:"
	curl -fsS "$HOST_URL/health/live" || echo "(khong tra loi)"
	echo
	echo "-- app co restart khong? so lan 'Application started' trong host.log:"
	count "Application started" "$LOGS/host.log"

	echo
	echo "════════════════════════════════════════════════"
	echo "  yeu cau publish   : $CHAOS_COUNT"
	echo "  publish thanh cong: ${published:-?}"
	echo "  publish that bai  : ${failed:-?}"
	echo "  consumer NHAN DUOC: $received"
	echo "  SO EVENT MAT      : $((CHAOS_COUNT - received))"
	echo "════════════════════════════════════════════════"
	echo
	echo "So o dong cuoi la ket qua chinh cua lab. Chep vao docs/benchmarks.md"
	echo "va ADR-022 nguyen van — khong lam tron ve 0."
}

case "$1" in
fanout) fanout ;;
dlq) dlq ;;
chaos) chaos ;;
*)
	echo "Dung: $0 fanout|dlq|chaos"
	exit 1
	;;
esac
