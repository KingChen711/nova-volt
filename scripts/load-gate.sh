#!/usr/bin/env sh
#
# D2 / R3 — fail-closed end-to-end load gate.
#
# A publisher-side rate is not ingestion throughput. This script owns the test source, captures
# exact gateway counters from its atomic local snapshot, waits for the durable queue to quiesce, and
# compares those values with the scoped PostgreSQL row delta. Any missing evidence is a failure.
#
# TWO QUESTIONS, TWO MILESTONES (ADR-031). By default this grades M2's: did everything that went in
# come out exactly, and did the receiver keep up with whatever the source managed? With
# NVM_LOAD_ENFORCE_N1=1 it also grades the capacity question: 5.000 msg/s at p95 under 5 s. That one is
# accepted at M9 and re-measured at M13, on a rig that is not itself the constraint. The numbers are
# printed either way.

set -eu

MSYS_NO_PATHCONV=1
export MSYS_NO_PATHCONV

COMPOSE="docker compose"

. "$(dirname "$0")/gateway-snapshot.sh"
. "$(dirname "$0")/reset-ingestion-fixture.sh"
# OFFERED rate. Not the pass mark - see N1_MINIMUM. A source that offers exactly the threshold
# can only ever meet it by being perfect, because every millisecond it loses to a stall is a
# millisecond it can never make up. Offering a little more is how a measurement gets to be about the
# pipeline instead of about the instrument.
RATE=${RATE:-5100}

# N1, from scope.md. NOT a knob: no environment variable moves it, and nothing here compares an
# achieved rate against anything else.
N1_MINIMUM=5000

# Which milestone this run is grading (ADR-031).
#
# Unset - the default - is M2's question: does everything that went in come out, exactly, and does
# the receiver keep up with whatever the source managed? Both are about the pipeline and this rig
# can answer both.
#
# NVM_LOAD_ENFORCE_N1=1 is the capacity question: does it reach 5.000 msg/s at p95 under 5 s? It is
# accepted at M9 - not later, because N9 measures degradation against N1 itself - and re-measured at
# M13. It needs a rig that DELIVERS at least 2x N1 at QoS 1 to a no-op subscriber. Measured at the
# publish side instead, this rig reads 9.925 and looks close; measured where the gateway actually
# lives it reads 5.951 and fails that preflight.
#
# Either way the N1 numbers are measured and printed. Only whether they FAIL the run changes.
ENFORCE_N1=${NVM_LOAD_ENFORCE_N1:-0}

DURATION=${DURATION:-600}
DRAIN_BUDGET=${DRAIN_BUDGET:-180}
SIMULATOR=nvm-simulator
POLLER=nvm-load-stats-poller
STATS_PID=

# Five, not ten. Two gates below read progress WITHIN the run window, and the sampling gap is dead
# ground for both of them.
POLL_SECONDS=5

# Ignored at the head of the run when measuring the receiver's sustained rate. Containers start,
# connections open, the first batches are small - none of which says anything about whether
# ingestion can hold N1, and all of which drags an interior average down.
RAMP_UP_SECONDS=30
# IngestionLag.WindowSize. Percentiles come from a ring of the most recent readings, so a sample
# taken before the ring has refilled still describes the PREVIOUS run.
LAG_WINDOW=8192
RESULT_FILE=$(mktemp)
SAMPLE_FILE=$(mktemp)
RESOURCE_FILE=$(mktemp)

NVM_EMQX_USER=$(grep -E '^NVM_EMQX_USER=' .env | cut -d= -f2-)
NVM_EMQX_PASSWORD=$(grep -E '^NVM_EMQX_PASSWORD=' .env | cut -d= -f2-)

cleanup() {
	status=$?
	trap - EXIT
	rm -f "$RESULT_FILE" "$SAMPLE_FILE" "$RESOURCE_FILE"
	docker rm -f "$POLLER" >/dev/null 2>&1 || true
	[ -n "$STATS_PID" ] && kill "$STATS_PID" 2>/dev/null || true

	if [ "${simulator_was_running:-false}" = "true" ]; then
		$COMPOSE --profile sim up -d --no-build simulator >/dev/null 2>&1 || true
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

say() { printf '%s\n' "$1"; }

fail() {
	say "FAIL: $1" >&2
	FAILED=1
}

ingestion_stats() {
	$COMPOSE --profile tools run --rm -T dmz-shell \
		-c 'wget -qO- http://ingestion:8080/api/ingestion/v1/stats' 2>/dev/null
}

json_value() {
	json=$1
	key=$2
	printf '%s' "$json" \
		| tr -d ' \r\n' \
		| sed -n "s/.*\"${key}\":\([^,}]*\).*/\1/p"
}

result_value() {
	key=$1
	awk -v key="$key" '
		$1 == "NVM_LOAD_RESULT" {
			for (field = 2; field <= NF; field++) {
				prefix = key "="
				if (index($field, prefix) == 1) {
					print substr($field, length(prefix) + 1)
				}
			}
		}' "$RESULT_FILE" | tail -1
}

require_unsigned_integer() {
	name=$1
	value=$2
	case "$value" in
		''|*[!0-9]*)
			say "Evidence '$name' khong phai so nguyen khong am: '$value'." >&2
			exit 1
			;;
	esac
}

require_number() {
	name=$1
	value=$2
	if ! awk -v value="$value" 'BEGIN {
		exit !(value ~ /^-?[0-9]+([.][0-9]+)?([eE][+-]?[0-9]+)?$/)
	}'; then
		say "Evidence '$name' khong phai so huu han: '$value'." >&2
		exit 1
	fi
}

# EMQX counts what it threw away per client session. The gateway subscribes at QoS 1, so a message
# the broker drops because the subscriber's queue is full is a message that reached the broker and
# never reached us - loss that no counter on either end of the pipeline can see, and the exact shape
# that made D2 report 936 msg/s while the harness insisted it had sent five thousand.
emqx_token() {
	docker exec nvm-emqx curl -sS -X POST http://localhost:18083/api/v5/login \
		-H 'Content-Type: application/json' \
		-d "{\"username\":\"$NVM_EMQX_USER\",\"password\":\"$NVM_EMQX_PASSWORD\"}" 2>/dev/null \
		| sed -n 's/.*"token":"\([^"]*\)".*/\1/p'
}

# Captures the raw value token, not just the digits inside it. `[0-9]*` silently turned
# `"send_msg.dropped":null` into an empty match and `:12.5` into 12 - a parse error dressed up as a
# counter. Whatever comes back is handed to require_unsigned_integer, which is the only thing
# allowed to decide whether it is evidence.
emqx_dropped() {
	docker exec nvm-emqx curl -sS -H "Authorization: Bearer $1" \
		http://localhost:18083/api/v5/clients/nvm-edge-gateway 2>/dev/null \
		| tr ',' '\n' \
		| sed -n 's/.*"send_msg\.dropped":\([^,}]*\).*/\1/p' \
		| head -1
}

telemetry_rows() {
	docker exec nvm-timescale sh -c \
		'psql -U "$POSTGRES_USER" -d "$POSTGRES_DB" -tAc "
            SELECT count(*)
            FROM ts.telemetry_measurement
            WHERE site_id = '\''NV1'\''
              AND equipment_id LIKE '\''NOVAVOLT/NV1/FORMATION/F1/%'\''
              AND signal_code = '\''Formation/Voltage'\'';"' \
		2>/dev/null | tr -d ' \r'
}

# Quiescent means the durable queue is empty AND no new MQTT publish was decoded for three samples.
# Depth alone can briefly be zero between batches while the broker still holds deliveries.
wait_for_quiescence() {
	started=$(date +%s)
	previous=-1
	stable=0

	while [ "$(( $(date +%s) - started ))" -le "$DRAIN_BUDGET" ]; do
		snapshot=$(gateway_snapshot || true)
		depth=$(snapshot_field "$snapshot" buffer_depth)
		decoded=$(snapshot_field "$snapshot" decoded)

		if [ -n "$depth" ] && [ -n "$decoded" ] && [ "$depth" = "0" ] && [ "$decoded" = "$previous" ]; then
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

if [ "$RATE" -lt "$N1_MINIMUM" ]; then
	say "RATE offered ($RATE) < N1 ($N1_MINIMUM msg/s). Ban khong the chung minh mot nguong bang" >&2
	say "cach ban duoi no; D2 khong duoc ha de lam gate xanh." >&2
	exit 2
fi

if [ "$DURATION" -lt 600 ]; then
	say "DURATION phai >= 600 giay; D2 khong duoc rut ngan." >&2
	exit 2
fi

if [ "$DRAIN_BUDGET" -gt 180 ]; then
	say "DRAIN_BUDGET khong duoc vuot 180 giay." >&2
	exit 2
fi

for container in nvm-emqx nvm-edge-gateway nvm-ingestion nvm-timescale; do
	if [ "$(docker inspect -f '{{.State.Running}}' "$container" 2>/dev/null || true)" != "true" ]; then
		say "$container chua chay; khoi dong stack M2 truoc khi do D2." >&2
		exit 2
	fi
done

if [ -n "$(docker ps -q --filter label=com.docker.compose.service=load-harness)" ]; then
	say "Da co load-harness khac dang chay; D2 khong the co lap source." >&2
	exit 2
fi

# The poller has to reach ingestion by service DNS the same way the gateway does, so it joins the
# network the gateway is actually on rather than a name this script guesses from the project prefix.
DMZ_NET=$(docker inspect nvm-edge-gateway \
	--format '{{range $name, $config := .NetworkSettings.Networks}}{{$name}}{{end}}')

if [ -z "$DMZ_NET" ]; then
	say "Khong xac dinh duoc dmz-net tu nvm-edge-gateway." >&2
	exit 2
fi

simulator_was_running=$(docker inspect -f '{{.State.Running}}' "$SIMULATOR" 2>/dev/null || true)

say "D2 — end-to-end load gate"
say "  Che do        : $([ "$ENFORCE_N1" = "1" ] && echo 'M9/M13 — ep N1/N2 thanh gate cung' || echo 'M2 — ep tinh dung dan; N1/N2 chi bao cao (ADR-031)')"
say "  Seed topology : ${NVM_SEED_DIR:-seed} (gateway va harness phai cung gia tri - K3)"
say "  Offered rate  : $RATE msg/s"
say "  Nguong N1     : $N1_MINIMUM msg/s (co dinh)"
say "  Duration      : $DURATION s"
say "  Drain budget  : $DRAIN_BUDGET s"
say ""

say "== build gateway diagnostics va load harness"
$COMPOSE build edge-gateway >/dev/null
$COMPOSE --profile load build load-harness >/dev/null

reset_ingestion_fixture || exit 1

say "== co lap EDGE-F1, recreate gateway, doi durable queue rong"
$COMPOSE --profile sim stop simulator >/dev/null
docker exec nvm-edge-gateway sh -c "rm -f '$NVM_GATEWAY_STATS' '$NVM_GATEWAY_STATS.tmp'" 2>/dev/null || true
$COMPOSE up -d --force-recreate edge-gateway >/dev/null

gateway_wait_snapshot 120 || {
	say "Gateway khong tao operational snapshot trong 120 giay." >&2
	exit 1
}

wait_for_quiescence || {
	say "Gateway khong ve trang thai quiescent trong $DRAIN_BUDGET giay truoc baseline." >&2
	exit 1
}

SNAPSHOT_BEFORE=$(gateway_snapshot)
GW_DECODED_BEFORE=$(snapshot_field "$SNAPSHOT_BEFORE" decoded)
GW_BUFFERED_BEFORE=$(snapshot_field "$SNAPSHOT_BEFORE" buffered)
GW_FORWARDED_BEFORE=$(snapshot_field "$SNAPSHOT_BEFORE" forwarded)
GW_REJECTED_BEFORE=$(snapshot_field "$SNAPSHOT_BEFORE" rejected)
GW_REBIRTH_BEFORE=$(snapshot_field "$SNAPSHOT_BEFORE" rebirth_requests)
DB_BEFORE=$(telemetry_rows)
INGEST_BEFORE=$(ingestion_stats)
LAG_SAMPLES_BEFORE=$(json_value "$INGEST_BEFORE" lagSamples)
DUPLICATES_BEFORE=$(json_value "$INGEST_BEFORE" duplicates)

EMQX_TOKEN=$(emqx_token)
[ -n "$EMQX_TOKEN" ] || {
	say "Khong dang nhap duoc EMQX dashboard API; khong the chung minh dropped = 0." >&2
	exit 1
}
# No fallback, at either end. A missing sample is not a quiet zero and not "same as before": the
# broker is the only witness to a message it accepted and then threw away, and if that witness
# cannot be read the run has no evidence for the one loss neither end of the pipeline can see.
# Turning that into a delta of 0 is how a dashboard schema change, a renamed client, or a curl that
# never ran become a PASS. Validate the baseline HERE, before spending ten minutes on a run whose
# verdict is already unprovable.
EMQX_DROPPED_BEFORE=$(emqx_dropped "$EMQX_TOKEN")
require_unsigned_integer emqx_dropped_before "$EMQX_DROPPED_BEFORE"

for pair in \
	"gateway_decoded_before:$GW_DECODED_BEFORE" \
	"gateway_buffered_before:$GW_BUFFERED_BEFORE" \
	"gateway_forwarded_before:$GW_FORWARDED_BEFORE" \
	"gateway_rejected_before:$GW_REJECTED_BEFORE" \
	"gateway_rebirth_before:$GW_REBIRTH_BEFORE" \
	"db_rows_before:$DB_BEFORE" \
	"lag_samples_before:$LAG_SAMPLES_BEFORE" \
	"duplicates_before:$DUPLICATES_BEFORE"; do
	require_unsigned_integer "${pair%%:*}" "${pair#*:}"
done

say "  Baseline: gateway decoded=$GW_DECODED_BEFORE buffered=$GW_BUFFERED_BEFORE forwarded=$GW_FORWARDED_BEFORE"
say "            DB rows=$DB_BEFORE lag samples=$LAG_SAMPLES_BEFORE"
say ""

# D2's p95 must describe the run. Reading /stats only at the end reads a ring holding the last
# 8.192 readings, which after a drain is the quietest stretch of the whole test — a pipeline that
# ran 40 seconds behind for ten minutes would still report a small p95 once it caught up.
say "== bat sampler p95 va tien do receiver (moi ${POLL_SECONDS}s, trong luc chay)"
docker rm -f "$POLLER" >/dev/null 2>&1 || true

# Each line is "<epoch> {json}". The timestamp is what turns a list of counters into a rate, and
# without it the only rate this script could compute is the one the source claims about itself.
docker run -d --name "$POLLER" --network "$DMZ_NET" --entrypoint /bin/sh \
	eclipse-mosquitto:2.0.22 \
	-c "while true; do printf '%s ' \"\$(date +%s)\"; wget -qO- http://ingestion:8080/api/ingestion/v1/stats; echo; sleep $POLL_SECONDS; done" \
	>/dev/null

# CPU and memory of ingestion while it is under load, which plan C16 asks for and nothing had
# collected. Host-side because `docker stats` describes the container from outside it.
( while true; do
	docker stats --no-stream --format '{{.CPUPerc}} {{.MemUsage}}' nvm-ingestion 2>/dev/null
	sleep "$POLL_SECONDS"
done ) >"$RESOURCE_FILE" 2>/dev/null &
STATS_PID=$!

say "== publish load"
RUN_STARTED=$(date +%s)
set +e
NVM_LOAD_RATE="$RATE" NVM_LOAD_DURATION="$DURATION" \
	$COMPOSE --profile load run --rm load-harness >"$RESULT_FILE" 2>&1
HARNESS_STATUS=$?
set -e
RUN_ENDED=$(date +%s)
cat "$RESULT_FILE"

SOURCE_MESSAGES=$(result_value source_messages)
SOURCE_MEASUREMENTS=$(result_value source_measurements)
DECLARED_MESSAGES=$(result_value declared_messages)
SOURCE_COLLISIONS=$(result_value collisions)
SOURCE_FAILED=$(result_value failed)
SOURCE_ELAPSED=$(result_value elapsed_seconds)
SOURCE_RATE=$(result_value achieved_rate)
SOURCE_REBIRTH=$(result_value rebirth_requests)

for pair in \
	"source_messages:$SOURCE_MESSAGES" \
	"source_measurements:$SOURCE_MEASUREMENTS" \
	"declared_messages:$DECLARED_MESSAGES" \
	"collisions:$SOURCE_COLLISIONS" \
	"failed:$SOURCE_FAILED" \
	"rebirth_requests:$SOURCE_REBIRTH"; do
	name=${pair%%:*}
	value=${pair#*:}
	require_unsigned_integer "$name" "$value"
done
require_number elapsed_seconds "$SOURCE_ELAPSED"
require_number achieved_rate "$SOURCE_RATE"

say "== doi broker delivery va durable queue ve quiescent"
DRAINED=1
wait_for_quiescence || DRAINED=0

docker logs "$POLLER" >"$SAMPLE_FILE" 2>/dev/null || true
docker rm -f "$POLLER" >/dev/null 2>&1 || true
[ -n "$STATS_PID" ] && kill "$STATS_PID" 2>/dev/null || true
STATS_PID=

SNAPSHOT_AFTER=$(gateway_snapshot)
GW_DECODED_AFTER=$(snapshot_field "$SNAPSHOT_AFTER" decoded)
GW_BUFFERED_AFTER=$(snapshot_field "$SNAPSHOT_AFTER" buffered)
GW_FORWARDED_AFTER=$(snapshot_field "$SNAPSHOT_AFTER" forwarded)
GW_REJECTED_AFTER=$(snapshot_field "$SNAPSHOT_AFTER" rejected)
GW_REBIRTH_AFTER=$(snapshot_field "$SNAPSHOT_AFTER" rebirth_requests)
BUFFER_DEPTH=$(snapshot_field "$SNAPSHOT_AFTER" buffer_depth)
CORRUPT_RECORDS=$(snapshot_field "$SNAPSHOT_AFTER" corrupt_records)
TRUNCATED_TAILS=$(snapshot_field "$SNAPSHOT_AFTER" truncated_tails)
DB_AFTER=$(telemetry_rows)
INGEST_AFTER=$(ingestion_stats)
LAG_SAMPLES_AFTER=$(json_value "$INGEST_AFTER" lagSamples)
DUPLICATES_AFTER=$(json_value "$INGEST_AFTER" duplicates)
EMQX_DROPPED_AFTER=$(emqx_dropped "$EMQX_TOKEN")

for pair in \
	"gateway_decoded_after:$GW_DECODED_AFTER" \
	"gateway_buffered_after:$GW_BUFFERED_AFTER" \
	"gateway_forwarded_after:$GW_FORWARDED_AFTER" \
	"gateway_rejected_after:$GW_REJECTED_AFTER" \
	"gateway_rebirth_after:$GW_REBIRTH_AFTER" \
	"buffer_depth:$BUFFER_DEPTH" \
	"corrupt_records:$CORRUPT_RECORDS" \
	"truncated_tails:$TRUNCATED_TAILS" \
	"db_rows_after:$DB_AFTER" \
	"lag_samples_after:$LAG_SAMPLES_AFTER" \
	"duplicates_after:$DUPLICATES_AFTER" \
	"emqx_dropped_after:$EMQX_DROPPED_AFTER"; do
	require_unsigned_integer "${pair%%:*}" "${pair#*:}"
done

# Worst p95 over every sample whose ring had already refilled with THIS run's readings.
PEAK_P95=$(awk -v before="$LAG_SAMPLES_BEFORE" -v window="$LAG_WINDOW" '
	{
		samples = $0
		p95 = $0
		sub(/.*"lagSamples":/, "", samples)
		sub(/[,}].*/, "", samples)
		sub(/.*"lagP95Seconds":/, "", p95)
		sub(/[,}].*/, "", p95)

		if (samples ~ /^[0-9]+$/ && p95 ~ /^-?[0-9.eE+-]+$/ && samples - before >= window) {
			counted++
			if (p95 > peak) {
				peak = p95
			}
		}
	}
	END {
		if (counted > 0) {
			printf "%.6f", peak
		}
	}' "$SAMPLE_FILE")
P95_SAMPLES=$(awk -v before="$LAG_SAMPLES_BEFORE" -v window="$LAG_WINDOW" '
	{
		samples = $0
		sub(/.*"lagSamples":/, "", samples)
		sub(/[,}].*/, "", samples)
		if (samples ~ /^[0-9]+$/ && samples - before >= window) {
			counted++
		}
	}
	END { print counted + 0 }' "$SAMPLE_FILE")

# The receiver's own sustained rate, measured on the INTERIOR of the run window.
#
# Everything else here is an end-state equality, and an end state cannot tell "ingestion held
# 5.000 msg/s for ten minutes" apart from "ingestion held 3.000 for ten minutes and spent the drain
# budget catching up". Both finish with the same rows in the same table. Only one of them is D2.
#
# Interior, because the edges are not the pipeline: the first samples cover container start and an
# empty connection pool, and the last sample lands wherever the poller's clock happened to fall.
# Counting accounted measurements - stored plus recognised as already stored - rather than rows,
# because two readings that share a millisecond are one measurement and dedup refusing the second
# is correct behaviour, not a slow receiver.
RECEIVER_RATE=$(awk -v from="$((RUN_STARTED + RAMP_UP_SECONDS))" -v to="$RUN_ENDED" '
	{
		stamp = $1
		inserted = $0
		duplicates = $0
		sub(/.*"inserted":/, "", inserted)
		sub(/[,}].*/, "", inserted)
		sub(/.*"duplicates":/, "", duplicates)
		sub(/[,}].*/, "", duplicates)

		if (stamp !~ /^[0-9]+$/ || inserted !~ /^[0-9]+$/ || duplicates !~ /^[0-9]+$/) {
			next
		}

		if (stamp < from || stamp > to) {
			next
		}

		accounted = inserted + duplicates

		if (first_stamp == 0) {
			first_stamp = stamp
			first_accounted = accounted
		}

		last_stamp = stamp
		last_accounted = accounted
	}
	END {
		if (first_stamp > 0 && last_stamp > first_stamp) {
			printf "%.3f", (last_accounted - first_accounted) / (last_stamp - first_stamp)
		}
	}' "$SAMPLE_FILE")

RECEIVER_SPAN=$(awk -v from="$((RUN_STARTED + RAMP_UP_SECONDS))" -v to="$RUN_ENDED" '
	{
		if ($1 ~ /^[0-9]+$/ && $1 >= from && $1 <= to) {
			if (first == 0) first = $1
			last = $1
		}
	}
	END { print (first > 0 && last > first) ? last - first : 0 }' "$SAMPLE_FILE")

PEAK_CPU=$(awk '{ gsub(/%/, "", $1); if ($1 ~ /^[0-9.]+$/ && $1 + 0 > peak) peak = $1 + 0 }
	END { if (peak > 0) printf "%.1f", peak }' "$RESOURCE_FILE")
# `docker stats` prints "456.7MiB / 2GiB", so the peak has to be compared in one unit rather than
# lexically - 9MiB sorts above 100MiB as text.
PEAK_MEMORY=$(awk '
	{
		value = $2
		unit = value
		sub(/[A-Za-z]+$/, "", value)
		sub(/^[0-9.]+/, "", unit)

		if (value !~ /^[0-9.]+$/) {
			next
		}

		# +0 khong phai trang tri: sau sub() thi `value` la chuoi thuan, va awk so sanh
		# chuoi voi chuoi. "1.35" > "98.5" la SAI theo thu tu tu dien, nen dinh 1,35 GiB
		# thua 98,5 MiB va bang so do bao mot con so nho hon su that.
		mib = value + 0
		if (unit == "GiB") mib = mib * 1024
		else if (unit == "KiB") mib = mib / 1024
		else if (unit == "B") mib = mib / 1048576

		if (mib > peak + 0) peak = mib
	}
	END { if (peak > 0) printf "%.1f MiB", peak }' "$RESOURCE_FILE")

GW_DECODED=$((GW_DECODED_AFTER - GW_DECODED_BEFORE))
GW_BUFFERED=$((GW_BUFFERED_AFTER - GW_BUFFERED_BEFORE))
GW_FORWARDED=$((GW_FORWARDED_AFTER - GW_FORWARDED_BEFORE))
GW_REJECTED=$((GW_REJECTED_AFTER - GW_REJECTED_BEFORE))
GW_REBIRTH=$((GW_REBIRTH_AFTER - GW_REBIRTH_BEFORE))
DB_DELTA=$((DB_AFTER - DB_BEFORE))
LAG_SAMPLE_DELTA=$((LAG_SAMPLES_AFTER - LAG_SAMPLES_BEFORE))
DUPLICATE_DELTA=$((DUPLICATES_AFTER - DUPLICATES_BEFORE))
EMQX_DROPPED=$((EMQX_DROPPED_AFTER - EMQX_DROPPED_BEFORE))
ACCOUNTED=$((DB_DELTA + DUPLICATE_DELTA))

say ""
say "  Ket qua D2"
say "  ----------------------------------------------------------------"
say "  Source MQTT messages            : $SOURCE_MESSAGES (+ $DECLARED_MESSAGES birth)"
say "  Source distinct measurements    : $SOURCE_MEASUREMENTS"
say "  Readings sharing a millisecond  : $SOURCE_COLLISIONS"
say "  Source publish failures         : $SOURCE_FAILED"
say "  Source achieved rate            : $SOURCE_RATE msg/s (offered $RATE, nguong $N1_MINIMUM)"
say "  Receiver sustained rate         : ${RECEIVER_RATE:-chua do} msg/s over ${RECEIVER_SPAN}s trong cua so"
say "  Gateway decoded / buffered      : $GW_DECODED / $GW_BUFFERED"
say "  Gateway forwarded / rejected    : $GW_FORWARDED / $GW_REJECTED"
say "  Gateway sequence gap / rebirth  : $GW_REBIRTH"
say "  PostgreSQL scoped row delta     : $DB_DELTA"
say "  Deduped as already present      : $DUPLICATE_DELTA"
say "  Lag sample delta                : $LAG_SAMPLE_DELTA"
say "  Peak p95 trong luc chay         : ${PEAK_P95:-chua do}s over $P95_SAMPLES sample"
say "  Final buffer depth              : $BUFFER_DEPTH"
say "  Corrupt records / truncated     : $CORRUPT_RECORDS / $TRUNCATED_TAILS"
say "  EMQX drop cho subscriber        : $EMQX_DROPPED"
say "  Ingestion CPU dinh / RAM dinh   : ${PEAK_CPU:-chua do}% / ${PEAK_MEMORY:-chua do}"
say ""

FAILED=0
[ "$HARNESS_STATUS" -eq 0 ] || fail "load harness exit=$HARNESS_STATUS"
[ "$DRAINED" -eq 1 ] || fail "gateway khong quiescent trong ${DRAIN_BUDGET}s"
[ "$SOURCE_FAILED" -eq 0 ] || fail "source co $SOURCE_FAILED publish loi"
[ "$SOURCE_REBIRTH" -eq 0 ] || fail "source nhan $SOURCE_REBIRTH rebirth request"
[ "$GW_REBIRTH" -eq 0 ] || fail "gateway phat hien $GW_REBIRTH sequence gap"
[ "$GW_REJECTED" -eq 0 ] || fail "gateway reject $GW_REJECTED message"
# Two comparisons, each between like and like. MESSAGES are what the broker and the gateway
# carry; MEASUREMENTS are what a row means. Comparing published messages with stored rows
# reports the millisecond collisions above as data loss and sends somebody to debug dedup.
SOURCE_PUBLISHED=$((SOURCE_MESSAGES + DECLARED_MESSAGES))
[ "$SOURCE_PUBLISHED" -eq "$GW_DECODED" ] \
	|| fail "source published $SOURCE_PUBLISHED != gateway decoded $GW_DECODED"
[ "$GW_DECODED" -eq "$GW_BUFFERED" ] \
	|| fail "gateway decoded $GW_DECODED != fsynced $GW_BUFFERED"
[ "$GW_BUFFERED" -eq "$GW_FORWARDED" ] \
	|| fail "gateway fsynced $GW_BUFFERED != ingestion-accepted $GW_FORWARDED"
# Stored OR recognised as already stored, never silently gone. The simulator compresses time
# (TimeProvider), so it publishes device timestamps days ahead of the wall clock, and those rows
# sit on natural keys this harness reaches later in real time. Dedup then REFUSES the harness
# row, which is correct behaviour and must not be read as loss: comparing source against the row
# delta alone reported 764 phantom losses on 2026-08-29. The invariant that carries meaning is
# that every source measurement is accounted for, and it stays an exact equality, no tolerance.
[ "$SOURCE_MEASUREMENTS" -eq "$ACCOUNTED" ] \
	|| fail "source measurements $SOURCE_MEASUREMENTS != stored $DB_DELTA + deduped $DUPLICATE_DELTA"
[ "$LAG_SAMPLE_DELTA" -eq "$DB_DELTA" ] \
	|| fail "lag samples $LAG_SAMPLE_DELTA != PostgreSQL delta $DB_DELTA"
[ "$BUFFER_DEPTH" -eq 0 ] || fail "buffer depth cuoi = $BUFFER_DEPTH"
[ "$CORRUPT_RECORDS" -eq 0 ] || fail "buffer co $CORRUPT_RECORDS corrupt record"
[ "$TRUNCATED_TAILS" -eq 0 ] || fail "buffer co $TRUNCATED_TAILS truncated tail"

[ "$EMQX_DROPPED" -eq 0 ] || fail "EMQX da vut $EMQX_DROPPED message cua subscriber gateway"

awk -v actual="$SOURCE_ELAPSED" -v minimum="$DURATION" 'BEGIN { exit !(actual >= minimum) }' \
	|| fail "run chi keo dai ${SOURCE_ELAPSED}s, can >= ${DURATION}s"

# Receiver keeps up with the SOURCE, and this one is enforced in both modes.
#
# It is not a capacity claim - it does not care how fast the source went. It says ingestion did not
# fall behind whatever it was given, which is the difference between "sustained the load" and
# "accumulated a backlog and caught up during the drain". Both end with the same rows in the table.
if [ -z "$RECEIVER_RATE" ]; then
	fail "khong do duoc receiver rate trong cua so chay (khong du sample sau ramp-up)"
else
	awk -v receiver="$RECEIVER_RATE" -v source="$SOURCE_RATE" 'BEGIN { exit !(receiver >= source * 0.99) }' \
		|| fail "receiver $RECEIVER_RATE tut lai sau source $SOURCE_RATE qua 1%; backlog dang tich luy trong cua so"
fi

# ── N1 va N2: nang luc, khong phai tinh dung dan (ADR-031) ────────────────────
#
# Do o day, nghiem thu o M9 roi do lai o M13. Ba menh de duoi day luon duoc TINH va luon duoc IN; ENFORCE_N1
# quyet dinh chung co lam run truot hay khong.
N1_MET=1
N1_WHY=""

note_n1() {
	N1_MET=0
	N1_WHY="${N1_WHY}    - $1
"
	if [ "$ENFORCE_N1" = "1" ]; then
		fail "$1"
	fi
}

awk -v actual="$SOURCE_RATE" -v minimum="$N1_MINIMUM" 'BEGIN { exit !(actual >= minimum) }' \
	|| note_n1 "source rate $SOURCE_RATE < N1 $N1_MINIMUM msg/s"

if [ -n "$RECEIVER_RATE" ]; then
	awk -v actual="$RECEIVER_RATE" -v minimum="$N1_MINIMUM" 'BEGIN { exit !(actual >= minimum) }' \
		|| note_n1 "receiver sustained $RECEIVER_RATE < N1 $N1_MINIMUM msg/s"
fi

# No qualifying sample is missing evidence, not a pass: it means the run never put a full lag window
# through ingestion, so nobody measured the p95 N2 is stated in. That one fails the run in either
# mode - a measurement that did not happen is not a capacity question.
if [ -z "$PEAK_P95" ]; then
	fail "khong co sample p95 nao sau khi ring $LAG_WINDOW day bang du lieu cua run nay"
else
	awk -v actual="$PEAK_P95" 'BEGIN { exit !(actual < 5.0) }' \
		|| note_n1 "peak p95 ${PEAK_P95}s >= 5s (N2)"
fi

if [ "$FAILED" -ne 0 ]; then
	say "D2 FAIL — target tra exit khac 0; khong duoc ghi throughput publisher thanh throughput ingestion."
	exit 1
fi

if [ "$N1_MET" -eq 1 ]; then
	say "D2 PASS — source = gateway fsync = ingestion = PostgreSQL, va dat ca N1 ($SOURCE_RATE msg/s) lan N2 (p95 ${PEAK_P95}s)."
	exit 0
fi

say "D2/M2 PASS — dung dan tuyet doi: source = gateway fsync = ingestion = PostgreSQL,"
say "             EMQX drop 0, va receiver theo kip nguon trong ca cua so chay."
say ""
say "  N1/N2 CHUA DAT tren rig nay — nghiem thu o M9, do lai o M13 (ADR-031):"
printf '%s' "$N1_WHY"
say ""
say "  Tran do duoc cua rig: 5.228 msg/s voi gateway that, do tren 8 KENH. Tren 1.000 kenh - topology"
say "  ma scope.md cam ket - cung rig do 3.933 msg/s va p95 99,5 s (2026-08-30). Khoang cach toi N1/N2"
say "  lon hon nhieu so voi thu bai do 8 kenh tung goi ra, va chay lai khong doi duoc dieu do."
say "  M9/M13: NVM_LOAD_ENFORCE_N1=1 make load, tren rig GIAO duoc >= 10.000 msg/s o QoS 1 toi mot"
say "  no-op subscriber. Tran khong-subscriber KHONG dung duoc: rig nay doc 9.925 o do va 5.951 o"
say "  tang ma gateway that su song."
