#!/usr/bin/env bash
# Runs the steps of .github/workflows/ci.yml on this machine, for when GitHub Actions does not start (for example a
# billing lock). A green run approves a merge into dev like green Actions do; attach the summary to the pull request.
#
#   tools/ci-local.sh [--base <ref>] [--summary <file>] [--skip-e2e]
#
#   --base <ref>      commits after this ref are checked for message format and sign-off (default: origin/dev)
#   --summary <file>  also write the Markdown summary to this file
#   --skip-e2e        leave out the E2E suite and the design checks (never for a merge approval)
#
# Every step runs even when an earlier one failed; the exit code is non-zero when any step failed. Step output goes to
# a log folder whose path is printed. The E2E daemon uses PITHYX_E2E_PORT, or 7485 when a dev daemon holds 7484.
set -uo pipefail
export LC_ALL=C.UTF-8 DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 CI=1

repo="$(cd "$(dirname "$0")/.." && pwd)"
base=origin/dev
summary_file=""
e2e=1

while [[ $# -gt 0 ]]; do
  case "$1" in
    --base)
      base="$2"
      shift 2
      ;;
    --summary)
      summary_file="$2"
      shift 2
      ;;
    --skip-e2e)
      e2e=0
      shift
      ;;
    *)
      echo "ci-local: unknown option: $1" >&2
      exit 2
      ;;
  esac
done

[[ "$EUID" -ne 0 ]] || {
  echo "ci-local: run as your user, never as root" >&2
  exit 2
}
cd "$repo" || exit 2
git rev-parse --verify --quiet "$base" >/dev/null || {
  echo "ci-local: unknown base ref $base" >&2
  exit 2
}

logs="$(mktemp -d "${TMPDIR:-/tmp}/pithyx-ci-local-XXXXXX")"
rows=()
failed=0

step() {
  # step <job> <name> <command...>
  local job="$1" name="$2" log started result
  shift 2
  log="$logs/$(printf '%02d' "${#rows[@]}")-${job}-${name//[^a-zA-Z0-9]/_}.log"
  started=$SECONDS
  printf '%-9s %-40s ' "$job" "$name"
  if "$@" >"$log" 2>&1; then
    result=pass
  else
    result=FAIL
    failed=$((failed + 1))
  fi
  printf '%s (%ss)\n' "$result" "$((SECONDS - started))"
  [[ "$result" == pass ]] || tail -n 20 "$log" | sed 's/^/    /'
  rows+=("| $job | $name | $result | $((SECONDS - started)) s |")
}

port_in_use() {
  ss -ltnH "sport = :$1" 2>/dev/null | grep -q .
}

e2e_port="${PITHYX_E2E_PORT:-}"
if [[ -z "$e2e_port" ]]; then
  e2e_port=7484
  port_in_use 7484 && e2e_port=7485
fi

started_all=$SECONDS
step setup "pnpm install" pnpm install --frozen-lockfile

step repo "repo check" node tools/checks/repo-check.ts
step repo "prettier" pnpm format:check
step repo "shellcheck" bash -c 'shellcheck tools/*.sh tools/*/*.sh'
step repo "commit messages" node tools/checks/commit-msg.ts --range "$base..HEAD"
step repo "sign-off (DCO)" node tools/checks/dco.ts "$base..HEAD"

step daemon "restore" dotnet restore daemon/Pithyx.slnx
step daemon "build (Release)" dotnet build daemon/Pithyx.slnx --configuration Release --no-restore
step daemon "OpenAPI document up to date" git diff --exit-code packages/api-client/openapi.json
step daemon "tests" dotnet test --solution daemon/Pithyx.slnx --configuration Release --no-build
step daemon "dotnet format" dotnet format daemon/Pithyx.slnx --verify-no-changes --no-restore

step frontend "lint" pnpm lint
step frontend "typecheck" pnpm typecheck
step frontend "unit tests" pnpm test
step frontend "build" pnpm build
step frontend "bundle budget" pnpm check:bundle

if [[ "$e2e" == 1 ]]; then
  step e2e "build daemon" dotnet build daemon/Pithyx.slnx
  step e2e "build desktop" pnpm --filter "@pithyx/desktop..." build
  step e2e "E2E and performance gate" env PITHYX_E2E_PORT="$e2e_port" pnpm e2e
  step e2e "design checks" pnpm design:test
fi

{
  echo "### Local CI (\`tools/ci-local.sh\`)"
  echo
  echo "Commit \`$(git rev-parse --short HEAD)\` on \`$(git rev-parse --abbrev-ref HEAD)\`, checked against \`$base\` ($(git rev-parse --short "$base")), on $(hostname), $(date -u +%Y-%m-%dT%H:%MZ), $((SECONDS - started_all)) s."
  [[ "$e2e" == 1 ]] || echo "E2E and design checks were skipped."
  echo
  echo "| Job | Step | Result | Time |"
  echo "| --- | --- | --- | --- |"
  printf '%s\n' "${rows[@]}"
  echo
  if [[ "$failed" == 0 ]]; then
    echo "Result: **green** ($(( ${#rows[@]} )) steps)."
  else
    echo "Result: **red**, $failed of ${#rows[@]} steps failed."
  fi
} >"$logs/summary.md"

echo
cat "$logs/summary.md"
[[ -z "$summary_file" ]] || cp "$logs/summary.md" "$summary_file"
echo
echo "Logs: $logs"
[[ "$failed" == 0 ]]
