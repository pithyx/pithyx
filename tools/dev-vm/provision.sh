#!/usr/bin/env bash
# Provisions a fresh Debian 13 VM as a Pithyx development machine. Safe to run repeatedly.
#
#   sudo tools/dev-vm/provision.sh [--test-disk-pattern <regex>]
#
# Exit codes: 0 done, 1 error or refused, 2 reboot required (run the script again afterwards).
set -euo pipefail
export LC_ALL=C.UTF-8 DEBIAN_FRONTEND=noninteractive

# Toolchain versions. Change them together with global.json, .nvmrc and package.json (engines).
readonly DOTNET_SDK_PACKAGE='dotnet-sdk-10.0=10.0.401-1'
readonly NODEJS_PACKAGE='nodejs=24.21.0-1nodesource1'
readonly NODE_MAJOR=24

readonly OS_RELEASE_FILE=${OS_RELEASE_FILE:-/etc/os-release}
readonly PITHYX_ETC_DIR=${PITHYX_ETC_DIR:-/etc/pithyx}

test_disk_pattern='^VYXTEST0[1-6]$'

log() {
  echo "==> $*"
}

die() {
  echo "provision: $*" >&2
  exit 1
}

os_release_value() {
  sed -nE "s/^$1=\"?([^\"]*)\"?$/\1/p" "$OS_RELEASE_FILE"
}

check_platform() {
  [[ -r "$OS_RELEASE_FILE" ]] || die "cannot read $OS_RELEASE_FILE"
  local id version_id
  id=$(os_release_value ID)
  version_id=$(os_release_value VERSION_ID)
  [[ "$id" == debian && "$version_id" == 13 ]] ||
    die "this script supports Debian 13 only (found ID=$id VERSION_ID=$version_id)"
  local environment_file="$PITHYX_ETC_DIR/environment"
  if [[ -f "$environment_file" ]]; then
    local environment
    environment=$(tr -d '[:space:]' <"$environment_file")
    [[ "$environment" == dev ]] ||
      die "$environment_file says '$environment'; refusing to provision a non-dev machine"
  fi
}

# Adds the contrib and non-free-firmware components to the Debian archive entries.
enable_components() {
  local file changed=false
  file=/etc/apt/sources.list
  if [[ -f "$file" ]]; then
    local line new
    local tmp
    tmp=$(mktemp)
    while IFS= read -r line; do
      new=$line
      if [[ "$line" =~ ^deb(-src)?[[:space:]].*(deb|security)\.debian\.org ]]; then
        [[ " $new " == *" contrib "* ]] || new="$new contrib"
        [[ " $new " == *" non-free-firmware "* ]] || new="$new non-free-firmware"
      fi
      [[ "$new" == "$line" ]] || changed=true
      printf '%s\n' "$new" >>"$tmp"
    done <"$file"
    if [[ "$changed" == true ]]; then
      log "enabling contrib and non-free-firmware in $file"
      cat "$tmp" >"$file"
    fi
    rm -f "$tmp"
  fi
  file=/etc/apt/sources.list.d/debian.sources
  if [[ -f "$file" ]]; then
    local components
    components=$(grep -E '^Components:' "$file" || true)
    if grep -Evq '(:| )contrib( |$)' <<<"$components" || grep -Evq '(:| )non-free-firmware( |$)' <<<"$components"; then
      log "enabling contrib and non-free-firmware in $file"
      sed -Ei -e '/^Components:/{/ contrib( |$)/!s/$/ contrib/}' \
        -e '/^Components:/{/ non-free-firmware( |$)/!s/$/ non-free-firmware/}' "$file"
      changed=true
    fi
  fi
  if [[ "$changed" == true ]]; then
    apt_updated=false
  fi
}

apt_updated=false
apt_update() {
  if [[ "$apt_updated" == false ]]; then
    log "apt-get update"
    apt-get update -q
    apt_updated=true
  fi
}

apt_install() {
  local missing=() package name
  for package in "$@"; do
    name=${package%%=*}
    if [[ "$package" == *=* ]]; then
      [[ "$(dpkg-query -W -f '${Status} ${Version}' "$name" 2>/dev/null)" == "install ok installed ${package#*=}" ]] ||
        missing+=("$package")
    else
      [[ "$(dpkg-query -W -f '${Status}' "$name" 2>/dev/null)" == "install ok installed" ]] ||
        missing+=("$package")
    fi
  done
  if [[ ${#missing[@]} -gt 0 ]]; then
    apt_update
    log "installing ${missing[*]}"
    apt-get install -y -q --no-install-recommends "${missing[@]}"
  fi
}

add_microsoft_repo() {
  if [[ ! -f /etc/apt/sources.list.d/microsoft-prod.list ]]; then
    log "adding the Microsoft package repository"
    local deb
    deb=$(mktemp --suffix=.deb)
    curl -fsSL https://packages.microsoft.com/config/debian/13/packages-microsoft-prod.deb -o "$deb"
    dpkg -i "$deb"
    rm -f "$deb"
    apt_updated=false
  fi
}

add_nodesource_repo() {
  local keyring=/usr/share/keyrings/nodesource.gpg
  local sources=/etc/apt/sources.list.d/nodesource.sources
  if [[ ! -f "$keyring" ]]; then
    log "adding the NodeSource signing key"
    curl -fsSL https://deb.nodesource.com/gpgkey/nodesource-repo.gpg.key | gpg --dearmor -o "$keyring"
    chmod 644 "$keyring"
  fi
  local expected
  expected=$(
    cat <<EOF
Types: deb
URIs: https://deb.nodesource.com/node_${NODE_MAJOR}.x
Suites: nodistro
Components: main
Architectures: $(dpkg --print-architecture)
Signed-By: $keyring
EOF
  )
  if [[ ! -f "$sources" || "$(cat "$sources")" != "$expected" ]]; then
    log "writing $sources"
    printf '%s\n' "$expected" >"$sources"
    apt_updated=false
  fi
  local package
  for package in nodejs nsolid; do
    local pin="/etc/apt/preferences.d/$package"
    local pin_content
    pin_content=$(printf 'Package: %s\nPin: origin deb.nodesource.com\nPin-Priority: 600' "$package")
    if [[ ! -f "$pin" || "$(cat "$pin")" != "$pin_content" ]]; then
      log "writing $pin"
      printf '%s\n' "$pin_content" >"$pin"
    fi
  done
}

install_packages() {
  local arch
  arch=$(dpkg --print-architecture)
  local qemu=()
  case "$arch" in
    amd64) qemu=(qemu-system-x86 ovmf) ;;
    arm64) qemu=(qemu-system-arm qemu-efi-aarch64) ;;
    *) die "unsupported architecture $arch" ;;
  esac
  echo 'zfs-dkms zfs-dkms/note-incompatible-licenses note true' | debconf-set-selections
  # The font and libraries after git are for headless Chromium (Playwright); without any font, text renders invisible.
  apt_install "linux-headers-$(uname -r)" "linux-headers-$arch" dkms zfs-dkms zfsutils-linux \
    mdadm btrfs-progs smartmontools samba nfs-kernel-server docker.io acl "${qemu[@]}" \
    shellcheck git \
    fonts-dejavu-core libgbm1 libxkbcommon0 libatk1.0-0t64 libatk-bridge2.0-0t64 libatspi2.0-0t64 \
    libxcomposite1 libxdamage1 libxfixes3 libxrandr2
  apt_install "$DOTNET_SDK_PACKAGE" "$NODEJS_PACKAGE"
  if [[ ! -e /usr/bin/pnpm ]]; then
    log "enabling corepack"
    corepack enable
  fi
}

load_zfs() {
  if [[ ! -d /sys/module/zfs ]]; then
    log "loading the zfs kernel module"
    if ! modprobe zfs; then
      echo "Reboot required: the zfs module could not be loaded. Reboot, then run provision.sh again." >&2
      exit 2
    fi
  fi
  if [[ -f /var/run/reboot-required ]]; then
    echo "Reboot required ($(tr '\n' ' ' </var/run/reboot-required.pkgs 2>/dev/null)). Reboot, then run provision.sh again." >&2
    exit 2
  fi
}

# Samba and NFS are configured by the daemon later; until then the dev VM exposes no shares.
disable_share_services() {
  local unit
  for unit in smbd nmbd nfs-server; do
    if systemctl is-enabled --quiet "$unit" 2>/dev/null || systemctl is-active --quiet "$unit"; then
      log "stopping and disabling $unit"
      systemctl disable --now "$unit"
    fi
  done
}

set_locale() {
  if ! grep -Eq '^en_US\.UTF-8 UTF-8' /etc/locale.gen; then
    log "enabling en_US.UTF-8 in /etc/locale.gen"
    sed -Ei 's/^#[[:space:]]*(en_US\.UTF-8 UTF-8)/\1/' /etc/locale.gen
    grep -Eq '^en_US\.UTF-8 UTF-8' /etc/locale.gen || echo 'en_US.UTF-8 UTF-8' >>/etc/locale.gen
  fi
  if [[ "$(locale -a)" != *en_US.utf8* ]]; then
    log "generating locales"
    locale-gen
  fi
  if ! grep -Eq '^LANG="?en_US\.UTF-8"?$' /etc/default/locale 2>/dev/null; then
    log "setting the system locale to en_US.UTF-8 (applies to new sessions)"
    update-locale LANG=en_US.UTF-8
  fi
}

write_if_changed() {
  local file=$1 content=$2
  if [[ ! -f "$file" || "$(cat "$file")" != "$content" ]]; then
    log "writing $file"
    printf '%s\n' "$content" >"$file"
  fi
}

write_markers() {
  install -d -m 755 "$PITHYX_ETC_DIR"
  write_if_changed "$PITHYX_ETC_DIR/environment" dev

  local serials=() name serial type
  while read -r name serial type; do
    [[ "$type" == disk && "$serial" =~ $test_disk_pattern ]] || continue
    if [[ -n "$(lsblk -n -o MOUNTPOINTS "/dev/$name" | tr -d '[:space:]')" ]]; then
      die "test disk candidate $name ($serial) has mounted filesystems"
    fi
    serials+=("$serial")
  done < <(lsblk -dn -o NAME,SERIAL,TYPE)
  [[ ${#serials[@]} -gt 0 ]] || die "no disks match the test disk pattern $test_disk_pattern"
  write_if_changed "$PITHYX_ETC_DIR/test-disks" "$(printf '%s\n' "${serials[@]}" | sort)"
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --test-disk-pattern)
      [[ -n "${2:-}" ]] || die "--test-disk-pattern needs a value"
      test_disk_pattern=$2
      shift 2
      ;;
    *) die "unknown argument $1" ;;
  esac
done

check_platform
if [[ $EUID -ne 0 ]]; then
  exec sudo --preserve-env=OS_RELEASE_FILE,PITHYX_ETC_DIR "$0" --test-disk-pattern "$test_disk_pattern"
fi

apt_install ca-certificates curl gnupg
enable_components
add_microsoft_repo
add_nodesource_repo
install_packages
load_zfs
disable_share_services
set_locale
write_markers
log "done"
