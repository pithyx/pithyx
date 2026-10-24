#!/usr/bin/env bash
# Creates the ruleset that protects stable and beta on GitHub (D367; dev is the owner's test branch and takes pushes): no deletion, no force push, pull request required, and the
# CI jobs green. The required contexts are the job names of .github/workflows/ci.yaml; a job skipped by its `if` (no
# daemon change, no docs change) counts as passed. e2e is not required: it runs on request (D278) and nightly.
# Run it once, when the repository goes public (rulesets on private repositories need a paid plan). It is not
# idempotent: a second run creates a second ruleset. The Actions settings for fork pull requests are not part of a
# ruleset; the owner sets them in the web interface (CLAUDE.md, CI).
set -euo pipefail

repo=${1:-pithyx/pithyx}

gh api --method POST "repos/$repo/rulesets" --input - <<'JSON'
{
  "name": "protected-branches",
  "target": "branch",
  "enforcement": "active",
  "conditions": {
    "ref_name": { "include": ["refs/heads/stable", "refs/heads/beta"], "exclude": [] }
  },
  "rules": [
    { "type": "deletion" },
    { "type": "non_fast_forward" },
    {
      "type": "pull_request",
      "parameters": {
        "required_approving_review_count": 0,
        "dismiss_stale_reviews_on_push": false,
        "require_code_owner_review": false,
        "require_last_push_approval": false,
        "required_review_thread_resolution": false
      }
    },
    {
      "type": "required_status_checks",
      "parameters": {
        "strict_required_status_checks_policy": false,
        "required_status_checks": [
          { "context": "changes" },
          { "context": "daemon" },
          { "context": "frontend" },
          { "context": "website" },
          { "context": "repo" }
        ]
      }
    }
  ]
}
JSON
