## Summary

<!-- What changes and why. Link the issue, the TODO.md step or the decision (Dnnn). -->

## How it was tested

<!-- Tests added or run, CI jobs, the integration.sh result for storage, account, command runner or Docker changes, a check on a dev box. -->

## Checklist

- [ ] Build, lint and tests pass (`dotnet test` in `daemon/`, `pnpm lint`, `pnpm typecheck`, `pnpm test`, `pnpm build`, `pnpm check:repo`); generated clients are up to date.
- [ ] New visible text has i18n keys in `en` and `de`; new error codes have translations.
- [ ] Docs under `website/src/content/docs/` are checked and updated in English and German, or the change needs none.
- [ ] `CHANGELOG.md` has a line under `Unreleased`; `TODO.md` is checked off.
- [ ] No em dashes and no non-English text outside translation files.
- [ ] Every commit is signed off (`git commit -s`, DCO).
