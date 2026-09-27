#!/usr/bin/env sh
#
# soak.sh — hard capacity / soak 24 giờ của M13 (scope §9/M13, ADR-034), đủ sáu vế theo thứ tự:
#   1. cold tail có sẵn trước T0 (raw chunk range_end < T0 - 7 ngày) + baseline nén và job nén
#   2. chạy liên tục DURATION giây (mặc định 86400) ở topology 1.000 kênh (seed-load)
#   3. không mất: load-gate.sh đối chiếu exact source = gateway = fsync = forward = row delta
#   4. thông lượng giờ cuối không thấp hơn giờ đầu quá 10 %
#   5. oracle cold/hot: mỗi hypertable (raw, parent, child) có cold chunk nén và hot chunk chưa nén, số chunk nén
#      tăng, job nén có successes tăng và failures không tăng; truy vấn child trên ba cửa sổ (cold, hot, cả hai) có dữ
#      liệu, in thời gian và EXPLAIN
#   6. dung lượng DB trước/sau và tỉ số nén của raw
#
# KHÔNG xoá dữ liệu: chạy load-gate với NVM_KEEP_FIXTURE=1, mọi phép so đều theo delta.
# Dry run cho máy móc: DURATION=600 make soak — vế 4–5 in số nhưng chỉ chấm đạt/trượt khi DURATION >= 86400.
set -eu
MSYS_NO_PATHCONV=1
export MSYS_NO_PATHCONV
DURATION=${DURATION:-86400}
RATE=${RATE:-1000}
OUT=${SOAK_OUT:-soak-$(date +%Y%m%d-%H%M%S)}
mkdir -p "$OUT"
FULL=0
[ "$DURATION" -ge 86400 ] && FULL=1
FAIL=0
say() { printf '%s\n' "$1" | tee -a "$OUT/summary.txt"; }
fail() { say "TRUOT: $1"; FAIL=1; }
sql() {
  docker exec nvm-timescale sh -c "psql -U \"\$POSTGRES_USER\" -d \"\$POSTGRES_DB\" -tA -F'|' -c \"$1\"" 2>/dev/null | tr -d '\r'
}

HYPERTABLES="telemetry_measurement _materialized_hypertable_4 _materialized_hypertable_8"
chunks() {  # $1 hypertable: "<nén>|<chưa nén>"
  sql "SELECT count(*) FILTER (WHERE is_compressed), count(*) FILTER (WHERE NOT is_compressed)
       FROM timescaledb_information.chunks WHERE hypertable_name = '$1';"
}
jobs() {  # "<successes>|<failures>" gộp ba job nén
  sql "SELECT coalesce(sum(s.total_successes),0), coalesce(sum(s.total_failures),0)
       FROM timescaledb_information.jobs j JOIN timescaledb_information.job_stats s USING (job_id)
       WHERE j.proc_name = 'policy_compression';"
}
snapshot() {  # $1 nhãn
  for h in $HYPERTABLES; do say "  $1 chunks $h (nen|chua nen): $(chunks "$h")"; done
  say "  $1 compression jobs (successes|failures): $(jobs)"
  say "  $1 database bytes: $(sql "SELECT pg_database_size(current_database());")"
  say "  $1 raw compression (truoc|sau bytes): $(sql "SELECT before_compression_total_bytes, after_compression_total_bytes FROM hypertable_compression_stats('ts.telemetry_measurement');")"
}

T0=$(sql "SELECT now();")
say "== SOAK $(date -u +%FT%TZ) DURATION=${DURATION}s RATE=${RATE} msg/s topology=seed-load T0=$T0 (full=$FULL)"

say "== 1. cold tail va baseline"
COLD=$(sql "SELECT count(*) FROM timescaledb_information.chunks
            WHERE hypertable_name = 'telemetry_measurement' AND range_end < now() - interval '7 days';")
say "  raw chunk co range_end < T0 - 7 ngay: $COLD"
[ "${COLD:-0}" -ge 1 ] || fail "khong co cold tail; sinh bang make telemetry-backfill truoc (scope §9/M13 ve 1)"
snapshot before
BEFORE_COMPRESSED=$(for h in $HYPERTABLES; do chunks "$h" | cut -d'|' -f1; done | tr '\n' ' ')
BEFORE_JOBS=$(jobs)

say "== 2-3. chay lien tuc va doi chieu exact (load-gate, khong xoa fixture)"
GATE=0
NVM_LOAD_SOAK=1 NVM_KEEP_FIXTURE=1 NVM_LOAD_KEEP_SAMPLES="$OUT/samples.txt" NVM_SEED_DIR=seed-load RATE="$RATE" DURATION="$DURATION" \
  sh scripts/load-gate.sh >"$OUT/load-gate.log" 2>&1 || GATE=$?
tail -40 "$OUT/load-gate.log" >>"$OUT/summary.txt"
[ "$GATE" -eq 0 ] || fail "load-gate exit=$GATE (doi chieu exact hoac drain), xem $OUT/load-gate.log"
# Tra gateway ve topology demo 8 kenh nhu truoc khi soak.
docker compose up -d --force-recreate edge-gateway >/dev/null 2>&1 || true

say "== 4. thong luong gio dau so voi gio cuoi"
WINDOW=3600
[ "$DURATION" -lt 14400 ] && WINDOW=$((DURATION / 4))
RATES=$(awk -v w="$WINDOW" '
  {
    stamp = $1; ins = $0; dup = $0
    sub(/.*"inserted":/, "", ins); sub(/[,}].*/, "", ins)
    sub(/.*"duplicates":/, "", dup); sub(/[,}].*/, "", dup)
    if (stamp !~ /^[0-9]+$/ || ins !~ /^[0-9]+$/ || dup !~ /^[0-9]+$/) next
    n++; t[n] = stamp; a[n] = ins + dup
  }
  END {
    if (n < 2) exit
    for (i = 1; i <= n && t[i] < t[1] + w; i++) last_first = i
    for (i = n; i >= 1 && t[i] > t[n] - w; i--) first_last = i
    printf "%.1f %.1f", (a[last_first] - a[1]) / (t[last_first] - t[1]), (a[n] - a[first_last]) / (t[n] - t[first_last])
  }' "$OUT/samples.txt" 2>/dev/null || true)
FIRST=$(echo "$RATES" | cut -d' ' -f1)
LAST=$(echo "$RATES" | cut -d' ' -f2)
say "  cua so ${WINDOW}s: dau ${FIRST:-?} msg/s, cuoi ${LAST:-?} msg/s"
if [ -n "${FIRST:-}" ] && [ -n "${LAST:-}" ]; then
  if awk -v f="$FIRST" -v l="$LAST" 'BEGIN { exit !(l < 0.9 * f) }'; then
    [ "$FULL" -eq 1 ] && fail "thong luong cuoi thap hon dau qua 10 %" || say "  (dry run) cuoi thap hon dau qua 10 %"
  fi
else
  fail "khong tinh duoc thong luong tu mau"
fi

say "== 5. oracle cold/hot"
snapshot after
i=1
for h in $HYPERTABLES; do
  now=$(chunks "$h"); was=$(echo "$BEFORE_COMPRESSED" | cut -d' ' -f$i); i=$((i + 1))
  compressed=$(echo "$now" | cut -d'|' -f1); hot=$(echo "$now" | cut -d'|' -f2)
  [ "${compressed:-0}" -ge 1 ] || fail "$h khong co cold chunk nen"
  if [ "${hot:-0}" -lt 1 ]; then
    [ "$FULL" -eq 1 ] && fail "$h khong co hot chunk chua nen" || say "  (dry run) $h chua co hot chunk (aggregate chi co sau lan refresh)"
  fi
  if [ "${compressed:-0}" -le "${was:-0}" ]; then
    [ "$FULL" -eq 1 ] && fail "$h so chunk nen khong tang ($was -> $compressed)" || say "  (dry run) $h chunk nen $was -> $compressed"
  fi
done
AFTER_JOBS=$(jobs)
if [ "$(echo "$AFTER_JOBS" | cut -d'|' -f2)" -gt "$(echo "$BEFORE_JOBS" | cut -d'|' -f2)" ]; then fail "job nen co failure moi"; fi
if [ "$(echo "$AFTER_JOBS" | cut -d'|' -f1)" -le "$(echo "$BEFORE_JOBS" | cut -d'|' -f1)" ]; then
  [ "$FULL" -eq 1 ] && fail "job nen khong co success moi" || say "  (dry run) job nen chua chay them"
fi
# Cửa sổ cold lấy từ chunk nén có thật của child (dữ liệu cũ có thể nằm bất kỳ đâu trước T0), hot từ T0.
COLD_RANGE=$(sql "SELECT range_start, range_end FROM timescaledb_information.chunks
                  WHERE hypertable_name = '_materialized_hypertable_8' AND is_compressed ORDER BY range_end DESC LIMIT 1;")
COLD_FROM=$(echo "$COLD_RANGE" | cut -d'|' -f1); COLD_TO=$(echo "$COLD_RANGE" | cut -d'|' -f2)
say "  cold window tu chunk nen moi nhat cua child: [$COLD_FROM, $COLD_TO)"
for window in "cold|'$COLD_FROM'::timestamptz|'$COLD_TO'::timestamptz" "hot|'$T0'::timestamptz|now()" \
              "both|'$COLD_FROM'::timestamptz|now()"; do
  name=$(echo "$window" | cut -d'|' -f1); from=$(echo "$window" | cut -d'|' -f2); to=$(echo "$window" | cut -d'|' -f3)
  rows=$(sql "SELECT count(*) FROM ts.process_signal_machine_1m WHERE bucket >= $from AND bucket < $to;")
  times=""
  for _ in 1 2 3 4 5; do
    ms=$(sql "EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON) SELECT count(*) FROM ts.process_signal_machine_1m
              WHERE bucket >= $from AND bucket < $to;" | tr -d '\n' | sed -n 's/.*"Execution Time": \([0-9.]*\).*/\1/p')
    times="$times $ms"
  done
  worst=$(echo "$times" | tr ' ' '\n' | sort -n | tail -1)
  say "  cua so $name: rows=$rows thoi gian 5 lan (ms):$times; lan cham nhat ${worst} ms"
  if [ "${rows:-0}" -eq 0 ]; then
    { [ "$FULL" -eq 1 ] || [ "$name" != "hot" ]; } && fail "cua so $name doc child rong" || say "  (dry run) cua so hot chua co du lieu aggregate"
  fi
done
sql "EXPLAIN (ANALYZE, BUFFERS) SELECT count(*) FROM ts.process_signal_machine_1m
     WHERE bucket >= now() - interval '8 days' AND bucket < now();" >"$OUT/explain-both.txt"

say "== 6. dung luong va ti so nen: xem dong 'before/after' o tren"
if [ "$FAIL" -eq 0 ]; then say "SOAK DAT (full=$FULL)"; else say "SOAK TRUOT"; fi
exit "$FAIL"
