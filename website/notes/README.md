# Docs research notes

Fact notes for the content pass of the website (`website/src/content/docs/docs/`), committed on the owner's wish of 2026-10-10 so other agents can work from them. Not built or published (outside `src/`). Delete this folder once every docs page is `ready`, and before the repository goes public. Code state: branch `feature/website` at `a033024` (2026-10-10). Answers of the owner are in `open-questions.md` under each question; they override TODO.md where they differ.

Each file goes page by page and heading by heading. It has terse facts with evidence (`path:line` relative to the repo root), UI texts quoted with their i18n file and key, limits, ports, timeouts and error codes. Each page also has "Not in code (plan only)" and "Questions users/developers will ask" (raw FAQ material). Each file ends with its own open questions; `open-questions.md` collects them without duplicates.

| File                             | Lines | Pages covered                                                                                                                                                                                                 |
| -------------------------------- | ----- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `getting-started-install.md`     | 492   | getting-started/what-is-pithyx, requirements, quick-start; install/usb-stick, firmware-and-boot, installer, first-setup, updates, recovery                                                                    |
| `user.md`                        | 484   | user/desktop, files, apps, account, file-access                                                                                                                                                               |
| `admin.md`                       | 523   | admin/storage, shared-folders, users-and-groups, permissions, file-services, network, security, apps-and-store, backup, logs-and-monitoring                                                                   |
| `developers-daemon-reference.md` | 380   | developers/daemon/architecture, api, errors-and-logging, development; reference/api, errors, glossary (about 55 terms), manifest-schema; appendix project/changelog, contributing, license, privacy, security |
| `developers-desktop.md`          | 353   | developers/desktop/architecture, i18n, performance, theming                                                                                                                                                   |
| `developers-apps-sdk.md`         | 441   | developers/apps/overview, first-app, manifest, backend, publishing; sdk/overview, sdk, ui, cli                                                                                                                |
| `open-questions.md`              |       | Everything that stayed unclear or where code, TODO.md and CLAUDE.md disagree                                                                                                                                  |

Not covered: `faq.mdx`, `index.mdx` and `reference/errors.mdx` are generated or already drafts. The FAQ raw material is in the "Questions ... will ask" sections.

## How the notes were made

- One research agent per area read code, i18n, TODO.md, CHANGELOG.md, README, CONTRIBUTING and SECURITY. Nothing was built or run, so numbers come from reading the code.
- TODO.md plans are only listed as facts where code shows them; everything else is under "Not in code (plan only)".
- Three files are longer than the 400-line target (admin, install, user). The extra lines are evidence and were not cut.

## Spot checks against the code (3 per area, all confirmed)

| Area                    | Fact                                                                                                                         | Evidence                                                          |
| ----------------------- | ---------------------------------------------------------------------------------------------------------------------------- | ----------------------------------------------------------------- |
| getting-started+install | No box-side updater: `daemon/src/Pithyx.Daemon/Updates` does not exist, though `tools/release/update-file.ts:2` refers to it | `ls`, `update-file.ts:2`                                          |
| getting-started+install | Installer is x86-64 only (`grub-install --target=x86_64-efi` hard-coded)                                                     | `Installer/SystemInstaller.cs:415`                                |
| getting-started+install | System disk minimum 40 GB (decimal)                                                                                          | `Installer/InstallerFacts.cs:12`                                  |
| user                    | No self-service password change; only `PUT /api/users/{id}/password`, admin only                                             | `Users/UserEndpoints.cs:51`, `UserService.cs:136`                 |
| user                    | Recycle bin retention default 30 days, 0 to 3650                                                                             | `Database/Migrations/0007_trash.sql:4`                            |
| user                    | FTP passive ports 50000 to 50099                                                                                             | `FileServices/Ftp/FtpServer.cs:27`                                |
| admin                   | Encrypted pool refused with `storage.encryption.unavailable`                                                                 | `Storage/StoragePlanner.cs:323`                                   |
| admin                   | Network confirmation window 60 s                                                                                             | `Network/NetworkConfirmation.cs:10`                               |
| admin                   | SMB on and workgroup `WORKGROUP` in a fresh DB; users get the SMB right by default                                           | `Migrations/0017_file_services.sql:12,15`                         |
| daemon+reference        | Password minimum 8 (TODO.md says 12)                                                                                         | `Auth/CredentialRules.cs:15`                                      |
| daemon+reference        | Cookie `pithyx_session`, 7 days idle, 30 days absolute; CSRF header `X-Pithyx-Request`                                       | `Auth/SessionService.cs:13-15`, `Auth/CsrfMiddleware.cs:11`       |
| daemon+reference        | Plain HTTP only today, HTTPS on 443 waits for M5                                                                             | `Program.cs:202`, `Hosting/PlainHttpRedirect.cs:4`                |
| desktop                 | Bundle limits 200 KB sign-in, 150 KB desktop (gzip), 5 percent tolerance on baseline 147995                                  | `tools/checks/bundle-budget.ts:20-22`, `bundle-budget.json:2`     |
| desktop                 | Reconnect backoff `min(30 s, 0.5 s * 2^n)` randomized to 50 to 100 percent                                                   | `desktop/src/realtime/reducers.ts:127`                            |
| desktop                 | Layout saves debounced 1 s                                                                                                   | `desktop/src/wm/persistence.ts:19`                                |
| apps+sdk                | `pithyx dev` defaults port 7400, host 127.0.0.1                                                                              | `packages/cli/src/cli.ts:80-81`                                   |
| apps+sdk                | Endpoint name 32 chars in manifest vs 63 in SDK                                                                              | `sdk/schema/manifest.v1.schema.json:260`, `sdk/src/backend.ts:17` |
| apps+sdk                | App uids 40000 to 49999                                                                                                      | `Apps/Docker/DockerHost.cs:46`                                    |

Line numbers in the notes are exact where the agents read them. In a few places only the file and function are given; check these before you quote them.
