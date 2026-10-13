---
title: 'Users and groups'
description: 'Create users and groups, give them rights, deactivate or delete accounts, and what happens to their home folders.'
status: ready
audience: admin
sidebar:
  order: 5
---

Administrators manage accounts in Settings > **Users** and Settings > **Groups**. Every Pithyx user is also a Linux account with an own user id, so files they create belong to them. Passwords have at least 8 characters, and only an administrator can set a new password for another user.

## Creating a user

Choose **Create user** and fill in:

- **User name**: lowercase letters, digits, hyphens and underscores, starting with a letter or underscore, at most 32 characters. System names such as `root`, `daemon`, `sshd`, `pithyx`, `docker` and `users`, and names starting with `systemd-`, are refused ([`auth.username.reserved`](/docs/reference/errors/#auth.username.reserved)). A name a system account may already use is refused with [`users.account.refused`](/docs/reference/errors/#users.account.refused).
- **Display name (optional)**: at most 64 characters.
- **Password**: 8 to 1024 characters. Well-known passwords are refused ([`auth.password.too_common`](/docs/reference/errors/#auth.password.too_common)); there are no other composition rules.
- **Groups**. Every user is also in the group users.

User ids come from 20000 to 29999 and are never given out again. Group ids come from 30000 to 39999. The shell is `/usr/sbin/nologin` unless the user has the SSH login right and SSH logins are switched on. A new user can sign in to the desktop, use SMB, and use the apps Files and Viewer; you can change this in the rights dialog.

The password is also set for the Linux and Samba accounts. A user who does not have one yet is marked "Not ready for SMB and SFTP yet: the user signs in once on the web, or you set a new password." Badges in the list: Administrator, Two-factor, Deactivated.

## Groups

Choose **Create group** in Settings > **Groups**. A group has a name (same rules as user names, and it may not start with `pithyx-`), a **Description (optional)** of up to 200 characters, and members. Rights given to a group apply to all its members. Deleting a group keeps its members; files keep the group id, and it is never given to another group. The built-in group users contains every user and cannot be renamed, deleted or given members by hand ([`users.group.builtin`](/docs/reference/errors/#users.group.builtin)).

## Special rights

The rights dialog of a user or group has four special rights: "Administrator (may change everything)", "Sign in to the desktop", "Sign in over SSH with a shell" and "Restart and shut down". Further sections cover file services, apps and shared folders. How rights combine is on [Permissions](/docs/admin/permissions/). Without "Sign in to the desktop" a user cannot sign in to the web desktop; a user who loses it is signed out with the next request.

## Deactivating and deleting

**Deactivate** gives the user no rights at all and signs them out; **Activate** gives the rights back. You cannot deactivate yourself ([`users.user.self`](/docs/reference/errors/#users.user.self)), and the last administrator can neither be deactivated, deleted nor lose the right ([`users.user.last_admin`](/docs/reference/errors/#users.user.last_admin)).

**Delete** asks you to type the user name and what happens to the user's home folder: **Keep it** (the default), **Move its files into a shared folder** or **Delete it with every file**, see [Home folders](/docs/admin/home-folders/#when-a-user-is-deleted). The account goes, files the user created in shared folders stay, and the user id is never given to anyone else. Grants, group memberships and the user's jobs and notifications are removed.

To reset a password, use **New password** on the user: the user is signed out everywhere, and SMB and SFTP use the new password as well. Pithyx has no reset by email. If the only administrator forgot the password, the recovery key from first setup is the only way, see [Security](/docs/admin/security/#the-recovery-key).

## Home folders

Each user gets a private home folder, a dataset `<home pool>/pithyx/homes/<user>` at `/<pool>/homes/<user>`, owned by the user. The list shows its path, or "No pool yet" until a pool with the Pithyx layout exists. Its pool, quotas and the homes of deleted users are on [Home folders](/docs/admin/home-folders/).
