#!/usr/bin/env sh
#
# reconcile.sh — D1, phep kiem quan trong nhat cua M2.
#
# Ve trai : so phep do LOGIC simulator ghi ra (`logicalMeasurements` trong run report).
# Ve phai : so row trong ts.telemetry_measurement cua chinh duong day do.
# Hai so phai BANG NHAU. Khong "xap xi", khong "chenh duoi 0,1%".
#
# Do bang DELTA chu khong bang tong tuyet doi: DB co the da co du lieu tu lan chay truoc, va
# `make down-v` de lam sach la thu AGENTS.md §1.2 cam.
#
# Bon so phu in kem KHONG phai trang tri. Neu tong khop ma ca bon deu 0 thi rat co the fault
# chua duoc bat, va phep kiem chua kiem gi (R-M2-1).

set -eu

MSYS_NO_PATHCONV=1
export MSYS_NO_PATHCONV

DURATION=${DURATION:-3600}
DRAIN_BUDGET=${DRAIN_BUDGET:-180}
LINE_PREFIX=${LINE_PREFIX:-NOVAVOLT/NV1/FORMATION/F1/}
COMPOSE="docker compose"

. "$(dirname "$0")/gateway-snapshot.sh"
. "$(dirname "$0")/reset-ingestion-fixture.sh"

NVM_POSTGRES_USER=$(grep -E '^NVM_POSTGRES_USER=' .env | cut -d= -f2-)
NVM_POSTGRES_DB=$(grep -E '^NVM_POSTGRES_DB=' .env | cut -d= -f2-)
export NVM_POSTGRES_USER NVM_POSTGRES_DB

PROJECT=$(docker inspect -f '{{index .Config.Labels "com.docker.compose.project"}}' nvm-timescale 2>/dev/null)
SIM_VOLUME="${PROJECT}_simdata"

say() { printf '%s\n' "$1"; }

# Doc tu VOLUME, khong tu container. Ve trai duoc chot SAU khi simulator dung — dung luc no vua
# flush not hang dang giu va ghi lai report lan cuoi — nen `docker exec` khong con dung duoc.
report_field() {
  docker run --rm -v "$SIM_VOLUME:/data:ro" alpine:3.21 \
    cat /data/simulator-run.json 2>/dev/null \
    | tr -d ' \r' \
    | sed -n "s/.*\"$1\":\([0-9]*\),*.*/\1/p" \
    | head -1
}

telemetry_rows() {
  docker exec nvm-timescale psql -U "$NVM_POSTGRES_USER" -d "$NVM_POSTGRES_DB" -tAc \
    "SELECT count(*) FROM ts.telemetry_measurement WHERE equipment_id LIKE '${LINE_PREFIX}%';" \
    2>/dev/null | tr -d ' \r'
}

drifted_rows() {
  docker exec nvm-timescale psql -U "$NVM_POSTGRES_USER" -d "$NVM_POSTGRES_DB" -tAc \
    "SELECT count(*) FROM ts.telemetry_measurement
     WHERE equipment_id LIKE '${LINE_PREFIX}%' AND clock_quality <> 'Good';" \
    2>/dev/null | tr -d ' \r'
}

# Dong ho cua DB, khong cua may chay script: moc nay cat cua so lag ben duoi, nen no phai cung
# dong ho voi chinh cot `recorded_at` ma no so sanh.
db_now() {
  docker exec nvm-timescale psql -U "$NVM_POSTGRES_USER" -d "$NVM_POSTGRES_DB" -tAc \
    "SELECT now();" 2>/dev/null | tr -d '\r' | sed 's/^ *//;s/ *$//'
}

# Lag cua CHINH lan chay nay, tra ve `count|p50|p95|max` tren row commit sau moc dau.
#
# KHONG doc `lagP95Seconds` cua /stats. Con so do la percentile tren mot ring 8.192 mau
# (`IngestionLag.WindowSize`), va do la lua chon dung cho D2: ba trieu message lam ring day bang
# du lieu cua chinh run do. D1 thi nguoc lai — mot gio o khoang mot phep do moi giay chi sinh vai
# nghin mau, KHONG du lap day ring, nen con so in ra van la cua lan chay truoc. Do la cach mot
# lan doi chieu 120 giay muon duoc p95 1.611 giay tu lab backpressure ngay truoc no.
#
# `percentile_disc` chu khong phai `percentile_cont`, dung ly do `IngestionLag` chon nearest-rank:
# noi suy giua hai mau sinh ra mot do tre khong reading nao tung co.
#
# Chi row `Good`, cung luat cua `IngestionLag`: lag la hieu HAI dong ho, nen mot thiet bi lech hai
# gio dong gop hai gio "do tre" khong noi gi ve duong ong — va D1 co y bat fault lech dong ho.
run_lag() {
  docker exec nvm-timescale psql -U "$NVM_POSTGRES_USER" -d "$NVM_POSTGRES_DB" -tAc \
    "SELECT count(*),
            coalesce(percentile_disc(0.50) WITHIN GROUP (ORDER BY lag), 0),
            coalesce(percentile_disc(0.95) WITHIN GROUP (ORDER BY lag), 0),
            coalesce(max(lag), 0)
     FROM (SELECT extract(epoch FROM recorded_at - device_timestamp) AS lag
           FROM ts.telemetry_measurement
           WHERE equipment_id LIKE '${LINE_PREFIX}%'
             AND clock_quality = 'Good'
             AND recorded_at >= '$1') s;" \
    2>/dev/null | tr -d ' \r'
}

# dmz-shell chay entrypoint /bin/sh, nen lenh phai vao bang -c. Ingestion nam o dmz-net con may
# nay thi khong — do la ranh gioi dang lam viec: duong vao duy nhat la tu ben trong.
ingestion_stat() {
  $COMPOSE --profile tools run --rm -T dmz-shell \
    -c 'wget -qO- http://ingestion:8080/api/ingestion/v1/stats' 2>/dev/null \
    | tr ',' '\n' \
    | sed -n "s/.*\"$1\":\([0-9.-]*\).*/\1/p" \
    | head -1
}

# Xa het, hoac dung han. Ban cu ket thuc bang `return 0`, nen mot lan xa qua han van di tiep va D1
# doc ve phai trong luc duong ong con hang — dung loi ma F2 goi la false-pass.
drain() {
  gateway_drain "$DRAIN_BUDGET" && return 0

  say "  Duong ong khong rong sau ${DRAIN_BUDGET}s (buffer_depth=$(gateway_stat buffer_depth))." >&2
  return 1
}

for container in nvm-edge-gateway nvm-ingestion nvm-timescale; do
  if [ "$(docker inspect -f '{{.State.Running}}' "$container" 2>/dev/null || echo false)" != "true" ]; then
    say "Thieu $container. Chay: make up && make edge-up && make ingestion-up" >&2
    exit 2
  fi
done

# Simulator chi can TON TAI, khong can dang chay: lab nay so huu vong doi cua no, va lan chay
# truoc ket thuc bang cach dung no. Doi phai dang chay se bat nguoi ta bat len chi de bi dung lai.
if ! docker inspect nvm-simulator >/dev/null 2>&1; then
  say "Thieu nvm-simulator. Chay: make sim-up" >&2
  exit 2
fi

gateway_wait_snapshot 120 || {
  say "Gateway khong ghi $NVM_GATEWAY_STATS. Build lai gateway: make edge-up" >&2
  exit 2
}

say "D1 — doi chieu $DURATION giay dong ho THAT (nen thoi gian khong dung o day)"
say ""

# Xa het TRUOC khi chot moc dau. Neu khong, phan dang nam trong buffer luc bat dau se roi vao ve
# PHAI cua phep tru va D1 bao thua du khong co gi thua.
# Lab nay SO HUU vong doi cua simulator, va do la ly do no doc duoc mot con so sach.
#
# Doc ve trai tu mot run dang chay thi luon lech: report duoc ghi lai moi vai giay, nen moc dau
# la mot anh chup CU hon vai giay so voi du lieu da nam trong duong ong. Phan o giua chi roi vao
# ve PHAI. Do duoc 16 row lech truoc khi co buoc dung o cuoi, va 6 row lech con lai chinh la khe
# ho nay — dung bang mot DBIRTH.
#
# Nen: dung han, xa het, chot ve phai; roi khoi dong lai de report ve 0. Sau do ca hai ve deu la
# con so cuoi cung cua DUNG mot run.
say "  Dung simulator, xa het duong ong, roi chot moc dau..."
$COMPOSE --profile sim stop simulator >/dev/null 2>&1
drain || {
  say "  D1 HUY: duong ong chua rong, nen moc dau se dem ca hang cu vao ve PHAI." >&2
  exit 1
}
sleep 10

reset_ingestion_fixture || exit 1

ROWS_START=$(telemetry_rows)
DUPLICATES_START=$(ingestion_stat duplicates)
DRIFTED_START=$(drifted_rows)
REBIRTH_START=$(gateway_stat rebirth_requests)
PUBLISH_FAIL_START=$(ingestion_stat publishFailures)
STARTED_AT=$(db_now)

say "  Moc dau"
say "    telemetry rows      : $ROWS_START"
say ""
say "  Khoi dong lai simulator o TimeCompression=1 — run report ve 0, ve trai la ca run nay."

# force-recreate chu khong phai start: nen thoi gian doc tu bien moi truong luc tao
# container. D1 phai la mot gio dong ho THAT, nen phep do khong duoc phep thua ke mot
# simulator dang chay nhanh gap 60 lan tu buoi lam viec truoc.
NVM_SIM_COMPRESSION=1 $COMPOSE --profile sim up -d --force-recreate simulator >/dev/null 2>&1

say "  Dang chay $DURATION giay. Ctrl-C se HUY phep do, khong phai dung som."
sleep "$DURATION"

say ""
say "  Dung simulator de CHOT ve trai, roi cho duong ong xa het (ngan sach ${DRAIN_BUDGET}s)..."

# Dung han, khong phai doc nhanh. Doc hai ve o hai thoi diem khac nhau tren mot day chuyen dang
# chay thi phan sinh ra o giua chi roi vao MOT ve — do dung la 16 row lech do duoc truoc khi co
# buoc nay, va no trong y het mat du lieu. Simulator flush not hang dang giu roi ghi lai report
# lan cuoi khi dung, nen ve trai la con so CUOI CUNG chu khong phai anh chup giua chung.
$COMPOSE --profile sim stop simulator >/dev/null 2>&1

MEASUREMENTS_END=$(report_field logicalMeasurements)

# Phep do da sinh ra ma link khong cho di: session chet giua batch, hoac publish loi. Doc o day chu
# khong suy ra tu hieu so, vi hieu so khong noi duoc mat o TRUOC hay SAU broker.
#
# Rong nghia la report duoc ghi boi mot simulator cu hon truong nay — khi do gate duoi im lang va
# D1 se xanh ma khong kiem gi. Bat loi to.
ABANDONED_END=$(report_field abandonedMeasurements)

case "${ABANDONED_END:-}" in
  ''|*[!0-9]*)
    say "  D1 TRUOT: run report khong co 'abandonedMeasurements' ('${ABANDONED_END:-}')." >&2
    say "  Report nay do mot simulator cu hon oracle hien tai ghi ra; chay lai voi image moi." >&2
    exit 1
    ;;
esac

drain || {
  say "  D1 TRUOT: duong ong con hang sau ${DRAIN_BUDGET}s, nen ve phai chua day du." >&2
  exit 1
}
sleep 10

ROWS_END=$(telemetry_rows)
DUPLICATES_END=$(ingestion_stat duplicates)
DRIFTED_END=$(drifted_rows)
REBIRTH_END=$(gateway_stat rebirth_requests)
PUBLISH_FAIL_END=$(ingestion_stat publishFailures)
LAG=$(run_lag "$STARTED_AT")
LAG_SAMPLES=$(printf '%s' "$LAG" | cut -d'|' -f1)
LAG_P50=$(printf '%s' "$LAG" | cut -d'|' -f2)
LAG_P95=$(printf '%s' "$LAG" | cut -d'|' -f3)
LAG_MAX=$(printf '%s' "$LAG" | cut -d'|' -f4)

# Khong tru: report da ve 0 khi simulator khoi dong lai, nen day la ca run.
LEFT=$MEASUREMENTS_END
RIGHT=$(( ROWS_END - ROWS_START ))
DRIFT=$(( RIGHT - LEFT ))
DUPLICATES=$(( ${DUPLICATES_END:-0} - ${DUPLICATES_START:-0} ))
DRIFTED=$(( ${DRIFTED_END:-0} - ${DRIFTED_START:-0} ))
REBIRTHS=$(( ${REBIRTH_END:-0} - ${REBIRTH_START:-0} ))
PUBLISH_FAILURES=$(( ${PUBLISH_FAIL_END:-0} - ${PUBLISH_FAIL_START:-0} ))

say ""
say "  Ket qua D1"
say "  ------------------------------------------------------"
say "  ★ Phep do logic simulator sinh ra   : $LEFT"
say "  ★ Row telemetry trong DB            : $RIGHT"
say "  ★ Lech (phai la 0)                  : $DRIFT"
say "  ★ Phep do link khong cho di (phai 0): $ABANDONED_END"
say ""
say "  Boi canh — bon so nay bang 0 het nghia la fault chua bat:"
say "    Duplicate bi dedup chan           : $DUPLICATES"
say "    Row clock_quality <> Good         : $DRIFTED"
say "    Rebirth request                   : $REBIRTHS"
say "    Publish that bai (ADR-022)        : $PUBLISH_FAILURES"
say "    p95 lag CUA RUN (giay)            : ${LAG_P95:-?}"
say "      p50 ${LAG_P50:-?} - max ${LAG_MAX:-?} - ${LAG_SAMPLES:-?} mau Good"
say ""
say "  Chep dung nhung so nay vao docs/benchmarks.md. KHONG lam tron lech ve 0."
say "  Bat lai simulator: make sim-up"

# Nen thoi gian lam dong ho THIET BI chay nhanh hon dong ho that, nen device_timestamp vuot len
# truoc gateway_timestamp: moi row thanh Drifted va lag thanh AM. So dem row van dung, hai cot boi
# canh kia thi vo nghia. D1 that phai chay o TimeCompression=1.
if [ "$LEFT" -gt 0 ] && [ "${DRIFTED:-0}" -gt "$(( LEFT / 2 ))" ]; then
  say ""
  say "  CANH BAO: hon mot nua so row la Drifted."
  say "  Gan nhu chac chan simulator dang chay NEN THOI GIAN. So dem row van dung, nhung"
  say "  clock_quality va lag KHONG dung. Dat NVM_SIM__TimeCompression=1 roi chay lai."
fi

# Khong tru khoi ve trai, va do la ca y nghia cua con so nay. Nhung phep do do CO THAT — kenh da doc
# xong, deadband da di qua — nen chung nam lai o ve trai va lam DRIFT do len. Gate rieng nay chi noi
# them mat o phia nao cua day: truoc broker thi ABANDONED > 0, sau broker thi no bang 0 va lech van con.
if [ "$ABANDONED_END" -ne 0 ]; then
  say ""
  say "  D1 TRUOT: $ABANDONED_END phep do khong ra khoi simulator."
  say "  Session chet giua batch, hoac publish loi — mat o chang thiet bi -> broker."
  say "  Dung sua ve trai cho khop; ghi dung con so nay (AGENTS.md §1.3)."
  exit 1
fi

if [ "$DRIFT" -ne 0 ]; then
  say ""
  say "  D1 TRUOT: lech $DRIFT. Ghi dung con so nay, dung sua no (AGENTS.md §1.3)."
  exit 1
fi

if [ "$DUPLICATES" -eq 0 ]; then
  say ""
  say "  CANH BAO: tong khop nhung dedup chan 0 duplicate. D1 chua kiem gi (R-M2-1)."
  exit 1
fi

say ""
say "  D1 DAT."
