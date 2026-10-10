# User docs notes (research, as of branch feature/website, 2026-10-10)

Evidence form: `path:line` from repo root. i18n: `file:key` (en). Only what code does today.
D-numbers only for plan-only items.

## PAGE desktop.md

### Signing in

- First run order: setup (create admin) -> sign-in -> desktop. desktop/src/App.tsx:109
- Setup: fields "User name", "Display name (optional)", "Password", "Repeat password"; button "Create administrator"; "Step {current} of {total}". i18n/en/auth.json:setup.\*
- Setup hint for user name: "Lowercase letters, digits, hyphens and underscores, starting with a letter or underscore." auth.json:setup.usernameHint
- User name rule: `^[a-z_][a-z0-9_-]{0,31}$` (max 32), lower-cased, trimmed. daemon/.../Auth/CredentialRules.cs:108
- Reserved names (root, daemon, sudo, docker, pithyx, ...) and `systemd-*` refused (`auth.username.reserved`). CredentialRules.cs:21-39
- Display name max 64 chars, no control chars (`auth.display_name.invalid`). CredentialRules.cs:17,70
- Recovery key shown once after setup: "Your recovery key", buttons "Copy key", "Print", checkbox-like "I have stored the recovery key", "Continue to the desktop". auth.json:recoveryKey.\*
- Recovery key format `PTHX-` + 26 chars in groups of 4 (Crockford Base32). AuthService.cs:16,295
- Sign-in card: heading "Welcome back", fields "User name" / "Password", button "Sign in" ("Signing in..."), link "Forgot password?". auth.json:login.\*
- Autocomplete: username `username`, password `current-password`, TOTP `one-time-code` (numeric, maxLength 7), recovery code `off`. desktop/src/auth/LoginScreen.tsx:269-270
- Sign-in page has a language selector in the footer. LoginScreen.tsx:91
- Wrong credentials: "The user name or password is incorrect." (`auth.credentials.invalid`) errors.json
- Rate limit on failed password/TOTP/recovery: 5 per user name+address and 30 per address, window 5 min; error `auth.rate.limited` "Try again in {n} seconds". daemon/.../Auth/LoginRateLimiter.cs:13-15; successful logins do not count (:7-9)
- Deactivated account: `auth.account.disabled` "This account is deactivated. Ask an administrator to activate it again." AuthService.cs:93
- Needs right "Sign in to the desktop" else `auth.desktop.not_allowed`. AuthService.cs:98; users.json:grants.right.desktopLogin
- Two-factor step: title "Two-factor authentication", "Code from your authenticator app", "Verify", links "Use a recovery code instead" / "Use a code from the app instead", "Back to sign in". auth.json:totp.\*
- 2FA challenge lives 5 min, bound to client address. daemon/.../Auth/LoginChallenges.cs:11
- Forgot password page: "Pithyx has no reset by email. Ask an administrator to set a new password for you."; admins: recovery key + new password. auth.json:forgot.\*
- Recovery reset: ends all sessions, turns 2FA off, issues a new recovery key; only for administrators. AuthService.cs:259-292; auth.json:forgot.doneIntro
- After login the page shows "Preparing your desktop..." auth.json:login.preparing
- Reload shows the desktop at once from a cached user hint (localStorage `pithyx.session`, holds no secret) and checks the session in background. desktop/src/auth/sessionHint.ts:5-8
- Sign out: account menu (Start menu profile) "Sign out"; layout save waits at most 1 s before sign-out. desktop.json:menu.signOut; desktop/src/wm/persistence.ts:123
- Today the box serves plain HTTP on 7484 (port 80 redirects there); no TLS yet. daemon/.../Program.cs:33,57; Hosting/PlainHttpRedirect.cs:4

### Windows and snapping

- Size classes by viewport width: compact < 600 px, medium 600-1023, expanded >= 1024. desktop/src/wm/sizeClass.ts:4-5; types.ts (SizeClass)
- Free windows (move, resize, snap by drag) only on expanded. wm/ManagedWindow.tsx:46-47,75-105
- Title bar buttons: "Minimize", "Maximize"/"Restore", "Close". desktop.json:window.\*; shell/Window.tsx:99-126
- Double-click on the title bar toggles maximize (expanded only, not on buttons). wm/ManagedWindow.tsx:83-87
- Resize handles on 8 edges/corners. ManagedWindow.tsx:15,91-106
- Drag a window to a screen edge: snap zone within 12 px of an edge; top edge = maximize; left/right = half. wm/geometry.ts:20,129-143
- Ultrawide (workspace >= 2400 px wide): edges give thirds (left-third / center-third at top middle / right-third). geometry.ts:22,131-141
- Pulling a maximized or snapped window away restores its free size under the pointer. geometry.ts:174-184; wm/pointer.ts:147
- Window menu: click the app icon in the title bar. Items: "Restore" (Alt+Shift+↓), "Move", "Size" (free windows only), "Minimize", "Maximize" (Alt+Shift+↑), "Snap left" (Alt+Shift+←), "Snap right" (Alt+Shift+→), "Close". wm/WindowMenu.tsx:48-68; desktop.json:window.\*
- Snap again to the same side on ultrawide cycles half -> third -> two thirds. wm/keys.ts:84-97
- Keyboard Move/Size mode: arrow keys move/resize by 16 px, Shift+arrow by 1 px, Enter confirms, Escape cancels, a pointer press ends it. wm/commands.ts:134,153-158; desktop.json:window.announce.move
- New windows cascade by 32 px; first one is centered. wm/geometry.ts:6,57-69
- Window size is clamped to the workspace; title bar always stays reachable (48 px grab area). geometry.ts:8-17
- Last free size per app is remembered (only from windows on screens >= 1024 px) and shared across devices. wm/persistence.ts:179-191
- At most 50 windows; opening more does nothing (no message found). wm/state.ts:6,48
- Apps may veto a close (unsaved changes); "Close all" in the switcher asks every app. wm/commands.ts:33-77
- App not answering: dialog "{name} is not responding" - "The app did not answer within 5 seconds. If you close it anyway, changes it has not saved are lost." buttons "Wait" / "Close anyway". desktop.json:appHost.unresponsive.\*
- Right-click inside an app window never opens the browser menu except in text fields. shell/Window.tsx:129-136
- Medium: windows fill the screen; snapped windows become left/right half; Maximize button hidden. shell/Window.module.css:121-150
- Compact: one app at a time, full screen, only the close button in the title bar. Window.module.css:153-176

### The taskbar

- Default bottom; can be top/left/right: account menu > "Taskbar position" > Bottom/Top/Left/Right. desktop.json:menu.taskbar,taskbar.position.\*; desktop/src/desktop/Desktop.tsx:255-279
- Position saved in the layout (per device); phones always use bottom and hide the setting. Desktop.tsx:480-482,674
- Parts: Pithyx menu button (left), running apps, app switcher button with count, transfers button, running-tasks button, notifications bell with unread badge, clock (time + date, tooltip long date, not clickable). shell/Taskbar.tsx:149-310
- Click a running app: focus it; click the focused one: minimize. desktop/Desktop.tsx:625-627 (onApp)
- Right-click (or Menu key / Shift+F10) on a running app: "Minimize"/"Restore", "Add to desktop", "Close". Taskbar.tsx:181-249; desktop.json:taskbar.appMenu,addToDesktop
- Tap and release the Meta key (Windows/Command) alone opens/closes the Pithyx menu. shell/metaTap.ts:10-15; Desktop.tsx:529
- Start menu: search field "Search apps, settings and files", but only apps are matched today; settings and files results and "Recent files" are empty. Desktop.tsx:662-663; shell/StartMenu.tsx:117-119,206
- Start menu shows account entry ("Signed in as {name}", role Administrator/User) with: Two-factor authentication, Language, Appearance, Accent color, Wallpaper, "Get desktop from", Taskbar position, Sign out. Desktop.tsx:204-281; desktop.json:menu.\*
- Power options (Restart, Shut down) only for administrators or users with right "Restart and shut down". Desktop.tsx:673-ff `power=`; users.json:grants.right.power
- Restart dialog: "Every user is signed out, and file transfers over the network stop. The box is back in a few minutes." desktop.json:power.restart.intro; shutdown: "...nothing is reachable until someone switches the box on again."
- Escape closes Start menu, switcher, notification panel. shell/useDismiss.ts:4,25
- Notifications: panel "Notifications" with "Running tasks", "Recent", "Clear all", per-item Dismiss/Mute. desktop.json:notifications.\*
- Transfers button: "{n} transfers, {p} percent"; list with cancel/remove. desktop.json:transfers.\*

### Desktop items and notes

- Items: shortcuts (to apps) and sticky notes, per user, shared across devices. desktop/CLAUDE.md "Desktop layout"; daemon/.../Layouts/DesktopItemService.cs
- Max 200 items (`desktop.item.limit_reached` "The desktop is full. It holds at most {max} items."). DesktopItemService.cs:21,85
- Right-click empty desktop: "New note", "New shortcut" > app list. items/ItemMenus.tsx:98-130; desktop.json:items.\*
- Right-click icon(s): "Open" (one icon), "Rename" (F2), "Move", "Remove from desktop" (Del). ItemMenus.tsx:66-90
- Taskbar/Start menu "Add to desktop" puts a shortcut in the first free cell. items/ItemsLayer.tsx:94-100
- Open a shortcut: double-click (mouse), single tap (touch), Enter/Space. shell/DesktopIcon.tsx:56,66-75
- Select: click; Ctrl/Meta/Shift = additive; drag a selection frame on empty desktop (not on touch); Shift+arrow ranges. ItemsLayer.tsx:289-291,357-388
- Arrow keys jump to the nearest icon; Delete removes selection; F2 renames one shortcut; ContextMenu key or Shift+F10 opens menu. ItemsLayer.tsx:361-400
- Keyboard move: choose "Move" in the menu, arrows move by one cell, Enter keeps, Escape cancels. ItemsLayer.tsx:77,321-345; desktop.json:items.moveStart
- Rename dialog: "Rename shortcut", "Leave it empty to use the app's name, {app}." desktop.json:items.renameTitle,nameHint
- Icons are stored by grid cell (cell 96x112 px, 12 px padding, max cell 255) and shown in the next free cell on small screens without changing the stored place. items/grid.ts:1-12
- Compact: icons flow in rows like a phone home screen; no dragging/frame. ItemsLayer.tsx:96-100,128 (free = not compact)
- Touch: long press 500 ms opens the item/desktop menu. ItemsLayer.tsx:67,213-218
- Shortcut to an app that is gone is kept on the daemon but not shown. ItemsLayer.tsx:81-86
- Notes: 230x210 px = 3x2 cells; text max 4000 chars (`desktop.note.too_long`); autosave 800 ms after typing and on blur. items/grid.ts:8-10; items/Note.tsx:20-21,189-193; DesktopItemService.cs:22
- Note grip button "Move note": arrow keys move one cell; announces "Note moved to column {column}, row {row}". Note.tsx:137-160; desktop.json:items.moveNote\*
- Note close "Remove note": empty note deletes at once; with text asks "Delete this note?" / "The text of the note is deleted on all your devices." / "Delete note". Note.tsx:164-172; desktop.json:items.removeNote\*
- Conflict between devices: device in use wins, but note text from the other device is kept on a new note beside it plus a toast. items/sync.ts:190-206
- Errors: `desktop.item.conflict`, `desktop.item.not_found`, `desktop.item.invalid`. errors.json desktop.item.\*
- Wallpaper: account menu "Wallpaper": "Pithyx boxes" (default) or "Plain", per user, all devices. appearance.ts:4,15; desktop.json:menu.wallpapers

### Keyboard shortcuts

- Global window shortcuts (all Alt+Shift; not active while typing in text fields): ←/→ snap left/right (cycle thirds on ultrawide), ↑ maximize, ↓ restore (if snapped/maximized) or minimize (if free), PageDown / PageUp next / previous window, Space window menu. wm/keys.ts:25-57; wm/commands.ts:187-208
- Text fields skip these (macOS Option+Shift+arrow selects words). keys.ts:61-84
- Space is matched by code because Option+Space types a no-break space on macOS. keys.ts:33-36
- Only ←,→,↑,↓ are shown in the menu (i18n window.keys.\*); PageUp/PageDown/Space exist only in code, not in any UI text.
- Meta tap opens Pithyx menu. metaTap.ts
- Desktop items: Delete, F2, Shift+F10 / Menu, arrows, Enter/Escape in move mode (see above).
- Escape closes overlays. useDismiss.ts:25

### Layout per device

- Server remembers per user and device: open windows, position, size, placement, minimized, order, focus, taskbar position, small app data (e.g. current folder). wm/layout.ts:19-47
- Device = browser profile via cookie `pithyx_device` (random id, HttpOnly, SameSite=Strict, Secure on HTTPS, 400 days, renewed at session start; "not a credential"). daemon/.../Layouts/DeviceCookie.cs:11-41
- Layout expires 90 days after last save (hourly cleanup). Layouts/LayoutService.cs:16; LayoutCleanupService.cs:6
- App data in a layout capped at 16 KB (`desktop.layout.too_large`: max 50 windows, 16 KB). wm/layout.ts:15; Layouts/LayoutRules.cs:14-16,53
- Saves: debounced 1 s, only from the visible focused tab (`document.visibilityState` + `hasFocus`), flushed on page hide; version via If-Match, a save from another tab -> this tab wins. wm/persistence.ts:19,58,67-86,264-265
- Restore on opening desktop; windows of apps that are gone or not allowed are skipped with notification "Some windows were not restored" ("{count} windows belong to an app that is no longer available."). persistence.ts:34-37,239-243; notifications.json:desktop.restoreSkipped
- Failure notices: "Window arrangement not saved"; restore failure "The daemon did not answer. Changes to this desktop are not saved until you reload the page." notifications.json:desktop.saveFailed,restoreFailed
- Layout never holds permissions; rights checked live. desktop/CLAUDE.md; persistence.ts:34-37
- "Get desktop from": account menu submenu listing the user's other devices as "{browser} on {os}" with "{w} x {h}, {n} windows, {time ago}"; "No other devices" when empty; loading shows toast "Desktop loaded". Desktop.tsx:88-160,243-254; desktop.json:menu.\*, notifications.json:desktop.copied
- Error `desktop.device.not_found` "No saved desktop was found for that device. It may have expired."
- Appearance: accent + wallpaper per user (all devices); theme and language per device (localStorage `pithyx.theme`, `pithyx.language`). appearance.ts:6-9; theme/preference.ts:5; i18n.ts:106
- Taskbar position is part of the per-device layout. Layout contains `taskbar`. layout.ts:24-30
- No live sync between devices (only on "Get desktop from"). desktop/CLAUDE.md

### Phones and tablets

- No separate mobile app; same UI. desktop/CLAUDE.md "Responsive and accessible"
- Phone (< 600 px): apps full screen, one at a time, taskbar bottom; switcher button shows open apps as cards ("Open apps", tap card to switch, X per card, "Close all", "No apps are open."). shell/AppSwitcher.tsx; desktop.json:switcher.\*
- Swipe horizontally along the taskbar (>= 60 px, mostly horizontal) cycles apps (compact only). Taskbar.tsx:74,113-137; Desktop.tsx:609-614
- Back gesture / browser Back while an app is full screen minimizes all apps (go home) instead of leaving the desktop; one history entry per active app. wm/backGesture.ts:1-77
- Tablet (600-1023 px): windows maximized by default, side-by-side halves via snap (menu "Snap left/right" or Alt+Shift+arrows). Window.module.css:121-150
- Long-press 500 ms for menus; touch opens shortcuts with a single tap. ItemsLayer.tsx:67; DesktopIcon.tsx:66
- Touch targets >= 44 px on coarse pointers; safe-area insets (rule, desktop/CLAUDE.md). Not verified in code here.
- Notes cannot be moved with the grip on compact (grip only when `free`). Note.tsx:128
- Clipboard fallback dialog "Copy text" when the browser denies copying. desktop.json:appHost.copy.\*

### Questions users will ask (desktop)

- Why is my desktop empty on another device? Layout is per device; use account menu > "Get desktop from". (Desktop.tsx:243)
- Do my icons/notes/wallpaper follow me? Yes, items, accent, wallpaper per user; theme/language per device. (appearance.ts:6; Note sync)
- How do I move the taskbar? Account menu > Taskbar position (not on phones). (Desktop.tsx:674)
- How long is my window layout kept? 90 days after the last save. (LayoutService.cs:16)
- Why did a window not come back? App removed or no longer allowed; notification explains. (persistence.ts:239)
- Can I snap windows? Drag to edge or Alt+Shift+arrows. (geometry.ts, keys.ts)
- I forgot my password. Ask an administrator; administrators use the recovery key; no email reset. (auth.json:forgot)
- Why can't I open an app / see Settings? Admin apps need administrator; Files/Viewer need grants. (access.ts:18-34)
- What do the cookies hold? `pithyx_session` (login), `pithyx_device` (device id, no personal data). (SessionService.cs:13; DeviceCookie.cs:8)
- Does the box track me / phone home? unknown from this scope (CLAUDE.md principle only).
- How many windows can I open? 50. (state.ts:6)
- Is there a dark mode? Account menu > Appearance: "Like the system" / "Light" / "Dark". (desktop.json:menu.themeChoice)
- Which languages? English and German; "Automatic" follows the browser, else system language. (i18n.ts:42,125)
- Where is the "Recent files" list / search for files? Present in UI but empty today. (Desktop.tsx:662)

### Not in code (plan only), desktop

- "Open in tab" for apps - CLAUDE.md Apps; not seen in desktop code.
- Passkeys - TODO.md:781 out of scope.
- Trust-this-device TLS step / HTTPS 443 - TODO.md:613,1390 (M5).
- Windows spanning multiple screens: `screen` field exists but is always 1. wm/state.ts:~110 (`screen: 1`)

## PAGE files.md

### Browsing shared folders

- Files app: multi-instance, default 920x580, min 320x280; needs grant `app:org.pithyx.files` (new users get it by default). desktop/src/apps/registry.tsx:~48-60; daemon/.../Permissions/Grant.cs:53; Apps/CoreApps.cs:26
- Sidebar "Shares" (toggle "Shares and recycle bin") lists shares the user may use; unavailable ones show "Not available"; "You have no shares yet. An administrator can create them." files.json:shares.\*,noShares,sidebar
- Read-only shares show "Read only"; write actions disabled. files.json:readOnly; FilesApp.tsx:546-560
- Share access levels: none, Read, Read and write (per user/group; admins everything). users.json:grants.access.\*
- Toolbar: Back, Forward, Up one level, path field "Path", search "Search in {folder}", toggle "Show as tiles"/"Show as list", row density "Comfortable"/"Compact". files.json
- Columns: Name, Size, Modified; click header sorts (asc/desc toggle). files.json:columns; FilesApp.tsx:785-792
- Sort keys name (natural, ignoring case), size, modified. daemon Files/FileEndpoints.cs:59-63
- Listings load in pages of 200 (client) from API default 100/max 1000; list is virtualized. desktop/src/apps/files/useFolder.ts:14; daemon/.../Files/FileService.cs:14-15
- Live updates of the folder via topic subscription. FileEndpoints.cs:62
- Window title "{folder} - Files"; window remembers share/path/view/layout in the layout. files.json:windowTitle; FilesApp.tsx:56,196
- Status line: "{n} items, {size}" or "{n} selected". files.json:summary,selected
- Open (double-click, Enter, tap): folder/symlink -> enter; viewable file -> Viewer; other file -> download; "other" (socket etc.) -> nothing. files/defaultAction.ts:12-22
- Symlink to a file is downloaded after the daemon says it is not a folder (`fs.entry.not_folder`). FilesApp.tsx:488-505
- Context menu actions: Open, Download, Upload, New folder, Rename, Copy, Cut, Paste, "Move to recycle bin", "Delete permanently", "Select all", "Previous versions", "Open with...". files.json:actions.\*
- "Open with...": dialog "Open {name} with": Viewer, Download. files.json:openWith.\*; defaultAction.ts:25-33
- Tiles view: tile 136x156 px; list view. FolderView.tsx:69-70
- Hidden entries: upload temp files `.pithyx-upload-*` and `.pithyx-trash` are not listed. FileService.cs:329; FsPath.cs:17,20
- Name rules: no empty, no slash, max 255 bytes (`fs.name.invalid`); names starting with upload prefix or equal to trash folder refused. errors.json fs.name.invalid; FsPath.cs:59
- Path rules: relative to share, no "." / "..", max bytes (`fs.path.invalid`).
- Share unavailable (pool missing/stopped/not mounted): `fs.share.unavailable` text. errors.json
- Previous versions dialog: restore points, "Restore the version of {date}", options "Replace the current version" / next to current ("name (version yyyy-MM-dd HH.mm UTC)"). files.json:versions.\*; FileEndpoints.cs:99-106
- Versions come from daily restore points kept 7 days, only if an administrator switched them on per shared folder in Settings > Shared folders; list empty otherwise. files.json:versions.empty,note; Shares/SharePlanner.cs:26
- Versions need read-write to restore ("You can only read this share, so versions cannot be restored here."). files.json:versions.readOnly

### Uploading

- Ways: toolbar "Upload" (file picker), drag files from the OS into the window or onto a folder. files.json:actions.upload; FilesApp.tsx:813,846-853; emptyFolder text "Drop files here to upload them."
- Upload = one streamed HTTP PUT per file (`PUT /api/files/{share}/content?path=`); NO chunking, NO resumable upload in code. daemon FileEndpoints.cs:72-77; FileService.cs:108-160
- Files upload one after another (sequential loop). FilesApp.tsx:376
- No size limit of its own; request body limit disabled; only free space limits (`fs.volume.full`). FileEndpoints.cs:249-253; errors.json fs.volume.full
- Server writes to hidden temp `.pithyx-upload-<guid>` with a 256 KiB buffer, fsyncs, then renames: the file appears only when complete. FileService.cs:126-147
- Length mismatch or client drop: `fs.upload.incomplete` "The upload was interrupted. Nothing was saved."; temp removed. FileService.cs:141-157
- Existing name: `fs.entry.exists` unless overwrite=true; desktop asks (see Conflicts). FileEndpoints.cs:75; FilesApp.tsx:393-414
- New files get the signed-in user's owner (fchown). FileService.cs:121,130
- Progress: transfers button in taskbar and status bar with per-file name "Uploading {name}", speed, time left, Cancel. files.json:job._; desktop.json:transfers._
- Cancel in taskbar = no error message (`sdk.transfer.cancelled`). FilesApp.tsx:406
- Failed upload text: "Failed. The connection was lost or the daemon refused the file." desktop.json:transfers.failed
- Folder upload / drag of folders: unknown (code uses `File[]` only). FilesApp.tsx:371
- Needs read-write on the share (`fs.share.read_only`).

### Copy, move and conflicts

- Copy/Cut/Paste: Ctrl+C / Ctrl+X / Ctrl+V (Meta works too); toast text "{n} items copied to the clipboard"/"cut". files.json:keys._,announce._; FolderView.tsx:176,255-270
- Cut to same folder does nothing. FilesApp.tsx:344-346
- Drag between folders/sidebar: moves by default; Ctrl or Alt while dropping copies. FolderView.tsx:304-305,323
- Copy/move/delete run as jobs with progress, cancel. files.json:job.kinds.\*; FileEndpoints.cs:52-58
- Max 1000 items per request; "select all" sends the folder with exceptions (not names). FileOperations.cs:66,36-39
- Conflict policies: fail (default), skip, overwrite (folders merge, files replaced), rename ("Report (2).pdf"). FileOperations.cs:17-28,165
- Dialog "Names already exist" / "The target folder already has entries with the same names. What should happen with them?" buttons "Skip them", "Replace them", "Keep both" (primary), "Cancel". files.json:dialog.conflict\*; FilesApp.tsx:975-990
- Rename policy picks "name (2).ext", "(3)" ... up to 9999 server side; client tries up to 50 for uploads. FileOperations.cs:707-715; FilesApp.tsx:396
- Move within same dataset is a rename; across datasets copy+delete. FileOperations.cs:451,494-497
- Overwrite onto a folder/file mismatch falls back to copy path (folders merged). FileOperations.cs:486
- Symlinks are never copied; they are reported as skipped. FileOperations.cs:55
- A folder cannot go into itself (`fs.operation.into_itself`). errors.json
- Rename in place: F2 or "Rename"; empty/unchanged name ignored; existing name -> `fs.entry.exists`. FilesApp.tsx:459-484
- New folder: "New folder" then immediately rename; "New folder (2)" when taken. FilesApp.tsx:418-445
- Download: "Download" or double-click non-viewable; goes as attachment, always with `Content-Security-Policy: sandbox`; range requests supported. FileEndpoints.cs:67-71
- Previous versions restore: job, next to current or overwrite. FileEndpoints.cs:99-106

### The recycle bin

- Delete (Del or "Move to recycle bin") moves to the share's hidden `.pithyx-trash`; deleting for good only explicit: Shift+Del or "Delete permanently" with confirm "Delete permanently?" "{n} items are deleted for good. This cannot be undone." files.json:dialog.permanent\*; FolderView.tsx:229-231; FsPath.cs:20
- Bin is per share: sidebar "Recycle bin" > share; title "Recycle bin of {share}"; empty text "Empty. Only hope remains." files.json:bin.\*
- Per item: name, "From {path}", "Deleted {date}", "Deleted for good: {date}" or "Kept until the bin is emptied". files.json:bin.\*; TrashView.tsx:125-133
- Buttons: "Restore", "Delete for good" (confirm "Delete for good?"), "Empty recycle bin" (confirm "Empty the recycle bin?" "Everything in the recycle bin of {share} is deleted for good."); only with write access. TrashView.tsx:67-100; files.json:dialog.\*
- Restore recreates missing parent folders; default conflict policy rename. FileOperations.cs:171-176; FileEndpoints.cs:81-83
- Retention default 30 days (migration), allowed 0-3650, 0 = until emptied; hourly cleanup deletes expired. Database/Migrations/0007_trash.sql:4; TrashService.cs:45,255
- Note shown: "Deleted items are kept for {days} days." or "...until the recycle bin is emptied." files.json:bin.retention\*
- Retention can be changed only by API `PUT /api/shares/{id}/trash` (admins); NO UI found in Settings. FileEndpoints.cs:84-88; grep desktop/src/apps/settings: none
- Deletes over SMB and WebDAV also land in this bin (SFTP and FTP: unknown/bypass, see open Q). settings.json:fileServices.smb.trash,webdav.trash; SmbConfig.cs:128
- Bin list pages of 100 ("Show more"). TrashService.cs:46; files.json:bin.more
- Items in bin keep original path. TrashEntry.originalPath (TrashView.tsx:126)

### Thumbnails and the viewer

- Thumbnail formats: JPEG, PNG, WebP, GIF only (checked by content, extension .jpg .jpeg .jpe .png .webp .gif). Files/Thumbnails/ThumbnailRenderer.cs:43; ThumbnailService.cs:28
- Sizes: small 64 px (list rows), large 256 px (tiles); encoded WebP quality 80. ThumbnailCache.cs:42; ThumbnailContracts.cs:6; ThumbnailRenderer.cs:101
- Limits: source file <= 256 MiB, <= 50 million pixels, edge <= 30000 px (else none; decompression bombs). ThumbnailRenderer.cs:35-41
- Only entries on screen are requested, max 200 names per request (`fs.thumbnail.too_many`), render queue 1000. ThumbnailService.cs:23,26; FileEndpoints.cs:107-111
- Thumbnails render asynchronously and appear when ready (topic event). ThumbnailService.cs:178
- Cache: one WebP per key in the pool's hidden dataset `<pool>/.pithyx/cache/thumbnails`, max 2 GB, least recently used first; leaves with the pool; folders outside pools (dev only) none. ThumbnailCache.cs:14-26; ThumbnailCaches.cs:9-16
- Changed file = new key; old thumbnail never served. ThumbnailCache.cs:45-55
- Video thumbnails and HEIC: not supported (icons). TODO.md:1372; grep: only 6 extensions
- Viewer: "Open" on image/pdf/text/audio/video; also from "Open with". viewer/kinds.ts:11-21
- Image: jpg jpeg jpe png webp gif avif bmp ico svg (SVG only as image; scripts never run). kinds.ts:10-11
- PDF: pdf.js, pages drawn to canvases. viewer/PdfView.tsx:1-10
- Audio: mp3 m4a aac oga ogg opus wav flac. Video: mp4 m4v webm ogv mov (browser support decides). kinds.ts:13-14
- Text (shown as plain text; HTML/SVG sources never rendered): txt md log csv json xml yaml toml ini conf html css js ts py ... (full list kinds.ts:16-21). Only first 1 MiB; note "Only the first megabyte is shown. Download the file to see all of it." sdk/viewerInternal.ts:5,39; viewer.json:truncated
- Image zoom steps 25,50,75,100,150,200,300,400 % and "Fit"; toolbar Zoom in/out, "Fit to window". ViewerApp.tsx:23; viewer.json
- Viewer keys: ←/→ previous/next file (siblings in the same folder, up to 5000), + or = zoom in, - zoom out, 0 fit (images). ViewerApp.tsx:25,109-139
- "{index} of {count}" position. viewer.json:position
- Unknown type: "The viewer cannot show this kind of file. Download it to open it with another program." viewer.json:unsupported; failure: "This file cannot be shown..." viewer.json:failed
- Files are served with CSP sandbox, so HTML/SVG can't run in the desktop. FileEndpoints.cs:67-71

### Keyboard shortcuts (Files)

- Arrows move active item (Left/Right only in tiles), Shift+arrow range, Ctrl+arrow moves focus without selecting. FolderView.tsx:156-209
- PageUp/PageDown, Home, End. FolderView.tsx:206-213
- Enter open; Space select (Ctrl+Space toggle). :218-227
- Backspace or Alt+ArrowUp: up one level. :190-196,216
- Delete: move to recycle bin; Shift+Delete: delete permanently (confirm). :229-231
- F2 rename; Escape clears selection (and cancels rename). :232-237; FolderView.tsx:610-612
- Ctrl+A select all; Ctrl+C/X/V (Meta works). :254-270; files.json:keys.\*
- ContextMenu key / Shift+F10: context menu. :238-252
- Esc/Enter in rename field. FolderView.tsx:610-612

### Questions users will ask (files)

- How big a file can I upload? No limit besides free space; one request per file, no resume. (FileEndpoints.cs:249)
- What if my upload is interrupted? Nothing is saved, retry the file. (FileService.cs:141-157)
- Do I lose a file when names clash? No, you are asked; "Keep both" renames "name (2)". (FilesApp.tsx:975)
- Where did my deleted file go? Share's recycle bin, 30 days default. (0007_trash.sql:4)
- How do I delete for good? Shift+Del or "Delete permanently", or from the bin. (FolderView.tsx:229)
- Can I get an older version? "Previous versions" if the admin enabled restore points (daily, 7 days). (SharePlanner.cs:26)
- Why no thumbnail for my video/HEIC/RAW? Only JPEG/PNG/WebP/GIF. (ThumbnailRenderer.cs:43)
- Why does my text file stop at 1 MB in the viewer? Limit; download for all. (viewerInternal.ts:5)
- Can I drag between two Files windows? Yes, entries drag type `application/x-pithyx-files` (same box). (FolderView.tsx:28) - cross-window behaviour not tested here.
- Why can't I write? Share is "Read only" for you. (files.json:readOnly)
- Symlinks? Shown, opened like folders; not copied. (FileOperations.cs:55)
- Are hidden files shown? unknown (no hidden-file toggle found).
- Who owns uploaded files on disk? The uploading user's Linux account. (FileService.cs:121)
- Is there a size limit for copy? Max 1000 items per request, no byte limit. (FileOperations.cs:66)

### Not in code (plan only), files

- TODO.md:1367 "double click downloads files for now": outdated, viewer exists (defaultAction.ts).
- Video thumbnails (ffmpeg), HEIC, syntax highlighting, pdf.js standard fonts: TODO.md:1372.
- Special folder "Apps" in Files (D314, A15b): not in code.
- i18n keys `places.home/desktop/documents` exist but are not used by the Files app (grep desktop/src/apps/files: none).
- NFS recycle bin: TODO.md:618.

## PAGE apps.md

### Opening an app

- Apps are in the Pithyx menu (Start menu) "Apps", by search (name match only), from desktop shortcuts, or "Add to desktop". Desktop.tsx:573-590,662; desktop.json:start.\*
- Core apps (names): Files, Viewer, Storage, Settings, Resource Monitor, Log Center, About Pithyx, Store. desktop.json:apps.org.pithyx.\*
- Who sees what: Storage, Settings, Resource Monitor, Log Center, Store = administrators only; Files and Viewer need a grant (given to new users by default); About for everybody. daemon/.../Apps/CoreApps.cs:23-33,58; desktop/src/apps/access.ts:18-34; registry.tsx (adminOnly/granted)
- Installed (third-party) apps appear for users the daemon says may use them (topic `apps`); app windows run in a sandboxed iframe from the same host. desktop/src/apps/access.ts:26; installed.ts:7; sandbox/SandboxApp.tsx:117-126
- Installing/removing apps is an administrator job in Store: "Install from file" (.pithyxapp), catalogs Official / Community / Own, Updates tab. store.json:tabs._,installed._
- Install dialog: "Install an app", file picker, shows version, author, size, "Permissions it asks for", "Folders it asks for", backend yes/no, root warning, special rights, "Who may use it", "Pool for app data". store.json:install.\*
- Packages from a file are "not verified": "Install only apps from developers you trust." store.json:install.unverified
- Catalog apps: badges "Pithyx", "Community", "Unverified"; "Installed from a catalog". store.json:catalog.badge._,detail.source._
- Window title and icon come from the app's manifest; names in the user's language, fallback exact lang -> base lang -> English. desktop/src/apps/installed.ts:23-25
- Admin "Open" button in the app detail. store.json:detail.open
- Disabled app: "Users can open the app" switch; disabled keeps data and access but nobody can open it. store.json:detail.enabled\*
- Opening while the app's pool is gone: badge "Pool not available"; text "The pool with the app's data is not available. Nobody can open the app until it is back." store.json:installed.unavailable,detail.unavailable

### Single and multiple windows

- Manifest `backend/window.instances`: `single` = opening again focuses the user's existing window on this device and passes launch args; `multi` = new window each time. sdk/schema/manifest.v1.schema.json:237-242; wm/state.ts:31-49
- Core apps: Files and Viewer `multi`; Storage, Settings, Monitor, Log Center, About, Store `single`. desktop/src/apps/registry.tsx (instances)
- "Single" is per device, per user; a second tab/device has its own window. wm/state.ts:31-37
- At most 50 windows total. state.ts:6
- Window sizes: default and minimum size come from the app (manifest; error `app.manifest.window_size_invalid` if min > default). errors.json
- Windows persist per device across reloads (restored from layout, app can save small session state). wm/persistence.ts; apps/host/rules.ts:24-40

### What an app may access

- Third-party app runs in `<iframe sandbox="allow-scripts allow-forms" referrerpolicy="no-referrer">` without allow-same-origin: no cookies, no storage, no access to the desktop page; talks only via a MessagePort. sandbox/SandboxApp.tsx:22-32,117-126
- Permissions shown (Store detail "Permissions"; install dialog): "Read files" (shared folders of the user using it), "Change files", "See shared folders", "Notifications", "See all users and groups", "Internet" ("Without it, the app stays on this box."). store.json:permission.\*
- Admin can turn a permission off per app; stays off after updates; open windows get it within a few minutes. store.json:detail.permissions.intro
- New permission in an update is flagged "New"; update asking for new permissions or special rights never installs automatically. store.json:install.newPermission,detail.policy.hint
- Folders: apps request `storage`; admin maps each to a folder in a shared folder (read/read-write), stays when the app is removed. store.json:detail.storage._; errors `app.storage._`, `app.mapping.invalid`
- Who may use an app: Everybody / Administrators only / Selected groups and users; install dialog suggests admins only when the app sees every user, runs as root or asks special rights. store.json:detail.access.\*,install.accessSuggested
- Files an app touches are those the signed-in user may access (permissions limited by user rights). store.json:permission.files.\*
- App backend (containers): runs as root only if declared, warning "A container of this app runs as root. Pithyx keeps it without rights on the box itself..." store.json:install.root
- Elevated rights (`docker.socket`, `host.full`) refused in v0.1; error `app.elevated.unsupported`. errors.json; CLAUDE.md Apps
- Logging: admin switch "Send debug messages to the Log Center"; info/warn/error always logged. store.json:detail.logs.\*
- Revoked permissions apply on next token refresh/within minutes; windows of an app the user lost are closed. wm/persistence.ts:34-37; sandbox/ThirdPartyWindow.tsx:10; SandboxApp.tsx:28-31
- App token expired: `app.token.invalid` "The app's access expired. Close the app and open it again."
- Unsaved work: apps get a last chance to save (beforeUnload) on sign-out, shutdown, update. apps/lifecycle.ts:3-8
- Uninstall: choose "Keep the data" or "Delete the data" (dataset with snapshots deleted); mapped folders stay either way. store.json:uninstall.\*

### When an app does not start

- Frontend loads in the iframe; if the window just closes, app was removed/disabled/no longer granted. sandbox/ThirdPartyWindow.tsx:10
- Backend apps: backend starts on demand; `onDemand` runs while any window is open plus idle timeout (default 10 min, manifest `idleTimeout`); window closes -> 2 min grace; `always` runs always. daemon/.../Apps/Docker/BackendLifecycle.cs:49; AppContexts.cs:139; AppBackendSupervisor.cs:568-571
- A request to a stopped backend starts it and waits up to 30 s. AppProxy.cs:103; AppBackendSupervisor.cs:36
- Start timeout 10 min for compose up; health wait 60 s; after a failed start retry no sooner than 1 min. AppBackendRuntime.cs:57,60; BackendLifecycle.cs:46
- Errors: `app.backend.unavailable` "The app's backend is not running or does not answer. Try again in a moment."; `app.backend.start_failed` "...The app's log shows what its containers wrote."; `app.docker.unavailable` "The container service does not answer, so app backends cannot run right now."; `app.backend.storage_unavailable` (folder unmapped or pool missing/locked). errors.json; AppProxy.cs:110,140; AppBackendRuntime.cs:141,239,441
- Required folder unmapped: "Not mapped yet. The app needs this folder to start its background service." store.json:detail.storage.requiredMissing
- Pool locked/missing: apps with data on it do not start; `app.pool.unavailable` reasons (not imported, not online, read-only, locked, not set up by Pithyx). errors.json
- Frozen/hung app: "{name} is not responding" dialog after 5 s without answer. desktop.json:appHost.unresponsive.\*
- Connection broken: `sdk.transport.closed` "The connection between the app and the desktop is closed. Open the app again."; `sdk.request.timeout`; `sdk.backend.unreachable`. errors.json
- App needs a newer Pithyx: `app.package.box_too_old`, `app.manifest.version_unsupported`; `sdk.protocol.unsupported`. errors.json
- Update can be rolled back by admin: "Back to {version}" (data as before the update). store.json:detail.rollback.\*
- Admin logs: Log Center source `app/<id>` (backend output + app.log). CLAUDE.md Apps D305; store.json:detail.logs
- `app.backend.unsupported` text says this version installs apps WITHOUT backend only; constant is defined but unused in code (ErrorCodes.cs:62; grep none) - stale text.

### Questions users will ask (apps)

- Where do I find apps? Pithyx menu; ask admin to install/enable. (Desktop.tsx:573)
- Why can't I see the Store/Settings? Admin only. (CoreApps.cs:30)
- Is an app safe? Sandboxed; sideloaded apps unverified; catalog apps signed only for project catalogs. (store.json:install.unverified,fromCatalog)
- Can an app see all my files? Only shares you can access, and only if it asked for the file permission and admin allowed. (store.json:permission.files)
- Where is the app's data? On the app pool the admin picked at first install. (store.json:installed.pool)
- Why does an app start slowly? On-demand backend start (up to ~30 s per request, 10 min max). (AppProxy.cs:103)
- Can I run two windows of one app? Depends on manifest single/multi. (manifest.v1.schema.json:240)
- Can I use apps on my phone? Yes, full screen in the same UI. (desktop/CLAUDE.md)
- Do apps work offline/with internet? Only with permission "Internet". (store.json:permission.network.internet)
- Are installed apps updated automatically? Policy in Store settings: Install it / Tell administrators / Do nothing; default notify at 03:00 (per CLAUDE.md state, not re-verified in code). store.json:settings.updates.\*

### Not in code (plan only), apps

- Special folder "Apps" (D314, A15b); elevated rights UI (D311/A16); `kind: "web"` external apps; "Open in tab". TODO.md:437,420; CLAUDE.md Apps.

## PAGE account.md

### Changing the password

- FINDING: no self-service password change in code. Only `PUT /api/users/{userId}/password`, "Administrators only. Sets a new password, for SMB and SFTP as well, and ends every session of the user." daemon/.../Users/UserEndpoints.cs:51-59
- Users page (admin): button "New password", dialog "New password for {name}", "The user is signed out everywhere and signs in with the new password.", button "Set password", "SMB and SFTP use the new password as well." users.json:password.\*
- Forgot password: "Pithyx has no reset by email. Ask an administrator..." auth.json:forgot.intro; admins use recovery key. auth.json:forgot.\*
- Rules (hard, every place): min 8 chars, max 1024, not in the embedded list of common passwords (checked lower-case). CredentialRules.cs:15-16,81-96; messages `auth.password.too_short` / `too_long` / `too_common`
- No character class rules; length plus blocklist only. CredentialRules.cs:99
- Desktop live feedback: hint "At least {minLength} characters." and strength meter "Strength: Weak/Medium/Strong"; reasons: too short, "This password is too well known.", "Only well-known words, as found in leaked passwords.", "Repeated characters or blocks are easy to guess.", "Keyboard or alphabet sequences are easy to guess.", "A few more characters or words make it stronger.", "A good password."; tip about passphrase. auth.json:password.\*
- Meter thresholds: medium >= 40 bits, strong >= 64 bits (estimate). desktop/src/auth/password/strength.ts:30-31
- Weak but long enough passwords are allowed (warning only). CredentialRules.cs:13-14
- Hashing: Argon2 (64 MiB, 3 passes, 1 lane). Auth/PasswordHasher.cs:20-22
- New-password fields use autocomplete `new-password`. auth/password/NewPasswordField.tsx:14; SetupScreen.tsx:131

### Two-factor sign-in

- Turn on: account menu > "Two-factor authentication" > "Set up"; scan QR code or enter key manually; type the 6-digit code; "Turn on". auth.json:totpSetup.\*
- Then shows "Your recovery codes": 10 single-use codes, each "works once instead of a code from the app", shown only once; "Copy codes". auth.json:totpSetup.recoveryCodes\*; AuthService.cs:17-18,224
- Recovery code format: 10 chars in groups of 5 (e.g. `XXXXX-XXXXX`), Crockford alphabet (O->0, I/L->1 accepted). AuthService.cs:18; Auth/Secrets.cs:17-30
- TOTP: SHA-1, 6 digits, 30 s period, otpauth issuer "Pithyx", label `Pithyx:<user>@<host>`. AuthService.cs:203-204
- Accepts previous/current/next step (+-30 s); a code works once (time step must be newer than last used). AuthService.cs:306,313
- Turn off: needs your password ("Your password" + "Turn off"). auth.json:totpSetup.disablePassword,disable; AuthService.cs:240-250
- Setup again: new secret replaces; confirming issues new recovery codes and deletes old ones. AuthService.cs:236
- Sign-in: after password, "Two-factor authentication" step, "Code from your authenticator app" or "Use a recovery code instead". auth.json:totp.\*
- Errors: `auth.totp.invalid` "The code is incorrect or has already been used."; `auth.totp.already_enabled`; `auth.totp.not_enrolled`; `auth.challenge.expired` after 5 min. errors.json; LoginChallenges.cs:11
- 2FA is not used for SMB/SFTP/WebDAV/FTP: those use the password only. FileServices/ProtocolSignIn.cs:9-12
- Admin recovery key reset turns 2FA off and ends all sessions. AuthService.cs:287-288
- A database/dev reset of an owner account turns 2FA off - dev only (CLAUDE.md), not user-facing.
- Admin cannot disable another user's 2FA via UI: unknown/not found in code (users.json has no 2FA entry).

### Sessions and devices

- FINDING: no UI or API to list or end sessions/devices (grep: only login, login/totp, recover, logout, session, totp/\*). daemon/.../Auth/AuthEndpoints.cs:27-37
- Session cookie `pithyx_session`: HttpOnly, SameSite=Strict, Secure on HTTPS, token random, only its SHA-256 stored. SessionService.cs:13,87-95
- Lifetime: idle 7 days (extended on use, refreshed at most once a minute), absolute 30 days from login. SessionService.cs:14-16,73
- An open desktop (WebSocket) keeps the session alive; it ends at the absolute limit. SessionService.cs:45-52
- Sign out ends that session only. AuthEndpoints.cs:121-125
- Password reset by admin, deactivating a user, recovery key reset end all of that user's sessions. UserEndpoints.cs:59; AuthService.cs:288; "Deactivating ends the user's sessions" UserEndpoints.cs:48
- Expired session: "The session has expired. Please sign in again." (`auth.session.expired`) errors.json
- "Devices" in the UI = desktop layout devices only: "Get desktop from" (browser, OS, screen size, windows, last use). Desktop.tsx:88-160
- Device cookie `pithyx_device` is separate from login. DeviceCookie.cs
- Support for multiple concurrent sessions: yes (each login creates a row). SessionService.cs:21-42

### Language, theme and accent

- Account menu items: "Language", "Appearance" (theme), "Accent color", "Wallpaper". desktop.json:menu.\*
- Language: "Automatic" or each language by its own name (English, Deutsch); stored per device in localStorage `pithyx.language`; Automatic uses browser language (en/de), else the system language. desktop/src/desktop/LanguageOptions.tsx; i18n.ts:42,125-136,189-206
- Sign-in page has its own language selector. LoginScreen.tsx:91
- Dates/numbers format with system region chosen by admin in Settings > Regional ("Formats", "System language", time zone). i18n.ts:151-160; settings.json:regional.\*
- Theme: "Like the system" / "Light" / "Dark" per device (`pithyx.theme`); applied before first paint. theme/preference.ts:1-43
- Accent: Petrol (default), Ocean, Indigo, Plum, Moss, Graphite - per user, saved in the daemon, same on every device. theme/appearance.ts:3,15; desktop.json:menu.accents; Layouts/LayoutEndpoints.cs:42
- Wallpaper: "Pithyx boxes" (default) / "Plain", per user. appearance.ts:4
- Appearance is cached in localStorage `pithyx.appearance` for first paint. appearance.ts:18
- Full list of languages: en, de (nothing else). i18n.ts:42
- Display name: set by administrator; max 64 chars; users cannot edit own profile (no UI found).

### Questions users will ask (account)

- How do I change my password? Ask an administrator (no self-service in code). (UserEndpoints.cs:51)
- I lost my 2FA device. Use a recovery code at sign-in; if none, admin can't reset it via UI (unknown); administrators can use the recovery key (turns 2FA off). (AuthService.cs:259-292)
- How long do I stay signed in? 7 days idle, 30 days max. (SessionService.cs:14-15)
- Can I see where I'm signed in / sign out other devices? No. (AuthEndpoints.cs:27-37)
- How many failed attempts before lock? 5 per user+address in 5 min. (LoginRateLimiter.cs:13-15)
- What is a good password? Length matters; no composition rules; a long passphrase. (auth.json:password.tip)
- Is 2FA required? No, optional per user. (auth.json:totpSetup.off)
- Does 2FA protect SMB/SFTP too? No, those use the password. (ProtocolSignIn.cs)
- Which authenticator apps? Any TOTP app (SHA-1/6/30). (AuthService.cs:204)
- Why is my theme different on my phone? Theme and language are per device. (preference.ts:5)
- Can I use a passkey? No (not implemented). TODO.md:781
- What does the recovery key do? Resets the admin password only. (AuthService.cs:259)

### Not in code (plan only), account

- Self-service password change; session list; passkeys (TODO.md:781); TODO.md:1166 mentions "password change" for Samba accounts but only admin reset exists in endpoints.
- Trusted-device/TLS step (M5).

## PAGE file-access.md

### Which protocol to use

- Four protocols, all off by default except SMB listed as default for new users: SMB (network drives), SFTP, WebDAV, FTP. Settings > File services (admins). settings.json:fileServices.\*; Permissions/Grant.cs:38-41
- Per-user/group right per protocol: "SMB (network drives)", "SFTP", "WebDAV", "FTP" under "File services". New users get desktop login, SMB and Files/Viewer. Grant.cs:53; users.json:grants.protocol.\*
- Per-share access: No access / Read / Read and write. users.json:grants.access.\*
- Administrators have every right and reach every share. users.json:grants.adminHasAll
- SMB: "Network drives for Windows, macOS and Linux. Only encrypted, signed connections of current systems (SMB 3) are accepted." settings.json:fileServices.smb.explain; SmbConfig.cs:74-76
- SFTP: "Encrypted file transfer over SSH, for example with FileZilla, WinSCP or the sftp command. Users see only their shared folders and get no shell." settings.json:fileServices.sftp.explain
- WebDAV: "Shared folders as a network drive over the web address of the box... Off by default." settings.json:fileServices.webdav.explain
- FTP: "For devices and programs that know neither SFTP nor WebDAV, such as scanners. Encrypted connections (FTP with TLS, also called FTPS), plain FTP only if you allow it below, and only passive mode. Off by default." settings.json:fileServices.ftp.explain
- Status values: Off / Running / "Not running" / "Not installed". settings.json:fileServices.state.\*; FileServiceContracts.cs (FileServiceState)
- Recommendation by code: SMB for everyday desktops (recycle bin, previous versions), SFTP for secure transfer, WebDAV for sync programs/file managers, FTP only for scanners/old devices. (derived from explain texts)
- All protocols see only shares the user's grant allows; owners/rights/recycle bin are those of Files. WebDav.cs:123-126; D291 (TODO.md:1424)
- SSH logins with a shell are a separate switch ("Let users with the SSH login right sign in with a shell"); off by default. settings.json:fileServices.ssh.\*; FileServiceContracts.cs (SshEnabled)

### Windows

- SMB: File Explorer address `\\<box address>\` (UI shows `\\<host>\`), then share name; ports: SMB over TCP 445 only (NetBIOS off). FileServicesPage.tsx:57; SmbConfig.cs:83-84
- Share name = shared folder name (SmbShare.Name). SmbConfig.cs:12
- Sign in with Pithyx user name + password (2FA not asked). settings.json:fileServices.intro; ProtocolSignIn.cs:9-12
- Requires SMB 3 with mandatory signing; no guests (`map to guest never`); old clients without SMB 3 cannot connect. SmbConfig.cs:74-76; settings.json:fileServices.smb.explain
- Per shared folder option "Require encryption over SMB" ("Older clients without SMB encryption cannot connect then."). settings.json:shares.smbEncrypt\*; SmbConfig.cs:119
- "Previous versions" tab in Explorer shows daily restore points. settings.json:fileServices.smb.versions; SmbConfig.cs:127-137
- Deleting in Explorer sends files to the share's recycle bin (restorable in Files); folder `.pithyx-trash/<user>` hidden via `veto files`. settings.json:fileServices.smb.trash; SmbConfig.cs:17,128
- Rights cannot be changed over SMB (ACL editing off). SmbConfig.cs:16
- Workgroup default WORKGROUP; admin can set 1-15 letters/digits/dots/dashes/underscores (`fileservices.workgroup.invalid`). settings.json:fileServices.smb.workgroup\*; SmbConfig.cs:41-46
- Box is NOT announced on the network (no mDNS/WS-Discovery): it will not appear in Explorer's Network view; type the address. grep daemon: no avahi/wsdd code; TODO.md:1379
- WebDAV in Windows: "Map network drive" with `http(s)://<host>[:7484]/webdav/`: possible, details unverified (Basic auth, HTTP today). WebDav.cs:130-161
- SFTP/FTP in Windows: use a client (FileZilla, WinSCP named in UI). settings.json:fileServices.sftp.explain

### macOS

- Finder > Go > Connect to Server: `smb://<host>/` (UI shows `smb://<host>/`). FileServicesPage.tsx:58; settings.json:fileServices.smb.mac ("macOS and Linux: connect to the server")
- vfs_fruit enabled (macOS metadata, Time Machine not mentioned). SmbConfig.cs:99,127
- SFTP: `sftp://<host>` in an SFTP client (Finder does not browse sftp). FileServicesPage.tsx:59
- WebDAV: Connect to Server with the WebDAV URL. unverified
- FTP: use explicit FTPS client. settings.json:fileServices.ftp.tls

### Linux

- `smb://<host>/` in a file manager, or mount cifs (vers=3). settings.json:fileServices.smb.mac; SmbConfig.cs:74
- SFTP: `sftp://<host>` or `sftp user@host`; SFTP users get no shell, password auth only (public keys off for SFTP users), no TTY, no forwarding. FileServicesPage.tsx:59; SshdConfig.cs:33-39,67
- SSH shell users (right "Sign in over SSH with a shell"): password and public keys allowed, no forwarding. SshdConfig.cs:42-52
- SFTP chroot per user `/run/pithyx/sftp/<user>/<share>` (bind mounts; rebuilt at start and on changes; on tmpfs). SftpViews.cs:17-21
- SFTP shows only the user's shared folders; the recycle-bin folder `.pithyx-trash` is visible but cannot be listed (follow-up). TODO.md:1379 (not re-verified)
- SFTP deletes bypass the recycle bin (only SMB uses vfs_recycle). TODO.md:1379 (consistent: only SmbConfig has recycle).
- Switching SFTP off only stops Pithyx users; SSH for other accounts keeps running. settings.json:fileServices.sftp.view
- SSH/SFTP port: default sshd 22 (no port set by Pithyx; UI shows `sftp://host` without port). FileServicesPage.tsx:59; SshdConfig.cs (no Port)
- WebDAV: `http(s)://<host>:7484/webdav/` today; `davfs2`/file manager `dav://`/`davs://`. WebDav.cs:130; Program.cs:33
- FTP: `lftp`/FTPS clients, explicit TLS. see Phones/FTP

### Phones

- No dedicated mobile section in code. Practical options from the facts: any app speaking SMB 3, SFTP, WebDAV (Basic auth) or explicit FTPS. unknown which phone apps work.
- WebDAV clients: methods supported OPTIONS PROPFIND PROPPATCH GET HEAD PUT DELETE MKCOL COPY MOVE LOCK UNLOCK; locks granted but not enforced. WebDav.cs:132,126
- PROPFIND Depth 0/1 (not "infinity" - unverified). WebDav.cs:207
- Browser Files app works on phones (full-screen). see desktop.md
- FTP (explicit FTPS, passive only): control port 21 (override `PITHYX_FTP_PORT`), passive data ports 50000-50099, IPv6 any listener; commands AUTH TLS, PBSZ, PROT P, USER, PASS, PASV, EPSV, LIST/NLST/MLSD/MLST, RETR, STOR, APPE, DELE, MKD, RMD, RNFR/RNTO, SIZE, MDTM, REST; PORT/EPRT not supported. Program.cs:95; Ftp/FtpServer.cs:27,121; Ftp/FtpSession.cs:102-355,125,249
- FTPS certificate is the box's own: client asks to trust once; compare "Certificate fingerprint (SHA-256)" shown in Settings. settings.json:fileServices.ftp.tls,fingerprint; FileServiceContracts.cs:51
- Choose "explicit FTP over TLS" in the program (not implicit FTPS on 990). settings.json:fileServices.ftp.tls; FtpSession.cs:102-113
- Plain FTP only if the admin allows "Also allow plain FTP without encryption" (warning text); turning it off ends unencrypted sessions. settings.json:fileServices.ftp.plain\*
- FTP data connection: only from the control connection's address, one at a time, TLS. FtpSession.cs:16-19
- Plain-HTTP warning: with plain HTTP today (no TLS) WebDAV Basic credentials travel unencrypted. Program.cs:33,57; WebDav.cs:161

### Signing in

- User name and password = Pithyx account (lower-case, trimmed). ProtocolSignIn.cs:35-36; settings.json:fileServices.intro
- Right needed per protocol: `protocol:smb|sftp|webdav|ftp` (own or via group); user must be active. ProtocolSignIn.cs:70-74; Grant.cs:38-41
- SMB and SFTP check Linux/Samba copies of the password: a user must sign in once on the web (or admin sets new password) after file services arrived; Users page shows "Not ready for SMB and SFTP yet: the user signs in once on the web, or you set a new password." users.json:fileServices.signInOnce; FileServicePasswords.cs:7-12
- WebDAV and FTP check the Pithyx hash directly (no extra step); successful check remembered 5 min; failures counted by the same rate limiter (5 per user+address / 30 per address per 5 min -> `auth.rate.limited`). ProtocolSignIn.cs:22,40-60; LoginRateLimiter.cs:7-9
- Password changes by admin or recovery update the SMB/SFTP passwords too. users.json:password.fileServices
- 2FA users use their password only. ProtocolSignIn.cs:12
- WebDAV auth: HTTP Basic realm "Pithyx", UTF-8; wrong -> 401. WebDav.cs:158-163
- WebDAV when off answers 404. WebDav.cs:146-150
- Admin switches: Settings > File services: switches "Offer shared folders over SMB/SFTP/WebDAV", "Offer shared folders over FTP with TLS", workgroup, "Save". settings.json:fileServices.\*
- Error codes: `fileservices.service.unavailable` ("... cannot be switched on: its program is not installed on this system."), `fileservices.workgroup.invalid`. errors.json
- Lock-outs: files created over WebDAV belong to the writing user. settings.json:fileServices.webdav.trash

### Questions users will ask (file-access)

- Why can't I connect with my password? Right missing, account deactivated, or must sign in once on the web (SMB/SFTP). (ProtocolSignIn.cs:70; users.json:fileServices.signInOnce)
- Why doesn't the box show in Windows Network? Not announced; use the address. (no mDNS code)
- Which address do I use? Same as the web address of the box (hostname or IP). (FileServicesPage.tsx:55-61)
- Do I need 2FA code for network drives? No. (ProtocolSignIn.cs:12)
- Can I use SMB 1/2? No, SMB 3 only. (SmbConfig.cs:74)
- Is my traffic encrypted? SFTP and FTPS yes; SMB signed, encrypted only if share requires it; WebDAV depends on HTTPS (HTTP today). (SmbConfig.cs:75,119; Program.cs:33)
- Do deleted files land in the recycle bin? SMB and WebDAV yes; SFTP/FTP: unknown/bypass. (settings.json; TODO.md:1379)
- Why does SFTP give no shell? By design; shell needs SSH right + switch. (SshdConfig.cs)
- Can I use SSH keys for SFTP? No, password only. (SshdConfig.cs:37)
- What happens to my files' owner? Uploader's Linux account. (WebDAV text; FileService.cs:121)
- Which ports must be open on my router/firewall? SMB 445, SFTP 22, FTP 21 + 50000-50099, WebDAV 7484 (HTTP). Firewall rules for these not found in code. (see Open questions)
- Why can't I change permissions from Windows? ACL editing off; use Pithyx. (SmbConfig.cs:16)

### Not in code (plan only), file-access

- WS-Discovery / mDNS announce (TODO.md:1379); SFTP keys; NFS (TODO.md:618; D287 "step 12 without NFS"); HTTPS 443 (M5, TODO.md:613).

## Open questions

1. Password change: TODO.md:1374 plans a user docs page "for passwords" and TODO.md:1166 mentions "password change", but code has only an admin reset (UserEndpoints.cs:51). account.md "Changing the password" must say: ask an administrator. Confirm no self-service is intended for v0.1.
2. Sessions and devices: no list/revoke UI or API. Page heading "Sessions and devices" can only describe lifetime, cookies and "Get desktop from". Rename heading?
3. Recycle bin retention can only be changed through the API; the UI shows the value but has no editor (grep settings/\*.tsx). Admin docs may need to say so; confirm.
4. TODO.md:1367 says double-click downloads files "for now"; code opens the Viewer for known types (defaultAction.ts). TODO is outdated.
5. `app.backend.unsupported` error text ("installs apps without a backend only") is unused and contradicts the Docker runtime (ErrorCodes.cs:62). Stale i18n?
6. Files i18n `places.*` (Home/Desktop/Documents) unused; does a "Home" share exist for users (home dataset) or not? unknown.
7. Start menu: search results for settings/files and "Recent files" are always empty (Desktop.tsx:662-663) though labels exist; should docs mention search finds apps only?
8. Window limit 50: no user message when reached (launchTarget 'full' just returns false). Confirm.
9. Upload: no chunk size, no resume (CLAUDE.md/ task hint expected chunks). Folder drops unclear (File[] only). Confirm folder upload unsupported.
10. SFTP/FTP deletes: SFTP bypasses recycle bin (TODO.md:1379); FTP through `ProtocolFiles` (D291) probably uses the bin: not verified. Confirm per protocol.
11. Network/ports: no firewall configuration for 445, 22, 21, 50000-50099 in code (only `inet pithyx apps-input`, DockerHost); docs cannot claim ports are opened automatically. SSH port 22 is the system sshd default.
12. HTTPS not implemented yet (HTTP 7484, 80 redirects). WebDAV URL examples must say http today; CLAUDE.md says 443 + 7484. Which to document?
13. Theme/language are per device (localStorage) but accent/wallpaper per user: confirm intended and whether docs should say sign-in screen language is separate from the account language.
14. Admin disabling another user's 2FA: no endpoint seen. Lost phone + no recovery codes for non-admins = ? (only admin recovery key resets 2FA, for admin accounts).
15. Device cookie and layout "Get desktop from" lists only devices with a non-expired layout (90 days); devices appear by browser/OS only. Confirm wording.
16. Global shortcuts Alt+Shift+PageUp/PageDown/Space are not shown anywhere in the UI; document them? (keys.ts only)
17. Compact size class shows only the close button in the title bar; minimize is not reachable there (use switcher/back gesture). Confirm intended (Window.module.css:171-176).
18. Windows Explorer / Finder / WebDAV mapping steps: not verifiable from code; only URL forms and ports are. Phone app names: none in code.
19. TODO says "Admins have every protocol" (D287), but UI per-user rights list protocols; admin implies all (users.json:grants.adminHasAll). OK but confirm for WebDAV/FTP under D290.
20. Notification: "Window arrangement not saved" etc. exist; no doc source on how often. Fine.
