#!/usr/bin/env bash
# Starts a short-lived second daemon on port 7485 with a fresh database, next to the dev unit on 7484, which it never
# touches. Use it for a branch or a clean first-run state: run the copy of this script in the branch's worktree.
#
#   tools/dev-vm/start.sh [--real] [--no-build]
#
#   --real      use real system commands instead of the simulation fixtures (the disk inventory is read-only)
#   --no-build  skip the desktop build and reuse desktop/dist
#
# The instance keeps its files in a new temporary directory, deleted when the daemon stops (Ctrl+C).
set -euo pipefail

readonly PORT=7485
repo="$(cd "$(dirname "$0")/../.." && pwd)"
simulate=1
build=1

die() {
  echo "start: $*" >&2
  exit 1
}

for arg in "$@"; do
  case "$arg" in
    --real) simulate=0 ;;
    --no-build) build=0 ;;
    *) die "unknown option: $arg" ;;
  esac
done

if ss -ltn "sport = :$PORT" | grep -q LISTEN; then
  die "port $PORT is in use; stop the other short-lived daemon first (ss -ltnp | grep $PORT)"
fi

if [[ "$build" == 1 ]]; then
  pnpm --dir "$repo/desktop" build
fi

data="$(mktemp -d -t pithyx-7485-XXXXXX)"
trap 'rm -rf "$data"' EXIT
mkdir -p "$data/logs"

echo "Pithyx on http://localhost:$PORT (simulate=$simulate, data=$data, deleted on exit)"

export ASPNETCORE_ENVIRONMENT=Development
export PITHYX_SIMULATE="$simulate"
export PITHYX_DEV_PORT="$PORT"
export PITHYX_DB="$data/pithyx.db"
export PITHYX_LOG_DIR="$data/logs"
export PITHYX_CACHE_DIR="$data/cache"
export PITHYX_DESKTOP_DIR="$repo/desktop/dist"
export PITHYX_FIXTURES="$repo/daemon/tests/fixtures"
dotnet run --project "$repo/daemon/src/Pithyx.Daemon" --no-launch-profile
