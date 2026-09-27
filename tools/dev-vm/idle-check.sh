#!/usr/bin/env bash
# Measures the daemon while it idles and checks the budgets from CLAUDE.md: below 1 percent CPU and 200 MB RSS, and
# nothing that polls. Read-only: it only samples /proc.
#
#   idle-check.sh [--seconds 60] [--pid <pid>]
#
# Without --pid it measures the process listening on port 7484. Close every desktop tab first: an open desktop keeps
# its WebSocket and live topics busy, which is not idle.
set -euo pipefail
export LC_ALL=C.UTF-8

readonly CPU_BUDGET_PERCENT=1
readonly RSS_BUDGET_KB=$((200 * 1024))
readonly PORT=7484

seconds=60
pid=""

die() {
  echo "idle-check: $*" >&2
  exit 2
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --seconds)
      seconds="${2:?--seconds needs a value}"
      shift 2
      ;;
    --pid)
      pid="${2:?--pid needs a value}"
      shift 2
      ;;
    *) die "unknown argument $1" ;;
  esac
done
[[ "$seconds" =~ ^[1-9][0-9]*$ ]] || die "--seconds must be a positive number"

if [[ -z "$pid" ]]; then
  pid="$(ss -Hltnp "sport = :$PORT" | grep -o 'pid=[0-9]*' | head -n 1 | cut -d= -f2 || true)"
  [[ -n "$pid" ]] || die "nothing listens on port $PORT; start the daemon or pass --pid"
fi
[[ -r "/proc/$pid/stat" ]] || die "no process $pid"

# utime + stime of all threads in clock ticks (fields 14 and 15; the name in field 2 may contain spaces).
cpu_ticks() {
  local stat
  stat="$(<"/proc/$pid/stat")"
  stat="${stat##*) }"
  read -r -a fields <<<"$stat"
  echo $((fields[11] + fields[12]))
}

# Context switches per thread: each one is a wakeup, so a steady rate means something polls.
declare -A start_switches=()
thread_switches() {
  local status=$1 total=0 key value
  while read -r key value; do
    if [[ "$key" == voluntary_ctxt_switches: || "$key" == nonvoluntary_ctxt_switches: ]]; then
      total=$((total + value))
    fi
  done <"$status"
  echo "$total"
}

record_switches() {
  local status tid
  for status in /proc/"$pid"/task/*/status; do
    tid="${status%/status}"
    start_switches["${tid##*/}"]="$(thread_switches "$status" 2>/dev/null || echo 0)"
  done
}

# Switches since record_switches: threads that ended in between are left out, new ones count from zero.
switches_since() {
  local status tid now total=0
  for status in /proc/"$pid"/task/*/status; do
    tid="${status%/status}"
    tid="${tid##*/}"
    now="$(thread_switches "$status" 2>/dev/null || echo 0)"
    total=$((total + now - ${start_switches[$tid]:-0}))
  done
  echo "$total"
}

rss_kb() {
  awk '/^VmRSS:/ { print $2 }' "/proc/$pid/status"
}

ticks_per_second="$(getconf CLK_TCK)"
command="$(tr '\0' ' ' <"/proc/$pid/cmdline")"
echo "Measuring pid $pid for $seconds s: $command"

start_ticks="$(cpu_ticks)"
record_switches
sleep "$seconds"
end_ticks="$(cpu_ticks)"
switched="$(switches_since)"
rss="$(rss_kb)"
threads="$(find "/proc/$pid/task" -mindepth 1 -maxdepth 1 | wc -l)"

cpu_hundredths=$(((end_ticks - start_ticks) * 10000 / (ticks_per_second * seconds)))
cpu_percent="$((cpu_hundredths / 100)).$(printf '%02d' $((cpu_hundredths % 100)))"
wakeups_tenths=$((switched * 10 / seconds))
wakeups="$((wakeups_tenths / 10)).$((wakeups_tenths % 10))"

echo "CPU:     $cpu_percent % (budget below $CPU_BUDGET_PERCENT %)"
echo "RSS:     $((rss / 1024)) MB (budget below $((RSS_BUDGET_KB / 1024)) MB)"
echo "Wakeups: $wakeups per second over $threads threads (context switches)"

status=0
if ((cpu_hundredths >= CPU_BUDGET_PERCENT * 100)); then
  echo "idle-check: CPU above budget" >&2
  status=1
fi
if ((rss >= RSS_BUDGET_KB)); then
  echo "idle-check: RSS above budget" >&2
  status=1
fi
exit "$status"
