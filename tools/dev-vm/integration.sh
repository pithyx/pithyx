#!/usr/bin/env bash
# Runs the integration tests that need root, real OpenZFS or block devices (Category=StorageIntegration) on the dev VM,
# with --docker the ones that need a real Docker engine (Category=DockerIntegration), or with --network the ones that
# need a real systemd-networkd (Category=NetworkIntegration). GitHub CI never runs them: its runners have no root, no
# ZFS, no Docker and no networkd of their own.
#
#   tools/dev-vm/integration.sh [--filter <xunit class filter>] [--disk-pulls] [--docker] [--network [--record]]
#
#   --disk-pulls  also run the tests that pull scsi_debug disks out of a running pool. They can leave ZFS waiting in
#                 the kernel until a reboot (OpenZFS 2.3.9), so run them on a freshly booted dev VM only.
#   --docker      run the Docker tests instead. They configure Docker with the box's own files (daemon.json, the
#                 drop-in, subuid and subgid, the remap user, the nftables chain) on a throwaway pool; the script keeps
#                 copies and puts all of it back afterwards, with Docker stopped as before.
#   --network     run the network tests instead (D337). They change only the veth ends ptest0 to ptest2, which
#                 /etc/pithyx/test-nics lists: the script connects them to the namespace pithyx-nettest with a bridge,
#                 a VLAN 42 and dnsmasq (DHCPv4 without a router, router advertisements with lifetime 0, DHCPv6), and
#                 starts systemd-networkd with a drop-in that leaves foreign routes, rules and next hops alone, so the
#                 management card (ifupdown) and its routes stay as they are. Afterwards networkd is stopped again and
#                 the namespace, the veths, the drop-in and every file of Pithyx's in /etc/systemd/network are gone.
#   --record      with --network: copy the kernel and networkd files the tests saw into daemon/tests/fixtures/network.
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
network=0
record=0

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
    --network)
      network=1
      shift
      ;;
    --record)
      record=1
      shift
      ;;
    *) die "unknown option: $1" ;;
  esac
done

[[ "$(head -n1 /etc/pithyx/environment 2>/dev/null)" == dev ]] || die "/etc/pithyx/environment does not say dev; this is for the dev VM only"
[[ "$EUID" -ne 0 ]] || die "run as your user; the script asks sudo for the test run only"
[[ "$docker" -eq 0 || "$network" -eq 0 ]] || die "--docker and --network run separately"
if [[ "$network" -eq 1 ]]; then
  command -v dnsmasq >/dev/null || [[ -x /usr/sbin/dnsmasq ]] || die "dnsmasq not found; run tools/dev-vm/provision.sh first"
  command -v networkctl >/dev/null || die "networkctl not found"
  [[ "$(sort /etc/pithyx/test-nics 2>/dev/null | tr '\n' ' ')" == "ptest0 ptest1 ptest2 " ]] ||
    die "/etc/pithyx/test-nics must list exactly ptest0, ptest1 and ptest2; run tools/dev-vm/provision.sh"
  ! systemctl is-active --quiet systemd-networkd.service || die "systemd-networkd runs already; this script expects it stopped"
  ! compgen -G "/etc/systemd/network/10-pithyx-*" >/dev/null || die "/etc/systemd/network has files of Pithyx's from an earlier run"
else
  command -v zpool >/dev/null || die "zpool not found"
fi

dotnet build "$repo/daemon/tests/Pithyx.Daemon.Tests" --configuration Release --nologo --verbosity quiet

if [[ "$network" -eq 0 ]]; then
  sudo modprobe zfs
  sudo modprobe -n scsi_debug || die "the scsi_debug module is missing (linux-modules-extra on some kernels)"
fi

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
netns=pithyx-nettest
networkd_dropin=/etc/systemd/networkd.conf.d/pithyx-integration.conf
setup_network() {
  echo "integration: building the test network in the namespace $netns"
  sudo ip netns add "$netns"
  sudo ip -n "$netns" link set lo up
  sudo ip -n "$netns" link add br0 type bridge
  local i
  for i in 0 1 2; do
    sudo ip link add "ptest$i" type veth peer name "peer$i" netns "$netns"
    sudo ip -n "$netns" link set "peer$i" master br0 up
  done
  sudo ip -n "$netns" link set br0 up
  sudo ip -n "$netns" address add 10.77.0.1/24 dev br0
  sudo ip -n "$netns" address add fd77::1/64 dev br0
  sudo ip -n "$netns" link add link br0 name br0.42 type vlan id 42
  sudo ip -n "$netns" link set br0.42 up
  sudo ip -n "$netns" address add 10.77.42.1/24 dev br0.42
  # No router option and a router lifetime of 0: the test network never becomes the dev VM's default route.
  sudo ip netns exec "$netns" /usr/sbin/dnsmasq --keep-in-foreground --conf-file=/dev/null --port=0 \
    --bind-interfaces --interface=br0 --interface=br0.42 \
    --dhcp-range=10.77.0.100,10.77.0.199,255.255.255.0,1h --dhcp-range=10.77.42.100,10.77.42.199,255.255.255.0,1h \
    --dhcp-option=option:router --dhcp-option=option:dns-server,10.77.0.1 \
    --enable-ra --dhcp-range=fd77::100,fd77::1ff,slaac,64,1h --ra-param=br0,10,0 \
    --dhcp-leasefile="$scratch/dnsmasq.leases" --pid-file="$scratch/dnsmasq.pid" --log-facility="$scratch/dnsmasq.log" &
  sudo install -d /etc/systemd/networkd.conf.d
  printf '%s\n' '# integration.sh --network: networkd manages only the test cards and leaves every foreign route alone.' \
    '[Network]' 'ManageForeignRoutes=no' 'ManageForeignRoutingPolicyRules=no' 'ManageForeignNextHops=no' |
    sudo tee "$networkd_dropin" >/dev/null
  sudo systemctl start systemd-networkd.service
}
teardown_network() {
  sudo rm -f /etc/systemd/network/10-pithyx-* /etc/systemd/network/*.pithyx-new
  sudo systemctl stop systemd-networkd.service systemd-networkd.socket systemd-networkd-varlink.socket 2>/dev/null || true
  sudo systemctl reset-failed systemd-networkd.service 2>/dev/null || true
  sudo rm -f "$networkd_dropin"
  sudo rmdir --ignore-fail-on-non-empty /etc/systemd/networkd.conf.d 2>/dev/null || true
  if [[ -s "$scratch/dnsmasq.pid" ]]; then
    sudo kill "$(sudo cat "$scratch/dnsmasq.pid")" 2>/dev/null || true
  fi
  local link
  for link in ptbond0 ptvlan42 ptest0 ptest1 ptest2; do
    sudo ip link delete "$link" 2>/dev/null || true
  done
  sudo ip netns delete "$netns" 2>/dev/null || true
}

fixtures=""
cleanup() {
  if [[ -n "$fixtures" && -d "$fixtures" ]]; then
    sudo chown -R "$(id -u):$(id -g)" "$fixtures"
    cp "$fixtures"/* "$repo/daemon/tests/fixtures/network/"
    echo "integration: recorded the network fixtures into daemon/tests/fixtures/network"
  fi
  if [[ "$docker" -eq 1 ]]; then
    restore_docker
  fi
  if [[ "$network" -eq 1 ]]; then
    teardown_network
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
[[ "$network" -eq 0 ]] || category=NetworkIntegration
if [[ "$network" -eq 1 ]]; then
  setup_network
  [[ "$record" -eq 0 ]] || fixtures="$scratch/fixtures"
fi

# A pool command stuck in the kernel never returns; stop the run instead of waiting for hours.
# The tests call losetup, modprobe and useradd, which a user's PATH may lack.
sudo -E env "PATH=$PATH:/usr/sbin:/sbin" "TMPDIR=$scratch" PITHYX_STORAGE_INTEGRATION=1 "PITHYX_DISK_PULL_TESTS=$disk_pulls" \
  "PITHYX_DOCKER_INTEGRATION=$docker" "PITHYX_NETWORK_INTEGRATION=$network" "PITHYX_FIXTURE_OUT=$fixtures" \
  timeout --kill-after=30s 20m \
  "$repo/daemon/tests/Pithyx.Daemon.Tests/bin/Release/net10.0/Pithyx.Daemon.Tests" \
  -trait "Category=$category" "${filter[@]}"
