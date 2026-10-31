---
title: 'Permissions'
description: 'How rights are given to users and groups, how shared folder, protocol and app access combine, and when a change takes effect.'
status: ready
audience: admin
sidebar:
  order: 6
---

Pithyx gives rights to users and to groups. A user's effective rights are the union of the user's own rights and those of all its groups, including the built-in group users. There are no deny rules, and administrators have every right. A deactivated user has none.

## The model

A right is one of:

- a special right: administrator, sign in to the desktop, sign in over SSH with a shell, restart and shut down;
- a protocol: SMB, SFTP, WebDAV or FTP;
- access to a shared folder: no access, read, or read and write (the highest wins);
- access to an app.

The rights dialog of a user or group says it plainly: the user has these rights and everything its groups give it; there are no rules that take a right away. A group never overrules a user, and you cannot deny what a group grants.

## Shared folder access

Set the levels **No access**, **Read** and **Read and write** per user or group in the rights dialog, or per folder in the folder's edit dialog, which also shows what a user "ends up with". Administrators always read and write every folder. Pithyx makes the file system agree: it creates Linux groups per folder and sets POSIX ACLs, so SMB, SFTP, WebDAV, FTP and the Files app see the same rights. ACL changes made from an SMB client are ignored. A folder without access is hidden, not refused ([`fs.share.not_found`](/docs/reference/errors/#fs.share.not_found)); writing with read access gives [`fs.share.read_only`](/docs/reference/errors/#fs.share.read_only).

## Protocol access

A user reaches a folder over a protocol only when both allow it: the user (or a group) has the protocol right, and the folder has the matching **Offer over ...** switch. A new user has SMB only. The server-side switches are on [File services](/docs/admin/file-services/). SSH with a shell is a right, not a protocol: it needs the right and the **SSH logins** switch.

Protocol sign-ins use the password only; two-factor sign-in applies to the web desktop.

## App access

Files and Viewer are given by right (new users get both), About is open to everybody, and Settings, Resource Monitor, Log Center and Store are for administrators only. For apps from the Store, **Who may use the app** is **Everybody**, **Administrators only** or **Selected groups and users**; administrators may use every app. Per app, administrators can turn permissions off to take them from the app. More on [apps and the Store](/docs/admin/apps-and-store/).

## When a change applies

Changes apply at once. Open sessions are re-authorized over the WebSocket, and a user who loses desktop sign-in is disconnected. For WebDAV and FTP the right is checked on every request. A correct protocol password is remembered for 5 minutes, and a new password ends that at once. Open windows of an app get a permission change within a few minutes. Web sessions end after 7 days without use or 30 days in total; deactivating, deleting or resetting the password ends them immediately.
