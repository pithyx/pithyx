#!/usr/bin/env bash
# Installs Pithyx from the ISO into a throwaway QEMU VM through the installer's API, the way the installer page does,
# then starts the VM again without the USB stick and checks the installed system: it answers its install id on port 80
# and asks for the sign-in, not the setup. Without root; needs qemu-system-x86, qemu-utils, ovmf and /dev/kvm.
#
#   image/test-install.sh [--iso <file>] [--mirror] [--network] [--update <folder>] [--reinstall] [--keep]
#
# Every run checks that tty2 shows no login prompt (D341). --reinstall (D341, root is asked for through qemu-nbd) gives
# the VM a third disk with the pool tank, reads the ZFS host id of the installed system, installs again onto the same
# disk and checks that the new system has another host id in /etc/hostid and its initramfs, and that tank imports there
# with the forced import, as after a reinstall on a real box.
# --mirror installs onto both disks and afterwards boots once with the first disk removed. --network gives the VM three
# network cards (D337): the first one, which the test talks through, gets a static address in installer step 2 after
# the step's connection test; the other two share one network and become an active-backup bond with a VLAN, reached
# through that bond while their primary card loses its link, and a wrong address on the first card rolls back after
# 60 seconds. --keep leaves the installed VM running.
#
# --update (D338, implies --mirror) takes a folder with two signed beta update files newer than the ISO, the older one
# N+1 and the newer one N+2 (tools/release/build-update.sh and sign-update.sh). Before the first start of the installed
# system the test writes the update source with the token of ~/.config/pithyx-dev/github-updates.env into it (through
# qemu-nbd, so root is asked for) and a serial GRUB console for itself. Then: N+1 from an uploaded file, N+2 online from
# the channel-beta of pithyx/pithyx (publish it first with tools/release/publish-beta.sh; without the token file N+2 is
# uploaded as well), a broken N+3 made from N+2 whose daemon never starts rolls back by itself, the GRUB entry of a
# snapshot rolls back through the recovery menu, and the recovery menu resets the network and installs N+2 from a
# virtual USB stick. Then the update source (D339): a fork's pithyx-source.json saved in Settings is chosen in the
# recovery menu, after which a file of the fork's key verifies and one of the project's is refused; a typed source
# replaces it, and "Back to the Pithyx project" restores the project's keys. Afterwards both ESPs carry the same GRUB
# and the trial file of the updater, and the mirror starts from its second disk alone.
set -euo pipefail
export LC_ALL=C.UTF-8

repo="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
iso=""
mirror=false
network=false
updates=""
keep=false
reinstall=false
keyboard=de
extra_disks=()

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
    --network) network=true; shift ;;
    --update) updates="$(realpath "$2")"; mirror=true; keyboard=us; shift 2 ;;
    --reinstall) reinstall=true; shift ;;
    --keep) keep=true; shift ;;
    -h | --help) sed -n '2,24p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'; exit 0 ;;
    *) die "unknown argument $1" ;;
  esac
done
if [[ -z "$iso" ]]; then
  iso="$(find "$repo/image/out" -maxdepth 1 -name 'pithyx-*-amd64.iso' -printf '%T@ %p\n' 2>/dev/null | sort -n | tail -n1 | cut -d' ' -f2-)"
fi
[[ -f "$iso" ]] || die "no ISO; run image/build.sh first or pass --iso"
for tool in qemu-system-x86_64 qemu-img curl python3 sshpass ssh sftp; do
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
if [[ "$reinstall" == true ]]; then
  qemu-img create -q -f qcow2 "$vm/disk3.qcow2" 8G
  extra_disks=(disk3)
fi
free_port() {
  python3 -c 'import socket; s = socket.socket(); s.bind(("127.0.0.1", 0)); print(s.getsockname()[1])'
}
port="$(free_port)"
plain="$(free_port)"
ssh_port="$(free_port)"
bonded="$(free_port)"
api="http://127.0.0.1:$port/api"
# The network cards with --network: the first one carries the test's connection, the other two share one network.
readonly NIC0_MAC=52:54:00:12:34:56 NIC1_MAC=52:54:00:12:34:61 NIC2_MAC=52:54:00:12:34:62
readonly BOND_ADDRESS=10.0.3.50

# Starts the VM; the arguments say which drives it gets: stick, disk1, disk2. The USB controller and the network cards
# keep their PCI slots with and without the stick, as on real hardware, so the cards keep their names after the
# installation (the settings of step 2 name them).
start_vm() {
  local drives=() nics=() bus=0 drive
  serial_log="$vm/serial-$(date +%s%N).log"
  for drive in "$@" "${extra_disks[@]}"; do
    case "$drive" in
      stick) drives+=(-drive "id=stick,if=none,format=raw,readonly=on,file=$iso" -device "usb-storage,bus=usb.0,drive=stick,removable=on") ;;
      usb) drives+=(-drive "id=usb,if=none,format=raw,readonly=on,file=$vm/usb.img" -device "usb-storage,bus=usb.0,drive=usb,removable=on") ;;
      disk*)
        drives+=(-drive "id=$drive,if=none,format=qcow2,discard=unmap,detect-zeroes=unmap,file=$vm/$drive.qcow2" -device "ide-hd,drive=$drive,bus=ahci.$bus,serial=PITHYXVM0${drive#disk}")
        bus=$((bus + 1))
        ;;
    esac
  done
  if [[ "$network" == true ]]; then
    # One user network behind an emulated hub with two ports, so both cards of the bond reach the same network.
    nics=(-netdev "user,id=lan,net=10.0.3.0/24,ipv6=off,hostfwd=tcp:127.0.0.1:$bonded-$BOND_ADDRESS:7484"
      -netdev "hubport,id=uplink,hubid=1,netdev=lan"
      -netdev "hubport,id=port1,hubid=1" -device "virtio-net-pci,netdev=port1,id=nic1,mac=$NIC1_MAC,addr=0x11"
      -netdev "hubport,id=port2,hubid=1" -device "virtio-net-pci,netdev=port2,id=nic2,mac=$NIC2_MAC,addr=0x12")
  fi
  qemu-system-x86_64 -name pithyx-install -machine q35,accel=kvm -cpu host -smp 2 -m 4096 \
    -drive if=pflash,format=raw,readonly=on,file="$OVMF_CODE" \
    -drive if=pflash,format=raw,file="$vm/vars.fd" \
    -device ahci,id=ahci -device usb-ehci,id=usb "${drives[@]}" \
    -netdev user,id=net0,hostfwd=tcp:127.0.0.1:"$port"-:7484,hostfwd=tcp:127.0.0.1:"$plain"-:80,hostfwd=tcp:127.0.0.1:"$ssh_port"-:22 \
    -device virtio-net-pci,netdev=net0,id=nic0,mac=$NIC0_MAC,addr=0x10 "${nics[@]}" \
    -display none -chardev "socket,id=serial0,path=$vm/serial.sock,server=on,wait=off,logfile=$serial_log" -serial chardev:serial0 \
    -monitor unix:"$vm/monitor.sock",server,nowait \
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

# A command for the QEMU monitor, for example "set_link nic1 off".
monitor() {
  python3 - "$vm/monitor.sock" "$1" <<'PY'
import socket, sys, time
s = socket.socket(socket.AF_UNIX)
s.connect(sys.argv[1])
time.sleep(0.3)
s.recv(65536)
s.sendall((sys.argv[2] + "\n").encode())
time.sleep(0.5)
PY
}

# The kernel's name of the card with this MAC address, from a network state or the installer's options.
nic_name() {
  json "[l['name'] for l in d['$2'] if l['macAddress'] == '$1'][0]"
}

# --update: the VM's disks on dev01 through qemu-nbd for a moment, while the VM is off. udev may assemble the VM's arrays
# by itself; they are stopped and the system array is assembled under a name of the test's.
nbd_attach() {
  local index=0 disk md
  sudo modprobe nbd max_part=8
  for disk in "${installed[@]}"; do
    sudo qemu-nbd --connect="/dev/nbd$index" "$vm/$disk.qcow2"
    index=$((index + 1))
  done
  sudo udevadm settle
  awk '/nbd/ {print $1}' /proc/mdstat | while read -r md; do
    sudo mdadm --stop "/dev/$md" >/dev/null
  done
}

nbd_detach() {
  local md index
  awk '/nbd/ {print $1}' /proc/mdstat | while read -r md; do
    sudo mdadm --stop "/dev/$md" >/dev/null
  done
  for index in "${!installed[@]}"; do
    sudo qemu-nbd --disconnect "/dev/nbd$index" >/dev/null
  done
}

# Before the installed system's first start: the update source, with the token from github-updates.env when it exists,
# and a GRUB console on the serial port, through which the test reads the menu and picks entries.
prepare_updates() {
  local root="$vm/system-root" members=() index
  nbd_attach
  for index in "${!installed[@]}"; do
    members+=("/dev/nbd${index}p2")
  done
  sudo mdadm --assemble --run /dev/md/pithyxvm-system "${members[@]}" >/dev/null
  mkdir -p "$root"
  sudo mount -o subvol=@ /dev/md/pithyxvm-system "$root"
  # The token goes from its file into the VM without passing through an argument, the terminal or a log.
  sudo env TOKEN_FILE="$HOME/.config/pithyx-dev/github-updates.env" TARGET="$root/etc/pithyx/update-source.json" python3 -c '
import json, os
token = None
if os.path.exists(os.environ["TOKEN_FILE"]):
    for line in open(os.environ["TOKEN_FILE"]):
        if line.startswith("GITHUB_UPDATES_TOKEN="):
            token = line.split("=", 1)[1].strip().strip("\"")
config = {"url": "https://github.com/pithyx/pithyx/releases/download/channel-{channel}/channel.json"}
if token:
    config["token"] = token
descriptor = os.open(os.environ["TARGET"], os.O_WRONLY | os.O_CREAT | os.O_TRUNC, 0o600)
os.write(descriptor, (json.dumps(config) + "\n").encode())
os.close(descriptor)
print("update source written " + ("with" if token else "without") + " a token")'
  # After the installer's pithyx.cfg, which sets 3 seconds.
  printf '# The QEMU test reads and picks GRUB entries on the serial console.\nGRUB_TERMINAL=console\nGRUB_TIMEOUT=10\n' \
    | sudo tee "$root/etc/default/grub.d/zz-pithyx-test.cfg" >/dev/null
  sudo umount "$root"
  nbd_detach
}

# Waits until a Python expression over GET /api/system/updates is true; the box restarts in between.
wait_updates() {
  local expression="$1" seconds="$2" deadline=$((SECONDS + $2)) state=""
  while :; do
    if state="$(admin /system/updates 2>/dev/null)"; then
      [[ "$(json "$expression" <<<"$state")" == True ]] && break
    else
      # A restart or a rollback can end the test's session (a snapshot brings back the sessions of its time).
      sign_in 2>/dev/null || true
    fi
    if ((SECONDS >= deadline)); then
      screenshot timeout || true
      die "the updater did not reach ($expression) within ${seconds}s: $(head -c 800 <<<"$state"); screenshots and serial logs in $vm"
    fi
    sleep 5
  done
}

upload_update() {
  curl -sS --fail-with-body --max-time 900 -b "$vm/session" -c "$vm/session" -H 'X-Pithyx-Request: 1' \
    -H 'Content-Type: application/octet-stream' -T "$1" "$api/system/updates/file" -o /dev/null
}

install_update() {
  admin /system/updates/install -X POST -H 'Content-Type: application/json' --data "{\"source\": \"$1\", \"includeDebian\": false}" -o /dev/null
}

update_version() {
  local name
  name="$(basename "$1")"
  name="${name#pithyx-}"
  echo "${name%-amd64.pithyxupdate}"
}

# An update file made from another with a new version, signed with a key of its own; "broken" makes its daemon never
# start, for the health check's rollback.
make_update() {
  local source="$1" version="$2" target="$3" key="$4" key_id="$5" broken="${6:-}" work="$vm/remade" deb_version="${2/-/\~}" deb
  rm -rf "$work"
  mkdir -p "$work/file" "$work/pithyx" "$work/kernel"
  tar --extract --file="$source" --directory="$work/file"
  dpkg-deb -R "$(ls "$work/file/debs"/pithyx_*.deb)" "$work/pithyx"
  dpkg-deb -R "$(ls "$work/file/debs"/pithyx-kernel_*.deb)" "$work/kernel"
  rm "$work/file/debs"/pithyx_*.deb "$work/file/debs"/pithyx-kernel_*.deb "$work/file/manifest.json.sig"
  sed -i -e "s/^Version: .*/Version: $deb_version/" -e "s/pithyx-kernel (= [^)]*)/pithyx-kernel (= $deb_version)/" "$work/pithyx/DEBIAN/control"
  sed -i "s/^Version: .*/Version: $deb_version/" "$work/kernel/DEBIAN/control"
  if [[ "$broken" == broken ]]; then
    sed -i 's|^ExecStart=.*|ExecStart=/bin/false|' "$work/pithyx/usr/lib/systemd/system/pithyx.service"
  fi
  sed -i "s/^PITHYX_VERSION=.*/PITHYX_VERSION=$version/" "$work/pithyx/usr/lib/pithyx/release"
  for deb in pithyx kernel; do
    dpkg-deb --root-owner-group -Zxz --build "$work/$deb" "$work/file/debs/" >/dev/null
  done
  (
    cd "$work/file/debs"
    dpkg-scanpackages --multiversion . /dev/null 2>/dev/null >Packages
    sed -i '/^SHA256:/,$d' Release
    printf 'SHA256:\n %s %s Packages\n' "$(sha256sum Packages | cut -d' ' -f1)" "$(stat -c %s Packages)" >>Release
  )
  read -r minimum commit kernel zfs published < <(python3 -c 'import json, sys; m = json.load(open(sys.argv[1])); print(m["minimumVersion"], m["commit"], m["kernel"]["release"], m["kernel"]["zfs"], m["publishedAt"])' "$work/file/manifest.json")
  node "$repo/tools/release/update-file.ts" manifest "$work/file" --version "$version" --channel beta --minimum "$minimum" \
    --commit "$commit" --kernel "$kernel" --zfs "$zfs" --published-at "$published"
  "$repo/tools/release/pack-update.sh" "$work/file" "$target"
  "$repo/tools/release/sign-update.sh" "$target" --key "$key" --key-id "$key_id" >/dev/null
}

# Shuts the VM down through ACPI and waits for QEMU to end.
power_off() {
  local pid deadline=$((SECONDS + 180))
  pid="$(cat "$vm/qemu.pid")"
  monitor "system_powerdown"
  while kill -0 "$pid" 2>/dev/null; do
    if ((SECONDS > deadline)); then
      kill "$pid"
      break
    fi
    sleep 2
  done
  rm -f "$vm/qemu.pid"
}

# Waits for GRUB's menu on the serial console and presses an entry's key there, once the countdown runs (before it,
# GRUB is not reading keys yet).
grub_choose() {
  local key="$1" deadline=$((SECONDS + 180))
  until grep -aq "Pithyx recovery" "$serial_log" 2>/dev/null && grep -aq "automatically in" "$serial_log" 2>/dev/null; do
    ((SECONDS < deadline)) || die "GRUB's menu did not appear on the serial console ($serial_log)"
    sleep 0.2
  done
  python3 - "$vm/serial.sock" "$key" <<'PY2'
import socket, sys, time
connection = socket.socket(socket.AF_UNIX)
connection.connect(sys.argv[1])
connection.sendall(sys.argv[2].encode())
time.sleep(0.5)
connection.close()
PY2
}

# Types on the VM's keyboard (tty1, where the recovery menu runs); a newline is Enter.
type_keys() {
  local text="$1" index char key
  for ((index = 0; index < ${#text}; index++)); do
    char="${text:index:1}"
    case "$char" in
      .) key='dot' ;;
      -) key=minus ;;
      _) key=shift-minus ;;
      ' ') key=spc ;;
      /) key=slash ;;
      :) key=shift-semicolon ;;
      =) key=equal ;;
      +) key=shift-equal ;;
      '{') key=shift-bracket_left ;;
      '}') key=shift-bracket_right ;;
      $'\n') key=ret ;;
      [A-Z]) key="shift-${char,,}" ;;
      *) key="$char" ;;
    esac
    monitor "sendkey $key"
  done
}

# Restarts into "Pithyx recovery" (key r) and types each argument as one answer to the menu on tty1.
recovery_menu() {
  local answer
  power_off
  start_vm "${installed[@]}"
  grub_choose r
  sleep 90
  screenshot "recovery-$(date +%s)"
  for answer in "$@"; do
    type_keys "$answer"
    sleep 3
  done
}

# A screenshot of the console as PNG, for a look when a step of the recovery menu fails.
screenshot() {
  monitor "screendump $vm/$1.ppm"
  python3 - "$vm/$1.ppm" "$vm/$1.png" <<'PY2'
import struct, sys, zlib
data = open(sys.argv[1], "rb").read()
header = data.split(b"\n", 3)
width, height = map(int, header[1].split())
pixels = header[3]
rows = b"".join(b"\x00" + pixels[y * width * 3:(y + 1) * width * 3] for y in range(height))
chunk = lambda kind, body: struct.pack(">I", len(body)) + kind + body + struct.pack(">I", zlib.crc32(kind + body) & 0xFFFFFFFF)
open(sys.argv[2], "wb").write(b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", width, height, 8, 2, 0, 0, 0)) + chunk(b"IDAT", zlib.compress(rows)) + chunk(b"IEND", b""))
PY2
}

sign_in() {
  curl -fsS --max-time 10 -c "$vm/session" -H 'X-Pithyx-Request: 1' -H 'Content-Type: application/json' \
    --data '{"username": "admin", "password": "correct horse battery staple"}' "$api/auth/login" -o /dev/null
}

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
  sign_in || die "the administrator cannot sign in"
}

# How many pixels of a screenshot differ from its most common colour: an empty virtual terminal shows at most a cursor.
drawn_pixels() {
  screenshot "$1"
  python3 - "$vm/$1.ppm" <<'PY'
import collections, sys
data = open(sys.argv[1], "rb").read()
header = data.split(b"\n", 3)
pixels = header[3]
colours = collections.Counter(pixels[i:i + 3] for i in range(0, len(pixels), 3))
print(sum(colours.values()) - colours.most_common(1)[0][1])
PY
}

# The ZFS host id of the installed system (powered off) from /etc/hostid, and whether its initramfs carries the same.
installed_hostid() {
  local root="$vm/system-root" members=() index
  nbd_attach
  for index in "${!installed[@]}"; do
    members+=("/dev/nbd${index}p2")
  done
  sudo mdadm --assemble --run /dev/md/pithyxvm-system "${members[@]}" >/dev/null
  mkdir -p "$root"
  sudo mount -o ro,subvol=@ /dev/md/pithyxvm-system "$root"
  local hostid initramfs extracted="$vm/initramfs"
  hostid="$(sudo od -An -tx1 "$root/etc/hostid" 2>/dev/null | tr -d ' \n' || true)"
  rm -rf "$extracted"
  initramfs="$(sudo find "$root/boot" -maxdepth 1 -name 'initrd.img-*' | sort | tail -n1)"
  sudo unmkinitramfs "$initramfs" "$extracted" >/dev/null 2>&1 || true
  local inner
  inner="$(sudo find "$extracted" -path '*/etc/hostid' | head -n1)"
  [[ -n "$inner" ]] && inner="$(sudo od -An -tx1 "$inner" | tr -d ' \n')"
  sudo rm -rf "$extracted"
  sudo umount "$root"
  nbd_detach
  [[ -n "$hostid" ]] || die "the installed system has no /etc/hostid"
  [[ "$inner" == "$hostid" ]] || die "the initramfs carries host id '$inner', /etc/hostid is $hostid"
  echo "$hostid"
}

# Plans a storage action as the administrator, runs it and waits for its job.
storage_action() {
  local preview plan job state deadline=$((SECONDS + 300))
  preview="$(admin /storage/plans -X POST -H 'Content-Type: application/json' --data "$1")" || die "planning $1 failed"
  plan="$(json 'd["planId"]' <<<"$preview")"
  [[ "$plan" != None ]] || die "no plan id for $1: $preview"
  job="$(admin "/storage/plans/$plan/run" -X POST | json 'd["id"]')"
  until state="$(admin "/jobs/$job" | json 'd["state"]')" && [[ "$state" != queued && "$state" != running ]]; do
    ((SECONDS < deadline)) || die "job $job did not finish within 5 minutes"
    sleep 2
  done
  [[ "$state" == succeeded ]] || die "job $job ended as $state: $(admin "/jobs/$job")"
}

# Installs from the stick onto the chosen disks and starts the installed system without the stick.
install_box() {
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
  # --reinstall keeps its pool on disk3 (serial PITHYXVM03), which no installation may take.
  mapfile -t disks < <(printf '%s\n' "${disks[@]}" | grep -v PITHYXVM03 || true)
  [[ ${#disks[@]} -ge 2 ]] || die "expected two suitable disks, got ${#disks[*]}"
  network_request=null
  if [[ "$network" == true ]]; then
    step "testing a static address on the first network card (installer step 2)"
    nic0="$(nic_name "$NIC0_MAC" network <<<"$options")"
    nic1="$(nic_name "$NIC1_MAC" network <<<"$options")"
    nic2="$(nic_name "$NIC2_MAC" network <<<"$options")"
    echo "cards: $nic0 (test connection), $nic1 and $nic2 (one network)"
    # The address the user network hands out anyway, now static, so the test keeps its connection.
    network_request="{\"interfaces\": [{\"name\": \"$nic0\", \"kind\": \"ethernet\", \"ipv4\": \"static\",
      \"ipv4Address\": \"10.0.2.15/24\", \"ipv4Gateway\": \"10.0.2.2\", \"ipv6\": \"off\"}],
      \"global\": {\"hostName\": \"pithyx-test\", \"dnsServers\": [\"10.0.2.3\"], \"searchDomains\": []}}"
    call -X POST -H 'Content-Type: application/json' "$api/installer/network/test" -o /dev/null --data "$network_request"
    deadline=$((SECONDS + 60))
    until result="$(call "$api/installer/network/test" 2>/dev/null)" && [[ "$(json 'd["running"]' <<<"$result")" == False ]]; do
      ((SECONDS < deadline)) || die "the connection test did not finish within 60 seconds"
      sleep 2
    done
    [[ "$(json 'd["errorCode"]' <<<"$result")" == None ]] || die "the connection test failed: $result"
    [[ "$(json "[i['up'] and '10.0.2.15/24' in i['addresses'] and i['gatewayReachable'] for i in d['interfaces'] if i['name'] == '$nic0']" <<<"$result")" == "[True]" ]] \
      || die "the connection test did not reach the gateway: $result"
    echo "the connection test reached the gateway 10.0.2.2 from $nic0"
  fi
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
 "keyboardLayout": "$keyboard", "diskIds": $chosen, "username": "admin", "password": "correct horse battery staple",
 "displayName": null, "eraseConfirmed": true, "network": $network_request}
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
  if [[ -n "$updates" ]]; then
    step "writing the update source and a serial GRUB console into the installed system"
    prepare_updates
  fi
  start_vm "${installed[@]}"
}

install_box
check_installed
echo "the installed system answers and the administrator signs in"

# Calls the installed system's API as the administrator, through the first card or (with --via-bond) through the bond.
admin() {
  local base="$api"
  if [[ "$1" == --via-bond ]]; then
    base="http://127.0.0.1:$bonded/api"
    shift
  fi
  local path="$1" answer
  shift
  # A refusal prints the daemon's problem details, which name the error code.
  if ! answer="$(curl -sS --fail-with-body --max-time 30 -b "$vm/session" -c "$vm/session" -H 'X-Pithyx-Request: 1' "$@" "$base$path")"; then
    echo "$answer" >&2
    return 22
  fi
  printf '%s' "$answer"
}

# The kernel and the ZFS module come as a pair from pithyx-kernel (D338): zpool loads the module on its first call, and
# a module built for another kernel would fail here.
step "checking that the ZFS module loads"
admin /storage/import/scan -X POST -o /dev/null || die "zpool cannot run on the installed system; serial logs in $vm"
echo "zpool answers: the ZFS module fits the kernel"

# sshd never ran on a fresh install (the image disables it), so switching SFTP or SSH on is its first start.
step "checking SFTP and SSH"
readonly REMOTE_PASSWORD="correct horse battery staple"
for account in "sftptest protocol:sftp" "sshtest right:sshLogin"; do
  read -r name resource <<<"$account"
  admin /users -X POST -H 'Content-Type: application/json' -o /dev/null \
    --data "{\"username\": \"$name\", \"password\": \"$REMOTE_PASSWORD\", \"grants\": [{\"resource\": \"$resource\", \"level\": \"allow\"}]}"
done
admin /file-services -X PUT -H 'Content-Type: application/json' --data '{"sftpEnabled": true, "sshEnabled": true}' -o /dev/null
services="$(admin /file-services)"
[[ "$(json 'd["sftp"]' <<<"$services")" == running ]] || die "SFTP does not run: $services; serial logs in $vm"
remote=(-o StrictHostKeyChecking=no -o UserKnownHostsFile=/dev/null -o LogLevel=ERROR -o ConnectTimeout=10 -o PubkeyAuthentication=no -P "$ssh_port")
for _ in $(seq 30); do
  SSHPASS="$REMOTE_PASSWORD" sshpass -e sftp "${remote[@]}" sftptest@127.0.0.1 <<<"ls" >/dev/null 2>&1 && break
  sleep 2
done
SSHPASS="$REMOTE_PASSWORD" sshpass -e sftp "${remote[@]}" sftptest@127.0.0.1 <<<"ls" >/dev/null || die "the SFTP user cannot sign in over SFTP"
shell="$(SSHPASS="$REMOTE_PASSWORD" sshpass -e ssh "${remote[@]/-P/-p}" sshtest@127.0.0.1 id -un)" || die "the SSH user cannot sign in"
[[ "$shell" == sshtest ]] || die "the SSH login answered $shell"
if SSHPASS="$REMOTE_PASSWORD" sshpass -e ssh "${remote[@]/-P/-p}" sftptest@127.0.0.1 true 2>/dev/null; then
  die "the SFTP user got a shell"
fi
echo "SFTP and SSH answer on port 22"

# Waits until a Python expression over GET /api/system/network is true.
wait_network() {
  local expression="$1" seconds="$2" via=("${@:3}") deadline=$((SECONDS + $2)) state=""
  until state="$(admin "${via[@]}" /system/network 2>/dev/null)" && [[ "$(json "$expression" <<<"$state")" == True ]]; do
    ((SECONDS < deadline)) || die "the network did not reach ($expression) within ${seconds}s: $state"
    sleep 2
  done
}

if [[ "$network" == true ]]; then
  step "checking the static address from the installer"
  wait_network "any(l['name'] == '$nic0' and l['method'] == 'static' and '10.0.2.15/24' in l['addresses'] for l in d['links'])" 30

  step "making an active-backup bond of $nic1 and $nic2 with a VLAN"
  config="$(admin /system/network | python3 -c "
import json, sys
config = json.load(sys.stdin)['config']
config['interfaces'] += [
    {'name': 'bond0', 'kind': 'bond', 'ipv4': 'static', 'ipv4Address': '$BOND_ADDRESS/24', 'ipv6': 'off',
     'bond': {'members': ['$nic1', '$nic2'], 'mode': 'activeBackup', 'primary': '$nic1'}},
    {'name': 'bond0.20', 'kind': 'vlan', 'ipv4': 'static', 'ipv4Address': '192.168.20.5/24', 'ipv6': 'off', 'mtu': 1400,
     'vlan': {'parent': 'bond0', 'id': 20}},
]
print(json.dumps(config))")"
  change="$(admin /system/network -X PUT -H 'Content-Type: application/json' --data "$config")"
  admin "/system/network/changes/$(json 'd["id"]' <<<"$change")/confirm" -X POST -o /dev/null
  wait_network "any(l['name'] == 'bond0' and '$BOND_ADDRESS/24' in l['addresses'] for l in d['links']) and any(l['name'] == 'bond0.20' and l['vlanId'] == 20 and l['mtu'] == 1400 and '192.168.20.5/24' in l['addresses'] for l in d['links']) and sum(1 for l in d['links'] if l['master'] == 'bond0') == 2 and d['pending'] is None" 60
  wait_for "http://127.0.0.1:$bonded/api/health" 30
  echo "bond0 answers at $BOND_ADDRESS, bond0.20 has VLAN 20 with MTU 1400"

  step "taking the link of $nic1 away: the bond goes on through $nic2"
  monitor "set_link nic1 off"
  wait_network "any(l['name'] == '$nic1' and not l['carrier'] for l in d['links'])" 30 --via-bond
  for _ in 1 2 3; do
    curl -fsS --max-time 5 "http://127.0.0.1:$bonded/api/health" -o /dev/null || die "the bond does not answer without $nic1"
    sleep 1
  done
  monitor "set_link nic1 on"
  echo "the bond answered through $nic2 alone"

  step "setting a wrong address on $nic0 without confirming it"
  config="$(admin /system/network | python3 -c "
import json, sys
config = json.load(sys.stdin)['config']
for item in config['interfaces']:
    if item['name'] == '$nic0':
        item['ipv4Address'] = '10.0.2.99/24'
print(json.dumps(config))")"
  # The answer may not come back: the test's own connection goes with the old address.
  admin /system/network -X PUT -H 'Content-Type: application/json' --data "$config" -o /dev/null || true
  wait_network "d['pending'] is not None and any(l['name'] == '$nic0' and '10.0.2.99/24' in l['addresses'] for l in d['links'])" 30 --via-bond
  if curl -fsS --max-time 3 "$api/health" -o /dev/null 2>&1; then
    die "the old address of $nic0 still answers"
  fi
  wait_network "d['pending'] is None and any(l['name'] == '$nic0' and l['addresses'] == ['10.0.2.15/24'] for l in d['links'])" 120 --via-bond
  wait_for "$api/health" 30
  echo "after 60 seconds without Keep, $nic0 is back on 10.0.2.15"
fi

if [[ -n "$updates" ]]; then
  mapfile -t files < <(find "$updates" -maxdepth 1 -name 'pithyx-*-amd64.pithyxupdate' | sort -V)
  [[ ${#files[@]} -ge 2 ]] || die "$updates holds fewer than two update files"
  first="${files[-2]}"
  second="${files[-1]}"
  first_version="$(update_version "$first")"
  second_version="$(update_version "$second")"
  installed_version="$(admin /system/updates | json 'd["installedVersion"]')"
  [[ "$(admin /system/updates | json 'd["supported"]')" == True ]] || die "the installed system says updates do not apply: $(admin /system/updates)"

  step "switching to the beta channel"
  admin /system/updates/settings -X PUT -H 'Content-Type: application/json' --data '{"checkEnabled": true, "channel": "beta"}' -o /dev/null

  step "installing $first_version from an uploaded file (from $installed_version)"
  upload_update "$first" || die "the upload of $(basename "$first") was refused"
  [[ "$(admin /system/updates | json 'd["staged"]["version"]')" == "$first_version" ]] || die "the uploaded file is not staged"
  install_update file
  wait_updates "d['installedVersion'] == '$first_version' and d['runningJobId'] is None and d['history'][0]['state'] == 'succeeded'" 1200
  echo "$first_version runs, snapshots: $(admin /system/updates | json '[s["version"] for s in d["snapshots"]]')"

  if [[ -f "$HOME/.config/pithyx-dev/github-updates.env" ]]; then
    step "installing $second_version online from channel-beta"
    admin /system/updates/check -X POST -o /dev/null
    wait_updates "d['available'] is not None and d['available']['version'] == '$second_version'" 120
    install_update online
  else
    step "installing $second_version from an uploaded file (no token for the online channel)"
    upload_update "$second" || die "the upload of $(basename "$second") was refused"
    install_update file
  fi
  wait_updates "d['installedVersion'] == '$second_version' and d['runningJobId'] is None and d['history'][0]['state'] == 'succeeded'" 1200
  echo "$second_version runs"

  broken_version="${second_version%.*}.$((${second_version##*.} + 1))"
  step "installing a broken $broken_version whose daemon never starts: the health check rolls back"
  make_update "$second" "$broken_version" "$vm/pithyx-$broken_version-amd64.pithyxupdate" "$HOME/.config/pithyx-dev/pithyx-beta-1.pem" pithyx-beta-1 broken
  upload_update "$vm/pithyx-$broken_version-amd64.pithyxupdate" || die "the upload of the broken file was refused"
  install_update file
  # The daemon of the broken version never answers; the health check gives up after five minutes and restarts.
  wait_updates "d['installedVersion'] == '$second_version' and d['runningJobId'] is None and d['history'][0]['toVersion'] == '$broken_version' and d['history'][0]['state'] == 'rolled_back'" 1500
  echo "the broken update rolled back by itself: $(admin /system/updates | json 'd["history"][0]["errorCode"]')"

  step "rolling back to $first_version through its GRUB entry and the recovery menu"
  status="$(admin /system/updates)"
  index="$(json "[i for i, s in enumerate(d['snapshots']) if s['version'] == '$first_version'][0] + 1" <<<"$status")"
  power_off
  start_vm "${installed[@]}"
  grub_choose "$index"
  # The menu of a snapshot's entry: 1 roll back, the snapshot's number, its version as the confirmation.
  sleep 90
  screenshot snapshot-menu
  type_keys $'1\n'
  sleep 2
  type_keys "$index"$'\n'
  sleep 2
  type_keys "$first_version"$'\n'
  wait_updates "d['installedVersion'] == '$first_version' and d['history'][0]['kind'] == 'rollback' and d['history'][0]['source'] == 'recovery'" 600
  echo "$first_version is back through GRUB key $index"

  step "setting a static address, then resetting the network and installing $second_version in the recovery menu"
  nic0="$(admin /system/network | nic_name "$NIC0_MAC" links)"
  config="$(admin /system/network | python3 -c "
import json, sys
config = json.load(sys.stdin)['config']
config['interfaces'] = [i for i in config['interfaces'] if i['name'] != '$nic0'] + [
    {'name': '$nic0', 'kind': 'ethernet', 'ipv4': 'static', 'ipv4Address': '10.0.2.15/24', 'ipv4Gateway': '10.0.2.2', 'ipv6': 'off'}]
config['global']['dnsServers'] = ['10.0.2.3']
print(json.dumps(config))")"
  change="$(admin /system/network -X PUT -H 'Content-Type: application/json' --data "$config")"
  admin "/system/network/changes/$(json 'd["id"]' <<<"$change")/confirm" -X POST -o /dev/null
  wait_network "any(l['name'] == '$nic0' and l['method'] == 'static' for l in d['links']) and d['pending'] is None" 60
  truncate -s 256M "$vm/usb.img"
  mformat -i "$vm/usb.img" -F ::
  mcopy -i "$vm/usb.img" "$second" ::/
  power_off
  start_vm "${installed[@]}" usb
  grub_choose r
  sleep 90
  screenshot recovery-menu
  type_keys $'3\n'
  sleep 2
  type_keys $'yes\n'
  sleep 5
  type_keys $'\n'
  sleep 3
  type_keys $'2\n'
  sleep 15
  screenshot recovery-usb
  type_keys $'1\n'
  # The check reads the whole file before it asks.
  sleep 60
  screenshot recovery-verified
  type_keys $'install\n'
  wait_updates "d['installedVersion'] == '$second_version' and d['runningJobId'] is None and d['history'][0]['source'] == 'recovery' and d['history'][0]['state'] == 'succeeded'" 1500
  wait_network "any(l['name'] == '$nic0' and l['method'] == 'dhcp' for l in d['links'])" 60
  echo "the recovery menu reset $nic0 to DHCP and installed $second_version from the stick"

  step "saving a fork's source file in Settings and switching to it in the recovery menu"
  fork_key="$(env -C "$vm" "$repo/tools/release/create-key.sh" fork-test-1 "$vm/fork.pem" | sed -n 's/^public key of fork-test-1: //p')"
  [[ -n "$fork_key" ]] || die "no fork key"
  # A source that is never reached: the switch is about trust, files come by upload.
  node "$repo/tools/release/update-file.ts" source --name "Test fork" \
    --url 'https://example.invalid/pithyx/channel-{channel}/channel.json' --key "fork-test-1:$fork_key:stable,beta" >"$vm/pithyx-source.json"
  admin /system/updates/source -X PUT -H 'Content-Type: application/json' --data-binary @"$vm/pithyx-source.json" -o /dev/null
  [[ "$(admin /system/updates | json 'd["sourceCandidate"]["name"]')" == "Test fork" ]] || die "the source file was not saved"
  recovery_menu $'5\n' $'2\n' $'Test fork\n' $'\n' $'4\n'
  wait_updates "d['source']['name'] == 'Test fork' and not d['source']['official'] and d['sourceCandidate'] is None" 600
  fork_version="${second_version%.*}.$((${second_version##*.} + 2))"
  make_update "$second" "$fork_version" "$vm/pithyx-$fork_version-amd64.pithyxupdate" "$vm/fork.pem" fork-test-1
  if upload_update "$vm/pithyx-$broken_version-amd64.pithyxupdate" 2>/dev/null; then
    die "a file of the project's beta key was accepted after the switch"
  fi
  upload_update "$vm/pithyx-$fork_version-amd64.pithyxupdate" || die "the fork's file was refused after the switch"
  admin /system/updates/file -X DELETE -o /dev/null
  [[ "$(admin /notifications | json "any(i['code'] == 'update.source.changed' for i in d['items'])")" == True ]] \
    || die "administrators were not told of the switch"
  echo "the box follows Test fork: its file verifies, the project's is refused, administrators were told"

  step "typing a source in the recovery menu, then going back to the Pithyx project"
  recovery_menu $'5\n' $'3\n' $'Typed fork\n' $'https://example.invalid/typed/channel-{channel}/channel.json\n' \
    $'fork-test-1\n' "$fork_key"$'\n' $'\n' $'\n' $'Typed fork\n' $'\n' $'4\n'
  wait_updates "d['source']['name'] == 'Typed fork' and d['source']['keys'][0]['publicKey'] == '$fork_key'" 600
  recovery_menu $'5\n' $'4\n' $'Pithyx\n' $'\n' $'4\n'
  wait_updates "d['source']['official']" 600
  upload_update "$vm/pithyx-$broken_version-amd64.pithyxupdate" || die "the project's file was refused after going back"
  admin /system/updates/file -X DELETE -o /dev/null
  echo "a typed source switched, and the box trusts the Pithyx project again"

  step "comparing both ESPs"
  power_off
  nbd_attach
  esp="$vm/esp"
  mkdir -p "$esp/1" "$esp/2"
  sudo mount -o ro /dev/nbd0p1 "$esp/1"
  sudo mount -o ro /dev/nbd1p1 "$esp/2"
  differences="$(sudo diff -r "$esp/1" "$esp/2" 2>&1 || true)"
  trials="$(sudo find "$esp/1" "$esp/2" -name pithyx.env | wc -l)"
  sudo umount "$esp/1" "$esp/2"
  nbd_detach
  [[ -z "$differences" ]] || die "the ESPs differ: $differences"
  [[ "$trials" -ge 2 ]] || die "pithyx.env is missing on an ESP"
  start_vm "${installed[@]}"
  check_installed
  echo "both ESPs carry the same GRUB and pithyx.env"
fi

step "checking that tty2 shows no login prompt"
monitor "sendkey ctrl-alt-f2"
sleep 3
tty2="$(drawn_pixels tty2)"
monitor "sendkey ctrl-alt-f1"
sleep 3
tty1="$(drawn_pixels tty1)"
((tty1 > 1000)) || die "tty1 shows no console menu ($tty1 pixels drawn); screenshots in $vm"
((tty2 < 200)) || die "tty2 shows something ($tty2 pixels drawn), a login prompt?; screenshots in $vm"
echo "tty2 is empty ($tty2 pixels drawn), tty1 shows the console menu ($tty1 pixels)"

if [[ "$reinstall" == true ]]; then
  step "creating the pool tank on disk3"
  disk3="$(admin /storage/disks | json "[x['disk']['id'] for x in d['disks'] if 'PITHYXVM03' in x['disk']['id']][0]")"
  storage_action "{\"action\": \"pool.create\", \"pool\": \"tank\", \"layout\": \"single\", \"diskIds\": [\"$disk3\"], \"encryption\": \"none\"}"
  echo "tank is on $disk3"

  step "reading the host id of the first installation"
  power_off
  first_hostid="$(installed_hostid)"
  echo "host id $first_hostid in /etc/hostid and the initramfs"

  step "installing again onto the same disk"
  # A fresh UEFI store, so the VM boots the stick and not the first installation.
  cp "$OVMF_VARS" "$vm/vars.fd"
  install_box
  check_installed

  step "reading the host id of the second installation"
  power_off
  second_hostid="$(installed_hostid)"
  [[ "$second_hostid" != "$first_hostid" ]] || die "both installations have host id $first_hostid"
  echo "host id $second_hostid, the first installation had $first_hostid"
  start_vm "${installed[@]}"
  check_installed

  step "importing tank with the forced import"
  scan="$(admin /storage/import/scan -X POST)"
  [[ "$(json "[(p['needsForce'], p['allowed']) for p in d['pools'] if p['name'] == 'tank']" <<<"$scan")" == "[(True, True)]" ]] \
    || die "tank is not offered as a pool of another system: $scan"
  guid="$(json "[p['poolGuid'] for p in d['pools'] if p['name'] == 'tank'][0]" <<<"$scan")"
  storage_action "{\"action\": \"pool.import.readonly\", \"pool\": \"tank\", \"poolGuid\": \"$guid\", \"force\": true, \"confirmName\": \"tank\"}"
  storage_action '{"action": "pool.import", "pool": "tank", "force": true, "confirmName": "tank"}'
  [[ "$(admin /storage/pools | json "[(p['state'], p['readOnly']) for p in d['pools'] if p['name'] == 'tank']")" == "[('ONLINE', False)]" ]] \
    || die "tank is not imported: $(admin /storage/pools)"
  echo "tank from the first installation is imported and ONLINE"
fi

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
