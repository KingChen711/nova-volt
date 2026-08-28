#!/usr/bin/env sh
#
# Crash-consistency proof for the hand-written edge queue.
# Every writer is stopped with SIGKILL. The verifier is a new process opening the same volume.

set -eu

# Git Bash rewrites Linux container paths such as /crash to a Windows host path unless disabled.
# Docker must receive the volume target verbatim on both Windows and Unix hosts.
MSYS_NO_PATHCONV=1
export MSYS_NO_PATHCONV

ROUNDS=${ROUNDS:-200}
IMAGE=novavolt/nvm-edge-gateway:dev
VOLUME="nvm-buffer-crash-$$-$(date +%s)"
RED=0
ACTIVE_CONTAINER=

case "$ROUNDS" in
  *[!0-9]*|'') echo "ROUNDS phai la so nguyen duong." >&2; exit 2 ;;
esac

if [ "$ROUNDS" -le 0 ]; then
  echo "ROUNDS phai lon hon 0." >&2
  exit 2
fi

docker volume create "$VOLUME" >/dev/null

cleanup() {
  if [ -n "$ACTIVE_CONTAINER" ]; then
    docker rm -f "$ACTIVE_CONTAINER" >/dev/null 2>&1 || true
  fi
  docker volume rm "$VOLUME" >/dev/null 2>&1 || true
}
trap cleanup EXIT INT TERM

# The production image runs as APP_UID=1654. Give that user the disposable volume, rather than
# weakening the image to root just for a test.
docker run --rm -v "$VOLUME:/crash" alpine:3.21 chown 1654:1654 /crash

round=1
while [ "$round" -le "$ROUNDS" ]; do
  container="nvm-buffer-crash-writer-$$-$round"
  ACTIVE_CONTAINER=$container
  directory="/crash/round-$round"

  docker run -d --name "$container" -v "$VOLUME:/crash" "$IMAGE" \
    --buffer-crash-writer --directory "$directory" --round "$round" >/dev/null

  # Do not confuse container startup time with the crash window. Wait until the writer has fsynced
  # its non-zero cursor, then vary SIGKILL across later data fsync batches.
  checks=0
  while ! docker exec "$container" test -f "$directory/writer-ready" >/dev/null 2>&1; do
    checks=$((checks + 1))

    if [ "$checks" -ge 100 ] || [ "$(docker inspect -f '{{.State.Running}}' "$container" 2>/dev/null || true)" != "true" ]; then
      echo "Writer vong $round khong vao duoc crash window." >&2
      docker logs "$container" >&2 || true
      RED=$((RED + 1))
      break
    fi

    sleep 0.05
  done

  # Release the writer only after the harness can observe it. The following delay is therefore a
  # real offset into append/fsync work, not an offset into CLR/container startup.
  docker exec "$container" touch "$directory/writer-start" >/dev/null 2>&1 || true

  hundredths=$((1 + ((round * 37) % 10)))
  delay=$(printf '0.%02d' "$hundredths")
  sleep "$delay"

  docker kill --signal KILL "$container" >/dev/null 2>&1 || true
  docker rm "$container" >/dev/null 2>&1 || true
  ACTIVE_CONTAINER=

  if ! docker run --rm -v "$VOLUME:/crash" "$IMAGE" \
      --buffer-crash-verify --directory "$directory" --round "$round"; then
    RED=$((RED + 1))
    echo "Vong $round DO." >&2
  fi

  if [ $((round % 25)) -eq 0 ] || [ "$round" -eq "$ROUNDS" ]; then
    echo "buffer-crash: $round/$ROUNDS vong, do=$RED"
  fi

  round=$((round + 1))
done

if [ "$RED" -ne 0 ]; then
  echo "buffer-crash THAT BAI: $RED/$ROUNDS vong do." >&2
  exit 1
fi

echo "buffer-crash DAT: 0/$ROUNDS vong do. Moi vong deu SIGKILL va reopen bang process moi."
