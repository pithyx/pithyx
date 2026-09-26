#!/usr/bin/env bash
# Creates and destroys ZFS test pools on the dev VM, refusing any disk that is not a declared test disk.
#
#   testpool.sh [--dry-run] create <pool> [--layout <vdev-type>] <serial>...
#   testpool.sh [--dry-run] destroy <pool>
#
# Pool names must start with "vyxtest". Disks are selected by serial and must match VYXTEST01 to
# VYXTEST06. If /etc/vyxis/test-disks exists, the serial must also be listed there.
set -euo pipefail
export LC_ALL=C.UTF-8

readonly SERIAL_PATTERN='^VYXTEST0[1-6]$'
readonly POOL_PATTERN='^vyxtest[a-z0-9_-]*$'
readonly TEST_DISKS_FILE=/etc/vyxis/test-disks

dry_run=false

die() {
  echo "testpool: $*" >&2
  exit 1
}

run() {
  echo "+ $*"
  if [[ "$dry_run" == false ]]; then
    "$@"
  fi
}

require_zpool() {
  if [[ "$dry_run" == false ]] && ! command -v zpool >/dev/null; then
    die "zpool not found; install zfsutils-linux first"
  fi
}

check_pool_name() {
  [[ "$1" =~ $POOL_PATTERN ]] || die "pool name '$1' must match $POOL_PATTERN"
}

check_serial() {
  local serial=$1
  [[ "$serial" =~ $SERIAL_PATTERN ]] || die "disk serial '$serial' is not a test disk"
  if [[ -f "$TEST_DISKS_FILE" ]] && ! grep -qx "$serial" "$TEST_DISKS_FILE"; then
    die "disk serial '$serial' is not listed in $TEST_DISKS_FILE"
  fi
}

# Prints the /dev/disk/by-id path of the whole disk with the given serial.
disk_by_serial() {
  local serial=$1 name
  name=$(lsblk -dn -o NAME,SERIAL,TYPE | awk -v s="$serial" '$2 == s && $3 == "disk" { print $1 }')
  [[ -n "$name" ]] || die "no disk with serial '$serial'"
  [[ $(wc -l <<<"$name") -eq 1 ]] || die "serial '$serial' matches more than one disk"
  if lsblk -n -o MOUNTPOINTS "/dev/$name" | grep -q '[^[:space:]]'; then
    die "disk '$name' ($serial) has mounted filesystems"
  fi
  local link
  for link in /dev/disk/by-id/*; do
    if [[ "$link" != *-part* && $(readlink -f "$link") == "/dev/$name" ]]; then
      echo "$link"
      return
    fi
  done
  die "no /dev/disk/by-id link for '$name' ($serial)"
}

create_pool() {
  local pool=$1
  shift
  check_pool_name "$pool"
  local layout=()
  if [[ "${1:-}" == --layout ]]; then
    [[ -n "${2:-}" ]] || die "--layout needs a vdev type"
    layout=("$2")
    shift 2
  fi
  [[ $# -gt 0 ]] || die "no disk serials given"
  local devices=() serial
  for serial in "$@"; do
    check_serial "$serial"
    devices+=("$(disk_by_serial "$serial")")
  done
  require_zpool
  run sudo zpool create -f -o ashift=12 -o autotrim=on "$pool" "${layout[@]}" "${devices[@]}"
}

destroy_pool() {
  local pool=$1
  check_pool_name "$pool"
  command -v zpool >/dev/null || die "zpool not found; install zfsutils-linux first"
  local vdevs
  vdevs=$(sudo zpool list -v -H -P -L "$pool" | awk '$1 ~ /^\/dev\// { print $1 }')
  [[ -n "$vdevs" ]] || die "pool '$pool' has no disk vdevs or does not exist"
  local vdev disk serial
  while read -r vdev; do
    disk=$(lsblk -dn -o PKNAME "$vdev")
    disk=${disk:-$(basename "$vdev")}
    serial=$(lsblk -dn -o SERIAL "/dev/$disk")
    check_serial "$serial"
  done <<<"$vdevs"
  run sudo zpool destroy "$pool"
}

if [[ "${1:-}" == --dry-run ]]; then
  dry_run=true
  shift
fi

case "${1:-}" in
  create)
    [[ $# -ge 2 ]] || die "usage: testpool.sh [--dry-run] create <pool> [--layout <type>] <serial>..."
    shift
    create_pool "$@"
    ;;
  destroy)
    [[ $# -eq 2 ]] || die "usage: testpool.sh [--dry-run] destroy <pool>"
    destroy_pool "$2"
    ;;
  *)
    die "usage: testpool.sh [--dry-run] create|destroy ..."
    ;;
esac
