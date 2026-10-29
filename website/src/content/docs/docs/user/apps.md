---
title: 'Using apps'
description: 'Opening apps, windows of the same app, and what an app may see of your data.'
status: ready
audience: user
sidebar:
  order: 4
---

Apps open in windows on the desktop, like Files or Settings. Some apps come with Pithyx; administrators can install more from the Store. Each app runs in a sandbox and only sees what an administrator allowed it to see, limited by what your own account may do.

## Opening an app

Open the Pithyx menu and click an app, or type part of its name into the search field. You can also open an app from a shortcut on the desktop, or put one there with **Add to desktop**. See [The desktop](/docs/user/desktop/).

The apps that come with Pithyx are **Files**, **Viewer**, **Storage**, **Settings**, **Resource Monitor**, **Log Center**, **About Pithyx** and **Store**. You only see the apps you may use:

- **Files** and **Viewer** are available to new users by default.
- **Storage**, **Settings**, **Resource Monitor**, **Log Center** and **Store** are for administrators. Settings opens on **Overview**: the box with its version, hardware, how long it runs and its updates, then **Needs attention** with one row per problem the box knows about (a pool alert, a Pithyx or app update, a device that does not trust the box yet) and the button that leads to the fix, then **All settings** as tiles. The navigation column on the left lists every page under **System** and **Access** and has a search that also finds a page by what it holds, such as `ntp`.
- **About Pithyx** is open to everybody.
- Apps an administrator installed show up for the users the administrator chose. Ask an administrator if you miss an app.

If an app's data is on a pool that is offline or locked, the app cannot be opened until the pool is back.

## Single and multiple windows

An app decides how many windows it may have:

- **One window:** opening the app again brings its window forward instead of opening a second one. This holds per device, so another browser has its own window. Storage, Settings, Resource Monitor, Log Center, About Pithyx and the Store work this way.
- **Several windows:** every click opens a new window. Files and Viewer work this way.

Windows come back after you reload the page or sign in again. See [Layout per device](/docs/user/desktop/#layout-per-device). In total you can have 50 windows open.

## What an app may access

An app installed from the Store runs inside a frame of its own, without access to the rest of the desktop, to your cookies or to your password. It talks to Pithyx only through a controlled channel. An administrator sees what an app asks for before installing it. The possible permissions are:

| Permission                   | What it lets the app do                                                    |
| ---------------------------- | -------------------------------------------------------------------------- |
| **Read files**               | Read files in the shared folders you may use                               |
| **Change files**             | Create, change and delete files in the shared folders you may write to     |
| **See shared folders**       | See which shared folders you may use                                       |
| **Notifications**            | Show notifications to that user                                            |
| **See all users and groups** | See the names of every user and group on the box                           |
| **Internet**                 | Connect to servers on the internet. Without it, the app stays on this box. |

Good to know:

- An app acts with your rights. It never sees a shared folder you cannot use, and it cannot write where you only may read. With **Read files** and **Change files** it can use the whole file API of the box in your shared folders, not only files you picked.
- An administrator can switch a permission off for an app. The change reaches open windows within a few minutes.
- If an administrator removes your right to use an app, its windows close.
- Apps with a backend run their own service in containers on the box. Depending on the app, the backend runs all the time, or it starts when someone opens the app and stops again after the last window closes (10 minutes later unless the app sets another time).
- Apps installed from a file are not checked by the project. Administrators should only install apps from developers they trust.

For administrators: [Apps and the Store](/docs/admin/apps-and-store/).

## When an app does not start

- **The window closes at once or the app is gone.** An administrator may have disabled or removed it, or taken away your right to use it. Ask an administrator.
- **The app is slow on its first start.** A backend that was stopped needs a moment to start. A request to it waits up to 30 seconds. If it still fails, you see "The app's backend is not running or does not answer. Try again in a moment."
- **A message mentions the container service** or that the backend could not start. An administrator can read the app's log in the Log Center.
- **A message says the folder for the app's storage is not ready.** The pool with the app's data is missing, offline or locked. Nobody can open the app until it is back.
- **The app's access expired** ("Close the app and open it again."). Close the window and open the app again.
- **The connection between the app and the desktop is closed.** Open the app again.
- **The app does not answer.** After five seconds a dialog asks whether to **Wait** or **Close anyway**. Closing loses unsaved changes.
- **The app needs a newer version of Pithyx.** Ask an administrator.
