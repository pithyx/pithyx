---
title: 'Certificates'
description: 'HTTPS on 7484, the box as its own certificate authority, trusting it on your devices, certificates for other devices and a certificate of your own.'
status: ready
audience: admin
since: '1.0.0'
sidebar:
  order: 10
---

The box speaks HTTPS on port 7484 with a certificate from its own certificate authority; ports 80 and 443 redirect there, except for the domains of the [reverse proxy](/docs/admin/reverse-proxy/), which present certificates of their own. Install the authority's root once on each device and the browser trusts the box, its reloads come from the cache, passkeys work and FTPS clients accept it. Everything on this page is in Settings > **Certificates** (administrators only).

## Why the browser warns

The first time you open `https://<box-address>:7484`, the browser warns that the connection is not private: it does not know who signed the box's certificate. That is expected. Continue to the page, sign in and trust the box as described below; the warning is gone on that device afterwards. Until then, Chromium-based browsers keep nothing from the box in their cache: every reload loads the desktop again, and every app window loads its app again.

## Trusting the box on a device

In **Trust this box**, the page first says whether the device you are using trusts the box. Choose **Download root certificate** (the file `pithyx-root-ca.crt`; anyone may download it, also before signing in, from `/api/system/tls/root-ca.crt`). Before you trust it, compare its SHA-256 fingerprint with the one the page shows. Then:

- **Windows**: open the file, choose **Install Certificate**, **Local Machine**, then the store **Trusted Root Certification Authorities**.
- **macOS**: open the file in Keychain Access (keychain **System**), open the certificate and set **Trust** to **Always Trust**.
- **iPhone and iPad**: open the file in Safari, install the profile under Settings, General, VPN and Device Management, then switch it on under Settings, General, About, Certificate Trust Settings.
- **Android**: Settings, Security, Encryption and credentials, Install a certificate, CA certificate, then pick the file.
- **Linux**: copy the file to `/usr/local/share/ca-certificates/` and run `sudo update-ca-certificates`.
- **Firefox** keeps its own store: Settings, Privacy and Security, Certificates, View Certificates, Authorities, Import, and trust it to identify websites.

Afterwards restart the browser, open the box again by its name and choose **Check again**. Every user sees the same steps without the fingerprint: after signing in on a device that does not trust the box, the desktop shows a hint with **Trust this device**, and **Personal settings** has the row **Trust this device** ([Your account](/docs/user/account/#trusting-the-box-on-this-device)).

The box tells a trusting device from another by registering its service worker: browsers refuse service workers on a connection with a certificate warning. Once the device trusts the box, the worker runs and keeps the desktop's files of the current version in the browser, and [passkeys](/docs/user/account/#passkeys) can be used. Over plain HTTP, such as the installer's live system, nothing is checked and no hint appears.

## The certificate authority

The authority has two parts: a root (ECDSA P-384, 20 years) that devices trust, and an intermediate (ECDSA P-256, 5 years) that signs every certificate. The intermediate renews itself a year before it ends; devices notice nothing, because they trust the root.

The first authority is limited by Name Constraints to local names and private addresses: the suffixes `local`, `lan`, `home.arpa`, `internal` and `localhost`, the box's host name, the network's search domains, and the address ranges 10.0.0.0/8, 172.16.0.0/12, 192.168.0.0/16, 100.64.0.0/10, 169.254.0.0/16, 127.0.0.0/8, fc00::/7, fe80::/10 and ::1. A device that trusts the root accepts nothing else from it, so even someone who took over the box could not forge a certificate for a public website. Both the root and the intermediate carry the limit.

**Create new authority…** replaces root and intermediate. With **Only local names and private addresses** (the default) you can add domains of your own, such as `home.example.org`; each covers every name below it. Without the limit the authority may sign any name, and a device that trusts it would accept a forged certificate for any website if the box were compromised. After a new authority every device has to trust the new root again; certificates issued for other devices stay valid only where the old root is still trusted, and the list marks them **Earlier authority**. Renaming the box also needs a new authority if you reach it by its bare host name, because the old host name is part of the limit.

## The box's own certificate

The box's certificate covers its host name, `<host>.local`, the host name in each search domain, `localhost`, every address of its network cards (not those of app containers) and the loopback addresses, as far as the authority may sign them. It lives 397 days, renews itself 30 days before it ends and is issued again at once when an address or the host name changes. **Names of the box's certificate** shows what it covers; **More names** adds names such as a domain that points at the box, which must lie inside the authority's limit ([`tls.name.not_permitted`](/docs/reference/errors/#tls.name.not_permitted) otherwise).

## Certificates for other devices

**Issue certificate…** makes a server certificate for a printer, router or another server in your network: names and addresses (a leading `*.` for a wildcard), 1 to 825 days, and an ECDSA key or, for devices that need it, RSA with 2048 bits. The browser downloads the certificate with the intermediate (`.crt`) and the private key (`.key`). The box does not keep the key. The list lets you download the certificate again, without the key, and remove an entry; the box keeps no revocation list, so a removed certificate stays valid until it ends.

## A certificate of your own

**Your own certificate** takes a certificate you got elsewhere, for example for your own domain: the certificate with its intermediates and the private key, both in PEM and the key without a password. The box presents it on 7484, 443 and for FTPS while it is valid, and its own certificate again once it has ended or after **Remove own certificate**. The upload is refused with [`tls.custom.invalid`](/docs/reference/errors/#tls.custom.invalid), [`tls.custom.key_mismatch`](/docs/reference/errors/#tls.custom.key_mismatch) or [`tls.custom.expired`](/docs/reference/errors/#tls.custom.expired). Let's Encrypt is not built in yet.

## Behind a reverse proxy

To put other services behind the box, use its own [reverse proxy](/docs/admin/reverse-proxy/). A reverse proxy elsewhere in front of the box connects to `https://<box-address>:7484`. Let it trust the box's root (or, less strictly, skip the check for this one backend), pass WebSocket upgrades through for `/ws` and app sockets, and send its own requests with the host name the browser used. The box sees the proxy's address as the client, so the limit of failed sign-ins per address applies to everyone behind the proxy together.

## Backup

The root, the intermediate, the box's certificate, an uploaded certificate, the extra names and the list of issued certificates are part of the configuration backup (part **Certificates**). Restoring them on a new system brings the same authority back, so devices keep trusting it; the box issues its own certificate again for the new system's names.
