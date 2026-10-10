---
title: 'Security'
description: 'Transport, SSH, two-factor sign-in, the recovery key and what leaves the box.'
status: draft
audience: admin
sidebar:
  order: 9
---

Pithyx keeps everything on the box: no telemetry, no cloud account, no phone-home. The one thing to know before you use it: the web desktop is served over plain HTTP today. This page covers transport, SSH, two-factor sign-in, the recovery key and the few connections the box makes on its own.

## HTTPS and certificates

:::caution[Plain HTTP]
The box serves the desktop on `http://<box-address>:7484`, and port 80 redirects there. HTTPS is not built, so the sign-in password, the session cookie and WebDAV Basic authentication travel unencrypted. Use the box only on a network you trust.
:::

The session cookie is `HttpOnly` and `SameSite=Strict`, and it gets the `Secure` flag only when a request arrives over HTTPS. State-changing requests need the header `X-Pithyx-Request`. The only TLS the box has today is the self-signed certificate of FTP over TLS (see [File services](/docs/admin/file-services/#ftp)). SMB 3 and SFTP encrypt their own traffic.

## SSH

SSH is off by default. Two separate switches on the **File services** page:

- **Offer shared folders over SFTP**: file transfer only, no shell.
- **SSH logins** with the switch **Let users with the SSH login right sign in with a shell**. Users with the right "Sign in over SSH with a shell" get a shell with their own rights, with password or public key, without forwarding, tunnels or X11. A shell reaches the whole system with the user's rights, so give the right only to people who need it.

Switching either off never stops SSH for the system's own accounts; Pithyx never stops `sshd`. The host keys are made on the first boot and are part of the configuration backup.

## Two-factor sign-in

Every user can turn on two-factor sign-in (TOTP) in the account menu under **Two-factor authentication**: scan the QR code or enter the key, enter a code, choose **Turn on**, and keep the 10 recovery codes shown once; each works once. The codes use SHA-1, 6 digits and 30 seconds. At sign-in, enter the code or choose "Use a recovery code instead"; the challenge is valid for 5 minutes. Turning it off needs the password. After 5 failed attempts per user or 30 per address in 5 minutes, further attempts are refused for a while. Sessions end after 7 days without use or 30 days in total.

SMB, SFTP, WebDAV and FTP use the password only.

## The recovery key

First setup shows the recovery key once (`PTHX-` followed by 26 characters). It is the only way to reset the administrator password. To use it, choose "Forgot password?" on the sign-in page, then enter the key, the user name of an administrator and a new password. This signs that user out everywhere, turns off two-factor sign-in for that user and issues a new recovery key; the old one stops working. There is one key for the whole box and it is part of the configuration backup. Without the key and the password, the system has to be reinstalled. Other users ask an administrator for a new password.

## What leaves the box

The box sends nothing to the Pithyx project. These are the outbound connections Pithyx itself makes:

- **App catalogs**: git over HTTPS or SSH to each enabled catalog repository (the official catalog is on and fetched from GitHub by default; the community catalog is off), once a day at the set time with a random delay of up to an hour, when you add a catalog, and on **Check now**. Switch a catalog off to stop it.
- **Container images** of catalog apps from the registry the catalog names, unless the package carries them.
- **Time**: with **Time servers** in Settings > **Regional settings** empty, the box uses the Debian defaults through systemd-timesyncd, which contacts public time servers.
- **Apps** that you allow the permission "Internet" get a second network with outbound access; other apps have none.

Logs stay on the box under `/var/log/pithyx/`. Secrets are masked in logs. Passwords are stored as Argon2id hashes.
