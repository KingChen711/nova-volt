#!/usr/bin/env sh
#
# R2 — isolate the Sparkplug session contract from the end-to-end capacity gate in R3.
# One EDGE-F1 owns one MQTT connection and one seq stream. The harness listens for NCMD itself and
# exits non-zero if the gateway detects a gap and asks for rebirth.

set -eu

COMPOSE="docker compose"
RATE=${RATE:-500}
DURATION=${DURATION:-60}
SIMULATOR=nvm-simulator

if [ "$DURATION" -lt 60 ]; then
	echo "DURATION phai >= 60 giay cho gate R2." >&2
	exit 1
fi

for container in nvm-emqx nvm-edge-gateway nvm-ingestion nvm-timescale; do
	running=$(docker inspect -f '{{.State.Running}}' "$container" 2>/dev/null || true)
	if [ "$running" != "true" ]; then
		echo "$container chua chay; khoi dong stack M2 truoc khi do R2." >&2
		exit 1
	fi
done

simulator_was_running=$(docker inspect -f '{{.State.Running}}' "$SIMULATOR" 2>/dev/null || true)

cleanup() {
	status=$?
	trap - EXIT HUP INT TERM

	if [ "$simulator_was_running" = "true" ]; then
		$COMPOSE --profile sim up -d --no-build simulator >/dev/null 2>&1 || true
	fi

	exit "$status"
}
trap cleanup EXIT HUP INT TERM

echo "== build load harness"
$COMPOSE --profile load build load-harness >/dev/null

echo "== dung simulator: hai process cung EDGE-F1 se tao hai Sparkplug session xung dot"
$COMPOSE --profile sim stop simulator >/dev/null

echo "== restart gateway de session tracker bat dau tu NBIRTH cua phep do"
$COMPOSE restart edge-gateway >/dev/null
started_at=$(docker inspect -f '{{.State.StartedAt}}' nvm-edge-gateway)

ready=0
i=0
while [ "$i" -lt 30 ]; do
	if docker logs --since "$started_at" nvm-edge-gateway 2>&1 | grep -q "Subscribed to"; then
		ready=1
		break
	fi
	i=$((i + 1))
	sleep 1
done

if [ "$ready" -ne 1 ]; then
	echo "Gateway khong subscribe lai EMQX trong 30 giay." >&2
	exit 1
fi

echo "== protocol smoke: $RATE msg/s trong $DURATION giay"
echo "   D2 van la 5.000 msg/s trong 600 giay o make load; gate nay chi co lap session/seq."
NVM_LOAD_RATE="$RATE" NVM_LOAD_DURATION="$DURATION" \
	$COMPOSE --profile load run --rm load-harness
