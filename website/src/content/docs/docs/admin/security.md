---
title: 'Security'
description: 'Transport, open ports, physical access, SSH, two-factor sign-in, passkeys, the recovery key and what leaves the box.'
status: ready
audience: admin
sidebar:
  order: 9
---

Pithyx keeps everything on the box: no telemetry, no cloud account, no phone-home. The web desktop speaks HTTPS with a certificate from the box's own certificate authority. This page covers transport, open ports, physical access, SSH, two-factor sign-in, passkeys, the recovery key and the few connections the box makes on its own.

## HTTPS and certificates

The box serves the desktop and WebDAV on `https://<box-address>:7484`, over TLS 1.2 or 1.3 with HTTP/2. Ports 80 and 443 redirect there, except for the domains of the [reverse proxy](/docs/admin/reverse-proxy/), and a request with plain `http://` on 7484 is redirected to `https://` on the same port. The box is its own certificate authority, so a browser warns about its certificate until the device trusts the box's root: download it in Settings > **Certificates** and install it once per device (see [Certificates](/docs/admin/certificates/)). FTP over TLS presents the same certificate.

The authority may sign only local names and private addresses unless an administrator creates one without that limit, so a device that trusts it never accepts a certificate from the box for a public website. The box sends no HSTS header: browsers do not let anyone pass a certificate warning on an HSTS host, and the box's certificate is untrusted until a device trusts its root.

The session cookie is `HttpOnly`, `SameSite=Strict` and `Secure`. State-changing requests need the header `X-Pithyx-Request`. SMB 3 and SFTP encrypt their own traffic. The live system of the installer is the one exception: it speaks plain HTTP, because it has no certificate yet and asks for no password.

## Open ports

Pithyx has a firewall that lets in only the web interface and the file services that are switched on, over IPv4 and IPv6: the web desktop and WebDAV on 7484, 80 and 443 for the redirect and the reverse proxy, the ports of enabled reverse proxy streams, SMB on 445 (on from the first start), SFTP and SSH on 22, FTP on 21 with the passive ports 50000 to 50099. Switching a service off in Settings > **File services** also closes its port. Everything else is dropped, except answers to the box's own connections, DHCP replies and the usual ICMP messages (ping among them) and those IPv6 needs. App backends publish no ports at all.

With **Only private networks may use the file services** (section **Firewall** on the same page, off by default), SMB, SFTP, SSH and FTP answer only computers in private networks (10.x, 172.16 to 31.x, 192.168.x, link-local and IPv6 ULA); the web interface stays reachable either way. Leave it off if you reach the box through a router from another network or over a VPN with public addresses. If the rules cannot be applied, the setting is still saved and the error is [`network.firewall.failed`](/docs/reference/errors/#network.firewall.failed). Pithyx offers no remote access of its own; keep the box behind a router and reach it from outside over a VPN rather than forwarding these ports.

## Physical access

Whoever can use the box's console, in person or through remote management (BMC, IPMI), can get root: the boot menu has no password, and the system disk is not encrypted. A password at the console would therefore protect nothing, so the console menu and the [recovery menu](/docs/install/updates/#the-recovery-menu) ask for none and only ask you to confirm what you do. The box has no login at the console at all: tty1 shows only the console menu, the other consoles are switched off, and root stays locked. Put the box where strangers cannot reach it, and secure its remote management.

Each box writes its own ZFS host id during the installation, so two boxes installed from the same stick never claim to be the same system. A pool from before a reinstall therefore counts as used by another system and needs the forced import (see [Recovery](/docs/install/recovery/#importing-the-pools-again)).

Pool encryption is not available in this version; the storage wizard says it comes with the key vault. A key kept in a vault on the system disk protects only against stolen disks, not against someone who has the whole box or its console.

## SSH

SSH is off by default. Two separate switches on the **File services** page:

- **Offer shared folders over SFTP**: file transfer only, no shell.
- **SSH logins** with the switch **Let users with the SSH login right sign in with a shell**. Users with the right "Sign in over SSH with a shell" get a shell with their own rights, with password or public key, without forwarding, tunnels or X11. A shell reaches the whole system with the user's rights, so give the right only to people who need it.

Switching either off never stops SSH for the system's own accounts; Pithyx never stops `sshd`. A shell sign-in greets with the Pithyx logo, the version and a warning that changes made by hand in the shell can break the box or be undone by Pithyx or an update (in English for now). The host keys are made on the first boot and are part of the configuration backup.

The administrator you created in the installer is in the group `sudo` and gets root with `sudo` and its own password (it needs to have signed in to the web interface once since the installation, so the system knows the password). Other administrators get no `sudo`. Root itself stays locked; what you do with it is up to you. `sudo`, `nano`, `htop`, `curl`, `wget` and `git` are installed.

## Two-factor sign-in

Every user can turn on two-factor sign-in (TOTP) in the account menu under **Two-factor authentication**: scan the QR code or enter the key, enter a code, choose **Turn on**, and keep the 10 recovery codes shown once; each works once. The codes use SHA-1, 6 digits and 30 seconds. At sign-in, enter the code or choose "Use a recovery code instead"; the challenge is valid for 5 minutes. Turning it off needs the password. After 5 failed attempts for one user from one address, or 30 from one address, in 5 minutes, further attempts are refused for a while. Sessions end after 7 days without use or 30 days in total.

SMB, SFTP, WebDAV and FTP use the password only.

## Passkeys

Every user can add passkeys in the account menu under **Passkeys** ([Your account](/docs/user/account/#passkeys)). A passkey replaces both the password and the second factor at the desktop sign-in; file services keep the password. Pithyx asks for discoverable credentials with user verification required (the device's PIN, fingerprint or face) and no attestation. It never asks a metadata service, so nothing leaves the box. Adding one needs the current password; each user has up to 20.

A passkey is bound to the name the box was opened under (the WebAuthn relying party), so it works only there and only over HTTPS on a device that trusts the box; the box refuses passkeys for an address with `auth.passkey.unavailable`. Each sign-in uses a one-time challenge valid for 5 minutes and checks the signature counter. Failed passkey sign-ins count towards the limit of 30 per address in 5 minutes. A deactivated account or one without the desktop sign-in right is refused after the passkey check, as with the password. Resetting a password with the recovery key removes that administrator's passkeys. Passkeys are part of the configuration backup (part **Users and groups**).

## The recovery key

First setup shows the recovery key once (`PTHX-` followed by 26 characters). It is the only way to reset the administrator password. To use it, choose "Forgot password?" on the sign-in page, then enter the key, the user name of an administrator and a new password. This signs that user out everywhere, turns off two-factor sign-in and removes the passkeys of that user and issues a new recovery key; the old one stops working. There is one key for the whole box and it is part of the configuration backup. Without the key and the password, the system has to be reinstalled. Other users ask an administrator for a new password.

## What leaves the box

The box sends no data to the Pithyx project. These are the outbound connections Pithyx itself makes:

- **System updates**: once a day at a random time chosen by each box (switch it off with **Check for updates daily** in Settings > **Updates**), and on **Check now**, the box reads the channel file of its update source (by default the project's releases on GitHub) and Debian's package lists from `deb.debian.org`. An update you install downloads its file from the same source. See [System updates](/docs/install/updates/).
- **App catalogs**: git over HTTPS or SSH to each enabled catalog repository (the official and the community catalog are on and fetched from GitHub by default), once a day at the set time with a random delay of up to an hour, when you add a catalog, and on **Check now**. Switch a catalog off to stop it.
- **Container images** of catalog apps from the registry the catalog names, unless the package carries them.
- **Time**: with **Time servers** in Settings > **Regional settings** empty, the box uses the Debian defaults through systemd-timesyncd, which contacts public time servers.
- **Notification channels**: the email server and the webhook addresses set up as [notification channels](/docs/admin/notifications/), when there is something to send or on **Send test**.
- **Let's Encrypt**: the ACME authority of reverse proxy hosts with a certificate from it, to get and renew the certificate ([Reverse proxy](/docs/admin/reverse-proxy/#lets-encrypt)).
- **UPS**: the NUT server of a UPS in the network, every 5 seconds ([UPS](/docs/admin/ups/)).
- **Apps** that you allow the permission "Internet" get a second network with outbound access to the internet; other apps have none. Devices in your local network are reachable only for apps with the permission "Local network".

Logs stay on the box under `/var/log/pithyx/`. Secrets are masked in logs. Passwords are stored as Argon2id hashes.
