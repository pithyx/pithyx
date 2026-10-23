#!/usr/bin/env bash
# Builds the Debian packages of a Pithyx release (D338), which image/build.sh installs into the image and
# tools/release/build-update.sh puts into an update file, so the image and an update are the same thing:
#
#   pithyx_<version>_amd64.deb                   the daemon, the desktop, the console, the units, apt pinning
#   pithyx-kernel_<version>_amd64.deb            pins the kernel and the ZFS module built for exactly that kernel
#   pithyx-zfs-modules-<kernel>_<zfs>_amd64.deb  the ZFS module, built by zfs-dkms here instead of on the box
#
#   tools/release/build-debs.sh [--version <version>] [--out <dir>] [--work <dir>] [--no-app-build] [--rebuild-kernel]
#
# --version overrides the version of daemon/Directory.Build.props (for example 1.0.0-beta.2 for an update test); the
# Debian version replaces the first "-" with "~", so a prerelease sorts before its release. The packages land in
# <out>/debs (default image/out/debs) with debs.env, which names the kernel and the versions. The kernel and the ZFS
# module come from the Debian snapshot of image/debian.env; the built module is kept in <work>/kernel-<snapshot> and
# reused until --rebuild-kernel. --no-app-build packs the daemon and desktop of an earlier run. Timestamps come from
# the commit (SOURCE_DATE_EPOCH), so the same commit gives the same packages. Needs mmdebstrap, uidmap, dpkg-dev and
# the .NET SDK and pnpm (tools/dev-vm/provision.sh installs them on a dev VM).
set -euo pipefail
export LC_ALL=C.UTF-8

repo="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
# shellcheck source=/dev/null
source "$repo/image/debian.env"
out="$repo/image/out"
work=""
version=""
app_build=1
kernel_build=0

die() {
  echo "build-debs: $*" >&2
  exit 1
}

step() {
  echo "==> $*"
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --version) version="$2"; shift 2 ;;
    --out) out="$(realpath -m "$2")"; shift 2 ;;
    --work) work="$(realpath -m "$2")"; shift 2 ;;
    --no-app-build) app_build=0; shift ;;
    --rebuild-kernel) kernel_build=1; shift ;;
    -h | --help) sed -n '2,19p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'; exit 0 ;;
    *) die "unknown argument $1" ;;
  esac
done
work="${work:-$out/work}"
debs="$out/debs"

for tool in mmdebstrap dpkg-deb; do
  command -v "$tool" >/dev/null || die "$tool not found; install mmdebstrap uidmap dpkg-dev"
done

version="${version:-$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$repo/daemon/Directory.Build.props")}"
[[ "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.-]+)?$ ]] || die "version $version is not a SemVer version"
deb_version="${version/-/\~}"
commit="$(git -C "$repo" rev-parse --short=12 HEAD)"
SOURCE_DATE_EPOCH="$(git -C "$repo" log -1 --format=%ct)"
export SOURCE_DATE_EPOCH

mkdir -p "$work" "$debs"
# dash (apt's hook shell) only takes fds 0 to 9: keep the lock fds out of mmdebstrap, or apt's status fd becomes 10.
mmdebstrap() { command mmdebstrap "$@" 8>&- 9>&-; }

exec 8>"$work/.debs.lock"
flock --nonblock 8 || die "another build of the packages uses $work"

if [[ "$app_build" == 1 ]]; then
  step "daemon $version ($commit)"
  rm -rf "$work/daemon"
  # The compiler server and MSBuild nodes outlive the build; without the lock fds they no longer hold the locks.
  dotnet publish "$repo/daemon/src/Pithyx.Daemon" --configuration Release --runtime "$DOTNET_RID" --self-contained \
    --nologo --verbosity quiet -p:Version="$version" -p:SourceRevisionId="$commit" --output "$work/daemon" 8>&- 9>&-
  step "desktop"
  [[ -d "$repo/node_modules" ]] || pnpm --dir "$repo" install --frozen-lockfile
  env -C "$repo/desktop" pnpm exec vite build --logLevel warn
  rm -rf "$work/desktop"
  cp -a "$repo/desktop/dist" "$work/desktop"
fi
[[ -x "$work/daemon/pithyxd" && -f "$work/desktop/index.html" ]] || die "no daemon or desktop build in $work; run without --no-app-build"

kernel="$work/kernel-$SNAPSHOT"
if [[ "$kernel_build" == 1 || ! -f "$kernel/versions.env" ]]; then
  step "kernel and ZFS module (snapshot $SNAPSHOT)"
  rm -rf "$kernel"
  mkdir -p "$kernel"
  # $1 in the single-quoted hook is the root directory; mmdebstrap expands it, not this shell.
  # shellcheck disable=SC2016
  mmdebstrap --mode=unshare --variant=apt --architectures="$ARCH" --format=null \
    --components="main contrib" \
    --include="linux-image-amd64,linux-headers-amd64,zfs-dkms,zfsutils-linux" \
    --aptopt='Acquire::Check-Valid-Until "false"' \
    --aptopt='Acquire::Retries "5"' \
    --customize-hook="copy-in '$repo/image/deb/pithyx-zfs-modules/collect.sh' /tmp" \
    --customize-hook='chroot "$1" sh /tmp/collect.sh' \
    --customize-hook="sync-out /tmp/pithyx-kernel '$kernel'" \
    "$SUITE" /dev/null \
    "deb http://snapshot.debian.org/archive/debian/$SNAPSHOT $SUITE main contrib" \
    "deb http://snapshot.debian.org/archive/debian/$SNAPSHOT $SUITE-updates main contrib" \
    "deb http://snapshot.debian.org/archive/debian-security/$SNAPSHOT $SUITE-security main contrib"
fi
# shellcheck source=/dev/null
source "$kernel/versions.env"
[[ -n "${KVER:-}" && -n "${KERNEL_VERSION:-}" && -n "${ZFS_VERSION:-}" ]] || die "no kernel versions in $kernel; run with --rebuild-kernel"

# Fills the placeholders of a control file or maintainer script.
render() {
  sed -e "s/@VERSION@/$deb_version/g" -e "s/@KVER@/$KVER/g" -e "s/@KERNEL_VERSION@/$KERNEL_VERSION/g" \
    -e "s/@ZFS_VERSION@/$ZFS_VERSION/g" -e "s/@DEPENDS@/${depends:-}/g" "$1"
}

# Packs a staged tree reproducibly: fixed modes and timestamps, root as owner, files in name order.
pack() {
  local tree="$1" file="$2"
  find "$tree" -type d -exec chmod 0755 {} +
  find "$tree" -type f ! -perm -u+x -exec chmod 0644 {} +
  find "$tree" -type f -perm -u+x -exec chmod 0755 {} +
  find "$tree" -exec touch --no-dereference --date="@$SOURCE_DATE_EPOCH" {} +
  dpkg-deb --root-owner-group -Zxz --build "$tree" "$file" >/dev/null
}

rm -f "$debs"/*.deb
stage="$work/debs-stage"

step "pithyx $deb_version"
rm -rf "$stage"
mkdir -p "$stage/pithyx/DEBIAN" "$stage/pithyx/usr/lib/pithyx"
# Everything of image/rootfs except what belongs to the image alone: /etc/issue is base-files', the console font is
# console-setup's, and the environment is written by the image (prod) or by hand (dev).
cp -r --no-preserve=ownership "$repo/image/rootfs/." "$stage/pithyx/"
rm -f "$stage/pithyx/etc/issue" "$stage/pithyx/etc/default/console-setup" "$stage/pithyx/etc/pithyx/environment"
find "$stage/pithyx/etc" -depth -type d -empty -delete
cp -r --no-preserve=ownership "$work/daemon" "$stage/pithyx/usr/lib/pithyx/daemon"
cp -r --no-preserve=ownership "$work/desktop" "$stage/pithyx/usr/lib/pithyx/desktop"
printf 'PITHYX_VERSION=%s\nPITHYX_COMMIT=%s\n' "$version" "$commit" >"$stage/pithyx/usr/lib/pithyx/release"
depends="$(grep -v '^\s*#' "$repo/image/deb/pithyx/depends" | tr -s ' \n' '\n' | sed '/^$/d' | paste -sd, | sed 's/,/, /g')"
render "$repo/image/deb/pithyx/control" >"$stage/pithyx/DEBIAN/control"
install -m 0755 "$repo/image/deb/pithyx/postinst" "$stage/pithyx/DEBIAN/postinst"
(cd "$stage/pithyx" && find etc -type f | sort | sed 's:^:/:') >"$stage/pithyx/DEBIAN/conffiles"
pack "$stage/pithyx" "$debs/pithyx_${deb_version}_$ARCH.deb"

step "pithyx-kernel $deb_version (linux-image-$KVER $KERNEL_VERSION, ZFS $ZFS_VERSION)"
mkdir -p "$stage/pithyx-kernel/DEBIAN"
render "$repo/image/deb/pithyx-kernel/control" >"$stage/pithyx-kernel/DEBIAN/control"
pack "$stage/pithyx-kernel" "$debs/pithyx-kernel_${deb_version}_$ARCH.deb"

step "pithyx-zfs-modules-$KVER $ZFS_VERSION"
modules="$stage/pithyx-zfs-modules/usr/lib/modules/$KVER/updates/zfs"
mkdir -p "$stage/pithyx-zfs-modules/DEBIAN" "$modules"
cp --no-preserve=ownership "$kernel/modules/"*.ko* "$modules/"
render "$repo/image/deb/pithyx-zfs-modules/control" >"$stage/pithyx-zfs-modules/DEBIAN/control"
for script in postinst postrm; do
  render "$repo/image/deb/pithyx-zfs-modules/$script" >"$stage/pithyx-zfs-modules/DEBIAN/$script"
  chmod 0755 "$stage/pithyx-zfs-modules/DEBIAN/$script"
done
pack "$stage/pithyx-zfs-modules" "$debs/pithyx-zfs-modules-${KVER}_${ZFS_VERSION}_$ARCH.deb"
rm -rf "$stage"

cat >"$debs/debs.env" <<EOF
VERSION=$version
DEB_VERSION=$deb_version
COMMIT=$commit
KVER=$KVER
KERNEL_VERSION=$KERNEL_VERSION
ZFS_VERSION=$ZFS_VERSION
EOF
(cd "$debs" && sha256sum ./*.deb)
step "done: $debs"
