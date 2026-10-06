#!/usr/bin/env bash
# Deploys dev to the dev VM: runs the daemon in real mode, as root, as the systemd unit pithyx-dev.service on port 7484.
#
#   tools/dev-vm/install-dev-daemon.sh [--no-build] [--yes]
#   tools/dev-vm/install-dev-daemon.sh --uninstall
#
#   --no-build   reuse the checkout's Release build and desktop/dist
#   --yes        stop a running unit or another daemon on port 7484 without asking
#   --uninstall  stop and remove the unit; /var/lib/pithyx and /var/log/pithyx are kept
#
# The unit is the one installation on the dev VM, with one database. It always runs from the main checkout on the
# branch dev, which the script fast-forwards to origin/dev first; branches are checked by CI before the merge and here
# after it (D277). After the start, seed-dev-accounts.sh makes sure the owner can sign in.
#
# Works when started by the owner (it asks sudo for the root steps) and under sudo. Either way the build runs as
# the calling user (SUDO_USER), never as root, so the checkout gets no root-owned files; only installing the unit
# and starting it run as root.
#
# Refuses to run unless /etc/pithyx/environment says dev. The database lives in /var/lib/pithyx/pithyx.db and the
# logs in /var/log/pithyx. Before anything is stopped, the script checks that the checkout knows every schema
# version in the database, and says what it would stop. The unit's DiskGuard only allows the disks listed in
# /etc/pithyx/test-disks.
set -euo pipefail

readonly PORT=7484
readonly UNIT=pithyx-dev.service
readonly DATABASE=/var/lib/pithyx/pithyx.db
# Keep in sync with DaemonExitCode.DatabaseNewerThanDaemon and RestartPreventExitStatus in the unit.
readonly EXIT_DATABASE_NEWER=65
script_repo="$(cd "$(dirname "$0")/../.." && pwd)"
# The main checkout, which holds .worktrees/, also when the script runs from a worktree.
main_repo="$(dirname "$(git -C "$script_repo" rev-parse --path-format=absolute --git-common-dir)")"
checkout="$main_repo"
build=1
assume_yes=0

die() {
  echo "install-dev-daemon: $*" >&2
  exit 1
}

as_root() {
  if [[ "$EUID" -eq 0 ]]; then
    "$@"
  else
    sudo "$@"
  fi
}

[[ "$(head -n1 /etc/pithyx/environment 2>/dev/null)" == dev ]] || die "/etc/pithyx/environment does not say dev; this is for the dev VM only"

uninstall=0
while [[ $# -gt 0 ]]; do
  case "$1" in
    --no-build)
      build=0
      shift
      ;;
    --yes)
      assume_yes=1
      shift
      ;;
    --uninstall)
      uninstall=1
      shift
      ;;
    *) die "unknown option: $1" ;;
  esac
done

if [[ "$uninstall" == 1 ]]; then
  as_root systemctl disable --now "$UNIT" 2>/dev/null || true
  as_root rm -f "/etc/systemd/system/$UNIT"
  as_root systemctl daemon-reload
  echo "removed $UNIT; /var/lib/pithyx and /var/log/pithyx are kept"
  exit 0
fi

branch="$(git -C "$checkout" rev-parse --abbrev-ref HEAD)"
[[ "$branch" == dev ]] || die "$checkout is on $branch; the dev unit only runs dev (branches are checked by CI and here after the merge)"

# The build user: whoever called sudo, else the checkout's owner. Never root.
if [[ "$EUID" -eq 0 ]]; then
  builder="${SUDO_USER:-}"
  [[ -n "$builder" && "$builder" != root ]] || builder="$(stat -c %U "$checkout")"
else
  builder="$(id -un)"
fi
[[ "$builder" != root ]] || die "refusing to build as root; run the script as your user or with sudo from your account"

as_builder() {
  if [[ "$(id -un)" == "$builder" ]]; then
    "$@"
  else
    sudo -u "$builder" -H -- "$@"
  fi
}

as_builder git -C "$checkout" fetch --quiet origin dev
as_builder git -C "$checkout" merge --ff-only --quiet origin/dev || die "$checkout cannot fast-forward to origin/dev"
commit="$(git -C "$checkout" log -1 --format='%h %s')"

root_file="$(find "$checkout" -xdev -path "$checkout/.worktrees" -prune -o -user 0 -print -quit 2>/dev/null || true)"
[[ -z "$root_file" ]] || die "root owns files in $checkout (for example $root_file), probably from an earlier run as root; fix them with: sudo chown -R $builder: $checkout"

# A daemon refuses a database that a newer daemon wrote (exit code 65). Find out before anything is stopped.
known="$(find "$checkout/daemon/src/Pithyx.Daemon/Database/Migrations" -name '[0-9]*.sql' -printf '%f\n' | cut -d_ -f1 | sed 's/^0*//' | sort -n | paste -sd,)"
if as_root test -f "$DATABASE"; then
  command -v python3 >/dev/null || die "python3 is needed to read the schema version of $DATABASE"
  unknown="$(as_root python3 - "$DATABASE" "$known" <<'PY'
import sqlite3, sys
known = {int(v) for v in sys.argv[2].split(",") if v}
db = sqlite3.connect(f"file:{sys.argv[1]}?mode=ro", uri=True)
try:
    applied = {row[0] for row in db.execute("SELECT version FROM schema_version")}
except sqlite3.OperationalError:
    applied = set()
print(",".join(str(v) for v in sorted(applied - known)))
PY
)"
  if [[ -n "$unknown" ]]; then
    die "$DATABASE has schema versions $unknown, which dev does not know (a branch's daemon wrote it); nothing was stopped. Run tools/dev-vm/reset-dev-db.sh, then this script again"
  fi
fi

if [[ "$build" == 1 ]]; then
  echo "building $checkout ($commit) as $builder"
  if [[ ! -d "$checkout/node_modules" ]]; then
    as_builder pnpm --dir "$checkout" install --frozen-lockfile
  fi
  as_builder dotnet build "$checkout/daemon/src/Pithyx.Daemon" --configuration Release --nologo --verbosity quiet
  as_builder env -C "$checkout/desktop" pnpm exec vite build --logLevel warn
fi
[[ -f "$checkout/daemon/src/Pithyx.Daemon/bin/Release/net10.0/pithyxd.dll" ]] || die "no Release build in $checkout; run without --no-build"

# Say what gets stopped, then stop it only with consent.
stops=()
if systemctl is-active --quiet "$UNIT"; then
  stops+=("the unit $UNIT, $(systemctl show -p Description --value "$UNIT")")
fi
pid="$(as_root ss -ltnpH "sport = :$PORT" | grep -o 'pid=[0-9]*' | head -n1 | cut -d= -f2 || true)"
unit_pid="$(systemctl show -p MainPID --value "$UNIT" 2>/dev/null || echo 0)"
if [[ -n "$pid" && "$pid" != "$unit_pid" ]]; then
  owner="$(ps -o user= -p "$pid" | tr -d ' ')"
  cwd="$(as_root readlink "/proc/$pid/cwd" || echo unknown)"
  stops+=("process $pid of $owner on port $PORT: $(ps -o args= -p "$pid") (in $cwd)")
else
  pid=""
fi
if [[ ${#stops[@]} -gt 0 ]]; then
  echo "To run $UNIT from $checkout ($commit), this stops:"
  printf '  - %s\n' "${stops[@]}"
  if [[ "$assume_yes" != 1 ]]; then
    [[ -t 0 ]] || die "not stopping anything without a terminal; pass --yes to agree"
    read -r -p "Stop them? [y/N] " answer
    [[ "$answer" == [yY]* ]] || die "nothing was stopped"
  fi
fi

if systemctl is-active --quiet "$UNIT"; then
  as_root systemctl stop "$UNIT"
fi
if [[ -n "$pid" ]]; then
  as_root kill "$pid"
  for _ in $(seq 50); do
    as_root kill -0 "$pid" 2>/dev/null || break
    sleep 0.1
  done
fi

as_root install -d -m 0700 /var/lib/pithyx
as_root install -d -m 0750 /var/log/pithyx
sed "s|@REPO@|$checkout|g" "$script_repo/tools/dev-vm/$UNIT" | as_root tee "/etc/systemd/system/$UNIT" >/dev/null
as_root systemctl daemon-reload
# Clears the start limit of earlier failed runs.
as_root systemctl reset-failed "$UNIT" 2>/dev/null || true
as_root systemctl enable --now "$UNIT"

for _ in $(seq 150); do
  if curl -fsS "http://127.0.0.1:$PORT/api/health" >/dev/null 2>&1; then
    echo "$UNIT runs $commit on http://$(hostname -I | awk '{ print $1 }'):$PORT (real mode)"
    as_builder "$script_repo/tools/dev-vm/seed-dev-accounts.sh"
    exit 0
  fi
  if [[ "$(systemctl show -p ExecMainStatus --value "$UNIT")" == "$EXIT_DATABASE_NEWER" ]]; then
    as_root journalctl -u "$UNIT" -n 5 --no-pager
    die "$DATABASE was written by a newer daemon than dev; run tools/dev-vm/reset-dev-db.sh"
  fi
  sleep 0.2
done
as_root systemctl --no-pager status "$UNIT" || true
as_root journalctl -u "$UNIT" -n 30 --no-pager
die "the daemon did not answer on port $PORT"
