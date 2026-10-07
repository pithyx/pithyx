#!/usr/bin/env bash
# Runs the integration tests that need root, real OpenZFS or block devices (Category=StorageIntegration) on the dev VM,
# or with --docker the ones that need a real Docker engine (Category=DockerIntegration). GitHub CI never runs them: its
# runners have no root, no ZFS and no Docker.
#
#   tools/dev-vm/integration.sh [--filter <xunit class filter>] [--disk-pulls] [--docker]
#
#   --disk-pulls  also run the tests that pull scsi_debug disks out of a running pool. They can leave ZFS waiting in
#                 the kernel until a reboot (OpenZFS 2.3.9), so run them on a freshly booted dev VM only.
#   --docker      run the Docker tests instead. They configure Docker with the box's own files (daemon.json, the
#                 drop-in, subuid and subgid, the remap user, the nftables chain) on a throwaway pool; the script keeps
#                 copies and puts all of it back afterwards, with Docker stopped as before.
#
# The tests build their own throwaway pools on sparse files (and scsi_debug RAM disks for the disk failure tests);
# they never name the system disk or the declared test disks. The sparse files live in /dev/shm, so the host's SSD
# sees no writes. The build runs as the calling user, never as root; only the test run itself runs under sudo.
set -euo pipefail
export LC_ALL=C.UTF-8

repo="$(cd "$(dirname "$0")/../.." && pwd)"
filter=()
disk_pulls=0
docker=0

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
    --docker)
      docker=1
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
# The Docker tests write the files the daemon owns on a box; these are kept and restored, and Docker ends stopped.
docker_files=(/etc/docker/daemon.json /etc/systemd/system/docker.service.d/pithyx.conf /etc/subuid /etc/subgid)
docker_user_existed=0
docker_chain_existed=0
if [[ "$docker" -eq 1 ]]; then
  command -v docker >/dev/null || die "docker not found; run tools/dev-vm/provision.sh first"
  mkdir -p "$scratch/docker-state"
  for file in "${docker_files[@]}"; do
    if sudo test -e "$file"; then
      sudo cp -a "$file" "$scratch/docker-state/$(echo "$file" | tr / _)"
    fi
  done
  getent passwd pithyx-docker >/dev/null && docker_user_existed=1
  sudo nft list chain inet pithyx apps-input >/dev/null 2>&1 && docker_chain_existed=1
fi
restore_docker() {
  sudo systemctl stop docker.socket docker.service containerd.service 2>/dev/null || true
  sudo systemctl reset-failed docker.service containerd.service 2>/dev/null || true
  for file in "${docker_files[@]}"; do
    local copy
    copy="$scratch/docker-state/$(echo "$file" | tr / _)"
    if [[ -e "$copy" ]]; then
      sudo cp -a "$copy" "$file"
    else
      sudo rm -f "$file"
    fi
  done
  sudo rmdir --ignore-fail-on-non-empty /etc/systemd/system/docker.service.d 2>/dev/null || true
  sudo systemctl daemon-reload
  if [[ "$docker_user_existed" -eq 0 ]] && getent passwd pithyx-docker >/dev/null; then
    sudo userdel pithyx-docker
  fi
  if [[ "$docker_chain_existed" -eq 0 ]]; then
    sudo nft delete chain inet pithyx apps-input 2>/dev/null || true
    sudo nft delete table inet pithyx 2>/dev/null || true
  fi
}
cleanup() {
  if [[ "$docker" -eq 1 ]]; then
    restore_docker
  fi
  sudo rm -rf --one-file-system "$scratch"
  if [[ "$dev_was_active" -eq 1 ]]; then
    echo "integration: starting $dev_unit again"
    sudo systemctl start "$dev_unit"
  fi
}
trap cleanup EXIT

category=StorageIntegration
[[ "$docker" -eq 0 ]] || category=DockerIntegration

# A pool command stuck in the kernel never returns; stop the run instead of waiting for hours.
# The tests call losetup, modprobe and useradd, which a user's PATH may lack.
sudo -E env "PATH=$PATH:/usr/sbin:/sbin" "TMPDIR=$scratch" PITHYX_STORAGE_INTEGRATION=1 "PITHYX_DISK_PULL_TESTS=$disk_pulls" \
  "PITHYX_DOCKER_INTEGRATION=$docker" \
  timeout --kill-after=30s 20m \
  "$repo/daemon/tests/Pithyx.Daemon.Tests/bin/Release/net10.0/Pithyx.Daemon.Tests" \
  -trait "Category=$category" "${filter[@]}"
