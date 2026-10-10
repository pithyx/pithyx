---
question: 'Why does my app not start?'
category: apps
tags: [app not starting, app error, backend not running, app slow, pool not available, app missing]
order: 20
status: ready
---

The most common causes: the app's pool is offline or locked ("Pool not available"), its backend is still starting (a request waits up to 30 seconds), or an administrator disabled the app or took away your right to use it. An administrator finds the backend's messages in the Log Center under the source `app/<id>`. If the app's access expired, close the window and open the app again.

Details: [When an app does not start](/docs/user/apps/#when-an-app-does-not-start).
