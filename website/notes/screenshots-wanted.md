# Screenshots wanted

Pictures that would help the docs pages, collected during the English content pass (2026-10-10). Make them from `pnpm design:shots` or a dev box (PNG or WebP, 16:10, at least 1600 px wide, see `website/CLAUDE.md`), put them in `website/src/assets/screenshots/` and reference them from the page. Format: page | what should be visible | light or dark.

## Getting started and install

- /docs/install/installer/ | Requirements page of the installer with the five checks passed | light
- /docs/install/installer/ | System disk step with "Mirror on two disks" switched on and a second disk chosen | light
- /docs/install/installer/ | Recovery key page "Pithyx is installed" (with a sample key, not a real one) | light
- /docs/install/first-setup/ | Storage app, "Create pool" wizard at the step "Encryption" | light
- /docs/install/recovery/ | Sign-in page, form "Reset your password" for administrators | light

## User

- /docs/user/desktop/ | Desktop with two snapped windows, the taskbar at the bottom, a shortcut and a sticky note | light
- /docs/user/desktop/ | Pithyx menu open with the account menu (Two-factor authentication, Language, Appearance, Taskbar position) | dark
- /docs/user/desktop/ | Window menu opened from the app icon in a title bar | light
- /docs/user/files/ | Files with the Shares sidebar, list view and the context menu of a file | light
- /docs/user/files/ | Dialog "Names already exist" with Skip them, Replace them and Keep both | light
- /docs/user/files/ | Recycle bin of a shared folder with Restore and Delete for good | light
- /docs/user/account/ | Two-factor setup dialog with the QR code, then the recovery codes | light
- /docs/user/file-access/ | Settings > File services with the SMB, SFTP, WebDAV and FTP switches | light

## Admin

- /docs/admin/storage/ | Storage, Disks tab with the column "For a new pool" (Available, Needs confirmation, Not available) | light
- /docs/admin/storage/ | "Create pool" wizard, step "Layout" with the recommendation | light
- /docs/admin/storage/ | Snapshots tab with schedules | dark
- /docs/admin/shared-folders/ | Settings > Shared folders, edit dialog with Rights and Network access | light
- /docs/admin/network/ | Settings > Network with the banner "... left to keep the new network settings" | light
- /docs/admin/apps-and-store/ | Store settings, Updates (policy, check time, channel) | light
- /docs/admin/backup/ | Settings > Backup and restore after "Check backup" | light
- /docs/admin/logs-and-monitoring/ | Log Center with filters and the details pane | dark
- /docs/admin/logs-and-monitoring/ | Resource Monitor, Performance view | light

## Developers

- /docs/developers/apps/first-app/ | `pithyx dev` mock desktop with an app window and the side panel (theme, permissions, backend status) | light
- /docs/developers/apps/first-app/ | Store, Installed tab, dialog "Install an app" with a package from a file and the unverified warning | light
- /docs/developers/desktop/theming/ | Account menu with Appearance, Accent color and Wallpaper open | dark
- /docs/developers/desktop/i18n/ | Account menu, Language with Automatic, English and Deutsch | light
