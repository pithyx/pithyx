---
title: 'UPS'
description: 'Shut the box down cleanly on a power failure with a UPS on USB or a UPS of another NUT server, and when it shuts down.'
status: ready
audience: admin
since: 1.2.0
sidebar:
  order: 13
---

Settings > **UPS** connects the box to an uninterruptible power supply. On a power failure the box notifies its administrators and shuts down cleanly before the battery runs out: apps and file services stop, and the pools are unmounted. Pithyx reads the UPS through NUT (Network UPS Tools), so it works with the UPS models NUT supports.

## Choosing the UPS

- **UPS on USB**: connect the UPS with its USB cable and choose this. The box runs its own NUT server only for itself (on `127.0.0.1`) with the `usbhid-ups` driver, which finds the UPS by itself. If the state says no UPS was found, check the cable, or whether the UPS needs another NUT driver (not selectable yet).
- **UPS of another NUT server**: another machine (a NAS, a router or a server with NUT) has the UPS. Enter its address, the port (3493 unless changed), the UPS's name from its `ups.conf` and a user of its `upsd.users` with `upsmon secondary`. The box signs in as a secondary, so that machine waits for the box before it cuts the power. Without a user the box only reads the UPS.

The password is never shown again; leave the field empty to keep it.

## When the box shuts down

The box reads the UPS every 5 seconds. On battery it shuts down as soon as one of these holds:

- the charge is at or below the **battery limit** (20 percent by default, 5 to 90),
- the runtime left is at or below the **runtime limit** (5 minutes by default, 1 to 60),
- the UPS says its battery is low,
- the NUT server's primary shuts every system down (forced shutdown),
- the UPS ran on battery and stopped answering for 15 seconds.

The administrators get a notification when the UPS goes on battery, when the power is back, before the shutdown, and when the UPS stays unreachable for a minute.

After the shutdown, a UPS on USB cuts its own power, so the box starts again once the power is back if its firmware is set to start when power returns. If the power comes back during the shutdown and the UPS keeps the power on, the box reboots after 10 minutes.

## Trying it without a UPS

NUT's `dummy-ups` driver plays a UPS from a file, so the settings can be tried with a NUT server on another Linux machine: a `[sim]` section with `driver = dummy-ups` and `port = sim.dev` in its `ups.conf`, where `sim.dev` holds lines such as `ups.status: OL` and `battery.charge: 100`. Changing them to `ups.status: OB` and a lower charge plays a power failure; below the limits the box really shuts down.
