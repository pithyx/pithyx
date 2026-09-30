#!/usr/bin/env bash
# Runs the integration tests that need root, real OpenZFS or block devices (Category=StorageIntegration) on the dev VM.
# GitHub CI never runs them: its runners have no root and no ZFS.
#
#   tools/dev-vm/integration.sh [--filter <xunit class filter>]
#
# The tests build their own throwaway pools on sparse files (and scsi_debug RAM disks for the disk failure tests);
# they never name the system disk or the declared test disks. The sparse files live in /dev/shm, so the host's SSD
# sees no writes. The build runs as the calling user, never as root; only the test run itself runs under sudo.
set -euo pipefail
export LC_ALL=C.UTF-8

repo="$(cd "$(dirname "$0")/../.." && pwd)"
filter=()

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
trap 'sudo rm -rf --one-file-system "$scratch"' EXIT

# A pool command stuck in the kernel never returns; stop the run instead of waiting for hours.
sudo -E env "PATH=$PATH" "TMPDIR=$scratch" PITHYX_STORAGE_INTEGRATION=1 \
  timeout --kill-after=30s 20m \
  "$repo/daemon/tests/Pithyx.Daemon.Tests/bin/Release/net10.0/Pithyx.Daemon.Tests" \
  -trait "Category=StorageIntegration" "${filter[@]}"
