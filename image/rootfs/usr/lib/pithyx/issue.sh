#!/usr/bin/env bash
# Writes the console greeting (the ASCII logo and the addresses of this box) to /run/issue.d, where agetty shows it
# above the login prompt, and redraws it on every address change. The live system adds a QR code of the first address
# and who holds the installation (written by the daemon to /run/pithyx/console/installer).
set -uo pipefail
export LC_ALL=C.UTF-8

readonly PORT=7484
readonly ISSUE=/run/issue.d/50-pithyx.issue
readonly STATE=/run/pithyx/console

installer=false
if grep -qw pithyx.installer /proc/cmdline; then
  installer=true
fi
# shellcheck source=/dev/null
. /usr/lib/pithyx/release

# IPv4 first: it is what most people type.
urls() {
  local family device address
  for family in -4 -6; do
    ip -o "$family" address show scope global | while read -r _ device _ address _; do
      case "$device" in docker* | veth* | br-*) continue ;; esac
      address="${address%/*}"
      if [[ "$family" == -6 ]]; then
        echo "http://[$address]:$PORT"
      else
        echo "http://$address:$PORT"
      fi
    done
  done
}

render() {
  local addresses first
  mapfile -t addresses < <(urls)
  first="${addresses[0]:-}"
  {
    echo
    cat /usr/lib/pithyx/logo.txt
    echo
    if [[ "$installer" == true ]]; then
      echo "  Pithyx $PITHYX_VERSION installer"
      echo
      if [[ -n "$first" ]]; then
        echo "  Open this address in a browser on another device to install Pithyx:"
        echo
        printf '    %s\n' "${addresses[@]}"
        echo
        qrencode -t UTF8 -m 2 "$first" | sed 's/^/  /'
        echo
        if [[ -s "$STATE/installer" ]]; then
          echo "  $(head -n1 "$STATE/installer")"
        else
          echo "  Whoever opens the page first owns the installation. To start over, restart from the USB stick."
        fi
      else
        echo "  Waiting for a network address (DHCP). Check the network cable."
      fi
    else
      echo "  Pithyx $PITHYX_VERSION"
      echo
      if [[ -n "$first" ]]; then
        echo "  Open in a browser:"
        printf '    %s\n' "${addresses[@]}"
      else
        echo "  No network address yet."
      fi
    fi
    echo
  } | sed 's/\\/\\\\/g' >"$ISSUE.tmp"
  mv "$ISSUE.tmp" "$ISSUE"
  agetty --reload 2>/dev/null || true
}

mkdir -p "$(dirname "$ISSUE")" "$STATE"
render
# Both sources are events; a burst of changes (DHCP brings address, route and DNS at once) is drawn once.
{
  ip -o monitor address &
  inotifywait -mq -e close_write,moved_to,delete "$STATE" &
  wait
} | while read -r _; do
  while read -r -t 1 _; do :; done
  render
done
