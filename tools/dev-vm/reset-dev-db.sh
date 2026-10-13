#!/usr/bin/env bash
# Resets the database of the dev unit on port 7484 and seeds the accounts again.
#
#   tools/dev-vm/reset-dev-db.sh
#
# Stops pithyx-dev.service, deletes /var/lib/pithyx/pithyx.db with its -wal and -shm files, starts the unit (the
# daemon applies every migration on start), waits for it and runs seed-dev-accounts.sh, which sets up the owner from
# ~/.config/pithyx-dev/owner.env again. The owner's 2FA is off afterwards. This is the only way to reset the dev
# database; the unit always keeps this one database.
set -euo pipefail

readonly PORT=7484
readonly UNIT=pithyx-dev.service
readonly DATABASE=/var/lib/pithyx/pithyx.db
repo="$(cd "$(dirname "$0")/../.." && pwd)"

die() {
  echo "reset-dev-db: $*" >&2
  exit 1
}

[[ "$(head -n1 /etc/pithyx/environment 2>/dev/null)" == dev ]] || die "/etc/pithyx/environment does not say dev; this is for the dev VM only"
[[ "$EUID" -ne 0 ]] || die "run as the owner's user, not as root; the script asks sudo for the root steps"
systemctl cat "$UNIT" >/dev/null 2>&1 || die "$UNIT is not installed; run tools/dev-vm/install-dev-daemon.sh"

sudo systemctl stop "$UNIT"
sudo rm -f "$DATABASE" "$DATABASE-wal" "$DATABASE-shm"
sudo systemctl reset-failed "$UNIT" 2>/dev/null || true
sudo systemctl start "$UNIT"

for _ in $(seq 150); do
  if curl --insecure -fsS "https://127.0.0.1:$PORT/api/health" >/dev/null 2>&1; then
    echo "reset $DATABASE"
    exec "$repo/tools/dev-vm/seed-dev-accounts.sh"
  fi
  sleep 0.2
done
sudo journalctl -u "$UNIT" -n 30 --no-pager
die "the daemon did not answer on port $PORT"
