#!/usr/bin/env bash
# Installs Pithyx from the ISO into a throwaway QEMU VM through the installer's API, the way the installer page does,
# then starts the VM again without the USB stick and checks the installed system: it answers its install id on port 80
# and asks for the sign-in, not the setup. Without root; needs qemu-system-x86, qemu-utils, ovmf and /dev/kvm.
#
#   image/test-install.sh [--iso <file>] [--mirror] [--network] [--update <folder>] [--keep]
#
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
# virtual USB stick. Afterwards both ESPs carry the same GRUB and the trial file of the updater, and the mirror starts
# from its second disk alone.
set -euo pipefail
export LC_ALL=C.UTF-8

repo="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
iso=""
mirror=false
network=false
updates=""
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
    --network) network=true; shift ;;
    --update) updates="$(realpath "$2")"; mirror=true; shift 2 ;;
    --keep) keep=true; shift ;;
    -h | --help) sed -n '2,24p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'; exit 0 ;;
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
  for drive in "$@"; do
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
    -netdev user,id=net0,hostfwd=tcp:127.0.0.1:"$port"-:7484,hostfwd=tcp:127.0.0.1:"$plain"-:80 \
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
  printf '# The QEMU test reads and picks GRUB entries on the serial console.\nGRUB_TERMINAL=console\nGRUB_TIMEOUT=10\n' \
    | sudo tee "$root/etc/default/grub.d/99-pithyx-test.cfg" >/dev/null
  sudo umount "$root"
  nbd_detach
}

# Waits until a Python expression over GET /api/system/updates is true; the box restarts in between.
wait_updates() {
  local expression="$1" seconds="$2" deadline=$((SECONDS + $2)) state=""
  until state="$(admin /system/updates 2>/dev/null)" && [[ "$(json "$expression" <<<"$state")" == True ]]; do
    ((SECONDS < deadline)) || die "the updater did not reach ($expression) within ${seconds}s: $(head -c 800 <<<"$state")"
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

# An update file made from another whose daemon never starts, for the health check's rollback.
make_broken() {
  local source="$1" version="$2" target="$3" work="$vm/broken" deb_version="${2/-/\~}" deb
  rm -rf "$work"
  mkdir -p "$work/file" "$work/pithyx" "$work/kernel"
  tar --extract --file="$source" --directory="$work/file"
  dpkg-deb -R "$(ls "$work/file/debs"/pithyx_*.deb)" "$work/pithyx"
  dpkg-deb -R "$(ls "$work/file/debs"/pithyx-kernel_*.deb)" "$work/kernel"
  rm "$work/file/debs"/pithyx_*.deb "$work/file/debs"/pithyx-kernel_*.deb "$work/file/manifest.json.sig"
  sed -i -e "s/^Version: .*/Version: $deb_version/" -e "s/pithyx-kernel (= [^)]*)/pithyx-kernel (= $deb_version)/" "$work/pithyx/DEBIAN/control"
  sed -i "s/^Version: .*/Version: $deb_version/" "$work/kernel/DEBIAN/control"
  sed -i 's|^ExecStart=.*|ExecStart=/bin/false|' "$work/pithyx/usr/lib/systemd/system/pithyx.service"
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
  "$repo/tools/release/sign-update.sh" "$target" --key "$HOME/.config/pithyx-dev/pithyx-beta-1.pem" --key-id pithyx-beta-1 >/dev/null
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

# Waits for GRUB's menu on the serial console and presses an entry's key there.
grub_choose() {
  local key="$1" deadline=$((SECONDS + 180))
  until grep -aq "Pithyx recovery" "$serial_log" 2>/dev/null; do
    ((SECONDS < deadline)) || die "GRUB's menu did not appear on the serial console ($serial_log)"
    sleep 1
  done
  sleep 1
  python3 - "$vm/serial.sock" "$key" <<'PY2'
import socket, sys
connection = socket.socket(socket.AF_UNIX)
connection.connect(sys.argv[1])
connection.sendall(sys.argv[2].encode())
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
      ' ') key=spc ;;
      $'\n') key=ret ;;
      *) key="$char" ;;
    esac
    monitor "sendkey $key"
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
 "keyboardLayout": "de", "diskIds": $chosen, "username": "admin", "password": "correct horse battery staple",
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
