---
title: 'Home folders'
description: 'Every user gets a private home folder on the home pool; administrators pick the pool, set quotas and decide what happens to the home of a deleted user.'
status: draft
audience: admin
sidebar:
  order: 5
---

Every user gets a home folder of their own: a private place for files that only that user and the administrators can open. Users see it as **home** in Files and over every file service; administrators also see every other home below **homes**. Settings > **Home folders** shows on which pool the homes are, their space and quotas, and the homes of deleted users.

## Where users find it

| Where          | The user's own home            | Every home (administrators only)                             |
| -------------- | ------------------------------ | ------------------------------------------------------------ |
| Files          | **home**, first in the sidebar | **homes** in the sidebar, which lists every home as a folder |
| SMB            | `\\<box-address>\home`         | `\\<box-address>\homes`                                      |
| SFTP           | The folder `home`              | The folder `homes`, one folder per user                      |
| WebDAV and FTP | The folder `home` at the top   | The folder `homes`, one folder per user                      |

A home is used like a shared folder: it has its own [recycle bin](/docs/user/files/#the-recycle-bin), files created there belong to the user, and the user needs the right for a protocol to reach it over that protocol (see [File services](/docs/admin/file-services/)). Nobody else can be given access to a home, and it never appears in Settings > **Shared folders**. Apps that may read the user's shared folders see the user's own home as well, never anybody else's.

:::caution
Files deleted in the SMB share `homes` do not go to a recycle bin; they are gone. Each home is a dataset of its own, and Samba cannot move a file across them. Over `home`, in Files, SFTP, WebDAV and FTP the recycle bin works as usual.
:::

## The home pool

Each home is a dataset `<pool>/pithyx/homes/<user>`, mounted at `/<pool>/homes/<user>`. When the first pool with the Pithyx layout exists, it becomes the home pool, and every user without a home gets one there; a user created before that gets the home as soon as the pool is there. Users created later get theirs at once. The **Users** page shows each home's path, or "No pool yet".

To move every home to another pool, choose the pool under **Move to** and **Move home folders**. A pool is offered only while it is online, writable, has the Pithyx layout and is unlocked; it also needs room for all homes ([`homes.pool.no_space`](/docs/reference/errors/#homes.pool.no_space)). The move runs as a job, one home after the other:

1. The home turns read-only, so it can be read but not changed while it is copied.
2. A snapshot of it is copied to the new pool with its files, owners, rights and recycle bin.
3. The home is used from the new pool, and the old copy is deleted with its snapshots.

If one home cannot be moved, it stays where it was and can be changed again; the homes moved before stay moved, and the home pool stays the old one until every home has arrived. Start the move again once the cause is fixed. Only one move runs at a time ([`homes.move.running`](/docs/reference/errors/#homes.move.running)).

A home on a pool that is gone (destroyed, or exported and not imported again) cannot be moved; the move leaves it where it is. If the pool will not come back, **Delete** the home on this page: the user gets a new, empty home on the home pool.

## Quotas

**Default quota** applies to every home without its own quota; leave it empty for no limit. **Quota** on a home sets the user's own: **Use the default quota**, **No limit** or **Own quota**. A quota is at least 1 MiB ([`homes.quota.invalid`](/docs/reference/errors/#homes.quota.invalid)) and counts the files and the snapshots of the home. When a home is full, writing to it fails until files are deleted and the recycle bin is emptied.

## When a user is deleted

Deleting a user in Settings > **Users** asks what happens to the home folder:

- **Keep it** (the default): the home stays, renamed to `<name>.deleted-<user id>`, so the name is free for a new user. Administrators see it below **homes**.
- **Move its files into a shared folder**: a new folder named after the user in the shared folder you choose gets every file, then the home is deleted with its recycle bin. This runs as a job.
- **Delete it with every file**: the home goes with its files, recycle bin and snapshots, after you typed the user name.

A kept home appears on the **Home folders** page with "user deleted". **Move into shared folder** and **Delete** do the same as above later; **Delete** asks you to type the folder name. A new user with the same name never gets an old home: a home that belongs to another user id is renamed out of the way first.

## Limits

- Homes have no `Desktop` folder for the desktop items yet.
- A home is never shared with other users; use a shared folder for that.
