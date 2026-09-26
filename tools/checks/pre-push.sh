#!/usr/bin/env bash
# Local stand-in for branch protection: master and dev only change through pull requests.
# The first push that creates a protected branch on the remote is allowed (repository bootstrap).
set -euo pipefail

zero=0000000000000000000000000000000000000000
status=0

while read -r _ local_sha remote_ref remote_sha; do
  case "$remote_ref" in
    refs/heads/master | refs/heads/dev) ;;
    *) continue ;;
  esac
  branch=${remote_ref#refs/heads/}
  if [[ "$local_sha" == "$zero" ]]; then
    echo "pre-push: refusing to delete protected branch '$branch'." >&2
    status=1
  elif [[ "$remote_sha" != "$zero" ]]; then
    echo "pre-push: direct pushes to '$branch' are not allowed. Open a pull request against dev." >&2
    status=1
  fi
done

exit "$status"
