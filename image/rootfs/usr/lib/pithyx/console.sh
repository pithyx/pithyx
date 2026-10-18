#!/usr/bin/env bash
# The console of the box on tty1 (D337): the greeting that issue.sh writes (the logo and the addresses), and a small
# keyboard menu to set a static address when there is no DHCP lease and to reset the network to DHCP. It needs no
# password: whoever sits at the box may fix its network. The menu talks to the daemon through files in
# /run/pithyx/console, which only root writes; the daemon checks the settings like Settings > Network does, and a
# static address waits 60 seconds for Keep like every other change. The box has no local login (D341).
set -uo pipefail
export LC_ALL=C.UTF-8

readonly ISSUE=/run/issue.d/50-pithyx.issue
readonly STATE=/run/pithyx/console
readonly REQUEST=$STATE/network-request
readonly RESULT=$STATE/network-result
readonly PENDING=$STATE/network-pending
readonly CARDS=$STATE/network-cards
# Touched on every draw, outside the folder issue.sh watches: a greeting newer than it is not on the screen yet.
readonly DRAWN=/run/pithyx/console-drawn

redraw=1
pending_shown=false
message=""
mkdir -p "$STATE" /run/issue.d

pending_deadline() {
  sed -n 's/^deadline=//p' "$PENDING" 2>/dev/null
}

show() {
  local deadline left addresses
  : >"$DRAWN"
  pending_shown=false
  printf '\033[H\033[2J'
  # agetty's issue escapes backslashes; the logo has some.
  sed 's/\\\\/\\/g' "$ISSUE" 2>/dev/null
  deadline="$(pending_deadline)"
  if [[ -n "$deadline" ]]; then
    pending_shown=true
    left=$((deadline - $(date +%s)))
    ((left < 0)) && left=0
    addresses="$(sed -n 's/^addresses=//p' "$PENDING")"
    echo "  A network change waits for its confirmation: ${left}s left, then it is rolled back."
    [[ -n "$addresses" ]] && echo "  New address: $addresses"
    echo
    echo "  [k] Keep the new network settings    [u] Undo them now"
  fi
  echo "  [s] Set a static address    [r] Reset the network to DHCP"
  [[ -n "$message" ]] && printf '\n  %s\n' "$message"
}

# Sends one request to the daemon and waits for its answer (at most 30 seconds).
request() {
  local answer="" tries=0
  rm -f "$RESULT"
  printf '%s\n' "$1" >"$REQUEST.tmp" && mv "$REQUEST.tmp" "$REQUEST"
  while [[ ! -s "$RESULT" ]] && ((tries < 60)); do
    sleep 0.5
    tries=$((tries + 1))
  done
  answer="$(head -n1 "$RESULT" 2>/dev/null)"
  case "$answer" in
    ok) message="$2" ;;
    "") message="The Pithyx service does not answer. Try again in a minute." ;;
    network.address.invalid) message="Refused: the address must look like 192.168.1.10/24." ;;
    network.gateway.invalid) message="Refused: the gateway must be an address in the same network." ;;
    network.change.pending) message="Refused: another change waits for its confirmation; keep or undo it first." ;;
    *) message="Refused ($answer). Settings > Network in a browser explains more." ;;
  esac
}

static_address() {
  local cards=() line number name address gateway dns servers=""
  [[ -s "$CARDS" ]] && mapfile -t cards <"$CARDS"
  if [[ ${#cards[@]} -eq 0 ]]; then
    message="No network card that Pithyx may change was found."
    return
  fi
  printf '\033[H\033[2J  Set a static IPv4 address. The box keeps it only when you confirm it within 60 seconds.\n\n'
  for number in "${!cards[@]}"; do
    read -r name line <<<"${cards[$number]}"
    printf '  [%d] %s  %s\n' "$((number + 1))" "$name" "$line"
  done
  echo
  read -rp "  Card number: " number
  if [[ ! "$number" =~ ^[0-9]+$ ]] || ((number < 1 || number > ${#cards[@]})); then
    message="No card chosen; nothing changed."
    return
  fi
  name="${cards[$((number - 1))]%% *}"
  read -rp "  Address with prefix length (for example 192.168.1.10/24): " address
  read -rp "  Gateway (empty for none): " gateway
  read -rp "  Name servers, separated by spaces (empty keeps them): " dns
  # Only characters of addresses go into the request; the daemon checks the rest.
  if [[ ! "$address" =~ ^[0-9.]+/[0-9]+$ || ! "$gateway" =~ ^[0-9.]*$ || ! "$dns" =~ ^[0-9a-fA-F.:\ ]*$ ]]; then
    message="That is not an IPv4 address with prefix length; nothing changed."
    return
  fi
  for server in $dns; do
    servers+="${servers:+,}\"$server\""
  done
  request "{\"action\":\"static\",\"interface\":\"$name\",\"address\":\"$address\",\"gateway\":\"$gateway\",\"dns\":[$servers]}" \
    "Applied: $name has $address. Press k to keep it."
}

while :; do
  if ((redraw)); then
    redraw=0
    show
  fi
  # read wakes every second to look for a new greeting (issue.sh writes one for every new address) and for a change
  # that waits for its confirmation or has just ended, with builtins only. No event can wake it instead: bash runs a
  # signal's trap but goes on waiting in read, and an event that comes before a watcher is ready is lost.
  if ! read -rsn1 -t 1 key; then
    if [[ "$ISSUE" -nt "$DRAWN" || -e "$PENDING" || "$pending_shown" == true ]]; then
      redraw=1
    fi
    continue
  fi
  case "$key" in
    s | S) static_address ;;
    r | R)
      printf '\n  Reset every network card to DHCP and remove bonds, VLANs and name servers? [y/N] '
      read -rsn1 answer
      if [[ "$answer" == [yY] ]]; then
        request '{"action":"reset"}' "The network is back on DHCP."
      else
        message="Nothing changed."
      fi
      ;;
    k | K) request '{"action":"keep"}' "The new network settings stay." ;;
    u | U) request '{"action":"undo"}' "The previous network settings are back." ;;
    *) ;;
  esac
  redraw=1
done
