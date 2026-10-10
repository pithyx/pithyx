---
title: 'Contributing'
description: 'How to report bugs, propose changes and send pull requests.'
status: draft
audience: developer
sidebar:
  order: 3
---

Pithyx is developed in the open on GitHub at `pithyx/pithyx`. Pull requests target the `dev` branch, commits carry a sign-off, and each pull request follows a short checklist. The project is pre-alpha: there is no release yet.

## Reporting bugs

Open an issue in the repository. Describe what you did, what you expected and what happened, and include the Pithyx version or commit. Do not report security problems in public: see [Reporting a vulnerability](/docs/project/security/).

## Pull requests

1. Set up the build as described in [Developing the daemon](/docs/developers/daemon/development/). `pnpm install` also installs the git hooks.
2. Branch from `dev` as `feature/<name>`, `fix/<name>` or `hotfix/<name>`. `master` is stable and receives only release pull requests and hotfixes. Never push directly to `master` or `dev`; the pre-push hook refuses it.
3. Open the pull request against `dev`. Its title follows the commit format below.

Checklist:

- Build, lint and tests pass: `dotnet test` in `daemon/`, `pnpm lint`, `pnpm typecheck`, `pnpm test`, `pnpm build`, `pnpm check:repo`.
- Generated clients are up to date (`pnpm generate`).
- New visible text has keys in `en` and `de`, and new error codes have translations.
- The changelog has a line under "Unreleased".
- The documentation under `website/` was checked and updated in English and German where the change touches it.
- No em dashes, and no non-English text outside translation files.
- Every commit is signed off.

Code style: short English comments only where logic is not obvious, warnings are errors in C# and TypeScript, and no abstraction without a second real implementation.

## Commit rules

Commits use a gitmoji, a type, an optional scope and an imperative subject in English:

```
<gitmoji> <type>(<scope>): <subject>
```

Use the Unicode emoji, not a `:shortcode:`. Types: `feat`, `fix`, `docs`, `style`, `refactor`, `perf`, `test`, `build`, `ci`, `chore`, `revert`. Examples:

```
✨ feat(desktop): add window snapping
🐛 fix(daemon): handle missing lsblk serial
♻️ refactor(storage): extract zpool parser
```

Every commit must be signed off to certify the [Developer Certificate of Origin](https://developercertificate.org/): commit with `git commit -s`. The trailer must match the commit author. A hook checks the message and CI rejects commits that are not signed off.
