---
title: 'Reverse proxy'
description: 'Proxy hosts and redirect hosts on ports 443 and 80 by domain, with certificates, access rules and the Pithyx sign-in, and TCP and UDP streams.'
status: ready
audience: admin
since: '1.0.0'
sidebar:
  order: 10
---

The built-in reverse proxy makes other services reachable under their own domains on ports 443 and 80: a request for `media.home.arpa` goes to the service behind it, with a certificate for that name. The box itself stays on port 7484; every name you did not set up goes there as before (or gets 404, if you choose so). Streams forward a TCP or UDP port of the box to another host. Everything is in Settings > **Reverse proxy** (administrators only), applies to the next request without a restart and is part of the configuration backup.

## Before you start

Point each domain at the box in your DNS server or router (an A or AAAA record with the box's address, or a wildcard record for `*.apps.home.arpa`). The proxy only answers names that reach it. Without HTTPS on the box (development or simulation) it answers plain HTTP only, and a development daemon listens on the proxy ports only when `PITHYX_DEV_PROXY_PORTS` names them.

## Proxy hosts

**Add proxy host** opens the settings of a host:

- **Domains**: up to 50 names, separated by commas or spaces. A leading `*.` covers any one label in front (`*.apps.home.arpa` answers `photos.apps.home.arpa`, not `a.b.apps.home.arpa`); an exact name wins over a wildcard. A name belongs to one host only ([`proxy.domain.taken`](/docs/reference/errors/#proxy.domain.taken)). A switched-off host answers nothing; its names count as unknown.
- **Target**: protocol, host and port of the service. For `https` choose how its certificate is checked: against the trusted authorities of the system, against the box's own authority (devices with a certificate from Settings > **Certificates**), or not at all for a self-signed certificate.
- **The target is a service on this box itself**: needed for a target such as `localhost`, the box's name or one of its addresses ([`proxy.target.on_box`](/docs/reference/errors/#proxy.target.on_box)), for example to reach the web interface on 7484 under a domain. Ports 80 and 443 of the box are always refused, because the request would come back to the proxy ([`proxy.target.loop`](/docs/reference/errors/#proxy.target.loop)).
- **Pass WebSockets on**: on by default; switched off, upgrades get 403 ([`proxy.websockets.disabled`](/docs/reference/errors/#proxy.websockets.disabled)).

Answers stream through without buffering, so server-sent events and large downloads reach the browser as they come. Targets are spoken to with HTTP/1.1.

### HTTPS

- **Certificate**: **From the box's certificate authority** makes a certificate for the host's domains in memory (397 days, renewed 30 days before its end and whenever the authority changes). Every domain must lie inside the authority's scope ([`tls.name.not_permitted`](/docs/reference/errors/#tls.name.not_permitted)); add your own domain to the scope with a new authority in Settings > **Certificates**. **Upload my own** takes a certificate with its chain and unencrypted key in PEM that covers every domain ([`proxy.certificate.name_mismatch`](/docs/reference/errors/#proxy.certificate.name_mismatch)), for example one for a public domain. An expired or broken upload is replaced by the box's own certificate, and the host shows **No certificate** when it has none of its own.
- **From Let's Encrypt** gets a free certificate that every device trusts, for public domains only; see [Let's Encrypt](#lets-encrypt).
- The box picks the certificate by the name the browser asks for in the TLS handshake, on 443 and on 7484; a name without a host gets the box's own certificate.
- **Redirect HTTP to HTTPS** (on by default) answers plain HTTP on 80 with a redirect to 443 (301 for GET and HEAD, 308 otherwise). Switched off, the host also answers on 80.
- **Offer HTTP/2** (on by default) lets browsers use HTTP/2 on 443.
- **Send HSTS** adds `Strict-Transport-Security: max-age=31536000` to HTTPS answers.

:::caution[HSTS]
With HSTS a browser refuses plain HTTP and every certificate warning for the name for a year, even after you switch it off again. Use it only with a certificate every device trusts.
:::

### Access

The proxy checks, in this order: the allowed and denied addresses, Basic Auth, then the Pithyx sign-in.

- **Allowed addresses** and **Denied addresses**: IP addresses or ranges such as `192.168.1.0/24`. A denied address never gets in; with allowed addresses, only those do ([`proxy.address.denied`](/docs/reference/errors/#proxy.address.denied)). The box sees the address the connection comes from.
- **Basic Auth**: users with a password the browser asks for before anything else. Passwords are stored as Argon2id hashes and never shown; leave the field empty to keep a stored password. Failed attempts count towards the same limits as failed sign-ins. The target does not get the `Authorization` header.
- **Require Pithyx sign-in**: a page of the host sends a visitor without access to the box's sign-in, by default on the requested name and port 7484 (or the **Sign-in address** of the page). After signing in there, the browser comes back with a one-time ticket (30 seconds) to `/.pithyx/auth` on the host, which sets a cookie of the host and continues to the page. Requests that are no page load get 401 ([`proxy.auth.required`](/docs/reference/errors/#proxy.auth.required)). The cookie belongs to the box session: signing out of the box, revoking the session, losing the right to sign in to the desktop or being removed from the host's users and groups ends the access at the next request. **Users** and **Groups** limit who gets in ([`proxy.access.denied`](/docs/reference/errors/#proxy.access.denied)); with neither, everybody who may sign in does, and administrators always do. The target gets the user in `X-Pithyx-User`, `X-Pithyx-User-Id`, `X-Pithyx-Groups` and `X-Pithyx-Admin`. The path `/.pithyx/auth` of such a host belongs to the proxy. Users and groups are stored by name: after renaming a group, update the host.

:::note
The sign-in happens on the box's web interface. If you reach a host from outside through 443 only, set **Sign-in address** to a host that leads to the box's web interface, such as a proxy host for `nas.example.org` with the target `https://localhost:7484`.
:::

### Headers

- The target never gets incoming `X-Pithyx-*`, `X-Forwarded-*`, `Forwarded` or trace headers, nor the box's own cookies (`pithyx_*`); an answer cannot set those cookies either.
- **Send X-Forwarded-For, -Proto and -Host** (on by default) tells the target the client's address, the protocol and the requested name.
- **Send the requested name as Host** passes the browser's `Host` on; otherwise the target gets its own host and port. **Host header** sets a `Host` of your own instead.
- **Request headers** and **Response headers**: one per line as `Name: value`; `Name:` alone removes the header. Headers the proxy controls itself (`Host`, `Connection`, `X-Forwarded-*`, `X-Pithyx-*`, `Strict-Transport-Security` and others) are refused ([`proxy.header.invalid`](/docs/reference/errors/#proxy.header.invalid)).

### Limits and logging

- **Largest upload in MB**: empty for no limit.
- **Connect timeout** (1 to 300 seconds, default 10) and **Answer timeout** (1 to 86400 seconds, default 100: how long the target may stay silent). An idle WebSocket stays open for up to a day. A target that cannot be reached gets 502 ([`proxy.target.unavailable`](/docs/reference/errors/#proxy.target.unavailable)), one that is too slow 504 ([`proxy.target.timeout`](/docs/reference/errors/#proxy.target.timeout)).
- **Write an access log** (on by default): every request in the Log Center under the source `proxy/<first domain>`, with method, name, path (without the query, which may hold secrets), status, duration, client address and, behind a sign-in, the user.

The list shows for each host whether its target answered when the page opened; **Check target** looks again.

### Let's Encrypt

A proxy host or redirect host with the certificate **From Let's Encrypt** gets one certificate for all its domains from Let's Encrypt (or another ACME authority) and renews it by itself a third of its lifetime before the end, 30 days for the usual 90. The box talks to the authority only for these hosts.

1. In the section **Let's Encrypt** of Settings > **Reverse proxy**, read the **Terms of service**, agree to them and save. An email address is optional; the authority may write to it about the account. Another authority goes into **ACME directory** (an `https` address) and needs its own agreement.
2. Point every domain of the host at the box's public address in DNS, and forward port 80 (and 443 for the visitors) of your router to the box. The authority checks each name over port 80 (HTTP-01); the box answers that check itself, for every host.
3. Create or edit the host with the certificate **From Let's Encrypt**. The first request starts at once.

The host shows where its certificate stands: waiting for the first one, valid until a date, or why the last request failed with the authority's own words, such as [`proxy.acme.dns`](/docs/reference/errors/#proxy.acme.dns) (a name has no address), [`proxy.acme.connection`](/docs/reference/errors/#proxy.acme.connection) (port 80 does not reach the box from the internet), [`proxy.acme.unauthorized`](/docs/reference/errors/#proxy.acme.unauthorized), [`proxy.acme.caa`](/docs/reference/errors/#proxy.acme.caa) or [`proxy.acme.rate_limited`](/docs/reference/errors/#proxy.acme.rate_limited). After a failure the box tries again after one hour, then waits twice as long each time up to a day; **Renew now** asks at once. Until the first certificate arrives, or when one could not be renewed, the host presents the box's own certificate. A failed renewal notifies the administrators.

Wildcards ([`proxy.acme.wildcard`](/docs/reference/errors/#proxy.acme.wildcard)) and local names such as `nas.home.arpa`, `.local`, names without a dot or IP addresses ([`proxy.acme.name_not_public`](/docs/reference/errors/#proxy.acme.name_not_public)) cannot get a certificate from a public authority; use the box's own authority for those. Without agreed terms a host refuses the choice with [`proxy.acme.terms_required`](/docs/reference/errors/#proxy.acme.terms_required), and without the plain proxy port 80 with [`proxy.acme.port_unavailable`](/docs/reference/errors/#proxy.acme.port_unavailable).

## Redirect hosts

A redirect host sends browsers from its domains to another address with 301, 302, 307 or 308. With **Keep path and query** a request for `/a?b=1` goes to the target address followed by `/a?b=1`. It presents a certificate on 443 like a proxy host.

## Names without a host

**Names without a host** decides what 443 and 80 do with a name no enabled host has: **Redirect to the web interface on port 7484** (the default, as without a reverse proxy) or **Answer “not found” (404)** ([`proxy.host.unknown`](/docs/reference/errors/#proxy.host.unknown)). The installer's check `/api/install-id` on port 80 always answers.

## Streams

A stream forwards a port of the box, TCP, UDP or both, byte for byte to a host and port, for services that do not speak HTTP. The firewall opens the port while the stream is on. The box's own ports are refused (21, 22, 80, 443, 445, 7484 and FTP's passive ports 50000 to 50099, [`proxy.stream.port_reserved`](/docs/reference/errors/#proxy.stream.port_reserved)), so are ports another program holds ([`proxy.stream.port_in_use`](/docs/reference/errors/#proxy.stream.port_in_use)) and ports below 1024 unless you confirm them. A UDP client keeps its own connection to the target until it is silent for a minute; a stream takes up to 512 TCP connections and 1024 UDP clients at a time.

## Security

The proxy runs inside the daemon, which runs as root, and only administrators may set targets. Requests the proxy itself sent are recognized and refused with 508 ([`proxy.loop.detected`](/docs/reference/errors/#proxy.loop.detected)). The settings are in the configuration backup as the part **Reverse proxy**; uploaded certificates and those from Let's Encrypt come with the part **Certificates** (files `proxy-host-<id>.pem`, `proxy-host-<id>-acme.pem` (`proxy-redirect-<id>-acme.pem`) and their `.key` next to the box's own, with the account key `acme-account-<hash>.key`).

## Not built yet

Load balancing with health checks, rules per path, rate limits and certificates over DNS-01 (needed for wildcards) come later.
