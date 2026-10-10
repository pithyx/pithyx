#!/usr/bin/env bash
# Decides which CI jobs a pull request needs, from the files it changes, and writes the answers to $GITHUB_OUTPUT:
#   code      anything besides Markdown and website/ changed (E2E runs)
#   daemon    the daemon, its translations, the OpenAPI document, the shared schemas or the CI setup changed
#   frontend  anything besides docs and daemon/ changed
#   website   website/ or what it reads (the error texts, the design tokens, the sources its references and examples are made from, the lockfile) changed
# Pushes to dev and master run everything; so does a range git cannot resolve.
set -euo pipefail

out="${GITHUB_OUTPUT:-/dev/stdout}"

if [[ "${EVENT_NAME:-}" != "pull_request" ]] || ! files="$(git diff --name-only "$BASE_SHA" "$HEAD_SHA")"; then
  printf 'code=true\ndaemon=true\nfrontend=true\nwebsite=true\n' >>"$out"
  exit 0
fi

code=false
daemon=false
frontend=false
website=false
while IFS= read -r file; do
  [[ -z "$file" ]] && continue
  case "$file" in
    website/* | i18n/* | packages/ui/tokens.css | packages/ui/src/* | packages/cli/src/* | sdk/src/* | sdk/schema/* | pnpm-lock.yaml | .github/workflows/*) website=true ;;
  esac
  case "$file" in
    *.md | website/* | .github/ISSUE_TEMPLATE/*) continue ;;
  esac
  code=true
  case "$file" in
    # sdk/schema holds the contracts and fixture corpora that the daemon's tests read as well.
    daemon/* | i18n/* | tools/i18n/* | global.json | .github/* | packages/api-client/* | sdk/schema/*) daemon=true ;;
  esac
  case "$file" in
    daemon/*) ;;
    *) frontend=true ;;
  esac
done <<<"$files"

printf 'code=%s\ndaemon=%s\nfrontend=%s\nwebsite=%s\n' "$code" "$daemon" "$frontend" "$website" >>"$out"
