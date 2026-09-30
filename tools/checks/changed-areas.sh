#!/usr/bin/env bash
# Decides which CI jobs a pull request needs, from the files it changes, and writes the answers to $GITHUB_OUTPUT:
#   code      anything besides Markdown and website/ changed (E2E runs)
#   daemon    the daemon, its translations, the OpenAPI document or the CI setup changed
#   frontend  anything besides docs and daemon/ changed
#   storage   storage, account or command runner code, its tests and fixtures, the dev VM tools or the CI setup changed
#             (the ZFS integration tests run)
# Pushes to dev and master run everything; so does a range git cannot resolve.
set -euo pipefail

out="${GITHUB_OUTPUT:-/dev/stdout}"

if [[ "${EVENT_NAME:-}" != "pull_request" ]] || ! files="$(git diff --name-only "$BASE_SHA" "$HEAD_SHA")"; then
  printf 'code=true\ndaemon=true\nfrontend=true\nstorage=true\n' >>"$out"
  exit 0
fi

code=false
daemon=false
frontend=false
storage=false
while IFS= read -r file; do
  [[ -z "$file" ]] && continue
  case "$file" in
    *.md | website/*) continue ;;
  esac
  code=true
  case "$file" in
    daemon/* | i18n/* | tools/i18n/* | global.json | .github/* | packages/api-client/*) daemon=true ;;
  esac
  case "$file" in
    daemon/*) ;;
    *) frontend=true ;;
  esac
  case "$file" in
    daemon/src/Pithyx.Daemon/Storage/* | daemon/src/Pithyx.Daemon/Commands/* | daemon/tests/Pithyx.Daemon.Tests/Storage/* | \
      daemon/src/Pithyx.Daemon/Users/* | daemon/tests/Pithyx.Daemon.Tests/Users/* | \
      daemon/tests/fixtures/commands/* | tools/dev-vm/* | tools/fixtures/* | .github/*) storage=true ;;
  esac
done <<<"$files"

printf 'code=%s\ndaemon=%s\nfrontend=%s\nstorage=%s\n' "$code" "$daemon" "$frontend" "$storage" >>"$out"
