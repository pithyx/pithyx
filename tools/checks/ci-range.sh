#!/usr/bin/env bash
# Prints the git revision range CI checks commits in. A push that creates a branch has no
# previous sha, so HEAD_SHA and all its ancestors are checked.
set -euo pipefail

if [[ -z "${BASE_SHA:-}" || "$BASE_SHA" =~ ^0+$ ]]; then
  echo "$HEAD_SHA"
else
  echo "$BASE_SHA..$HEAD_SHA"
fi
