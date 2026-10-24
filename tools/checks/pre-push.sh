#!/usr/bin/env bash
# Local stand-in for branch protection: stable and beta only change through pull requests (D367); dev takes pushes.
# The first push that creates a protected branch on the remote is allowed (repository bootstrap).
set -euo pipefail

zero=0000000000000000000000000000000000000000
status=0

while read -r _ local_sha remote_ref remote_sha; do
  case "$remote_ref" in
    refs/heads/stable | refs/heads/beta) ;;
    *) continue ;;
  esac
  branch=${remote_ref#refs/heads/}
  if [[ "$local_sha" == "$zero" ]]; then
    echo "pre-push: refusing to delete protected branch '$branch'." >&2
    status=1
  elif [[ "$remote_sha" != "$zero" ]]; then
    echo "pre-push: direct pushes to '$branch' are not allowed. Push to dev and open a pull request from there." >&2
    status=1
  fi
done

exit "$status"
