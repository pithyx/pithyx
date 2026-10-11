#!/usr/bin/env bash
# The owner's disk failure test on the dev VM, as a script: a disk is pulled out of a running pool through sysfs, the
# way a failing or unplugged disk leaves the kernel, and brought back with a SCSI rescan.
#
#   tools/dev-vm/disk-failure-test.sh [--report <file>]
#
# It runs its own daemon in real mode from this checkout's Release build, with a fresh database in a temporary folder,
# so the owner's database and administrator stay untouched; pithyx-dev.service is stopped for the test and started
# again afterwards. Only the test disks VYXTEST01 and VYXTEST06 are used (through testpool.sh), and they are wiped at
# the end. Other pools (for example the owner's) are only read, to check that they stay as they were.
#
# Scenarios:
#   1. A mirror on VYXTEST01 and VYXTEST06 loses VYXTEST06: DEGRADED within seconds, alerts for the pool and the disk,
#      other pools unchanged. Data written while degraded. After the rescan the disk is back, resilvered, all clear.
#   2. A single-disk pool on VYXTEST06 loses its disk: UNAVAIL, destroy and export plans refused (a destroy would hang
#      in the kernel), back after the rescan.
#
# Writes one Markdown report (stdout, and --report <file>). Exits non-zero when a check failed.
# Functions called through check and wait_for look unreachable to shellcheck.
# shellcheck disable=SC2317
set -euo pipefail
export LC_ALL=C.UTF-8

readonly PORT=7484
readonly UNIT=pithyx-dev.service
readonly POOL=pithyxtestdf
readonly KEEP=VYXTEST01
readonly PULL=VYXTEST06
repo="$(cd "$(dirname "$0")/../.." && pwd)"
report_file=""
failures=0
report=""

die() {
  echo "disk-failure-test: $*" >&2
  exit 2
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --report)
      report_file="$2"
      shift 2
      ;;
    *) die "unknown option: $1" ;;
  esac
done

[[ "$(head -n1 /etc/pithyx/environment 2>/dev/null)" == dev ]] || die "/etc/pithyx/environment does not say dev"
for serial in "$KEEP" "$PULL"; do
  grep -qx "$serial" /etc/pithyx/test-disks || die "$serial is not listed in /etc/pithyx/test-disks"
done

line() { report+="$*"$'\n'; }
check() {
  # check <description> <condition command...>
  local what="$1"
  shift
  if "$@"; then
    line "- [x] $what"
  else
    line "- [ ] **FAILED:** $what"
    failures=$((failures + 1))
  fi
}

device_of() {
  lsblk -dn -o NAME,SERIAL | awk -v serial="$1" '$2 == serial { print $1 }'
}

host_of() {
  readlink -f "/sys/block/$1" | tr '/' '\n' | grep -m1 '^host'
}

work="$(mktemp -d /tmp/pithyx-disk-failure-XXXXXX)"
jar="$work/cookies"
daemon_pid=""
unit_was_active=0

api() {
  # api <method> <path> [json body]
  local method="$1" path="$2" body="${3:-}"
  if [[ -n "$body" ]]; then
    curl -s -b "$jar" -c "$jar" -X "$method" -H 'Content-Type: application/json' -H 'X-Pithyx-Request: 1' --data "$body" "http://127.0.0.1:$PORT$path"
  else
    curl -s -b "$jar" -c "$jar" -X "$method" -H 'X-Pithyx-Request: 1' "http://127.0.0.1:$PORT$path"
  fi
}

pool_state() {
  api GET /api/storage/pools | python3 -c "import json,sys; pools={p['name']:p for p in json.load(sys.stdin)['pools']}; print(pools.get('$1',{}).get('state','GONE'))"
}

wait_for() {
  # wait_for <seconds> <command...>: true once the command succeeds
  local deadline=$((SECONDS + $1))
  shift
  while ((SECONDS < deadline)); do
    if "$@"; then
      return 0
    fi
    sleep 0.5
  done
  return 1
}

state_is() { [[ "$(pool_state "$1")" == "$2" ]]; }

alerts_clear() { ! api GET /api/storage/alerts | grep -q ":$POOL"; }

cleanup() {
  set +e

  # A pulled disk comes back first: a pool without its disks must never be destroyed (it would hang the kernel).
  for scan in /sys/class/scsi_host/host*/scan; do
    echo '- - -' | sudo tee "$scan" >/dev/null
  done
  sudo udevadm settle
  sudo zpool clear "$POOL" 2>/dev/null
  "$repo/tools/dev-vm/testpool.sh" destroy "$POOL" >/dev/null 2>&1
  "$repo/tools/dev-vm/testpool.sh" wipe "$KEEP" "$PULL" >/dev/null 2>&1
  if [[ -n "$daemon_pid" ]]; then
    sudo kill "$daemon_pid" 2>/dev/null
    wait "$daemon_pid" 2>/dev/null
  fi
  if [[ "$unit_was_active" == 1 ]]; then
    sudo systemctl start "$UNIT"
  fi
  rm -rf "$work"
}
trap cleanup EXIT

line "## Disk failure test on the dev VM, $(date -u +%Y-%m-%dT%H:%M:%SZ)"
line ""
line "Checkout: \`$(git -C "$repo" rev-parse --short HEAD)\` ($(git -C "$repo" branch --show-current)), OpenZFS $(zfs version | head -n1), kernel $(uname -r). ZED: $(systemctl is-active zfs-zed 2>/dev/null || true)."
line ""

echo "Building the daemon (Release)..." >&2
dotnet build "$repo/daemon/src/Pithyx.Daemon" --configuration Release --nologo -v quiet >/dev/null

if systemctl is-active --quiet "$UNIT"; then
  unit_was_active=1
  sudo systemctl stop "$UNIT"
fi
if ss -ltn "sport = :$PORT" | grep -q LISTEN; then
  die "port $PORT is still in use"
fi

before="$(zpool list -H -o name,health 2>/dev/null | grep -v "^$POOL" || true)"
"$repo/tools/dev-vm/testpool.sh" create "$POOL" --layout mirror "$KEEP" "$PULL" >/dev/null

mkdir -p "$work/logs" "$work/cache"
(
  cd "$repo/daemon/src/Pithyx.Daemon/bin/Release/net10.0"
  exec sudo env PITHYX_DB="$work/pithyx.db" PITHYX_LOG_DIR="$work/logs" PITHYX_CACHE_DIR="$work/cache" \
    ASPNETCORE_ENVIRONMENT=Development dotnet pithyxd.dll >"$work/daemon.log" 2>&1
) &
daemon_pid=$!
wait_for 60 curl -sf "http://127.0.0.1:$PORT/api/health" >/dev/null || die "the daemon did not start; see $work/daemon.log"
api POST /api/setup '{"username":"disktest","password":"disk failure test on the dev VM"}' >/dev/null
check "The test daemon runs in real mode" bash -c "curl -s http://127.0.0.1:$PORT/api/health | grep -q '\"real\"'"
check "Pool $POOL (mirror on $KEEP and $PULL) is ONLINE" state_is "$POOL" ONLINE

line ""
line "### 1. A mirror disk is pulled and comes back"
device="$(device_of "$PULL")"
host="$(host_of "$device")"
line ""
line "\`echo 1 > /sys/block/$device/device/delete\` ($PULL, $host)"
line ""
pulled_at=$SECONDS
echo 1 | sudo tee "/sys/block/$device/device/delete" >/dev/null
if wait_for 10 state_is "$POOL" DEGRADED; then
  line "- [x] DEGRADED after $((SECONDS - pulled_at)) s (ZFS itself still says: \`$(zpool list -H -o health "$POOL")\`)"
else
  line "- [ ] **FAILED:** DEGRADED within 10 s (the API says $(pool_state "$POOL"))"
  failures=$((failures + 1))
fi
alerts="$(api GET /api/storage/alerts)"
check "Alert pool.degraded:$POOL (banner)" grep -q "pool.degraded:$POOL" <<<"$alerts"
check "Alert disk.missing for $PULL" grep -q "disk.missing:$POOL" <<<"$alerts"
if notifications="$(api GET /api/notifications 2>/dev/null)" && grep -q '"items"\|\[' <<<"$notifications"; then
  check "A persistent notification for the degraded pool" grep -q "poolDegraded" <<<"$notifications"
fi
after="$(zpool list -H -o name,health 2>/dev/null | grep -v "^$POOL" || true)"
check "Other pools unchanged (\`${before//$'\n'/, }\`)" test "$before" == "$after"
sudo dd if=/dev/urandom of="/$POOL/written-while-degraded.bin" bs=1M count=64 status=none
sync -f "/$POOL"
check "64 MB written while degraded" test -s "/$POOL/written-while-degraded.bin"

echo '- - -' | sudo tee "/sys/class/scsi_host/$host/scan" >/dev/null
sudo udevadm settle
line ""
line "SCSI rescan of $host: the disk is back as \`$(device_of "$PULL")\`."
line ""
back_at=$SECONDS
if wait_for 60 bash -c "[[ \$(zpool list -H -o health $POOL) == ONLINE ]] && ! zpool status $POOL | grep -q 'resilver in progress'"; then
  line "- [x] ONLINE and resilvered after $((SECONDS - back_at)) s: \`$(zpool status "$POOL" | awk '/scan:/ {sub(/^ +scan: /, ""); print}')\`"
else
  line "- [ ] **FAILED:** ONLINE and resilvered within 60 s"
  failures=$((failures + 1))
fi
check "The API says ONLINE again" wait_for 10 state_is "$POOL" ONLINE
check "Its alerts are gone (all clear)" wait_for 10 alerts_clear
check "The daemon brought the disk back itself (zpool online)" grep -q 'came back: ran zpool' "$work/daemon.log"
check "No write errors left: zpool status $POOL is clean" bash -c "zpool status -x $POOL | grep -q 'is healthy'"

line ""
line "### 2. A single-disk pool loses its only disk"
"$repo/tools/dev-vm/testpool.sh" destroy "$POOL" >/dev/null
"$repo/tools/dev-vm/testpool.sh" wipe "$KEEP" "$PULL" >/dev/null
"$repo/tools/dev-vm/testpool.sh" create "$POOL" "$PULL" >/dev/null
wait_for 10 state_is "$POOL" ONLINE || true
device="$(device_of "$PULL")"
host="$(host_of "$device")"
echo 1 | sudo tee "/sys/block/$device/device/delete" >/dev/null
check "UNAVAIL in the API after the pull" wait_for 10 state_is "$POOL" UNAVAIL
plan="$(api POST /api/storage/plans "{\"action\":\"pool.destroy\",\"pool\":\"$POOL\",\"confirmName\":\"$POOL\"}")"
check "Destroy refused with storage.pool.disks_missing" grep -q 'storage.pool.disks_missing' <<<"$plan"
plan="$(api POST /api/storage/plans "{\"action\":\"pool.export\",\"pool\":\"$POOL\"}")"
check "Export refused with storage.pool.disks_missing" grep -q 'storage.pool.disks_missing' <<<"$plan"
after="$(zpool list -H -o name,health 2>/dev/null | grep -v "^$POOL" || true)"
check "Other pools unchanged" test "$before" == "$after"
echo '- - -' | sudo tee "/sys/class/scsi_host/$host/scan" >/dev/null
sudo udevadm settle
check "Back ONLINE after the rescan" wait_for 30 state_is "$POOL" ONLINE

line ""
if ((failures == 0)); then
  line "**Result: every check passed.**"
else
  line "**Result: $failures check(s) failed.** Daemon log lines about pools:"
  line ""
  line '```'
  line "$(grep -E 'Pools changed|came back|Bringing back|Reading the pools' "$work/daemon.log" | tail -n 20 | cut -c1-240)"
  line '```'
fi

printf '%s' "$report"
if [[ -n "$report_file" ]]; then
  printf '%s' "$report" >"$report_file"
fi
exit $((failures > 0 ? 1 : 0))
