#!/bin/sh
# NovaVolt MES — kịch bản kiểm chứng bus của M1/C13, chuyển sang đường thật ở M2/C18.
#
#   ./bus-lab.sh fanout   D1 — 1 publish, 2 queue độc lập cùng nhận
#   ./bus-lab.sh dlq      D2 — consumer ném exception 5 lần, message vào _error
#   ./bus-lab.sh chaos    D4 — tắt broker giữa lúc publish, ĐẾM số event mất
#
# Khác M1 ở chỗ quan trọng nhất: bên PUBLISH giờ là Nvm.Ingestion thật, và nó được kích bằng
# một file CSV thả vào inbox của C15 — không còn dev endpoint nào trên Nvm.Host.All. Nghĩa là
# ba con số dưới đây đo đúng đường mà production đi: file -> dedup -> transaction -> CloudEvents.
#
# Bên NHẬN là `tools/buslab/Nvm.BusLab`, process riêng. Riêng process là điều kiện của D1: chạy
# chung một process thì hai handler trong cùng một container DI trông y hệt fan-out thật.

set -e

MSYS_NO_PATHCONV=1
export MSYS_NO_PATHCONV

ROOT=$(cd "$(dirname "$0")/.." && pwd)
# cd thay vi --project-directory: tren Windows, duong dan kieu MSYS lam Docker Compose
# tu choi thang, va vi lenh bi nuot output nen no chet im lang giua chung.
cd "$ROOT"
LOGS="$ROOT/artifacts/bus-logs"
RABBIT="nvm-rabbitmq"
INGESTION="nvm-ingestion"
SIGNAL="Formation/CapacityResult"
CHANNEL="NOVAVOLT/NV1/FORMATION/F1/FORM-01/FORM-01-CH-0001"
UNIT_ID="NV1CL16238A00123"

# Số của lab phá hoại. Ghi ở đây, không rải trong code, vì chúng là ĐIỀU KIỆN ĐO và phải đi kèm
# con số khi chép sang benchmarks.md.
CHAOS_COUNT=${CHAOS_COUNT:-200}
CHAOS_DOWNTIME=${CHAOS_DOWNTIME:-30}

lab_pid=""
lab_log=""

exe() {
	if [ -f "$1.exe" ]; then echo "$1.exe"; else echo "$1"; fi
}

# KHÔNG bọc lệnh trong subshell `( ... ) &`. Làm thế thì $! là pid của subshell, và giết subshell
# để lại binary chạy tiếp. Đã gặp thật ở M1: hai consumer cũ vẫn đang đọc queue trong khi lần chạy
# mới tưởng chúng đã tắt, và phép kiểm thứ ba của D1 cho kết quả ngược.
stop() {
	[ -n "$1" ] || return 0
	kill "$1" 2>/dev/null || true
	wait "$1" 2>/dev/null || true
}

cleanup() { stop "$lab_pid"; }
trap cleanup EXIT INT TERM

env_value() { grep -E "^$1=" "$ROOT/.env" | cut -d= -f2-; }

require_broker_and_ingestion() {
	docker exec "$RABBIT" rabbitmq-diagnostics -q check_running >/dev/null 2>&1 || {
		echo "RabbitMQ chua chay. Chay 'make up' truoc."
		exit 1
	}

	if [ "$(docker inspect -f '{{.State.Running}}' "$INGESTION" 2>/dev/null || echo false)" != "true" ]; then
		echo "Nvm.Ingestion chua chay. Chay 'make ingestion-up' truoc."
		exit 1
	fi

}

# Ca ba kich ban dung ket qua CUOI co UnitId tu CSV adapter. Simulator phat duong cong
# Formation/Capacity tho; no la telemetry va khong con la nguon hop le cho bat ky bus lab nao.
# Dung simulator de row va log cua lab chi den tu fixture da danh gia ben duoi.
require() {
	require_broker_and_ingestion

	if [ "$(docker inspect -f '{{.State.Running}}' nvm-simulator 2>/dev/null || echo false)" = "true" ]; then
		echo "== dung simulator de con so cua lab chi den tu file CSV cua lab"
		docker compose --profile sim stop simulator >/dev/null 2>&1
	fi
}

build() {
	echo "== build"
	# Duong dan TUONG DOI, khong tuyet doi: MSYS_NO_PATHCONV=1 o dau file (can cho `docker exec`)
	# tat luon phep doi /d/... thanh D:\... cho dotnet, va dotnet tren Windows khong mo duoc
	# "/d/code/...". Da chet im lang mot lan vi output bi nuot vao /dev/null.
	dotnet build NovaVolt.Mes.slnx --nologo -v quiet >/dev/null
}

# $1 = danh sach consumer ("cache,audit")   $2 = ten file log
start_lab() {
	mkdir -p "$LOGS"
	lab_log="$LOGS/$2"
	cd "$ROOT/artifacts/bin/Nvm.BusLab/debug"

	DOTNET_ENVIRONMENT=Development NVM_BUS_LAB_CONSUMERS="$1" \
		"$(exe ./Nvm.BusLab)" >"$lab_log" 2>&1 &
	lab_pid=$!
	cd "$ROOT"

	printf "== bus-lab [%s] -> %s" "$1" "$2"
	i=0
	while [ $i -lt 60 ]; do
		if grep -aq "Bus started" "$lab_log" 2>/dev/null; then
			echo " up"
			return 0
		fi
		printf "."
		sleep 1
		i=$((i + 1))
	done

	echo " KHONG len duoc — xem $lab_log"
	exit 1
}

stop_lab() {
	stop "$lab_pid"
	lab_pid=""
	# Cho broker thay connection dong han, neu khong queue van con consumer online.
	sleep 3
}

# Do D1 bang hai moc CO THAT trong log, va noi ro cai gi nam giua chung.
#
# Moc dau la luc file CSV cham vao inbox, khong phai luc publish: ingestion khong log lan publish
# thanh cong, va them mot dong log vao duong nong chi de lab doc duoc thi la sua production cho
# tien do luong. Nen con so nay bao gom ca: file watcher thay file -> parse -> dedup -> transaction
# -> publish CloudEvent -> broker dinh tuyen -> consumer xu ly. Chep sang benchmarks.md thi phai
# chep ca cau nay, neu khong nguoi doc sau se tuong day la do tre cua rieng cai bus.
#
# So thu hai moi la so noi ve D1: khoang cach giua HAI consumer. Mot publish, hai queue doc lap,
# nen no do dung cai gia cua viec fan-out them mot consumer nua.
fanout_timing() {
	first=$(grep -a "measurement-cache received" "$lab_log" | tail -1 | cut -c1-12)
	second=$(grep -a "measurement-audit recorded" "$lab_log" | tail -1 | cut -c1-12)

	if [ -z "$first" ] || [ -z "$second" ]; then
		echo "-- D1 timing: khong doc duoc moc tu log, bo qua"
		return 0
	 fi

	ms() { printf '%s' "$1" | awk -F'[:.]' '{print (($1*3600)+($2*60)+$3)*1000+$4}'; }

	drop_ms=$(ms "$DROP_AT"); a=$(ms "$first"); b=$(ms "$second")
	late=$a; [ "$b" -gt "$a" ] && late=$b
	spread=$((b - a)); [ "$spread" -lt 0 ] && spread=$((-spread))

	# Fail-closed. Am thi hai dong ho khong cung mui gio; qua lon thi moc bat duoc la cua lan chay
	# truoc. Ca hai truong hop deu phai IM thay vi in ra mot con so trong nhu mot phep do.
	if [ "$((late - drop_ms))" -lt 0 ] || [ "$((late - drop_ms))" -gt 120000 ]; then
		echo
		echo "-- D1 timing: hai moc khong cung mot dong ho ($DROP_AT vs $first/$second), khong bao so"
		return 0
	fi

	echo
	echo "-- D1 timing (moc dau = luc file cham inbox, KHONG phai luc publish)"
	echo "   tha file            : $DROP_AT"
	echo "   consumer thu nhat   : $first  (+$((a - drop_ms)) ms)"
	echo "   consumer thu hai    : $second  (+$((b - drop_ms)) ms)"
	echo "   ca hai nhan xong    : +$((late - drop_ms)) ms"
	echo "   lech giua 2 consumer: $spread ms"
}

# `grep -c` tra ve 0 VA thoat 1 khi khong khop, nen `|| n=0` de tranh in ra hai dong.
count() {
	n=$(grep -ac "$1" "$2" 2>/dev/null) || n=0
	echo "$n"
}

wait_for() {
	i=0
	while [ "$i" -lt "$3" ]; do
		if [ "$(count "$1" "$lab_log")" -ge "$2" ]; then
			sleep 1
			return 0
		fi
		sleep 1
		i=$((i + 1))
	done
	return 1
}

# ─────────────────────────────────────────────────────────
# Publish bằng đường THẬT: thả một file CSV vào inbox của C15
#
# $1 = so dong, $2 = nhan de phan biet cac file giua cac lan chay
#
# Moi dong mot device_timestamp khac nhau, vi timestamp nam trong khoa tu nhien: hai dong cung
# thoi diem la CUNG mot phep do, dedup nuot dung, va lab se dem thieu ma khong bao gi.
# ─────────────────────────────────────────────────────────
drop_csv() {
	rows=$1
	tag=$2
	stamp=$(date +%s)
	# Moc bat dau cua phep do D1, ghi ngay truoc khi file cham vao inbox. Doc lai bang
	# `fanout_timing`. Ghi o day chu khong o `fanout()` de moc luon la moc cua LAN THA cuoi cung.
	# UTC, vi BusLab ghi log bang UTC con `date` tran lay gio may. Lay sai mui gio thi hieu so ra
	# -25.192.730 ms — do dung la thu da xay ra, va no im lang cho toi luc co nguoi doc con so.
	#
	# Ke tu hop dong publish: file duoc ghi ra `<ten>.csv.partial`, dong, roi rename mot lan thanh
	# `<ten>.csv.ready`. Moc doc duoc cua ingestion la luc phep rename do landing, khong phai luc byte
	# dau tien cham vao inbox — hai moc cach nhau vai mili-giay o day, nhung `SettleTime` khong con
	# nam trong hieu so nua, nen con so D1 cu (co 2.000 ms settle) khong so sanh truc tiep duoc.
	DROP_AT=$(date -u '+%H:%M:%S.%3N')
	file="/var/lib/nvm-ingestion/inbox/bus-lab-$tag-$stamp.csv"

	{
		# Gio/phut/giay lay tu DONG HO LUC THA, mili-giay lay tu chi so dong. Hai lan tha cach nhau
		# vai giay se ra khoa tu nhien KHAC nhau — neu khong, lan hai la duplicate, dedup nuot dung,
		# va lab ngoi cho mot event khong bao gio duoc phat.
		echo "equipment_path,unit_id,signal_code,measured_at,value_kind,value"
		i=0
		while [ "$i" -lt "$rows" ]; do
			printf '%s,%s,%s,2026-08-29T%02d:%02d:%02d.%03dZ,real,4.8%03d\n' \
				"$CHANNEL" "$UNIT_ID" "$SIGNAL" \
				$(( (stamp / 3600) % 24 )) $(( (stamp / 60) % 60 )) $(( stamp % 60 )) $(( i % 1000 )) $(( i % 1000 ))
			i=$((i + 1))
		done
	} | docker exec -i "$INGESTION" sh -c "cat > $file.partial && mv $file.partial $file.ready"

	echo "== tha $rows dong vao inbox: $(basename "$file")"
}

# Chaos can mot stream du lau de broker chet GIUA luc publish. Mot file lon co the duoc publish
# xong trong mot poll 5 giay; nhieu file mot dong buoc adapter di qua tung transaction va cho script
# mot moc quan sat that. Tat ca van la ket qua da danh gia co UnitId, khong muon duong cong MQTT.
drop_csv_files() {
	rows=$1
	tag=$2

	if [ "$rows" -gt 1000 ]; then
		echo "drop_csv_files chi ho tro toi da 1000 row de mili-giay van hop le" >&2
		return 1
	fi

	stamp=$(date +%s)
	measured_second=$(date -u +%Y-%m-%dT%H:%M:%S)

	docker exec \
		-e NVM_BUS_LAB_ROWS="$rows" \
		-e NVM_BUS_LAB_TAG="$tag" \
		-e NVM_BUS_LAB_STAMP="$stamp" \
		-e NVM_BUS_LAB_TIME="$measured_second" \
		-e NVM_BUS_LAB_CHANNEL="$CHANNEL" \
		-e NVM_BUS_LAB_UNIT="$UNIT_ID" \
		-e NVM_BUS_LAB_SIGNAL="$SIGNAL" \
		"$INGESTION" sh -c '
			i=0
			while [ "$i" -lt "$NVM_BUS_LAB_ROWS" ]; do
				file="/var/lib/nvm-ingestion/inbox/bus-lab-$NVM_BUS_LAB_TAG-$NVM_BUS_LAB_STAMP-$i.csv"
				{
					echo "equipment_path,unit_id,signal_code,measured_at,value_kind,value"
					printf "%s,%s,%s,%s.%03dZ,real,4.8%03d\n" \
						"$NVM_BUS_LAB_CHANNEL" "$NVM_BUS_LAB_UNIT" "$NVM_BUS_LAB_SIGNAL" \
						"$NVM_BUS_LAB_TIME" "$i" "$i"
				} >"$file.partial"
				mv "$file.partial" "$file.ready"
				i=$((i + 1))
			done
		'

	echo "== tha $rows file ket qua da danh gia vao inbox"
}

ingestion_stat() {
	docker compose --profile tools run --rm -T dmz-shell \
		-c 'wget -qO- http://ingestion:8080/api/ingestion/v1/stats' 2>/dev/null \
		| tr ',' '\n' | sed -n "s/.*\"$1\":\([0-9.-]*\).*/\1/p" | head -1
}

show_topology() {
	echo
	echo "-- exchange tren broker"
	docker exec "$RABBIT" rabbitmqctl -q list_exchanges name type | grep '^nvm\.' || true
	echo
	echo "-- queue tren broker (name / type / messages)"
	docker exec "$RABBIT" rabbitmq-diagnostics -q list_queues name type messages | grep '^nvm\.' || true
}

# Moi kich ban bat dau tu trang thai da biet. Lan chay truoc de lai message trong queue cua
# consumer that bai, va lan chay sau dem ca chung — mot con so ma dieu kien do khong con dung la
# mot con so vo nghia (benchmarks.md, luat 2).
purge_lab_queues() {
	for q in nvm.quality.measurement-cache nvm.quality.measurement-audit \
		nvm.quality.measurement-failing nvm.quality.measurement-failing_error; do
		docker exec "$RABBIT" rabbitmqadmin \
			-u "$(env_value NVM_RABBITMQ_USER)" -p "$(env_value NVM_RABBITMQ_PASSWORD)" \
			purge queue --name "$q" >/dev/null 2>&1 || true
	done
}

# ─────────────────────────────────────────────────────────
# D1 — một publish, hai queue độc lập cùng nhận
# ─────────────────────────────────────────────────────────
fanout() {
	require
	purge_lab_queues
	build
	start_lab "cache,audit" "lab-fanout.log"

	echo
	drop_csv 1 fanout
	wait_for "measurement-cache received\|measurement-audit recorded" 2 40 || true

	echo
	echo "-- consumer nhan duoc:"
	grep -a "measurement-cache received\|measurement-audit recorded" "$lab_log" || true
	echo
	echo "-- so dong: $(count 'measurement-cache received\|measurement-audit recorded' "$lab_log")  (ky vong 2)"

	fanout_timing

	show_topology

	# Phep kiem quan trong nhat cua D1: tat MOT consumer roi publish tiep. Neu hai queue that su
	# doc lap thi queue cua consumer dang tat phai TANG messages, con consumer con lai van nhan
	# binh thuong. Hai consumer dung chung mot queue thi khong the tao ra ket qua nay.
	echo
	echo "== tat measurement-audit, chay lai chi voi measurement-cache"
	stop_lab
	start_lab "cache" "lab-cache-only.log"

	echo
	drop_csv 1 fanout-second
	wait_for "measurement-cache received" 1 40 || true

	echo
	echo "-- consumer (chi cache) nhan duoc:"
	grep -a "measurement-cache received\|measurement-audit recorded" "$lab_log" || true

	show_topology
	echo
	echo "Ky vong: nvm.quality.measurement-audit co messages > 0 (khong ai doc),"
	echo "         nvm.quality.measurement-cache co messages = 0 (da doc xong)."
}

# ─────────────────────────────────────────────────────────
# D2 — 5 lần thử, rồi vào _error, không mất
# ─────────────────────────────────────────────────────────
dlq() {
	require
	purge_lab_queues
	build
	start_lab "cache,audit,failing" "lab-dlq.log"

	echo
	drop_csv 1 dlq
	wait_for "measurement-failing attempt 5" 1 60 || true

	echo
	echo "-- so lan thu (log cua consumer, KHONG phai counter cua broker):"
	grep -a "measurement-failing attempt" "$lab_log" || true
	echo
	echo "-- so lan: $(count 'measurement-failing attempt' "$lab_log")  (ky vong 5)"

	# _error chi ra doi SAU lan loi dau tien — MassTransit khai bao no luc can. Kiem su ton tai
	# cua no truoc khi co message loi la kiem mot thu chua ai tao.
	show_topology

	# reject_requeue_true: xem xong TRA message lai queue, khong tieu thu mat.
	#
	# Loc DU CA SAU header ce_*, khong phai ba cai de nhan ra nhat. ADR-008 quyet dinh rang sau
	# thuoc tinh la MOT BO; mot lab chi soi ba cai thi khong the do duoc quyet dinh do — no van
	# xanh khi mot trong ba cai con lai bien mat tren day.
	echo
	echo "-- header cua message trong _error (doc roi tra lai queue):"
	dlq_headers=$(docker exec "$RABBIT" rabbitmqadmin -u "$(env_value NVM_RABBITMQ_USER)" -p "$(env_value NVM_RABBITMQ_PASSWORD)" get messages --queue nvm.quality.measurement-failing_error --ack-mode reject_requeue_true 2>/dev/null | grep -aoE '"(MT-Fault-(ConsumerType|ExceptionType|Message|RetryCount)|MT-Reason|ce_(specversion|id|type|source|time|datacontenttype))":"?[^",]*' | sort -u || true)
	echo "$dlq_headers"

	# Dem TEN header duy nhat, khong dem dong. rabbitmqadmin in moi header HAI lan (mot lan trong
	# bang tom tat, mot lan trong phan properties), va `sort -u` tren ca dong khong gop chung lai
	# khi phan gia tri in ra khac nhau du chi mot ky tu. Dem dong o day cho ra 12 va lam DoD truot
	# trong khi broker van giu du sau.
	ce_found=$(echo "$dlq_headers" | grep -aoE '"ce_[a-z]+"' | sort -u | wc -l | tr -d '[:space:]')

	echo
	echo "MT-Fault-RetryCount dem LAN THU LAI, nen 4 nghia la 5 lan chay."
	echo "-- ten header ce_* duy nhat con lai tren day: $ce_found  (ky vong 6)"
	echo "$dlq_headers" | grep -aoE '"ce_[a-z]+"' | sort -u | tr -d '"' | sed 's/^/     /'

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
	require
	purge_lab_queues
	build
	start_lab "cache,audit" "lab-chaos.log"

	# Chot moc TRUOC khi co traffic, va sau khi consumer da len. Lam nguoc lai thi consumer ghi
	# duoc ca phan hang nong truoc khi moc dau duoc doc, va "so event mat" ra AM — do duoc -674
	# truoc khi thu tu nay duoc sua.
	published_before=$(ingestion_stat published)
	failures_before=$(ingestion_stat publishFailures)

	# Cat log ngay sau khi chot moc, khong truoc. Gateway co the con ton dong tu lan chay truoc va
	# ingestion phat not cho trong luc lab dang build — nhung event do da o trong log truoc khi
	# moc dau duoc doc, nen chung dem vao ve NHAN ma khong dem vao ve PHAT. Do duoc -61 truoc khi
	# co dong nay.
	: >"$lab_log"

	drop_csv_files "$CHAOS_COUNT" chaos

	echo
	echo "== cho publish bat dau, tat broker $CHAOS_DOWNTIME s, roi bat lai"
	i=0
	while [ "$i" -lt 30 ]; do
		current=$(ingestion_stat published)
		delta=$(( ${current:-0} - ${published_before:-0} ))
		if [ "$delta" -gt 0 ] && [ "$delta" -lt "$CHAOS_COUNT" ]; then
			break
		fi
		if [ "$delta" -ge "$CHAOS_COUNT" ]; then
			echo "TRUOT: $CHAOS_COUNT event da publish xong truoc khi broker bi tat; lab khong tao outage giua stream." >&2
			return 1
		fi
		i=$((i + 1))
		sleep 1
	done

	if [ "$i" -ge 30 ]; then
		echo "TRUOT: CSV adapter khong bat dau publish trong 30 lan quan sat." >&2
		return 1
	fi

	echo "-- docker compose stop rabbitmq"
	docker compose stop rabbitmq >/dev/null 2>&1

	echo "-- /health/ready cua ingestion trong luc broker tat:"
	docker exec "$INGESTION" dotnet Nvm.Ingestion.dll --health-probe http://127.0.0.1:8080/health/ready 		&& echo "   Healthy (ingestion KHONG chet cung broker — N15)" 		|| echo "   khong tra loi"

	sleep "$CHAOS_DOWNTIME"
	echo "-- docker compose start rabbitmq"
	docker compose start rabbitmq >/dev/null 2>&1

	echo "-- cho du $CHAOS_COUNT ket qua duoc thu publish..."
	settle=0
	while [ "$settle" -lt 60 ]; do
		current_published=$(ingestion_stat published)
		current_failures=$(ingestion_stat publishFailures)
		attempted_so_far=$((
			${current_published:-0} - ${published_before:-0}
			+ ${current_failures:-0} - ${failures_before:-0}
		))
		[ "$attempted_so_far" -ge "$CHAOS_COUNT" ] && break
		settle=$((settle + 1))
		sleep 2
	done

	if [ "$attempted_so_far" -ne "$CHAOS_COUNT" ]; then
		echo "TRUOT: fixture co $CHAOS_COUNT ket qua nhung ingestion da thu publish $attempted_so_far." >&2
		return 1
	fi

	# Them mot nhip cho consumer doc not, roi moi chot CA HAI.
	sleep 15
	published_after=$(ingestion_stat published)
	failures_after=$(ingestion_stat publishFailures)
	published=$(( ${published_after:-0} - ${published_before:-0} ))
	failed=$(( ${failures_after:-0} - ${failures_before:-0} ))
	attempted=$(( published + failed ))

	# Dem theo ce_id DUY NHAT tren dong cua cache-updater, khong dem dong. Bus la at-least-once:
	# broker len lai co the giao lai thu no da giao ma chua duoc xac nhan. Tru so dong ra se cho
	# "so event mat" AM, va mot con so am o day khong phai phat hien nao ca — no la phep tru sai.
	delivered=$(count "measurement-cache received" "$lab_log")
	received=$(grep -a "measurement-cache received" "$lab_log" 2>/dev/null 		| grep -ao "ce_id [0-9a-f-]*" | sort -u | wc -l | tr -d '[:space:]')
	redelivered=$(( delivered - received ))

	echo
	echo "-- ingestion co restart khong? (0 la dung)"
	docker inspect -f '{{.RestartCount}}' "$INGESTION"

	echo
	echo "════════════════════════════════════════════════"
	echo "  ingestion publish thanh cong : $published"
	echo "  ingestion publish that bai   : $failed"
	echo "  ingestion tong event thu phat : $attempted"
	echo "  consumer nhan (dong)         : $delivered"
	echo "  consumer nhan (ce_id duy nhat): $received"
	echo "  GIAO LAI (at-least-once)     : $redelivered"
	echo "  SO EVENT MAT                 : $(( attempted - received ))"
	echo "════════════════════════════════════════════════"
	echo
	echo "So o dong cuoi la ket qua chinh cua lab. Chep vao docs/benchmarks.md va ADR-022"
	echo "nguyen van — khong lam tron ve 0. Row telemetry VAN CON du event mat: do la dual-write"
	echo "cua ADR-022, va M6 (outbox) moi dong duoc no."
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
