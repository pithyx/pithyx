---
title: 'Contributing'
description: 'How to report bugs, propose changes and send pull requests.'
status: ready
audience: developer
sidebar:
  order: 3
---

Pithyx is developed in the open on GitHub at `pithyx/pithyx`. Pull requests target the `dev` branch, commits carry a sign-off, and each pull request follows a short checklist. The first release is 1.0.0, marked beta.

## Reporting bugs

Open an issue in the repository and choose **Bug report** or **Feature request**; blank issues are off, and the chooser links these docs. The bug form asks what happened, the steps, the Pithyx version, the area, the hardware and browser, and the error code with log lines (without secrets). Do not report security problems in public: see [Reporting a vulnerability](/docs/project/security/).

## Pull requests

1. Set up the build as described in [Developing the daemon](/docs/developers/daemon/development/). `pnpm install` also installs the git hooks.
2. Branch from `dev` as `feature/<name>`, `fix/<name>` or `hotfix/<name>`. Changes go from `dev` (integration) to `beta` (the beta channel) to `stable` (the default branch, where releases are tagged); `beta` and `stable` receive only pull requests from the branch before them and hotfixes. Never push directly to `stable` or `beta`; the branch ruleset and the pre-push hook refuse it.
3. Open the pull request against `dev`. Its title follows the commit format below. The pull request template asks for a summary, how it was tested, and the checklist below. CI on a pull request from a fork starts only after a maintainer approved the run, and it runs on GitHub-hosted runners.

Checklist:

- Build, lint and tests pass: `dotnet test` in `daemon/`, `pnpm lint`, `pnpm typecheck`, `pnpm test`, `pnpm build`, `pnpm check:repo`.
- Generated clients are up to date (`pnpm generate`).
- New visible text has keys in `en` and `de`, and new error codes have translations.
- The changelog has a line under "Unreleased".
- The documentation under `website/` was checked and updated in English and German where the change touches it.
- No em dashes, and no non-English text outside translation files.
- Every commit is signed off.

YAML files in the repository end in `.yaml`; the repo check refuses `.yml`.

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
