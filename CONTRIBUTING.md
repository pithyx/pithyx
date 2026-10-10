# Contributing to Pithyx

Thanks for your interest. Pithyx is pre-alpha, so expect things to move fast.

## Branches

- `master` is stable. It only receives release pull requests from `dev` and hotfixes.
- `dev` is the integration branch for beta builds.
- Work happens in `feature/<name>`, `fix/<name>` or `hotfix/<name>`. Pull requests target `dev`.
- Hotfixes branch from `master` and are merged back into both `master` and `dev`.
- Never push directly to `master` or `dev`. The pre-push hook refuses it.

## Commit messages

Gitmoji plus Conventional Commits, in English, imperative mood, no em dashes:

```
<gitmoji> <type>(<scope>): <subject>
```

Use the Unicode emoji, not the `:shortcode:`. Types: `feat`, `fix`, `docs`, `style`, `refactor`, `perf`, `test`, `build`, `ci`, `chore`, `revert`. Examples:

```
✨ feat(desktop): add window snapping
🐛 fix(daemon): handle missing lsblk serial
📝 docs(website): explain spindown
♻️ refactor(storage): extract zpool parser
✅ test(auth): cover rate limit
🔧 chore: update lefthook
👷 ci: add DCO check
```

Pull request titles use the same format. The `commit-msg` hook and CI check it.

## Developer Certificate of Origin

Every commit must be signed off to certify the [Developer Certificate of Origin](https://developercertificate.org/):

```bash
git commit -s
```

This adds a `Signed-off-by: Your Name <you@example.com>` trailer matching the commit author. CI rejects pull requests with commits that are not signed off.

## Setup

See [Development](README.md#development) in the README. `pnpm install` also installs the git hooks (lefthook).

## Pull request checklist

- [ ] Build, lint and tests pass locally (`dotnet test` in `daemon/`, `pnpm lint`, `pnpm typecheck`, `pnpm test`, `pnpm build`, `pnpm check:repo`).
- [ ] Generated clients are up to date.
- [ ] New visible text has i18n keys in `en` and `de`.
- [ ] New error codes have translations.
- [ ] `TODO.md` is checked off and `CHANGELOG.md` is updated.
- [ ] Docs in `website/` were checked and updated in English and German, or the change was judged not doc-worthy (D340).
- [ ] No em dashes and no non-English text outside translation files.
- [ ] Every commit is signed off.

The same checklist is in the pull request template (`.github/pull_request_template.md`).

## License and attribution

Contributions to the system are licensed under the [GNU AGPL-3.0](LICENSE) with the additional terms in [NOTICE](NOTICE); contributions to `sdk/`, `packages/ui` and `packages/cli` under the MIT License of those folders. Pithyx was originally created by [Schecher1](https://github.com/Schecher1). NOTICE asks every fork and redistribution to keep the attribution "Based on Pithyx, originally created by Schecher1" in its source, its About page and its docs; a fork may use its own name but states that it is based on Pithyx by Schecher1. Do not remove or reword that attribution in a pull request.
