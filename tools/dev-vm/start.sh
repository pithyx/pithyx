#!/usr/bin/env bash
# Starts a test session: builds the desktop and runs the daemon, which serves the desktop and the API on port 7484.
#
#   tools/dev-vm/start.sh [--real] [--reset] [--no-build]
#
#   --real      use real system commands instead of the simulation fixtures (the disk inventory is read-only)
#   --reset     delete the session's database and logs first, so the setup wizard starts fresh
#   --no-build  skip the desktop build and reuse desktop/dist
#
# The session keeps its files in $PITHYX_RUN_DIR (default ~/.cache/pithyx-run), apart from .pithyx-dev.
set -euo pipefail

readonly PORT=7484
repo="$(cd "$(dirname "$0")/../.." && pwd)"
data="${PITHYX_RUN_DIR:-$HOME/.cache/pithyx-run}"
simulate=1
build=1
reset=0

die() {
  echo "start: $*" >&2
  exit 1
}

for arg in "$@"; do
  case "$arg" in
    --real) simulate=0 ;;
    --reset) reset=1 ;;
    --no-build) build=0 ;;
    *) die "unknown option: $arg" ;;
  esac
done

if ss -ltn "sport = :$PORT" | grep -q LISTEN; then
  die "port $PORT is in use; stop the other daemon first (ss -ltnp | grep $PORT)"
fi

if [[ "$reset" == 1 ]]; then
  rm -rf "$data"
fi
mkdir -p "$data/logs"

if [[ "$build" == 1 ]]; then
  pnpm --dir "$repo/desktop" build
fi

echo "Pithyx on http://localhost:$PORT (simulate=$simulate, data=$data)"

export ASPNETCORE_ENVIRONMENT=Development
export PITHYX_SIMULATE="$simulate"
export PITHYX_DB="$data/pithyx.db"
export PITHYX_LOG_DIR="$data/logs"
export PITHYX_CACHE_DIR="$data/cache"
export PITHYX_DESKTOP_DIR="$repo/desktop/dist"
export PITHYX_FIXTURES="$repo/daemon/tests/fixtures"
exec dotnet run --project "$repo/daemon/src/Pithyx.Daemon" --no-launch-profile
