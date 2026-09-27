#!/usr/bin/env bash
# Runs the daemon on the dev VM in real mode, as root, as the systemd unit pithyx-dev.service on port 7484.
#
#   tools/dev-vm/install-dev-daemon.sh [--no-build]   build this checkout and (re)start the unit from it
#   tools/dev-vm/install-dev-daemon.sh --uninstall    stop and remove the unit
#
# Refuses to run unless /etc/pithyx/environment says dev. The database lives in /var/lib/pithyx/pithyx.db and the
# logs in /var/log/pithyx; the desktop comes from this checkout's desktop/dist. A daemon already listening on 7484
# (the simulation started by start.sh) is stopped first. The unit's DiskGuard only allows the disks listed in
# /etc/pithyx/test-disks.
set -euo pipefail

readonly PORT=7484
readonly UNIT=pithyx-dev.service
repo="$(cd "$(dirname "$0")/../.." && pwd)"
build=1

die() {
  echo "install-dev-daemon: $*" >&2
  exit 1
}

[[ "$(head -n1 /etc/pithyx/environment 2>/dev/null)" == dev ]] || die "/etc/pithyx/environment does not say dev; this is for the dev VM only"

case "${1:-}" in
  --uninstall)
    sudo systemctl disable --now "$UNIT" 2>/dev/null || true
    sudo rm -f "/etc/systemd/system/$UNIT"
    sudo systemctl daemon-reload
    echo "removed $UNIT; /var/lib/pithyx and /var/log/pithyx are kept"
    exit 0
    ;;
  --no-build) build=0 ;;
  "") ;;
  *) die "unknown option: $1" ;;
esac

if [[ "$build" == 1 ]]; then
  dotnet build "$repo/daemon/src/Pithyx.Daemon" --configuration Release --nologo --verbosity quiet
  (cd "$repo/apps/demo" && pnpm exec vite build --logLevel warn)
  (cd "$repo/desktop" && pnpm exec vite build --logLevel warn)
fi
[[ -f "$repo/daemon/src/Pithyx.Daemon/bin/Release/net10.0/pithyxd.dll" ]] || die "no Release build in $repo; run without --no-build"

if systemctl is-active --quiet "$UNIT"; then
  sudo systemctl stop "$UNIT"
fi
# Another daemon on the port, for example the simulation from start.sh, is stopped by its PID.
pid=$(ss -ltnpH "sport = :$PORT" | grep -o 'pid=[0-9]*' | head -n1 | cut -d= -f2 || true)
if [[ -n "$pid" ]]; then
  echo "stopping the daemon on port $PORT (pid $pid)"
  kill "$pid"
  for _ in $(seq 50); do
    kill -0 "$pid" 2>/dev/null || break
    sleep 0.1
  done
fi

sudo install -d -m 0700 /var/lib/pithyx
sudo install -d -m 0750 /var/log/pithyx
sed "s|@REPO@|$repo|g" "$repo/tools/dev-vm/$UNIT" | sudo tee "/etc/systemd/system/$UNIT" >/dev/null
sudo systemctl daemon-reload
sudo systemctl enable --now "$UNIT"

for _ in $(seq 100); do
  if curl -fsS "http://127.0.0.1:$PORT/api/health" >/dev/null 2>&1; then
    echo "$UNIT runs from $repo on http://$(hostname -I | awk '{ print $1 }'):$PORT (real mode)"
    exit 0
  fi
  sleep 0.2
done
sudo journalctl -u "$UNIT" -n 30 --no-pager
die "the daemon did not answer on port $PORT"
