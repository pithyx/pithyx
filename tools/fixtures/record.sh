#!/usr/bin/env bash
# Records the output of a command as a fixture for simulation mode and parser tests.
#
#   tools/fixtures/record.sh <scenario> <id> -- <command> [args...]
#
# Writes daemon/tests/fixtures/commands/<scenario>/<id>.json, <id>.stdout and, if not empty, <id>.stderr.
# Run the command exactly as the daemon does: same program name, same arguments.
set -euo pipefail
export LC_ALL=C.UTF-8

die() {
  echo "record: $*" >&2
  exit 1
}

[[ $# -ge 4 && "$3" == -- ]] || die "usage: record.sh <scenario> <id> -- <command> [args...]"
scenario=$1
id=$2
shift 3
name_pattern='^[a-z0-9][a-z0-9_-]*$'
[[ "$scenario" =~ $name_pattern ]] || die "scenario must match $name_pattern"
[[ "$id" =~ $name_pattern ]] || die "id must match $name_pattern"

dir="$(git rev-parse --show-toplevel)/daemon/tests/fixtures/commands/$scenario"
mkdir -p "$dir"

exit_code=0
"$@" >"$dir/$id.stdout" 2>"$dir/$id.stderr" || exit_code=$?
[[ -s "$dir/$id.stderr" ]] || rm -f "$dir/$id.stderr"

node -e '
const [file, exitCode, command, ...args] = process.argv.slice(1);
require("node:fs").writeFileSync(file, JSON.stringify({ command, args, exitCode: Number(exitCode) }, null, 2) + "\n");
' "$dir/$id.json" "$exit_code" "$@"

echo "recorded $scenario/$id (exit code $exit_code)"
