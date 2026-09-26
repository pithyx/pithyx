#!/usr/bin/env bash
# Creates the ruleset that protects master and dev on GitHub.
# Not usable yet: rulesets on private repositories need a paid plan or a public repository.
set -euo pipefail

repo=${1:-vyxis/vyxis}

gh api --method POST "repos/$repo/rulesets" --input - <<'JSON'
{
  "name": "protected-branches",
  "target": "branch",
  "enforcement": "active",
  "conditions": {
    "ref_name": { "include": ["refs/heads/master", "refs/heads/dev"], "exclude": [] }
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
          { "context": "daemon" },
          { "context": "frontend" },
          { "context": "repo" },
          { "context": "dco" }
        ]
      }
    }
  ]
}
JSON
