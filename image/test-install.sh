#!/usr/bin/env bash
# Installs Pithyx from the ISO into a throwaway QEMU VM through the installer's API, the way the installer page does,
# then starts the VM again without the USB stick and checks the installed system: it answers its install id on port 80
# and asks for the sign-in, not the setup. Without root; needs qemu-system-x86, qemu-utils, ovmf and /dev/kvm.
#
#   image/test-install.sh [--iso <file>] [--mirror] [--keep]
#
# --mirror installs onto both disks and afterwards boots once with the first disk removed. --keep leaves the
# installed VM running.
set -euo pipefail
export LC_ALL=C.UTF-8

repo="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
iso=""
mirror=false
keep=false

die() {
  echo "test-install: $*" >&2
  exit 1
}

step() {
  echo "==> $*"
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --iso) iso="$(realpath "$2")"; shift 2 ;;
    --mirror) mirror=true; shift ;;
    --keep) keep=true; shift ;;
    -h | --help) sed -n '2,10p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'; exit 0 ;;
    *) die "unknown argument $1" ;;
  esac
done
if [[ -z "$iso" ]]; then
  iso="$(find "$repo/image/out" -maxdepth 1 -name 'pithyx-*-amd64.iso' -printf '%T@ %p\n' 2>/dev/null | sort -n | tail -n1 | cut -d' ' -f2-)"
fi
[[ -f "$iso" ]] || die "no ISO; run image/build.sh first or pass --iso"
for tool in qemu-system-x86_64 qemu-img curl python3; do
  command -v "$tool" >/dev/null || die "$tool not found"
done
[[ -w /dev/kvm ]] || die "/dev/kvm is not writable; an installation without KVM takes too long"
readonly OVMF_CODE=/usr/share/OVMF/OVMF_CODE_4M.fd
readonly OVMF_VARS=/usr/share/OVMF/OVMF_VARS_4M.fd

# On disk, not in /tmp: a tmpfs fills up with the VM's disks and the VM stalls.
mkdir -p "$repo/image/out/vm"
vm="$(mktemp -d "$repo/image/out/vm/pithyx-install.XXXXXX")"
stop_vm() {
  local pid
  pid="$(cat "$vm/qemu.pid" 2>/dev/null || true)"
  if [[ -n "$pid" ]]; then
    kill "$pid" 2>/dev/null || true
    while kill -0 "$pid" 2>/dev/null; do sleep 0.5; done
    rm -f "$vm/qemu.pid"
  fi
}
cleanup() {
  stop_vm
  rm -rf "$vm"
}
trap 'if [[ $keep == false ]]; then cleanup; fi' EXIT

cp "$OVMF_VARS" "$vm/vars.fd"
qemu-img create -q -f qcow2 "$vm/disk1.qcow2" 64G
qemu-img create -q -f qcow2 "$vm/disk2.qcow2" 64G
free_port() {
  python3 -c 'import socket; s = socket.socket(); s.bind(("127.0.0.1", 0)); print(s.getsockname()[1])'
}
port="$(free_port)"
plain="$(free_port)"
api="http://127.0.0.1:$port/api"

# Starts the VM; the arguments say which drives it gets: stick, disk1, disk2.
start_vm() {
  local drives=() bus=0 drive
  for drive in "$@"; do
    case "$drive" in
      stick) drives+=(-drive "id=stick,if=none,format=raw,readonly=on,file=$iso" -device usb-ehci -device "usb-storage,drive=stick,removable=on") ;;
      disk*)
        drives+=(-drive "id=$drive,if=none,format=qcow2,discard=unmap,detect-zeroes=unmap,file=$vm/$drive.qcow2" -device "ide-hd,drive=$drive,bus=ahci.$bus,serial=PITHYXVM0${drive#disk}")
        bus=$((bus + 1))
        ;;
    esac
  done
  qemu-system-x86_64 -name pithyx-install -machine q35,accel=kvm -cpu host -smp 2 -m 4096 \
    -drive if=pflash,format=raw,readonly=on,file="$OVMF_CODE" \
    -drive if=pflash,format=raw,file="$vm/vars.fd" \
    -device ahci,id=ahci "${drives[@]}" \
    -netdev user,id=net0,hostfwd=tcp:127.0.0.1:"$port"-:7484,hostfwd=tcp:127.0.0.1:"$plain"-:80 \
    -device virtio-net-pci,netdev=net0,mac=52:54:00:12:34:56 \
    -display none -serial file:"$vm/serial-$(date +%s).log" -monitor unix:"$vm/monitor.sock",server,nowait \
    -daemonize -pidfile "$vm/qemu.pid"
}

wait_for() {
  local url="$1" seconds="$2" deadline=$((SECONDS + $2))
  until curl -fsS --max-time 3 "$url" >/dev/null 2>&1; do
    ((SECONDS < deadline)) || die "$url did not answer within ${seconds}s"
    sleep 3
  done
}

json() {
  python3 -c "import json, sys; data = json.load(sys.stdin); print(eval(sys.argv[1], {'d': data}))" "$1"
}

call() {
  curl -fsS --max-time 30 -b "$vm/cookies" -c "$vm/cookies" -H 'X-Pithyx-Request: 1' "$@"
}

step "booting the installer from $(basename "$iso")"
start_vm stick disk1 disk2
wait_for "$api/health" 240
[[ "$(call "$api/setup" | json 'd["installer"]')" == True ]] || die "the live system does not run the installer"

step "claiming the installation and checking the requirements"
status="$(call -X POST "$api/installer/claim")"
failed="$(json '[c["id"] for c in d["checks"] if not c["passed"]]' <<<"$status")"
[[ "$failed" == "[]" ]] || die "requirements not met: $failed"
options="$(call "$api/installer/options")"
mapfile -t disks < <(json '"\n".join(x["disk"]["id"] for x in d["disks"] if x["suitable"])' <<<"$options")
[[ ${#disks[@]} -ge 2 ]] || die "expected two suitable disks, got ${#disks[*]}"
if [[ "$mirror" == true ]]; then
  chosen="[\"${disks[0]}\", \"${disks[1]}\"]"
  installed=(disk1 disk2)
else
  chosen="[\"${disks[0]}\"]"
  # The disk ids carry the serials PITHYXVM01 and PITHYXVM02 that start_vm gives disk1 and disk2.
  installed=("disk${disks[0]: -1}")
fi

step "installing onto $chosen"
call -X POST -H 'Content-Type: application/json' "$api/installer/install" -o /dev/null --data @- <<EOF
{"hostname": "pithyx-test", "language": "en", "region": null, "timeZone": "Europe/Berlin", "ntpServers": [],
 "keyboardLayout": "de", "diskIds": $chosen, "username": "admin", "password": "correct horse battery staple",
 "displayName": null, "eraseConfirmed": true}
EOF
deadline=$((SECONDS + 900))
slow=0
while :; do
  ((SECONDS < deadline)) || die "the installation did not finish within 15 minutes"
  # Under the load of the installation a request can take long; the next one tries again.
  if ! status="$(call "$api/installer")"; then
    slow=$((slow + 1))
    sleep 5
    continue
  fi
  phase="$(json 'd["phase"]' <<<"$status")"
  [[ "$phase" == installing ]] || break
  sleep 5
done
[[ "$phase" == installed ]] || die "the installation ended as $phase in step $(json 'd["step"]' <<<"$status"); serial logs in $vm"
install_id="$(json 'd["installId"]' <<<"$status")"
[[ "$(json 'd["recoveryKey"][:4]' <<<"$status")" == PTHX ]] || die "no recovery key"
echo "installed in $((SECONDS - deadline + 900))s, install id $install_id ($slow status requests timed out)"

step "restarting without the USB stick"
call -X POST "$api/installer/reboot" -o /dev/null
sleep 5
stop_vm
start_vm "${installed[@]}"

check_installed() {
  wait_for "http://127.0.0.1:$plain/api/install-id" 300
  local answer
  answer="$(curl -fsS "http://127.0.0.1:$plain/api/install-id" | json 'd["installId"]')"
  [[ "$answer" == "$install_id" ]] || die "the installed system answers install id $answer, expected $install_id"
  local redirect
  redirect="$(curl -s -o /dev/null -w '%{http_code} %{redirect_url}' "http://127.0.0.1:$plain/")"
  [[ "$redirect" == "307 http://127.0.0.1:7484/" ]] || die "port 80 does not redirect to 7484: $redirect"
  local setup
  setup="$(curl -fsS "$api/setup")"
  [[ "$(json 'd["installer"] or d["required"]' <<<"$setup")" == False ]] || die "the installed system is not set up: $setup"
  [[ "$(json 'd["hostname"]' <<<"$setup")" == pithyx-test ]] || die "wrong host name: $setup"
  curl -fsS -c "$vm/session" -H 'X-Pithyx-Request: 1' -H 'Content-Type: application/json' \
    --data '{"username": "admin", "password": "correct horse battery staple"}' "$api/auth/login" -o /dev/null \
    || die "the administrator cannot sign in"
}
check_installed
echo "the installed system answers and the administrator signs in"

if [[ "$mirror" == true ]]; then
  step "booting with the first disk removed"
  stop_vm
  start_vm disk2
  check_installed
  echo "the mirror boots from the second disk alone"
fi

step "done"
if [[ "$keep" == true ]]; then
  echo "VM kept: http://127.0.0.1:$port, monitor $vm/monitor.sock (pid $(cat "$vm/qemu.pid"))"
fi
