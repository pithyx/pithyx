---
title: 'Your account'
description: 'Password, two-factor sign-in, how long you stay signed in, and personal settings such as language, theme and accent.'
status: draft
audience: user
sidebar:
  order: 5
---

Your account is your user name and password on the box. The same password signs you in to the desktop and to file services such as SMB and SFTP. You cannot change your own password yourself: an administrator sets a new one. You can turn on two-factor sign-in and choose language, theme, accent color and wallpaper.

## Changing the password

Pithyx has no way for users to change their own password and no reset by email. Ask an administrator to set a new one. The administrator does this in Settings > **Users** with **New password**. You are then signed out everywhere and sign in with the new password. File services use the new password too.

A password must have:

- at least 8 characters and at most 1024,
- no match on the built-in list of widely used passwords.

There are no rules about capital letters or symbols. A long passphrase of several words is easier to remember than a complex password. When an administrator types a new password, the page shows a strength meter (**Weak**, **Medium**, **Strong**) and hints. The meter only advises; a weak password that is long enough is accepted.

If you are an administrator and forgot your password, use the recovery key. See [Recovery and reinstall](/docs/install/recovery/).

## Two-factor sign-in

![The dialog Two-factor authentication with a QR code, the key to enter manually, the field Code and the button Turn on](../../../../assets/screenshots/account-two-factor-qr.webp)

_Scan the QR code with an authenticator app, then enter the code it shows._

![The dialog Your recovery codes with ten one-time codes and the button Copy codes](../../../../assets/screenshots/account-recovery-codes.webp)

_After the first code the dialog shows ten recovery codes, once. These belong to a deleted test machine._

Two-factor sign-in asks for a code from an authenticator app on your phone in addition to your password. It is optional and applies per user. Any app for time-based codes works (6 digits, 30 seconds).

To turn it on:

1. Open the Pithyx menu, click your name and choose **Two-factor authentication**.
2. Click **Set up**.
3. Scan the QR code with your authenticator app, or enter the key shown below it by hand.
4. Type the code the app shows into **Code** and click **Turn on**.
5. Store the recovery codes. Pithyx shows 10 codes once. Each works one time instead of a code from the app. Click **Copy codes** and keep them somewhere safe.

When you sign in, Pithyx asks for the code after the password. Click **Use a recovery code instead** if you do not have your phone. A code can be used only once. You have five minutes to finish the second step.

To turn it off, open the same dialog, type **Your password** and click **Turn off**. Setting it up again replaces the old secret and creates new recovery codes.

:::note
Two-factor sign-in protects the desktop only. SMB, SFTP, WebDAV and FTP use your password alone.
:::

Keep the recovery codes safe. If you lose both your phone and the codes, ask an administrator for help.

## Sessions and devices

Pithyx does not show a list of your sessions and cannot sign out your other devices from the desktop. What it does:

- You stay signed in for up to 7 days without using the desktop and at most 30 days in total. An open desktop keeps the session alive until the 30-day limit.
- **Sign out** ends the session of this browser only.
- When an administrator sets a new password for you or deactivates your account, all your sessions end.
- Each browser is recognized as a device by a cookie, which the box uses to remember your windows. To copy the arrangement of another device, open your account menu and choose **Get desktop from**. See [Layout per device](/docs/user/desktop/#layout-per-device).
- An expired session shows "The session has expired. Please sign in again."

## Language, theme and accent

Open the Pithyx menu, click your name and choose:

- **Language**: **Automatic** (follows your browser, English or German) or a language by its own name. The choice belongs to this device. The sign-in page has its own language selector.
- **Appearance**: **Like the system**, **Light** or **Dark**. This also belongs to this device.
- **Accent color**: **Petrol**, **Ocean**, **Indigo**, **Plum**, **Moss** or **Graphite**. It is saved for your account and is the same on all your devices.
- **Wallpaper**: **Pithyx boxes** or **Plain**, also saved for your account.

Dates, times and number formats follow the region and time zone an administrator set in Settings.

Your display name is set by an administrator; you cannot edit it yourself.
