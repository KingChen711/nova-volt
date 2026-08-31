#!/usr/bin/env sh
# C13: prove the Grafana container has only its intended IT route.

set -u

GRAFANA=nvm-grafana
SIDECAR_IMAGE=alpine:3.21
FAILED=0
CHECKS=0

if ! docker inspect "$GRAFANA" >/dev/null 2>&1; then
  echo "nvm-grafana chua chay. Chay 'make up-obs' truoc." >&2
  exit 2
fi

network_count=$(docker inspect --format '{{len .NetworkSettings.Networks}}' "$GRAFANA")
network_names=$(docker inspect --format '{{range $name, $_ := .NetworkSettings.Networks}}{{$name}} {{end}}' "$GRAFANA")

CHECKS=$((CHECKS + 1))
case "$network_count:$network_names" in
  "1:novavolt-mes_it-net ")
    echo "  Grafana chi o it-net             dat"
    ;;
  *)
    echo "  Grafana chi o it-net             TRUOT ($network_names)"
    FAILED=$((FAILED + 1))
    ;;
esac

connect_from_grafana() {
  docker run --rm --network "container:$GRAFANA" "$SIDECAR_IMAGE" \
    sh -c "timeout 3 nc $1 $2 < /dev/null" >/dev/null 2>&1
}

CHECKS=$((CHECKS + 1))
if connect_from_grafana timescale 5432; then
  echo "  Grafana -> timescale:5432        dat (open)"
else
  echo "  Grafana -> timescale:5432        TRUOT (blocked)"
  FAILED=$((FAILED + 1))
fi

CHECKS=$((CHECKS + 1))
if connect_from_grafana emqx 1883; then
  echo "  Grafana -> emqx:1883             TRUOT (open)"
  FAILED=$((FAILED + 1))
else
  echo "  Grafana -> emqx:1883             dat (blocked)"
fi

if [ "$FAILED" -eq 0 ]; then
  echo "Grafana giu dung ranh gioi: $CHECKS/$CHECKS phep do dat."
  exit 0
fi

echo "Grafana vi pham ranh gioi: $FAILED/$CHECKS phep do truot."
exit 1
