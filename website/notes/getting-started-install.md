# Notes: getting-started and install pages

Evidence format: path:line, relative to repo root /home/user/pithyx. "(plan)" = only in TODO/CLAUDE.md, not in code.
Short paths: SI = daemon/src/Pithyx.Daemon/Installer/SystemInstaller.cs, IF = .../Installer/InstallerFacts.cs, IP = .../Installer/InstallPlans.cs, IE = .../Installer/InstallerEndpoints.cs, IC = .../Installer/InstallerContracts.cs, INS = i18n/en/installer.json, UI = desktop/src/installer/InstallerScreen.tsx.

## Global facts used by several pages

- Version in tree: 0.1.0-dev (daemon/Directory.Build.props:5). README says "Pre-alpha. Nothing here is usable yet. Do not put data you care about on it." (README.md:15). No released ISO, no download URL exists in repo (grep "releases" in README: nothing).
- Web interface today is plain HTTP on port 7484; port 80 redirects to 7484 (307, method kept); `/api/install-id` is exempt (daemon/src/Pithyx.Daemon/Program.cs:33,57-66; Hosting/PlainHttpRedirect.cs:15,19). HTTPS on 443 is NOT in code (Program.cs:202 comment; TODO.md:613; README.md:19). CLAUDE.md:118 states HTTPS 443 as the target.
- Port 80 listener exists only on a production build in real mode (Program.cs:58).
- Installed box: root locked, no login on tty, SSH off, Samba off until needed (image/customize.sh: "passwd --lock root", "systemctl disable ssh.service smbd.service ..."); CLAUDE.md:63.
- Console on tty1 shows logo + addresses; Alt+F2 opens a login prompt (image/rootfs/usr/lib/pithyx/console.sh:6,47). Logo is the only allowed ASCII art (CLAUDE.md:141).
- Pool encryption not in v0.1; wizard says it can only be chosen at creation and "keyLater" (i18n/en/storage.json: storage.wizard.encryption.explain / .keyLater). Owner decision: pools of v0.1 stay unencrypted (TODO.md:95).

---

# PAGE getting-started/what-is-pithyx.md

## What you get

- Free, self-hosted NAS and server OS with a web desktop; Debian 13; manages storage with ZFS; shares files over SMB, SFTP, WebDAV, FTP; runs apps as containers (CLAUDE.md:3; README.md:11).
- README.md:11 also lists NFS: NOT in code and "Not in v0.1" (TODO.md:524-526). Contradiction, see Open questions.
- Core apps (desktop names): Files, Viewer, Storage, Settings, Resource Monitor, Log Center, About Pithyx, Store (i18n/en/desktop.json: apps.org.pithyx.\*; daemon/src/Pithyx.Daemon/Apps/CoreApps.cs:13-20).
- Settings pages that exist: Info Center, Network, Regional settings, Backup and restore, Shared folders, File services, Users, Groups (desktop/src/apps/settings/SettingsApp.tsx:43-50).
- Web desktop works on phone and ultrawide (README.md:11; desktop/CLAUDE.md responsive rules).
- Users and groups as real Linux accounts, uid never reused (i18n/en/users.json: users.intro).
- Rights per user/group: admin, desktop login, SSH login, restart/shutdown; protocols SMB, SFTP, WebDAV, FTP; apps; shared folders none/read/read-write; union, no deny rules (i18n/en/users.json: users.grants.\*).
- File services: SMB and SFTP in Settings > File services; WebDAV and FTP exist (i18n/en/settings.json:101,108-109,216-230; TODO.md D291 line 1424). Defaults per D291: WebDAV and FTP off.
- Shared folders with daily restore points kept 7 days ("Previous versions") (i18n/en/settings.json: restorePoints.what).
- Configuration backup and restore, encrypted with a password you choose (i18n/en/settings.json: backup.\*).
- Apps: install from a file (.pithyxapp) in the Store, catalogs (official on, community off), updates, rollback; backends run in Docker on the app pool (CHANGELOG.md:17,22,24).
- Network settings changeable in the UI incl. bonds, VLANs, with 60 s confirm timer and rollback (TODO.md:91; CHANGELOG.md:15).
- Bundled third-party fonts etc.: n/a.

## How it is built

- C# / ASP.NET Core (.NET 10) daemon is the only privileged process; React + Vite + TypeScript desktop; apps are static frontend plus optional containers; image built with mmdebstrap (CLAUDE.md "Repository" block, lines ~170-185).
- Storage: ZFS pools for data; system disk is btrfs on mdadm RAID1 (one or two disks) (IP:25-27,60-70; SI:345-351).
- Config in SQLite (/var/lib/pithyx/pithyx.db); daemon generates smb.conf etc. from it (daemon/CLAUDE.md:~21-24).
- Single systemd unit `pithyx.service`; installer vs NAS decided by kernel flag `pithyx.installer` (image/rootfs/usr/lib/systemd/system/pithyx.service:1-2; SI:27).
- Licenses: AGPL-3.0, npm packages MIT (CLAUDE.md:3).
- No telemetry, no cloud accounts, no services the box depends on (CLAUDE.md:9). Installer and installed box need no internet (INS:5 welcome.intro "needs no internet connection").
- Debian security updates come from Debian itself (image/customize.sh apt sources to deb.debian.org). Box-side Pithyx updater: NOT in code (see updates page).

## What Pithyx is not

- Not usable for real data yet: pre-alpha (README.md:15).
- Fresh installs only, no import from other NAS systems (CLAUDE.md:10).
- Installer does not restore backups or import pools; both happen in Settings and Storage after install (TODO.md:487 owner decision; no such step in UI:276-330 page list).
- Not in v0.1: network bridges, web apps (`kind: "web"`), docker.socket / host.full rights, NFS, SSD cache, energy, resource history, Files rework, video thumbnails (TODO.md:524-526).
- No pool encryption in v0.1 (TODO.md:95).
- No Secure Boot support: installer refuses Secure Boot on (INS:16-17; IF:50-58).
- No 32-bit, no BIOS/legacy boot (INS:11-13). Installer is x86-64 only (SI:415 hard-codes `--target=x86_64-efi`; image/build.sh ARCH=amd64 in image/debian.env:4). arm64 image: plan only (TODO.md:471).
- No Wi-Fi support in the installer/network defaults: DHCP file matches `Type=ether` only (image/rootfs/etc/systemd/network/80-pithyx-ethernet.network:3). No wlan UI found (grep wifi/wlan in desktop/src, i18n: nothing). Say "wired network".
- No email password reset, no cloud account (i18n/en/auth.json: auth.forgot.intro).
- No mDNS / `.local` name: "the address is what works everywhere" (TODO.md:473, D294).

## Where to go next

- Nothing in code; link choices are the editor's. Candidates: quick-start, requirements, usb-stick, installer, first-setup.

## Questions users will ask

- Is it production ready? No, pre-alpha (README.md:15).
- Does it phone home? No (CLAUDE.md:9).
- Is it free? AGPL-3.0 (CLAUDE.md:3).
- Can I import my old NAS? No (CLAUDE.md:10).
- Does it support NFS? No, not in v0.1 (TODO.md:526).
- Does it need internet? Not for install (INS:5); box features that fetch (Store catalogs) need it: unknown how the install handles offline beyond that.
- Can I run it in a VM? Code is tested in QEMU/KVM with OVMF (image/test-boot.sh, image/test-install.sh). Not tested on real hardware or as virtual CD (TODO.md:471,490).
- Does it run on Raspberry Pi? No, installer is x86-64 only today (SI:415).

---

# PAGE getting-started/requirements.md

## Supported platforms

- 64-bit x86 with x86-64-v2: flags cx16, lahf_lm, popcnt, sse4_1, sse4_2, ssse3 all present in /proc/cpuinfo (IF:19,21-27). Installer text: "64-bit processor with x86-64-v2" (INS:10; failure INS:11 names SSE4.2 and POPCNT).
- UEFI boot only; check = /sys/firmware/efi exists (SI:225; IF:48). Legacy BIOS fails: INS:13.
- Secure Boot must be off (EFI variable SecureBoot, byte 4 == 1 means on) (IF:32,50). Reason: ZFS module not signed (INS:17).
- Memory: at least 4 GB, check passes if total + 400 MB tolerance >= 4,000,000,000 bytes, i.e. ~3.6 GB usable total passes (IF:9,15,53). Text: "At least {minGb} GB of memory" (INS:14).
- Debian 13 (trixie) base (image/debian.env:2).
- Supported per CLAUDE.md:113: x86-64-v2+, UEFI, 64-bit; community arm64 (plan; no installer support: SI:415).

## Reference hardware

- Reference: Intel N100/N305, 8 GB RAM (CLAUDE.md:114). Budget: daemon < 200 MB RSS, < 1 percent CPU idle (CLAUDE.md:114).
- These are CI budgets, not requirements; minimum enforced by the installer is 4 GB (IF:9).
- Nothing in code about CPU core count or ZFS ARC sizing recommendation (ZFS ARC cap only when VMs exist: daemon/CLAUDE.md:65, plan).

## Disks and pools

- System disk: internal disk of at least 40 GB (40,000,000,000 bytes) (IF:12; INS:18 "An internal disk of at least {minGb} GB"). Layout actually written per disk: ESP 512 MiB + system 32 GiB + swap 4 GiB (IP:40-42), the rest unused in v0.1 (TODO.md:497 System layout).
- Not offered as system disk: USB (transport usb), removable/mmc, read-only, in use (anything mounted), the installer stick (mount under /run/live), smaller than 40 GB (IF:78-86; INS:73-80 reason texts: "USB disk", "removable medium", "the USB stick of this installer", "read only", "in use", "smaller than {minGb} GB").
- HDD allowed with hint "Hard disk: it works, but an SSD is faster and quieter and uses less power." (INS:70; UI:734). SATA/SAS/NVMe: code does not check bus beyond USB/mmc (IF:81-82).
- One or two system disks; two = mirror (RAID1), never more, never a stripe (SI:262 `ids.Count is < 1 or > 2`; error `installer.disk.count`, i18n/en/errors.json installer.disk.count). Mirror option shows only if >= 2 suitable disks (UI:~775 `suitable.length >= 2`).
- Single disk is a RAID1 with one member so a second disk can join later (IP:21 comment; TODO D295). Adding the second disk later in Settings: NOT in code (TODO.md:495).
- System filesystem btrfs on mdadm RAID1 with subvolume `@`, mount options `compress=zstd:1,noatime` (SI:349-351; IP:61-67).
- Data pools are created in Storage after first sign-in, on other disks ("Your data goes on other disks later, in Storage." INS:61).
- Pool layouts and minimum disks: single 1, stripe 2, mirror 2, mirrors 4 (even count), raidz1 3, raidz2 4, raidz3 5 (daemon/src/Pithyx.Daemon/Storage/Zfs/ZfsPlans.cs:11-20,56). Pool name regex `^[a-z][a-z0-9_-]{0,31}$`, reserved names mirror, raidz\*, draid, spare, log, cache, special, dedup, pithyx; names starting `c0` refused (ZfsPlans.cs:35,40,263). Error codes `storage.pool.name_invalid`, `storage.pool.name_taken`, `storage.pool.disk_count` (ErrorCodes via ZfsPlans.cs:42,46,60).
- New pools: ashift=12, compression=lz4, atime=off (ZfsPlans.cs:25-29).
- Disks with data need their serial typed to be used; disks without serial and with data are refused (i18n/en/storage.json: storage.reason.has_data, .no_serial; storage.create.confirm).
- System disk never usable for pools (storage.reason.system_disk).
- Pools unencrypted in v0.1.

## Network

- DHCP on every wired (Type=ether) card by default, IPv4 and IPv6 (RA), same DHCP client identifier (MAC) in live and installed system so address stays (image/rootfs/etc/systemd/network/80-pithyx-ethernet.network:3-12).
- Installer needs a network cable/address only to be reached from another device's browser: console shows `http://<ip>:7484` plus QR (issue.sh:53-66); "Waiting for a network address (DHCP). Check the network cable." when none (issue.sh:67). IPv6 shown as `http://[addr]:7484` (issue.sh:27-31).
- Ports: 7484 (web, fixed), 80 (redirect). FTP 21 plus passive 50000-50099 and WebDAV `/webdav/` when switched on (TODO.md D291, line 1424: only plan-check against code if needed).
- HTTPS 443: not in code.
- Installer step "Network": per-card DHCP or fixed address, "Test connection" (applies, pings gateway, rolls back) (INS:40-57; CHANGELOG.md:15). More (bonds, VLANs) only in Settings > Network (INS:42).
- mDNS / `.local`: none.

## Community platforms

- arm64 / Raspberry Pi 5: plan only (CLAUDE.md:113; TODO.md:471). Not in code: image/build.sh and debian.env only amd64; installer grub target x86_64-efi (SI:415).
- Virtualisation: QEMU + OVMF + KVM tested (image/test-install.sh header). Other hypervisors / virtual CD: untested (TODO.md:471).

## Questions users will ask

- How much RAM? Installer refuses below ~4 GB (IF:9,15,53). Reference 8 GB (CLAUDE.md:114).
- Can I install to a USB stick or SD card? No (INS:19; IF:81-82).
- Does a 32 GB SSD work? No, 40 GB min (IF:12).
- Why is the rest of my system SSD unused? v0.1 leaves it unused (TODO.md:497).
- Can I use an HDD for the system? Yes with a hint (INS:70).
- BIOS/legacy? No (INS:13).
- Secure Boot? Must be off (INS:17).
- Do I need two disks? No; mirror optional (UI mirror checkbox, INS:63-64).
- Can I use Wi-Fi? Not supported in code (see above); unknown whether a Wi-Fi card would simply be ignored.
- Does it need a monitor? Not for install: use another device's browser, but the console address needs a screen or you find the address in your router. Unknown whether a headless box can be found (no mDNS).
- Which browsers? unknown (nothing in code; desktop/CLAUDE.md may say).

---

# PAGE getting-started/quick-start.md

Summary page; take facts from the install pages below. Step list as the code runs it:

## Download the image

- Name `pithyx-<version>-amd64.iso`, checksum file `pithyx-<version>-amd64.iso.sha256` (sha256sum format) in image/out (image/build.sh:70,162).
- Download location: not in code (no release published; website pages placeholder). Unknown.

## Write the USB stick

- Hybrid ISO: write with any image writer; also attachable as virtual CD (TODO.md:471 says so; tested only as virtual USB stick in QEMU). xorriso hybrid with appended GPT EFI partition (image/build.sh:156-161).
- Dedicated writer tool: none in code.

## Install

- Boot stick in UEFI mode, pick "Install Pithyx <version>" (or wait 5 s) (image/grub/grub.cfg:3,6).
- Console shows logo, `http://<ip>:7484`, QR (issue.sh:53-66). Open from another device. First browser owns the install (SI:95-108).
- Steps: Welcome and checks, Step 1 of 5 Name and region, 2 Network, 3 System disk, 4 Administrator, 5 Summary (INS:25; UI:39,319).
- Install Pithyx button; progress steps (INS:98-108); recovery key; "Remove the USB stick", "Restart now" (INS:117-125); page opens the new system when it answers.

## First setup

- No separate setup page after an installed box: administrator is created during install (SI:447-459); you sign in at `http://<address>:7484`.
- Recovery key shown once before the restart.

## Next steps

- Sign in; create a pool in Storage ("Create pool…", i18n/en/storage.json: storage.create.button); create shared folders (Settings > Shared folders); create users (Settings > Users and groups, "Create user", users.create.button); optional two-factor ("Two-factor authentication" in user menu, desktop.json menu.twoFactor); make a configuration backup (Settings > Backup and restore, settings.json backup.create.\*).

## Questions users will ask

- How long does it take? Doc says "a few minutes" (INS:5). Build tooling: unknown for real hardware.
- What if I don't see the page? Check cable/DHCP message on console (issue.sh:67); no mDNS name.
- Is there a default password? No; you choose admin in the installer (INS:84). Root locked.

---

# PAGE install/usb-stick.md

## Download

- ISO file name `pithyx-<version>-amd64.iso` (image/build.sh:70). Size: about 520 MB per TODO.md (D298, "a 520 MB ISO"; stale? image now carries Docker and kernel pair, size unverified). Update full file ~360 MB (TODO.md:1474, not ISO).
- Volume label `PITHYX`, appid "Pithyx <version>", publisher "Pithyx" (image/build.sh:157).
- Official download location / mirror / torrent: not in code. Unknown. Repository private/pre-release (TODO.md:93-95 "Make the repository public").
- Architecture: amd64 only.
- Reproducibility: pinned Debian snapshot 20261001T000000Z (image/debian.env:1); bit-for-bit not verified (TODO.md:501 D298).

## Check the download

- `<iso>.sha256` is produced with `sha256sum <iso>` run in the output dir, so contents are `<hash>  pithyx-<version>-amd64.iso` (image/build.sh:162). Verify: `sha256sum -c pithyx-<version>-amd64.iso.sha256` (Linux); Windows `certutil -hashfile`, macOS `shasum -a 256`: general knowledge, not in repo.
- ISO signature (GPG/minisign): not in code. Update files are Ed25519 signed, ISOs are not (tools/release/sign-update.sh handles .pithyxupdate only).
- A `.packages` list is written next to the ISO (`pithyx-<version>-amd64.packages`, image/build.sh:123): developer artifact, not for users.

## Write on Windows, macOS and Linux

- Nothing tool-specific in code. TODO.md:1375 lists "writing the stick" as a website doc follow-up.
- Facts to state: hybrid ISO, "any image writer" (TODO.md:471); whole stick is overwritten (general). Stick size: ISO size plus nothing; the stick is not used after boot (live system loaded from stick, `/run/live/medium`, IF:132).
- Must be written in raw/DD mode, not as files (general; hybrid ISO with appended EFI partition build.sh:157-160).
- Virtual CD alternative (hypervisor, IPMI): TODO.md:471 says supported; untested on real hardware.
- Stick is erased? Installer never touches the stick: it is excluded as "the USB stick of this installer" (INS:76; IF:80).
- UEFI-only stick; no BIOS boot sector (TODO.md:471; build.sh:22 comment).

## Questions users will ask

- Which program do I use? unknown (no recommendation in code).
- Can I put other files on the stick? unknown; no data partition defined (build.sh:156-161).
- Does the stick need to stay in? No: after install, boot entry points at the disk, even with stick in (SI:415-425 `--removable`, efibootmgr; TODO.md:488). UI still says "Remove the USB stick" (INS:122-123).
- Can I verify the ISO is genuine? Only the sha256 file; no signature (see above).

---

# PAGE install/firmware-and-boot.md

## UEFI settings

- UEFI required; legacy/CSM start fails check "Started with UEFI" with text INS:13 ("Switch the firmware to UEFI, then start from the USB stick again.").
- Check implementation: /sys/firmware/efi directory present (SI:225; IE environment SI:35).
- Boot order: GRUB `grub-install` runs with `--removable` on every ESP plus a named entry "pithyx" for the first disk; second disk gets extra entry "Pithyx (second disk)" via efibootmgr (SI:412-425). Fallback path `\EFI\BOOT\BOOTX64.EFI` (removable) (SI:415).
- Wake/boot menu key: firmware-specific, not in code.

## Secure Boot

- Must be off; installer shows INS:17 text: "Secure Boot is on. Pithyx uses a ZFS module that is not signed: switch Secure Boot off in the firmware settings (often under Security or Boot), then start from the USB stick again."
- Detection reads EFI var `SecureBoot-*` under /sys/firmware/efi/efivars; if variable absent treated as off (SI:219-221; IF:32,57).
- No MOK enrollment, no signed shim (nothing in image/). CLAUDE.md:141 says "No Secure Boot".
- Starting the stick in Secure Boot mode: the GRUB image is an unsigned standalone (build.sh:145-148); firmware may refuse to even start it before the installer page appears. Unknown what the user sees; no code path.

## The GRUB menu

- Embedded standalone GRUB, timeout 5 s, default first entry (image/grub/grub.cfg:3-4).
- Entries: "Install Pithyx <version>" (quiet loglevel=3), "Install Pithyx <version> (show boot messages)", "UEFI firmware settings" (`fwsetup`) (grub.cfg:6,11,16). Kernel flag `pithyx.installer` starts installer mode.
- Installed system GRUB: GRUB_DISTRIBUTOR=Pithyx, GRUB_TIMEOUT=3, quiet (SI:621-622). GRUB has no password (TODO.md D338 text, line 1474).
- Previous-snapshot boot entry (update rollback): not in code (TODO.md:513 "GRUB can also boot the previous snapshot").

## Booting from the stick

- Console greeting appears (logo, addresses, QR, who owns installation, "Whoever opens the page first owns the installation. To start over, restart from the USB stick.") (issue.sh:53-66).
- Live system login: none; root locked; Alt+F2 prompt exists but no user (CLAUDE.md:63).
- After a failed install the stick can be restarted; nothing is mounted left (SI:325-329).
- Console keyboard layout choice only affects the installed system (SI:379); live console layout default: image/rootfs/etc/default/console-setup (not read).
- Firmware: some boards hide USB until fast boot is off: general, not in code.

## Questions users will ask

- Stick does not boot / shows nothing: unknown; hints only that UEFI is needed and Secure Boot off.
- "Why Secure Boot off?" Unsigned ZFS module (INS:17).
- Which key opens the boot menu? unknown (hardware).
- Can I keep Secure Boot on and enrol a key? Not supported; no code.
- Does it install alongside Windows? No: installer erases the chosen disk(s) (INS:94; SI:340 wipefs/sgdisk zap) and has no dual boot code.

---

# PAGE install/installer.md

## Opening the installer

- Address: `http://<ip>:7484` from the console greeting (also QR of first address); IPv4 listed first, then IPv6 (issue.sh:21-33,53-66). Port 80 redirects to 7484 (PlainHttpRedirect.cs:19), docker/veth/bridge interfaces skipped (issue.sh:24).
- First browser to open claims it: `POST /api/installer/claim` sets cookie `pithyx_installer` (HttpOnly, SameSite Strict, path /api/installer) (IE:10,53-67; SI:95-108). Console line: "This installation is in use by the browser at <addr> since HH:mm UTC. To start over, restart from the USB stick." (SI:106).
- Second browser sees "Installation in use": "Another browser is already installing Pithyx on this computer. To start over, restart the computer from the USB stick." (INS:22-23); error codes `installer.claim.taken`, `installer.claim.required` (i18n/en/errors.json installer.claim.\*).
- No PIN. Cookie lasts as long as the live system (IE:9 comment).
- Welcome: title "Welcome to Pithyx", tagline "Let's begin the free journey.", intro "This installs Pithyx {version} on this computer. It takes a few minutes and needs no internet connection.", button "Begin" (INS:3-6). "Begin" is disabled until all checks pass (UI:~366 `disabled={!passed}`).
- UI languages: en and de, preselected from browser (UI:129-131).
- Steps count "Step {current} of {total}", total 5 (INS:25; UI:39). Buttons "Back" and "Next" (INS:26-27).

## What the checks mean

Five checks, shown on the first page "Requirements" (INS:7,9-19; SI:212-229; IF:40-60):

- cpu: x86-64-v2; always passes on non-x64 architectures (IF:48).
- uefi: booted with UEFI.
- memory: >= 4 GB (400 MB tolerance), shows found GB rounded to 0.1 (IF:53-57).
- secure_boot: off (variable missing counts as off).
- disk: at least one suitable internal disk >= 40 GB.
- A failed check shows the red text (xxxFailed) and blocks "Begin"; server also refuses `POST /api/installer/install` with `installer.check.failed` + param `check` (SI:233-236; errors.json installer.check.failed: "This computer does not meet a requirement of Pithyx. The first page of the installer shows which.").
- Checks are re-evaluated on every status poll (SI:118-137).

## Step 1 "Name and region" (INS:28-39)

- "Name of the NAS" default `pithyx`; lowercase letters, digits, hyphens; 1-63 chars; no leading/trailing hyphen (UI:135; IF:84-89; INS:31 hint; error installer.hostname.invalid).
- "Language" (en, de), "Formats" (country list or "Follow each user's language"), "Time zone" (prefilled from browser), "Time servers (optional)" comma separated, empty = defaults, "Keyboard layout of the console" (us, gb, de, ch, at, fr, be, nl, es, pt, it, se, no, dk, fi, pl, cz) (SI:72-73; UI:55-83 prefill).
- Time servers must be host names or addresses, else `system.time.server_invalid` (SI:249-252; errors.json time.server_invalid). Time zone invalid: `system.time.zone_invalid` (SI:245-248).
- Written to the installed system: /etc/hostname, /etc/hosts, /etc/timezone + /etc/localtime, /etc/default/keyboard, timesyncd drop-in (only if servers given) (SI:373-385). Language/region go to DB table system_region (SI:462-464).

## Network (step 2) (INS:40-57)

- Shows every card with "Connected"/"No cable"/"No address yet"; "No network card was found." if none.
- Default: DHCP on every connected card; per card "Settings of {name}" with "Fixed address" option; "Name servers (optional)" space separated; "Test connection" (applies for a moment, page may lose connection for a few seconds, reports address and gateway reachability, rolls back) (INS:42-56; IE:75-89; CHANGELOG.md:15).
- "After the installation the box answers at {address}." (INS:57).
- Settings are validated with the same rules as Settings > Network (SI:175-182). Bonds, VLANs, more: only after install (INS:42).
- Console menu on tty1 can also set a static address or reset to DHCP without a password (console.sh:1-8,47; 60 s Keep/Undo).

## System disk and mirror (step 3) (INS:59-80)

- Each disk listed with model, path, "{size} · serial {serial}" or "no serial"; unsuitable disks are shown disabled with "Not offered: {reason}" (UI:~700-740; INS:68-72).
- Warning for disks with data: "This disk holds data that will be erased." (INS:71; IF:92 hasData = partitions or filesystem).
- Mirror: checkbox "Mirror on two disks" with hint "The system is written to both disks; the box keeps running when one of them fails." (INS:63-64); appears only when two suitable disks exist; second disk "Second disk"; errors "Choose a disk", "Choose a second disk or switch the mirror off." (INS:66-67).
- Server errors: `installer.disk.count` ("Choose one disk, or two different disks for a mirror."), `installer.disk.unsuitable` ("Pithyx cannot be installed on this disk. Choose another one.") (errors.json).
- Mirror disks of different size: layout uses fixed 36.5 GiB partitions per disk, so only the 40 GB minimum matters (IP:40-42; IF:12).

## Administrator (step 4) (INS:82-85; i18n/en/auth.json: auth.setup.\*)

- Fields: "User name" (lowercase letters, digits, hyphens, underscores, start with letter or underscore, max 32), "Display name (optional)" (max 64, no control chars), "Password", "Repeat password" (auth.json setup.\*; CredentialRules.cs:12-17,31,35, regex ^[a-z\_][a-z0-9_-]{0,31}$ at CredentialRules.cs:~100).
- Password: min 8, max 1024 chars, common-password list refused, no composition rules, strength meter only warns (CredentialRules.cs:14-15,78-95; TODO.md:1374). Errors auth.password.too_short / too_long / too_common.
- Reserved user names refused (root, daemon, pithyx, docker, sudo, users, adm, staff, ... systemd-\*) → `auth.username.reserved` (CredentialRules.cs:21-24,31-39).
- Root stays locked; this account is the first administrator (image/customize.sh "Root stays locked").

## Summary and install (INS:86-96)

- Table: Name, Language, Time zone, Keyboard, Administrator; list "These disks will be erased"; checkbox "All data on these disks will be erased" (required: `installer.erase.not_confirmed` "Confirm that all data on the chosen disks will be erased."); button "Install Pithyx" (INS:93-95).
- Errors from the server send you back to the matching step (UI:928-950).
- Progress page "Installing Pithyx" / "Keep the computer switched on." with steps: "Partitioning the disks", "Creating the mirror", "Creating the file systems", "Copying the system", "Writing the settings", "Installing the boot loader", "Finishing" (INS:98-108; SI:69). Step "raid" label says "mirror" also for a single disk.
- What each step does: wipe + GPT + ESP 512 MiB + 32 GiB + 4 GiB per disk (IP:31-47); mdadm RAID1 `/dev/md/pithyx-system`, `/dev/md/pithyx-swap` (IP:12-13,72-80); FAT32 label PITHYX-EFI, btrfs label pithyx-system, swap (IP:60-67); `unsquashfs` of live squashfs (SI:360, timeout 30 min SI:78); fstab, mdadm.conf, host files, DB (SI:362-399); ssh-keygen -A, update-initramfs, grub-install each ESP, update-grub (SI:402-426); sync + umount (SI:429-430).
- Failure page: "The installation failed", "Failed while: {step}", "Command: {command} (exit code {exitCode})", button "Change the settings" (INS:110-114). Error `installer.install.failed`: "The installation failed. Nothing was installed completely; you can try again. The log of the installer has the details." (errors.json). Installer log download: NOT in code (TODO.md:1375). Where the log is on the live system: daemon logs (journal); no UI. A failed attempt unmounts the target and can be retried (SI:325-329). A running/finished install cannot be started again: `installer.install.running` (SI:159-162).

## After the restart

- Recovery key page: title "Pithyx is installed", text "This key is the only way to reset the administrator password. It is shown only now. Store it somewhere safe before you restart.", buttons "Copy key", "Print", checkbox "I have stored the recovery key", "Continue" (INS:117-119; i18n/en/auth.json recoveryKey.\*). Key shown only to the claiming browser (SI:134).
- Then "Remove the USB stick": "Remove the USB stick, then restart. The computer starts Pithyx from its disk, and this page opens it as soon as it answers." button "Restart now" (INS:122-124). Reboot happens after 2 s (SI:201-202); only possible once installed (`installer.reboot.not_ready`).
- "Pithyx is starting" page; polls `http://<host>/api/install-id` on port 80 every 3 s; when the id equals the installed one it navigates to `http://<host>:7484/` (UI:1065-1106). Fallback text "If nothing happens, open {url} yourself." (INS:129).
- Host used for the redirect: address after change (static address from step 2), UI:~165 `hostAfterChange`. If the box gets another address after restart (e.g. DHCP lease changed): page cannot find it; TODO.md:1375 lists this as a docs follow-up. DHCP client identifier is MAC-based so the same address is likely, not guaranteed (80-pithyx-ethernet.network:11).
- Installed system listens on 7484 (HTTP) and 80 (redirect); first request shows sign-in, not setup (image/test-install.sh header; Auth SetupRequired false because the installer created the user, SI:459).
- Boot with the stick still in works (boot entry, `--removable` fallback) but remove it anyway (INS:122).
- Console on the installed box: same logo/addresses (no QR, no installer line) (issue.sh:70-77).

## Questions users will ask

- Can I go back and change my choices? Yes with "Back"; after a failed install via "Change the settings" (INS:26,113).
- Can two people install at once? No, first browser owns (SI:95-108).
- What if I close the browser during install? Install runs in the daemon; progress continues (SI:168 Task.Run); but the cookie is needed to reconnect; same browser works. Other browser gets "taken". Unknown if cookie survives browser restart (session cookie? IsEssential, no Expires set at IE:57-64 so it is a session cookie): closing the browser fully probably loses ownership; flag as "unknown / likely".
- Will my other disks be touched? Only the chosen disks are wiped; others untouched (SI:340 uses only `disks`). The ZFS pools on other disks: not touched; they must be imported afterwards (see recovery).
- Does it keep data from an older Pithyx? No, fresh install erases the chosen disks.
- How long? "a few minutes" (INS:5).
- Can I install without a second device? No: installer is web-only; the console has no installer UI beyond network menu (console.sh).
- What are the swap and ESP for? ESP = boot partition; 4 GB swap RAID1 (IP:42).
- Why must the stick be removed? Not required (see above); UI says remove.
- Is the install log available? Not in UI (TODO.md:1375).

---

# PAGE install/first-setup.md

Note: after the installer there is no setup page. `SetupScreen` (desktop/src/auth/SetupScreen.tsx) runs only when the daemon has no users (Auth SetupRequired, daemon/src/Pithyx.Daemon/Auth/AuthService.cs:25; `auth.setup.already_done`, AuthService.cs:37). It is used on a development box or after a reset; on an installed box the administrator comes from installer step 4. The page should say this.

## The owner account

- Created in the installer (SI:447-459) with the same code as first setup (`AuthService.SetupAsync`, AuthService.cs:29-71): gets rights admin + desktop login (AuthService.cs:62), a Linux account (SetupAsync flow AuthEndpoints.cs:64-72), SMB/SFTP password hash when the box starts (AuthEndpoints.cs:68).
- Fields and rules as in installer step 4 (see above). Button text in the standalone SetupScreen: "Create administrator" (i18n/en/auth.json: setup.submit); title "Set up Pithyx", intro "Create the administrator account. You can add more users later." (setup.title, setup.intro).
- First uid from the user range 20000-29999 (CLAUDE.md/TODO.md:1162; UidAllocator).
- Sign in: "Sign in" form, user name + password; "Welcome back" heading; rate limit: 5 failed tries per user name+address and 30 per address per 5 minutes, error `auth.rate.limited` "Too many attempts. Try again in {n} seconds." (LoginRateLimiter.cs:13-15; errors.json auth.rate.limited).
- Optional two-factor (TOTP): "Two-factor authentication" in the user menu, "Set up" → QR + manual key + "Turn on"; 10 single-use recovery codes of 10 characters (AuthService.cs:17-18; auth.json totpSetup.\*).
- Administrator is not special in Linux: root stays locked, SSH off, SSH login is a per-user right (users.json grants.right.sshLogin).

## The recovery key

- Format `PTHX-` + 26 Crockford Base32 characters in groups of 4, e.g. `PTHX-7KQ2-...` (AuthService.cs:15-16,295; Secrets.cs:10-28: no I L O U). Typing accepts lower case, spaces, hyphens, O as 0, I/L as 1, prefix optional (Secrets.cs:34-50).
- Stored only as password hash (Argon2id via PasswordHasher) in setting `recovery_key_hash` (AuthService.cs:14,42,67). Cannot be shown again.
- Shown once: in the installer after the install and before restart; in standalone setup as step 2 of 2 (SetupScreen.tsx:20 STEPS=2; RecoveryKeyCard.tsx). Card: "Your recovery key" / "This key is the only way to reset the administrator password. It is shown only now. Store it somewhere safe, for example printed or in a password manager." buttons "Copy key", "Print", checkbox "I have stored the recovery key" (required to continue), "Continue to the desktop" (auth.json recoveryKey.\*). If copying is unavailable (no secure context) text is selected: "Copying is not available here. The key is selected: press Ctrl+C or use the context menu." (auth.json recoveryKey.copyManually).
- The key is per box, belongs to the administrator role: any administrator can be reset with it (AuthService.cs:266-268: user must be admin, deactivated admin counts).
- A new key is created with each reset (see recovery page). The configuration backup contains the recovery key (settings.json backup.categories.users.description: "...the recovery key and personal settings").
- Print layout: print button hides other UI (RecoveryKeyCard noPrint classes).

## The first pool

- No pool exists after install. "No pools yet." (storage.json pools.empty). Home folder: users show "No pool yet" until a pool exists; "The user gets a home folder on the first pool when there is one." (users.json noHome, create.intro).
- Create in Storage app (admin only): button "Create pool…" (storage.create.button) opens the wizard with steps "Disks", "Layout", "Encryption", "Name and check" and final button "Create pool" (storage.wizard.steps.\*, wizard.create).
- Layouts offered: Single disk, Mirror, Mirror pairs, RAID-Z1, RAID-Z2, RAID-Z3, Stripe, with recommendations by disk count (storage.wizard.layouts._, wizard.recommend._). Min disks see requirements page.
- Encryption step: "Should the pool be encrypted?"; options "No encryption", "Encrypt with a key" (shown as coming later: "Comes with the key vault in the next update. To encrypt this pool, wait for it: a pool created now stays unencrypted.") (storage.wizard.encryption.\*).
- Disk rules: disks with data require typing the serial ("Type the serial number of {disk} ({serial})", storage.create.confirm); system disks refused; "Show commands" preview lists exact commands (storage.create.preview/commands).
- Pool name hint: "Lowercase letters, digits, - and \_; starts with a letter." (storage.create.nameHint).
- Progress: "Running step {step} of {count}…", can close window, notifications report when done (storage.run.\*).
- Apps need an app pool chosen at first app install (CLAUDE.md "Apps", setting apps.pool); shared folders live on a pool dataset (settings.json shares.intro).
- Is a first-run wizard prompting for a pool? Not found in desktop/src (grep onboarding/firstRun: nothing). Page must tell the user to open Storage.

## Inviting users

- No invite mechanism in code (grep invite: nothing in daemon/desktop/i18n). Administrator creates accounts with a password: Settings > Users and groups > "Create user" (users.json create.\*; UserService.cs:36-80).
- Fields: user name, display name (optional), password, groups ("Every user is also in the group users.") (users.json field.\*).
- Default rights of a new user: sign in to the desktop, SMB, and the apps Files and Viewer (Grant.cs:53; CoreApps.cs:26). Everything else (SFTP, WebDAV, FTP, SSH, restart/shutdown, shared folder access, Storage/Settings/Store apps) must be granted: "Rights" dialog (users.json grants.\*). Admin right gives everything (users.grants.adminHasAll).
- Home folder created on the first pool when present (UserService.cs:78; users.json create.intro).
- User must sign in once on the web before SMB/SFTP work if the password was unknown to those services: "Not ready for SMB and SFTP yet: the user signs in once on the web, or you set a new password." (users.json fileServices.signInOnce). Users made by an admin already get the password pushed (UserService.cs:78-79).
- Admin password reset for a user: "New password" action; user is signed out everywhere (users.json password.\*).
- Groups: "Create group", rights to groups apply to all members (users.json groups.\*).
- Deleting a user keeps files and home folder; uid never reused (users.json delete.intro).
- Permissions model details belong to the admin docs.

## Questions users will ask

- I lost the recovery key, what now? Nothing resets the admin password without it; reinstall (auth.json forgot.adminIntro).
- Can I see the recovery key again? No (shown only now). Unknown whether the backup restore shows it: no.
- Can I change the recovery key? Only via reset with the old one (AuthService.cs:278-289). No "regenerate" button found.
- Do other admins have their own key? No, one key per box (setting `recovery_key_hash`).
- Can users reset their own password? No email reset: ask an administrator (auth.json forgot.intro).
- How do I add my family members? Create user with a password; no invite link.
- Is 2FA mandatory? No, optional per user (auth.json totpSetup.off).
- Why can't my new user open Storage/Settings? Not granted; only Files and Viewer by default (Grant.cs:53, CoreApps.cs:26).

---

# PAGE install/updates.md

KEY FINDING: There is NO box-side updater in the daemon. `ls daemon/src/Pithyx.Daemon` has no Updates folder; grep for UpdateKeys / update-source / update.file / update.channel in daemon/src, desktop/src, i18n/en finds nothing (checked). tools/release/update-file.ts says its verdicts match "daemon/src/Pithyx.Daemon/Updates" (update-file.ts:2) but that folder does not exist. Settings has no "Updates" page: nav key `settings.nav.updates` ("Updates", i18n/en/settings.json:7) only appears in the design gallery (desktop/src/design/SampleApps.tsx:229); real pages list SettingsApp.tsx:43-50 has none. Everything about checking/applying on the box is PLAN ONLY. The release-build side exists.

## Channels

In code:

- Two channels `stable` and `beta` in the update manifest/channel schema (tools/release/update-file.ts:33 `type Channel`; tools/release/schema/update-channel.v1.schema.json). A stable box accepts only stable files, a beta box accepts stable and beta (update-file.ts:188-190).
- Beta pre-releases `beta-<version>` plus moving release `channel-beta` on GitHub (tools/release/publish-beta.sh:2-4; build-channel.sh header); stable: `channel-stable` and `v<version>` (build-channel.sh:11-12).
- Store catalogs have their own stable/beta channel setting (CHANGELOG.md:17) : that is apps, not system.
  Plan only (D297, D338, TODO.md:510-516, 1474): source in `/etc/pithyx/update-source.json` (URL + optional bearer token; no UI); channels stable = tags on master, beta = dev; default source project's GitHub releases; default channel and setting UI: unknown. Not in code: `/etc/pithyx/update-source.json` (grep: nothing).

## Checking and installing

In code (build side only):

- An update is one file `pithyx-<version>-amd64.pithyxupdate`, a deterministic tar: `manifest.json`, `manifest.json.sig`, `debs/` (with Packages, Release) (tools/release/pack-update.sh:2-5; build-update.sh:6-13).
- Contents: packages `pithyx`, `pithyx-kernel`, ZFS module, plus every Debian package whose version differs from the minimum version's package list (default minimum 0.1.0; baseline lists in image/baseline/ which does not exist in the repo yet) (build-update.sh:11-15,77-79). `--full` builds a file with every package (about 360 MB per TODO.md:1474).
- Manifest has format version, version, channel, minimum version, architecture, commit, kernel + ZFS versions, size + SHA-256 per file (update-file.ts:52-65; schema update-manifest.v1.schema.json).
- Debian security updates are not in the file; the box gets them from Debian (build-update.sh:19-20; image/customize.sh apt sources deb.debian.org).
- apt pinning: kernel, headers and ZFS packages from Debian have priority -1; `pithyx*` never from Debian; `pithyx-kernel` pins kernel + matching ZFS module and conflicts with zfs-dkms (image/rootfs/etc/apt/preferences.d/pithyx:1-12; image/deb/pithyx-kernel/control).
- apt on the box: do users run `apt upgrade` themselves? Possible (Debian sources configured) but not a documented path; the pinning prevents kernel/ZFS changes.
  Plan only: daily check at random time, notification only, never automatic install, switch to turn check off, offline file upload in Settings, btrfs snapshot before apply, health check after restart, auto rollback, `@snapshots`, last three kept, console recovery (TODO.md:510-516, 1474). Not in code: grep `@snapshots` only in installer-free places (ErrorCodes comment unrelated). The installer creates subvolume `@` only (SI:349), no `@snapshots`.

## Signatures

In code (tools side):

- Ed25519 over the exact bytes of manifest.json; stored in `manifest.json.sig` (sign-update.sh:2-3; update-file.ts:155-185). At least one signature by a key trusted for the channel; all trusted signatures must verify; other keys ignored (rotation) (update-file.ts:151-155).
- Public keys: tools/release/keys.json lists only `pithyx-beta-1` (channels: beta). `pithyx-release-1` (stable) is not in the file yet: "joins once the owner created it" (keys.json:2).
- Channel documents `channel.json` + `channel.json.sig`, with a growing `sequence` so old ones cannot be replayed (build-channel.sh:8-10; update-file.ts:227-262: update.channel.sequence_old).
- Error/issue codes of the verdict corpus: update.file.signature_invalid, update.file.invalid, update.file.channel, update.file.unexpected_entry, update.file.missing_entry, update.file.hash_mismatch, update.channel.signature_invalid, update.channel.invalid, update.channel.mismatch, update.channel.sequence_old (update-file.ts:196-295). These codes are NOT in daemon ErrorCodes nor errors.json (grep: nothing).
- Unsigned files = development files; plan says only a box with environment `dev` installs them (build-update.sh:17-18; TODO.md:1474). /etc/pithyx/environment is `prod` on the image (image/rootfs/etc/pithyx/environment).
- Beta key only beta, lives with the maintainers; stable key held by the owner (sign-update.sh:9-11). Stable key id `pithyx-release-1` (sign-update.sh:10).
- ISO download is not signed (see usb-stick).

## When an update fails

- Nothing in code. Plan: automatic rollback to the btrfs snapshot after a failed health check; GRUB can boot the previous snapshot; rollback asks to type the version; console recovery menu without password (TODO.md:513,1474). The installer's layout supports it only in that the root is the subvolume `@` on btrfs (SI:349-351).
- What exists as safety net today: configuration backup (Settings > Backup and restore) to rebuild a reinstalled box (see recovery).

## Questions users will ask

- Does Pithyx update itself? No updater exists in code today; plan says notify only, never automatic (TODO.md:512, D297).
- How do I update today? Unknown: no mechanism in the box. Reinstall + restore backup is the only code-backed path.
- Are updates signed? Update files will be (tools exist); ISO is not.
- Does it contact a server? Plan: GitHub releases (default source) once a day; nothing in code yet. The Store catalogs do fetch from git hosts today (CHANGELOG.md:17).
- Can I update offline? Plan yes (file upload in Settings); not in code.
- Will an update upgrade my ZFS pools? Plan: never automatically (TODO.md:513).
- Do I get kernel updates from Debian? No, pinned away (preferences.d/pithyx).
- Beta vs stable: which should I pick? unknown (no UI).

---

# PAGE install/recovery.md

## Forgotten password

- Normal users: "Pithyx has no reset by email. Ask an administrator to set a new password for you." (auth.json forgot.intro). Admin sets it in Settings > Users and groups > "New password" ("The user is signed out everywhere and signs in with the new password.", users.json password.intro); SMB/SFTP get the new password too (users.json password.fileServices).
- Administrators: sign-in screen link "Forgot password?" (auth.json login.forgot) → "Reset your password": fields "User name", "Recovery key", "New password", button "Reset password" (auth.json forgot.\*; LoginScreen.tsx:297-350). Endpoint `POST /api/auth/recover` anonymous (AuthEndpoints.cs:32,105-118).
- Works only for a user name that is an administrator (a deactivated admin counts) and a correct key; otherwise one generic error `auth.recovery.invalid` "The user name or recovery key is incorrect." (AuthService.cs:266-275; errors.json auth.recovery.invalid). Same dummy-hash time for unknown users (WasteTimeAsync).
- Effects: password changed (min 8 chars etc.), TOTP turned off, all sessions of that admin ended, WebSockets closed, new recovery key issued; old key dead (AuthService.cs:254-256,278-292; AuthEndpoints.cs:116-117; auth.json forgot.doneIntro: "Sign in with your new password. This is your new recovery key; the old one no longer works. Two-factor authentication was turned off and all sessions were ended. Store the key somewhere safe."). Only that user's TOTP/sessions? Code: ClearTotpAsync(user.Id) and DELETE sessions of user.Id: yes only that admin.
- Samba/Linux password updated (passwords.SetAsync, AuthEndpoints.cs:116).
- Rate limit: 5 failures/user+address per 5 min (LoginRateLimiter.cs:13-15). Error `auth.rate.limited` with Retry-After (ErrorHandling.cs:81).
- Lost key and lost password: "Without it the system has to be reinstalled." (auth.json forgot.adminIntro; CLAUDE.md:140). No console reset, no recovery boot entry in code (grep grub.cfg/console.sh recover: nothing). Other administrators can reset each other's passwords in Users; only if one admin remains signed in.
- Lost TOTP device: sign in with a recovery code ("Use a recovery code instead", auth.json totp.\*) or admin recovery key flow turns TOTP off. An admin resetting another user's TOTP: unknown, grep Users for totp reset: not checked.
- Last administrator cannot be removed or demoted; nobody deletes themselves (TODO.md:1183; users code).

## System disk failed

- With a mirror install, the box boots and runs with one disk missing: GRUB is on both ESPs, ESP fstab entries `nofail`, `x-systemd.device-timeout=10s` (SI:412-426; InstallerFiles.Fstab SI:601). Proven in test (`image/test-install.sh --mirror` boots with first disk removed; header lines 1-8).
- Notification of a missing mirror disk, replacing a failed disk, showing mirror state in Settings, ESP sync hook after updates: NOT in code (TODO.md:494-495, 1375).
- Single-disk install: system disk failure loses the system (not the pools). Reinstall + restore (below).
- "The system disk is disposable": everything to rebuild is in the config backup (daemon/CLAUDE.md:25).

## Reinstalling

- Make a configuration backup BEFORE: Settings > Backup and restore > "Create a backup" → "Create and download"; password required (same rules as account password, min 8), file downloaded, nothing kept on the box; "Without this password the backup cannot be restored. Pithyx does not keep it, and nobody can recover it." (settings.json backup.create.\*; BackupContracts.cs:15; D293 TODO.md:1426). Format `.pithyxbackup`, max 16 MB (BackupArchive.cs:31; errors.json system.backup.too_large).
- Contents: users/groups/passwords (Linux + Samba hashes), 2FA, recovery key, personal settings; shared folders + file service settings; restore point and scrub/self-test schedules; system language/time/log retention; certificates + SSH host keys; apps registry ("Apps" part: installed apps with permissions, who may use, app pool) (settings.json backup.intro, backup.categories.\*). Does NOT hold files on pools (settings.json backup.intro). App data stays in the pool.
- Do the backup restore after reinstall in this order: install fresh (new admin, new recovery key), sign in, create/import pools first so shared folder paths exist (inference; code rule: rights on shared folders "come from the side whose shared folders are kept", TODO.md:1426; storage not checked in restore code: verify), then Settings > Backup and restore > "Choose file…" + "Backup password" + "Check backup" → shows created at, from host, Pithyx version, counts → choose parts ("What to restore") → "Restore…" → confirm "Restore now". Each chosen part replaces the current one completely; restoring users signs everyone out; Pithyx restarts (settings.json backup.restore._, backup.confirm._).
- Backup from a newer Pithyx is refused: `system.backup.too_new` "A newer version of Pithyx ({version}) created this backup. Update this box first." Other errors: invalid file, wrong password, expired (15 min), restore failed (nothing changed), restore running (errors.json system.backup.\*). Older backups are migrated (TODO.md:1426).
- Restore keeps a copy of the replaced DB as `pithyx.db.pre-restore-<time>.bak` (TODO.md:1426; D293).
- Reinstall erases only the chosen system disk(s); pool disks are untouched unless chosen (SI:340). Installer lists disks that hold data with a warning (INS:71).
- Installer cannot restore a backup or import pools (TODO.md:487). Restore during setup: later (TODO.md:1426).
- Hostname, network, region are re-entered in the installer; a restore of "System settings" brings language/time/log retention (settings.json backup.categories.system). Network settings: not listed in the categories; unknown.
- Installed apps' data and Docker's data live on pool, not system disk (CLAUDE.md "Apps"; daemon/CLAUDE.md:48). After restore the apps part brings the registry back; behaviour of Docker images/containers: unknown, not read.

## Importing the pools again

- Pools are not remembered by the fresh system. Storage app > Pools tab > "Import a pool…" (storage.import.button), dialog "Import a pool": explains import is read-only first; "Scan disks" ("The scan reads the labels of every disk and may wake a sleeping disk. It runs only when you start it.") (storage.json import.\*; StorageEndpoints.cs:36,43).
- Two-stage flow: "Import read-only" (nothing mounted, nothing changes) → check datasets on its card → "Import for use…" (storage.import.done, change.import.\*; PoolImportScanner.cs:8-10; StoragePlanner.cs plan ids pool.import.readonly then pool.import).
- Pool last used by another system and not exported (what a reinstall looks like): flagged "Used by another system"; needs "Import it anyway (force)" and typing the pool name; warns "two systems writing to one pool destroy it" (storage.import.usedElsewhere/otherSystem/force; StoragePlanner.cs:174-193; error `storage.import.force_required` via ErrorCodes.StorageImportForceRequired). Whether a pool after a reinstall counts as "another system" depends on ZFS hostid: new install has a new hostid → expect force needed; not verified in code → flag as expected/unknown.
- Name clash with an imported pool: "New name for the pool" (import.newName, import.nameTaken).
- Pools that cannot be imported: "This pool cannot be imported, usually because disks are missing. Connect them and scan again." (import.cannot); devices Pithyx does not know: import.unknown; error `storage.import.device_unknown` (StoragePlanner.cs:166-170).
- Datasets that would hide system folders stay unmounted with canmount=noauto, administrator types the name to confirm (storage.json unmounted.system_path; StoragePlanner.cs:245).
- After import: shares, users' home datasets, app datasets `<pool>/pithyx/apps/<id>` exist on the pool (daemon/CLAUDE.md:44-47; TODO.md:1168 homes `<pool>/pithyx/homes/<user>`). Re-creating shared folders in Settings to match existing datasets: unknown whether import re-registers them; the Backup "Shared folders" part restores the registry.
- Encrypted pools: none in v0.1.
- Does the daemon auto-import at boot? Image has Debian zfsutils-linux (zfs-import-cache.service is listed in log sources, Logs/JournalParser.cs:13); on a fresh install no cache file exists. Not verified. Unknown.

## Questions users will ask

- I forgot my admin password and have the key: use "Forgot password?" (above).
- I lost both: reinstall; pools can be imported again; users/shared folder setup lost unless a backup exists.
- Will reinstalling delete my data on the pools? Not if only the system disk(s) are selected; the installer lists data on disks (INS:71) and requires the erase checkbox (INS:94).
- Does the backup include my files? No (settings.json backup.intro).
- Can I restore a backup from a newer version? No, too_new.
- Where is my backup stored? Only the downloaded file; keep it off the box (backup.create.intro).
- Is there a root password / emergency shell? Root locked; Alt+F2 login prompt exists but only for Pithyx accounts with a login shell (image/customize.sh; console.sh:47; users shell nologin unless SSH right TODO.md:1165). Unknown whether an admin without the SSH right can log in on tty2 at all (shell nologin) → probably not.
- My mirror disk failed, what do I do? Box keeps running. Replace workflow is not in the UI yet (TODO.md:495).
- My system disk is dead and no backup: reinstall, new accounts.

---

# Not in code (plan only), per page

- what-is-pithyx: NFS (README.md:11 vs TODO.md:526); arm64 image (D294, TODO.md:471); web apps `kind: "web"` (TODO.md:526); encryption (TODO.md:95, D after v0.2).
- requirements: arm64/Raspberry Pi 5 installer (CLAUDE.md:113); adding second system disk later, replacing failed mirror disk (D295, TODO.md:494-495); ARC cap for VMs (daemon/CLAUDE.md:65); HTTPS 443 (CLAUDE.md:118).
- quick-start: download page and website screenshots (website D340).
- usb-stick: published ISO location, ISO signature (D296 release job `image.yml` with `pithyx-release-1`, TODO.md:503); reproducibility check.
- firmware-and-boot: GRUB entry for previous system snapshot (D297/D338, TODO.md:513); Secure Boot support (CLAUDE.md:141 "No Secure Boot").
- installer: installer log download on failure (D299 follow-up, TODO.md:1375); Playwright run of the page in the VM; test on real hardware and virtual CD (TODO.md:490,1375); recovery key in step 4 (D299 deviates, TODO.md:485).
- first-setup: "Trust this device" setup step / local CA download (TODO.md:1390, M5); passkeys (CLAUDE.md:119); home dataset `Desktop` folder and user quotas (TODO.md:1168-1169); first-pool prompt (not found).
- updates: everything on the box: check, notify, apply, offline upload, rollback, `/etc/pithyx/update-source.json`, `@snapshots`, health check, console recovery, ESP sync hook (D297, D338, TODO.md:510-516, 494, 1474); stable signing key `pithyx-release-1` not yet in keys.json; `image/baseline/` package lists absent.
- recovery: console password reset (explicitly none: D338 text, "Console recovery has no password in v0.1"); mirror replace/notify (TODO.md:494-495); restore during setup (D293 "later"); installer-time pool import (rejected D294).

# Fact counts per page (rough)

- what-is-pithyx ~28, requirements ~38, quick-start ~15, usb-stick ~14, firmware-and-boot ~18, installer ~60, first-setup ~42, updates ~35 (only ~12 about code that exists; rest plan), recovery ~38.

# Open questions (code vs TODO vs CLAUDE.md)

1. No box-side updater in daemon (no Updates folder, no UI, no error codes) although update-file.ts says verdicts match `daemon/src/Pithyx.Daemon/Updates`. TODO.md:510-516 has all four updater items unchecked except "Release". updates.md can only describe the build/sign side and plan. Decide whether the page is published as plan or deferred.
2. Settings nav key "Updates" (i18n/en/settings.json:7) exists but no real page (SettingsApp.tsx:43-50). Dead key?
3. README.md:11 lists NFS; TODO.md "Not in v0.1" and code have no NFS. README also says "Nothing here is usable yet" (pre-alpha); CLAUDE.md has no such status.
4. HTTPS: CLAUDE.md:118 says HTTPS on 443 with self-signed cert; code serves only plain HTTP 7484 (+80 redirect). Pages must state HTTP today; also passkeys/service worker off. Trust wizard/local CA: not built.
5. ISO size "520 MB" (TODO.md:1431/501) predates the Docker/kernel-pair changes; no current number. Download URL, signature for the ISO: none. How the user obtains the ISO is undefined.
6. TODO.md:473 claims "port 80 redirects to 7484" for the live installer; code: port 80 listener only for production build in real mode (Program.cs:58) which the live system is too; consistent but untested by me. Installer page itself is served on 7484.
7. TODO.md:484 says recovery key appears in step 4; code shows it after the install (D299 deviation, flagged "the owner should confirm"). Docs follow the code.
8. Installer step labels: progress step key `raid` shows "Creating the mirror" even for a single disk (INS:102).
9. Installer cookie `pithyx_installer` has no expiry (session cookie): closing the browser during an install may lock the owner out (other browsers see "Installation in use"). Needs a test; not verified.
10. After reinstall, will existing ZFS pools need force import (hostid differs)? Not verifiable in code; also whether the fresh system auto-imports pools at boot (zfs-import-cache vs scan). Needs a dev01 test.
11. Backup/restore order after reinstall (pools first or not), whether network settings are in the backup, behaviour of installed apps and Docker after restore: not clear from code read. Backup category "System settings" lists language, formats, time zone, time servers, log retention only.
12. Can an administrator without SSH right use the Alt+F2 login prompt? Shell is nologin unless SSH login right (TODO.md:1165). CLAUDE.md:63 says "no login". Console hint text says "Alt+F2: log in". Contradiction for users: state "no local login" or clarify.
13. TOTP reset for another user by an admin: not checked.
14. Minimum RAM 4 GB (code) vs reference 8 GB (CLAUDE.md); no stated recommendation for ZFS memory per TB.
15. SATA/SAS/NVMe-only rule in D295 vs code: any non-USB/non-removable disk >= 40 GB passes (IF:78-86). Unknown how e.g. virtio, SD (mmc) behave; mmc is refused.
16. Wi-Fi: no support in code (only `Type=ether`), docs should say wired only; confirm with owner.
17. "Installer needs no internet" (INS:5): true for install; hostnames/time servers need network only after.
18. mDNS absent: how users find a headless box with no screen? Only router DHCP list; the console needs a monitor. State clearly.
19. `pithyx-release-1` missing from tools/release/keys.json; stable channel cannot be verified by any box yet.
20. Language support in installer is en and de only (UI:129-131); formats list `REGIONS` constant not read.
