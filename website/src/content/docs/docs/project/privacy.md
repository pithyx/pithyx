---
title: 'Privacy'
description: 'Pithyx and this website collect nothing: no telemetry, no accounts, no trackers.'
status: ready
sidebar:
  order: 2
---

Pithyx is built to run entirely on your own machine: it has no telemetry, does not phone home, needs no cloud account and depends on no service run by the project. Logs stay on the box. This website has no analytics and loads nothing from third parties.

## The box

- No telemetry, no usage statistics and no account with the project. Logs are written to the box only (see [Logs and monitoring](/docs/admin/logs-and-monitoring/)). The daemon's service turns off the .NET command line telemetry.
- Pithyx is installed fresh; it does not import data from other systems.
- Pithyx itself makes these outgoing connections:
  - **System updates.** Once a day the box reads the channel file of its update source (by default the project's releases on GitHub) and Debian's package lists; an update you install downloads its file from there.
  - **Catalogs.** The Store fetches each enabled catalog with Git (by default the project's official catalog on GitHub, checked daily) and pulls app images from the registry named in the catalog.
  - **Time servers.** If the list of time servers in Settings > **Regional settings** is empty, the system time service (systemd-timesyncd) uses its Debian defaults, which are public servers.
  - **Notifications.** Only if an administrator or user sets them up: the email server and the webhook addresses of the [notification channels](/docs/admin/notifications/).
  - **Let's Encrypt.** Only for a reverse proxy host with a certificate from Let's Encrypt (or another ACME authority you name): the box talks to the authority to get and renew it.
  - **UPS.** Only with a UPS of another NUT server: the box reads it from that server.
  - **Apps.** An app with the permission `network.internet` can reach the internet; apps without it cannot. Other devices in your network are reachable only for an app with the permission `network.lan`.
- An administrator can switch the daily update check and catalogs off and set own time servers. See [What leaves the box](/docs/admin/security/#what-leaves-the-box) and [Apps and the Store](/docs/admin/apps-and-store/).

## This website

The website has no analytics and no tracking scripts, and loads its fonts and all other files from its own host: there is no external font service and no CDN.

## Support bundles

Pithyx does not have support bundles yet.
