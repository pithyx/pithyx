# Open questions

Collected from the six notes files (the number in brackets is the item in that file's own list: `inst`, `user`, `admin`, `daemon`, `desk`, `apps`). Docs follow the code as it is. Until a question is answered, the affected heading stays `status: draft`. Answers of the owner follow the question; they override TODO.md.

## A. Code and CLAUDE.md/TODO.md disagree (affects many pages)

1. **HTTPS.** CLAUDE.md "Network" says HTTPS on 443 with a self-signed certificate and a trust wizard. The code serves plain HTTP on 7484 only, and port 80 redirects there (`Program.cs:202`: "HTTPS on 443 arrives with the TLS work in M5"). There is no local CA and no Let's Encrypt. Passkeys and the service worker are off. The web login and WebDAV Basic auth travel unencrypted. Proposal: every page uses `http://<box>:7484` and admin/security says "HTTPS is not built yet" in one sentence. (inst 4, user 12, admin 2, daemon 2)
   - **Answer (owner, 2026-10-10):** HTTPS will be built before the release. Until then the pages describe HTTP on 7484 and admin/security stays `draft`.
2. **System updates.** There is no box-side updater: no `daemon/.../Updates` folder, no settings page, no `update.*` error codes. Only the build and sign side exists (`tools/release/*`). `keys.json` has only `pithyx-beta-1`, not `pithyx-release-1`. Settings nav keys "Updates" and "About" exist in `settings.json` without pages. Should `install/updates.md` stay a placeholder or say "not available yet"? (inst 1, 2, 19; admin 1)
   - **Answer (owner, 2026-10-10):** The updater is being built on another branch; this checkout shows an intermediate state. Write `install/updates.md` from that branch once it is merged.
3. **Password minimum.** The code requires 8 characters (`CredentialRules.cs:15`) and TODO.md 3.3 says 12. The docs will say 8. (daemon 1)
   - **Answer (owner, 2026-10-10):** TODO.md is outdated. 8 characters is intended for v0.1.
4. **Self-service password change.** Users cannot change their own password; only an administrator resets it (`UserEndpoints.cs:51`). TODO.md:1166 and :1374 imply otherwise. Is that intended for v0.1? `user/account.md` would then say "ask an administrator". (user 1)
   - **Answer (owner, 2026-10-10):** Intended for v0.1: no self-service password change, an administrator resets it.
5. **Pool encryption.** CLAUDE.md describes ZFS native encryption. The wizard refuses "Encrypt with a key" with `storage.encryption.unavailable`, and TODO step 9 is after v0.2. (admin 3)
   - **Answer (owner, 2026-10-10):** Comes later. v0.1 is a beta and ships without pool encryption (fixed).
6. **Not implemented although CLAUDE.md or README names them:** NFS (README.md:11, CLAUDE.md "Protocol access"), support bundles, SSH MOTD with the logo, per-pool spindown, the keep-alive lease for app backends, watchdog/`sd_notify`/HybridCache (daemon/CLAUDE.md), the API socket `/run/pithyx`, streaming directory listings over the WebSocket and a sorting Web Worker (desktop/CLAUDE.md). Should the nested CLAUDE.md files be corrected? (inst 3, admin 4-7, apps 5, daemon 3, 5, desk 3)
   - **Answer (owner, 2026-10-10):** Corrected: README, CLAUDE.md, daemon/CLAUDE.md and desktop/CLAUDE.md now say what is built and mark the rest "planned, not built yet" (power profiles and `/etc/exports` too).
7. **Timing regression rule.** desktop/CLAUDE.md says a regression of more than 5 percent fails the build. The code uses absolute timing budgets, and only the sign-in bundle has a 5 percent tolerance. (desk 1)
8. **Local login.** `image/rootfs/usr/lib/pithyx/console.sh:47` shows "Alt+F2: log in", but CLAUDE.md says the live and installed systems have no login. What does tty2 offer, and to whom (only users with the SSH right)? (inst 12, admin 12)
   - **Answer (owner, 2026-10-10):** In progress, noted by the owner.
9. **Recovery key position in the installer.** TODO.md:484 says step 4; the code shows it after the install and before the restart (the D299 deviation the owner should confirm). (inst 7)

## B. Owner decisions for the docs

10. **Sessions and devices** (`user/account.md`): there is no list or revoke for sessions. "Devices" only exists as "Get desktop from". Should the heading be renamed? (user 2)
11. **Admin resetting another user's 2FA:** no endpoint found. What does a non-admin who lost the phone do? (user 14, inst 13)
12. **Recycle bin retention** can only be changed through the API (`PUT /api/shares/{id}/trash`), not in the UI. Document the API call or say "fixed at 30 days for now"? (user 3, admin 8)
    - **Answer (owner, 2026-10-10):** Noted by the owner; ask an agent later.
13. **SMB on by default** in a fresh DB, and new users get the SMB right (`0017_file_services.sql:12,15`). Is that intended? (admin 15)
    - **Answer (owner, 2026-10-10):** Noted by the owner; ask an agent later.
14. **Time servers:** when empty, timesyncd uses the Debian defaults, an outbound connection. Mention it under "What leaves the box" and on `project/privacy.md`? (admin 19)
    - **Answer (owner, 2026-10-10):** Noted by the owner; ask an agent later.
15. **Official catalog on stable** shows "Fetch failed" until the owner promotes it once. Should the Store pages keep quiet about it or mention it? (admin 17)
16. **Public vs internal:** dev01, owner and Claude accounts in CLAUDE.md stay off the site. Glossary scope: leave out "owner" and "management card"? (daemon 10, 11)
17. **Undocumented shortcuts** Alt+Shift+PageUp/PageDown/Space exist only in `keys.ts`. Document them? (user 16)
18. **README status:** README says "Nothing here is usable yet". Should `getting-started/what-is-pithyx.md` say the same (pre-release)? (inst 3)
19. **Hardware wording:** the installer minimum is 4 GB RAM (400 MB tolerance), the reference is 8 GB. Is there a recommendation for ZFS memory? Wi-Fi is not supported (only `Type=ether`). arm64 is plan only (`x86_64-efi` is hard-coded). (inst 14, 16)
20. **Headless box:** there is no mDNS or WS-Discovery. Users find the address through the router or the console. Is that acceptable as the documented way? (inst 18, user 11)
21. **ISO distribution:** there is no download location or ISO signature, only `<iso>.sha256`, and the TODO.md size of 520 MB is stale. `install/usb-stick.md` stays draft until this exists. (inst 5)
22. **Contributing to the project catalogs:** only D328 describes the process (who reviews, how long it takes). (apps 11)

## C. Needs a test on dev01 or a closer code read

23. Reinstall: do old pools need a forced import (new hostid)? Are pools imported automatically at boot? What is the order of pools and backup restore, and does the backup include network settings? (inst 10, 11)
    - **Answer (owner, 2026-10-10):** Noted by the owner; ask an agent later.
24. Installer cookie `pithyx_installer` has no expiry. Does closing the browser mid-install lock out the owner ("Installation in use")? (inst 9)
    - **Answer (owner, 2026-10-10):** Noted by the owner; ask an agent later.
25. Does FTP delete go through the recycle bin (SFTP bypasses it)? Who may restore from the bin? (user 10, admin 16)
    - **Answer (owner, 2026-10-10):** Noted by the owner; ask an agent later.
26. Firewall: no rules for 445, 22, 21 or 50000 to 50099 were found, only `inet pithyx apps-input`. Docs must not claim ports are opened or filtered. Is there a ruleset in the image? (user 11, admin 18)
    - **Answer (owner, 2026-10-10):** Noted by the owner; ask an agent later.
27. Single disk to mirror: `ZfsPlans` has an attach plan and `wizard.layouts.single.fixed` promises it, but no UI button was found. (admin 11)
28. Is the dataset `<pool>/pithyx/volumes` created on demand? (admin 14)
29. Is the `sdk` range in the manifest checked against the host protocol, or only for syntax? (apps 3)
30. When are backend images pulled for a sideloaded package without `backend/images/`, and is internet needed? (apps 8)
31. Are the elevated rights `host.network`, `host.pid`, `device:` and `capability:` implemented end to end, or only accepted by the schema? (apps 7)
32. `@pithyx/ui` reads `--color-*` while the SDK sets `--pithyx-*`. Does a sandboxed app have to import `tokens.css`? (apps 6)
33. Not read yet: the device cookie name and lifetime, the actual 90-day layout expiry value, daemon validation of accent and wallpaper, the Vite env var for the daemon URL, the full topic list, and the test for C# error codes against `errors.json` parity. (desk 4-5, 9, 11, 13; daemon 6, 12)
34. Is it intended that the 50-window limit is silent, and that the compact title bar has only Close? (user 8, 17)
35. Folder upload (drag a folder) is unclear: the code takes `File[]` only. (user 9)

## D. Stale or broken texts found in passing (bugs, not docs)

36. The text of `app.backend.unsupported` says "installs apps without a backend only", but the code is unused and backends run since A14. The generated error reference will show the stale text. (apps 1, user 5, admin 9)
    - **Answer (owner, 2026-10-10):** Noted by the owner; ask an agent later.
37. Endpoint name length: the manifest and proxy allow `{0,31}`, `sdk/src/backend.ts:17` allows `{0,62}`. (apps 2)
    - **Answer (owner, 2026-10-10):** Noted by the owner; ask an agent later.
38. The `packages/cli` README lists 4 commands and misses `build` and `catalog`. Its dist-tag statement differs from D306. (apps 4)
    - **Answer (owner, 2026-10-10):** Noted by the owner; ask an agent later.
39. NOTICE names only `sdk/` as MIT, but `packages/ui` and `packages/cli` are MIT too. (daemon 8)
    - **Answer (owner, 2026-10-10):** Noted by the owner; ask an agent later.
40. `storage.scrub.finished` and `storage.disk.warm` notifications have no emitter. (admin 10)
41. Files `places.*` keys are unused. The start menu's settings and file search and "Recent files" are always empty (`Desktop.tsx:662-663`). (user 6, 7)
42. Installer progress step `raid` says "Creating the mirror" also for a single disk. (inst 8)
43. TODO.md:1367 says double-click downloads; the code opens the Viewer. (user 4)
44. `tools/release/update-file.ts:2` refers to the non-existent `daemon/src/Pithyx.Daemon/Updates`. (inst 1)
45. Adding a language needs edits in five places with `'en'/'de'` hard-coded (`i18n.ts:131,191`, `vite.config.ts` ariaLocales, `SystemRegion.Languages`, the design-shots matrix, the SDK i18n test). There is no single list. (desk 6)
46. SECURITY.md has no response times and no supported-versions table. (daemon 9)

## E. Website tooling

47. `reference/api.md` and `reference/manifest-schema.md` need a generator (build-time like `errorCodes.ts`, or checked-in output); none exists in `website/`. (daemon 15)
48. `/api/openapi.json` is served anonymously in every build (`Program.cs:228`). Mention it on `developers/daemon/api.md`? (daemon 13)

## F. Found while writing the English pages (2026-10-10)

49. **Generated references.** `reference/api.md` and `reference/manifest-schema.md` have only a short introduction (`draft`). They should be generated at build time, the endpoint list from `packages/api-client/openapi.json` and the field list from `sdk/schema/manifest.v1.schema.json`, like `reference/errors` from `errors.json` (see 47). Nothing was built yet because the generator cannot be tested without a build.
50. **App permission texts.** The Store describes `files.read` as "Reads files in the shared folders of the user who uses it." and `files.write` as "Creates, changes and deletes files in the shared folders the user may write to." (`i18n/en/store.json` `permission.files.*`). The SDK grants `files.read`/`files.write` only for files the user opens with the app or picks in a dialog; browsing shared folders needs `files.shares` (notes `developers-apps-sdk.md:155-157`, `sdk/src` files API). `user/apps.md` follows the SDK. Should the Store texts change?
51. **Registry token of a catalog.** Private images of an own catalog need a read token (`user:token`), accepted only through `PATCH /api/store/catalogs/{key}` with `registryToken` (`Apps/Catalog/StoreContracts.cs:111`); the Store has no field for it (`desktop/src/apps/sdk/store.ts:93` passes it, no UI uses it). `developers/apps/publishing.md` says so. Add a field?
52. **WebDAV over plain HTTP on Windows and macOS.** `user/file-access.md` gives `http://<box>:7484/webdav/` for "Map network drive" and "Connect to Server" and warns that the system may refuse a password over plain HTTP. Not tested; needs a check on real clients.
53. **Code of conduct.** The repository has none; `project/contributing.md` does not mention one. Add one before the repository goes public?
54. **Tokens for scripts.** There are no personal access tokens; a script signs in like the desktop (`developers/daemon/api.md`). Intended for v0.1?
55. **German pages.** Only the English pages under `docs/` were written in this pass; the German pages under `de/docs/` are still placeholders and must follow (website/CLAUDE.md wants both in the same commit).
    - **Done (2026-10-10):** every English page under `docs/` except `install/updates.md` is translated to `de/docs/` with the same frontmatter, status and sidebar order.

## G. Found while writing the FAQ (2026-10-10)

56. **Pools after a reboot** (FAQ `pool-missing-after-restart`, `draft`). The answer says a pool created or imported for use comes back at every boot. That relies on Debian's ZFS pool cache: the read-write import uses the default cache file, the read-only preview `cachefile=none` (`Storage/Zfs/ImportPlans.cs:58-64`), and `pithyx.service` starts after `zfs-import.target`. Not tested on a box: confirm with a reboot, and how a pool whose disks are all missing shows after the boot (see 23).
57. **Lost authenticator for non-admins** (FAQ `lost-2fa`, `draft`). The answer says the interface cannot turn off two-factor sign-in for another user (see 11). Rewrite once that is decided.
58. **Answers that depend on HTTPS** (FAQ `https-warning`, `encrypted-traffic`, `ports`, `draft`): they describe plain HTTP on 7484 and advise against forwarding ports to the internet. Rewrite when HTTPS is built (see 1).
59. **System updates** (FAQ `system-updates`, `draft`): says this pre-release has no update function and a new version means a reinstall with backup and pool import. Rewrite together with `install/updates.md` after the update branch is merged (see 2).
60. **Download and scripts** (FAQ `download` and `api-scripts`, `draft`): follow 21 (no public download, no ISO signature) and 54 (no personal access tokens).

## H. Found while writing the landing page (2026-10-10)

61. **Updates card.** The landing page had a feature card "Updates" ("Signed system updates and app updates with rollback") linking to `install/updates.md`. There is no box-side updater in this checkout (see 2), so the card became "Configuration backup" and app updates with rollback moved into the "Apps" card. Bring an "Updates" card back once the update branch is merged.
62. **HTTP on the landing page.** The spec row "Web desktop" says "HTTP on port 7484 (HTTPS not built yet)" and "Careful by default" tells readers to keep the box on a trusted network (see 1). Rewrite both when HTTPS is built. The arm64 note was dropped from "Platform" (plan only, see 19).
