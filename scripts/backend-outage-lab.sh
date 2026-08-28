#!/usr/bin/env sh
#
# Lab D3 (plan M2 §5.C10.2): tat toan bo backend giua luc simulator chay.
#
# Menh de phai chung minh: simulator VAN CHAY binh thuong khi backend chet (N15), khong mat
# message nao tren chang thiet bi -> ingestion, va backlog tieu het duoi 3 phut.
#
# Script KHONG `make down-v` va KHONG xoa volume (AGENTS.md §1.2). No chi stop/start container.
#
# Lab SO HUU vong doi cua simulator, giong reconcile.sh va vi dung mot ly do: doc hai ve o hai
# thoi diem khac nhau tren mot day chuyen dang chay thi phan sinh ra o giua chi roi vao mot ve.
# Lan chay dau tru hai TONG TUYET DOI va in ra "97.492 row lech" — mot con so khong noi len gi.
#
# FAIL-CLOSED. Moi menh de cua D3 la mot phep assert rieng, va bat ky bang chung nao thieu cung la
# truot. Ban truoc chi kiem `DRAINED=1` va `DRIFT >= 0`, nen no co the bao dat trong khi simulator
# da chet, gateway da restart, hoac ve phai dang dem ca mot bang telemetry cua duong day khac.

set -eu

MSYS_NO_PATHCONV=1
export MSYS_NO_PATHCONV

WARMUP=${WARMUP:-120}
OUTAGE=${OUTAGE:-120}
DRAIN_BUDGET=${DRAIN_BUDGET:-180}
LINE_PREFIX=${LINE_PREFIX:-NOVAVOLT/NV1/FORMATION/F1/}
COMPOSE="docker compose"
STOPPED="ingestion timescale rabbitmq"

. "$(dirname "$0")/gateway-snapshot.sh"
. "$(dirname "$0")/reset-ingestion-fixture.sh"

NVM_POSTGRES_USER=$(grep -E '^NVM_POSTGRES_USER=' .env | cut -d= -f2-)
NVM_POSTGRES_DB=$(grep -E '^NVM_POSTGRES_DB=' .env | cut -d= -f2-)
export NVM_POSTGRES_USER NVM_POSTGRES_DB

PROJECT=$(docker inspect -f '{{index .Config.Labels "com.docker.compose.project"}}' nvm-timescale 2>/dev/null)
SIM_VOLUME="${PROJECT}_simdata"
BACKEND_STOPPED=false

say() { printf '%s\n' "$1"; }

fail() {
  say "FAIL: $1" >&2
  FAILED=1
}

# Mot lab that bai giua chung khong duoc de lai mot stack tat dien. Nguoi chay tiep theo se doc
# "ingestion khong len" thanh mot phat hien moi, va do la mot gio di tim thu khong ton tai.
cleanup() {
  status=$?
  trap - EXIT

  if [ "$BACKEND_STOPPED" = "true" ]; then
    say ""
    say "  Bat lai $STOPPED sau khi lab ket thuc..."
    # shellcheck disable=SC2086
    $COMPOSE --profile ingestion start $STOPPED >/dev/null 2>&1 || true
  fi

  exit "$status"
}

interrupted() {
  status=$1
  trap - HUP INT TERM
  exit "$status"
}

trap cleanup EXIT
trap 'interrupted 129' HUP
trap 'interrupted 130' INT
trap 'interrupted 143' TERM

# Doc tu VOLUME, khong tu container: ve trai duoc chot SAU khi simulator dung.
report_field() {
  docker run --rm -v "$SIM_VOLUME:/data:ro" alpine:3.21 \
    cat /data/simulator-run.json 2>/dev/null \
    | tr -d ' \r' \
    | sed -n "s/.*\"$1\":\([0-9]*\),*.*/\1/p" \
    | head -1
}

# Ve PHAI phai dem dung duong day ma ve TRAI noi ve. Ban truoc dem `count(*)` toan bang, nen mot
# row cua bat ky thiet bi nao khac cung lam D3 lech ma khong ai chi ra duoc no den tu dau.
telemetry_rows() {
  docker exec nvm-timescale psql -U "$NVM_POSTGRES_USER" -d "$NVM_POSTGRES_DB" -tAc \
    "SELECT count(*) FROM ts.telemetry_measurement WHERE equipment_id LIKE '${LINE_PREFIX}%';" \
    2>/dev/null | tr -d ' \r'
}

restart_count() {
  docker inspect -f '{{.RestartCount}}' "$1" 2>/dev/null || echo 0
}

# Đợi tới khi MỌI thứ nguồn đã phát nằm trên đĩa của gateway.
#
# `decoded` đứng yên nghĩa là EMQX đã giao hết; `buffered == decoded` nghĩa là mỗi thứ đã giao cũng
# đã fsync. Chỉ khi cả hai đúng thì backlog mới là một con số HỮU HẠN, và chỉ lúc đó "thời gian tiêu
# backlog" mới là một phép đo thay vì một cuộc đuổi bắt.
wait_for_absorption() {
  budget=$1
  started=$(date +%s)
  previous=-1
  stable=0

  while [ "$(( $(date +%s) - started ))" -le "$budget" ]; do
    snapshot=$(gateway_snapshot || true)
    decoded=$(snapshot_field "$snapshot" decoded)
    buffered=$(snapshot_field "$snapshot" buffered)

    if [ -n "$decoded" ] && [ "$decoded" = "$previous" ] && [ "$decoded" = "$buffered" ]; then
      stable=$((stable + 1))
      [ "$stable" -ge 3 ] && return 0
    else
      stable=0
    fi

    previous=${decoded:--1}
    sleep 1
  done

  return 1
}

# Đợi backlog HỮU HẠN đã chốt được forward hết, và hàng đợi đứng yên ở 0.
#
# `depth = 0` một mình chưa đủ: nguồn đã dừng nên nó ổn định, nhưng nó không nói gì về việc batch
# cuối đã được ACK hay chưa. Câu hỏi thứ hai phải là *"đã forward đủ CHỪNG ẤY record chưa"*, và
# `target` là con số đó: `forwarded` tại `SNAPSHOT_FINITE` cộng backlog đo được ở chính snapshot ấy.
#
# Không dùng `buffered == forwarded`. Hai counter đó đếm theo đời TIẾN TRÌNH hiện tại, còn buffer và
# cursor thì sống qua restart: gateway forward một record do tiến trình trước ghi xuống thì
# `forwarded` lớn hơn `buffered` một khoản hoàn toàn hợp lệ, và đẳng thức tuyệt đối không bao giờ
# đúng nữa. Đo được ngày 2026-08-30: ba snapshot liên tiếp đều `buffer_depth=0` mà
# `buffered=33616` với `forwarded=33673` — lệch 57 vĩnh viễn. Vòng đợi cũ vì thế chờ hết ngân sách
# trên một backlog đã sạch từ lâu, và 181/180 giây ghi lại được là thời gian của cái đồng hồ chứ
# không phải của đường ống. Delta so với một mốc đã chốt thì miễn nhiễm với khoản lệch đó.
#
# `-eq`, không phải `-ge`: nguồn đã dừng và mọi thứ đã fsync, nên không có record nào được phép
# xuất hiện thêm. Forward VƯỢT target là một con số không giải thích được, và một oracle nhận nó là
# đạt thì không còn phát hiện được gì nữa.
#
# `-lt`, không phải `-le`: DoD viết *"tiêu hết TRONG DƯỚI 3 phút"*, nên đúng 180 giây là trượt. Một
# vòng đợi chấp nhận `elapsed == budget` biến một bất đẳng thức nghiêm thành một bất đẳng thức lỏng
# ở đúng cái điểm mà nó được phát biểu để loại trừ.
wait_for_forward_quiescence() {
  budget=$1
  target=$2
  started=$(date +%s)
  stable=0

  while [ "$(( $(date +%s) - started ))" -lt "$budget" ]; do
    snapshot=$(gateway_snapshot || true)
    depth=$(snapshot_field "$snapshot" buffer_depth)
    forwarded=$(snapshot_field "$snapshot" forwarded)

    case "$forwarded" in
      ''|*[!0-9]*) forwarded=-1 ;;
    esac

    if [ "$depth" = "0" ] && [ "$forwarded" -eq "$target" ]; then
      stable=$((stable + 1))
      [ "$stable" -ge 3 ] && return 0
    else
      stable=0
    fi

    sleep 1
  done

  return 1
}

require_unsigned_integer() {
  case "$2" in
    ''|*[!0-9]*)
      say "Bang chung '$1' khong phai so nguyen khong am: '$2'." >&2
      exit 1
      ;;
  esac
}

say "Lab D3 — tat backend $OUTAGE giay giua luc simulator chay"
say ""
say "  Warmup       : $WARMUP giay"
say "  Outage       : $OUTAGE giay ($STOPPED)"
say "  Ngan sach xa : $DRAIN_BUDGET giay"
say "  Duong day    : $LINE_PREFIX"
say ""

if [ "$OUTAGE" -lt 120 ]; then
  say "OUTAGE phai >= 120 giay; D3 khong duoc rut ngan de xanh." >&2
  exit 2
fi

if [ "$DRAIN_BUDGET" -gt 180 ]; then
  say "DRAIN_BUDGET khong duoc vuot 180 giay." >&2
  exit 2
fi

for container in nvm-edge-gateway nvm-ingestion nvm-timescale; do
  if [ "$(docker inspect -f '{{.State.Running}}' "$container" 2>/dev/null || echo false)" != "true" ]; then
    say "Thieu $container. Chay: make up && make edge-up && make ingestion-up" >&2
    exit 2
  fi
done

if ! docker inspect nvm-simulator >/dev/null 2>&1; then
  say "Thieu nvm-simulator. Chay: make sim-up" >&2
  exit 2
fi

gateway_wait_snapshot 120 || {
  say "Gateway khong ghi $NVM_GATEWAY_STATS. Build lai gateway: make edge-up" >&2
  exit 2
}

say "0/7 Dung simulator, xa het duong ong, chot moc dau..."
$COMPOSE --profile sim stop simulator >/dev/null 2>&1

gateway_drain "$DRAIN_BUDGET" || {
  say "Duong ong chua rong truoc khi lab bat dau; moc dau se sai." >&2
  exit 1
}

sleep 10

# Fixture về trạng thái xác định, và làm việc đó khi nguồn ĐÃ DỪNG — nếu không thì TRUNCATE chạy
# song song với dòng dữ liệu đang tới và mốc đầu là một con số không ai tái lập được.
reset_ingestion_fixture || exit 1

ROWS_BEFORE=$(telemetry_rows)
require_unsigned_integer rows_before "$ROWS_BEFORE"
say "    telemetry_rows=$ROWS_BEFORE (depth=0)"

# Chot counter TRUOC khi bat lai nguon. Doc chung sau do lay ve nhung con so o nhung thoi diem
# khac nhau cua mot duong ong dang chay, va hieu so cuoi cung se sai theo mot huong khong doan duoc.
SNAPSHOT_BEFORE=$(gateway_snapshot)
GW_DECODED_BEFORE=$(snapshot_field "$SNAPSHOT_BEFORE" decoded)
GW_FORWARDED_BEFORE=$(snapshot_field "$SNAPSHOT_BEFORE" forwarded)
GW_REJECTED_BEFORE=$(snapshot_field "$SNAPSHOT_BEFORE" rejected)

# Khoi dong lai de run report ve 0: ve trai la ca run nay, khong phai mot hieu so.
$COMPOSE --profile sim up -d --force-recreate simulator >/dev/null 2>&1

SIM_RESTARTS_BEFORE=$(restart_count nvm-simulator)
GW_RESTARTS_BEFORE=$(restart_count nvm-edge-gateway)

say "1/7 Warmup $WARMUP giay..."
sleep "$WARMUP"
say "    logicalMeasurements=$(report_field logicalMeasurements)"

# Muc day thuong truc cua duong day, do trong 12 giay ngay truoc khi tat. Tren mot day chuyen con
# phat, hang doi khong bao gio rong — xem gateway-snapshot.sh. Day la nguong ma "backlog da tieu
# het" duoc so voi, chu khong phai so 0.
DEPTH_BEFORE_OUTAGE=$(gateway_steady_depth 12)
# Doc ngay truoc khi tat. Trong suot outage khong co gi duoc acknowledge nen segment chi lon them,
# va hieu so nay la chi phi dia THUC cua mot record — khong lan voi segment cu con nam lai.
BYTES_BEFORE_OUTAGE=$(gateway_stat buffer_bytes)
require_unsigned_integer depth_before_outage "$DEPTH_BEFORE_OUTAGE"
require_unsigned_integer bytes_before_outage "$BYTES_BEFORE_OUTAGE"
say "    muc day thuong truc     : $DEPTH_BEFORE_OUTAGE"

# Vế trái tại thời điểm tắt backend. Hiệu số với con số cuối là bằng chứng dây chuyền VẪN CHẠY
# trong lúc backend chết — đó là N15, và không có phép đo nào khác nói được điều đó.
MEASUREMENTS_AT_OUTAGE=$(report_field logicalMeasurements)
require_unsigned_integer measurements_at_outage "$MEASUREMENTS_AT_OUTAGE"

say "2/7 Tat $STOPPED..."
OUTAGE_START=$(date +%s)
BACKEND_STOPPED=true
# shellcheck disable=SC2086
$COMPOSE --profile ingestion stop $STOPPED >/dev/null 2>&1

say "3/7 Cho $OUTAGE giay..."
sleep "$OUTAGE"

SIM_ALIVE=$(docker inspect -f '{{.State.Running}}' nvm-simulator 2>/dev/null || echo false)
GW_ALIVE=$(docker inspect -f '{{.State.Running}}' nvm-edge-gateway 2>/dev/null || echo false)
SIM_RESTARTS_AFTER=$(restart_count nvm-simulator)
GW_RESTARTS_AFTER=$(restart_count nvm-edge-gateway)
SNAPSHOT_AT_OUTAGE=$(gateway_snapshot)
DEPTH_AT_OUTAGE=$(snapshot_field "$SNAPSHOT_AT_OUTAGE" buffer_depth)
BUFFER_BYTES=$(snapshot_field "$SNAPSHOT_AT_OUTAGE" buffer_bytes)
require_unsigned_integer depth_at_outage "$DEPTH_AT_OUTAGE"
require_unsigned_integer buffer_bytes "$BUFFER_BYTES"

say "    simulator con chay      : $SIM_ALIVE"
say "    gateway con chay        : $GW_ALIVE"
say "    simulator restart       : $SIM_RESTARTS_BEFORE -> $SIM_RESTARTS_AFTER"
say "    gateway restart         : $GW_RESTARTS_BEFORE -> $GW_RESTARTS_AFTER"
say "    message tren dia (depth): $DEPTH_AT_OUTAGE"
say "    buffer bytes            : $BUFFER_BYTES"

# Dừng nguồn TRONG LÚC backend vẫn tắt. Đây là thay đổi quan trọng nhất của lab này.
#
# Bản cũ bật backend rồi mới bấm giờ trong khi simulator VẪN đang phát, nên "thời gian tiêu backlog"
# là thời gian đuổi theo một hàng đợi còn được nạp thêm — và ngưỡng 180 giây được so với một con số
# không có nghĩa. Tệ hơn: message mà FaultInjectingPublisher đang giữ trong RAM lúc đó còn chưa ra
# khỏi simulator, nên đồng hồ dừng trước khi chúng tồn tại, rồi phép so row chạy SAU cùng vẫn xanh
# dù chúng chỉ về đích ngoài ngân sách.
#
# Dừng nguồn trước: `FlushAsync` xả hết phần đang giữ, gateway fsync hết xuống đĩa, và backlog trở
# thành một con số HỮU HẠN đã đứng yên. Từ lúc đó "tiêu hết trong bao lâu" mới là một phép đo.
say "4/7 Dung simulator trong luc backend VAN TAT, cho fsync het phan dang giu..."
$COMPOSE --profile sim stop simulator >/dev/null 2>&1

ABSORBED=0
wait_for_absorption "$DRAIN_BUDGET" && ABSORBED=1

LEFT=$(report_field logicalMeasurements)
require_unsigned_integer logical_measurements "$LEFT"

# Phep do da sinh ra ma link khong cho di. Doc o day chu khong suy ra tu hieu so: hieu so noi co mat
# hay khong, con so nay noi mat o TRUOC hay SAU broker. Rong nghia la report do mot simulator cu hon
# oracle hien tai ghi ra, va require_unsigned_integer bat loi to thay vi de gate im lang.
ABANDONED=$(report_field abandonedMeasurements)
require_unsigned_integer abandoned_measurements "$ABANDONED"

# Ca hai con so tu CUNG mot snapshot, va chot khi backend van tat con nguon da dung: luc do backlog
# la mot con so dung yen va `forwarded` khong the nhuc nhich. Doc `forwarded` o mot lan `docker exec`
# khac la lay hai moc o hai thoi diem roi tru chung cho nhau.
SNAPSHOT_FINITE=$(gateway_snapshot)
BACKLOG_RECORDS=$(snapshot_field "$SNAPSHOT_FINITE" buffer_depth)
FORWARDED_FINITE=$(snapshot_field "$SNAPSHOT_FINITE" forwarded)
require_unsigned_integer backlog_records "$BACKLOG_RECORDS"
require_unsigned_integer forwarded_finite "$FORWARDED_FINITE"

# Dich den cua lan xa nay, phat bieu bang mot con so tuyet doi thay vi bang mot dang thuc giua hai
# counter. `forwarded` la counter cua tien trinh gateway hien tai; cong them backlog da chot thi
# khoan lech lich su (record cua tien trinh truoc) nam ca trong so hang thu nhat va tu triet tieu.
FORWARD_TARGET=$(( FORWARDED_FINITE + BACKLOG_RECORDS ))

say "    phep do logic cua ca run : $LEFT"
say "    backlog huu han tren dia : $BACKLOG_RECORDS record"
say "    forward da chot / dich   : $FORWARDED_FINITE -> $FORWARD_TARGET"

# Bấm giờ TRƯỚC lệnh bật backend, không phải sau.
#
# Mệnh đề của D3 là *"bật lại → backlog tiêu hết dưới 3 phút"*, và trên dây chuyền thật đồng hồ đó
# bắt đầu lúc người vận hành bật MES lên, không phải lúc ba container đã sẵn sàng nhận request.
# `docker compose start` của `timescale` + `rabbitmq` + `ingestion` mất một khoảng thật, và bản
# trước đặt `DRAIN_START` SAU lệnh đó nên khoảng ấy rơi ra ngoài phép đo — trong khi
# `benchmarks.md` dòng 2026-08-29 lại ghi rằng nó đã được tính vào. Hai chỗ nói khác nhau về cùng
# một con số, và chỗ đo là chỗ sai.
say "5/7 Bam gio, roi bat lai backend tren mot backlog dung yen..."
DRAIN_START=$(date +%s)
# shellcheck disable=SC2086
$COMPOSE --profile ingestion start $STOPPED >/dev/null 2>&1
BACKEND_STOPPED=false
OUTAGE_SECONDS=$(( $(date +%s) - OUTAGE_START ))

say "6/7 Do thoi gian tieu backlog toi depth=0 va counter dung yen..."
DRAINED=0
# Ngân sách CÒN LẠI, không phải ngân sách đầy đủ. Thời gian khởi động đã tiêu một phần của 180
# giây; đưa cả 180 vào đây thì vòng đợi có thể thành công ở giây thứ 200 kể từ lúc bấm giờ.
wait_for_forward_quiescence "$(( DRAIN_BUDGET - ($(date +%s) - DRAIN_START) ))" "$FORWARD_TARGET" && DRAINED=1
DRAIN_SECONDS=$(( $(date +%s) - DRAIN_START ))

say "7/7 Doi chieu..."
sleep 5

ROWS_END=$(telemetry_rows)
require_unsigned_integer rows_end "$ROWS_END"
SNAPSHOT_AFTER=$(gateway_snapshot)
GW_DECODED_AFTER=$(snapshot_field "$SNAPSHOT_AFTER" decoded)
GW_FORWARDED_AFTER=$(snapshot_field "$SNAPSHOT_AFTER" forwarded)
GW_REJECTED_AFTER=$(snapshot_field "$SNAPSHOT_AFTER" rejected)
GW_DECODED=$((GW_DECODED_AFTER - GW_DECODED_BEFORE))
GW_FORWARDED=$((GW_FORWARDED_AFTER - GW_FORWARDED_BEFORE))
GW_REJECTED=$((GW_REJECTED_AFTER - GW_REJECTED_BEFORE))
PUBLISHED=$(report_field publishedMessages)
OUTAGE_BYTES=$((BUFFER_BYTES - BYTES_BEFORE_OUTAGE))
OUTAGE_RECORDS=$((DEPTH_AT_OUTAGE - DEPTH_BEFORE_OUTAGE))
BYTES_PER_RECORD=0
[ "$OUTAGE_RECORDS" -gt 0 ] && BYTES_PER_RECORD=$((OUTAGE_BYTES / OUTAGE_RECORDS))
RIGHT=$(( ROWS_END - ROWS_BEFORE ))
DRIFT=$(( RIGHT - LEFT ))
THROTTLED=$(snapshot_field "$SNAPSHOT_AFTER" throttled_flushes)
RATE_LIMITED=$(snapshot_field "$SNAPSHOT_AFTER" rate_limited_flushes)
FINAL_DEPTH=$(snapshot_field "$SNAPSHOT_AFTER" buffer_depth)

say ""
say "  Ket qua D3"
say "  ------------------------------------------------------"
say "  Thoi gian backend thuc su tat            : ${OUTAGE_SECONDS}s (yeu cau >= ${OUTAGE}s)"
say "  Depth truoc khi tat                      : $DEPTH_BEFORE_OUTAGE"
say "  Message tren dia sau $OUTAGE giay tat    : $DEPTH_AT_OUTAGE"
say "  Buffer bytes sau $OUTAGE giay tat        : $BUFFER_BYTES"
say "  Phep do sinh ra TRONG outage             : $(( LEFT - MEASUREMENTS_AT_OUTAGE ))"
say "  Backlog huu han khi bat dau bam gio      : $BACKLOG_RECORDS record"
say "  Thoi gian tieu backlog                   : ${DRAIN_SECONDS}s (ngan sach STRICT < ${DRAIN_BUDGET}s)"
say "    (dong ho bat dau TRUOC lenh bat backend, nen no bao gom ca thoi gian ba container len lai)"
say "  Backlog tieu het trong ngan sach         : $([ "$DRAINED" = 1 ] && echo yes || echo NO)"
say "  Forward dich / thuc te                   : $FORWARD_TARGET / ${GW_FORWARDED_AFTER:-?}"
say "  Depth cuoi cung                          : ${FINAL_DEPTH:-?}"
say "  Simulator con chay suot outage           : $SIM_ALIVE"
say "  Simulator restart                        : $(( SIM_RESTARTS_AFTER - SIM_RESTARTS_BEFORE ))"
say "  Gateway restart                          : $(( GW_RESTARTS_AFTER - GW_RESTARTS_BEFORE ))"
say "  Byte moi record tren dia                 : $BYTES_PER_RECORD ($OUTAGE_BYTES B / $OUTAGE_RECORDS record)"
say "  Simulator publish / gateway decode       : $PUBLISHED / $GW_DECODED"
say "    (chenh lech = so NBIRTH: mot NBIRTH chi mang bdSeq nen khong sinh phep do nao)"
say "  Gateway forward / reject                 : $GW_FORWARDED / $GW_REJECTED"
say "  Phep do logic sinh ra (ve trai)          : $LEFT"
say "  Row telemetry them vao (ve phai)         : $RIGHT"
say "  So row lech (phai dung bang 0)           : $DRIFT"
say "  Phep do link khong cho di (phai bang 0)  : $ABANDONED"
say "  gateway.flush.throttled                  : ${THROTTLED:-?}"
say "  gateway.flush.rate_limited               : ${RATE_LIMITED:-?}"
say ""

FAILED=0
[ "$OUTAGE_SECONDS" -ge "$OUTAGE" ] || fail "backend chi tat ${OUTAGE_SECONDS}s, can >= ${OUTAGE}s"
[ "$SIM_ALIVE" = "true" ] || fail "simulator khong con chay khi backend chet — day la N15"
[ "$GW_ALIVE" = "true" ] || fail "gateway khong con chay khi backend chet"
[ "$(( SIM_RESTARTS_AFTER - SIM_RESTARTS_BEFORE ))" -eq 0 ] || fail "simulator da restart"
[ "$(( GW_RESTARTS_AFTER - GW_RESTARTS_BEFORE ))" -eq 0 ] || fail "gateway da restart"
[ "$DEPTH_AT_OUTAGE" -gt "$DEPTH_BEFORE_OUTAGE" ] \
  || fail "buffer khong tang trong outage ($DEPTH_BEFORE_OUTAGE -> $DEPTH_AT_OUTAGE); lab chua chung minh gi"
# N15 bang so, khong bang mot container con song. Mot simulator dang chay nhung da ngung phat cung
# tra ve "true" o phep kiem tren; chi con so nay noi duoc day chuyen VAN LAM VIEC.
[ "$LEFT" -gt "$MEASUREMENTS_AT_OUTAGE" ] \
  || fail "phep do khong tang trong outage ($MEASUREMENTS_AT_OUTAGE -> $LEFT); day chuyen da dung"
[ "$ABSORBED" = 1 ] \
  || fail "nguon chua duoc fsync het khi bat lai backend; backlog chua huu han nen dong ho vo nghia"
[ "$BACKLOG_RECORDS" -gt 0 ] || fail "backlog bang 0 luc bat dau bam gio; khong co gi de tieu"
[ "$DRAINED" = 1 ] || fail "backlog khong tieu het trong ${DRAIN_BUDGET}s"
# Doc lai o cuoi, sau khi moi thu da dung: mot lan chay ma vong doi het gio van phai noi duoc no
# dung o dau. Thieu so voi dich la con hang; vuot dich la mot con so khong ai giai thich duoc.
[ "${GW_FORWARDED_AFTER:-0}" -eq "$FORWARD_TARGET" ] \
  || fail "forward ${GW_FORWARDED_AFTER:-?} != dich $FORWARD_TARGET"
# Hai vế khác nhau, và vế thứ hai là vế bị thiếu. `DRAINED` chỉ nói vòng đợi đã thấy trạng thái
# quiescent trong ngân sách của RIÊNG nó; con số đi vào `benchmarks.md` và đi vào DoD là
# `DRAIN_SECONDS`, đo từ lúc bấm giờ. Không assert nó thì đúng 180 giây vẫn in ra "DAT".
[ "$DRAIN_SECONDS" -lt "$DRAIN_BUDGET" ] \
  || fail "tieu backlog het ${DRAIN_SECONDS}s; DoD la STRICT < ${DRAIN_BUDGET}s, dung ${DRAIN_BUDGET} cung la truot"
[ "${FINAL_DEPTH:-1}" = "0" ] || fail "depth cuoi cung = ${FINAL_DEPTH:-?}"
[ "$LEFT" -gt 0 ] || fail "ve trai bang 0; simulator khong sinh ra phep do nao"
# Khong tru khoi ve trai: nhung phep do do CO THAT, kenh da doc xong va deadband da di qua, nen chung
# nam lai o $LEFT va lam $DRIFT do len. Gate rieng nay chi noi mat o phia nao cua day — truoc broker
# thi no > 0, sau broker thi no bang 0 va lech van con.
[ "$ABANDONED" -eq 0 ] \
  || fail "$ABANDONED phep do khong ra khoi simulator (session chet giua batch)"
[ "$GW_REJECTED" -eq 0 ] || fail "gateway reject $GW_REJECTED message"
[ "$GW_DECODED" -eq "$GW_FORWARDED" ] \
  || fail "gateway decode $GW_DECODED != forward $GW_FORWARDED; con hang trong buffer"
# Bang nhau, khong phai "khong am". Ve phai lon hon nghia la co row khong giai thich duoc, va do
# la mot loi khac chu khong phai mot khoan du an toan.
[ "$DRIFT" -eq 0 ] || fail "lech $DRIFT row ($RIGHT vs $LEFT)"

say "  Chep dung nhung so nay vao docs/benchmarks.md. Khong lam tron."
say "  Bat lai simulator: make sim-up"

if [ "$FAILED" -ne 0 ]; then
  say ""
  say "  D3 TRUOT. Ghi dung nhung con so tren, dung sua chung (AGENTS.md §1.3)."
  exit 1
fi

say ""
say "  D3 DAT."
