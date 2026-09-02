#!/bin/sh
# NovaVolt MES — hai probe của vòng audit 7, chạy lại trên image thật (ADR-035 §Evidence).
#
#   ./file-drop-race-probe.sh pair    PAIR_RACE        — producer publish lại cùng một tên trong
#                                                         lúc consumer đang claim
#   ./file-drop-race-probe.sh resv    RESERVATION_RACE — producer publish trong lúc consumer đang
#                                                         trả một claim hỏng về inbox
#   ./file-drop-race-probe.sh all     cả hai
#
# Đây là BẢN DỰNG LẠI hai probe của auditor, không phải script gốc của họ. Output của bản gốc,
# chạy trên bản sửa dùng marker rời:
#
#   PAIR_RACE claimed_data=B inbox_marker=present
#   RESERVATION_RACE final_data=A
#
# Hai dòng đó nói: consumer lấy byte của export B dưới readiness của A và để lại một mẩu readiness
# mồ côi; và restore ghi đè mất export B mà producer vừa publish. Probe này hỏi lại đúng hai câu đó
# bằng thứ quan sát được từ ngoài, nên nó chạy được trên CẢ HAI thiết kế:
#
#   PAIR_RACE        — inbox có còn gì sau khi rút hết không, và export nào vào được `processed/`
#                      có nguyên vẹn byte-for-byte không
#   RESERVATION_RACE — sau khi race xong, còn tìm lại được ĐỦ CẢ HAI export đã publish không
#
# CANH BAO: probe nay XOA SACH inbox/processed/rejected va TAT MinIO mot lat. No la lab, khong
# phai lenh van hanh — dung chay tren mot stack dang giu du lieu can giu.
#
# Probe im lặng không chứng minh được gì nếu oracle của nó không đỏ nổi, nên mỗi probe chạy kèm
# một NEGATIVE CONTROL ở cuối: một file thả vào inbox KHÔNG theo hợp đồng phải không bao giờ được
# đọc, và một file cắt cụt phải không bao giờ vào được database.

set -e

MSYS_NO_PATHCONV=1
export MSYS_NO_PATHCONV

ROOT=$(cd "$(dirname "$0")/.." && pwd)
cd "$ROOT"

INGESTION="nvm-ingestion"
TIMESCALE="nvm-timescale"
MINIO="nvm-minio"
INBOX="/var/lib/nvm-ingestion/inbox"
PROCESSED="/var/lib/nvm-ingestion/processed"
CHANNEL="NOVAVOLT/NV1/FORMATION/F1/FORM-01/FORM-01-CH-0001"
UNIT_ID="NV1CL16238A00123"
SIGNAL="Formation/CapacityResult"

# Poll interval that C15 runs at. Mọi phép chờ dưới đây là bội số của nó, không phải số bịa.
POLL=5
ROWS=${ROWS:-20}
# Publish PHAI keo dai qua nhieu luot poll. Mot vong lap 200 lan chay xong trong chua den mot giay
# thi consumer chi thay file cuoi cung va race chua bao gio mo ra — do dung la ket qua cua lan chay
# dau, va no khong chung minh gi ca.
RACE_SECONDS=${RACE_SECONDS:-30}

env_value() { grep -E "^$1=" "$ROOT/.env" | cut -d= -f2-; }

PGUSER_=$(env_value NVM_POSTGRES_USER)
PGDB_=$(env_value NVM_POSTGRES_DB)

fail=0

note() { echo "   $*"; }

check() {
	# $1 = nhãn, $2 = giá trị đo, $3 = giá trị phải bằng
	if [ "$2" = "$3" ]; then
		echo "   PASS  $1: $2"
	else
		echo "   FAIL  $1: $2 (phải là $3)"
		fail=1
	fi
}

sql() {
	docker exec "$TIMESCALE" psql -U "$PGUSER_" -d "$PGDB_" -tAc "$1" | tr -d '
'
}

# Mot cho duy nhat bien "giay thu may" thanh mot instant. Truoc do phep tinh nay nam rai o nam
# cho khac nhau, va mot trong nam cho tinh phut bang `second / 60` — export nao roi vao giay 3660
# tro len mang `04:61:xx`, bi tu choi ca file, va probe bao "mat export B" cho mot loi cua chinh no.
instant() {
	printf '2026-09-02T%02d:%02d:%02d.%03dZ' \
		"$((4 + $1 / 3600))" "$(((  $1 % 3600) / 60))" "$(($1 % 60))" "$2"
}

# So row cua mot export, nhan dien bang dai mot giay rieng cua no trong device_timestamp.
rows_in_band() {
	sql "SELECT count(*) FROM ts.telemetry_measurement
	     WHERE signal_code = '$SIGNAL'
	       AND device_timestamp >= '$(instant "$1" 0)'::timestamptz
	       AND device_timestamp <  '$(instant "$(($1 + 1))" 0)'::timestamptz;"
}

inbox_files() {
	docker exec "$INGESTION" sh -c "ls -1 $INBOX 2>/dev/null | grep -v '^\.processing$' || true" | tr -d '
'
}

inbox_count() { inbox_files | grep -c . || true; }

processing_count() {
	docker exec "$INGESTION" sh -c \
		"find $INBOX/.processing -type f 2>/dev/null | wc -l" | tr -d '\r '
}

# SHA-256 cua moi file adapter dang giu: trong inbox VA trong claim do dang. Chi nhin inbox thi
# mot export dang duoc claim ngay luc lay mau trong nhu da mat — cau hoi cua probe la "con giu
# khong", khong phai "dang nam o thu muc nao".
custody_digests() {
	docker exec "$INGESTION" sh -c \
		"find $INBOX -type f -exec sha256sum {} \; 2>/dev/null | cut -d' ' -f1" | tr -d '
'
}

# Digest cua MOI ban goc da archive cho mot ten export. Day la oracle manh nhat co: MinIO la WORM
# content-addressed, nen mot lan doc file cat cut se de lai mot digest LA trong bang nay va khong
# xoa duoc. `processed/` chi giu ban cuoi cung vi cac lan publish dung chung mot ten.
archive_digests() {
	sql "SELECT sha256 FROM ts.raw_curve_archive WHERE reason LIKE '%$1%';"
}

claims_seen() {
	docker logs "$INGESTION" 2>&1 | grep -c "File drop '$1.csv': stored=" || true
}

processed_digests() {
	docker exec "$INGESTION" sh -c \
		"find $PROCESSED -maxdepth 1 -type f -exec sha256sum {} \; 2>/dev/null | cut -d' ' -f1" | tr -d '
'
}

# Sinh nội dung một export vào stdout của container. $1 = giây, $2 = giá trị đầu.
# Mỗi export chiếm một dải mili-giây riêng, nên database trả lời được "export này vào ĐỦ chưa"
# chứ không chỉ "có row nào không" — một lần đọc file cắt cụt sẽ ra ít hơn $ROWS.
export_body() {
	second=$1
	value=$2
	echo "equipment_path,unit_id,signal_code,measured_at,value_kind,value"
	i=0
	while [ "$i" -lt "$ROWS" ]; do
		printf '%s,%s,%s,%s,real,%s.%03d\n' \
			"$CHANNEL" "$UNIT_ID" "$SIGNAL" "$(instant "$second" "$i")" "$value" "$i"
		i=$((i + 1))
	done
}

drain() {
	# Chờ inbox rút hết, tối đa $1 lượt poll. Không sleep cố định: một lượt poll thừa làm probe
	# dài thêm, một lượt thiếu làm nó báo sai.
	tries=$1
	while [ "$tries" -gt 0 ]; do
		if [ "$(inbox_count)" = "0" ] && [ "$(processing_count)" = "0" ]; then
			return 0
		fi
		sleep "$POLL"
		tries=$((tries - 1))
	done
	return 1
}

reset_inbox() {
	docker exec "$INGESTION" sh -c \
		"rm -rf $INBOX/* $INBOX/.processing $PROCESSED/* /var/lib/nvm-ingestion/rejected/* 2>/dev/null || true"
}

require_stack() {
	docker exec "$INGESTION" true 2>/dev/null || { echo "nvm-ingestion chua chay. 'make ingestion-up' truoc."; exit 1; }
	docker exec "$TIMESCALE" true 2>/dev/null || { echo "nvm-timescale chua chay. 'make up' truoc."; exit 1; }

	echo "== image dang chay"
	docker inspect "$INGESTION" --format '   container created: {{.Created}}'
	docker exec "$INGESTION" printenv NVM_INGEST__FileDrop__PublishedSuffix \
		| sed 's/^/   PublishedSuffix: /'
	docker logs "$INGESTION" 2>&1 \
		| grep -E "\[2512\]|\[2513\]" | tail -1 | sed 's/^/   /'
	echo
}

# ─────────────────────────────────────────────────────────
# PAIR_RACE — producer publish lại CÙNG MỘT TÊN trong lúc consumer claim
#
# Bản marker rời lấy marker rồi mới lấy data. Ở đây producer publish A và B xen kẽ vào đúng một
# tên, $PUBLISHES lần, sát nhau hết mức, trong khi consumer poll 5 giây một lần. Hai câu hỏi:
#
#   1. Sau khi rút hết, inbox có còn mẩu nào không? (auditor: `inbox_marker=present`)
#   2. Export vào được `processed/` có đúng byte của A hoặc của B không, và database có bao giờ
#      giữ MỘT PHẦN của một export không?
# ─────────────────────────────────────────────────────────
pair_race() {
	echo "== PAIR_RACE — publish lai cung mot ten trong luc consumer claim"
	reset_inbox
	sleep "$POLL"

	# Ten rieng cho moi lan chay. Archive la WORM va append-only, nen mot lan chay truoc de lai
	# row cua no VINH VIEN — dung chung ten thi oracle dem ca ban goc cua lan truoc va bao sai.
	name="pair-race-$(date +%s)"
	a_second=$((RANDOM % 600 + 600))
	b_second=$((a_second + 1200))

	# Digest của hai bản gốc, tính trong container để so byte-for-byte về sau.
	a_sha=$(export_body "$a_second" 1 | docker exec -i "$INGESTION" sh -c 'sha256sum | cut -d" " -f1' | tr -d '
')
	b_sha=$(export_body "$b_second" 2 | docker exec -i "$INGESTION" sh -c 'sha256sum | cut -d" " -f1' | tr -d '
')

	export_body "$a_second" 1 | docker exec -i "$INGESTION" sh -c "cat > /tmp/pair-a.csv"
	export_body "$b_second" 2 | docker exec -i "$INGESTION" sh -c "cat > /tmp/pair-b.csv"

	# Kiem chinh oracle TRUOC khi do bat cu thu gi: digest cua ban goc phai bang digest cua file
	# that su nam trong container. Sai o day thi moi phep so sanh ben duoi la vo nghia — va mot
	# probe co oracle sai la mot probe bao xanh cho mot he thong hong.
	check "oracle: digest A khop file trong container" 		"$(docker exec "$INGESTION" sh -c "sha256sum /tmp/pair-a.csv | cut -d' ' -f1" | tr -d '
')" "$a_sha"
	check "oracle: digest B khop file trong container" 		"$(docker exec "$INGESTION" sh -c "sha256sum /tmp/pair-b.csv | cut -d' ' -f1" | tr -d '
')" "$b_sha"

	note "A sha256=$a_sha  ($ROWS row, dai $(instant "$a_second" 0))"
	note "B sha256=$b_sha  ($ROWS row, dai $(instant "$b_second" 0))"
	note "publish lien tuc $RACE_SECONDS giay (= $((RACE_SECONDS / POLL)) luot poll), xen ke A/B, vao dung ten '$name.csv.ready'"

	claims_before=$(claims_seen "$name")

	# Ca vong lap chay trong MOT lan exec: moi publish cach nhau mot `cp` + mot `mv`, tuc la sat
	# nhau het muc shell lam duoc, va no keo dai qua nhieu luot poll nen consumer claim NGAY GIUA
	# luc producer dang publish. Mot `docker exec` cho moi lan publish se dat ~100 ms giua hai lan
	# va race se hep di hang tram lan.
	docker exec -e NAME="$name" -e SECONDS_="$RACE_SECONDS" -e INBOX="$INBOX" "$INGESTION" sh -c '
		stop=$(( $(date +%s) + SECONDS_ ))
		i=0
		while [ "$(date +%s)" -lt "$stop" ]; do
			if [ $((i % 2)) -eq 0 ]; then src=/tmp/pair-a.csv; else src=/tmp/pair-b.csv; fi
			cp "$src" "$INBOX/$NAME.csv.partial"
			mv "$INBOX/$NAME.csv.partial" "$INBOX/$NAME.csv.ready"
			i=$((i + 1))
		done
		echo "   publish=$i"
	'

	drain 12 || note "inbox chua rut het sau 12 luot poll"

	echo
	echo "-- oracle"
	# 1. Không còn gì sót lại trong inbox. Đây là chỗ bản marker rời in `inbox_marker=present`.
	leftovers=$(inbox_files)
	if [ -z "$leftovers" ]; then
		check "inbox sau khi rut het (so file)" "0" "0"
	else
		echo "   FAIL  inbox con lai:"
		echo "$leftovers" | sed 's/^/         /'
		fail=1
	fi
	check "claim con bo do trong .processing" "$(processing_count)" "0"

	# 2. Race co that su mo ra khong: consumer phai claim NHIEU LAN trong lúc producer publish.
	claims=$(( $(claims_seen "$name") - claims_before ))
	note "so lan consumer claim trong luc producer dang publish: $claims"
	if [ "$claims" -lt 2 ]; then
		echo "   FAIL  race khong mo ra ($claims claim) — tang RACE_SECONDS roi chay lai"
		fail=1
	else
		check "claim chong len cua so publish (>= 2)" "yes" "yes"
	fi

	# 3. Moi ban goc DA ARCHIVE phai la exact byte cua A hoac cua B. Archive la WORM va
	# content-addressed, nen mot lan doc cat cut se de lai mot digest la o day vinh vien.
	unknown=0
	archived=0
	for d in $(archive_digests "$name"); do
		archived=$((archived + 1))
		[ "$d" = "$a_sha" ] || [ "$d" = "$b_sha" ] || unknown=$((unknown + 1))
	done
	note "so ban goc da archive cho '$name.csv': $archived"
	check "ban goc archive khong khop A hay B" "$unknown" "0"

	unknown_processed=0
	for d in $(processed_digests); do
		[ "$d" = "$a_sha" ] || [ "$d" = "$b_sha" ] || unknown_processed=$((unknown_processed + 1))
	done
	check "file trong processed/ khong khop A hay B" "$unknown_processed" "0"

	# 3. Database: mỗi export vào ĐỦ $ROWS row hoặc chưa vào — không bao giờ ở giữa.
	a_rows=$(rows_in_band "$a_second")
	b_rows=$(rows_in_band "$b_second")
	note "row cua A trong DB: $a_rows / $ROWS      row cua B: $b_rows / $ROWS"
	for r in "$a_rows" "$b_rows"; do
		if [ "$r" != "0" ] && [ "$r" != "$ROWS" ]; then
			echo "   FAIL  mot export vao DB chi MOT PHAN: $r / $ROWS"
			fail=1
		fi
	done
	[ "$a_rows" = "0" ] && [ "$b_rows" = "0" ] && { echo "   FAIL  khong export nao vao duoc DB — probe khong chay dung"; fail=1; }
	check "co it nhat mot export vao du" "$([ "$a_rows" = "$ROWS" ] || [ "$b_rows" = "$ROWS" ] && echo yes || echo no)" "yes"

	echo
	echo "PAIR_RACE claimed_data=$( [ "$a_rows" = "$ROWS" ] && printf A; [ "$b_rows" = "$ROWS" ] && printf B ) torn_archives=$unknown inbox_leftover=$( [ -z "$leftovers" ] && echo none || echo present )"
	echo
}

# ─────────────────────────────────────────────────────────
# RESERVATION_RACE — producer publish trong lúc consumer trả một claim hỏng về inbox
#
# MinIO tắt ⇒ archive không tới được ⇒ ADR-033: file được trả về inbox và thử lại. Đó là đường duy
# nhất kích được restore từ bên ngoài. Trong lúc A đang quay vòng retry, producer publish B vào
# đúng cái tên A đã đến. Câu hỏi: cuối cùng còn tìm lại được mấy export?
# ─────────────────────────────────────────────────────────
reservation_race() {
	echo "== RESERVATION_RACE — publish trong luc consumer tra claim hong ve inbox"
	reset_inbox
	sleep "$POLL"

	name="resv-race-$(date +%s)"
	a_second=$((RANDOM % 600 + 1900))
	b_second=$((a_second + 1200))

	a_sha=$(export_body "$a_second" 3 | docker exec -i "$INGESTION" sh -c 'sha256sum | cut -d" " -f1' | tr -d '
')
	b_sha=$(export_body "$b_second" 4 | docker exec -i "$INGESTION" sh -c 'sha256sum | cut -d" " -f1' | tr -d '
')
	export_body "$a_second" 3 | docker exec -i "$INGESTION" sh -c "cat > /tmp/resv-a.csv"
	export_body "$b_second" 4 | docker exec -i "$INGESTION" sh -c "cat > /tmp/resv-b.csv"

	# Kiem chinh oracle TRUOC khi do bat cu thu gi: digest cua ban goc phai bang digest cua file
	# that su nam trong container. Sai o day thi moi phep so sanh ben duoi la vo nghia — va mot
	# probe co oracle sai la mot probe bao xanh cho mot he thong hong.
	check "oracle: digest A khop file trong container" 		"$(docker exec "$INGESTION" sh -c "sha256sum /tmp/resv-a.csv | cut -d' ' -f1" | tr -d '
')" "$a_sha"
	check "oracle: digest B khop file trong container" 		"$(docker exec "$INGESTION" sh -c "sha256sum /tmp/resv-b.csv | cut -d' ' -f1" | tr -d '
')" "$b_sha"

	note "A sha256=$a_sha"
	note "B sha256=$b_sha"

	note "tat MinIO — archive khong toi duoc, claim bi tra ve inbox (ADR-033)"
	docker stop "$MINIO" >/dev/null

	# A vào inbox và bắt đầu quay vòng retry.
	docker exec "$INGESTION" sh -c \
		"cp /tmp/resv-a.csv $INBOX/$name.csv.partial && mv $INBOX/$name.csv.partial $INBOX/$name.csv.ready"

	note "cho A quay it nhat mot vong retry"
	spins=0
	while [ "$spins" -lt 12 ]; do
		if inbox_files | grep -q "retry-"; then break; fi
		sleep "$POLL"
		spins=$((spins + 1))
	done
	inbox_files | sed 's/^/         /'

	note "publish B vao DUNG cai ten A da den"
	docker exec "$INGESTION" sh -c \
		"cp /tmp/resv-b.csv $INBOX/$name.csv.partial && mv $INBOX/$name.csv.partial $INBOX/$name.csv.ready"

	# Lay mau moi giay trong ba luot poll, khong phai mot lan duy nhat. MinIO van tat nen KHONG
	# export nao roi khoi vong retry duoc: cai gi bi restore ghi de mat se khong bao gio xuat hien
	# lai o mot mau nao sau do. Mot lan lay mau don le lai la mot cuoc dua khac — file dang duoc
	# rename ngay luc sha256sum chay thi bien mat khoi ket qua ma khong he mat that.
	echo
	echo "-- lay mau custody moi giay trong $((POLL * 3)) giay (MinIO van tat)"
	has_a=no; has_b=no
	samples=0
	seen=0
	while [ "$samples" -lt $((POLL * 3)) ]; do
		for d in $(custody_digests); do
			seen=$((seen + 1))
			[ "$d" = "$a_sha" ] && has_a=yes
			[ "$d" = "$b_sha" ] && has_b=yes
		done
		samples=$((samples + 1))
		sleep 1
	done
	# Mot mau khong thay file nao la mot oracle mu, khong phai mot he thong sach.
	note "so lan lay mau: $samples, tong so file quan sat duoc: $seen"
	check "co quan sat duoc file nao khong" "$([ "$seen" -gt 0 ] && echo yes || echo no)" "yes"
	docker exec "$INGESTION" sh -c "find $INBOX -type f" | sed 's|.*/|         |'

	echo
	echo "-- oracle"
	check "export A van con (inbox hoac claim)" "$has_a" "yes"
	check "export B van con (inbox hoac claim)" "$has_b" "yes"

	found=""
	[ "$has_a" = "yes" ] && found="A"
	[ "$has_b" = "yes" ] && found="${found}B"
	echo
	echo "RESERVATION_RACE final_data=${found:-none}"

	note "bat MinIO lai — ca hai phai chay tiep den processed/"
	docker start "$MINIO" >/dev/null
	# MinIO cần vài giây để nhận request; chờ nó healthy thay vì đoán.
	waited=0
	while [ "$waited" -lt 60 ]; do
		status=$(docker inspect "$MINIO" --format '{{.State.Health.Status}}' 2>/dev/null | tr -d '
')
		[ "$status" = "healthy" ] && break
		sleep 2
		waited=$((waited + 2))
	done

	drain 12 || note "inbox chua rut het sau 12 luot poll"

	proc=$(processed_digests)
	proc_a=no; proc_b=no
	for d in $proc; do
		[ "$d" = "$a_sha" ] && proc_a=yes
		[ "$d" = "$b_sha" ] && proc_b=yes
	done
	check "A den duoc processed/" "$proc_a" "yes"
	check "B den duoc processed/" "$proc_b" "yes"

	a_rows=$(rows_in_band "$a_second")
	b_rows=$(rows_in_band "$b_second")
	check "row cua A trong DB" "$a_rows" "$ROWS"
	check "row cua B trong DB" "$b_rows" "$ROWS"
	echo
}

# ─────────────────────────────────────────────────────────
# Negative control — oracle của probe phải đỏ được, nếu không probe xanh chẳng nói gì
# ─────────────────────────────────────────────────────────
negative_control() {
	echo "== NEGATIVE CONTROL — oracle co do noi khong"
	reset_inbox
	sleep "$POLL"

	c_second=$((RANDOM % 600 + 3200))
	export_body "$c_second" 7 | docker exec -i "$INGESTION" sh -c "cat > $INBOX/not-published.csv"
	note "tha 'not-published.csv' — dung noi dung, SAI hop dong (khong rename vao .ready)"

	sleep $((POLL * 3))

	c_rows=$(rows_in_band "$c_second")
	check "row cua file chua publish trong DB" "$c_rows" "0"
	check "file chua publish van nam nguyen trong inbox" \
		"$(inbox_files | grep -c '^not-published.csv$' || true)" "1"
	check "no khong bi claim" "$(processing_count)" "0"

	note "rename vao dung cho — cung file do phai vao ngay"
	docker exec "$INGESTION" sh -c "mv $INBOX/not-published.csv $INBOX/not-published.csv.ready"
	drain 6 || true
	check "row sau khi publish" \
		"$(rows_in_band "$c_second")" \
		"$ROWS"
	echo
}

require_stack

case "${1:-all}" in
	pair) pair_race ;;
	resv) reservation_race ;;
	control) negative_control ;;
	all) pair_race; reservation_race; negative_control ;;
	*) echo "usage: $0 [pair|resv|control|all]"; exit 2 ;;
esac

if [ "$fail" -eq 0 ]; then
	echo "PROBE OK — moi oracle xanh"
else
	echo "PROBE FAILED — xem dong FAIL o tren"
	exit 1
fi
