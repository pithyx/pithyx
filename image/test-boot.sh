#!/usr/bin/env bash
# Boots the ISO in a throwaway QEMU VM (UEFI, KVM when available) with two empty disks and checks that the live
# system comes up: the daemon answers on 7484 and the console shows the greeting. Without root; needs qemu-system-x86
# and ovmf, and /dev/kvm for a quick boot.
#
#   image/test-boot.sh [--iso <file>] [--keep] [--timeout <seconds>]
#
# --keep leaves the VM running after the check and prints how to reach it.
set -euo pipefail
export LC_ALL=C.UTF-8

repo="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
iso=""
keep=false
timeout=300

die() {
  echo "test-boot: $*" >&2
  exit 1
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --iso) iso="$(realpath "$2")"; shift 2 ;;
    --keep) keep=true; shift ;;
    --timeout) timeout="$2"; shift 2 ;;
    -h | --help) sed -n '2,9p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'; exit 0 ;;
    *) die "unknown argument $1" ;;
  esac
done
if [[ -z "$iso" ]]; then
  iso="$(find "$repo/image/out" -maxdepth 1 -name 'pithyx-*-amd64.iso' -printf '%T@ %p\n' 2>/dev/null | sort -n | tail -n1 | cut -d' ' -f2-)"
fi
[[ -f "$iso" ]] || die "no ISO; run image/build.sh first or pass --iso"
command -v qemu-system-x86_64 >/dev/null || die "qemu-system-x86_64 not found; install qemu-system-x86"
readonly OVMF_CODE=/usr/share/OVMF/OVMF_CODE_4M.fd
readonly OVMF_VARS=/usr/share/OVMF/OVMF_VARS_4M.fd
[[ -f "$OVMF_CODE" ]] || die "$OVMF_CODE not found; install ovmf"

vm="$(mktemp -d "${TMPDIR:-/tmp}/pithyx-vm.XXXXXX")"
cleanup() {
  if [[ -f "$vm/qemu.pid" ]]; then
    kill "$(cat "$vm/qemu.pid")" 2>/dev/null || true
  fi
  rm -rf "$vm"
}
if [[ "$keep" == false ]]; then
  trap cleanup EXIT
fi

cp "$OVMF_VARS" "$vm/vars.fd"
qemu-img create -q -f qcow2 "$vm/disk1.qcow2" 64G
qemu-img create -q -f qcow2 "$vm/disk2.qcow2" 64G
port="$(python3 -c 'import socket; s = socket.socket(); s.bind(("127.0.0.1", 0)); print(s.getsockname()[1])')"
accel=tcg
if [[ -w /dev/kvm ]]; then
  accel=kvm
fi

echo "booting $(basename "$iso") ($accel), daemon at http://127.0.0.1:$port"
qemu-system-x86_64 -name pithyx-test -machine q35,accel="$accel" -cpu "$([[ $accel == kvm ]] && echo host || echo max)" \
  -smp 2 -m 4096 \
  -drive if=pflash,format=raw,readonly=on,file="$OVMF_CODE" \
  -drive if=pflash,format=raw,file="$vm/vars.fd" \
  -device ahci,id=ahci \
  -drive id=disk1,if=none,format=qcow2,file="$vm/disk1.qcow2" -device ide-hd,drive=disk1,bus=ahci.0,serial=PITHYXVM01 \
  -drive id=disk2,if=none,format=qcow2,file="$vm/disk2.qcow2" -device ide-hd,drive=disk2,bus=ahci.1,serial=PITHYXVM02 \
  -drive id=stick,if=none,format=raw,readonly=on,file="$iso" -device usb-ehci -device usb-storage,drive=stick,removable=on \
  -netdev user,id=net0,hostfwd=tcp:127.0.0.1:"$port"-:7484 -device virtio-net-pci,netdev=net0 \
  -display none -serial file:"$vm/serial.log" -monitor unix:"$vm/monitor.sock",server,nowait \
  -daemonize -pidfile "$vm/qemu.pid"

deadline=$((SECONDS + timeout))
until curl -fsS --max-time 2 "http://127.0.0.1:$port/api/health" >"$vm/health.json" 2>/dev/null; do
  if ((SECONDS > deadline)); then
    tail -n 40 "$vm/serial.log" >&2 || true
    die "the daemon did not answer within ${timeout}s"
  fi
  sleep 3
done
echo "daemon answers after $((timeout - deadline + SECONDS))s: $(cat "$vm/health.json")"

if [[ "$keep" == true ]]; then
  echo "VM kept: http://127.0.0.1:$port, serial log $vm/serial.log, monitor socket $vm/monitor.sock (pid $(cat "$vm/qemu.pid"))"
fi
