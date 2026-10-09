#!/bin/sh
# Runs inside the throwaway chroot of tools/release/build-debs.sh after zfs-dkms built the module: collects the module
# files and the versions of the kernel and ZFS in /tmp/pithyx-kernel, which the build copies out.
set -eu
kver="$(ls /usr/lib/modules)"
[ "$(echo "$kver" | wc -w)" = 1 ] || { echo "expected one kernel, got $kver" >&2; exit 1; }
built="/usr/lib/modules/$kver/updates/dkms"
ls "$built"/zfs.ko* >/dev/null 2>&1 || { echo "zfs-dkms built no module for $kver" >&2; exit 1; }
mkdir -p /tmp/pithyx-kernel/modules
cp "$built"/*.ko* /tmp/pithyx-kernel/modules/
{
  echo "KVER=$kver"
  echo "KERNEL_VERSION=$(dpkg-query -W -f '${Version}' "linux-image-$kver")"
  echo "ZFS_VERSION=$(dpkg-query -W -f '${Version}' zfsutils-linux)"
} >/tmp/pithyx-kernel/versions.env
