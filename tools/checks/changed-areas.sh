#!/usr/bin/env bash
# Decides which CI jobs a pull request needs, from the files it changes, and writes the answers to $GITHUB_OUTPUT:
#   code      anything besides Markdown and website/ changed (E2E runs)
#   daemon    the daemon, its translations, the OpenAPI document or the CI setup changed
#   frontend  anything besides docs and daemon/ changed
# Pushes to dev and master run everything; so does a range git cannot resolve.
set -euo pipefail

out="${GITHUB_OUTPUT:-/dev/stdout}"

if [[ "${EVENT_NAME:-}" != "pull_request" ]] || ! files="$(git diff --name-only "$BASE_SHA" "$HEAD_SHA")"; then
  printf 'code=true\ndaemon=true\nfrontend=true\n' >>"$out"
  exit 0
fi

code=false
daemon=false
frontend=false
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
done <<<"$files"

printf 'code=%s\ndaemon=%s\nfrontend=%s\n' "$code" "$daemon" "$frontend" >>"$out"
