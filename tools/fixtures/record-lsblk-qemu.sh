#!/usr/bin/env bash
# Records `lsblk -J -b -O` fixtures for disks behind real Linux drivers without the hardware: a throwaway QEMU VM with
# Debian 13 (the image's snapshot and kernel, with udev) sees one data disk on an emulated AHCI (SATA), NVMe or USB
# (xhci, usb-storage) controller, runs lsblk exactly as the daemon does and powers off. Model and vendor are QEMU's;
# transport, rotation, WWN, removable flag and the udev fields come from the guest kernel as on hardware.
#
#   tools/fixtures/record-lsblk-qemu.sh [sata|nvme|usb ...]
#
# Writes daemon/tests/fixtures/commands/lsblk-qemu-<case>/lsblk.json and lsblk.stdout (through anonymize.ts, like
# record.sh). Needs mmdebstrap (unshare mode, subuid range), e2fsprogs 1.47.1 or later, qemu-system-x86 and /dev/kvm.
set -euo pipefail
export LC_ALL=C.UTF-8
# mke2fs lives in /usr/sbin, which is not in a normal user's PATH on Debian.
export PATH="$PATH:/usr/sbin:/sbin"

die() {
  echo "record-lsblk-qemu: $*" >&2
  exit 1
}

repo="$(git rev-parse --show-toplevel)"
# shellcheck source=/dev/null
source "$repo/image/debian.env"
cases=("$@")
[[ ${#cases[@]} -gt 0 ]] || cases=(sata nvme usb)
for name in "${cases[@]}"; do
  [[ "$name" =~ ^(sata|nvme|usb)$ ]] || die "unknown case $name; use sata, nvme or usb"
done
for tool in mmdebstrap mke2fs qemu-system-x86_64 node; do
  command -v "$tool" >/dev/null || die "$tool not found"
done
[[ -w /dev/kvm ]] || die "/dev/kvm is not writable for $(id -un)"

work="$(mktemp -d "${TMPDIR:-/tmp}/pithyx-lsblk.XXXXXX")"
trap 'rm -rf "$work"' EXIT
debian="http://snapshot.debian.org/archive/debian/$SNAPSHOT"

# The guest waits for udev, prints lsblk onto its second serial port and powers off.
cat >"$work/record-lsblk.service" <<'EOF'
[Unit]
Description=Print lsblk to the second serial port
After=systemd-udev-settle.service
Wants=systemd-udev-settle.service

[Service]
Type=oneshot
ExecStart=/bin/sh -c 'stty -F /dev/ttyS1 raw -echo && lsblk -J -b -O >/dev/ttyS1'
ExecStartPost=/bin/systemctl poweroff

[Install]
WantedBy=multi-user.target
EOF

echo "root file system (Debian $SUITE, snapshot $SNAPSHOT)"
# shellcheck disable=SC2016
mmdebstrap --mode=unshare --variant=minbase --architectures="$ARCH" \
  --include=systemd-sysv,udev,util-linux,kmod,initramfs-tools,linux-image-amd64 \
  --aptopt='Acquire::Check-Valid-Until "false"' \
  --aptopt='Acquire::Retries "5"' \
  --customize-hook="copy-in '$work/record-lsblk.service' /etc/systemd/system" \
  --customize-hook='chroot "$1" systemctl enable record-lsblk.service' \
  --customize-hook='echo pithyx-lsblk >"$1/etc/hostname"' \
  --customize-hook='mkdir -p "$1/out" && cp -L "$1"/boot/vmlinuz-* "$1/out/vmlinuz" && cp -L "$1"/boot/initrd.img-* "$1/out/initrd.img"' \
  --customize-hook="sync-out /out '$work/boot'" \
  "$SUITE" "$work/rootfs.tar" "deb $debian $SUITE main"
mke2fs -q -t ext4 -d "$work/rootfs.tar" "$work/root.img" 2G
truncate -s 8G "$work/data.img"

for name in "${cases[@]}"; do
  # QEMU options are comma lists.
  # shellcheck disable=SC2054
  case "$name" in
    sata) data=(-device ahci,id=ahci -drive if=none,id=data,file="$work/data.img",format=raw
      -device ide-hd,drive=data,bus=ahci.0,serial=PITHYXSATA01,wwn=0x5000c500a1b2c3d4,rotation_rate=7200) ;;
    nvme) data=(-drive if=none,id=data,file="$work/data.img",format=raw -device nvme,drive=data,serial=PITHYXNVME01) ;;
    usb) data=(-device qemu-xhci,id=xhci -drive if=none,id=data,file="$work/data.img",format=raw
      -device usb-storage,bus=xhci.0,drive=data,serial=PITHYXUSB01) ;;
  esac
  echo "booting with a $name disk"
  rm -f "$work/lsblk.out"
  timeout 300 qemu-system-x86_64 -machine q35,accel=kvm -cpu host -m 1024 -nodefaults -display none \
    -kernel "$work/boot/vmlinuz" -initrd "$work/boot/initrd.img" -append "root=/dev/vda rw console=ttyS0 quiet" \
    -drive if=virtio,file="$work/root.img",format=raw \
    "${data[@]}" \
    -serial file:"$work/console-$name.log" -serial file:"$work/lsblk.out" \
    || die "the VM with the $name disk did not power off; console in $work/console-$name.log"
  node -e 'JSON.parse(require("node:fs").readFileSync(process.argv[1], "utf8"))' "$work/lsblk.out" \
    || die "no lsblk JSON from the $name VM"

  dir="$repo/daemon/tests/fixtures/commands/lsblk-qemu-$name"
  mkdir -p "$dir"
  cp "$work/lsblk.out" "$dir/lsblk.stdout"
  node "$repo/tools/fixtures/anonymize.ts" "$dir/lsblk.stdout"
  printf '{\n  "command": "lsblk",\n  "args": [\n    "-J",\n    "-b",\n    "-O"\n  ],\n  "exitCode": 0\n}\n' >"$dir/lsblk.json"
  echo "recorded lsblk-qemu-$name/lsblk"
done
