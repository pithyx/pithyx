#!/usr/bin/env bash
# Builds the update file of a Pithyx version (D338) from the image build of that version:
#
#   tools/release/build-update.sh [--version <version>] [--channel beta|stable] [--minimum <version>]
#                                 [--since <packages file> | --full] [--out <dir>] [--work <dir>]
#
# Run image/build.sh for the version first: the update takes its packages from there, so an update installs exactly
# what the image of the same version contains. The file pithyx-<version>-amd64.pithyxupdate is a tar (deterministic:
# sorted, root as owner, the commit's time) with manifest.json first, then debs/ with the packages, Packages and
# Release, which the box reads as a local apt repository. It carries pithyx, pithyx-kernel, the ZFS module and every
# Debian package of the image whose version differs from the packages file of --minimum (default: the version itself
# for --full, else 1.0.0-beta.1), written by image/build.sh as pithyx-<version>-amd64.packages and kept for every release in
# image/baseline/. Snapshot dates only move forward, so a box of any version from --minimum on already has every
# package the file leaves out, and one file updates every such box without the versions between. --since names the
# packages file directly; --full puts every package of the image in (a large file that needs no baseline).
#
# The file is unsigned: a dev file that only a box whose /etc/pithyx/environment says dev installs. Sign it with
# tools/release/sign-update.sh. --channel (default beta) is written into the manifest; a box on the stable channel
# installs only stable files. Debian security updates are not part of the file; the box gets them from Debian.
# --out is the folder of the image build (default image/out): the packages come from its debs/ and the file goes there.
set -euo pipefail
export LC_ALL=C.UTF-8

repo="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
# shellcheck source=/dev/null
source "$repo/image/debian.env"
out="$repo/image/out"
work=""
version=""
channel=beta
minimum=""
since=""
full=false

die() {
  echo "build-update: $*" >&2
  exit 1
}

step() {
  echo "==> $*"
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --version) version="$2"; shift 2 ;;
    --channel) channel="$2"; shift 2 ;;
    --minimum) minimum="$2"; shift 2 ;;
    --since) since="$(realpath "$2")"; shift 2 ;;
    --full) full=true; shift ;;
    --out) out="$(realpath -m "$2")"; shift 2 ;;
    --work) work="$(realpath -m "$2")"; shift 2 ;;
    -h | --help) sed -n '2,22p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'; exit 0 ;;
    *) die "unknown argument $1" ;;
  esac
done
work="${work:-$out/work}"
[[ "$channel" == beta || "$channel" == stable ]] || die "--channel must be beta or stable"
for tool in dpkg-scanpackages node tar sha256sum; do
  command -v "$tool" >/dev/null || die "$tool not found; install dpkg-dev and node"
done

debs="$out/debs"
[[ -f "$debs/debs.env" ]] || die "no packages in $debs; run image/build.sh first"
built_version="$(sed -n 's/^VERSION=//p' "$debs/debs.env")"
version="${version:-$built_version}"
[[ "$built_version" == "$version" ]] || die "the packages in $debs are $built_version, not $version; run image/build.sh --version $version"
commit="$(sed -n 's/^COMMIT=//p' "$debs/debs.env")"
kver="$(sed -n 's/^KVER=//p' "$debs/debs.env")"
zfs="$(sed -n 's/^ZFS_VERSION=//p' "$debs/debs.env")"
image_packages="$out/pithyx-$version-$ARCH.packages"
[[ -f "$image_packages" && -d "$work/archives" ]] || die "no image build of $version in $out; run image/build.sh --version $version"

if [[ "$full" == true ]]; then
  minimum="${minimum:-$version}"
  since=/dev/null
else
  minimum="${minimum:-1.0.0-beta.1}"
  since="${since:-$repo/image/baseline/pithyx-$minimum-$ARCH.packages}"
  [[ -f "$since" ]] || die "no packages file of $minimum ($since); pass --since <file> or --full"
fi

file="$out/pithyx-$version-$ARCH.pithyxupdate"
stage="$work/update-stage"
rm -rf "$stage" "$file"
mkdir -p "$stage/debs"

step "packages of $version that a box of $minimum lacks"
# Every package of the image whose name and version the baseline does not list; the Pithyx packages come from $debs.
count=0
while IFS=$'\t' read -r name package_version package_arch; do
  case "$name" in
    pithyx | pithyx-kernel | pithyx-zfs-modules-*) continue ;;
  esac
  # apt saves an epoch's colon as %3a.
  deb="$work/archives/${name}_${package_version//:/%3a}_$package_arch.deb"
  [[ -f "$deb" ]] || die "$name $package_version is installed in the image but not in $work/archives"
  cp "$deb" "$stage/debs/"
  count=$((count + 1))
done < <(LC_ALL=C comm -23 <(cut -f1-3 "$image_packages" | LC_ALL=C sort) <(cut -f1-3 "$since" | LC_ALL=C sort))
cp "$debs"/pithyx_*.deb "$debs"/pithyx-kernel_*.deb "$debs"/pithyx-zfs-modules-*.deb "$stage/debs/"
echo "$count Debian packages plus pithyx, pithyx-kernel and the ZFS module"

SOURCE_DATE_EPOCH="$(git -C "$repo" log -1 --format=%ct "$commit" 2>/dev/null || git -C "$repo" log -1 --format=%ct)"
published="$(date -u -d "@$SOURCE_DATE_EPOCH" +%Y-%m-%dT%H:%M:%SZ)"
(
  cd "$stage/debs"
  dpkg-scanpackages --multiversion . /dev/null 2>/dev/null >Packages
  cat >Release <<EOF
Origin: Pithyx
Label: Pithyx
Suite: pithyx
Codename: pithyx-$version
Date: $(date -u -d "@$SOURCE_DATE_EPOCH" -R)
Architectures: $ARCH all
Description: Pithyx $version update file
SHA256:
 $(sha256sum Packages | cut -d' ' -f1) $(stat -c %s Packages) Packages
EOF
)

step "manifest"
node "$repo/tools/release/update-file.ts" manifest "$stage" --version "$version" --channel "$channel" \
  --minimum "$minimum" --commit "$commit" --kernel "$kver" --zfs "$zfs" --published-at "$published" \
  --changelog "$repo/CHANGELOG.md"

step "pithyx-$version-$ARCH.pithyxupdate"
"$repo/tools/release/pack-update.sh" "$stage" "$file"
rm -rf "$stage"
(cd "$out" && sha256sum "$(basename "$file")" >"$(basename "$file").sha256")
step "done: $file ($(du -h "$file" | cut -f1), unsigned; sign it with tools/release/sign-update.sh)"
