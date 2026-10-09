#!/usr/bin/env bash
# Builds the Pithyx installation ISO (D294, D296) without root and without Docker.
#
#   image/build.sh [--version <version>] [--out <dir>] [--work <dir>] [--mirror <url>] [--no-app-build] [--reuse-rootfs]
#
# The root file system comes from mmdebstrap in unshare mode (subuid and subgid ranges for the user are needed), from
# Debian 13 pinned to the snapshot.debian.org date of image/debian.env, so a build is reproducible. Pithyx itself comes
# from the packages of tools/release/build-debs.sh (pithyx, pithyx-kernel and the ZFS module), which this script builds
# first, so the image and an update file (tools/release/build-update.sh) install the same thing. The root file system
# is packed as a squashfs that the live system boots and the installer later unpacks onto the system disks. The ISO is
# UEFI only: a FAT image with a standalone GRUB is appended as an EFI system partition, so the same file works on a USB
# stick and as a virtual CD. Next to the ISO it writes pithyx-<version>-amd64.packages (every installed package with
# its version, the base an update file is built against) and keeps the downloaded Debian packages in <work>/archives.
#
# --version overrides the version of daemon/Directory.Build.props (see build-debs.sh). --mirror replaces the snapshot
# with another Debian mirror (for example http://deb.debian.org/debian), which is faster but no longer reproducible.
# --no-app-build packs the daemon and desktop already built by an earlier run, and --reuse-rootfs packs the root file
# system of an earlier run again (for work on the boot steps only).
# Packages needed on the build host: mmdebstrap uidmap squashfs-tools xorriso mtools dosfstools grub-efi-amd64-bin
# dpkg-dev, plus the .NET SDK and pnpm for the daemon and desktop (tools/dev-vm/provision.sh installs them on dev01).
set -euo pipefail
export LC_ALL=C.UTF-8

repo="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
# shellcheck source=/dev/null
source "$repo/image/debian.env"
version=""
out="$repo/image/out"
work=""
mirror=""
app_build=1
rootfs_build=1

die() {
  echo "build: $*" >&2
  exit 1
}

step() {
  echo "==> $*"
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --out) out="$(realpath -m "$2")"; shift 2 ;;
    --work) work="$(realpath -m "$2")"; shift 2 ;;
    --version) version="$2"; shift 2 ;;
    --mirror) mirror="$2"; shift 2 ;;
    --no-app-build) app_build=0; shift ;;
    --reuse-rootfs) rootfs_build=0; app_build=0; shift ;;
    -h | --help) sed -n '2,21p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'; exit 0 ;;
    *) die "unknown argument $1" ;;
  esac
done
work="${work:-$out/work}"

for tool in mmdebstrap newuidmap sqfstar xorriso mcopy mmd grub-mkstandalone; do
  command -v "$tool" >/dev/null || die "$tool not found; install mmdebstrap uidmap squashfs-tools xorriso mtools grub-efi-amd64-bin"
done
mkfs_fat="$(command -v mkfs.fat || echo /usr/sbin/mkfs.fat)"
[[ -x "$mkfs_fat" ]] || die "mkfs.fat not found; install dosfstools"
grep -q "^$(id -un):" /etc/subuid || die "no subuid range for $(id -un); mmdebstrap --mode=unshare needs one (usermod --add-subuids)"

version="${version:-$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$repo/daemon/Directory.Build.props")}"
[[ -n "$version" ]] || die "no <Version> in daemon/Directory.Build.props"
commit="$(git -C "$repo" rev-parse --short=12 HEAD)"
# The commit time makes every timestamp in the image the same for the same commit.
SOURCE_DATE_EPOCH="$(git -C "$repo" log -1 --format=%ct)"
export SOURCE_DATE_EPOCH
iso="$out/pithyx-$version-$ARCH.iso"

if [[ -n "$mirror" ]]; then
  debian="$mirror"
  security="${mirror%/debian}/debian-security"
  echo "build: $mirror instead of the snapshot $SNAPSHOT; this build is not reproducible" >&2
else
  debian="http://snapshot.debian.org/archive/debian/$SNAPSHOT"
  security="http://snapshot.debian.org/archive/debian-security/$SNAPSHOT"
fi

mkdir -p "$out" "$work"
exec 9>"$work/.lock"
flock --nonblock 9 || die "another build uses $work"
rm -rf "$work/staging" "$work/efi.img"
if [[ "$rootfs_build" == 1 ]]; then
  rm -f "$work/rootfs.tar"
fi

if [[ "$rootfs_build" == 1 ]]; then
  debs_options=(--version "$version" --out "$out" --work "$work")
  [[ "$app_build" == 1 ]] || debs_options+=(--no-app-build)
  "$repo/tools/release/build-debs.sh" "${debs_options[@]}"
  debs_version="$(sed -n 's/^VERSION=//p' "$out/debs/debs.env")"
  kver="$(sed -n 's/^KVER=//p' "$out/debs/debs.env")"
  [[ "$debs_version" == "$version" && -n "$kver" ]] || die "the packages in $out/debs are not $version"
  # The packages pithyx depends on and its kernel come in with mmdebstrap; the three Pithyx packages are local files,
  # installed by apt inside the root file system once it exists.
  packages="$(grep -v '^\s*#' "$repo/image/deb/pithyx/depends" | tr -s ' \n' ',' | sed 's/^,//; s/,$//'),linux-image-$kver"
  rm -rf "$work/archives" "$work/manifest"
  step "root file system ($SUITE, ${mirror:-snapshot $SNAPSHOT})"
  # $1 in the single-quoted hooks is the root directory; mmdebstrap expands it, not this shell. The Debian packages
  # stay in apt's cache until the cleanup (essential/unlink skipped), so the hooks can copy them out for update files.
  # shellcheck disable=SC2016
  mmdebstrap --mode=unshare --variant=apt --architectures="$ARCH" \
    --components="main contrib non-free-firmware" \
    --include="$packages" \
    --skip=essential/unlink \
    --aptopt='Acquire::Check-Valid-Until "false"' \
    --aptopt='Acquire::Retries "5"' \
    --customize-hook='mkdir -p "$1/tmp/pithyx/rootfs" "$1/tmp/pithyx/debs" "$1/tmp/pithyx-out"' \
    --customize-hook="sync-in '$repo/image/rootfs' /tmp/pithyx/rootfs" \
    --customize-hook="sync-in '$out/debs' /tmp/pithyx/debs" \
    --customize-hook='chroot "$1" sh -c "DEBIAN_FRONTEND=noninteractive apt-get install --yes --no-install-recommends /tmp/pithyx/debs/*.deb"' \
    --customize-hook='chroot "$1" dpkg-query --show --showformat="\${Package}\t\${Version}\t\${Architecture}\n" >"$1/tmp/pithyx-out/packages"' \
    --customize-hook="sync-out /tmp/pithyx-out '$work/manifest'" \
    --customize-hook="sync-out /var/cache/apt/archives '$work/archives'" \
    --customize-hook="copy-in '$repo/image/customize.sh' /tmp/pithyx" \
    --customize-hook="chroot \"\$1\" /bin/bash /tmp/pithyx/customize.sh '$version' '$commit'" \
    "$SUITE" "$work/rootfs.tar" \
    "deb $debian $SUITE main contrib non-free-firmware" \
    "deb $debian $SUITE-updates main contrib non-free-firmware" \
    "deb $security $SUITE-security main contrib non-free-firmware"
  LC_ALL=C sort "$work/manifest/packages" >"$out/pithyx-$version-$ARCH.packages"
fi
[[ -f "$work/rootfs.tar" ]] || die "no root file system in $work; run without --reuse-rootfs"

step "squashfs"
mkdir -p "$work/staging/live"
sqfstar -quiet -comp zstd -Xcompression-level 19 -b 1M -no-xattrs -force "$work/staging/live/filesystem.squashfs" <"$work/rootfs.tar"

# The kernel and the live initramfs are read from outside the squashfs.
# One listing: grep -q on a pipe from tar would end it early, and pipefail would count the broken pipe as a failure.
tar -tf "$work/rootfs.tar" >"$work/rootfs.list"
kernel="$(grep -E '^\./boot/vmlinuz-' "$work/rootfs.list" | sort -V | tail -n1)"
[[ -n "$kernel" ]] || die "no kernel in the root file system"
kernel_version="${kernel#./boot/vmlinuz-}"
tar -xOf "$work/rootfs.tar" "$kernel" >"$work/staging/live/vmlinuz"
tar -xOf "$work/rootfs.tar" "./boot/initrd.img-$kernel_version" >"$work/staging/live/initrd.img"
grep -qF "./usr/lib/modules/$kernel_version/updates/zfs/zfs.ko" "$work/rootfs.list" \
  || die "no ZFS module for $kernel_version in the root file system"

step "EFI boot image"
marker="pithyx-$version-$commit"
touch "$work/staging/.$marker"
sed -e "s/@MARKER@/$marker/g" -e "s/@VERSION@/$version/g" "$repo/image/grub/grub.cfg" >"$work/grub.cfg"
grub-mkstandalone --format=x86_64-efi --output="$work/BOOTX64.EFI" --locales="" --fonts="" \
  --modules="part_gpt part_msdos fat iso9660 search search_fs_file linux normal configfile echo test all_video gfxterm efifwsetup" \
  "boot/grub/grub.cfg=$work/grub.cfg"
truncate -s 8M "$work/efi.img"
"$mkfs_fat" -n PITHYX-EFI -i "${SOURCE_DATE_EPOCH: -8}" "$work/efi.img" >/dev/null
mmd -i "$work/efi.img" ::/EFI ::/EFI/BOOT
mcopy -i "$work/efi.img" -m "$work/BOOTX64.EFI" ::/EFI/BOOT/BOOTX64.EFI

step "ISO"
rm -f "$iso"
xorriso -as mkisofs -quiet -iso-level 3 -full-iso9660-filenames -joliet -joliet-long -rational-rock \
  -volid PITHYX -appid "Pithyx $version" -publisher "Pithyx" \
  -partition_offset 16 \
  -append_partition 2 C12A7328-F81F-11D2-BA4B-00A0C93EC93B "$work/efi.img" -appended_part_as_gpt \
  -e --interval:appended_partition_2:all:: -no-emul-boot \
  -output "$iso" "$work/staging"
(cd "$out" && sha256sum "$(basename "$iso")" >"$(basename "$iso").sha256")

step "done: $iso ($(du -h "$iso" | cut -f1), kernel $kernel_version)"
