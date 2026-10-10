---
question: 'Where do apps keep their data?'
category: apps
tags: [app data, app pool, dataset, docker data, uninstall, system disk]
order: 40
status: ready
---

On the app pool an administrator picks at the first app install, in a dataset per app; Docker's data lives on the same pool, never on the system disk. The pool cannot change while an app is installed. Uninstalling asks whether to keep the data, so a later install finds it again.

Details: [The app pool](/docs/admin/apps-and-store/#the-app-pool).
