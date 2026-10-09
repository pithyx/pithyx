#!/usr/bin/env bash
# Runs inside the root file system at the end of image/build.sh (mmdebstrap customize hook, as root in the user
# namespace), after the Pithyx packages are installed. Only what belongs to the image goes here; what an update must
# bring as well lives in the packages (image/deb, tools/release/build-debs.sh). Everything here is the same for the
# live system and the installed system; only the kernel command line (pithyx.installer) tells them apart.
#
#   customize.sh <version> <commit>
set -euo pipefail
export LC_ALL=C.UTF-8

version="$1"
commit="$2"
staged=/tmp/pithyx

# The files of image/rootfs that no package owns: /etc/issue belongs to base-files, the console font setting to
# console-setup, and the environment says this is a box (prod). Copied without the build user's ownership.
for file in etc/issue etc/default/console-setup etc/pithyx/environment; do
  install -D --mode=0644 "$staged/rootfs/$file" "/$file"
done
grep -qx "PITHYX_VERSION=$version" /usr/lib/pithyx/release || { echo "the pithyx package is not $version" >&2; exit 1; }
grep -qx "PITHYX_COMMIT=$commit" /usr/lib/pithyx/release || { echo "the pithyx package is not from commit $commit" >&2; exit 1; }
rm -rf "$staged"

# Linux accounts outside Pithyx stay below its uid and gid range 20000 to 49999 (step 10, Users/LoginDefs.cs).
sed -i -E \
  -e 's/^#?\s*UID_MAX\s+.*/UID_MAX\t\t\t19999/' \
  -e 's/^#?\s*GID_MAX\s+.*/GID_MAX\t\t\t19999/' \
  /etc/login.defs
grep -qE '^UID_MAX\s+19999' /etc/login.defs && grep -qE '^GID_MAX\s+19999' /etc/login.defs

echo pithyx >/etc/hostname
printf '127.0.0.1\tlocalhost\n127.0.1.1\tpithyx\n::1\t\tlocalhost ip6-localhost ip6-loopback\n' >/etc/hosts
ln -sf /run/systemd/resolve/stub-resolv.conf /etc/resolv.conf

# Root stays locked; the administrator comes from the installer.
passwd --lock root >/dev/null

# The daemon decides which services run: SSH is off by default and Samba starts once a share needs it.
systemctl disable ssh.service smbd.service nmbd.service samba-ad-dc.service 2>/dev/null || true
systemctl mask samba-ad-dc.service
# Docker runs only while a backend app is installed and its pool is mounted; the daemon starts and stops it (D309).
systemctl disable docker.socket docker.service containerd.service 2>/dev/null || true
systemctl enable systemd-networkd.service systemd-resolved.service systemd-timesyncd.service \
  pithyx.service pithyx-issue.service pithyx-console.service
# tty1 belongs to the console menu (D337); the login prompt is on tty2 and further, where logind starts it on Alt+F2.
systemctl mask getty@tty1.service autovt@tty1.service

# Generated per machine: the live system creates them at boot, the installer for the installed system.
rm -f /etc/ssh/ssh_host_* /var/lib/dbus/machine-id
: >/etc/machine-id

# The installed system gets its updates from Debian itself (D297), not from the build snapshot.
rm -f /etc/apt/sources.list
cat >/etc/apt/sources.list.d/debian.sources <<'EOF'
Types: deb
URIs: http://deb.debian.org/debian
Suites: trixie trixie-updates
Components: main contrib non-free-firmware
Signed-By: /usr/share/keyrings/debian-archive-keyring.gpg

Types: deb
URIs: http://deb.debian.org/debian-security
Suites: trixie-security
Components: main contrib non-free-firmware
Signed-By: /usr/share/keyrings/debian-archive-keyring.gpg
EOF

update-initramfs -u -k all
# mmdebstrap clears the apt lists, the apt cache and /tmp after this hook.
rm -rf /var/log/*.log /var/log/apt/* /var/tmp/*
