#!/usr/bin/env bash
# Runs the integration tests that need root, real OpenZFS or block devices (Category=StorageIntegration) on the dev VM.
# GitHub CI never runs them: its runners have no root and no ZFS.
#
#   tools/dev-vm/integration.sh [--filter <xunit class filter>] [--disk-pulls]
#
#   --disk-pulls  also run the tests that pull scsi_debug disks out of a running pool. They can leave ZFS waiting in
#                 the kernel until a reboot (OpenZFS 2.3.9), so run them on a freshly booted dev VM only.
#
# The tests build their own throwaway pools on sparse files (and scsi_debug RAM disks for the disk failure tests);
# they never name the system disk or the declared test disks. The sparse files live in /dev/shm, so the host's SSD
# sees no writes. The build runs as the calling user, never as root; only the test run itself runs under sudo.
set -euo pipefail
export LC_ALL=C.UTF-8

repo="$(cd "$(dirname "$0")/../.." && pwd)"
filter=()
disk_pulls=0

die() {
  echo "integration: $*" >&2
  exit 2
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --filter)
      [[ $# -ge 2 ]] || die "--filter needs a class name"
      filter=(-class "$2")
      shift 2
      ;;
    --disk-pulls)
      disk_pulls=1
      shift
      ;;
    *) die "unknown option: $1" ;;
  esac
done

[[ "$(head -n1 /etc/pithyx/environment 2>/dev/null)" == dev ]] || die "/etc/pithyx/environment does not say dev; this is for the dev VM only"
[[ "$EUID" -ne 0 ]] || die "run as your user; the script asks sudo for the test run only"
command -v zpool >/dev/null || die "zpool not found"

dotnet build "$repo/daemon/tests/Pithyx.Daemon.Tests" --configuration Release --nologo --verbosity quiet

sudo modprobe zfs
sudo modprobe -n scsi_debug || die "the scsi_debug module is missing (linux-modules-extra on some kernels)"

scratch="$(mktemp -d /dev/shm/pithyx-integration-XXXXXX)"
# The dev daemon on 7484 manages every pool it sees, the throwaway test pools included (cache datasets, mounts,
# reconnects). It pauses for the run and always comes back (D276).
dev_unit=pithyx-dev.service
dev_was_active=0
if systemctl is-active --quiet "$dev_unit"; then
  dev_was_active=1
  echo "integration: stopping $dev_unit for the run"
  sudo systemctl stop "$dev_unit"
fi
cleanup() {
  sudo rm -rf --one-file-system "$scratch"
  if [[ "$dev_was_active" -eq 1 ]]; then
    echo "integration: starting $dev_unit again"
    sudo systemctl start "$dev_unit"
  fi
}
trap cleanup EXIT

# A pool command stuck in the kernel never returns; stop the run instead of waiting for hours.
# The tests call losetup, modprobe and useradd, which a user's PATH may lack.
sudo -E env "PATH=$PATH:/usr/sbin:/sbin" "TMPDIR=$scratch" PITHYX_STORAGE_INTEGRATION=1 "PITHYX_DISK_PULL_TESTS=$disk_pulls" \
  timeout --kill-after=30s 20m \
  "$repo/daemon/tests/Pithyx.Daemon.Tests/bin/Release/net10.0/Pithyx.Daemon.Tests" \
  -trait "Category=StorageIntegration" "${filter[@]}"
